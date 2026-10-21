// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Gsplat
{
    public enum GsplatRelightingMode
    {
        Volume,
        ProxyMesh
    }

    public enum GsplatVolumeLightMode
    {
        Automatic,
        SingleOverride
    }

    /// <summary>
    /// Unified relighting controller. Volume mode lights Gaussian surfels directly and estimates
    /// self-occlusion from a compact opacity volume. Proxy Mesh mode configures and selects the
    /// preserved <see cref="GsplatProxyRelighting"/> capture backend.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GsplatRenderer))]
    [AddComponentMenu("")]
    public class GsplatVolumeLighting : MonoBehaviour
    {
        const int k_minResolution = 16;
        const int k_maxResolution = 128;
        const int k_maxSupportedLights = 4;
        const double k_automaticLightRefreshSeconds = 0.25;

        // Per-sample optical-depth cap of an 8-bit opacity texel, -ln(1/255). The field stores
        // sqrt(depth / cap) so the shader decodes a texel with one multiply instead of a log().
        const float k_maxSampleOpticalDepth = 5.5451774f;

        [Tooltip("Chooses direct Gaussian volume lighting or the existing proxy-mesh capture path.")]
        public GsplatRelightingMode RelightingMode = GsplatRelightingMode.Volume;

        [Header("Proxy Mesh Relighting")]
        [Tooltip("Aligned MeshRenderer or SkinnedMeshRenderer used by Proxy Mesh mode.")]
        public Renderer ProxyMesh;

        [Tooltip("Camera used to capture the proxy lighting. When empty, the Main Camera or first active Game camera is selected automatically.")]
        public Camera ProxySourceCamera;

        [Range(0.1f, 1f)]
        public float ProxyTextureScale = 0.5f;

        [Range(0f, 1f)]
        public float ProxyBlend = 1f;

        [Range(0f, 5f)]
        public float ProxyBrightness = 2f;

        [Range(0f, 5f)]
        public float ProxyBackground = 1f;

        [SerializeField, HideInInspector] GsplatProxyRelighting m_proxyBinding;
        [SerializeField, HideInInspector] bool m_proxySettingsInitialized;

        [Tooltip(
            "Automatic combines the strongest active lights affecting this renderer. Single Override is useful for isolating one light while comparing results.")]
        public GsplatVolumeLightMode LightMode = GsplatVolumeLightMode.Automatic;

        [Tooltip("Maximum number of directional, point, or spot lights combined in Automatic mode.")]
        [Range(1, k_maxSupportedLights)]
        public int AutomaticLightLimit = k_maxSupportedLights;

        [Tooltip("Optional light used only in Single Override mode.")]
        [FormerlySerializedAs("SourceLight")]
        public Light SingleLightOverride;

        [Tooltip(
            "Crossfades from the original captured appearance at 0 to direct Gaussian surface lighting at 1.")]
        [Range(0f, 1f)]
        public float Blend = 1f;

        [Tooltip("Color of the light-independent fill applied to the Gaussian surface lighting.")]
        [ColorUsage(true, true)]
        public Color AmbientTint = Color.white;

        [Tooltip("Strength of the light-independent fill.")]
        [Range(0f, 2f)]
        public float AmbientStrength = 0.2f;

        [Tooltip("Multiplier applied to the combined direct contribution from resolved Unity lights.")]
        [Range(0f, 8f)]
        public float DirectStrength = 1f;

        [Tooltip(
            "Wraps diffuse illumination around thin or uncertain Gaussian normals. 0 is Lambertian; higher values soften the terminator.")]
        [Range(0f, 1f)]
        public float DiffuseWrap = 0.15f;

        [Header("Opacity Self-Occlusion")]
        [Tooltip(
            "Resolution of the renderer-local opacity field. Higher values preserve finer shadow detail but cost more memory and take longer to rebuild.")]
        [Range(k_minResolution, k_maxResolution)]
        public int FieldResolution = 64;

        [Tooltip(
            "Opacity accumulated into the self-occlusion field. Raise it for denser shadows or lower it for wispy captures.")]
        [Range(0.1f, 4f)]
        public float FieldDensity = 1f;

        [Tooltip(
            "Number of neighborhood smoothing passes used while building the opacity field. This reduces voxel grain and softens self-shadows.")]
        [Range(0, 2)]
        public int FieldSmoothing = 1;

        [Tooltip("Strength of opacity-derived self-occlusion. 0 disables the ray-marched shadow term.")]
        [Range(0f, 4f)]
        public float ShadowStrength = 1f;

        [Tooltip(
            "Number of opacity-field samples toward the light. More steps reduce missed occluders but add vertex-shader cost.")]
        [Range(1, 24)]
        public int ShadowSteps = 8;

        [Tooltip(
            "Distance from the receiving Gaussian before self-occlusion sampling begins, measured in opacity-field voxels. Raise it to reduce self-shadow acne.")]
        [Range(0f, 4f)]
        public float ShadowBias = 1.25f;

        SharedOpacityField m_field;
        bool m_forceRebuild;

        readonly Light[] m_resolvedLights = new Light[k_maxSupportedLights];
        readonly float[] m_resolvedLightScores = new float[k_maxSupportedLights];
        readonly Vector4[] m_lightColors = new Vector4[k_maxSupportedLights];
        readonly Vector4[] m_lightPositions = new Vector4[k_maxSupportedLights];
        readonly Vector4[] m_lightDirections = new Vector4[k_maxSupportedLights];
        readonly Vector4[] m_lightSpotParams = new Vector4[k_maxSupportedLights];
        int m_resolvedLightCount;
        double m_nextAutomaticLightSearch;

        static Texture3D s_emptyOpacityField;
        static bool s_warnedUnsupportedOpacityField;

        // One scene scan serves every relit renderer instead of each component allocating its
        // own FindObjectsByType result several times per second.
        static Light[] s_sceneLights = Array.Empty<Light>();
        static double s_nextSceneLightSearch;

        /// <summary>
        /// Opacity fields depend only on the asset and the field settings, so renderers drawing the
        /// same asset share one texture. A failed build is kept as an entry without a texture, which
        /// stops the renderer from re-scanning every splat each frame until an input changes.
        /// </summary>
        internal sealed class SharedOpacityField
        {
            public readonly FieldKey Key;
            public readonly int Generation;
            public Texture3D Texture;
            public Bounds Bounds;
            public int RefCount;

            public SharedOpacityField(FieldKey key, int generation)
            {
                Key = key;
                Generation = generation;
            }
        }

        internal readonly struct FieldKey : IEquatable<FieldKey>
        {
            public readonly GsplatAsset Asset;
            public readonly uint SplatCount;
            public readonly int Resolution;
            public readonly int Smoothing;
            public readonly float Density;

            public FieldKey(GsplatAsset asset, int resolution, int smoothing, float density)
            {
                Asset = asset;
                SplatCount = asset ? asset.SplatCount : 0;
                Resolution = resolution;
                Smoothing = smoothing;
                Density = density;
            }

            public bool Equals(FieldKey other) =>
                ReferenceEquals(Asset, other.Asset) && SplatCount == other.SplatCount &&
                Resolution == other.Resolution && Smoothing == other.Smoothing &&
                Density.Equals(other.Density);

            public override bool Equals(object obj) => obj is FieldKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(
                RuntimeHelpers.GetHashCode(Asset), SplatCount, Resolution, Smoothing, Density);
        }

        static readonly Dictionary<FieldKey, SharedOpacityField> s_sharedFields = new();
        static int s_sharedFieldGeneration;

        /// <summary>The generated renderer-local opacity field, or null before it is built.</summary>
        public Texture3D OpacityField => m_field?.Texture;

        /// <summary>True when the field could not be built and self-occlusion is disabled.</summary>
        internal bool IsOpacityFieldUnavailable => m_field != null && !m_field.Texture;

        /// <summary>Number of renderers currently sharing this component's opacity field.</summary>
        internal int OpacityFieldUsers => m_field?.RefCount ?? 0;

        /// <summary>Number of scene lights currently used by this component.</summary>
        public int ActiveLightCount
        {
            get
            {
                if (!TryGetComponent(out GsplatRenderer renderer))
                    return 0;
                return ResolveLights(renderer);
            }
        }

        /// <summary>The strongest resolved light, or null when only ambient fill is available.</summary>
        public Light PrimaryLight
        {
            get
            {
                if (!TryGetComponent(out GsplatRenderer renderer) || ResolveLights(renderer) == 0)
                    return null;
                return m_resolvedLights[0];
            }
        }

        /// <summary>True while the unified component has selected proxy-free volume lighting.</summary>
        internal bool IsVolumeModeSelected => isActiveAndEnabled &&
                                              RelightingMode == GsplatRelightingMode.Volume;

        /// <summary>True when volume lighting should submit shader data.</summary>
        internal bool IsActive => IsVolumeModeSelected && Blend > 0f;

        /// <summary>True while the unified component has selected proxy-mesh capture.</summary>
        internal bool IsProxyModeSelected => isActiveAndEnabled &&
                                             RelightingMode == GsplatRelightingMode.ProxyMesh;

        internal GsplatProxyRelighting ProxyBinding => m_proxyBinding;

        internal readonly struct ShaderData
        {
            public readonly Texture3D OpacityField;
            public readonly Vector4 BoundsMin;
            public readonly Vector4 BoundsMax;
            public readonly Vector4 BoundsInvSize;
            public readonly Vector4 LightingParams;
            public readonly Vector4 OcclusionParams;
            public readonly Vector4 AmbientColor;
            public readonly int LightCount;
            public readonly Vector4[] LightColors;
            public readonly Vector4[] LightPositions;
            public readonly Vector4[] LightDirections;
            public readonly Vector4[] LightSpotParams;
            public readonly Matrix4x4 NormalMatrix;
            public readonly Matrix4x4 WorldToLocal;

            public ShaderData(SharedOpacityField field, GsplatVolumeLighting source,
                int lightCount)
            {
                // Without a field (unsupported device or asset without CPU splat data) the
                // surface lighting still applies; only the self-occlusion term is disabled.
                bool hasField = field.Texture;
                OpacityField = hasField ? field.Texture : EmptyOpacityField;
                Bounds bounds = field.Bounds;
                Vector3 size = bounds.size;
                BoundsMin = new Vector4(bounds.min.x, bounds.min.y, bounds.min.z, 0f);
                BoundsMax = new Vector4(bounds.max.x, bounds.max.y, bounds.max.z, 0f);
                BoundsInvSize = new Vector4(
                    1f / Mathf.Max(size.x, 1e-6f),
                    1f / Mathf.Max(size.y, 1e-6f),
                    1f / Mathf.Max(size.z, 1e-6f),
                    0f);
                LightingParams = new Vector4(source.Blend, source.AmbientStrength,
                    source.DirectStrength, source.DiffuseWrap);

                // Per-renderer constants are resolved here instead of once per splat vertex.
                float voxelSize = size.magnitude / Mathf.Max(field.Key.Resolution, 1);
                OcclusionParams = new Vector4(
                    hasField ? source.ShadowStrength * k_maxSampleOpticalDepth : 0f,
                    source.ShadowSteps,
                    voxelSize * source.ShadowBias,
                    0f);

                Color ambient = source.AmbientTint.linear;
                AmbientColor = new Vector4(ambient.r, ambient.g, ambient.b, 1f);

                LightCount = lightCount;
                LightColors = source.m_lightColors;
                LightPositions = source.m_lightPositions;
                LightDirections = source.m_lightDirections;
                LightSpotParams = source.m_lightSpotParams;

                NormalMatrix = source.transform.worldToLocalMatrix.transpose;
                WorldToLocal = source.transform.worldToLocalMatrix;
            }
        }

        internal static Texture3D EmptyOpacityField
        {
            get
            {
                if (s_emptyOpacityField)
                    return s_emptyOpacityField;

                s_emptyOpacityField = new Texture3D(1, 1, 1, TextureFormat.R8, false)
                {
                    name = "Gsplat.EmptyOpacityField",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                s_emptyOpacityField.SetPixelData(new byte[] { 0 }, 0);
                s_emptyOpacityField.Apply(false, true);
                return s_emptyOpacityField;
            }
        }

        internal bool TryGetShaderData(out ShaderData data)
        {
            data = default;
            if (!IsActive || !TryGetComponent(out GsplatRenderer renderer) ||
                !renderer.GsplatAsset)
                return false;

            SharedOpacityField field = EnsureOpacityField(renderer.GsplatAsset);
            int lightCount = ResolveLights(renderer);
            UpdateLightShaderData(lightCount);
            data = new ShaderData(field, this, lightCount);
            return true;
        }

        internal bool TryGetProxyRelightingSource(out GsplatRelighting source,
            out float blend, out float brightness, out float background)
        {
            source = null;
            blend = ProxyBlend;
            brightness = ProxyBrightness;
            background = ProxyBackground;
            if (!IsProxyModeSelected)
                return false;

            bool created = false;
            if (!ResolveProxyBinding() && Application.isPlaying && ProxyMesh)
            {
                m_proxyBinding = ProxyMesh.gameObject.AddComponent<GsplatProxyRelighting>();
                created = m_proxyBinding;
            }
            if (!m_proxyBinding)
                return false;

            ApplyProxySettings(m_proxyBinding, created);
            return m_proxyBinding.TryGetRelightingSource(out source);
        }

        internal bool AdoptProxyBindingFromChildren()
        {
            if (m_proxyBinding && m_proxyBinding.transform.IsChildOf(transform))
                return true;

            m_proxyBinding = GetComponentInChildren<GsplatProxyRelighting>(true);
            if (!m_proxyBinding || m_proxyBinding.transform == transform)
            {
                m_proxyBinding = null;
                return false;
            }

            if (!ProxyMesh)
                ProxyMesh = m_proxyBinding.GetComponent<Renderer>() ??
                            m_proxyBinding.GetComponentInChildren<Renderer>(true);
            if (!m_proxySettingsInitialized)
            {
                ProxySourceCamera = m_proxyBinding.SourceCamera;
                ProxyTextureScale = m_proxyBinding.TextureScale;
                ProxyBlend = m_proxyBinding.Blend;
                ProxyBrightness = m_proxyBinding.Brightness;
                ProxyBackground = m_proxyBinding.Background;
                m_proxySettingsInitialized = true;
            }
            return true;
        }

        internal void SetProxyBinding(GsplatProxyRelighting binding)
        {
            m_proxyBinding = binding;
            m_proxySettingsInitialized = true;
            if (binding)
                ApplyProxySettings(binding, true);
            NotifyRenderer();
        }

        internal void ApplyProxySettings(GsplatProxyRelighting binding, bool refresh)
        {
            if (!binding)
                return;
            binding.SourceCamera = ProxySourceCamera;
            binding.TextureScale = ProxyTextureScale;
            binding.Blend = ProxyBlend;
            binding.Brightness = ProxyBrightness;
            binding.Background = ProxyBackground;
            binding.enabled = IsProxyModeSelected;
            if (refresh && binding.enabled)
                binding.Refresh();
        }

        bool ResolveProxyBinding()
        {
            if (m_proxyBinding && m_proxyBinding.transform.IsChildOf(transform) &&
                (!ProxyMesh || m_proxyBinding.gameObject == ProxyMesh.gameObject))
                return true;

            m_proxyBinding = ProxyMesh
                ? ProxyMesh.GetComponent<GsplatProxyRelighting>()
                : null;
            return m_proxyBinding || AdoptProxyBindingFromChildren();
        }

        int ResolveLights(GsplatRenderer renderer)
        {
            if (!renderer || !renderer.GsplatAsset)
            {
                Array.Clear(m_resolvedLights, 0, m_resolvedLights.Length);
                m_resolvedLightCount = 0;
                return 0;
            }

            if (LightMode == GsplatVolumeLightMode.SingleOverride)
            {
                Array.Clear(m_resolvedLights, 0, m_resolvedLights.Length);
                m_resolvedLightCount = IsSupportedLight(SingleLightOverride, renderer.gameObject.layer)
                    ? 1
                    : 0;
                if (m_resolvedLightCount != 0)
                    m_resolvedLights[0] = SingleLightOverride;
                return m_resolvedLightCount;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < m_nextAutomaticLightSearch)
                return m_resolvedLightCount;

            // A reset timer means this component was just enabled or edited, so it must see
            // lights added since the last shared scan.
            bool forceSceneSearch = m_nextAutomaticLightSearch == 0;
            m_nextAutomaticLightSearch = now + k_automaticLightRefreshSeconds;
            Array.Clear(m_resolvedLights, 0, m_resolvedLights.Length);
            Array.Clear(m_resolvedLightScores, 0, m_resolvedLightScores.Length);
            m_resolvedLightCount = 0;

            Bounds worldBounds = TransformBounds(renderer.transform, renderer.GsplatAsset.Bounds);
            int layer = renderer.gameObject.layer;
            ConsiderLight(RenderSettings.sun, layer, worldBounds);
            ConsiderLights(FindSceneLights(now, forceSceneSearch), layer, worldBounds);
            return m_resolvedLightCount;
        }

        static Light[] FindSceneLights(double now, bool force)
        {
            if (force || now >= s_nextSceneLightSearch)
            {
                s_sceneLights = FindObjectsByType<Light>(FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);
                s_nextSceneLightSearch = now + k_automaticLightRefreshSeconds;
            }
            return s_sceneLights;
        }

        void ConsiderLights(Light[] lights, int layer, Bounds worldBounds)
        {
            if (lights == null)
                return;
            foreach (Light light in lights)
                ConsiderLight(light, layer, worldBounds);
        }

        void ConsiderLight(Light light, int layer, Bounds worldBounds)
        {
            if (!IsSupportedLight(light, layer))
                return;
            for (int i = 0; i < m_resolvedLightCount; ++i)
                if (m_resolvedLights[i] == light)
                    return;

            float score = ScoreLight(light, worldBounds);
            if (score <= 0f)
                return;

            int limit = Mathf.Clamp(AutomaticLightLimit, 1, k_maxSupportedLights);
            int insertion = 0;
            while (insertion < m_resolvedLightCount &&
                   m_resolvedLightScores[insertion] >= score)
                ++insertion;
            if (insertion >= limit)
                return;

            int last = Mathf.Min(m_resolvedLightCount, limit - 1);
            for (int i = last; i > insertion; --i)
            {
                m_resolvedLights[i] = m_resolvedLights[i - 1];
                m_resolvedLightScores[i] = m_resolvedLightScores[i - 1];
            }
            m_resolvedLights[insertion] = light;
            m_resolvedLightScores[insertion] = score;
            m_resolvedLightCount = Mathf.Min(m_resolvedLightCount + 1, limit);
        }

        static bool IsSupportedLight(Light light, int layer)
        {
            if (!light || !light.isActiveAndEnabled || light.intensity <= 0f ||
                (light.cullingMask & (1 << layer)) == 0)
                return false;
            return light.type == LightType.Directional || light.type == LightType.Point ||
                   light.type == LightType.Spot;
        }

        static float ScoreLight(Light light, Bounds worldBounds)
        {
            Color color = light.color.linear;
            float brightness = Mathf.Max(color.r, Mathf.Max(color.g, color.b)) * light.intensity;
            if (light.type == LightType.Directional)
                return brightness;

            Vector3 position = light.transform.position;
            Vector3 closest = worldBounds.ClosestPoint(position);
            float distanceToBounds = Vector3.Distance(position, closest);
            float range = Mathf.Max(light.range, 1e-3f);
            if (distanceToBounds >= range)
                return 0f;

            if (light.type == LightType.Spot)
            {
                Vector3 toCenter = worldBounds.center - position;
                float centerDistance = toCenter.magnitude;
                if (centerDistance > 1e-4f)
                {
                    float angularRadius = Mathf.Asin(Mathf.Clamp01(
                        worldBounds.extents.magnitude / centerDistance));
                    float expandedHalfAngle = light.spotAngle * 0.5f * Mathf.Deg2Rad +
                                              angularRadius;
                    float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(
                        light.transform.forward, toCenter / centerDistance), -1f, 1f));
                    if (angle > expandedHalfAngle)
                        return 0f;
                }
            }

            float normalizedDistance = distanceToBounds / range;
            float rangeFade = 1f - normalizedDistance * normalizedDistance;
            rangeFade *= rangeFade;
            return brightness * rangeFade / Mathf.Max(distanceToBounds * distanceToBounds, 0.01f);
        }

        static Bounds TransformBounds(Transform transform, Bounds localBounds)
        {
            Matrix4x4 matrix = transform.localToWorldMatrix;
            Vector3 localExtents = localBounds.extents;
            Vector3 axisX = matrix.MultiplyVector(new Vector3(localExtents.x, 0f, 0f));
            Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, localExtents.y, 0f));
            Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, localExtents.z));
            Vector3 worldExtents = new(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(matrix.MultiplyPoint3x4(localBounds.center), worldExtents * 2f);
        }

        void UpdateLightShaderData(int lightCount)
        {
            for (int i = 0; i < k_maxSupportedLights; ++i)
            {
                Light light = i < lightCount ? m_resolvedLights[i] : null;
                if (!light)
                {
                    m_lightColors[i] = Vector4.zero;
                    m_lightPositions[i] = new Vector4(0f, 0f, 0f, 3f);
                    m_lightDirections[i] = Vector4.zero;
                    m_lightSpotParams[i] = Vector4.zero;
                    continue;
                }

                Color lightColor = light.color.linear * Mathf.Max(0f, light.intensity);
                m_lightColors[i] = new Vector4(lightColor.r, lightColor.g, lightColor.b, 1f);
                Vector3 position = light.transform.position;
                Vector3 forward = light.transform.forward.normalized;
                float inverseRangeSquared = 1f / Mathf.Max(light.range * light.range, 1e-6f);
                float shadowStrength = light.shadows == LightShadows.None
                    ? 0f
                    : light.shadowStrength;

                if (light.type == LightType.Directional)
                {
                    Vector3 toLight = -forward;
                    m_lightPositions[i] = Vector4.zero;
                    m_lightDirections[i] = new Vector4(toLight.x, toLight.y, toLight.z, 0f);
                    m_lightSpotParams[i] = new Vector4(0f, 0f, shadowStrength, 0f);
                    continue;
                }

                float lightType = light.type == LightType.Spot ? 2f : 1f;
                m_lightPositions[i] = new Vector4(position.x, position.y, position.z, lightType);
                m_lightDirections[i] = new Vector4(forward.x, forward.y, forward.z,
                    inverseRangeSquared);
                if (light.type == LightType.Spot)
                {
                    float outerCos = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad);
                    float innerAngle = Mathf.Clamp(light.innerSpotAngle, 0f, light.spotAngle);
                    float innerCos = Mathf.Cos(innerAngle * 0.5f * Mathf.Deg2Rad);
                    m_lightSpotParams[i] = new Vector4(outerCos,
                        1f / Mathf.Max(innerCos - outerCos, 1e-4f), shadowStrength, 0f);
                }
                else
                {
                    m_lightSpotParams[i] = new Vector4(0f, 0f, shadowStrength, 0f);
                }
            }
        }

        /// <summary>Rebuilds the opacity field immediately from the currently assigned asset.</summary>
        public bool RebuildOpacityField()
        {
            m_forceRebuild = true;
            if (!TryGetComponent(out GsplatRenderer renderer) || !renderer.GsplatAsset)
                return false;
            bool rebuilt = EnsureOpacityField(renderer.GsplatAsset).Texture;
            NotifyRenderer();
            return rebuilt;
        }

        SharedOpacityField EnsureOpacityField(GsplatAsset asset)
        {
            ClampSettings();
            var key = new FieldKey(asset, FieldResolution, FieldSmoothing, FieldDensity);
            if (!m_forceRebuild && m_field != null &&
                m_field.Generation == s_sharedFieldGeneration && m_field.Key.Equals(key))
                return m_field;

            bool forceRebuild = m_forceRebuild;
            m_forceRebuild = false;
            SharedOpacityField previous = m_field;
            // Acquire before releasing so a forced rebuild of the same key never drops the
            // shared entry to zero users in between.
            m_field = AcquireField(key, forceRebuild);
            ReleaseField(previous);
            return m_field;
        }

        static SharedOpacityField AcquireField(FieldKey key, bool forceRebuild)
        {
            if (!s_sharedFields.TryGetValue(key, out SharedOpacityField field))
            {
                field = new SharedOpacityField(key, s_sharedFieldGeneration);
                s_sharedFields.Add(key, field);
                forceRebuild = true;
            }

            ++field.RefCount;
            if (forceRebuild)
                BuildField(field);
            return field;
        }

        static void ReleaseField(SharedOpacityField field)
        {
            if (field == null || --field.RefCount > 0)
                return;
            if (s_sharedFields.TryGetValue(field.Key, out SharedOpacityField registered) &&
                registered == field)
                s_sharedFields.Remove(field.Key);
            DestroyUnityObject(field.Texture);
            field.Texture = null;
        }

        static bool SupportsOpacityField =>
            SystemInfo.supports3DTextures && SystemInfo.SupportsTextureFormat(TextureFormat.R8);

        static void BuildField(SharedOpacityField field)
        {
            DestroyUnityObject(field.Texture);
            field.Texture = null;
            field.Bounds = default;

            GsplatAsset asset = field.Key.Asset;
            if (!SupportsOpacityField)
            {
                if (!s_warnedUnsupportedOpacityField)
                {
                    s_warnedUnsupportedOpacityField = true;
                    Debug.LogWarning(
                        "[GsplatVolumeLighting] This device cannot sample R8 3D textures. Gaussian Relighting continues without opacity self-occlusion.");
                }
                return;
            }

            try
            {
                field.Texture = CreateOpacityTexture(asset, field.Key, out field.Bounds);
                if (!field.Texture)
                    Debug.LogWarning(
                        $"[GsplatVolumeLighting] '{asset.name}' has no CPU-side splat positions and opacities to build an opacity field from. Gaussian Relighting continues without self-occlusion.",
                        asset);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[GsplatVolumeLighting] Failed to build the opacity field for '{asset.name}' ({exception.Message}). Gaussian Relighting continues without self-occlusion.",
                    asset);
            }
        }

        static Texture3D CreateOpacityTexture(GsplatAsset asset, FieldKey key,
            out Bounds fieldBounds)
        {
            fieldBounds = default;
            if (asset.SplatCount == 0 || asset.SplatCount > int.MaxValue)
                return null;

            int resolution = key.Resolution;
            int voxelCount = checked(resolution * resolution * resolution);

            Bounds sourceBounds = asset.Bounds;
            Vector3 sourceSize = sourceBounds.size;
            float longestAxis = Mathf.Max(sourceSize.x, Mathf.Max(sourceSize.y, sourceSize.z));
            float minimumSize = Mathf.Max(longestAxis * 1e-4f, 1e-4f);
            sourceSize.x = Mathf.Max(sourceSize.x, minimumSize);
            sourceSize.y = Mathf.Max(sourceSize.y, minimumSize);
            sourceSize.z = Mathf.Max(sourceSize.z, minimumSize);
            Vector3 padding = sourceSize / Mathf.Max(1, resolution - 2);
            fieldBounds = new Bounds(sourceBounds.center, sourceSize + padding * 2f);
            var grid = new FieldGrid(fieldBounds, resolution);

            // Native scratch memory is returned as soon as the build finishes. The former managed
            // arrays left up to 18 MB of garbage per rebuild on the non-compacting Mono heap.
            var extinction = new NativeArray<float>(voxelCount, Allocator.Persistent);
            var pixels = default(NativeArray<byte>);
            try
            {
                Span<float> values = extinction.AsSpan();
                int added = asset switch
                {
                    GsplatAssetUncompressed uncompressed =>
                        AccumulateUncompressed(uncompressed, values, grid),
                    GsplatAssetSpark spark => AccumulateSpark(spark, values, grid),
                    _ => 0
                };
                if (added == 0)
                    return null;

                if (key.Smoothing > 0)
                    SmoothField(values, resolution, key.Smoothing);

                pixels = new NativeArray<byte>(voxelCount, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                EncodeOpticalDepth(values, pixels.AsSpan(), key.Density);

                var texture = new Texture3D(resolution, resolution, resolution,
                    TextureFormat.R8, false)
                {
                    name = $"{asset.name} Gaussian Opacity Field",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                try
                {
                    texture.SetPixelData(pixels, 0);
                    // Release the CPU copy; only the GPU texture is needed for sampling.
                    texture.Apply(false, true);
                }
                catch
                {
                    DestroyUnityObject(texture);
                    throw;
                }
                return texture;
            }
            finally
            {
                extinction.Dispose();
                if (pixels.IsCreated)
                    pixels.Dispose();
            }
        }

        readonly struct FieldGrid
        {
            public readonly int Resolution;
            public readonly Vector3 Min;
            public readonly Vector3 Scale;

            public FieldGrid(Bounds bounds, int resolution)
            {
                Resolution = resolution;
                Min = bounds.min;
                Vector3 size = bounds.size;
                float cells = resolution - 1;
                Scale = new Vector3(cells / size.x, cells / size.y, cells / size.z);
            }
        }

        static int AccumulateUncompressed(GsplatAssetUncompressed asset, Span<float> extinction,
            in FieldGrid grid)
        {
            Vector3[] positions = asset.Positions;
            Vector4[] colors = asset.Colors;
            if (positions == null || colors == null)
                return 0;

            int count = Mathf.Min((int)asset.SplatCount,
                Mathf.Min(positions.Length, colors.Length));
            for (int i = 0; i < count; ++i)
            {
                float alpha = Mathf.Clamp(colors[i].w, 0f, 1f - 1e-5f);
                if (alpha > 1f / 255f)
                    Splat(extinction, grid, positions[i], -Mathf.Log(1f - alpha));
            }
            return count;
        }

        static int AccumulateSpark(GsplatAssetSpark asset, Span<float> extinction,
            in FieldGrid grid)
        {
            uint4[] packedSplats = asset.PackedSplats;
            if (packedSplats == null)
                return 0;

            // SPARK opacity is 8-bit, so its extinction comes from a 256-entry table instead of
            // one log() per splat.
            Span<float> alphaExtinction = stackalloc float[256];
            for (int value = 0; value < 256; ++value)
            {
                float alpha = Mathf.Min(value / 255f, 1f - 1e-5f);
                alphaExtinction[value] = alpha > 1f / 255f ? -Mathf.Log(1f - alpha) : 0f;
            }

            int count = Mathf.Min((int)asset.SplatCount, packedSplats.Length);
            for (int i = 0; i < count; ++i)
            {
                uint4 packed = packedSplats[i];
                float splatExtinction = alphaExtinction[(int)(packed.x >> 24)];
                if (splatExtinction <= 0f)
                    continue;
                var position = new Vector3(
                    math.f16tof32(packed.y & 0xffffu),
                    math.f16tof32(packed.y >> 16),
                    math.f16tof32(packed.z & 0xffffu));
                Splat(extinction, grid, position, splatExtinction);
            }
            return count;
        }

        static void Splat(Span<float> extinction, in FieldGrid grid, Vector3 position,
            float value)
        {
            float gx = (position.x - grid.Min.x) * grid.Scale.x;
            float gy = (position.y - grid.Min.y) * grid.Scale.y;
            float gz = (position.z - grid.Min.z) * grid.Scale.z;
            float last = grid.Resolution - 1;
            // Written so NaN fails the test: a corrupt center is skipped instead of indexing
            // outside the field.
            if (!(gx >= 0f && gx <= last && gy >= 0f && gy <= last && gz >= 0f && gz <= last))
                return;

            int resolution = grid.Resolution;
            int x0 = Math.Min((int)gx, resolution - 1);
            int y0 = Math.Min((int)gy, resolution - 1);
            int z0 = Math.Min((int)gz, resolution - 1);
            float fx = gx - x0;
            float fy = gy - y0;
            float fz = gz - z0;
            int dx = x0 < resolution - 1 ? 1 : 0;
            int dy = y0 < resolution - 1 ? resolution : 0;
            int dz = z0 < resolution - 1 ? resolution * resolution : 0;
            int i000 = x0 + resolution * (y0 + resolution * z0);

            float wx0 = value * (1f - fx);
            float wx1 = value * fx;
            float wy0z0 = (1f - fy) * (1f - fz);
            float wy1z0 = fy * (1f - fz);
            float wy0z1 = (1f - fy) * fz;
            float wy1z1 = fy * fz;
            extinction[i000] += wx0 * wy0z0;
            extinction[i000 + dx] += wx1 * wy0z0;
            extinction[i000 + dy] += wx0 * wy1z0;
            extinction[i000 + dx + dy] += wx1 * wy1z0;
            extinction[i000 + dz] += wx0 * wy0z1;
            extinction[i000 + dx + dz] += wx1 * wy0z1;
            extinction[i000 + dy + dz] += wx0 * wy1z1;
            extinction[i000 + dx + dy + dz] += wx1 * wy1z1;
        }

        static void SmoothField(Span<float> values, int resolution, int passes)
        {
            // Separable 3-tap box blur done in place: each line is copied to a small stack
            // buffer first, so no second full-size field is needed.
            Span<float> line = stackalloc float[resolution];
            int plane = resolution * resolution;
            for (int pass = 0; pass < passes; ++pass)
            {
                BlurAxis(values, line, resolution, 1, resolution, plane);
                BlurAxis(values, line, resolution, resolution, 1, plane);
                BlurAxis(values, line, resolution, plane, 1, resolution);
            }
        }

        static void BlurAxis(Span<float> values, Span<float> line, int resolution, int stride,
            int outerStrideA, int outerStrideB)
        {
            for (int b = 0; b < resolution; ++b)
            for (int a = 0; a < resolution; ++a)
            {
                int start = a * outerStrideA + b * outerStrideB;
                for (int i = 0; i < resolution; ++i)
                    line[i] = values[start + i * stride];

                for (int i = 0; i < resolution; ++i)
                {
                    int lower = Math.Max(0, i - 1);
                    int upper = Math.Min(resolution - 1, i + 1);
                    float sum = 0f;
                    for (int sample = lower; sample <= upper; ++sample)
                        sum += line[sample];
                    values[start + i * stride] = sum / (upper - lower + 1);
                }
            }
        }

        static void EncodeOpticalDepth(ReadOnlySpan<float> extinction, Span<byte> pixels,
            float density)
        {
            float scale = density / k_maxSampleOpticalDepth;
            for (int i = 0; i < pixels.Length; ++i)
            {
                float depth = Mathf.Min(Mathf.Max(0f, extinction[i]) * scale, 1f);
                pixels[i] = (byte)(Mathf.Sqrt(depth) * 255f + 0.5f);
            }
        }

        void ClampSettings()
        {
            Blend = Mathf.Clamp01(Blend);
            ProxyTextureScale = Mathf.Clamp(ProxyTextureScale, 0.1f, 1f);
            ProxyBlend = Mathf.Clamp01(ProxyBlend);
            ProxyBrightness = Mathf.Clamp(ProxyBrightness, 0f, 5f);
            ProxyBackground = Mathf.Clamp(ProxyBackground, 0f, 5f);
            AutomaticLightLimit = Mathf.Clamp(AutomaticLightLimit, 1, k_maxSupportedLights);
            AmbientStrength = Mathf.Clamp(AmbientStrength, 0f, 2f);
            DirectStrength = Mathf.Clamp(DirectStrength, 0f, 8f);
            DiffuseWrap = Mathf.Clamp01(DiffuseWrap);
            FieldResolution = Mathf.Clamp(FieldResolution, k_minResolution, k_maxResolution);
            FieldDensity = Mathf.Clamp(FieldDensity, 0.1f, 4f);
            FieldSmoothing = Mathf.Clamp(FieldSmoothing, 0, 2);
            ShadowStrength = Mathf.Clamp(ShadowStrength, 0f, 4f);
            ShadowSteps = Mathf.Clamp(ShadowSteps, 1, 24);
            ShadowBias = Mathf.Clamp(ShadowBias, 0f, 4f);
        }

        protected virtual void OnEnable()
        {
            m_nextAutomaticLightSearch = 0;
            AdoptProxyBindingFromChildren();
            if (m_proxyBinding)
                ApplyProxySettings(m_proxyBinding, IsProxyModeSelected);
            NotifyRenderer();
        }

        protected virtual void OnDisable()
        {
            ReleaseOpacityField();
            // A preserved legacy binding becomes the fallback when the unified controller is
            // disabled, maintaining the earlier quick comparison workflow.
            if (m_proxyBinding)
            {
                m_proxyBinding.enabled = true;
                m_proxyBinding.Refresh();
            }
            NotifyRenderer();
        }

        protected virtual void OnDestroy()
        {
            ReleaseOpacityField();
        }

        protected virtual void OnValidate()
        {
            ClampSettings();
            m_nextAutomaticLightSearch = 0;
            AdoptProxyBindingFromChildren();
            if (m_proxyBinding)
                ApplyProxySettings(m_proxyBinding, false);
            NotifyRenderer();
        }

        void NotifyRenderer()
        {
            if (TryGetComponent(out GsplatRenderer renderer))
                renderer.NotifyVolumeLightingChanged();
        }

        void ReleaseOpacityField()
        {
            ReleaseField(m_field);
            m_field = null;
        }

        static void DestroyUnityObject(UnityEngine.Object value)
        {
            if (!value)
                return;
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticResources()
        {
            if (s_emptyOpacityField)
                DestroyUnityObject(s_emptyOpacityField);
            s_emptyOpacityField = null;
            s_warnedUnsupportedOpacityField = false;
            s_sceneLights = Array.Empty<Light>();
            s_nextSceneLightSearch = 0;

            // Components holding an entry from the previous generation re-acquire on their next
            // draw, so destroying these textures cannot leave a renderer bound to a dead field.
            foreach (SharedOpacityField field in s_sharedFields.Values)
            {
                DestroyUnityObject(field.Texture);
                field.Texture = null;
            }
            s_sharedFields.Clear();
            ++s_sharedFieldGeneration;
        }
    }
}
