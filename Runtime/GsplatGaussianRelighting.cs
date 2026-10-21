// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// User-facing unified Gaussian relighting component. The inherited implementation keeps
    /// existing serialized volume-lighting scenes compatible while exposing both relighting paths
    /// through one component and one mode selector.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GsplatRenderer))]
    [AddComponentMenu("Gsplat/Gaussian Relighting")]
    public sealed class GsplatGaussianRelighting : GsplatVolumeLighting
    {
    }
}
