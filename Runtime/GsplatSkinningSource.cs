// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    /// <summary>Common GPU deformation contract for manual and proxy-based Gaussian rigs.</summary>
    public abstract class GsplatSkinningSource : MonoBehaviour
    {
        internal abstract GraphicsBuffer SkinWeightBuffer { get; }
        internal abstract GraphicsBuffer ReferenceInverseBuffer { get; }
        internal abstract GraphicsBuffer BoneRowBuffer { get; }
        internal abstract Texture2D SkinIndexTexture { get; }
        internal abstract Texture2D SkinWeightTexture { get; }
        internal abstract Texture2D ReferenceInverseTexture { get; }
        internal abstract Texture2D BoneMatrixTexture { get; }
        internal abstract bool UsesVertexTextures { get; }
        internal abstract bool IsActivelyAnimating { get; }
        internal abstract GsplatSkinningSnapshot CaptureCpuSnapshot();
        internal abstract Bounds CalculateLocalBounds(Bounds sourceBounds);
    }

    internal abstract class GsplatSkinningSnapshot
    {
        public abstract Vector3 TransformPoint(uint sourceId, Vector3 point);
    }
}
