// PlayCanvas-compatible Gaussian-splat LOD streaming for Unity.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gsplat.Formats;
using UnityEngine;
using UnityEngine.Networking;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Gsplat
{
    /// <summary>
    /// Streams a PlayCanvas <c>lod-meta.json</c> using one renderer per referenced resource.
    /// Exact leaf intervals are grouped per file and an old resident LOD remains visible until
    /// its replacement has decoded successfully.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class GsplatLodStreamer : MonoBehaviour
    {
        // A single unique SOG decode at a time keeps many tiled streamers from saturating
        // the worker pool. Identical URIs join the same in-flight cache entry below.
        static readonly SemaphoreSlim s_sharedAssetLoadGate = new(1, 1);

        [Header("Manifest")]
        public GsplatLodManifestAsset ManifestAsset;
        [Tooltip("Optional runtime lod-meta.json URI. Relative values resolve under StreamingAssets.")]
        public string ManifestUri;
        public SourceCoordinates SourceCoordinates = SourceCoordinates.Unspecified;
        public CompressionMode Compression = CompressionMode.Spark;

        [Header("LOD selection")]
        public Camera LodCamera;
        [Min(0.1f)] public float LodBaseDistance = 5f;
        [Min(1.2f)] public float LodMultiplier = 3f;
        [Min(0)] public int LodRangeMin;
        [Min(0)] public int LodRangeMax = 99;
        [Min(1f)] public float LodBehindPenalty = 1f;
        [Min(0)] public int LodUnderfillLimit;
        [Range(1, 120)] public int LodUpdateIntervalFrames = 10;
        // A tile grid assigns a phase so its streamers do not evaluate on the same frame.
        [HideInInspector] public int LodUpdatePhaseFrames = -1;

        [Header("Streaming")]
        [Range(1, 16)] public int MaxConcurrentLoads = 2;
        [Range(0, 8)] public int MaxRetries = 2;
        [Min(0)] public int RetryDelayMilliseconds = 250;
        [Min(0f)] public float CacheCooldownSeconds = 2f;
        [Min(0f)] public float FailureCooldownSeconds = 5f;
        public bool AsyncGpuUpload = true;

        [Header("Visibility and budgets")]
        public bool FrustumCulling = true;
        [Min(0f)] public float FrustumPadding = 0.5f;
        [Range(0f, 0.49f)] public float LodHysteresis = 0.1f;
        [Min(0)] public int MaxActiveSplats = 1_500_000;
        [Min(0f)] public float MaxEstimatedResidentGpuMB = 1536f;

        [Header("Rendering")]
        [Range(0, 4)] public int SHDegree = 3;
        [Min(0f)] public float Brightness = 1f;
        [Range(0f, 1f)] public float SplatDownscaleFactor;
        public bool GammaToLinear;
        public uint RenderOrder;

        public bool IsInitialized => m_manifest != null;
        public int VisibleLeafCount { get; private set; }
        public int VisibleMinimumActiveSplatCount { get; private set; }
        public int VisibleOptimalActiveSplatCount { get; private set; }
        public int PredictedVisibleLeafCount { get; private set; }
        public long PredictivePrefetchDemandSplats { get; private set; }
        public long PredictivePrefetchRequestedSplats { get; private set; }
        public int ActiveSplatCount { get; private set; }
        public int LodEvaluationCount { get; private set; }
        public int ActiveRangeUpdateCount { get; private set; }
        public float LastLodEvaluationCpuMilliseconds { get; private set; }
        public long LastLodEvaluationAllocatedBytes { get; private set; }
        public float LastActiveRangeUpdateCpuMilliseconds { get; private set; }
        public long LastActiveRangeUpdateAllocatedBytes { get; private set; }
        public float LastGpuUploadPollCpuMilliseconds { get; private set; }
        public long LastGpuUploadPollAllocatedBytes { get; private set; }
        public float LastLoadQueuePumpCpuMilliseconds { get; private set; }
        public long LastLoadQueuePumpAllocatedBytes { get; private set; }
        public float LastCooldownTickCpuMilliseconds { get; private set; }
        public int ResidentSplatCount => GetResidentSplatCount();
        public long EstimatedResidentGpuBytes => GetEstimatedResidentGpuBytes();
        public int LoadedFileCount
        {
            get
            {
                int count = 0;
                if (m_slots != null)
                    foreach (FileSlot slot in m_uniqueSlots)
                        if (slot.Status == LoadStatus.Loaded) ++count;
                return count;
            }
        }
        public int ActiveLeafCount
        {
            get
            {
                int count = 0;
                if (m_currentLods != null)
                    foreach (int lod in m_currentLods)
                        if (lod >= 0) ++count;
                return count;
            }
        }
        public GsplatLodManifest Manifest => m_manifest;

        public event Action<int, string, Exception> FileLoadFailed;

        enum LoadStatus { Unloaded, Queued, Loading, Loaded, Failed }

        sealed class FileSlot
        {
            public readonly int Index;
            public readonly string Uri;
            public readonly List<int> ManifestIndices = new();
            public GameObject GameObject;
            public GsplatRenderer Renderer;
            public LoadStatus Status;
            public GsplatAsset Asset;
            public bool GpuReady;
            public bool Wanted;
            public int WantedReferences;
            public int Priority = int.MaxValue;
            public float LastWantedTime;
            public float FailureUntil;
            public CancellationTokenSource LoadCancellation;
            public GsplatActiveRange[] AppliedRanges = Array.Empty<GsplatActiveRange>();

            public FileSlot(int index, string uri)
            {
                Index = index;
                Uri = uri;
                ManifestIndices.Add(index);
            }
        }

        readonly struct LodCandidate : IComparable<LodCandidate>
        {
            public readonly float Distance;
            public readonly int LeafIndex;

            public LodCandidate(float distance, int leafIndex)
            {
                Distance = distance;
                LeafIndex = leafIndex;
            }

            // Descending distance: the least important visible leaves lose detail first.
            public int CompareTo(LodCandidate other) => other.Distance.CompareTo(Distance);
        }

        GsplatLodManifest m_manifest;
        FileSlot[] m_slots;
        readonly List<FileSlot> m_uniqueSlots = new();
        FileSlot m_environment;
        int[] m_currentLods;
        int[] m_optimalLods;
        int[] m_desiredLods;
        readonly List<FileSlot> m_loadQueue = new();
        readonly List<FileSlot> m_uploadingSlots = new();
        readonly Dictionary<int, List<GsplatActiveRange>> m_rangeScratch = new();
        readonly HashSet<int> m_activeRangeFiles = new();
        readonly HashSet<int> m_previousActiveRangeFiles = new();
        readonly HashSet<int> m_filesToApply = new();
        readonly List<LodCandidate> m_budgetCandidates = new();
        readonly List<LodCandidate> m_predictiveCandidates = new();
        readonly Plane[] m_frustumPlanes = new Plane[6];
        readonly Plane[] m_predictiveFrustumPlanes = new Plane[6];
        CancellationTokenSource m_lifetime;
        int m_activeLoads;
        int m_sessionGeneration;
        bool m_forceEvaluate;
        bool m_rangesDirty;
        bool m_shuttingDown;
        bool m_hasPredictiveCameraPose;
        Vector3 m_predictiveCameraPosition;
        Quaternion m_predictiveCameraRotation = Quaternion.identity;
        float m_predictiveFrustumPadding;
        int m_predictiveLodRefinementLevels;
        int m_predictivePrefetchSplatBudget;
        int[] m_predictiveLods;
        int m_lastEvaluationFrame = int.MinValue;
        string m_baseUri;
        SourceCoordinates m_sessionCoordinates = global::Gsplat.SourceCoordinates.Unspecified;

        async void OnEnable()
        {
            if (!Application.isPlaying || m_manifest != null) return;
            try
            {
                m_shuttingDown = false;
                m_lifetime = new CancellationTokenSource();
                if (ManifestAsset)
                {
                    SourceCoordinates coordinates = SourceCoordinates == global::Gsplat.SourceCoordinates.Unspecified
                        ? ManifestAsset.SourceCoordinates : SourceCoordinates;
                    GsplatLodManifest manifest = coordinates == ManifestAsset.SourceCoordinates
                        ? ManifestAsset.Manifest
                        : GsplatLodManifest.Parse(ManifestAsset.Json, coordinates);
                    Initialize(manifest, ManifestAsset.BaseUri, coordinates);
                }
                else if (!string.IsNullOrWhiteSpace(ManifestUri))
                {
                    string uri = GsplatLodUri.ResolveManifestUri(ManifestUri);
                    byte[] bytes = await LoadBytesAsync(uri, m_lifetime.Token);
                    string json = Encoding.UTF8.GetString(bytes);
                    SourceCoordinates coordinates = SourceCoordinates == global::Gsplat.SourceCoordinates.Unspecified
                        ? global::Gsplat.SourceCoordinates.RUB : SourceCoordinates;
                    Initialize(GsplatLodManifest.Parse(json, coordinates), GsplatLodUri.DirectoryOf(uri), coordinates);
                }
                else
                {
                    Debug.LogError("[Gsplat LOD] Assign a manifest asset or ManifestUri.", this);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        /// <summary>Initializes from an already parsed manifest. Must be called on Unity's main thread.</summary>
        public void Initialize(GsplatLodManifest manifest, string baseUri,
            SourceCoordinates sourceCoordinates = SourceCoordinates.Unspecified)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            SourceCoordinates resolvedCoordinates = sourceCoordinates == global::Gsplat.SourceCoordinates.Unspecified
                ? manifest.SourceCoordinates : sourceCoordinates;
            if (resolvedCoordinates != manifest.SourceCoordinates)
                throw new ArgumentException(
                    $"The manifest bounds were parsed as {manifest.SourceCoordinates}, but resources were requested as {resolvedCoordinates}. Reparse the manifest with the same coordinate frame.",
                    nameof(sourceCoordinates));
            Shutdown();
            m_shuttingDown = false;
            m_lifetime = new CancellationTokenSource();
            m_manifest = manifest;
            SourceCoordinates = resolvedCoordinates;
            m_sessionCoordinates = resolvedCoordinates;
            m_baseUri = baseUri ?? string.Empty;
            m_slots = new FileSlot[manifest.Filenames.Length];
            m_uniqueSlots.Clear();
            var slotsByUri = new Dictionary<string, FileSlot>(StringComparer.Ordinal);
            for (int i = 0; i < m_slots.Length; ++i)
            {
                string uri = GsplatLodUri.Resolve(m_baseUri, manifest.Filenames[i]);
                if (!slotsByUri.TryGetValue(uri, out FileSlot slot))
                {
                    slot = new FileSlot(i, uri);
                    slotsByUri.Add(uri, slot);
                    m_uniqueSlots.Add(slot);
                }
                else
                {
                    slot.ManifestIndices.Add(i);
                }
                m_slots[i] = slot;
            }

            if (!string.IsNullOrWhiteSpace(manifest.Environment))
                m_environment = new FileSlot(-1, GsplatLodUri.Resolve(m_baseUri, manifest.Environment));

            m_currentLods = new int[manifest.Leaves.Length];
            m_optimalLods = new int[manifest.Leaves.Length];
            m_desiredLods = new int[manifest.Leaves.Length];
            m_predictiveLods = new int[manifest.Leaves.Length];
            Array.Fill(m_currentLods, -1);
            Array.Fill(m_optimalLods, -1);
            Array.Fill(m_desiredLods, -1);
            Array.Fill(m_predictiveLods, -1);
            VisibleLeafCount = 0;
            VisibleMinimumActiveSplatCount = 0;
            VisibleOptimalActiveSplatCount = 0;
            PredictedVisibleLeafCount = 0;
            PredictivePrefetchDemandSplats = 0;
            PredictivePrefetchRequestedSplats = 0;
            ActiveSplatCount = 0;
            m_forceEvaluate = true;
            m_rangesDirty = true;
            m_lastEvaluationFrame = int.MinValue;

            // Environment resources are independent of octree selection and stay referenced.
            if (m_environment != null)
            {
                MarkWanted(m_environment, -10_000);
                QueueLoad(m_environment);
            }
        }

        void Update()
        {
            if (m_manifest == null || m_shuttingDown) return;
            long cpuStart = Stopwatch.GetTimestamp();
            long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            PollGpuUploads();
            LastGpuUploadPollCpuMilliseconds = (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency);
            LastGpuUploadPollAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            Camera camera = LodCamera ? LodCamera : Camera.main;
            int interval = Mathf.Max(1, LodUpdateIntervalFrames);
            int phase = LodUpdatePhaseFrames < 0 ? -1 : Mathf.Clamp(LodUpdatePhaseFrames, 0, interval - 1);
            bool phaseDue = phase < 0 || Time.frameCount % interval == phase;
            if (phaseDue && (m_forceEvaluate || Time.frameCount - m_lastEvaluationFrame >= interval))
            {
                if (camera) Evaluate(camera);
                m_lastEvaluationFrame = Time.frameCount;
                m_forceEvaluate = false;
            }
            cpuStart = Stopwatch.GetTimestamp();
            allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            PumpLoadQueue();
            LastLoadQueuePumpCpuMilliseconds = (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency);
            LastLoadQueuePumpAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
            cpuStart = Stopwatch.GetTimestamp();
            TickCooldowns();
            LastCooldownTickCpuMilliseconds = (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency);
        }

        /// <summary>Forces LOD evaluation on the next Update, useful after changing parameters.</summary>
        public void ForceLodUpdate() => m_forceEvaluate = true;

        /// <summary>Supplies a camera pose extrapolated by the tile grid for low-priority prefetch.</summary>
        internal void SetPredictiveCameraPose(bool enabled, Vector3 position, Quaternion rotation,
            float frustumPadding, int lodRefinementLevels, int prefetchSplatBudget)
        {
            m_hasPredictiveCameraPose = enabled;
            m_predictiveCameraPosition = position;
            m_predictiveCameraRotation = rotation;
            m_predictiveFrustumPadding = Mathf.Max(0f, frustumPadding);
            m_predictiveLodRefinementLevels = Mathf.Max(0, lodRefinementLevels);
            m_predictivePrefetchSplatBudget = Mathf.Max(0, prefetchSplatBudget);
        }

        void Evaluate(Camera camera)
        {
            long cpuStart = Stopwatch.GetTimestamp();
            long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            ++LodEvaluationCount;
            int maxLod = m_manifest.LodLevels - 1;
            int rangeMin = Mathf.Clamp(LodRangeMin, 0, maxLod);
            int rangeMax = Mathf.Clamp(LodRangeMax, rangeMin, maxLod);
            Vector3 localCameraPosition = transform.InverseTransformPoint(camera.transform.position);
            Vector3 localCameraForward = transform.InverseTransformDirection(camera.transform.forward).normalized;

            if (FrustumCulling)
                GeometryUtility.CalculateFrustumPlanes(camera, m_frustumPlanes);

            m_budgetCandidates.Clear();
            VisibleLeafCount = 0;
            VisibleMinimumActiveSplatCount = 0;
            VisibleOptimalActiveSplatCount = 0;
            PredictedVisibleLeafCount = 0;
            PredictivePrefetchDemandSplats = 0;
            PredictivePrefetchRequestedSplats = 0;
            m_predictiveCandidates.Clear();
            Array.Fill(m_predictiveLods, -1);
            for (int leafIndex = 0; leafIndex < m_manifest.Leaves.Length; ++leafIndex)
            {
                GsplatLodLeaf leaf = m_manifest.Leaves[leafIndex];
                if (FrustumCulling && !IsLeafVisible(leaf))
                {
                    m_optimalLods[leafIndex] = -1;
                    m_desiredLods[leafIndex] = -1;
                    continue;
                }

                ++VisibleLeafCount;
                int coarsestAvailable = FindAvailableCoarserLod(leaf, rangeMax, rangeMin);
                if (coarsestAvailable >= 0)
                    VisibleMinimumActiveSplatCount += leaf.Lods[coarsestAvailable].Count;

                int optimal = GsplatLodSelector.SelectLod(leaf.Bounds, localCameraPosition,
                    localCameraForward, camera.fieldOfView, camera.aspect, LodBaseDistance,
                    LodMultiplier, maxLod, rangeMin, rangeMax, LodBehindPenalty);
                float effectiveDistance = GsplatLodSelector.GetEffectiveDistance(leaf.Bounds,
                    localCameraPosition, localCameraForward, camera.fieldOfView, camera.aspect,
                    LodBehindPenalty);
                int previous = m_currentLods[leafIndex];
                if (previous >= 0 && IsLodResident(leaf, previous))
                    optimal = GsplatLodSelector.ApplyHysteresis(previous, optimal, effectiveDistance,
                        LodBaseDistance, LodMultiplier, LodHysteresis);

                int desired = SelectDesiredLod(leaf, optimal, maxLod);
                if (HasLod(leaf, desired))
                    VisibleOptimalActiveSplatCount += leaf.Lods[desired].Count;
                m_optimalLods[leafIndex] = optimal;
                m_desiredLods[leafIndex] = desired;
                float distance = Vector3.Distance(leaf.Bounds.ClosestPoint(localCameraPosition),
                    localCameraPosition);
                m_budgetCandidates.Add(new LodCandidate(distance, leafIndex));
            }

            ApplyActiveSplatBudget(maxLod);

            foreach (FileSlot slot in m_uniqueSlots)
            {
                slot.Wanted = false;
                slot.WantedReferences = 0;
                slot.Priority = int.MaxValue;
            }
            if (m_environment != null)
            {
                m_environment.Wanted = false;
                m_environment.WantedReferences = 0;
                m_environment.Priority = int.MaxValue;
                MarkWanted(m_environment, -10_000);
            }

            for (int leafIndex = 0; leafIndex < m_manifest.Leaves.Length; ++leafIndex)
            {
                GsplatLodLeaf leaf = m_manifest.Leaves[leafIndex];
                int previousCurrent = m_currentLods[leafIndex];
                int desired = m_desiredLods[leafIndex];
                if (desired < 0)
                {
                    m_currentLods[leafIndex] = -1;
                    if (previousCurrent >= 0) m_rangesDirty = true;
                    continue;
                }

                int current = previousCurrent;
                if (!IsLodResident(leaf, current)) current = -1;

                if (IsLodResident(leaf, desired))
                {
                    current = desired;
                }
                else if (current < 0)
                {
                    current = FindResidentCoarserLod(leaf, desired, rangeMax);
                }

                m_currentLods[leafIndex] = current;
                if (current != previousCurrent) m_rangesDirty = true;
            }

            EnforceResidentActiveSplatBudget(rangeMax);

            for (int leafIndex = 0; leafIndex < m_manifest.Leaves.Length; ++leafIndex)
            {
                int desired = m_desiredLods[leafIndex];
                if (desired < 0) continue;

                GsplatLodLeaf leaf = m_manifest.Leaves[leafIndex];
                int current = m_currentLods[leafIndex];
                if (current >= 0)
                    MarkLodWanted(leaf, current, -1_000 + current * 100 + leafIndex);

                MarkLodWanted(leaf, desired, desired * 100 + leafIndex);
                if (current < 0)
                {
                    // Establish the cheapest coverage first; refine only after it is resident.
                    int coarse = FindAvailableCoarserLod(leaf, rangeMax, Mathf.Max(desired, rangeMin));
                    MarkLodWanted(leaf, coarse, -5_000 + coarse * 100 + leafIndex);
                }

                if (desired == m_optimalLods[leafIndex])
                    PrefetchNextLod(leaf, desired, m_optimalLods[leafIndex], leafIndex);
            }

            EvaluatePredictivePrefetch(camera, maxLod, rangeMin, rangeMax);

            ActiveSplatCount = (int)Math.Min(int.MaxValue, GetCurrentActiveSplatCount());

            if (m_rangesDirty) ApplyActiveRanges();
            else SyncActiveRendererSettings();
            ReconcileRequests();
            LastLodEvaluationCpuMilliseconds = (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency);
            LastLodEvaluationAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
        }

        bool IsLeafVisible(GsplatLodLeaf leaf)
        {
            return IsLeafVisible(leaf, m_frustumPlanes, FrustumPadding);
        }

        bool IsLeafVisible(GsplatLodLeaf leaf, Plane[] planes, float padding)
        {
            Bounds worldBounds = TransformBounds(leaf.Bounds, transform.localToWorldMatrix);
            padding = Mathf.Max(0f, padding);
            if (padding > 0f) worldBounds.Expand(padding * 2f);
            return GeometryUtility.TestPlanesAABB(planes, worldBounds);
        }

        void EvaluatePredictivePrefetch(Camera camera, int maxLod, int rangeMin, int rangeMax)
        {
            if (!m_hasPredictiveCameraPose || m_predictivePrefetchSplatBudget <= 0 ||
                m_manifest == null || m_predictiveLods == null)
                return;

            Matrix4x4 currentCameraTransform =
                Matrix4x4.TRS(camera.transform.position, camera.transform.rotation, Vector3.one);
            Matrix4x4 cameraViewConvention = camera.worldToCameraMatrix * currentCameraTransform;
            Matrix4x4 predictedCameraTransform =
                Matrix4x4.TRS(m_predictiveCameraPosition, m_predictiveCameraRotation, Vector3.one);
            Matrix4x4 predictedWorldToCamera = cameraViewConvention * predictedCameraTransform.inverse;
            GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * predictedWorldToCamera,
                m_predictiveFrustumPlanes);

            Vector3 localCameraPosition = transform.InverseTransformPoint(m_predictiveCameraPosition);
            Vector3 localCameraForward = transform.InverseTransformDirection(
                m_predictiveCameraRotation * Vector3.forward).normalized;
            long fullDemand = 0;

            for (int leafIndex = 0; leafIndex < m_manifest.Leaves.Length; ++leafIndex)
            {
                GsplatLodLeaf leaf = m_manifest.Leaves[leafIndex];
                if (!IsLeafVisible(leaf, m_predictiveFrustumPlanes,
                        FrustumPadding + m_predictiveFrustumPadding))
                    continue;

                ++PredictedVisibleLeafCount;
                int predictedLod = GsplatLodSelector.SelectLod(leaf.Bounds, localCameraPosition,
                    localCameraForward, camera.fieldOfView, camera.aspect, LodBaseDistance,
                    LodMultiplier, maxLod, rangeMin, rangeMax, LodBehindPenalty);
                m_predictiveLods[leafIndex] = predictedLod;
                // Current visible leaves already request their live target and one-step
                // refinement. Predictive traffic is reserved for leaves entering the future view.
                if (predictedLod < 0 || m_desiredLods[leafIndex] >= 0)
                    continue;

                bool needsRequest = false;
                int requestedLod = predictedLod;
                for (int refinement = 0; refinement <= m_predictiveLodRefinementLevels; ++refinement)
                {
                    if (refinement > 0)
                    {
                        requestedLod = FindNextFinerLod(leaf, requestedLod - 1, rangeMin);
                        if (requestedLod < 0) break;
                    }

                    if (m_desiredLods[leafIndex] == requestedLod ||
                        m_currentLods[leafIndex] == requestedLod || IsLodResident(leaf, requestedLod))
                        continue;

                    fullDemand += leaf.Lods[requestedLod].Count;
                    needsRequest = true;
                }

                if (needsRequest)
                {
                    float distance = Vector3.Distance(leaf.Bounds.ClosestPoint(localCameraPosition),
                        localCameraPosition);
                    m_predictiveCandidates.Add(new LodCandidate(distance, leafIndex));
                }
            }

            PredictivePrefetchDemandSplats = fullDemand;
            if (fullDemand <= 0) return;

            // LodCandidate sorts farthest-first for active-budget reduction. Walk it backwards
            // here so the limited prediction budget warms the nearest future leaves first.
            m_predictiveCandidates.Sort();
            int remainingBudget = m_predictivePrefetchSplatBudget;
            for (int candidateIndex = m_predictiveCandidates.Count - 1;
                 candidateIndex >= 0 && remainingBudget > 0; --candidateIndex)
            {
                int leafIndex = m_predictiveCandidates[candidateIndex].LeafIndex;
                GsplatLodLeaf leaf = m_manifest.Leaves[leafIndex];
                int predictedLod = m_predictiveLods[leafIndex];
                int requestedLod = predictedLod;
                for (int refinement = 0; refinement <= m_predictiveLodRefinementLevels; ++refinement)
                {
                    if (refinement > 0)
                    {
                        requestedLod = FindNextFinerLod(leaf, requestedLod - 1, rangeMin);
                        if (requestedLod < 0) break;
                    }

                    if (m_desiredLods[leafIndex] == requestedLod ||
                        m_currentLods[leafIndex] == requestedLod || IsLodResident(leaf, requestedLod))
                        continue;

                    int splatCount = leaf.Lods[requestedLod].Count;
                    if (splatCount > remainingBudget) continue;

                    int priority = 5_000 + refinement * 1_000 + requestedLod * 100 + leafIndex;
                    MarkLodWanted(leaf, requestedLod, priority);
                    remainingBudget -= splatCount;
                    PredictivePrefetchRequestedSplats += splatCount;
                }
            }
        }

        static Bounds TransformBounds(Bounds localBounds, Matrix4x4 localToWorld)
        {
            Vector3 center = localBounds.center;
            Vector3 extents = localBounds.extents;
            Vector3 first = localToWorld.MultiplyPoint3x4(center + new Vector3(-extents.x, -extents.y, -extents.z));
            var worldBounds = new Bounds(first, Vector3.zero);
            for (int corner = 1; corner < 8; ++corner)
            {
                Vector3 point = center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                worldBounds.Encapsulate(localToWorld.MultiplyPoint3x4(point));
            }
            return worldBounds;
        }

        void ApplyActiveSplatBudget(int maxLod)
        {
            long total = 0;
            for (int i = 0; i < m_desiredLods.Length; ++i)
            {
                int lod = m_desiredLods[i];
                if (HasLod(m_manifest.Leaves[i], lod))
                    total += m_manifest.Leaves[i].Lods[lod].Count;
            }

            if (MaxActiveSplats > 0 && total > MaxActiveSplats)
            {
                m_budgetCandidates.Sort();
                foreach (LodCandidate candidate in m_budgetCandidates)
                {
                    GsplatLodLeaf leaf = m_manifest.Leaves[candidate.LeafIndex];
                    int lod = m_desiredLods[candidate.LeafIndex];
                    while (total > MaxActiveSplats)
                    {
                        int next = FindNextCoarserLod(leaf, lod, maxLod);
                        if (next < 0) break;
                        total += leaf.Lods[next].Count - leaf.Lods[lod].Count;
                        lod = next;
                        m_desiredLods[candidate.LeafIndex] = lod;
                    }
                    if (total <= MaxActiveSplats) break;
                }
            }

            ActiveSplatCount = (int)Math.Min(int.MaxValue, total);
        }

        void EnforceResidentActiveSplatBudget(int maxLod)
        {
            if (MaxActiveSplats <= 0) return;
            long total = GetCurrentActiveSplatCount();
            if (total <= MaxActiveSplats) return;

            m_budgetCandidates.Sort();
            foreach (LodCandidate candidate in m_budgetCandidates)
            {
                int leafIndex = candidate.LeafIndex;
                GsplatLodLeaf leaf = m_manifest.Leaves[leafIndex];
                int current = m_currentLods[leafIndex];
                if (!HasLod(leaf, current)) continue;

                int coarser = FindResidentCoarserLod(leaf, current + 1, maxLod);
                if (coarser >= 0)
                {
                    total += leaf.Lods[coarser].Count - leaf.Lods[current].Count;
                    m_currentLods[leafIndex] = coarser;
                }
                else
                {
                    total -= leaf.Lods[current].Count;
                    m_currentLods[leafIndex] = -1;
                }
                m_rangesDirty = true;
                if (total <= MaxActiveSplats) break;
            }

            ActiveSplatCount = (int)Math.Max(0, Math.Min(int.MaxValue, total));
        }

        static int FindNextCoarserLod(GsplatLodLeaf leaf, int current, int max)
        {
            for (int lod = current + 1; lod <= max; ++lod)
                if (HasLod(leaf, lod)) return lod;
            return -1;
        }

        static int FindNextFinerLod(GsplatLodLeaf leaf, int current, int min)
        {
            for (int lod = current; lod >= min; --lod)
                if (HasLod(leaf, lod)) return lod;
            return -1;
        }

        long GetCurrentActiveSplatCount()
        {
            long total = 0;
            for (int i = 0; i < m_currentLods.Length; ++i)
            {
                int lod = m_currentLods[i];
                GsplatLodLeaf leaf = m_manifest.Leaves[i];
                if (HasLod(leaf, lod)) total += leaf.Lods[lod].Count;
            }
            return total;
        }

        int SelectDesiredLod(GsplatLodLeaf leaf, int optimal, int maxLod)
        {
            if (LodUnderfillLimit <= 0) return optimal;
            int allowedMax = Mathf.Min(maxLod, optimal + LodUnderfillLimit);
            for (int lod = optimal; lod <= allowedMax; ++lod)
                if (IsLodResident(leaf, lod)) return lod;
            for (int lod = allowedMax; lod >= optimal; --lod)
                if (HasLod(leaf, lod)) return lod;
            return optimal;
        }

        void PrefetchNextLod(GsplatLodLeaf leaf, int desired, int optimal, int leafIndex)
        {
            if (desired < 0 || optimal < 0) return;
            if (desired == optimal)
            {
                MarkLodWanted(leaf, optimal, optimal * 100 + leafIndex);
                return;
            }
            int target = Mathf.Max(optimal, desired - 1);
            for (int lod = target; lod >= optimal; --lod)
            {
                if (!HasLod(leaf, lod)) continue;
                MarkLodWanted(leaf, lod, 10_000 + lod * 100 + leafIndex);
                break;
            }
        }

        void MarkLodWanted(GsplatLodLeaf leaf, int lod, int priority)
        {
            if (!HasLod(leaf, lod)) return;
            MarkWanted(m_slots[leaf.Lods[lod].FileIndex], priority);
        }

        static bool HasLod(GsplatLodLeaf leaf, int lod) =>
            lod >= 0 && lod < leaf.Lods.Length && leaf.Lods[lod].IsPresent;

        bool IsLodResident(GsplatLodLeaf leaf, int lod) =>
            HasLod(leaf, lod) && m_slots[leaf.Lods[lod].FileIndex] is { Status: LoadStatus.Loaded, GpuReady: true };

        int FindResidentCoarserLod(GsplatLodLeaf leaf, int start, int max)
        {
            for (int lod = Mathf.Max(0, start); lod <= max; ++lod)
                if (IsLodResident(leaf, lod)) return lod;
            return -1;
        }

        static int FindAvailableCoarserLod(GsplatLodLeaf leaf, int max, int minimum)
        {
            for (int lod = max; lod >= Mathf.Max(0, minimum); --lod)
                if (HasLod(leaf, lod)) return lod;
            return -1;
        }

        void ApplyActiveRanges()
        {
            long cpuStart = Stopwatch.GetTimestamp();
            long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            ++ActiveRangeUpdateCount;
            foreach (int fileIndex in m_activeRangeFiles)
                m_rangeScratch[fileIndex].Clear();
            m_activeRangeFiles.Clear();

            for (int leafIndex = 0; leafIndex < m_currentLods.Length; ++leafIndex)
            {
                int lod = m_currentLods[leafIndex];
                GsplatLodLeaf leaf = m_manifest.Leaves[leafIndex];
                if (!HasLod(leaf, lod)) continue;
                GsplatLodSlice slice = leaf.Lods[lod];
                int canonicalFileIndex = m_slots[slice.FileIndex].Index;
                if (!m_rangeScratch.TryGetValue(canonicalFileIndex, out List<GsplatActiveRange> ranges))
                {
                    ranges = new List<GsplatActiveRange>();
                    m_rangeScratch.Add(canonicalFileIndex, ranges);
                }
                ranges.Add(new GsplatActiveRange((uint)slice.Offset, (uint)slice.Count));
                m_activeRangeFiles.Add(canonicalFileIndex);
            }

            m_filesToApply.Clear();
            foreach (int fileIndex in m_previousActiveRangeFiles) m_filesToApply.Add(fileIndex);
            foreach (int fileIndex in m_activeRangeFiles) m_filesToApply.Add(fileIndex);
            foreach (int i in m_filesToApply)
            {
                FileSlot slot = m_slots[i];
                if (slot.Status != LoadStatus.Loaded || !slot.Asset || !slot.GpuReady) continue;
                SyncRendererSettings(slot);
                IReadOnlyList<GsplatActiveRange> source = m_activeRangeFiles.Contains(i)
                    ? m_rangeScratch[i] : Array.Empty<GsplatActiveRange>();
                GsplatActiveRange[] normalized = GsplatLodRangeNormalizer.Normalize(source, slot.Asset.SplatCount);
                if (RangesEqual(slot.AppliedRanges, normalized)) continue;

                EnsureRendererBound(slot);
                slot.Renderer.SetActiveRanges(normalized);
                slot.AppliedRanges = normalized;
            }
            m_previousActiveRangeFiles.Clear();
            foreach (int fileIndex in m_activeRangeFiles) m_previousActiveRangeFiles.Add(fileIndex);
            m_rangesDirty = false;

            if (m_environment is { Status: LoadStatus.Loaded, GpuReady: true } && m_environment.Asset)
            {
                SyncRendererSettings(m_environment);
                EnsureRendererBound(m_environment);
                if (m_environment.Renderer.HasActiveRanges)
                    m_environment.Renderer.ClearActiveRanges();
            }
            LastActiveRangeUpdateCpuMilliseconds = (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency);
            LastActiveRangeUpdateAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
        }

        static bool RangesEqual(IReadOnlyList<GsplatActiveRange> a, IReadOnlyList<GsplatActiveRange> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; ++i)
                if (a[i].Offset != b[i].Offset || a[i].Count != b[i].Count) return false;
            return true;
        }

        void SyncActiveRendererSettings()
        {
            foreach (int fileIndex in m_previousActiveRangeFiles)
            {
                FileSlot slot = m_slots[fileIndex];
                if (slot.Status == LoadStatus.Loaded && slot.Renderer)
                    SyncRendererSettings(slot);
            }
            if (m_environment is { Status: LoadStatus.Loaded } && m_environment.Renderer)
                SyncRendererSettings(m_environment);
        }

        void EnsureRendererBound(FileSlot slot)
        {
            EnsureRendererCreated(slot);
            GsplatRenderer renderer = slot.Renderer;
            SyncRendererSettings(slot);
            renderer.RenderBeforeUploadComplete = false;
            renderer.AsyncUpload = AsyncGpuUpload;
            if (renderer.GsplatAsset != slot.Asset)
            {
                renderer.GsplatAsset = slot.Asset;
                renderer.enabled = true;
                renderer.Update();
            }
        }

        void SyncRendererSettings(FileSlot slot)
        {
            EnsureRendererCreated(slot);
            GsplatRenderer renderer = slot.Renderer;
            slot.GameObject.layer = gameObject.layer;
            renderer.SHDegree = SHDegree;
            renderer.Brightness = Brightness;
            renderer.SplatDownscaleFactor = SplatDownscaleFactor;
            renderer.GammaToLinear = GammaToLinear;
            renderer.RenderOrder = RenderOrder;
        }

        void EnsureRendererCreated(FileSlot slot)
        {
            if (slot.Renderer) return;
            slot.GameObject = new GameObject(slot.Index >= 0 ? $"LOD file {slot.Index}" : "LOD environment");
            slot.GameObject.layer = gameObject.layer;
            slot.GameObject.transform.SetParent(transform, false);
            slot.Renderer = slot.GameObject.AddComponent<GsplatRenderer>();
            slot.Renderer.enabled = false;
        }

        void PrepareGpuUpload(FileSlot slot)
        {
            slot.GpuReady = false;
            slot.AppliedRanges = Array.Empty<GsplatActiveRange>();
            EnsureRendererBound(slot);
            // Explicitly hide the newly decoded file. It must not replace the old visible LOD
            // until the asynchronous GPU upload has reached the complete source buffer.
            slot.Renderer.SetActiveRanges(Array.Empty<GsplatActiveRange>());
            if (slot.Renderer.SplatCount == slot.Asset.SplatCount)
            {
                slot.GpuReady = true;
                m_rangesDirty = true;
                m_forceEvaluate = true;
            }
            else if (!m_uploadingSlots.Contains(slot))
            {
                m_uploadingSlots.Add(slot);
            }
        }

        void PollGpuUploads()
        {
            for (int i = m_uploadingSlots.Count - 1; i >= 0; --i)
            {
                FileSlot slot = m_uploadingSlots[i];
                if (slot.Status != LoadStatus.Loaded || !slot.Asset)
                {
                    m_uploadingSlots.RemoveAt(i);
                    continue;
                }
                Task uploadTask = slot.Renderer.GsplatResource?.UploadTask;
                if (uploadTask is { IsFaulted: true })
                {
                    m_uploadingSlots.RemoveAt(i);
                    FailGpuUpload(slot, uploadTask.Exception?.GetBaseException() ??
                        new IOException($"GPU upload failed for '{slot.Uri}'."));
                    continue;
                }
                if (uploadTask is { IsCanceled: true })
                {
                    m_uploadingSlots.RemoveAt(i);
                    FailGpuUpload(slot, new OperationCanceledException(
                        $"GPU upload was canceled for '{slot.Uri}'."));
                    continue;
                }
                if (slot.Renderer.SplatCount != slot.Asset.SplatCount) continue;
                slot.GpuReady = true;
                m_uploadingSlots.RemoveAt(i);
                m_rangesDirty = true;
                m_forceEvaluate = true;
            }
        }

        void FailGpuUpload(FileSlot slot, Exception exception)
        {
            ReleaseSlotPayload(slot);
            slot.Status = LoadStatus.Failed;
            slot.FailureUntil = Time.unscaledTime + Mathf.Max(0f, FailureCooldownSeconds);
            m_rangesDirty = true;
            m_forceEvaluate = true;
            FileLoadFailed?.Invoke(slot.Index, slot.Uri, exception);
            Debug.LogError($"[Gsplat LOD] GPU upload failed for '{slot.Uri}': {exception.Message}", this);
        }

        void MarkWanted(FileSlot slot, int priority)
        {
            if (slot == null) return;
            slot.Wanted = true;
            ++slot.WantedReferences;
            if (priority < slot.Priority) slot.Priority = priority;
            slot.LastWantedTime = Time.unscaledTime;
            QueueLoad(slot);
        }

        void QueueLoad(FileSlot slot)
        {
            if (slot.Status == LoadStatus.Loaded || slot.Status == LoadStatus.Loading ||
                slot.Status == LoadStatus.Queued) return;
            if (slot.Status == LoadStatus.Failed && Time.unscaledTime < slot.FailureUntil) return;
            slot.Status = LoadStatus.Queued;
            if (!m_loadQueue.Contains(slot)) m_loadQueue.Add(slot);
        }

        void ReconcileRequests()
        {
            foreach (FileSlot slot in m_uniqueSlots) ReconcileSlot(slot);
            if (m_environment != null) ReconcileSlot(m_environment);
            PumpLoadQueue();
        }

        static void ReconcileSlot(FileSlot slot)
        {
            if (slot.Wanted) return;
            if (slot.Status == LoadStatus.Queued) slot.Status = LoadStatus.Unloaded;
            if (slot.Status == LoadStatus.Loading) slot.LoadCancellation?.Cancel();
        }

        void PumpLoadQueue()
        {
            if (m_shuttingDown) return;
            m_loadQueue.RemoveAll(slot => slot.Status != LoadStatus.Queued || !slot.Wanted);
            m_loadQueue.Sort((a, b) =>
            {
                int priority = a.Priority.CompareTo(b.Priority);
                return priority != 0 ? priority : a.Index.CompareTo(b.Index);
            });
            int limit = Mathf.Max(1, MaxConcurrentLoads);
            int blocked = 0;
            while (m_activeLoads < limit && m_loadQueue.Count != 0 && blocked < m_loadQueue.Count)
            {
                FileSlot slot = m_loadQueue[0];
                m_loadQueue.RemoveAt(0);
                if (!TryMakeResidentBudgetRoom(slot))
                {
                    m_loadQueue.Add(slot);
                    ++blocked;
                    continue;
                }
                blocked = 0;
                BeginLoad(slot);
            }
        }

        bool TryMakeResidentBudgetRoom(FileSlot requested)
        {
            if (MaxEstimatedResidentGpuMB <= 0f) return true;
            long budget = (long)(MaxEstimatedResidentGpuMB * 1024f * 1024f);
            long requiredBytes = EstimateGpuBytes(GetManifestSplatCount(requested), 4);
            if (requiredBytes > budget) return false;

            while (GetEstimatedResidentGpuBytes() + requiredBytes > budget)
            {
                FileSlot victim = null;
                foreach (FileSlot slot in m_uniqueSlots)
                {
                    if (slot == requested || slot.Wanted || slot.Status != LoadStatus.Loaded || !slot.Asset)
                        continue;
                    if (victim == null || slot.LastWantedTime < victim.LastWantedTime)
                        victim = slot;
                }
                if (victim == null) return false;
                UnloadSlot(victim);
            }
            return true;
        }

        int GetManifestSplatCount(FileSlot slot)
        {
            if (slot == null || slot.Index < 0 || m_manifest == null) return 0;
            int count = 0;
            foreach (int index in slot.ManifestIndices)
                count = Mathf.Max(count, m_manifest.RequiredSplatCounts[index]);
            return count;
        }

        long GetEstimatedResidentGpuBytes()
        {
            long bytes = 0;
            foreach (FileSlot slot in m_uniqueSlots)
            {
                if (slot.Asset)
                    bytes += EstimateGpuBytes(slot.Asset.SplatCount, slot.Asset.SHBands);
                else if (slot.Status == LoadStatus.Loading)
                    bytes += EstimateGpuBytes(GetManifestSplatCount(slot), 4);
            }

            if (m_environment != null)
            {
                if (m_environment.Asset)
                    bytes += EstimateGpuBytes(m_environment.Asset.SplatCount, m_environment.Asset.SHBands);
            }
            return bytes;
        }

        int GetResidentSplatCount()
        {
            long count = 0;
            foreach (FileSlot slot in m_uniqueSlots)
                if (slot.Asset) count += slot.Asset.SplatCount;
            if (m_environment is { Asset: not null }) count += m_environment.Asset.SplatCount;
            return (int)Math.Min(int.MaxValue, count);
        }

        static long EstimateGpuBytes(long splatCount, int shBands)
        {
            if (splatCount <= 0) return 0;
            int attributeBytes = 16;
            if (shBands > 0) attributeBytes += 8;
            if (shBands > 1) attributeBytes += 16;
            if (shBands > 2) attributeBytes += 16;
            if (shBands > 3) attributeBytes += 24;
            // Asset attributes + renderer sort buffers + conservative global-merge copy/work arrays.
            return splatCount * (2L * attributeBytes + 24L);
        }

        async void BeginLoad(FileSlot slot)
        {
            int session = m_sessionGeneration;
            ++m_activeLoads;
            slot.Status = LoadStatus.Loading;
            slot.LoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
            GsplatAsset loadedAsset = null;
            try
            {
                Exception lastError = null;
                int attempts = Mathf.Max(0, MaxRetries) + 1;
                for (int attempt = 0; attempt < attempts; ++attempt)
                {
                    slot.LoadCancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        loadedAsset = await AcquireGsplatAssetAsync(slot.Uri, slot.LoadCancellation.Token);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        lastError = exception;
                        if (attempt + 1 >= attempts) break;
                        if (RetryDelayMilliseconds > 0)
                            await Task.Delay(RetryDelayMilliseconds, slot.LoadCancellation.Token);
                    }
                }

                if (!loadedAsset)
                    throw lastError ?? new IOException($"Could not load '{slot.Uri}'.");
                slot.LoadCancellation.Token.ThrowIfCancellationRequested();
                if (session != m_sessionGeneration) throw new OperationCanceledException();
                if (slot.Index >= 0)
                    foreach (int fileIndex in slot.ManifestIndices)
                        m_manifest.ValidateFileSplatCount(fileIndex, loadedAsset.SplatCount);

                slot.Asset = loadedAsset;
                loadedAsset = null;
                slot.Status = LoadStatus.Loaded;
                slot.FailureUntil = 0f;
                PrepareGpuUpload(slot);
                m_forceEvaluate = true;
            }
            catch (OperationCanceledException)
            {
                if (session == m_sessionGeneration)
                {
                    ReleaseSlotPayload(slot);
                    slot.Status = LoadStatus.Unloaded;
                }
            }
            catch (Exception exception)
            {
                if (session == m_sessionGeneration)
                {
                    // The decoded asset is transferred to the slot before renderer binding.
                    // If binding or a synchronous upload throws, release both the asset and
                    // any partially-created renderer resources before a later retry.
                    ReleaseSlotPayload(slot);
                    slot.Status = LoadStatus.Failed;
                    slot.FailureUntil = Time.unscaledTime + Mathf.Max(0f, FailureCooldownSeconds);
                    FileLoadFailed?.Invoke(slot.Index, slot.Uri, exception);
                    Debug.LogError($"[Gsplat LOD] Failed to load '{slot.Uri}': {exception.Message}", this);
                }
            }
            finally
            {
                DestroyAsset(loadedAsset);
                slot.LoadCancellation?.Dispose();
                slot.LoadCancellation = null;
                if (session == m_sessionGeneration)
                {
                    --m_activeLoads;
                    PumpLoadQueue();
                }
            }
        }

        Task<GsplatAsset> AcquireGsplatAssetAsync(string uri, CancellationToken token)
        {
            CompressionMode compression = Compression;
            SourceCoordinates coordinates = m_sessionCoordinates;
            string key = GsplatLodSharedAssetCache.MakeKey(uri, compression, coordinates);
            return GsplatLodSharedAssetCache.AcquireAsync(key, token, async sharedToken =>
            {
                await s_sharedAssetLoadGate.WaitAsync(sharedToken);
                try
                {
                    return await LoadGsplatAssetAsync(uri, compression, coordinates, sharedToken);
                }
                finally
                {
                    s_sharedAssetLoadGate.Release();
                }
            });
        }

        static async Task<GsplatAsset> LoadGsplatAssetAsync(string uri, CompressionMode compression,
            SourceCoordinates coordinates, CancellationToken token)
        {
            if (GsplatLodUri.IsUnpackedSogMetadata(uri))
            {
                string metadata = Encoding.UTF8.GetString(await LoadBytesAsync(uri, token));
                string directory = GsplatLodUri.DirectoryOf(uri);
                string[] files = CollectSogFiles(metadata);
                var resolved = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    token.ThrowIfCancellationRequested();
                    resolved[NormalizeResourceName(file)] = await LoadBytesAsync(
                        GsplatLodUri.Resolve(directory, file), token);
                }
                token.ThrowIfCancellationRequested();
                return await GsplatRuntimeLoader.LoadUnpackedSogAsync(metadata,
                    filename => resolved.TryGetValue(NormalizeResourceName(filename), out byte[] bytes)
                        ? bytes : throw new FileNotFoundException($"SOG dependency '{filename}' was not loaded."),
                    compression, coordinates, token);
            }

            byte[] data = await LoadBytesAsync(uri, token);
            token.ThrowIfCancellationRequested();
            return GsplatRuntimeLoader.Load(data, GsplatLodUri.ExtensionOf(uri), compression,
                coordinates);
        }

        static string[] CollectSogFiles(string metadata)
        {
            object root = MiniJson.Deserialize(metadata);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count != 0)
            {
                object value = stack.Pop();
                if (value is Dictionary<string, object> dictionary)
                {
                    foreach (var pair in dictionary)
                    {
                        if (pair.Key == "files" && pair.Value is List<object> list)
                        {
                            for (int i = 0; i < list.Count; ++i)
                                files.Add(NormalizeResourceName(JsonValues.String(list[i], $"files[{i}]")));
                        }
                        else if (pair.Value != null) stack.Push(pair.Value);
                    }
                }
                else if (value is List<object> list)
                {
                    foreach (object child in list)
                        if (child != null) stack.Push(child);
                }
            }
            if (files.Count == 0) throw new FormatException("Unpacked SOG metadata references no files.");
            var result = new string[files.Count];
            files.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        static string NormalizeResourceName(string value)
        {
            string normalized = value.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized.Substring(2);
            return normalized;
        }

        static async Task<byte[]> LoadBytesAsync(string uri, CancellationToken token)
        {
            if (GsplatLodUri.RequiresUnityWebRequest(uri))
            {
                using var request = UnityWebRequest.Get(uri);
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (token.IsCancellationRequested)
                    {
                        request.Abort();
                        token.ThrowIfCancellationRequested();
                    }
                    await Task.Yield();
                }
                token.ThrowIfCancellationRequested();
                if (request.result != UnityWebRequest.Result.Success)
                    throw new IOException($"GET {uri} failed: {request.error}");
                return request.downloadHandler.data ?? throw new IOException($"GET {uri} returned no data.");
            }

            string path = GsplatLodUri.LocalPath(uri);
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(path), token);
            token.ThrowIfCancellationRequested();
            return bytes;
        }

        void TickCooldowns()
        {
            float now = Time.unscaledTime;
            foreach (FileSlot slot in m_uniqueSlots)
                if (!slot.Wanted && slot.Status == LoadStatus.Loaded &&
                    now - slot.LastWantedTime >= Mathf.Max(0f, CacheCooldownSeconds))
                    UnloadSlot(slot);
        }

        void UnloadSlot(FileSlot slot)
        {
            ReleaseSlotPayload(slot);
            slot.Status = LoadStatus.Unloaded;
            m_rangesDirty = true;
            if (slot.GameObject)
            {
                if (Application.isPlaying) Destroy(slot.GameObject);
                else DestroyImmediate(slot.GameObject);
                slot.GameObject = null;
                slot.Renderer = null;
            }
            m_forceEvaluate = true;
        }

        void OnDisable() => Shutdown();
        void OnDestroy() => Shutdown();

        void Shutdown()
        {
            if (m_shuttingDown) return;
            m_shuttingDown = true;
            ++m_sessionGeneration;
            m_lifetime?.Cancel();
            m_lifetime?.Dispose();
            m_lifetime = null;
            m_loadQueue.Clear();
            m_uploadingSlots.Clear();
            m_activeLoads = 0;

            foreach (FileSlot slot in m_uniqueSlots) DestroySlot(slot);
            if (m_environment != null) DestroySlot(m_environment);
            m_slots = null;
            m_uniqueSlots.Clear();
            m_environment = null;
            m_manifest = null;
            m_sessionCoordinates = global::Gsplat.SourceCoordinates.Unspecified;
            m_currentLods = null;
            m_optimalLods = null;
            m_desiredLods = null;
            m_predictiveLods = null;
            m_predictiveCandidates.Clear();
            m_hasPredictiveCameraPose = false;
            PredictedVisibleLeafCount = 0;
            PredictivePrefetchDemandSplats = 0;
            PredictivePrefetchRequestedSplats = 0;
            m_rangesDirty = false;
            m_rangeScratch.Clear();
            m_activeRangeFiles.Clear();
            m_previousActiveRangeFiles.Clear();
            m_filesToApply.Clear();
        }

        void ReleaseSlotPayload(FileSlot slot)
        {
            m_uploadingSlots.Remove(slot);
            if (slot.Renderer)
            {
                // Disabling first unregisters the renderer and disposes any partial graphics
                // resources even when its explicit Update failed during initial binding.
                slot.Renderer.enabled = false;
                slot.Renderer.GsplatAsset = null;
            }
            DestroyAsset(slot.Asset);
            slot.Asset = null;
            slot.GpuReady = false;
            slot.AppliedRanges = Array.Empty<GsplatActiveRange>();
        }

        static void DestroySlot(FileSlot slot)
        {
            if (slot == null) return;
            slot.LoadCancellation?.Cancel();
            if (slot.Renderer)
            {
                slot.Renderer.GsplatAsset = null;
                if (slot.Renderer.enabled) slot.Renderer.Update();
            }
            DestroyAsset(slot.Asset);
            if (slot.GameObject)
            {
                if (Application.isPlaying) Destroy(slot.GameObject);
                else DestroyImmediate(slot.GameObject);
            }
        }

        static void DestroyAsset(GsplatAsset asset)
        {
            if (!asset) return;
            if (GsplatLodSharedAssetCache.Release(asset)) return;
            if (Application.isPlaying) Destroy(asset);
            else DestroyImmediate(asset);
        }

        void OnValidate()
        {
            LodBaseDistance = Mathf.Max(0.1f, LodBaseDistance);
            LodMultiplier = Mathf.Max(1.2f, LodMultiplier);
            LodBehindPenalty = Mathf.Max(1f, LodBehindPenalty);
            LodRangeMin = Mathf.Max(0, LodRangeMin);
            LodRangeMax = Mathf.Max(LodRangeMin, LodRangeMax);
            MaxConcurrentLoads = Mathf.Clamp(MaxConcurrentLoads, 1, 16);
            MaxRetries = Mathf.Clamp(MaxRetries, 0, 8);
            FrustumPadding = Mathf.Max(0f, FrustumPadding);
            LodHysteresis = Mathf.Clamp(LodHysteresis, 0f, 0.49f);
            MaxActiveSplats = Mathf.Max(0, MaxActiveSplats);
            MaxEstimatedResidentGpuMB = Mathf.Max(0f, MaxEstimatedResidentGpuMB);
            m_forceEvaluate = true;
        }
    }

    /// <summary>URI/path resolution shared by runtime streaming and editor import.</summary>
    public static class GsplatLodUri
    {
        public const string StreamingAssetsScheme = "streaming-assets://";

        public static string ResolveManifestUri(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Manifest URI is empty.", nameof(value));
            value = value.Replace('\\', '/');
            if (value.StartsWith(StreamingAssetsScheme, StringComparison.OrdinalIgnoreCase))
                return ExpandStreamingAssets(value.Substring(StreamingAssetsScheme.Length));
            return HasExplicitUriScheme(value) || Path.IsPathRooted(value)
                ? value : Resolve(StreamingAssetsScheme, value);
        }

        public static string Resolve(string baseUri, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new ArgumentException("Resource URI is empty.", nameof(relative));
            relative = relative.Replace('\\', '/');
            if (relative.StartsWith(StreamingAssetsScheme, StringComparison.OrdinalIgnoreCase))
                return ExpandStreamingAssets(relative.Substring(StreamingAssetsScheme.Length));
            if (HasExplicitUriScheme(relative)) return relative;

            baseUri = (baseUri ?? string.Empty).Replace('\\', '/');
            if (baseUri.StartsWith(StreamingAssetsScheme, StringComparison.OrdinalIgnoreCase))
            {
                string suffix = baseUri.Substring(StreamingAssetsScheme.Length).Trim('/');
                string combined = relative.StartsWith("/", StringComparison.Ordinal)
                    ? relative.TrimStart('/')
                    : string.IsNullOrEmpty(suffix) ? relative : $"{suffix}/{relative}";
                return ExpandStreamingAssets(combined);
            }

            if (IsWindowsDrive(relative))
                return Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar));

            // URI references must be resolved before Path.IsPathRooted: '/assets/x' is an
            // origin-root HTTP/content/jar reference and '//host/x' is protocol-relative,
            // not a local filesystem path. Preserve the archive portion of Android jar URIs
            // for origin-root references because System.Uri otherwise returns "jar:/...".
            bool uriBase = HasExplicitUriScheme(baseUri);
            if (uriBase && baseUri.StartsWith("jar:", StringComparison.OrdinalIgnoreCase) &&
                relative.StartsWith("/", StringComparison.Ordinal) &&
                !relative.StartsWith("//", StringComparison.Ordinal))
            {
                int archiveEnd = baseUri.IndexOf('!');
                if (archiveEnd >= 0)
                    return baseUri.Substring(0, archiveEnd + 1) + relative;
            }

            if (uriBase && Uri.TryCreate(EnsureDirectoryUri(baseUri), UriKind.Absolute,
                    out Uri absoluteBase))
                return new Uri(absoluteBase, relative).AbsoluteUri;

            if (Path.IsPathRooted(relative))
                return Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar));
            string basePath = string.IsNullOrWhiteSpace(baseUri) ? Directory.GetCurrentDirectory() : baseUri;
            return Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        public static string DirectoryOf(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return string.Empty;
            if (Uri.TryCreate(uri, UriKind.Absolute, out Uri absolute) && !IsWindowsDrive(uri))
                return new Uri(absolute, ".").AbsoluteUri;
            return Path.GetDirectoryName(Path.GetFullPath(uri)) ?? string.Empty;
        }

        public static bool IsUnpackedSogMetadata(string uri) =>
            string.Equals(Path.GetFileName(UriPath(uri)), "meta.json", StringComparison.OrdinalIgnoreCase);

        public static string ExtensionOf(string uri) => Path.GetExtension(UriPath(uri));

        public static bool RequiresUnityWebRequest(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) || IsWindowsDrive(uri)) return false;
            return parsed.Scheme is "http" or "https" or "jar" or "content";
        }

        public static string LocalPath(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) && parsed.IsFile) return parsed.LocalPath;
            return uri;
        }

        static string UriPath(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out Uri parsed) && !IsWindowsDrive(uri))
                return Uri.UnescapeDataString(parsed.AbsolutePath);
            int query = uri.IndexOfAny(new[] { '?', '#' });
            return query >= 0 ? uri.Substring(0, query) : uri;
        }

        static string ExpandStreamingAssets(string suffix)
        {
            string root = Application.streamingAssetsPath.Replace('\\', '/');
            suffix = suffix.Replace('\\', '/').TrimStart('/');
            return string.IsNullOrEmpty(suffix) ? root : Resolve(root, suffix);
        }

        static bool HasExplicitUriScheme(string value) => !IsWindowsDrive(value) &&
            Uri.TryCreate(value, UriKind.Absolute, out Uri parsed) && !string.IsNullOrEmpty(parsed.Scheme) &&
            value.IndexOf(':') > 0;
        static bool IsWindowsDrive(string value) => value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
        static string EnsureDirectoryUri(string value) => string.IsNullOrEmpty(value) || value.EndsWith("/")
            ? value : value + "/";
    }
}
