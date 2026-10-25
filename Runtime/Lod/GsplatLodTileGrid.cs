// Runtime camera-centered grid for PlayCanvas LOD tiles.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using UnityEngine.Serialization;

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
        [Header("Tile grid")]
        [Range(3, 5)] public int GridSize = 3;
        [Min(0.1f)] public float TileSpacingMeters = 5f;
        public bool FollowCameraOnXZ = true;
        [FormerlySerializedAs("ProbeCamera")]
        [Tooltip("Camera that drives grid recentering and tile LOD selection. Defaults to the template camera, then Camera.main.")]
        public Camera GridCamera;
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

        readonly List<GsplatLodStreamer> m_streamers = new();
        Vector3 m_gridCenterWorld;
        Vector3 m_previousCameraPosition;
        Quaternion m_previousCameraRotation;
        Vector3 m_smoothedCameraVelocity;
        Vector3 m_smoothedCameraAngularVelocity;
        bool m_hasPreviousCameraPose;
        bool m_captureDiagnosticTimings;

        internal IReadOnlyList<GsplatLodStreamer> Tiles => m_streamers;
        internal int VisibleTileCount { get; private set; }
        internal int VisibleLeafCount { get; private set; }
        internal long VisibleMinimumSplatDemand { get; private set; }
        internal long VisibleOptimalSplatDemand { get; private set; }
        internal int VisibleActiveSplatAllowance { get; private set; }
        internal int PredictiveTileCount { get; private set; }
        internal int PredictiveLeafCount { get; private set; }
        internal long PredictivePrefetchDemandSplats { get; private set; }
        internal long PredictivePrefetchRequestedSplats { get; private set; }
        internal Vector3 MeasuredCameraVelocity => m_smoothedCameraVelocity;
        internal Vector3 MeasuredAngularVelocity => m_smoothedCameraAngularVelocity;
        internal bool RecentredThisFrame { get; private set; }
        internal Vector3 LastRecenteringDelta { get; private set; }
        internal float LastRecenterCpuMilliseconds { get; private set; }
        internal float LastGridControllerCpuMilliseconds { get; private set; }

        void Awake()
        {
            if (!Application.isPlaying) return;
            BuildGrid();
        }

        void Update()
        {
            if (!Application.isPlaying) return;

            bool captureTimings = m_captureDiagnosticTimings;
            long cpuStart = captureTimings ? Stopwatch.GetTimestamp() : 0;
            RecentredThisFrame = FollowCameraOnXZ && RecenterGridOnCamera();
            LastRecenteringDelta = RecentredThisFrame ? m_pendingRecenteringDelta : Vector3.zero;
            LastRecenterCpuMilliseconds = captureTimings
                ? (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency)
                : 0f;
            cpuStart = captureTimings ? Stopwatch.GetTimestamp() : 0;
            UpdateCameraPrediction();
            UpdateVisibleTileActiveBudgets();
            LastGridControllerCpuMilliseconds = captureTimings
                ? (float)((Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency)
                : 0f;
        }

        internal void SetDiagnosticTimingCapture(bool enabled) => m_captureDiagnosticTimings = enabled;

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
            if (GridCamera == null)
                GridCamera = TileTemplate.LodCamera ? TileTemplate.LodCamera : Camera.main;
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

        }

        Vector3 GetGridCenterWorld()
        {
            Vector3 center = transform.position;
            Camera camera = GridCamera ? GridCamera : Camera.main;
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
                VisibleTileCount = 0;
                VisibleLeafCount = 0;
                VisibleMinimumSplatDemand = 0;
                VisibleOptimalSplatDemand = 0;
                VisibleActiveSplatAllowance = 0;
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

            VisibleTileCount = visibleTileCount;
            VisibleLeafCount = visibleLeafCount;
            VisibleMinimumSplatDemand = minimumDemand;
            VisibleOptimalSplatDemand = optimalDemand;
            VisibleActiveSplatAllowance = visibleAllowance;
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
            Camera camera = GridCamera ? GridCamera : Camera.main;
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
            if (GridCamera) tile.LodCamera = GridCamera;
            tile.SetPredictiveCameraPose(false, Vector3.zero, Quaternion.identity,
                PredictionFrustumPaddingMeters, PredictiveLodRefinementLevels,
                PredictivePrefetchSplatBudgetPerTile);
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
        }
    }
}
