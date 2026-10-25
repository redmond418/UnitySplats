// Optional Play Mode profiling and camera-motion probe for GsplatLodTileGrid.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using Unity.Profiling;
using UnityEngine;

namespace Gsplat
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-500)]
    public sealed class GsplatLodGridDiagnostics : MonoBehaviour
    {
        struct ProbeFrameDiagnostic
        {
            public int Frame;
            public float FrameMs;
            public float RecenterMs;
            public float GridUpdateMs;
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

        [Header("Target")]
        public GsplatLodTileGrid Grid;

        [Header("Camera motion probe")]
        [Tooltip("Camera moved by the optional test. Defaults to the grid camera, then Camera.main.")]
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
        [SerializeField] int m_predictiveTileCount;
        [SerializeField] int m_predictiveLeafCount;
        [SerializeField] long m_predictivePrefetchDemandSplats;
        [SerializeField] long m_predictivePrefetchRequestedSplats;
        [SerializeField] Vector3 m_measuredCameraVelocity;
        [SerializeField] Vector3 m_measuredAngularVelocity;
        [SerializeField, TextArea(2, 5)] string m_lastProbeReport;

        readonly List<float> m_frameTimesMs = new(2048);
        readonly List<ProbeFrameDiagnostic> m_frameDiagnostics = new(2048);
        Transform m_cameraTransform;
        Camera m_savedGridCamera;
        Vector3 m_savedCameraPosition;
        Quaternion m_savedCameraRotation;
        float m_probeStartTime;
        int m_peakResidentSplatCount;
        bool m_probeRunning;
        ProfilerRecorder m_cpuTotalFrameRecorder;
        ProfilerRecorder m_cpuMainThreadRecorder;
        ProfilerRecorder m_cpuRenderThreadRecorder;
        ProfilerRecorder m_gpuFrameRecorder;
        ProfilerRecorder m_gcAllocatedRecorder;
        bool m_profilerRecordersStarted;

        public bool IsProbeRunning => m_probeRunning;

        void Awake()
        {
            if (!Grid) Grid = GetComponent<GsplatLodTileGrid>();
            if (Grid) return;

            Debug.LogError("[Gsplat LOD Diagnostics] Assign a GsplatLodTileGrid or place this component beside one.", this);
            enabled = false;
        }

        void Start()
        {
            if (Application.isPlaying && RunCameraMotionProbeOnPlay)
                BeginCameraMotionProbe();
        }

        void Update()
        {
            if (!Application.isPlaying || !Grid) return;

            UpdateCounters();
            if (!m_probeRunning || !m_cameraTransform) return;

            float elapsed = Time.unscaledTime - m_probeStartTime;
            float sampleElapsed = elapsed - ProbeWarmupSeconds;
            if (elapsed >= ProbeWarmupSeconds && sampleElapsed < ProbeDurationSeconds)
            {
                float frameMs = Time.unscaledDeltaTime * 1000f;
                m_frameTimesMs.Add(frameMs);
                m_frameDiagnostics.Add(CaptureFrameDiagnostic(frameMs));
            }

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

        void OnDisable()
        {
            if (m_probeRunning) RestoreProbeState();
            m_probeRunning = false;
            if (Grid) Grid.SetDiagnosticTimingCapture(false);
            DisposeProfilerRecorders();
        }

        void BeginCameraMotionProbe()
        {
            if (!Grid) return;
            Camera camera = ProbeCamera ? ProbeCamera : Grid.GridCamera ? Grid.GridCamera : Camera.main;
            if (!camera)
            {
                Debug.LogWarning("[Gsplat LOD Diagnostics] Camera-motion probe skipped: no camera is assigned.", this);
                return;
            }

            ProbeCamera = camera;
            m_cameraTransform = camera.transform;
            m_savedCameraPosition = m_cameraTransform.position;
            m_savedCameraRotation = m_cameraTransform.rotation;
            m_savedGridCamera = Grid.GridCamera;
            Grid.GridCamera = camera;
            foreach (GsplatLodStreamer tile in Grid.Tiles)
                if (tile) tile.LodCamera = camera;

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
            Grid.SetDiagnosticTimingCapture(true);
            StartProfilerRecorders();
            Debug.Log($"[Gsplat LOD Diagnostics] {Grid.GridSize}x{Grid.GridSize} probe starts after " +
                      $"{ProbeWarmupSeconds:F1}s warm-up; path {ProbeStartPosition} <-> {ProbeEndPosition}, " +
                      $"{ProbeLegSeconds:F1}s per leg for {ProbeDurationSeconds:F1}s.", this);
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
            foreach (GsplatLodStreamer streamer in Grid.Tiles)
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
            m_builtTileCount = Grid.Tiles.Count;
            m_activeSplatCount = active;
            m_residentSplatCount = resident;
            m_loadedFileCount = files;
            m_predictiveTileCount = predictiveTiles;
            m_predictiveLeafCount = predictiveLeaves;
            m_predictivePrefetchDemandSplats = predictiveDemand;
            m_predictivePrefetchRequestedSplats = predictiveRequested;
            m_measuredCameraVelocity = Grid.MeasuredCameraVelocity;
            m_measuredAngularVelocity = Grid.MeasuredAngularVelocity;
            m_peakActiveSplatCount = Mathf.Max(m_peakActiveSplatCount, active);
            m_peakResidentSplatCount = Mathf.Max(m_peakResidentSplatCount, resident);
        }

        ProbeFrameDiagnostic CaptureFrameDiagnostic(float frameMs)
        {
            ProbeFrameDiagnostic diagnostic = new()
            {
                Frame = Time.frameCount - 1,
                FrameMs = frameMs,
                RecenterMs = Grid.LastRecenterCpuMilliseconds,
                GridUpdateMs = Grid.LastGridControllerCpuMilliseconds,
                Recentred = Grid.RecentredThisFrame,
                RecenterDelta = Grid.LastRecenteringDelta,
                CpuTotalFrameMs = RecorderNanosecondsToMilliseconds(m_cpuTotalFrameRecorder),
                CpuMainThreadMs = RecorderNanosecondsToMilliseconds(m_cpuMainThreadRecorder),
                CpuRenderThreadMs = RecorderNanosecondsToMilliseconds(m_cpuRenderThreadRecorder),
                GpuFrameMs = RecorderNanosecondsToMilliseconds(m_gpuFrameRecorder),
                GcAllocatedBytes = m_gcAllocatedRecorder.IsRunning ? m_gcAllocatedRecorder.LastValue : 0
            };
            foreach (GsplatLodStreamer streamer in Grid.Tiles)
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

        void FinishCameraMotionProbe()
        {
            RestoreProbeState();
            Grid.SetDiagnosticTimingCapture(false);
            UpdateCounters();

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

            string result = $"{Grid.GridSize}x{Grid.GridSize}; frames={sorted.Length}; " +
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
            Debug.Log($"[Gsplat LOD Diagnostics] Probe result: {result}", this);

            ProbeFrameDiagnostic[] worstFrames = m_frameDiagnostics.ToArray();
            Array.Sort(worstFrames, (a, b) => b.FrameMs.CompareTo(a.FrameMs));
            int diagnosticCount = Mathf.Min(5, worstFrames.Length);
            for (int i = 0; i < diagnosticCount; ++i)
            {
                ProbeFrameDiagnostic frame = worstFrames[i];
                Debug.Log($"[Gsplat LOD Diagnostics] Slow frame #{i + 1}: frame={frame.Frame}, " +
                          $"dt={frame.FrameMs:F2}ms, recenter={frame.RecenterMs:F3}ms " +
                          $"(moved={frame.Recentred}, delta={frame.RecenterDelta}), grid-update={frame.GridUpdateMs:F3}ms, " +
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

        void RestoreProbeState()
        {
            if (m_cameraTransform)
            {
                m_cameraTransform.position = m_savedCameraPosition;
                m_cameraTransform.rotation = m_savedCameraRotation;
            }
            if (!Grid) return;

            Grid.GridCamera = m_savedGridCamera;
            Camera restoreCamera = Grid.GridCamera ? Grid.GridCamera : Camera.main;
            foreach (GsplatLodStreamer tile in Grid.Tiles)
                if (tile && restoreCamera) tile.LodCamera = restoreCamera;
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
            ProbeWarmupSeconds = Mathf.Max(0f, ProbeWarmupSeconds);
            ProbeDurationSeconds = Mathf.Max(1f, ProbeDurationSeconds);
            ProbeLegSeconds = Mathf.Max(0.1f, ProbeLegSeconds);
        }
    }
}
