// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

#ifndef GSPLAT_VOLUME_LIGHTING_INCLUDED
#define GSPLAT_VOLUME_LIGHTING_INCLUDED

int _GsplatVolumeEnabled;
sampler3D _GsplatVolumeDensity;       // R8 texel = sqrt(normalized optical depth)
float4 _GsplatVolumeBoundsMin;
float4 _GsplatVolumeBoundsMax;
float4 _GsplatVolumeBoundsInvSize;
float4 _GsplatVolumeLightingParams;  // blend, ambient strength, direct strength, diffuse wrap
float4 _GsplatVolumeOcclusionParams; // optical-depth scale (0 disables), steps, start distance
float4 _GsplatVolumeAmbientColor;
int _GsplatVolumeLightCount;
#define GSPLAT_VOLUME_MAX_LIGHTS 4
float4 _GsplatVolumeLightColors[GSPLAT_VOLUME_MAX_LIGHTS];
float4 _GsplatVolumeLightPositions[GSPLAT_VOLUME_MAX_LIGHTS]; // w: 0 directional, 1 point, 2 spot
float4 _GsplatVolumeLightDirections[GSPLAT_VOLUME_MAX_LIGHTS]; // xyz normalized direction; w inverse range squared
float4 _GsplatVolumeLightSpotParams[GSPLAT_VOLUME_MAX_LIGHTS]; // outer cosine, inverse cosine range, shadow strength
float4x4 _GsplatVolumeNormalMatrix;
float4x4 _GsplatVolumeWorldToLocal;

// Transmittance below exp(-8) is invisible after 8-bit output, so the march stops there.
#define GSPLAT_VOLUME_OPAQUE_DEPTH 8.0

float GsplatVolumeRayBoxExit(float3 origin, float3 direction, float3 boundsMin,
                             float3 boundsMax)
{
    float3 directionSign = lerp(-1.0.xxx, 1.0.xxx, step(0.0.xxx, direction));
    float3 safeDirection = directionSign * max(abs(direction), 1e-5.xxx);
    float3 t0 = (boundsMin - origin) / safeDirection;
    float3 t1 = (boundsMax - origin) / safeDirection;
    float3 farDistance = max(t0, t1);
    return max(0.0, min(farDistance.x, min(farDistance.y, farDistance.z)));
}

float GsplatVolumeTransmittance(float3 localCenter, float3 toLight,
                                float4 lightPosition, float lightShadowStrength)
{
    float strength = _GsplatVolumeOcclusionParams.x * lightShadowStrength;
    if (strength <= 0.0)
        return 1.0;

    float3 localDirection = mul((float3x3)_GsplatVolumeWorldToLocal, toLight);
    float localDirectionLength = length(localDirection);
    if (localDirectionLength <= 1e-6)
        return 1.0;
    localDirection /= localDirectionLength;

    float3 boundsMin = _GsplatVolumeBoundsMin.xyz;
    float rayLength = GsplatVolumeRayBoxExit(localCenter, localDirection,
                                             boundsMin, _GsplatVolumeBoundsMax.xyz);

    if (lightPosition.w > 0.5)
    {
        float3 localLight = mul(_GsplatVolumeWorldToLocal,
                                float4(lightPosition.xyz, 1.0)).xyz;
        rayLength = min(rayLength, length(localLight - localCenter));
    }

    float startDistance = _GsplatVolumeOcclusionParams.z;
    float usableLength = rayLength - startDistance;
    if (usableLength <= 1e-5)
        return 1.0;

    int sampleCount = clamp((int)round(_GsplatVolumeOcclusionParams.y), 1, 24);
    float stepLength = usableLength / sampleCount;
    float3 invSize = _GsplatVolumeBoundsInvSize.xyz;
    float3 uvw = (localCenter + localDirection * (startDistance + 0.5 * stepLength) -
                  boundsMin) * invSize;
    float3 uvwStep = localDirection * stepLength * invSize;
    float opaqueDepth = GSPLAT_VOLUME_OPAQUE_DEPTH / strength;
    float opticalDepth = 0.0;
    [loop]
    for (int sampleIndex = 0; sampleIndex < 24; ++sampleIndex)
    {
        if (sampleIndex >= sampleCount || opticalDepth >= opaqueDepth)
            break;
        float3 sampleUvw = uvw;
        uvw += uvwStep;
        if (any(sampleUvw <= 0.0.xxx) || any(sampleUvw >= 1.0.xxx))
            continue;
        float encoded = tex3Dlod(_GsplatVolumeDensity, float4(sampleUvw, 0.0)).r;
        opticalDepth += encoded * encoded;
    }
    return exp(-opticalDepth * strength);
}

float3 GsplatEvaluateVolumeLighting(float3 modelCenter, float3 modelNormal)
{
    if (_GsplatVolumeEnabled == 0)
        return 1.0.xxx;

    float3 worldCenter = mul(_MATRIX_M, float4(modelCenter, 1.0)).xyz;
    float3 worldNormal = normalize(mul((float3x3)_GsplatVolumeNormalMatrix,
                                       modelNormal));
    float3 viewDirection = normalize(_WorldSpaceCameraPos.xyz - worldCenter);
    if (dot(worldNormal, viewDirection) < 0.0)
        worldNormal = -worldNormal;

    float wrap = _GsplatVolumeLightingParams.w;
    float wrapScale = 1.0 / (1.0 + wrap);
    float3 ambient = _GsplatVolumeAmbientColor.rgb *
        _GsplatVolumeLightingParams.y;
    float3 direct = 0.0.xxx;

    [loop]
    for (int lightIndex = 0; lightIndex < GSPLAT_VOLUME_MAX_LIGHTS; ++lightIndex)
    {
        if (lightIndex >= _GsplatVolumeLightCount)
            break;

        float4 lightPosition = _GsplatVolumeLightPositions[lightIndex];
        float4 lightDirection = _GsplatVolumeLightDirections[lightIndex];
        float4 spotParams = _GsplatVolumeLightSpotParams[lightIndex];
        float lightType = lightPosition.w;
        float3 toLight;
        float attenuation = 1.0;

        if (lightType < 0.5)
        {
            toLight = lightDirection.xyz;
        }
        else
        {
            float3 delta = lightPosition.xyz - worldCenter;
            float distanceSquared = max(dot(delta, delta), 1e-4);
            toLight = delta * rsqrt(distanceSquared);

            // Match Unity's smooth inverse-square local-light falloff: energy diminishes
            // with distance and fades cleanly to zero at the configured range.
            float normalizedDistanceSquared = distanceSquared * lightDirection.w;
            float rangeFade = saturate(1.0 -
                                       normalizedDistanceSquared * normalizedDistanceSquared);
            attenuation = rangeFade * rangeFade / distanceSquared;

            if (lightType > 1.5)
            {
                float spotCosine = dot(lightDirection.xyz, -toLight);
                float spot = saturate((spotCosine - spotParams.x) * spotParams.y);
                attenuation *= spot * spot;
            }
        }

        float diffuse = saturate((dot(worldNormal, toLight) + wrap) * wrapScale);
        float irradiance = diffuse * attenuation;
        // Back-facing, out-of-range and out-of-cone lights add nothing, so skip their
        // shadow march, which is by far the most expensive part of this function.
        if (irradiance <= 0.0)
            continue;
        float visibility = GsplatVolumeTransmittance(modelCenter, toLight,
                                                     lightPosition, spotParams.z);
        direct += _GsplatVolumeLightColors[lightIndex].rgb * (irradiance * visibility);
    }

    direct *= _GsplatVolumeLightingParams.z;
    return max(ambient + direct, 0.0.xxx);
}

#endif
