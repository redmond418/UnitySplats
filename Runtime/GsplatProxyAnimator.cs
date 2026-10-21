// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Gsplat
{
    /// <summary>
    /// Deforms a Gaussian splat with the bones and skin weights of an aligned proxy hierarchy.
    /// Bindings are generated from the closest bind-pose triangle across all child proxy meshes,
    /// with barycentrically interpolated skin weights. They are kept only at runtime; the
    /// imported Gaussian asset is never modified.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GsplatRenderer))]
    [AddComponentMenu("Gaussian Splat/Proxy Mesh Animator")]
    public sealed class GsplatProxyAnimator : GsplatSkinningSource
    {
        [Tooltip("Optional primary skinned renderer. Proxy Root is preferred when the proxy contains multiple child meshes.")]
        public SkinnedMeshRenderer ProxyRenderer;

        [Tooltip("Root containing every proxy mesh. Leave empty to use the first child Animator hierarchy, or the assigned Proxy Renderer hierarchy.")]
        public Transform ProxyRoot;

        [Tooltip("Updates the Gaussian while previewing animation outside Play mode.")]
        public bool AnimateInEditMode;

        [Tooltip("Hides every Renderer under Proxy Root while retaining the hierarchy as the Gaussian deformation driver.")]
        public bool HideProxyRenderer = true;

        [Tooltip("Extra local-space bounds around the union of the original splat and animated proxy bounds.")]
        [Min(0f)] public float BoundsPadding = 0.1f;

        [Tooltip("In the Editor, compares this component's skinning against Unity's baked proxy mesh once shortly after animation starts.")]
        public bool ValidateSkinningAgainstProxy = true;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SkinWeightData
        {
            public uint BoneIndex0;
            public uint BoneIndex1;
            public uint BoneIndex2;
            public uint BoneIndex3;
            public float Weight0;
            public float Weight1;
            public float Weight2;
            public float Weight3;

            public SkinWeightData(BoneWeight source, int boneCount) : this(source, boneCount, 0)
            {
            }

            public SkinWeightData(BoneWeight source, int boneCount, int boneOffset)
            {
                bool valid0 = IsValidBoneIndex(source.boneIndex0, boneCount);
                bool valid1 = IsValidBoneIndex(source.boneIndex1, boneCount);
                bool valid2 = IsValidBoneIndex(source.boneIndex2, boneCount);
                bool valid3 = IsValidBoneIndex(source.boneIndex3, boneCount);
                BoneIndex0 = valid0 ? checked((uint)(source.boneIndex0 + boneOffset)) : 0u;
                BoneIndex1 = valid1 ? checked((uint)(source.boneIndex1 + boneOffset)) : 0u;
                BoneIndex2 = valid2 ? checked((uint)(source.boneIndex2 + boneOffset)) : 0u;
                BoneIndex3 = valid3 ? checked((uint)(source.boneIndex3 + boneOffset)) : 0u;
                Weight0 = valid0 ? Mathf.Max(0f, source.weight0) : 0f;
                Weight1 = valid1 ? Mathf.Max(0f, source.weight1) : 0f;
                Weight2 = valid2 ? Mathf.Max(0f, source.weight2) : 0f;
                Weight3 = valid3 ? Mathf.Max(0f, source.weight3) : 0f;

                float total = Weight0 + Weight1 + Weight2 + Weight3;
                if (total <= 1e-8f)
                {
                    BoneIndex0 = BoneIndex1 = BoneIndex2 = BoneIndex3 = 0;
                    Weight0 = 1f;
                    Weight1 = Weight2 = Weight3 = 0f;
                    return;
                }

                float inverseTotal = 1f / total;
                Weight0 *= inverseTotal;
                Weight1 *= inverseTotal;
                Weight2 *= inverseTotal;
                Weight3 *= inverseTotal;
            }

            public static SkinWeightData Rigid(int boneIndex) => new()
            {
                BoneIndex0 = checked((uint)boneIndex),
                BoneIndex1 = checked((uint)boneIndex),
                BoneIndex2 = checked((uint)boneIndex),
                BoneIndex3 = checked((uint)boneIndex),
                Weight0 = 1f,
                Weight1 = 0f,
                Weight2 = 0f,
                Weight3 = 0f
            };

            public static SkinWeightData Interpolate(SkinWeightData a, SkinWeightData b,
                SkinWeightData c, Vector3 barycentric, uint[] scratchIndices,
                float[] scratchWeights)
            {
                int influenceCount = 0;
                AddInfluences(a, Mathf.Max(0f, barycentric.x), scratchIndices, scratchWeights,
                    ref influenceCount);
                AddInfluences(b, Mathf.Max(0f, barycentric.y), scratchIndices, scratchWeights,
                    ref influenceCount);
                AddInfluences(c, Mathf.Max(0f, barycentric.z), scratchIndices, scratchWeights,
                    ref influenceCount);

                var result = new SkinWeightData();
                SelectStrongest(ref result.BoneIndex0, ref result.Weight0, scratchIndices,
                    scratchWeights, influenceCount);
                SelectStrongest(ref result.BoneIndex1, ref result.Weight1, scratchIndices,
                    scratchWeights, influenceCount);
                SelectStrongest(ref result.BoneIndex2, ref result.Weight2, scratchIndices,
                    scratchWeights, influenceCount);
                SelectStrongest(ref result.BoneIndex3, ref result.Weight3, scratchIndices,
                    scratchWeights, influenceCount);

                float total = result.Weight0 + result.Weight1 + result.Weight2 + result.Weight3;
                if (total <= 1e-8f)
                    return Rigid(0);
                float inverseTotal = 1f / total;
                result.Weight0 *= inverseTotal;
                result.Weight1 *= inverseTotal;
                result.Weight2 *= inverseTotal;
                result.Weight3 *= inverseTotal;
                return result;
            }

            static void AddInfluences(SkinWeightData source, float barycentricWeight,
                uint[] indices, float[] weights, ref int count)
            {
                AddInfluence(source.BoneIndex0, source.Weight0 * barycentricWeight,
                    indices, weights, ref count);
                AddInfluence(source.BoneIndex1, source.Weight1 * barycentricWeight,
                    indices, weights, ref count);
                AddInfluence(source.BoneIndex2, source.Weight2 * barycentricWeight,
                    indices, weights, ref count);
                AddInfluence(source.BoneIndex3, source.Weight3 * barycentricWeight,
                    indices, weights, ref count);
            }

            static void AddInfluence(uint boneIndex, float weight, uint[] indices,
                float[] weights, ref int count)
            {
                if (weight <= 1e-8f)
                    return;
                for (int i = 0; i < count; ++i)
                {
                    if (indices[i] != boneIndex)
                        continue;
                    weights[i] += weight;
                    return;
                }
                indices[count] = boneIndex;
                weights[count] = weight;
                count++;
            }

            static void SelectStrongest(ref uint boneIndex, ref float weight, uint[] indices,
                float[] weights, int count)
            {
                int strongest = -1;
                float strongestWeight = -1f;
                for (int i = 0; i < count; ++i)
                {
                    if (weights[i] <= strongestWeight)
                        continue;
                    strongest = i;
                    strongestWeight = weights[i];
                }
                if (strongest < 0)
                {
                    boneIndex = 0;
                    weight = 0f;
                    return;
                }
                boneIndex = indices[strongest];
                weight = strongestWeight;
                weights[strongest] = -1f;
            }

            static bool IsValidBoneIndex(int index, int boneCount) =>
                index >= 0 && index < boneCount;

            public Vector3 TransformPoint(Vector3 point, Matrix4x4[] boneMatrices)
            {
                return BlendMatrix(boneMatrices).MultiplyPoint3x4(point);
            }

            public Matrix4x4 BlendMatrix(Matrix4x4[] boneMatrices)
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
        }

        readonly struct ProxyTriangle
        {
            public readonly int A;
            public readonly int B;
            public readonly int C;

            public ProxyTriangle(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }
        }

        sealed class TriangleBvh
        {
            const int k_leafSize = 8;

            struct Node
            {
                public Vector3 Min;
                public Vector3 Max;
                public int Left;
                public int Right;
                public int Start;
                public int Count;
            }

            readonly Vector3[] m_vertices;
            readonly ProxyTriangle[] m_triangles;
            readonly int[] m_order;
            readonly Node[] m_nodes;
            int m_nodeCount;
            readonly int m_root;

            public TriangleBvh(Vector3[] vertices, ProxyTriangle[] triangles)
            {
                m_vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
                m_triangles = triangles ?? throw new ArgumentNullException(nameof(triangles));
                if (triangles.Length == 0)
                    throw new ArgumentException("The proxy hierarchy contains no triangles.",
                        nameof(triangles));
                m_order = new int[triangles.Length];
                m_nodes = new Node[checked(triangles.Length * 2)];
                for (int i = 0; i < m_order.Length; ++i)
                    m_order[i] = i;
                m_root = Build(0, triangles.Length);
            }

            int Build(int start, int count)
            {
                Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity,
                    float.PositiveInfinity);
                Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity,
                    float.NegativeInfinity);
                Vector3 centroidMin = min;
                Vector3 centroidMax = max;
                for (int i = start; i < start + count; ++i)
                {
                    ProxyTriangle triangle = m_triangles[m_order[i]];
                    Vector3 a = m_vertices[triangle.A];
                    Vector3 b = m_vertices[triangle.B];
                    Vector3 c = m_vertices[triangle.C];
                    min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
                    max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
                    Vector3 centroid = (a + b + c) / 3f;
                    centroidMin = Vector3.Min(centroidMin, centroid);
                    centroidMax = Vector3.Max(centroidMax, centroid);
                }

                int nodeIndex = m_nodeCount++;
                if (count <= k_leafSize)
                {
                    m_nodes[nodeIndex] = new Node
                    {
                        Min = min, Max = max, Left = -1, Right = -1,
                        Start = start, Count = count
                    };
                    return nodeIndex;
                }

                Vector3 extent = centroidMax - centroidMin;
                int axis = extent.x >= extent.y && extent.x >= extent.z
                    ? 0
                    : extent.y >= extent.z ? 1 : 2;
                int leftCount = count / 2;
                int median = start + leftCount;
                if (extent[axis] > 1e-10f)
                    Select(start, start + count - 1, median, axis);
                int left = Build(start, leftCount);
                int right = Build(median, count - leftCount);
                m_nodes[nodeIndex] = new Node
                {
                    Min = min, Max = max, Left = left, Right = right,
                    Start = 0, Count = 0
                };
                return nodeIndex;
            }

            void Select(int left, int right, int target, int axis)
            {
                while (left < right)
                {
                    int pivot = Partition(left, right, (left + right) >> 1, axis);
                    if (pivot == target)
                        return;
                    if (target < pivot)
                        right = pivot - 1;
                    else
                        left = pivot + 1;
                }
            }

            int Partition(int left, int right, int pivot, int axis)
            {
                float pivotValue = CentroidAxis(m_order[pivot], axis);
                Swap(pivot, right);
                int store = left;
                for (int i = left; i < right; ++i)
                {
                    if (CentroidAxis(m_order[i], axis) >= pivotValue)
                        continue;
                    Swap(store, i);
                    store++;
                }
                Swap(store, right);
                return store;
            }

            float CentroidAxis(int triangleIndex, int axis)
            {
                ProxyTriangle triangle = m_triangles[triangleIndex];
                return (m_vertices[triangle.A][axis] + m_vertices[triangle.B][axis] +
                        m_vertices[triangle.C][axis]) / 3f;
            }

            void Swap(int a, int b) =>
                (m_order[a], m_order[b]) = (m_order[b], m_order[a]);

            public ProxyTriangle FindNearest(Vector3 point, out Vector3 barycentric)
            {
                int nearestTriangle = -1;
                float nearestDistance = float.PositiveInfinity;
                barycentric = new Vector3(1f, 0f, 0f);
                Search(m_root, point, ref nearestTriangle, ref nearestDistance,
                    ref barycentric);
                if (nearestTriangle < 0)
                    throw new InvalidOperationException("Could not find a proxy surface triangle.");
                return m_triangles[nearestTriangle];
            }

            void Search(int nodeIndex, Vector3 point, ref int nearestTriangle,
                ref float nearestDistance, ref Vector3 nearestBarycentric)
            {
                Node node = m_nodes[nodeIndex];
                if (DistanceToBoundsSquared(point, node.Min, node.Max) > nearestDistance)
                    return;

                if (node.Count > 0)
                {
                    for (int i = node.Start; i < node.Start + node.Count; ++i)
                    {
                        int triangleIndex = m_order[i];
                        ProxyTriangle triangle = m_triangles[triangleIndex];
                        float distance = ClosestBarycentric(point, m_vertices[triangle.A],
                            m_vertices[triangle.B], m_vertices[triangle.C],
                            out Vector3 candidateBarycentric);
                        if (distance >= nearestDistance)
                            continue;
                        nearestDistance = distance;
                        nearestTriangle = triangleIndex;
                        nearestBarycentric = candidateBarycentric;
                    }
                    return;
                }

                Node left = m_nodes[node.Left];
                Node right = m_nodes[node.Right];
                float leftDistance = DistanceToBoundsSquared(point, left.Min, left.Max);
                float rightDistance = DistanceToBoundsSquared(point, right.Min, right.Max);
                if (leftDistance <= rightDistance)
                {
                    if (leftDistance <= nearestDistance)
                        Search(node.Left, point, ref nearestTriangle, ref nearestDistance,
                            ref nearestBarycentric);
                    if (rightDistance <= nearestDistance)
                        Search(node.Right, point, ref nearestTriangle, ref nearestDistance,
                            ref nearestBarycentric);
                }
                else
                {
                    if (rightDistance <= nearestDistance)
                        Search(node.Right, point, ref nearestTriangle, ref nearestDistance,
                            ref nearestBarycentric);
                    if (leftDistance <= nearestDistance)
                        Search(node.Left, point, ref nearestTriangle, ref nearestDistance,
                            ref nearestBarycentric);
                }
            }

            static float DistanceToBoundsSquared(Vector3 point, Vector3 min, Vector3 max)
            {
                float dx = point.x < min.x ? min.x - point.x : point.x > max.x ? point.x - max.x : 0f;
                float dy = point.y < min.y ? min.y - point.y : point.y > max.y ? point.y - max.y : 0f;
                float dz = point.z < min.z ? min.z - point.z : point.z > max.z ? point.z - max.z : 0f;
                return dx * dx + dy * dy + dz * dz;
            }

            static float ClosestBarycentric(Vector3 point, Vector3 a, Vector3 b, Vector3 c,
                out Vector3 barycentric)
            {
                Vector3 ab = b - a;
                Vector3 ac = c - a;
                if (Vector3.Cross(ab, ac).sqrMagnitude <= 1e-16f)
                    return ClosestDegenerate(point, a, b, c, out barycentric);

                Vector3 ap = point - a;
                float d1 = Vector3.Dot(ab, ap);
                float d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f)
                {
                    barycentric = new Vector3(1f, 0f, 0f);
                    return ap.sqrMagnitude;
                }

                Vector3 bp = point - b;
                float d3 = Vector3.Dot(ab, bp);
                float d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0f && d4 <= d3)
                {
                    barycentric = new Vector3(0f, 1f, 0f);
                    return bp.sqrMagnitude;
                }

                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f)
                {
                    float v = d1 / (d1 - d3);
                    barycentric = new Vector3(1f - v, v, 0f);
                    return (point - (a + v * ab)).sqrMagnitude;
                }

                Vector3 cp = point - c;
                float d5 = Vector3.Dot(ab, cp);
                float d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0f && d5 <= d6)
                {
                    barycentric = new Vector3(0f, 0f, 1f);
                    return cp.sqrMagnitude;
                }

                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f)
                {
                    float w = d2 / (d2 - d6);
                    barycentric = new Vector3(1f - w, 0f, w);
                    return (point - (a + w * ac)).sqrMagnitude;
                }

                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
                {
                    float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                    barycentric = new Vector3(0f, 1f - w, w);
                    return (point - (b + w * (c - b))).sqrMagnitude;
                }

                float inverseDenominator = 1f / (va + vb + vc);
                float faceV = vb * inverseDenominator;
                float faceW = vc * inverseDenominator;
                barycentric = new Vector3(1f - faceV - faceW, faceV, faceW);
                Vector3 closest = a * barycentric.x + b * barycentric.y + c * barycentric.z;
                return (point - closest).sqrMagnitude;
            }

            static float ClosestDegenerate(Vector3 point, Vector3 a, Vector3 b, Vector3 c,
                out Vector3 barycentric)
            {
                float ab = ClosestSegment(point, a, b, out float tAb);
                float bc = ClosestSegment(point, b, c, out float tBc);
                float ca = ClosestSegment(point, c, a, out float tCa);
                if (ab <= bc && ab <= ca)
                {
                    barycentric = new Vector3(1f - tAb, tAb, 0f);
                    return ab;
                }
                if (bc <= ca)
                {
                    barycentric = new Vector3(0f, 1f - tBc, tBc);
                    return bc;
                }
                barycentric = new Vector3(tCa, 0f, 1f - tCa);
                return ca;
            }

            static float ClosestSegment(Vector3 point, Vector3 a, Vector3 b, out float t)
            {
                Vector3 segment = b - a;
                float denominator = segment.sqrMagnitude;
                t = denominator > 1e-16f
                    ? Mathf.Clamp01(Vector3.Dot(point - a, segment) / denominator)
                    : 0f;
                return (point - (a + t * segment)).sqrMagnitude;
            }
        }

        GsplatRenderer m_splatRenderer;
        GsplatAsset m_boundAsset;
        Transform m_boundProxyRoot;
        Transform m_resolvedProxyRoot;
        Mesh[] m_boundMeshes;
        Renderer[] m_proxyMeshRenderers = Array.Empty<Renderer>();
        Transform[] m_paletteBones;
        Matrix4x4[] m_paletteBindTransforms;
        SkinWeightData[] m_skinWeights;
        Vector4[] m_referenceInverseRows;
        Matrix4x4[] m_boneMatrices;
        GraphicsBuffer m_skinWeightBuffer;
        GraphicsBuffer m_referenceInverseBuffer;
        GraphicsBuffer m_boneMatrixBuffer;
        Texture2D m_skinIndexTexture;
        Texture2D m_skinWeightTexture;
        Texture2D m_referenceInverseTexture;
        Texture2D m_boneMatrixTexture;
        Vector4[] m_boneMatrixRows;
        bool m_usesVertexTextures;
        bool m_initializationFailed;
        Renderer[] m_hiddenRenderers = Array.Empty<Renderer>();
        bool[] m_previousForceRenderingOff = Array.Empty<bool>();
        readonly List<SkinningValidationGroup> m_validationGroups = new();
        int m_validationFramesRemaining;

        const int k_validationDelayFrames = 10;
        const int k_validationSamplesPerRenderer = 64;

        sealed class SkinningValidationGroup
        {
            public Renderer Renderer;
            public int[] VertexIndices;
            public Vector3[] SourcePositions;
            public Vector3[] ReferencePositions;
            public SkinWeightData[] SkinWeights;
            public Matrix4x4[] ReferenceInverses;
        }

        internal override GraphicsBuffer SkinWeightBuffer => m_skinWeightBuffer;
        internal override GraphicsBuffer ReferenceInverseBuffer => m_referenceInverseBuffer;
        internal override GraphicsBuffer BoneRowBuffer => m_boneMatrixBuffer;
        internal override Texture2D SkinIndexTexture => m_skinIndexTexture;
        internal override Texture2D SkinWeightTexture => m_skinWeightTexture;
        internal override Texture2D ReferenceInverseTexture => m_referenceInverseTexture;
        internal override Texture2D BoneMatrixTexture => m_boneMatrixTexture;
        internal override bool UsesVertexTextures => m_usesVertexTextures;
        public bool IsReady => m_skinWeights != null && m_referenceInverseRows != null &&
                               m_boneMatrices != null &&
                               (m_usesVertexTextures
                                   ? m_skinIndexTexture && m_skinWeightTexture &&
                                     m_referenceInverseTexture && m_boneMatrixTexture
                                   : m_skinWeightBuffer != null &&
                                     m_referenceInverseBuffer != null &&
                                     m_boneMatrixBuffer != null);
        internal override bool IsActivelyAnimating => IsReady && isActiveAndEnabled &&
                                                      (Application.isPlaying || AnimateInEditMode);

        void OnEnable()
        {
            m_splatRenderer = GetComponent<GsplatRenderer>();
            ResolveProxyHierarchy();
            ApplyProxyVisibility();
            if (Application.isPlaying)
                TryInitialize();
        }

        void Update()
        {
            // Generate deterministic bind-pose bindings before Unity's animation update. Binding
            // must not depend on whichever animation-preview pose happens to be active here.
            if (Application.isPlaying)
            {
                if (!IsReady)
                    TryInitialize();
            }
            else if (AnimateInEditMode)
                RefreshAnimation();
        }

        void LateUpdate()
        {
            if (Application.isPlaying)
                RefreshAnimation();
        }

        void OnDisable()
        {
            RestoreProxyVisibility();
            ReleaseResources();
            m_splatRenderer?.NotifySkinningChanged();
        }

        void OnDestroy()
        {
            RestoreProxyVisibility();
            ReleaseResources();
        }

        void OnValidate()
        {
            BoundsPadding = Mathf.Max(0f, BoundsPadding);
            m_initializationFailed = false;
            m_splatRenderer ??= GetComponent<GsplatRenderer>();
            ResolveProxyHierarchy(true);
            ApplyProxyVisibility();
            if (IsReady && !BindingsMatchCurrentHierarchy())
                ReleaseResources();
        }

        [ContextMenu("Rebuild Gaussian Skin Bindings")]
        public void RebuildBindings()
        {
            m_initializationFailed = false;
            ReleaseResources();
            ResolveProxyHierarchy(true);
            ApplyProxyVisibility();
            TryInitialize();
            RefreshAnimation();
        }

        void RefreshAnimation()
        {
            if (!TryInitialize())
                return;
            UpdateBoneMatrices();
            UploadBoneMatrices();
#if UNITY_EDITOR
            ValidateSkinningWhenReady();
#endif
            m_splatRenderer.NotifySkinningChanged();
        }

        bool TryInitialize()
        {
            m_splatRenderer ??= GetComponent<GsplatRenderer>();
            ResolveProxyHierarchy();
            if (IsReady)
            {
                if (BindingsMatchCurrentHierarchy())
                    return true;
                ReleaseResources();
            }
            if (m_initializationFailed)
                return false;

            if (!m_splatRenderer || !m_splatRenderer.GsplatAsset || !ProxyRoot ||
                m_proxyMeshRenderers.Length == 0)
                return false;

            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                Vector3[] splatCenters = GetSplatCenters(m_splatRenderer.GsplatAsset);
                if (splatCenters.Length == 0)
                    throw new InvalidOperationException("The Gaussian asset contains no splats to bind.");

                var proxyVertices = new List<Vector3>();
                var proxyWeights = new List<SkinWeightData>();
                var proxyTriangles = new List<ProxyTriangle>();
                var paletteBones = new List<Transform> { null };
                var paletteBindTransforms = new List<Matrix4x4> { Matrix4x4.identity };
                var validationGroups = new List<SkinningValidationGroup>();
                var boundMeshes = new Mesh[m_proxyMeshRenderers.Length];
                Matrix4x4 splatToWorld = m_splatRenderer.transform.localToWorldMatrix;
                Matrix4x4 worldToSplat = m_splatRenderer.transform.worldToLocalMatrix;

                for (int rendererIndex = 0; rendererIndex < m_proxyMeshRenderers.Length; ++rendererIndex)
                {
                    Renderer proxy = m_proxyMeshRenderers[rendererIndex];
                    Mesh mesh = GetProxyMesh(proxy);
                    if (!mesh)
                        throw new InvalidOperationException($"Proxy renderer '{proxy.name}' has no mesh.");
                    if (!mesh.isReadable)
                        throw new InvalidOperationException(
                            $"Enable Read/Write on proxy mesh '{mesh.name}' so Gaussian skin bindings can be generated.");

                    boundMeshes[rendererIndex] = mesh;
                    Vector3[] meshVertices = mesh.vertices;
                    if (meshVertices.Length == 0)
                        continue;

                    var skinnedProxy = proxy as SkinnedMeshRenderer;
                    Transform[] bones = skinnedProxy ? skinnedProxy.bones : null;
                    Matrix4x4[] bindPoses = mesh.bindposes;
                    BoneWeight[] vertexWeights = mesh.boneWeights;
                    bool rigid = !skinnedProxy || bones == null || bones.Length == 0;
                    if (!rigid && (bindPoses.Length != bones.Length ||
                                   vertexWeights.Length != meshVertices.Length))
                        throw new InvalidOperationException(
                            $"Proxy mesh '{mesh.name}' must provide matching bind poses, bones, and four-bone vertex weights.");

                    Matrix4x4 splatToMesh = proxy.transform.worldToLocalMatrix * splatToWorld;
                    Matrix4x4 meshToSplat = worldToSplat * proxy.transform.localToWorldMatrix;
                    int paletteOffset = paletteBones.Count;
                    if (rigid)
                    {
                        paletteBones.Add(proxy.transform);
                        paletteBindTransforms.Add(splatToMesh);
                    }
                    else
                    {
                        for (int boneIndex = 0; boneIndex < bones.Length; ++boneIndex)
                        {
                            paletteBones.Add(bones[boneIndex]);
                            paletteBindTransforms.Add(bindPoses[boneIndex] * splatToMesh);
                        }
                    }

                    int vertexOffset = proxyVertices.Count;
                    for (int vertexIndex = 0; vertexIndex < meshVertices.Length; ++vertexIndex)
                    {
                        proxyVertices.Add(meshToSplat.MultiplyPoint3x4(meshVertices[vertexIndex]));
                        proxyWeights.Add(rigid
                            ? SkinWeightData.Rigid(paletteOffset)
                            : new SkinWeightData(vertexWeights[vertexIndex], bones.Length,
                                paletteOffset));
                    }

                    if (ValidateSkinningAgainstProxy)
                    {
                        int sampleCount = Mathf.Min(k_validationSamplesPerRenderer,
                            meshVertices.Length);
                        var sampleIndices = new int[sampleCount];
                        var sampleSources = new Vector3[sampleCount];
                        var sampleReferences = new Vector3[sampleCount];
                        var sampleWeights = new SkinWeightData[sampleCount];
                        for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex)
                        {
                            int vertexIndex = sampleCount == 1
                                ? 0
                                : Mathf.RoundToInt(sampleIndex * (meshVertices.Length - 1f) /
                                                   (sampleCount - 1f));
                            sampleIndices[sampleIndex] = vertexIndex;
                            sampleSources[sampleIndex] = meshToSplat.MultiplyPoint3x4(
                                meshVertices[vertexIndex]);
                            sampleReferences[sampleIndex] = sampleSources[sampleIndex];
                            sampleWeights[sampleIndex] = proxyWeights[vertexOffset + vertexIndex];
                        }
                        validationGroups.Add(new SkinningValidationGroup
                        {
                            Renderer = proxy,
                            VertexIndices = sampleIndices,
                            SourcePositions = sampleSources,
                            ReferencePositions = sampleReferences,
                            SkinWeights = sampleWeights
                        });
                    }

                    int[] meshTriangles = mesh.triangles;
                    if (meshTriangles.Length % 3 != 0)
                        throw new InvalidOperationException(
                            $"Proxy mesh '{mesh.name}' contains an invalid triangle index buffer.");
                    for (int triangleIndex = 0; triangleIndex < meshTriangles.Length;
                         triangleIndex += 3)
                    {
                        int a = meshTriangles[triangleIndex];
                        int b = meshTriangles[triangleIndex + 1];
                        int c = meshTriangles[triangleIndex + 2];
                        if ((uint)a >= (uint)meshVertices.Length ||
                            (uint)b >= (uint)meshVertices.Length ||
                            (uint)c >= (uint)meshVertices.Length)
                            throw new InvalidOperationException(
                                $"Proxy mesh '{mesh.name}' contains an out-of-range triangle index.");
                        proxyTriangles.Add(new ProxyTriangle(vertexOffset + a,
                            vertexOffset + b, vertexOffset + c));
                    }
                }

                if (proxyVertices.Count == 0)
                    throw new InvalidOperationException("The proxy hierarchy contains no bindable mesh vertices.");
                if (proxyTriangles.Count == 0)
                    throw new InvalidOperationException("The proxy hierarchy contains no bindable triangles.");
                Vector3[] vertices = proxyVertices.ToArray();
                SkinWeightData[] vertexSkinWeights = proxyWeights.ToArray();
                ProxyTriangle[] triangles = proxyTriangles.ToArray();
                var surface = new TriangleBvh(vertices, triangles);
                var bindings = new SkinWeightData[splatCenters.Length];
                var scratchIndices = new uint[12];
                var scratchWeights = new float[12];
                for (int i = 0; i < splatCenters.Length; ++i)
                {
                    ProxyTriangle triangle = surface.FindNearest(splatCenters[i],
                        out Vector3 barycentric);
                    bindings[i] = SkinWeightData.Interpolate(
                        vertexSkinWeights[triangle.A], vertexSkinWeights[triangle.B],
                        vertexSkinWeights[triangle.C], barycentric, scratchIndices,
                        scratchWeights);
                }

                m_skinWeights = bindings;
                m_paletteBones = paletteBones.ToArray();
                m_paletteBindTransforms = paletteBindTransforms.ToArray();
                m_boneMatrices = new Matrix4x4[m_paletteBones.Length];
                m_boundAsset = m_splatRenderer.GsplatAsset;
                m_boundProxyRoot = ProxyRoot;
                m_boundMeshes = boundMeshes;
                UpdateAbsoluteBoneMatrices();
                CreateReferenceInverseRows(bindings);
                PrepareSkinningValidation(validationGroups);
                m_usesVertexTextures = GsplatUtils.UseVertexDataTextures;
                if (m_usesVertexTextures)
                    CreateSkinTextures(bindings, m_paletteBones.Length);
                else
                    CreateSkinBuffers(bindings, m_paletteBones.Length);
                UploadBoneMatrices();
                stopwatch.Stop();
                Debug.Log(
                    $"[GsplatProxyAnimator] Bound {bindings.Length:N0} splats to {vertices.Length:N0} " +
                    $"vertices/{triangles.Length:N0} triangles across " +
                    $"{m_proxyMeshRenderers.Length} proxy meshes and " +
                    $"{m_paletteBones.Length - 1} bone slots using deterministic bind-pose skinning " +
                    $"in {stopwatch.Elapsed.TotalSeconds:F2}s.", this);
                GsplatSorter.Instance.MarkGlobalBuffersDirty();
                return true;
            }
            catch (Exception exception)
            {
                ReleaseResources();
                Fail(exception.Message);
                Debug.LogException(exception, this);
                return false;
            }
        }

        void UpdateBoneMatrices()
        {
            UpdateAbsoluteBoneMatrices();
        }

        void CreateReferenceInverseRows(SkinWeightData[] bindings)
        {
            m_referenceInverseRows = new Vector4[checked(bindings.Length * 3)];
            for (int i = 0; i < bindings.Length; ++i)
            {
                int offset = i * 3;
                m_referenceInverseRows[offset] = new Vector4(1f, 0f, 0f, 0f);
                m_referenceInverseRows[offset + 1] = new Vector4(0f, 1f, 0f, 0f);
                m_referenceInverseRows[offset + 2] = new Vector4(0f, 0f, 1f, 0f);
            }
        }

        void PrepareSkinningValidation(List<SkinningValidationGroup> groups)
        {
            m_validationGroups.Clear();
            m_validationFramesRemaining = 0;
            if (!ValidateSkinningAgainstProxy)
                return;

            foreach (SkinningValidationGroup group in groups)
            {
                group.ReferenceInverses = new Matrix4x4[group.SkinWeights.Length];
                for (int i = 0; i < group.SkinWeights.Length; ++i)
                    group.ReferenceInverses[i] = Matrix4x4.identity;
                m_validationGroups.Add(group);
            }
            m_validationFramesRemaining = k_validationDelayFrames;
        }

#if UNITY_EDITOR
        void ValidateSkinningWhenReady()
        {
            if (!ValidateSkinningAgainstProxy || m_validationFramesRemaining <= 0 ||
                m_validationGroups.Count == 0)
                return;
            if (--m_validationFramesRemaining > 0)
                return;

            double bindErrorTotal = 0.0;
            double animatedErrorTotal = 0.0;
            float bindErrorMax = 0f;
            float animatedErrorMax = 0f;
            int sampleCount = 0;
            Matrix4x4 splatToWorld = m_splatRenderer.transform.localToWorldMatrix;
            Matrix4x4 worldToSplat = m_splatRenderer.transform.worldToLocalMatrix;

            foreach (SkinningValidationGroup group in m_validationGroups)
            {
                Mesh sharedMesh = GetProxyMesh(group.Renderer);
                if (!sharedMesh)
                    continue;
                Vector3[] currentVertices = GetCurrentProxyVertices(group.Renderer, sharedMesh);
                Matrix4x4 meshToSplat = worldToSplat * group.Renderer.transform.localToWorldMatrix;
                for (int i = 0; i < group.VertexIndices.Length; ++i)
                {
                    Matrix4x4 current = group.SkinWeights[i].BlendMatrix(m_boneMatrices);
                    Vector3 reconstructedReference = current.MultiplyPoint3x4(
                        group.SourcePositions[i]);
                    Vector3 predictedCurrent = (current * group.ReferenceInverses[i])
                        .MultiplyPoint3x4(group.ReferencePositions[i]);
                    Vector3 actualCurrent = meshToSplat.MultiplyPoint3x4(
                        currentVertices[group.VertexIndices[i]]);

                    float bindError = splatToWorld.MultiplyVector(
                        reconstructedReference - actualCurrent).magnitude;
                    float animatedError = splatToWorld.MultiplyVector(
                        predictedCurrent - actualCurrent).magnitude;
                    bindErrorTotal += bindError;
                    animatedErrorTotal += animatedError;
                    bindErrorMax = Mathf.Max(bindErrorMax, bindError);
                    animatedErrorMax = Mathf.Max(animatedErrorMax, animatedError);
                    sampleCount++;
                }
            }

            if (sampleCount == 0)
                return;
            string verdict = animatedErrorMax <= 0.001f ? "PASSED" : "FAILED";
            Debug.Log(
                $"[GsplatProxyAnimator] Unity BakeMesh validation {verdict} for {sampleCount} vertices. " +
                $"Current-pose error mean/max: {animatedErrorTotal / sampleCount:F6}/{animatedErrorMax:F6} world units. " +
                $"Direct skin-matrix error mean/max: {bindErrorTotal / sampleCount:F6}/{bindErrorMax:F6} world units.",
                this);
        }
#endif

        void UpdateAbsoluteBoneMatrices()
        {
            Matrix4x4 worldToSplat = m_splatRenderer.transform.worldToLocalMatrix;
            m_boneMatrices[0] = Matrix4x4.identity;
            for (int i = 1; i < m_boneMatrices.Length; ++i)
            {
                Transform bone = m_paletteBones[i];
                m_boneMatrices[i] = bone
                    ? worldToSplat * bone.localToWorldMatrix * m_paletteBindTransforms[i]
                    : Matrix4x4.identity;
            }
        }

        static Vector3[] GetCurrentProxyVertices(Renderer proxy, Mesh sharedMesh)
        {
            if (proxy is not SkinnedMeshRenderer skinned)
                return sharedMesh.vertices;

            var bakedMesh = new Mesh { name = $"{sharedMesh.name}.GsplatBindingPose" };
            try
            {
                skinned.BakeMesh(bakedMesh);
                Vector3[] bakedVertices = bakedMesh.vertices;
                if (bakedVertices.Length != sharedMesh.vertexCount)
                    throw new InvalidOperationException(
                        $"Baked proxy mesh '{sharedMesh.name}' changed its vertex count.");
                return bakedVertices;
            }
            finally
            {
                if (Application.isPlaying)
                    Destroy(bakedMesh);
                else
                    DestroyImmediate(bakedMesh);
            }
        }

        void CreateSkinBuffers(SkinWeightData[] bindings, int boneCount)
        {
            m_skinWeightBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                bindings.Length, Marshal.SizeOf<SkinWeightData>())
            {
                name = $"Gsplat.SkinWeights.{name}"
            };
            m_boneMatrixBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                checked(boneCount * 4), Marshal.SizeOf<Vector4>())
            {
                name = $"Gsplat.BoneRows.{name}"
            };
            m_referenceInverseBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                m_referenceInverseRows.Length, Marshal.SizeOf<Vector4>())
            {
                name = $"Gsplat.ReferenceInverseRows.{name}"
            };
            m_skinWeightBuffer.SetData(bindings);
            m_referenceInverseBuffer.SetData(m_referenceInverseRows);
            m_boneMatrixRows = new Vector4[checked(boneCount * 4)];
        }

        void CreateSkinTextures(SkinWeightData[] bindings, int boneCount)
        {
            m_skinIndexTexture = GsplatUtils.CreateVertexDataTexture(
                checked(bindings.Length * 4), GraphicsFormat.R8G8B8A8_UNorm,
                $"Gsplat.SkinIndices.{name}");
            m_skinWeightTexture = GsplatUtils.CreateVertexDataTexture(bindings.Length,
                GraphicsFormat.R32G32B32A32_SFloat, $"Gsplat.SkinWeights.{name}");
            m_referenceInverseTexture = GsplatUtils.CreateVertexDataTexture(
                checked(bindings.Length * 3), GraphicsFormat.R32G32B32A32_SFloat,
                $"Gsplat.ReferenceInverse.{name}");
            m_boneMatrixTexture = GsplatUtils.CreateVertexDataTexture(checked(boneCount * 4),
                GraphicsFormat.R32G32B32A32_SFloat, $"Gsplat.BoneMatrices.{name}");

            var indices = new uint[checked(bindings.Length * 4)];
            var weights = new Vector4[checked(m_skinWeightTexture.width *
                                              m_skinWeightTexture.height)];
            for (int i = 0; i < bindings.Length; ++i)
            {
                SkinWeightData skin = bindings[i];
                int indexOffset = i * 4;
                indices[indexOffset] = skin.BoneIndex0;
                indices[indexOffset + 1] = skin.BoneIndex1;
                indices[indexOffset + 2] = skin.BoneIndex2;
                indices[indexOffset + 3] = skin.BoneIndex3;
                weights[i] = new Vector4(skin.Weight0, skin.Weight1, skin.Weight2, skin.Weight3);
            }
            GsplatUtils.UploadVertexUIntTexture(m_skinIndexTexture, indices);
            GsplatUtils.UploadVertexDataTexture(m_skinWeightTexture, weights, weights.Length);
            GsplatUtils.UploadVertexDataTexture(m_referenceInverseTexture,
                m_referenceInverseRows, m_referenceInverseRows.Length);
            m_boneMatrixRows = new Vector4[checked(m_boneMatrixTexture.width *
                                                   m_boneMatrixTexture.height)];
        }

        void UploadBoneMatrices()
        {
            for (int i = 0; i < m_boneMatrices.Length; ++i)
            {
                Matrix4x4 matrix = m_boneMatrices[i];
                int offset = i * 4;
                m_boneMatrixRows[offset] = matrix.GetRow(0);
                m_boneMatrixRows[offset + 1] = matrix.GetRow(1);
                m_boneMatrixRows[offset + 2] = matrix.GetRow(2);
                m_boneMatrixRows[offset + 3] = matrix.GetRow(3);
            }
            if (!m_usesVertexTextures)
            {
                m_boneMatrixBuffer.SetData(m_boneMatrixRows);
                return;
            }
            GsplatUtils.UploadVertexDataTexture(m_boneMatrixTexture, m_boneMatrixRows,
                m_boneMatrixRows.Length, 1, false);
        }

        static Vector3[] GetSplatCenters(GsplatAsset asset)
        {
            if (asset is GsplatAssetUncompressed uncompressed)
                return uncompressed.Positions ?? Array.Empty<Vector3>();
            if (asset is not GsplatAssetSpark spark || spark.PackedSplats == null)
                throw new NotSupportedException(
                    $"Proxy animation does not support Gaussian asset type '{asset.GetType().Name}'.");

            var centers = new Vector3[spark.PackedSplats.Length];
            for (int i = 0; i < centers.Length; ++i)
            {
                var packed = spark.PackedSplats[i];
                centers[i] = new Vector3(
                    HalfToFloat(packed.y & 0xffffu),
                    HalfToFloat((packed.y >> 16) & 0xffffu),
                    HalfToFloat(packed.z & 0xffffu));
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
                if (mantissa == 0)
                    bits = sign;
                else
                {
                    int unbiasedExponent = -14;
                    while ((mantissa & 0x400u) == 0)
                    {
                        mantissa <<= 1;
                        unbiasedExponent--;
                    }
                    mantissa &= 0x3ffu;
                    bits = sign | (uint)(unbiasedExponent + 127) << 23 | mantissa << 13;
                }
            }
            else if (exponent == 0x1fu)
                bits = sign | 0x7f800000u | mantissa << 13;
            else
                bits = sign | (exponent + 112u) << 23 | mantissa << 13;
            return BitConverter.Int32BitsToSingle((int)bits);
        }

        internal override GsplatSkinningSnapshot CaptureCpuSnapshot()
        {
            if (!IsActivelyAnimating)
                return null;
            return new GsplatProxySkinningSnapshot(m_skinWeights, m_referenceInverseRows,
                (Matrix4x4[])m_boneMatrices.Clone());
        }

        internal override Bounds CalculateLocalBounds(Bounds sourceBounds)
        {
            if (!IsActivelyAnimating || !ProxyRoot)
                return sourceBounds;

            bool hasWorldBounds = false;
            Bounds world = default;
            for (int i = 0; i < m_hiddenRenderers.Length; ++i)
            {
                Renderer proxyRenderer = m_hiddenRenderers[i];
                if (!proxyRenderer)
                    continue;
                if (!hasWorldBounds)
                {
                    world = proxyRenderer.bounds;
                    hasWorldBounds = true;
                }
                else
                    world.Encapsulate(proxyRenderer.bounds);
            }
            if (!hasWorldBounds)
                return sourceBounds;

            Vector3 min = world.min;
            Vector3 max = world.max;
            Matrix4x4 worldToSplat = m_splatRenderer.transform.worldToLocalMatrix;
            Bounds result = sourceBounds;
            for (int x = 0; x < 2; ++x)
            for (int y = 0; y < 2; ++y)
            for (int z = 0; z < 2; ++z)
            {
                Vector3 corner = new(x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z);
                result.Encapsulate(worldToSplat.MultiplyPoint3x4(corner));
            }
            result.Expand(BoundsPadding * 2f);
            return result;
        }

        void ResolveProxyHierarchy(bool forceRefresh = false)
        {
            if (!ProxyRoot)
            {
                Animator animator = ProxyRenderer
                    ? ProxyRenderer.GetComponentInParent<Animator>()
                    : GetComponentInChildren<Animator>(true);
                if (animator && animator.transform != transform)
                    ProxyRoot = animator.transform;
                else
                {
                    if (!ProxyRenderer)
                        ProxyRenderer = GetComponentInChildren<SkinnedMeshRenderer>(true);
                    if (ProxyRenderer)
                        ProxyRoot = FindProxyHierarchyRoot(ProxyRenderer.transform);
                    else
                    {
                        MeshRenderer meshRenderer = GetComponentInChildren<MeshRenderer>(true);
                        if (meshRenderer)
                            ProxyRoot = FindProxyHierarchyRoot(meshRenderer.transform);
                    }
                }
            }

            if (!ProxyRoot)
            {
                m_proxyMeshRenderers = Array.Empty<Renderer>();
                m_resolvedProxyRoot = null;
                return;
            }
            if (forceRefresh || m_resolvedProxyRoot != ProxyRoot || m_proxyMeshRenderers == null)
            {
                var renderers = new List<Renderer>();
                renderers.AddRange(ProxyRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                MeshFilter[] meshFilters = ProxyRoot.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < meshFilters.Length; ++i)
                {
                    MeshRenderer meshRenderer = meshFilters[i].GetComponent<MeshRenderer>();
                    if (meshRenderer)
                        renderers.Add(meshRenderer);
                }
                m_proxyMeshRenderers = renderers.ToArray();
                m_resolvedProxyRoot = ProxyRoot;
            }
            if (!ProxyRenderer)
                ProxyRenderer = ProxyRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);
        }

        Transform FindProxyHierarchyRoot(Transform candidate)
        {
            while (candidate.parent && candidate.parent != transform)
                candidate = candidate.parent;
            return candidate;
        }

        static Mesh GetProxyMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh;
            return renderer ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null;
        }

        bool BindingsMatchCurrentHierarchy()
        {
            if (m_boundAsset != m_splatRenderer?.GsplatAsset ||
                m_boundProxyRoot != ProxyRoot || m_boundMeshes == null ||
                m_boundMeshes.Length != m_proxyMeshRenderers.Length)
                return false;
            for (int i = 0; i < m_proxyMeshRenderers.Length; ++i)
            {
                if (!m_proxyMeshRenderers[i] ||
                    m_boundMeshes[i] != GetProxyMesh(m_proxyMeshRenderers[i]))
                    return false;
            }
            return true;
        }

        void ApplyProxyVisibility()
        {
            RestoreProxyVisibility();
            if (!ProxyRoot)
                return;
            m_hiddenRenderers = ProxyRoot.GetComponentsInChildren<Renderer>(true);
            m_previousForceRenderingOff = new bool[m_hiddenRenderers.Length];
            for (int i = 0; i < m_hiddenRenderers.Length; ++i)
                m_previousForceRenderingOff[i] = m_hiddenRenderers[i].forceRenderingOff;
            if (HideProxyRenderer)
            {
                for (int i = 0; i < m_hiddenRenderers.Length; ++i)
                    m_hiddenRenderers[i].forceRenderingOff = true;
            }
        }

        void RestoreProxyVisibility()
        {
            int count = Math.Min(m_hiddenRenderers.Length, m_previousForceRenderingOff.Length);
            for (int i = 0; i < count; ++i)
            {
                if (m_hiddenRenderers[i])
                    m_hiddenRenderers[i].forceRenderingOff = m_previousForceRenderingOff[i];
            }
            m_hiddenRenderers = Array.Empty<Renderer>();
            m_previousForceRenderingOff = Array.Empty<bool>();
        }

        void Fail(string message)
        {
            m_initializationFailed = true;
            Debug.LogError($"[GsplatProxyAnimator] {message}", this);
        }

        void ReleaseResources()
        {
            m_skinWeightBuffer?.Dispose();
            m_skinWeightBuffer = null;
            m_referenceInverseBuffer?.Dispose();
            m_referenceInverseBuffer = null;
            m_boneMatrixBuffer?.Dispose();
            m_boneMatrixBuffer = null;
            GsplatUtils.DestroyRuntimeTexture(m_skinIndexTexture);
            m_skinIndexTexture = null;
            GsplatUtils.DestroyRuntimeTexture(m_skinWeightTexture);
            m_skinWeightTexture = null;
            GsplatUtils.DestroyRuntimeTexture(m_referenceInverseTexture);
            m_referenceInverseTexture = null;
            GsplatUtils.DestroyRuntimeTexture(m_boneMatrixTexture);
            m_boneMatrixTexture = null;
            m_skinWeights = null;
            m_referenceInverseRows = null;
            m_validationGroups.Clear();
            m_validationFramesRemaining = 0;
            m_boneMatrices = null;
            m_boneMatrixRows = null;
            m_usesVertexTextures = false;
            m_boundAsset = null;
            m_boundProxyRoot = null;
            m_boundMeshes = null;
            m_paletteBones = null;
            m_paletteBindTransforms = null;
            GsplatSorter.Instance.MarkGlobalBuffersDirty();
        }
    }

    internal sealed class GsplatProxySkinningSnapshot : GsplatSkinningSnapshot
    {
        readonly GsplatProxyAnimator.SkinWeightData[] m_skinWeights;
        readonly Vector4[] m_referenceInverseRows;
        readonly Matrix4x4[] m_boneMatrices;

        public GsplatProxySkinningSnapshot(GsplatProxyAnimator.SkinWeightData[] skinWeights,
            Vector4[] referenceInverseRows, Matrix4x4[] boneMatrices)
        {
            m_skinWeights = skinWeights;
            m_referenceInverseRows = referenceInverseRows;
            m_boneMatrices = boneMatrices;
        }

        public override Vector3 TransformPoint(uint sourceId, Vector3 point)
        {
            if (sourceId >= m_skinWeights.Length)
                return point;
            int offset = checked((int)sourceId * 3);
            Matrix4x4 referenceInverse = Matrix4x4.identity;
            referenceInverse.SetRow(0, m_referenceInverseRows[offset]);
            referenceInverse.SetRow(1, m_referenceInverseRows[offset + 1]);
            referenceInverse.SetRow(2, m_referenceInverseRows[offset + 2]);
            Matrix4x4 deformation = m_skinWeights[sourceId].BlendMatrix(m_boneMatrices) *
                                    referenceInverse;
            return deformation.MultiplyPoint3x4(point);
        }
    }
}
