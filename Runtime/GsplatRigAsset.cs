// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Gsplat
{
    /// <summary>Four-bone linear-blend skinning weights for one Gaussian.</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct GsplatRigWeight
    {
        public int BoneIndex0;
        public int BoneIndex1;
        public int BoneIndex2;
        public int BoneIndex3;
        public float Weight0;
        public float Weight1;
        public float Weight2;
        public float Weight3;

        public float GetWeight(int boneIndex)
        {
            float result = 0f;
            if (BoneIndex0 == boneIndex) result += Weight0;
            if (BoneIndex1 == boneIndex) result += Weight1;
            if (BoneIndex2 == boneIndex) result += Weight2;
            if (BoneIndex3 == boneIndex) result += Weight3;
            return result;
        }

        public void SetWeight(int boneIndex, float value, bool normalizeOthers)
        {
            value = Mathf.Clamp01(value);
            int slot = -1;
            for (int i = 0; i < 4; ++i)
            {
                if (GetIndex(i) == boneIndex && GetSlotWeight(i) > 0f)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
            {
                slot = 0;
                for (int i = 1; i < 4; ++i)
                    if (GetSlotWeight(i) < GetSlotWeight(slot))
                        slot = i;
                SetIndex(slot, boneIndex);
            }

            if (normalizeOthers)
            {
                float otherTotal = 0f;
                for (int i = 0; i < 4; ++i)
                    if (i != slot)
                        otherTotal += Mathf.Max(0f, GetSlotWeight(i));
                float remaining = 1f - value;
                if (otherTotal > 1e-8f)
                {
                    float scale = remaining / otherTotal;
                    for (int i = 0; i < 4; ++i)
                        if (i != slot)
                            SetSlotWeight(i, Mathf.Max(0f, GetSlotWeight(i)) * scale);
                }
                else
                {
                    for (int i = 0; i < 4; ++i)
                        if (i != slot)
                            SetSlotWeight(i, 0f);
                }
            }
            SetSlotWeight(slot, value);
            SortDescending();
        }

        public void Normalize()
        {
            Weight0 = Mathf.Max(0f, Weight0);
            Weight1 = Mathf.Max(0f, Weight1);
            Weight2 = Mathf.Max(0f, Weight2);
            Weight3 = Mathf.Max(0f, Weight3);
            float total = Weight0 + Weight1 + Weight2 + Weight3;
            if (total <= 1e-8f)
            {
                BoneIndex0 = BoneIndex1 = BoneIndex2 = BoneIndex3 = 0;
                Weight0 = 1f;
                Weight1 = Weight2 = Weight3 = 0f;
                return;
            }
            float inverse = 1f / total;
            Weight0 *= inverse;
            Weight1 *= inverse;
            Weight2 *= inverse;
            Weight3 *= inverse;
            SortDescending();
        }

        internal Matrix4x4 BlendMatrix(Matrix4x4[] boneMatrices)
        {
            Matrix4x4 a = boneMatrices[BoneIndex0];
            Matrix4x4 b = boneMatrices[BoneIndex1];
            Matrix4x4 c = boneMatrices[BoneIndex2];
            Matrix4x4 d = boneMatrices[BoneIndex3];
            return new Matrix4x4(
                a.GetColumn(0) * Weight0 + b.GetColumn(0) * Weight1 +
                c.GetColumn(0) * Weight2 + d.GetColumn(0) * Weight3,
                a.GetColumn(1) * Weight0 + b.GetColumn(1) * Weight1 +
                c.GetColumn(1) * Weight2 + d.GetColumn(1) * Weight3,
                a.GetColumn(2) * Weight0 + b.GetColumn(2) * Weight1 +
                c.GetColumn(2) * Weight2 + d.GetColumn(2) * Weight3,
                a.GetColumn(3) * Weight0 + b.GetColumn(3) * Weight1 +
                c.GetColumn(3) * Weight2 + d.GetColumn(3) * Weight3);
        }

        internal static GsplatRigWeight FromInfluences(
            int index0, float weight0, int index1, float weight1,
            int index2, float weight2, int index3, float weight3)
        {
            int count = 0;
            var result = new GsplatRigWeight();
            Add(ref result, ref count, index0, weight0);
            Add(ref result, ref count, index1, weight1);
            Add(ref result, ref count, index2, weight2);
            Add(ref result, ref count, index3, weight3);
            result.Normalize();
            return result;

            static void Add(ref GsplatRigWeight target, ref int influenceCount,
                int index, float weight)
            {
                if (weight <= 1e-8f)
                    return;
                for (int i = 0; i < influenceCount; ++i)
                {
                    if (target.GetIndex(i) != index)
                        continue;
                    target.SetSlotWeight(i, target.GetSlotWeight(i) + weight);
                    return;
                }
                if (influenceCount >= 4)
                    return;
                target.SetIndex(influenceCount, index);
                target.SetSlotWeight(influenceCount, weight);
                influenceCount++;
            }
        }

        int GetIndex(int slot) => slot switch
        {
            0 => BoneIndex0,
            1 => BoneIndex1,
            2 => BoneIndex2,
            _ => BoneIndex3
        };

        float GetSlotWeight(int slot) => slot switch
        {
            0 => Weight0,
            1 => Weight1,
            2 => Weight2,
            _ => Weight3
        };

        void SetIndex(int slot, int value)
        {
            switch (slot)
            {
                case 0: BoneIndex0 = value; break;
                case 1: BoneIndex1 = value; break;
                case 2: BoneIndex2 = value; break;
                default: BoneIndex3 = value; break;
            }
        }

        void SetSlotWeight(int slot, float value)
        {
            switch (slot)
            {
                case 0: Weight0 = value; break;
                case 1: Weight1 = value; break;
                case 2: Weight2 = value; break;
                default: Weight3 = value; break;
            }
        }

        void SortDescending()
        {
            for (int pass = 0; pass < 3; ++pass)
            {
                for (int i = 0; i < 3 - pass; ++i)
                    if (GetSlotWeight(i) < GetSlotWeight(i + 1))
                        Swap(i, i + 1);
            }
        }

        void Swap(int a, int b)
        {
            int index = GetIndex(a);
            float weight = GetSlotWeight(a);
            SetIndex(a, GetIndex(b));
            SetSlotWeight(a, GetSlotWeight(b));
            SetIndex(b, index);
            SetSlotWeight(b, weight);
        }
    }

    /// <summary>
    /// Persistent Gaussian armature binding. Binary serialization keeps large weight arrays
    /// compact and makes Undo/asset saves practical for production splats.
    /// </summary>
    [PreferBinarySerialization]
    [CreateAssetMenu(fileName = "Gaussian Rig", menuName = "Gaussian Splat/Rig Asset")]
    public sealed class GsplatRigAsset : ScriptableObject
    {
        [SerializeField] GsplatAsset m_splatAsset;
        [SerializeField, HideInInspector] string[] m_bonePaths = Array.Empty<string>();
        [SerializeField, HideInInspector] string[] m_boneNames = Array.Empty<string>();
        [SerializeField, HideInInspector] Matrix4x4[] m_bindTransforms = Array.Empty<Matrix4x4>();
        [SerializeField, HideInInspector] GsplatRigWeight[] m_weights = Array.Empty<GsplatRigWeight>();

        public GsplatAsset SplatAsset => m_splatAsset;
        public int SplatCount => m_weights?.Length ?? 0;
        public int BoneCount => m_bonePaths?.Length ?? 0;
        public IReadOnlyList<string> BonePaths => m_bonePaths;
        public IReadOnlyList<string> BoneNames => m_boneNames;
        public IReadOnlyList<Matrix4x4> BindTransforms => m_bindTransforms;

        public bool IsValidFor(GsplatAsset asset, out string error)
        {
            if (!asset || m_splatAsset != asset)
                error = "The rig was created for a different Gaussian asset.";
            else if (m_weights == null || m_weights.Length != asset.SplatCount)
                error = "The rig weight count does not match the Gaussian count.";
            else if (m_bonePaths == null || m_bindTransforms == null ||
                     m_bonePaths.Length == 0 || m_bonePaths.Length != m_bindTransforms.Length)
                error = "The rig bone palette is invalid.";
            else
            {
                error = null;
                return true;
            }
            return false;
        }

        public GsplatRigWeight GetWeight(int splatIndex) => m_weights[splatIndex];

        public void SetWeight(int splatIndex, GsplatRigWeight weight)
        {
            weight.Normalize();
            m_weights[splatIndex] = weight;
        }

        public float GetBoneWeight(int splatIndex, int boneIndex) =>
            m_weights[splatIndex].GetWeight(boneIndex);

        public void SetBoneWeight(int splatIndex, int boneIndex, float value,
            bool normalizeOthers = true)
        {
            GsplatRigWeight weight = m_weights[splatIndex];
            weight.SetWeight(boneIndex, value, normalizeOthers);
            if (!normalizeOthers)
                weight.Normalize();
            m_weights[splatIndex] = weight;
        }

        public void NormalizeAll()
        {
            for (int i = 0; i < m_weights.Length; ++i)
            {
                GsplatRigWeight weight = m_weights[i];
                weight.Normalize();
                m_weights[i] = weight;
            }
        }

        internal GsplatRigWeight[] CopyWeights() =>
            m_weights == null ? Array.Empty<GsplatRigWeight>() : (GsplatRigWeight[])m_weights.Clone();

        internal string[] CopyBonePaths() =>
            m_bonePaths == null ? Array.Empty<string>() : (string[])m_bonePaths.Clone();

        internal string[] CopyBoneNames() =>
            m_boneNames == null ? Array.Empty<string>() : (string[])m_boneNames.Clone();

        internal Matrix4x4[] CopyBindTransforms() => m_bindTransforms == null
            ? Array.Empty<Matrix4x4>()
            : (Matrix4x4[])m_bindTransforms.Clone();

        internal void SetData(GsplatAsset splatAsset, string[] bonePaths, string[] boneNames,
            Matrix4x4[] bindTransforms, GsplatRigWeight[] weights)
        {
            m_splatAsset = splatAsset;
            m_bonePaths = bonePaths ?? Array.Empty<string>();
            m_boneNames = boneNames ?? Array.Empty<string>();
            m_bindTransforms = bindTransforms ?? Array.Empty<Matrix4x4>();
            m_weights = weights ?? Array.Empty<GsplatRigWeight>();
            NormalizeAll();
        }
    }
}
