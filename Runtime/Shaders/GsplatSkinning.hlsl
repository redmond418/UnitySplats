// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

#ifndef GSPLAT_SKINNING_INCLUDED
#define GSPLAT_SKINNING_INCLUDED

#if !defined(GSPLAT_DISABLE_SKINNING)
struct GsplatSkinWeight
{
    uint4 boneIndices;
    float4 boneWeights;
};

int _GsplatSkinningEnabled;
#ifdef GSPLAT_VERTEX_TEXTURES
Texture2D<float4> _GsplatSkinIndexTexture;
Texture2D<float4> _GsplatSkinWeightTexture;
Texture2D<float4> _GsplatSkinReferenceTexture;
Texture2D<float4> _GsplatSkinBoneTexture;
int _GsplatSkinIndexTextureWidth;
int _GsplatSkinWeightTextureWidth;
int _GsplatSkinReferenceTextureWidth;
int _GsplatSkinBoneTextureWidth;

int2 GsplatSkinTextureCoord(uint index, int width)
{
    uint unsignedWidth = (uint)width;
    return int2(index % unsignedWidth, index / unsignedWidth);
}

float4 GsplatLoadSkinIndexTexel(uint index)
{
    return _GsplatSkinIndexTexture.Load(int3(
        GsplatSkinTextureCoord(index, _GsplatSkinIndexTextureWidth), 0));
}

float4 GsplatLoadSkinWeightTexel(uint index)
{
    return _GsplatSkinWeightTexture.Load(int3(
        GsplatSkinTextureCoord(index, _GsplatSkinWeightTextureWidth), 0));
}

float4 GsplatLoadSkinBoneTexel(uint index)
{
    return _GsplatSkinBoneTexture.Load(int3(
        GsplatSkinTextureCoord(index, _GsplatSkinBoneTextureWidth), 0));
}

float4 GsplatLoadSkinReferenceTexel(uint index)
{
    return _GsplatSkinReferenceTexture.Load(int3(
        GsplatSkinTextureCoord(index, _GsplatSkinReferenceTextureWidth), 0));
}

GsplatSkinWeight GsplatLoadSkinWeight(uint sourceId)
{
    uint indexOffset = sourceId * 4u;
    GsplatSkinWeight skin;
    skin.boneIndices = uint4(
        GsplatDecodeTextureUInt(GsplatLoadSkinIndexTexel(indexOffset)),
        GsplatDecodeTextureUInt(GsplatLoadSkinIndexTexel(indexOffset + 1u)),
        GsplatDecodeTextureUInt(GsplatLoadSkinIndexTexel(indexOffset + 2u)),
        GsplatDecodeTextureUInt(GsplatLoadSkinIndexTexel(indexOffset + 3u)));
    skin.boneWeights = GsplatLoadSkinWeightTexel(sourceId);
    return skin;
}

float4x4 GsplatLoadBoneMatrix(uint boneIndex)
{
    uint matrixOffset = boneIndex * 4u;
    return float4x4(
        GsplatLoadSkinBoneTexel(matrixOffset),
        GsplatLoadSkinBoneTexel(matrixOffset + 1u),
        GsplatLoadSkinBoneTexel(matrixOffset + 2u),
        GsplatLoadSkinBoneTexel(matrixOffset + 3u));
}

float4x4 GsplatLoadReferenceInverse(uint sourceId)
{
    uint matrixOffset = sourceId * 3u;
    return float4x4(
        GsplatLoadSkinReferenceTexel(matrixOffset),
        GsplatLoadSkinReferenceTexel(matrixOffset + 1u),
        GsplatLoadSkinReferenceTexel(matrixOffset + 2u),
        float4(0.0, 0.0, 0.0, 1.0));
}
#else
StructuredBuffer<GsplatSkinWeight> _GsplatSkinWeights;
StructuredBuffer<float4> _GsplatSkinReferenceRows;
StructuredBuffer<float4> _GsplatSkinBoneRows;

GsplatSkinWeight GsplatLoadSkinWeight(uint sourceId)
{
    return _GsplatSkinWeights[sourceId];
}

float4x4 GsplatLoadBoneMatrix(uint boneIndex)
{
    uint matrixOffset = boneIndex * 4u;
    return float4x4(
        _GsplatSkinBoneRows[matrixOffset],
        _GsplatSkinBoneRows[matrixOffset + 1u],
        _GsplatSkinBoneRows[matrixOffset + 2u],
        _GsplatSkinBoneRows[matrixOffset + 3u]);
}

float4x4 GsplatLoadReferenceInverse(uint sourceId)
{
    uint matrixOffset = sourceId * 3u;
    return float4x4(
        _GsplatSkinReferenceRows[matrixOffset],
        _GsplatSkinReferenceRows[matrixOffset + 1u],
        _GsplatSkinReferenceRows[matrixOffset + 2u],
        float4(0.0, 0.0, 0.0, 1.0));
}
#endif

float4x4 GsplatLoadSkinMatrix(uint sourceId)
{
    GsplatSkinWeight skin = GsplatLoadSkinWeight(sourceId);
    float4x4 current =
        GsplatLoadBoneMatrix(skin.boneIndices.x) * skin.boneWeights.x +
        GsplatLoadBoneMatrix(skin.boneIndices.y) * skin.boneWeights.y +
        GsplatLoadBoneMatrix(skin.boneIndices.z) * skin.boneWeights.z +
        GsplatLoadBoneMatrix(skin.boneIndices.w) * skin.boneWeights.w;
    return mul(current, GsplatLoadReferenceInverse(sourceId));
}

#ifndef GSPLAT_SKINNING_POSITION_ONLY
SplatCovariance GsplatTransformCovariance(SplatCovariance covariance, float3x3 transform)
{
    float3x3 source = float3x3(
        covariance.covA.x, covariance.covA.y, covariance.covA.z,
        covariance.covA.y, covariance.covB.x, covariance.covB.y,
        covariance.covA.z, covariance.covB.y, covariance.covB.z);
    float3x3 deformed = mul(mul(transform, source), transpose(transform));

    SplatCovariance result;
    result.covA = float3(deformed[0][0], deformed[0][1], deformed[0][2]);
    result.covB = float3(deformed[1][1], deformed[1][2], deformed[2][2]);
    return result;
}

void GsplatApplySkinning(uint sourceId, inout float3 modelCenter,
                         inout SplatCovariance covariance)
{
    if (_GsplatSkinningEnabled == 0)
        return;

    float4x4 skin = GsplatLoadSkinMatrix(sourceId);
    modelCenter = mul(skin, float4(modelCenter, 1.0)).xyz;
    float3x3 deformation = float3x3(skin[0].xyz, skin[1].xyz, skin[2].xyz);
    covariance = GsplatTransformCovariance(covariance, deformation);
}

void GsplatApplySkinningSurface(uint sourceId, inout float3 modelCenter,
                                inout SplatCovariance covariance,
                                inout float3 modelNormal)
{
    if (_GsplatSkinningEnabled == 0)
        return;

    float4x4 skin = GsplatLoadSkinMatrix(sourceId);
    modelCenter = mul(skin, float4(modelCenter, 1.0)).xyz;
    float3x3 deformation = float3x3(skin[0].xyz, skin[1].xyz, skin[2].xyz);
    covariance = GsplatTransformCovariance(covariance, deformation);
    // Rig transforms are normally rotational or uniformly scaled. Normalizing the transformed
    // thin axis is stable on the portable shader target and avoids a per-vertex matrix inverse.
    modelNormal = normalize(mul(deformation, modelNormal));
}
#endif

void GsplatApplySkinningPosition(uint sourceId, inout float3 modelCenter)
{
    if (_GsplatSkinningEnabled == 0)
        return;
    modelCenter = mul(GsplatLoadSkinMatrix(sourceId), float4(modelCenter, 1.0)).xyz;
}
#else
#ifndef GSPLAT_SKINNING_POSITION_ONLY
void GsplatApplySkinning(uint sourceId, inout float3 modelCenter,
                         inout SplatCovariance covariance)
{
}

void GsplatApplySkinningSurface(uint sourceId, inout float3 modelCenter,
                                inout SplatCovariance covariance,
                                inout float3 modelNormal)
{
}
#endif

void GsplatApplySkinningPosition(uint sourceId, inout float3 modelCenter)
{
}
#endif

#endif
