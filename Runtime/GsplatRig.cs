// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Gsplat
{
    /// <summary>
    /// Animates Gaussian centers and covariance from a manually authored Transform armature and
    /// per-Gaussian weights. No proxy mesh is required.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GsplatRenderer))]
    [AddComponentMenu("Gaussian Splat/Gaussian Rig")]
    public class GsplatRig : GsplatSkinningSource
    {
        [Tooltip("Root container for the manually created Gaussian bones.")]
        public Transform ArmatureRoot;

        [Tooltip("Persistent per-Gaussian bone groups, weights, and bind matrices.")]
        public GsplatRigAsset RigAsset;

        [Tooltip("Updates deformation while previewing bone animation outside Play mode.")]
        public bool AnimateInEditMode;

        [Tooltip("Additional local-space padding added to conservative animated bounds.")]
        [Min(0f)] public float BoundsPadding = 0.1f;

        GsplatRenderer m_renderer;
        GsplatAsset m_boundAsset;
        GsplatRigAsset m_boundRigAsset;
        Transform m_boundArmatureRoot;
        Transform[] m_bones;
        Matrix4x4[] m_bindTransforms;
        GsplatRigWeight[] m_weights;
        Matrix4x4[] m_boneMatrices;
        Vector4[] m_boneRows;
        Vector4[] m_identityReferenceRows;
        GraphicsBuffer m_weightBuffer;
        GraphicsBuffer m_referenceBuffer;
        GraphicsBuffer m_boneBuffer;
        Texture2D m_indexTexture;
        Texture2D m_weightTexture;
        Texture2D m_referenceTexture;
        Texture2D m_boneTexture;
        bool m_usesVertexTextures;
        bool m_initializationFailed;

        internal override GraphicsBuffer SkinWeightBuffer => m_weightBuffer;
        internal override GraphicsBuffer ReferenceInverseBuffer => m_referenceBuffer;
        internal override GraphicsBuffer BoneRowBuffer => m_boneBuffer;
        internal override Texture2D SkinIndexTexture => m_indexTexture;
        internal override Texture2D SkinWeightTexture => m_weightTexture;
        internal override Texture2D ReferenceInverseTexture => m_referenceTexture;
        internal override Texture2D BoneMatrixTexture => m_boneTexture;
        internal override bool UsesVertexTextures => m_usesVertexTextures;
        public bool IsReady => m_weights != null && m_boneMatrices != null &&
                               (m_usesVertexTextures
                                   ? m_indexTexture && m_weightTexture && m_referenceTexture && m_boneTexture
                                   : m_weightBuffer != null && m_referenceBuffer != null && m_boneBuffer != null);
        internal override bool IsActivelyAnimating => IsReady && isActiveAndEnabled &&
                                                      (Application.isPlaying || AnimateInEditMode);

        protected virtual void OnEnable()
        {
            m_renderer = GetComponent<GsplatRenderer>();
            if (Application.isPlaying || AnimateInEditMode)
                TryInitialize();
        }

        void Update()
        {
            if (!Application.isPlaying && AnimateInEditMode)
                RefreshAnimation();
            else if (Application.isPlaying && !IsReady)
                TryInitialize();
        }

        void LateUpdate()
        {
            if (Application.isPlaying)
                RefreshAnimation();
        }

        protected virtual void OnDisable()
        {
            ReleaseResources();
            m_renderer?.NotifySkinningChanged();
        }

        protected virtual void OnDestroy() => ReleaseResources();

        protected virtual void OnValidate()
        {
            BoundsPadding = Mathf.Max(0f, BoundsPadding);
            m_initializationFailed = false;
            m_renderer ??= GetComponent<GsplatRenderer>();
            if (IsReady && !BindingsMatch())
                ReleaseResources();
        }

        [ContextMenu("Reload Painted Gaussian Rig")]
        public void RebuildBindings()
        {
            m_initializationFailed = false;
            ReleaseResources();
            TryInitialize();
            RefreshAnimation();
        }

        void RefreshAnimation()
        {
            if (!TryInitialize())
                return;
            UpdateBoneMatrices();
            UploadBoneMatrices();
            m_renderer.NotifySkinningChanged();
        }

        bool TryInitialize()
        {
            m_renderer ??= GetComponent<GsplatRenderer>();
            if (IsReady)
            {
                if (BindingsMatch())
                    return true;
                ReleaseResources();
            }
            if (m_initializationFailed || !m_renderer || !m_renderer.GsplatAsset ||
                !ArmatureRoot || !RigAsset)
                return false;

            try
            {
                if (!RigAsset.IsValidFor(m_renderer.GsplatAsset, out string error))
                    throw new InvalidOperationException(error);
                string[] paths = RigAsset.CopyBonePaths();
                m_bindTransforms = RigAsset.CopyBindTransforms();
                m_weights = RigAsset.CopyWeights();
                m_bones = new Transform[paths.Length];
                for (int i = 1; i < paths.Length; ++i)
                {
                    m_bones[i] = ResolveBonePath(paths[i]);
                    if (!m_bones[i])
                        throw new InvalidOperationException(
                            $"Bone '{paths[i]}' cannot be found below Armature Root '{ArmatureRoot.name}'.");
                }

                m_boneMatrices = new Matrix4x4[m_bones.Length];
                UpdateBoneMatrices();
                CreateIdentityReferenceRows(m_weights.Length);
                m_usesVertexTextures = GsplatUtils.UseVertexDataTextures;
                if (m_usesVertexTextures)
                    CreateTextures();
                else
                    CreateBuffers();
                UploadBoneMatrices();
                m_boundAsset = m_renderer.GsplatAsset;
                m_boundRigAsset = RigAsset;
                m_boundArmatureRoot = ArmatureRoot;
                Debug.Log($"[GsplatRig] Loaded {m_weights.Length:N0} painted Gaussian weights and " +
                          $"{Mathf.Max(0, m_bones.Length - 1):N0} bones from '{RigAsset.name}'.", this);
                GsplatSorter.Instance.MarkGlobalBuffersDirty();
                return true;
            }
            catch (Exception exception)
            {
                ReleaseResources();
                m_initializationFailed = true;
                Debug.LogError($"[GsplatRig] {exception.Message}", this);
                Debug.LogException(exception, this);
                return false;
            }
        }

        void UpdateBoneMatrices()
        {
            Matrix4x4 worldToSplat = m_renderer.transform.worldToLocalMatrix;
            m_boneMatrices[0] = Matrix4x4.identity;
            for (int i = 1; i < m_boneMatrices.Length; ++i)
                m_boneMatrices[i] = m_bones[i]
                    ? worldToSplat * m_bones[i].localToWorldMatrix * m_bindTransforms[i]
                    : Matrix4x4.identity;
        }

        void CreateIdentityReferenceRows(int splatCount)
        {
            m_identityReferenceRows = new Vector4[checked(splatCount * 3)];
            for (int i = 0; i < splatCount; ++i)
            {
                int offset = i * 3;
                m_identityReferenceRows[offset] = new Vector4(1f, 0f, 0f, 0f);
                m_identityReferenceRows[offset + 1] = new Vector4(0f, 1f, 0f, 0f);
                m_identityReferenceRows[offset + 2] = new Vector4(0f, 0f, 1f, 0f);
            }
        }

        void CreateBuffers()
        {
            m_weightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                m_weights.Length, Marshal.SizeOf<GsplatRigWeight>()) { name = $"Gsplat.RigWeights.{name}" };
            m_referenceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                m_identityReferenceRows.Length, Marshal.SizeOf<Vector4>()) { name = $"Gsplat.RigReference.{name}" };
            m_boneBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                checked(m_bones.Length * 4), Marshal.SizeOf<Vector4>()) { name = $"Gsplat.RigBones.{name}" };
            m_weightBuffer.SetData(m_weights);
            m_referenceBuffer.SetData(m_identityReferenceRows);
            m_boneRows = new Vector4[checked(m_bones.Length * 4)];
        }

        void CreateTextures()
        {
            m_indexTexture = GsplatUtils.CreateVertexDataTexture(checked(m_weights.Length * 4),
                GraphicsFormat.R8G8B8A8_UNorm, $"Gsplat.RigIndices.{name}");
            m_weightTexture = GsplatUtils.CreateVertexDataTexture(m_weights.Length,
                GraphicsFormat.R32G32B32A32_SFloat, $"Gsplat.RigWeights.{name}");
            m_referenceTexture = GsplatUtils.CreateVertexDataTexture(
                m_identityReferenceRows.Length, GraphicsFormat.R32G32B32A32_SFloat,
                $"Gsplat.RigReference.{name}");
            m_boneTexture = GsplatUtils.CreateVertexDataTexture(checked(m_bones.Length * 4),
                GraphicsFormat.R32G32B32A32_SFloat, $"Gsplat.RigBones.{name}");

            var indices = new uint[checked(m_weights.Length * 4)];
            var weights = new Vector4[checked(m_weightTexture.width * m_weightTexture.height)];
            for (int i = 0; i < m_weights.Length; ++i)
            {
                GsplatRigWeight source = m_weights[i];
                int offset = i * 4;
                indices[offset] = (uint)Mathf.Max(0, source.BoneIndex0);
                indices[offset + 1] = (uint)Mathf.Max(0, source.BoneIndex1);
                indices[offset + 2] = (uint)Mathf.Max(0, source.BoneIndex2);
                indices[offset + 3] = (uint)Mathf.Max(0, source.BoneIndex3);
                weights[i] = new Vector4(source.Weight0, source.Weight1, source.Weight2, source.Weight3);
            }
            GsplatUtils.UploadVertexUIntTexture(m_indexTexture, indices);
            GsplatUtils.UploadVertexDataTexture(m_weightTexture, weights, weights.Length);
            GsplatUtils.UploadVertexDataTexture(m_referenceTexture, m_identityReferenceRows,
                m_identityReferenceRows.Length);
            m_boneRows = new Vector4[checked(m_boneTexture.width * m_boneTexture.height)];
        }

        void UploadBoneMatrices()
        {
            if (m_boneMatrices == null || m_boneRows == null)
                return;
            for (int i = 0; i < m_boneMatrices.Length; ++i)
            {
                int offset = i * 4;
                Matrix4x4 matrix = m_boneMatrices[i];
                m_boneRows[offset] = matrix.GetRow(0);
                m_boneRows[offset + 1] = matrix.GetRow(1);
                m_boneRows[offset + 2] = matrix.GetRow(2);
                m_boneRows[offset + 3] = matrix.GetRow(3);
            }
            if (m_usesVertexTextures)
                GsplatUtils.UploadVertexDataTexture(m_boneTexture, m_boneRows,
                    m_boneRows.Length, 1, false);
            else
                m_boneBuffer.SetData(m_boneRows);
        }

        public Transform ResolveRigBone(int boneIndex)
        {
            if (!RigAsset || boneIndex <= 0 || boneIndex >= RigAsset.BoneCount)
                return null;
            return ResolveBonePath(RigAsset.BonePaths[boneIndex]);
        }

        Transform ResolveBonePath(string path) => string.IsNullOrEmpty(path)
            ? null
            : ArmatureRoot.Find(path);

        internal override GsplatSkinningSnapshot CaptureCpuSnapshot() => IsActivelyAnimating
            ? new GsplatRigSkinningSnapshot(m_weights, m_identityReferenceRows,
                (Matrix4x4[])m_boneMatrices.Clone())
            : null;

        internal override Bounds CalculateLocalBounds(Bounds sourceBounds)
        {
            if (!IsActivelyAnimating || m_boneMatrices == null)
                return sourceBounds;
            Bounds result = sourceBounds;
            Vector3 min = sourceBounds.min;
            Vector3 max = sourceBounds.max;
            for (int bone = 1; bone < m_boneMatrices.Length; ++bone)
            for (int x = 0; x < 2; ++x)
            for (int y = 0; y < 2; ++y)
            for (int z = 0; z < 2; ++z)
                result.Encapsulate(m_boneMatrices[bone].MultiplyPoint3x4(new Vector3(
                    x == 0 ? min.x : max.x, y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z)));
            result.Expand(BoundsPadding * 2f);
            return result;
        }

        bool BindingsMatch() => m_boundAsset == m_renderer?.GsplatAsset &&
                                m_boundRigAsset == RigAsset &&
                                m_boundArmatureRoot == ArmatureRoot;

        void ReleaseResources()
        {
            m_weightBuffer?.Dispose(); m_weightBuffer = null;
            m_referenceBuffer?.Dispose(); m_referenceBuffer = null;
            m_boneBuffer?.Dispose(); m_boneBuffer = null;
            GsplatUtils.DestroyRuntimeTexture(m_indexTexture); m_indexTexture = null;
            GsplatUtils.DestroyRuntimeTexture(m_weightTexture); m_weightTexture = null;
            GsplatUtils.DestroyRuntimeTexture(m_referenceTexture); m_referenceTexture = null;
            GsplatUtils.DestroyRuntimeTexture(m_boneTexture); m_boneTexture = null;
            m_weights = null;
            m_bones = null;
            m_bindTransforms = null;
            m_boneMatrices = null;
            m_boneRows = null;
            m_identityReferenceRows = null;
            m_boundAsset = null;
            m_boundRigAsset = null;
            m_boundArmatureRoot = null;
            m_usesVertexTextures = false;
            GsplatSorter.Instance.MarkGlobalBuffersDirty();
        }

        internal static Vector3[] GetSplatCenters(GsplatAsset asset)
        {
            if (asset is GsplatAssetUncompressed uncompressed)
                return uncompressed.Positions ?? Array.Empty<Vector3>();
            if (asset is not GsplatAssetSpark spark || spark.PackedSplats == null)
                throw new NotSupportedException(
                    $"Gaussian rigging does not support asset type '{asset.GetType().Name}'.");
            var centers = new Vector3[spark.PackedSplats.Length];
            for (int i = 0; i < centers.Length; ++i)
            {
                var packed = spark.PackedSplats[i];
                centers[i] = new Vector3(HalfToFloat(packed.y & 0xffffu),
                    HalfToFloat((packed.y >> 16) & 0xffffu), HalfToFloat(packed.z & 0xffffu));
            }
            return centers;
        }

        static float HalfToFloat(uint value)
        {
            uint sign = (value & 0x8000u) << 16;
            uint exponent = value >> 10 & 0x1fu;
            uint mantissa = value & 0x3ffu;
            uint bits;
            if (exponent == 0)
            {
                if (mantissa == 0) bits = sign;
                else
                {
                    int unbiasedExponent = -14;
                    while ((mantissa & 0x400u) == 0) { mantissa <<= 1; unbiasedExponent--; }
                    mantissa &= 0x3ffu;
                    bits = sign | (uint)(unbiasedExponent + 127) << 23 | mantissa << 13;
                }
            }
            else if (exponent == 0x1fu) bits = sign | 0x7f800000u | mantissa << 13;
            else bits = sign | (exponent + 112u) << 23 | mantissa << 13;
            return BitConverter.Int32BitsToSingle((int)bits);
        }
    }

    internal sealed class GsplatRigSkinningSnapshot : GsplatSkinningSnapshot
    {
        readonly GsplatRigWeight[] m_weights;
        readonly Vector4[] m_referenceRows;
        readonly Matrix4x4[] m_boneMatrices;

        public GsplatRigSkinningSnapshot(GsplatRigWeight[] weights, Vector4[] referenceRows,
            Matrix4x4[] boneMatrices)
        {
            m_weights = weights;
            m_referenceRows = referenceRows;
            m_boneMatrices = boneMatrices;
        }

        public override Vector3 TransformPoint(uint sourceId, Vector3 point)
        {
            if (sourceId >= m_weights.Length)
                return point;
            int offset = checked((int)sourceId * 3);
            Matrix4x4 reference = Matrix4x4.identity;
            reference.SetRow(0, m_referenceRows[offset]);
            reference.SetRow(1, m_referenceRows[offset + 1]);
            reference.SetRow(2, m_referenceRows[offset + 2]);
            return (m_weights[sourceId].BlendMatrix(m_boneMatrices) * reference)
                .MultiplyPoint3x4(point);
        }
    }
}
