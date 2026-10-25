// PlayCanvas-compatible Gaussian-splat LOD selection helpers.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gsplat
{
    public static class GsplatLodSelector
    {
        // tan(22.5 degrees), matching PlayCanvas' 45-degree reference vertical FOV.
        const float ReferenceTanHalfFov = 0.4142135623730950f;

        /// <summary>
        /// Selects a geometric LOD using nearest-AABB distance, perspective FOV compensation,
        /// and the same linear behind-camera penalty as PlayCanvas.
        /// </summary>
        public static int SelectLod(Bounds localBounds, Vector3 localCameraPosition,
            Vector3 localCameraForward, float verticalFovDegrees, float aspect,
            float lodBaseDistance, float lodMultiplier, int maxLod, int rangeMin, int rangeMax,
            float behindPenalty = 1f)
        {
            if (maxLod < 0) throw new ArgumentOutOfRangeException(nameof(maxLod));
            lodBaseDistance = Mathf.Max(0.1f, lodBaseDistance);
            lodMultiplier = Mathf.Max(1.2f, lodMultiplier);
            rangeMin = Mathf.Clamp(rangeMin, 0, maxLod);
            rangeMax = Mathf.Clamp(rangeMax, rangeMin, maxLod);

            float distance = GetEffectiveDistance(localBounds, localCameraPosition, localCameraForward,
                verticalFovDegrees, aspect, behindPenalty);

            int lod = 0;
            if (maxLod > 0 && distance >= lodBaseDistance)
            {
                lod = maxLod;
                float threshold = lodBaseDistance;
                for (int i = 2; i <= maxLod; ++i)
                    threshold *= lodMultiplier;
                while (lod > 1 && distance < threshold)
                {
                    threshold /= lodMultiplier;
                    --lod;
                }
            }

            return Mathf.Clamp(lod, rangeMin, rangeMax);
        }

        /// <summary>Returns the FOV-compensated nearest-AABB distance used for LOD bands.</summary>
        public static float GetEffectiveDistance(Bounds localBounds, Vector3 localCameraPosition,
            Vector3 localCameraForward, float verticalFovDegrees, float aspect,
            float behindPenalty = 1f)
        {
            if (!float.IsFinite(verticalFovDegrees) || verticalFovDegrees <= 0f || verticalFovDegrees >= 180f)
                throw new ArgumentOutOfRangeException(nameof(verticalFovDegrees));
            if (!float.IsFinite(aspect) || aspect <= 0f) throw new ArgumentOutOfRangeException(nameof(aspect));
            behindPenalty = Mathf.Max(1f, behindPenalty);

            Vector3 delta = localBounds.ClosestPoint(localCameraPosition) - localCameraPosition;
            float actualDistance = delta.magnitude;
            float penalizedDistance = actualDistance;
            if (behindPenalty > 1f && actualDistance > 0.01f)
            {
                Vector3 forward = localCameraForward.sqrMagnitude > 1e-12f
                    ? localCameraForward.normalized : Vector3.forward;
                float dot = Vector3.Dot(forward, delta) / actualDistance;
                if (dot < 0f)
                    penalizedDistance *= 1f + (-dot) * (behindPenalty - 1f);
            }

            float tanHalfV = Mathf.Tan(verticalFovDegrees * 0.5f * Mathf.Deg2Rad);
            float tanHalfH = tanHalfV * aspect;
            float fovScale = Mathf.Min(tanHalfV, tanHalfH) / ReferenceTanHalfFov;
            return penalizedDistance * fovScale;
        }

        /// <summary>Retains the current band inside a relative hysteresis zone around its boundary.</summary>
        public static int ApplyHysteresis(int currentLod, int desiredLod, float effectiveDistance,
            float lodBaseDistance, float lodMultiplier, float hysteresisFraction)
        {
            if (currentLod < 0 || currentLod == desiredLod || hysteresisFraction <= 0f)
                return desiredLod;

            lodBaseDistance = Mathf.Max(0.1f, lodBaseDistance);
            lodMultiplier = Mathf.Max(1.2f, lodMultiplier);
            hysteresisFraction = Mathf.Clamp(hysteresisFraction, 0f, 0.49f);

            if (desiredLod > currentLod)
            {
                float boundary = lodBaseDistance * Mathf.Pow(lodMultiplier, currentLod);
                return effectiveDistance >= boundary * (1f + hysteresisFraction)
                    ? desiredLod : currentLod;
            }

            float finerBoundary = lodBaseDistance * Mathf.Pow(lodMultiplier, currentLod - 1);
            return effectiveDistance <= finerBoundary * (1f - hysteresisFraction)
                ? desiredLod : currentLod;
        }
    }

    /// <summary>Deterministic sort/union helpers used before submitting ranges to a renderer.</summary>
    public static class GsplatLodRangeNormalizer
    {
        public static GsplatActiveRange[] Normalize(IEnumerable<GsplatActiveRange> ranges, uint splatCount)
        {
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));
            var sorted = new List<GsplatActiveRange>();
            foreach (GsplatActiveRange range in ranges)
            {
                ulong end = (ulong)range.Offset + range.Count;
                if (end > splatCount)
                    throw new ArgumentOutOfRangeException(nameof(ranges),
                        $"Active interval [{range.Offset},{end}) exceeds splat count {splatCount}.");
                if (range.Count != 0) sorted.Add(range);
            }
            sorted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            if (sorted.Count == 0) return Array.Empty<GsplatActiveRange>();

            var result = new List<GsplatActiveRange>(sorted.Count);
            ulong start = sorted[0].Offset;
            ulong endExclusive = start + sorted[0].Count;
            for (int i = 1; i < sorted.Count; ++i)
            {
                ulong nextStart = sorted[i].Offset;
                ulong nextEnd = nextStart + sorted[i].Count;
                if (nextStart <= endExclusive)
                {
                    if (nextEnd > endExclusive) endExclusive = nextEnd;
                    continue;
                }
                result.Add(new GsplatActiveRange
                    { Offset = (uint)start, Count = (uint)(endExclusive - start) });
                start = nextStart;
                endExclusive = nextEnd;
            }
            result.Add(new GsplatActiveRange
                { Offset = (uint)start, Count = (uint)(endExclusive - start) });
            return result.ToArray();
        }
    }
}
