// Runtime grid for profiling tiled PlayCanvas LOD scenes.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using Unity.Profiling;
using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// Creates a centered square of streamed copies of one LOD tile. The active-splat budget is
    /// apportioned across frustum-visible leaves, while the resident-memory limit is divided
    /// across the grid. Identical source URIs share one decoded asset and GPU attribute resource.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public sealed class GsplatLodTileGrid : MonoBehaviour
    {
        struct ProbeFrameDiagnostic
        {
            public int Frame;
            public float FrameMs;
            public float RecenterMs;
            public float CounterMs;
            public float LodEvalSumMs;
            public float LodEvalMaxMs;
            public float ActiveRangeSumMs;
            public float UploadPollSumMs;
            public float LoadQueueSumMs;
            public float CooldownSumMs;
            public long LodEvalAllocatedBytes;
            public long ActiveRangeAllocatedBytes;
            public long UploadPollAllocatedBytes;
            public long LoadQueueAllocatedBytes;
            public float CpuTotalFrameMs;
            public float CpuMainThreadMs;
            public float CpuRenderThreadMs;
            public float GpuFrameMs;
            public long GcAllocatedBytes;
            public bool Recentred;
            public Vector3 RecenterDelta;
        }

        [Header("Tile grid")]
        [Range(3, 5)] public int GridSize = 3;
        [Min(0.1f)] public float TileSpacingMeters = 5f;
        public bool FollowCameraOnXZ = true;
        public GsplatLodStreamer TileTemplate;

        [Header("Shared grid budgets")]
        [Tooltip("Shared across visible tiles: first cover coarsest visible LODs, then distribute refinement capacity by each tile's additional desired-LOD cost.")]
        [Min(1)] public int TotalActiveSplatBudget = 8_000_000;
        [Tooltip("Aggregate estimated resident GPU memory. Visible tiles share most of this so old and replacement LOD files can coexist during streaming.")]
        [Min(128f)] public float TotalEstimatedResidentGpuMB = 6_400f;
        [Min(0f)] public float CacheCooldownSeconds = 8f;

        [Header("LOD quality")]
        [Min(0.1f)] public float LodBaseDistance = 0.5f;
        [Min(1.2f)] public float LodMultiplier = 2f;
        [Min(0)] public int LodRangeMin;
        [Min(0)] public int LodRangeMax = 6;
        [Range(0f, 0.49f)] public float LodHysteresis = 0.1f;
        [Range(1, 120)] public int LodUpdateIntervalFrames = 10;
        [Min(0)] public int LodUnderfillLimit;

        [Header("Predictive streaming")]
        [Tooltip("Predicts the next camera view from measured linear and angular velocity, then preloads its tile LODs.")]
        public bool EnablePredictivePrefetch = false;
        [Min(0.05f)] public float PredictionHorizonSeconds = 1.25f;
        [Tooltip("Also preloads this many finer LOD levels than the predicted view needs. Lower LOD indices contain more detail.")]
        [Range(0, 2)] public int PredictiveLodRefinementLevels = 1;
        [Min(0f)] public float PredictionFrustumPaddingMeters = 1f;
        [Min(0)] public int PredictivePrefetchSplatBudgetPerTile = 250_000;
        [Tooltip("Resident-memory pool reserved for predicted tiles, taken from the aggregate resident budget only while prefetch requests exist.")]
        [Min(0f)] public float PredictiveResidentBudgetMB = 512f;
        [Min(0.01f)] public float PredictionVelocitySmoothingSeconds = 0.2f;
        [Min(0f)] public float MaxPredictionSpeedMetersPerSecond = 20f;
        [Min(0f)] public float MaxPredictionAngularSpeedDegreesPerSecond = 360f;

        [Header("Camera-motion measurement")]
        public Camera ProbeCamera;
        public bool RunCameraMotionProbeOnPlay;
        [Min(0f)] public float ProbeWarmupSeconds = 4f;
        [Min(1f)] public float ProbeDurationSeconds = 16f;
        [Min(0.1f)] public float ProbeLegSeconds = 2f;
        public Vector3 ProbeStartPosition = new(0f, 3f, -3f);
        public Vector3 ProbeEndPosition = new(6f, 3f, -3f);

        [Header("Live counters (Play Mode)")]
        [SerializeField] int m_builtTileCount;
        [SerializeField] int m_activeSplatCount;
        [SerializeField] int m_peakActiveSplatCount;
        [SerializeField] int m_residentSplatCount;
        [SerializeField] int m_loadedFileCount;
        [SerializeField] int m_budgetVisibleTileCount;
        [SerializeField] int m_budgetVisibleLeafCount;
        [SerializeField] long m_budgetVisibleMinimumSplatDemand;
        [SerializeField] long m_budgetVisibleOptimalSplatDemand;
        [SerializeField] int m_budgetVisibleActiveSplatAllowance;
        [SerializeField] int m_predictiveTileCount;
        [SerializeField] int m_predictiveLeafCount;
        [SerializeField] long m_predictivePrefetchDemandSplats;
        [SerializeField] long m_predictivePrefetchRequestedSplats;
        [SerializeField] Vector3 m_measuredCameraVelocity;
        [SerializeField] Vector3 m_measuredAngularVelocity;
        [SerializeField, TextArea(2, 5)] string m_lastProbeReport;

        readonly List<GsplatLodStreamer> m_streamers = new();
        readonly List<float> m_frameTimesMs = new(2048);
        readonly List<ProbeFrameDiagnostic> m_frameDiagnostics = new(2048);
        Transform m_cameraTransform;
        Vector3 m_savedCameraPosition;
        Quaternion m_savedCameraRotation;
        float m_probeStartTime;
        Vector3 m_gridCenterWorld;
        Vector3 m_previousCameraPosition;
        Quaternion m_previousCameraRotation;
        Vector3 m_smoothedCameraVelocity;
        Vector3 m_smoothedCameraAngularVelocity;
        int m_peakResidentSplatCount;
        bool m_probeRunning;
        bool m_hasPreviousCameraPose;
        public int PredictiveTileCount { get; private set; }
        public int PredictiveLeafCount { get; private set; }
        public long PredictivePrefetchDemandSplats { get; private set; }
        public long PredictivePrefetchRequestedSplats { get; private set; }
        float m_lastRecenterCpuMilliseconds;
        float m_lastCounterCpuMilliseconds;
        bool m_lastFrameRecentred;
        Vector3 m_lastRecenteringDelta;
        ProfilerRecorder m_cpuTotalFrameRecorder;
        ProfilerRecorder m_cpuMainThreadRecorder;
        ProfilerRecorder m_cpuRenderThreadRecorder;
        ProfilerRecorder m_gpuFrameRecorder;
        ProfilerRecorder m_gcAllocatedRecorder;
        bool m_profilerRecordersStarted;

        void Awake()
        {
            if (!Application.isPlaying) return;
            BuildGrid();
        }

        void Start()
        {
            if (!Application.isPlaying || !RunCameraMotionProbeOnPlay) return;
            BeginCameraMotionProbe();
        }

        void Update()
        {
            if (!Application.isPlaying) return;

            float elapsed = m_probeRunning ? Time.unscaledTime - m_probeStartTime : -1f;
            float sampleElapsed = elapsed - ProbeWarmupSeconds;
            bool collectingSample = m_probeRunning && elapsed >= ProbeWarmupSeconds &&
                                    sampleElapsed < ProbeDurationSeconds;
            if (collectingSample)
            {
                float frameMs = Time.unscaledDeltaTime * 1000f;
                m_frameTimesMs.Add(frameMs);
                m_frameDiagnostics.Add(CaptureFrameDiagnostic(frameMs));
            }

            long cpuStart = Stopwatch.GetTimestamp();
            m_lastFrameRecentred = FollowCameraOnXZ && RecenterGridOnCamera();
            m_lastRecenteringDelta = m_lastFrameRecentred ? m_pendingRecenteringDelta : Vector3.zero;
            m_lastRecenterCpuMilliseconds = (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency);
            cpuStart = Stopwatch.GetTimestamp();
            UpdateCameraPrediction();
            UpdateVisibleTileActiveBudgets();
            UpdateCounters();
            m_lastCounterCpuMilliseconds = (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency);
            if (!m_probeRunning || !m_cameraTransform) return;

            if (elapsed < ProbeWarmupSeconds)
            {
                m_cameraTransform.position = ProbeStartPosition;
                return;
            }

            if (sampleElapsed < ProbeDurationSeconds)
            {
                m_cameraTransform.position = PositionOnProbePath(sampleElapsed);
                return;
            }

            m_probeRunning = false;
            m_cameraTransform.position = ProbeEndPosition;
            FinishCameraMotionProbe();
        }

        void OnDisable() => DisposeProfilerRecorders();

        void BuildGrid()
        {
            GsplatLodSharedAssetCache.ResetCounters();
            if (!TileTemplate)
            {
                TileTemplate = FindFirstObjectByType<GsplatLodStreamer>();
                if (!TileTemplate)
                {
                    Debug.LogError("[Gsplat LOD Grid] Assign a tile template streamer.", this);
                    enabled = false;
                    return;
                }
            }

            GridSize = Mathf.Clamp(GridSize, 3, 5);
            TileSpacingMeters = Mathf.Max(0.1f, TileSpacingMeters);
            if (ProbeCamera == null)
                ProbeCamera = TileTemplate.LodCamera ? TileTemplate.LodCamera : Camera.main;
            int tileCount = GridSize * GridSize;
            if (m_recenterTargetPositions.Length != tileCount)
            {
                m_recenterTargetPositions = new Vector3[tileCount];
                m_recenterTargetOccupied = new bool[tileCount];
            }
            if (m_recenterRecycleTiles.Capacity < tileCount)
                m_recenterRecycleTiles.Capacity = tileCount;
            int perTileActive = Mathf.Max(1, TotalActiveSplatBudget / tileCount);
            float perTileResident = Mathf.Max(1f, TotalEstimatedResidentGpuMB / tileCount);
            Vector3 center = GetGridCenterWorld();
            m_gridCenterWorld = center;
            Debug.Log($"[Gsplat LOD Grid] Building {GridSize}x{GridSize} around {center}; " +
                      $"initial per-tile active/resident budgets={perTileActive}/{perTileResident:F1} MiB; " +
                      $"camera-follow-XZ={FollowCameraOnXZ}.", this);
            Quaternion tileRotation = TileTemplate.transform.rotation;
            Vector3 tileScale = TileTemplate.transform.lossyScale;
            Transform tileParent = transform;

            TileTemplate.transform.SetParent(tileParent, true);
            TileTemplate.transform.localScale = tileScale;
            m_streamers.Clear();

            float gridCenter = (GridSize - 1) * 0.5f;
            for (int z = 0; z < GridSize; ++z)
            for (int x = 0; x < GridSize; ++x)
            {
                Vector3 offset = new(
                    (x - gridCenter) * TileSpacingMeters,
                    0f,
                    (z - gridCenter) * TileSpacingMeters);
                Vector3 worldPosition = center + transform.rotation * offset;

                GsplatLodStreamer tile;
                if (x == GridSize / 2 && z == GridSize / 2)
                {
                    tile = TileTemplate;
                    tile.transform.position = worldPosition;
                    tile.transform.rotation = tileRotation;
                }
                else
                {
                    GameObject tileObject = Instantiate(TileTemplate.gameObject, worldPosition,
                        tileRotation, tileParent);
                    tileObject.name = $"GSplat Tile [{x + 1},{z + 1}]";
                    tile = tileObject.GetComponent<GsplatLodStreamer>();
                }

                ConfigureTile(tile, perTileActive, perTileResident, z * GridSize + x);
                tile.transform.localScale = tileScale;
                m_streamers.Add(tile);
            }

            m_builtTileCount = m_streamers.Count;
        }

        Vector3 GetGridCenterWorld()
        {
            Vector3 center = transform.position;
            Camera camera = ProbeCamera ? ProbeCamera : Camera.main;
            if (!FollowCameraOnXZ || !camera) return center;

            float spacing = Mathf.Max(0.1f, TileSpacingMeters);
            Vector3 cameraPosition = camera.transform.position;
            center.x = Mathf.Round(cameraPosition.x / spacing) * spacing;
            center.z = Mathf.Round(cameraPosition.z / spacing) * spacing;
            return center;
        }

        Vector3 m_pendingRecenteringDelta;
        Vector3[] m_recenterTargetPositions = Array.Empty<Vector3>();
        bool[] m_recenterTargetOccupied = Array.Empty<bool>();
        readonly List<GsplatLodStreamer> m_recenterRecycleTiles = new();

        bool RecenterGridOnCamera()
        {
            m_pendingRecenteringDelta = Vector3.zero;
            if (m_streamers.Count == 0) return false;
            Vector3 nextCenter = GetGridCenterWorld();
            Vector3 delta = nextCenter - m_gridCenterWorld;
            delta.y = 0f;
            if (delta.sqrMagnitude < 1e-6f) return false;

            int targetCount = GridSize * GridSize;
            if (m_recenterTargetPositions.Length != targetCount)
            {
                m_recenterTargetPositions = new Vector3[targetCount];
                m_recenterTargetOccupied = new bool[targetCount];
            }

            float gridCenter = (GridSize - 1) * 0.5f;
            int targetIndex = 0;
            for (int z = 0; z < GridSize; ++z)
            for (int x = 0; x < GridSize; ++x)
            {
                Vector3 offset = new((x - gridCenter) * TileSpacingMeters, 0f,
                    (z - gridCenter) * TileSpacingMeters);
                m_recenterTargetPositions[targetIndex++] = nextCenter + transform.rotation * offset;
            }

            // Keep tiles whose world positions are still part of the new grid. A one-cell
            // center shift should only recycle the outgoing row/column; moving every tile
            // at once makes the whole grid jump and forces every renderer transform to
            // change on the same frame.
            Array.Clear(m_recenterTargetOccupied, 0, targetCount);
            m_recenterRecycleTiles.Clear();
            const float positionToleranceSqr = 0.0001f;
            foreach (GsplatLodStreamer tile in m_streamers)
            {
                if (!tile) continue;
                Vector3 position = tile.transform.position;
                int matchingTarget = -1;
                for (int i = 0; i < targetCount; ++i)
                {
                    if ((position - m_recenterTargetPositions[i]).sqrMagnitude > positionToleranceSqr)
                        continue;
                    matchingTarget = i;
                    break;
                }

                if (matchingTarget >= 0 && !m_recenterTargetOccupied[matchingTarget])
                    m_recenterTargetOccupied[matchingTarget] = true;
                else
                    m_recenterRecycleTiles.Add(tile);
            }

            int recycleIndex = 0;
            for (int i = 0; i < targetCount && recycleIndex < m_recenterRecycleTiles.Count; ++i)
            {
                if (m_recenterTargetOccupied[i]) continue;
                m_recenterRecycleTiles[recycleIndex++].transform.position = m_recenterTargetPositions[i];
            }

            m_gridCenterWorld = nextCenter;
            m_pendingRecenteringDelta = delta;
            return true;
        }

        void UpdateVisibleTileActiveBudgets()
        {
            if (m_streamers.Count == 0) return;

            // Streamers evaluate in Update after this grid controller. Their latest visible-leaf
            // minimum costs are therefore the cheapest accurate weights without duplicating
            // frustum and LOD traversal here.
            int visibleLeafCount = 0;
            int visibleTileCount = 0;
            long visibleMinimumSplatDemand = 0;
            long visibleRefinementSplatDemand = 0;
            foreach (GsplatLodStreamer tile in m_streamers)
            {
                if (!tile) continue;
                int leaves = Mathf.Max(0, tile.VisibleLeafCount);
                if (leaves == 0) continue;
                visibleLeafCount += leaves;
                ++visibleTileCount;
                long tileMinimum = Math.Max(0, tile.VisibleMinimumActiveSplatCount);
                if (tileMinimum <= 0) tileMinimum = leaves;
                long tileOptimal = Math.Max(tileMinimum, tile.VisibleOptimalActiveSplatCount);
                visibleMinimumSplatDemand += tileMinimum;
                visibleRefinementSplatDemand += tileOptimal - tileMinimum;
            }

            if (visibleLeafCount == 0)
            {
                UpdateVisibleTileResidentBudgets(0);
                m_budgetVisibleTileCount = 0;
                m_budgetVisibleLeafCount = 0;
                m_budgetVisibleMinimumSplatDemand = 0;
                m_budgetVisibleOptimalSplatDemand = 0;
                m_budgetVisibleActiveSplatAllowance = 0;
                return;
            }

            int totalBudget = Mathf.Max(1, TotalActiveSplatBudget);
            int inactiveTileAllowance = Mathf.Max(1, totalBudget / Mathf.Max(1, GridSize * GridSize));
            if (visibleMinimumSplatDemand <= 0)
                visibleMinimumSplatDemand = visibleLeafCount;
            long minimumDemand = visibleMinimumSplatDemand;
            long optimalDemand = minimumDemand + visibleRefinementSplatDemand;
            long extraBudget = Math.Max(0L, totalBudget - minimumDemand);
            int visibleAllowance = 0;
            foreach (GsplatLodStreamer tile in m_streamers)
            {
                if (!tile) continue;
                int leaves = Mathf.Max(0, tile.VisibleLeafCount);
                long tileMinimum = Math.Max(0, tile.VisibleMinimumActiveSplatCount);
                if (tileMinimum == 0 && leaves > 0) tileMinimum = leaves;
                long tileOptimal = Math.Max(tileMinimum, tile.VisibleOptimalActiveSplatCount);
                long tileRefinementDemand = tileOptimal - tileMinimum;
                int allowance;
                if (leaves == 0)
                    allowance = inactiveTileAllowance;
                else if (totalBudget >= minimumDemand)
                {
                    long weight = visibleRefinementSplatDemand > 0
                        ? tileRefinementDemand : tileMinimum;
                    long totalWeight = visibleRefinementSplatDemand > 0
                        ? visibleRefinementSplatDemand : minimumDemand;
                    allowance = (int)(tileMinimum + extraBudget * weight / totalWeight);
                }
                else
                    allowance = (int)((long)totalBudget * tileMinimum / minimumDemand);
                allowance = Mathf.Clamp(allowance, 1, totalBudget);
                if (tile.MaxActiveSplats != allowance)
                    tile.MaxActiveSplats = allowance;
                if (leaves > 0) visibleAllowance += allowance;
            }

            m_budgetVisibleTileCount = visibleTileCount;
            m_budgetVisibleLeafCount = visibleLeafCount;
            m_budgetVisibleMinimumSplatDemand = minimumDemand;
            m_budgetVisibleOptimalSplatDemand = optimalDemand;
            m_budgetVisibleActiveSplatAllowance = visibleAllowance;
            UpdateVisibleTileResidentBudgets(visibleTileCount);
        }

        void UpdateVisibleTileResidentBudgets(int visibleTileCount)
        {
            int tileCount = Mathf.Max(1, m_streamers.Count);
            float totalResidentBudget = Mathf.Max(1f, TotalEstimatedResidentGpuMB);
            float perTileBudget = totalResidentBudget / tileCount;
            float inactiveTileReserve = Mathf.Clamp(totalResidentBudget / (tileCount * 8f),
                Mathf.Min(16f, perTileBudget), perTileBudget);
            int residentInterestTileCount = 0;
            int predictiveTileCount = 0;
            int predictiveLeafCount = 0;
            long predictiveDemand = 0;
            foreach (GsplatLodStreamer tile in m_streamers)
            {
                if (!tile) continue;
                bool visible = tile.VisibleLeafCount > 0;
                bool predictive = tile.PredictivePrefetchDemandSplats > 0;
                predictiveLeafCount += tile.PredictedVisibleLeafCount;
                if (visible || predictive) ++residentInterestTileCount;
                if (!predictive) continue;
                ++predictiveTileCount;
                predictiveDemand += tile.PredictivePrefetchDemandSplats;
            }
            PredictiveLeafCount = predictiveLeafCount;

            int inactiveTileCount = Mathf.Max(0, tileCount - residentInterestTileCount);
            float inactiveResidentTotal = inactiveTileReserve * inactiveTileCount;
            float minimumVisibleReserve = Mathf.Min(128f * visibleTileCount,
                Mathf.Max(0f, totalResidentBudget - inactiveResidentTotal));
            float predictiveResidentBudget = predictiveDemand > 0
                ? Mathf.Min(Mathf.Max(0f, PredictiveResidentBudgetMB), totalResidentBudget * 0.25f,
                    Mathf.Max(0f, totalResidentBudget - inactiveResidentTotal - minimumVisibleReserve))
                : 0f;
            float visibleResidentBudget = Mathf.Max(0f,
                totalResidentBudget - inactiveResidentTotal - predictiveResidentBudget);
            float visibleTileBudget = visibleTileCount > 0
                ? visibleResidentBudget / visibleTileCount
                : inactiveTileReserve;
            long visibleRefinementDemand = 0;
            foreach (GsplatLodStreamer tile in m_streamers)
            {
                if (!tile || tile.VisibleLeafCount <= 0) continue;
                long minimum = Math.Max(0, tile.VisibleMinimumActiveSplatCount);
                if (minimum <= 0) minimum = tile.VisibleLeafCount;
                long optimal = Math.Max(minimum, tile.VisibleOptimalActiveSplatCount);
                visibleRefinementDemand += optimal - minimum;
            }
            PredictiveTileCount = predictiveTileCount;
            PredictivePrefetchDemandSplats = predictiveDemand;
            PredictivePrefetchRequestedSplats = 0;
            foreach (GsplatLodStreamer tile in m_streamers)
                if (tile) PredictivePrefetchRequestedSplats += tile.PredictivePrefetchRequestedSplats;

            float visibleTileReserve = visibleTileCount > 0
                ? Mathf.Min(128f, visibleResidentBudget / visibleTileCount)
                : 0f;
            float refinableResidentBudget = Mathf.Max(0f,
                visibleResidentBudget - visibleTileReserve * visibleTileCount);

            foreach (GsplatLodStreamer tile in m_streamers)
            {
                if (!tile) continue;
                float allowance = tile.PredictivePrefetchDemandSplats > 0
                    ? 0f : inactiveTileReserve;
                if (tile.VisibleLeafCount > 0)
                {
                    if (visibleRefinementDemand > 0)
                    {
                        long minimum = Math.Max(0, tile.VisibleMinimumActiveSplatCount);
                        if (minimum <= 0) minimum = tile.VisibleLeafCount;
                        long optimal = Math.Max(minimum, tile.VisibleOptimalActiveSplatCount);
                        long tileRefinementDemand = optimal - minimum;
                        allowance = visibleTileReserve + refinableResidentBudget *
                            tileRefinementDemand / visibleRefinementDemand;
                    }
                    else
                    {
                        allowance = visibleTileBudget;
                    }
                }
                if (predictiveDemand > 0 && tile.PredictivePrefetchDemandSplats > 0)
                    allowance += predictiveResidentBudget *
                        tile.PredictivePrefetchDemandSplats / predictiveDemand;
                if (Mathf.Abs(tile.MaxEstimatedResidentGpuMB - allowance) > 0.5f)
                    tile.MaxEstimatedResidentGpuMB = allowance;
            }
        }

        void UpdateCameraPrediction()
        {
            Camera camera = ProbeCamera ? ProbeCamera : Camera.main;
            if (!EnablePredictivePrefetch || !camera)
            {
                m_hasPreviousCameraPose = false;
                m_smoothedCameraVelocity = Vector3.zero;
                m_smoothedCameraAngularVelocity = Vector3.zero;
                SetTilePrediction(false, Vector3.zero, Quaternion.identity);
                return;
            }

            Transform cameraTransform = camera.transform;
            Vector3 position = cameraTransform.position;
            Quaternion rotation = cameraTransform.rotation;
            float deltaTime = Time.unscaledDeltaTime;
            if (!m_hasPreviousCameraPose || deltaTime <= 1e-5f)
            {
                m_previousCameraPosition = position;
                m_previousCameraRotation = rotation;
                m_smoothedCameraVelocity = Vector3.zero;
                m_smoothedCameraAngularVelocity = Vector3.zero;
                m_hasPreviousCameraPose = true;
                SetTilePrediction(false, position, rotation);
                return;
            }

            Vector3 linearVelocity = (position - m_previousCameraPosition) / deltaTime;
            linearVelocity = Vector3.ClampMagnitude(linearVelocity,
                Mathf.Max(0f, MaxPredictionSpeedMetersPerSecond));

            Quaternion deltaRotation = rotation * Quaternion.Inverse(m_previousCameraRotation);
            deltaRotation.ToAngleAxis(out float angleDegrees, out Vector3 axis);
            if (angleDegrees > 180f)
            {
                angleDegrees = 360f - angleDegrees;
                axis = -axis;
            }
            Vector3 angularVelocity = axis.sqrMagnitude > 1e-8f
                ? axis.normalized * (angleDegrees / deltaTime)
                : Vector3.zero;
            angularVelocity = Vector3.ClampMagnitude(angularVelocity,
                Mathf.Max(0f, MaxPredictionAngularSpeedDegreesPerSecond));

            float smoothingSeconds = Mathf.Max(0.01f, PredictionVelocitySmoothingSeconds);
            float smoothing = 1f - Mathf.Exp(-deltaTime / smoothingSeconds);
            m_smoothedCameraVelocity = Vector3.Lerp(m_smoothedCameraVelocity, linearVelocity, smoothing);
            m_smoothedCameraAngularVelocity = Vector3.Lerp(m_smoothedCameraAngularVelocity,
                angularVelocity, smoothing);
            m_previousCameraPosition = position;
            m_previousCameraRotation = rotation;

            bool moving = m_smoothedCameraVelocity.sqrMagnitude >= 0.04f ||
                          m_smoothedCameraAngularVelocity.sqrMagnitude >= 4f;
            float horizon = Mathf.Max(0f, PredictionHorizonSeconds);
            Vector3 predictedPosition = position + m_smoothedCameraVelocity * horizon;
            float angularSpeed = m_smoothedCameraAngularVelocity.magnitude;
            Quaternion predictedRotation = angularSpeed > 1e-4f
                ? Quaternion.AngleAxis(angularSpeed * horizon,
                    m_smoothedCameraAngularVelocity / angularSpeed) * rotation
                : rotation;
            SetTilePrediction(moving, predictedPosition, predictedRotation);
        }

        void SetTilePrediction(bool enabled, Vector3 position, Quaternion rotation)
        {
            foreach (GsplatLodStreamer tile in m_streamers)
            {
                if (!tile) continue;
                tile.SetPredictiveCameraPose(enabled, position, rotation,
                    PredictionFrustumPaddingMeters, PredictiveLodRefinementLevels,
                    PredictivePrefetchSplatBudgetPerTile);
            }
        }

        ProbeFrameDiagnostic CaptureFrameDiagnostic(float frameMs)
        {
            ProbeFrameDiagnostic diagnostic = new()
            {
                Frame = Time.frameCount - 1,
                FrameMs = frameMs,
                RecenterMs = m_lastRecenterCpuMilliseconds,
                CounterMs = m_lastCounterCpuMilliseconds,
                Recentred = m_lastFrameRecentred,
                RecenterDelta = m_lastRecenteringDelta,
                CpuTotalFrameMs = RecorderNanosecondsToMilliseconds(m_cpuTotalFrameRecorder),
                CpuMainThreadMs = RecorderNanosecondsToMilliseconds(m_cpuMainThreadRecorder),
                CpuRenderThreadMs = RecorderNanosecondsToMilliseconds(m_cpuRenderThreadRecorder),
                GpuFrameMs = RecorderNanosecondsToMilliseconds(m_gpuFrameRecorder),
                GcAllocatedBytes = m_gcAllocatedRecorder.IsRunning ? m_gcAllocatedRecorder.LastValue : 0
            };
            foreach (GsplatLodStreamer streamer in m_streamers)
            {
                if (!streamer) continue;
                float eval = streamer.LastLodEvaluationCpuMilliseconds;
                diagnostic.LodEvalSumMs += eval;
                diagnostic.LodEvalMaxMs = Mathf.Max(diagnostic.LodEvalMaxMs, eval);
                diagnostic.ActiveRangeSumMs += streamer.LastActiveRangeUpdateCpuMilliseconds;
                diagnostic.UploadPollSumMs += streamer.LastGpuUploadPollCpuMilliseconds;
                diagnostic.LoadQueueSumMs += streamer.LastLoadQueuePumpCpuMilliseconds;
                diagnostic.CooldownSumMs += streamer.LastCooldownTickCpuMilliseconds;
                diagnostic.LodEvalAllocatedBytes += streamer.LastLodEvaluationAllocatedBytes;
                diagnostic.ActiveRangeAllocatedBytes += streamer.LastActiveRangeUpdateAllocatedBytes;
                diagnostic.UploadPollAllocatedBytes += streamer.LastGpuUploadPollAllocatedBytes;
                diagnostic.LoadQueueAllocatedBytes += streamer.LastLoadQueuePumpAllocatedBytes;
            }
            return diagnostic;
        }

        void ConfigureTile(GsplatLodStreamer tile, int perTileActive, float perTileResident, int tileIndex)
        {
            if (!tile) return;
            tile.MaxConcurrentLoads = 1;
            tile.MaxActiveSplats = perTileActive;
            tile.MaxEstimatedResidentGpuMB = perTileResident;
            tile.CacheCooldownSeconds = Mathf.Max(0f, CacheCooldownSeconds);
            tile.LodBaseDistance = Mathf.Max(0.1f, LodBaseDistance);
            tile.LodMultiplier = Mathf.Max(1.2f, LodMultiplier);
            tile.LodRangeMin = Mathf.Max(0, LodRangeMin);
            tile.LodRangeMax = Mathf.Max(tile.LodRangeMin, LodRangeMax);
            tile.LodHysteresis = Mathf.Clamp(LodHysteresis, 0f, 0.49f);
            tile.LodUpdateIntervalFrames = Mathf.Clamp(LodUpdateIntervalFrames, 1, 120);
            tile.LodUpdatePhaseFrames = tileIndex % tile.LodUpdateIntervalFrames;
            tile.LodUnderfillLimit = Mathf.Max(0, LodUnderfillLimit);
            if (ProbeCamera) tile.LodCamera = ProbeCamera;
            tile.SetPredictiveCameraPose(false, Vector3.zero, Quaternion.identity,
                PredictionFrustumPaddingMeters, PredictiveLodRefinementLevels,
                PredictivePrefetchSplatBudgetPerTile);
        }

        void BeginCameraMotionProbe()
        {
            Camera camera = ProbeCamera ? ProbeCamera : Camera.main;
            if (!camera)
            {
                Debug.LogWarning("[Gsplat LOD Grid] Camera-motion probe skipped: no camera is assigned.", this);
                return;
            }

            ProbeCamera = camera;
            m_cameraTransform = camera.transform;
            m_savedCameraPosition = m_cameraTransform.position;
            m_savedCameraRotation = m_cameraTransform.rotation;
            m_cameraTransform.position = ProbeStartPosition;
            m_probeStartTime = Time.unscaledTime;
            m_frameTimesMs.Clear();
            m_frameDiagnostics.Clear();
            m_frameTimesMs.Capacity = Mathf.Max(m_frameTimesMs.Capacity,
                Mathf.CeilToInt((ProbeDurationSeconds + 1f) * 500f));
            m_frameDiagnostics.Capacity = Mathf.Max(m_frameDiagnostics.Capacity, m_frameTimesMs.Capacity);
            m_peakActiveSplatCount = 0;
            m_peakResidentSplatCount = 0;
            m_probeRunning = true;
            StartProfilerRecorders();
            Debug.Log($"[Gsplat LOD Grid] {GridSize}x{GridSize} probe starts after {ProbeWarmupSeconds:F1}s warm-up; " +
                      $"path {ProbeStartPosition} <-> {ProbeEndPosition}, {ProbeLegSeconds:F1}s per leg for {ProbeDurationSeconds:F1}s.", this);
        }

        Vector3 PositionOnProbePath(float elapsed)
        {
            float leg = Mathf.Max(0.1f, ProbeLegSeconds);
            float phase = Mathf.Repeat(elapsed / leg, 2f);
            float t = phase <= 1f ? phase : 2f - phase;
            return Vector3.LerpUnclamped(ProbeStartPosition, ProbeEndPosition, t);
        }

        void UpdateCounters()
        {
            int active = 0;
            int resident = 0;
            int files = 0;
            int predictiveTiles = 0;
            int predictiveLeaves = 0;
            long predictiveDemand = 0;
            long predictiveRequested = 0;
            foreach (GsplatLodStreamer streamer in m_streamers)
            {
                if (!streamer) continue;
                active += streamer.ActiveSplatCount;
                resident += streamer.ResidentSplatCount;
                files += streamer.LoadedFileCount;
                if (streamer.PredictedVisibleLeafCount > 0) ++predictiveTiles;
                predictiveLeaves += streamer.PredictedVisibleLeafCount;
                predictiveDemand += streamer.PredictivePrefetchDemandSplats;
                predictiveRequested += streamer.PredictivePrefetchRequestedSplats;
            }
            m_activeSplatCount = active;
            m_residentSplatCount = resident;
            m_loadedFileCount = files;
            m_predictiveTileCount = predictiveTiles;
            m_predictiveLeafCount = predictiveLeaves;
            m_predictivePrefetchDemandSplats = predictiveDemand;
            m_predictivePrefetchRequestedSplats = predictiveRequested;
            m_measuredCameraVelocity = m_smoothedCameraVelocity;
            m_measuredAngularVelocity = m_smoothedCameraAngularVelocity;
            m_peakActiveSplatCount = Mathf.Max(m_peakActiveSplatCount, active);
            m_peakResidentSplatCount = Mathf.Max(m_peakResidentSplatCount, resident);
        }

        void FinishCameraMotionProbe()
        {
            if (m_cameraTransform)
            {
                m_cameraTransform.position = m_savedCameraPosition;
                m_cameraTransform.rotation = m_savedCameraRotation;
            }

            float[] sorted = m_frameTimesMs.ToArray();
            Array.Sort(sorted);
            int over167 = 0;
            int over333 = 0;
            int over500 = 0;
            for (int i = 0; i < sorted.Length; ++i)
            {
                if (sorted[i] > 16.7f) ++over167;
                if (sorted[i] > 33.3f) ++over333;
                if (sorted[i] > 50f) ++over500;
            }

            string result = $"{GridSize}x{GridSize}; frames={sorted.Length}; " +
                            $"p50/p95/p99/max={Percentile(sorted, .50f):F2}/{Percentile(sorted, .95f):F2}/" +
                            $"{Percentile(sorted, .99f):F2}/{Percentile(sorted, 1f):F2}ms; " +
                            $">16.7/>33.3/>50ms={over167}/{over333}/{over500}; " +
                            $"peakActive/peakResident={m_peakActiveSplatCount}/{m_peakResidentSplatCount}; " +
                            $"endActive/endResident/loadedFiles={m_activeSplatCount}/{m_residentSplatCount}/{m_loadedFileCount}; " +
                            $"predictiveTiles/leaves/requestedSplats={m_predictiveTileCount}/{m_predictiveLeafCount}/" +
                            $"{m_predictivePrefetchRequestedSplats}; " +
                            $"sharedAssetLoads/joins/reuses={GsplatLodSharedAssetCache.LoadCount}/" +
                            $"{GsplatLodSharedAssetCache.JoinedLoadCount}/{GsplatLodSharedAssetCache.ReuseCount}";
            m_lastProbeReport = result;
            Debug.Log($"[Gsplat LOD Grid] Probe result: {result}", this);

            ProbeFrameDiagnostic[] worstFrames = m_frameDiagnostics.ToArray();
            Array.Sort(worstFrames, (a, b) => b.FrameMs.CompareTo(a.FrameMs));
            int diagnosticCount = Mathf.Min(5, worstFrames.Length);
            for (int i = 0; i < diagnosticCount; ++i)
            {
                ProbeFrameDiagnostic frame = worstFrames[i];
                Debug.Log($"[Gsplat LOD Grid] Probe slow frame #{i + 1}: frame={frame.Frame}, " +
                          $"dt={frame.FrameMs:F2}ms, recenter={frame.RecenterMs:F3}ms " +
                          $"(moved={frame.Recentred}, delta={frame.RecenterDelta}), counters={frame.CounterMs:F3}ms, " +
                          $"LODsum/max={frame.LodEvalSumMs:F2}/{frame.LodEvalMaxMs:F2}ms, " +
                          $"ranges/upload/queue/cooldowns={frame.ActiveRangeSumMs:F2}/{frame.UploadPollSumMs:F2}/" +
                          $"{frame.LoadQueueSumMs:F2}/{frame.CooldownSumMs:F2}ms, " +
                          $"alloc(LOD/ranges/upload/queue)={frame.LodEvalAllocatedBytes}/{frame.ActiveRangeAllocatedBytes}/" +
                          $"{frame.UploadPollAllocatedBytes}/{frame.LoadQueueAllocatedBytes}B, " +
                          $"CPU(total/main/render)={frame.CpuTotalFrameMs:F2}/{frame.CpuMainThreadMs:F2}/" +
                          $"{frame.CpuRenderThreadMs:F2}ms, GPU={frame.GpuFrameMs:F2}ms, " +
                          $"GC={frame.GcAllocatedBytes}B.", this);
            }
            DisposeProfilerRecorders();
        }

        void StartProfilerRecorders()
        {
            DisposeProfilerRecorders();
            m_cpuTotalFrameRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Total Frame Time", 64);
            m_cpuMainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Main Thread Frame Time", 64);
            m_cpuRenderThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Render Thread Frame Time", 64);
            m_gpuFrameRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", 64);
            m_gcAllocatedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 64);
            m_profilerRecordersStarted = true;
        }

        void DisposeProfilerRecorders()
        {
            if (!m_profilerRecordersStarted) return;
            m_cpuTotalFrameRecorder.Dispose();
            m_cpuMainThreadRecorder.Dispose();
            m_cpuRenderThreadRecorder.Dispose();
            m_gpuFrameRecorder.Dispose();
            m_gcAllocatedRecorder.Dispose();
            m_profilerRecordersStarted = false;
        }

        static float RecorderNanosecondsToMilliseconds(ProfilerRecorder recorder) =>
            recorder.IsRunning ? recorder.LastValue * 1e-6f : 0f;

        static float Percentile(float[] sorted, float percentile)
        {
            if (sorted.Length == 0) return 0f;
            int index = Mathf.Clamp(Mathf.CeilToInt(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }

        void OnValidate()
        {
            GridSize = Mathf.Clamp(GridSize, 3, 5);
            TileSpacingMeters = Mathf.Max(0.1f, TileSpacingMeters);
            TotalActiveSplatBudget = Mathf.Max(1, TotalActiveSplatBudget);
            TotalEstimatedResidentGpuMB = Mathf.Max(128f, TotalEstimatedResidentGpuMB);
            CacheCooldownSeconds = Mathf.Max(0f, CacheCooldownSeconds);
            PredictionHorizonSeconds = Mathf.Max(0.05f, PredictionHorizonSeconds);
            PredictiveLodRefinementLevels = Mathf.Clamp(PredictiveLodRefinementLevels, 0, 2);
            PredictionFrustumPaddingMeters = Mathf.Max(0f, PredictionFrustumPaddingMeters);
            PredictivePrefetchSplatBudgetPerTile = Mathf.Max(0, PredictivePrefetchSplatBudgetPerTile);
            PredictiveResidentBudgetMB = Mathf.Max(0f, PredictiveResidentBudgetMB);
            PredictionVelocitySmoothingSeconds = Mathf.Max(0.01f, PredictionVelocitySmoothingSeconds);
            MaxPredictionSpeedMetersPerSecond = Mathf.Max(0f, MaxPredictionSpeedMetersPerSecond);
            MaxPredictionAngularSpeedDegreesPerSecond = Mathf.Max(0f, MaxPredictionAngularSpeedDegreesPerSecond);
            LodBaseDistance = Mathf.Max(0.1f, LodBaseDistance);
            LodMultiplier = Mathf.Max(1.2f, LodMultiplier);
            LodRangeMin = Mathf.Max(0, LodRangeMin);
            LodRangeMax = Mathf.Max(LodRangeMin, LodRangeMax);
            ProbeWarmupSeconds = Mathf.Max(0f, ProbeWarmupSeconds);
            ProbeDurationSeconds = Mathf.Max(1f, ProbeDurationSeconds);
            ProbeLegSeconds = Mathf.Max(0.1f, ProbeLegSeconds);
        }
    }
}
