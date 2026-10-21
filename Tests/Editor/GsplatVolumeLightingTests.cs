// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatVolumeLightingTests
    {
        [Test]
        public void UnifiedRelightingComponentDefaultsToVolumeMode()
        {
            var gameObject = new GameObject("Unified Gaussian relighting defaults test");
            try
            {
                var lighting = gameObject.AddComponent<GsplatGaussianRelighting>();

                Assert.AreEqual(GsplatRelightingMode.Volume, lighting.RelightingMode);
                Assert.IsTrue(gameObject.TryGetComponent(out GsplatRenderer _));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void NewVolumeLightingUsesComparisonFriendlyDefaults()
        {
            var gameObject = new GameObject("Gsplat volume lighting defaults test");
            try
            {
                var lighting = gameObject.AddComponent<GsplatVolumeLighting>();

                Assert.AreEqual(GsplatRelightingMode.Volume, lighting.RelightingMode);
                Assert.AreEqual(1f, lighting.Blend);
                Assert.AreEqual(GsplatVolumeLightMode.Automatic, lighting.LightMode);
                Assert.AreEqual(4, lighting.AutomaticLightLimit);
                Assert.IsNull(lighting.SingleLightOverride);
                Assert.AreEqual(0.2f, lighting.AmbientStrength);
                Assert.AreEqual(1f, lighting.DirectStrength);
                Assert.AreEqual(64, lighting.FieldResolution);
                Assert.AreEqual(1f, lighting.ShadowStrength);
                Assert.AreEqual(8, lighting.ShadowSteps);
                Assert.IsTrue(gameObject.TryGetComponent(out GsplatRenderer renderer));
                Assert.IsTrue(renderer.HasActiveVolumeLighting);

                lighting.RelightingMode = GsplatRelightingMode.ProxyMesh;
                Assert.IsFalse(renderer.HasActiveVolumeLighting);

                lighting.enabled = false;
                Assert.IsFalse(renderer.HasActiveVolumeLighting);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void UncompressedAssetBuildsOpacityFieldWithoutProxyGeometry()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            var gameObject = new GameObject("Gsplat volume lighting field test");
            try
            {
                asset.SplatCount = 2;
                asset.Bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
                asset.Positions = new[] { new Vector3(-0.25f, 0f, 0f),
                    new Vector3(0.25f, 0f, 0f) };
                asset.Colors = new[] { new Vector4(1f, 1f, 1f, 0.5f),
                    new Vector4(1f, 1f, 1f, 0.75f) };
                asset.Scales = new[] { Vector3.one * 0.1f, Vector3.one * 0.1f };
                asset.Rotations = new[] { new Vector4(1f, 0f, 0f, 0f),
                    new Vector4(1f, 0f, 0f, 0f) };

                GsplatRenderer renderer = gameObject.AddComponent<GsplatRenderer>();
                renderer.GsplatAsset = asset;
                GsplatVolumeLighting lighting =
                    gameObject.AddComponent<GsplatVolumeLighting>();
                lighting.FieldResolution = 16;
                lighting.FieldSmoothing = 0;

                Assert.IsTrue(lighting.RebuildOpacityField());
                Assert.IsNotNull(lighting.OpacityField);
                Assert.AreEqual(16, lighting.OpacityField.width);
                Assert.AreEqual(16, lighting.OpacityField.height);
                Assert.AreEqual(16, lighting.OpacityField.depth);
                Assert.IsNull(gameObject.GetComponent<MeshFilter>());
                Assert.IsNull(gameObject.GetComponent<MeshRenderer>());
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void RenderersDrawingTheSameAssetShareOneOpacityField()
        {
            GsplatAssetUncompressed asset = CreateTwoSplatAsset();
            var first = new GameObject("Gsplat shared opacity field A");
            var second = new GameObject("Gsplat shared opacity field B");
            try
            {
                GsplatVolumeLighting firstLighting = AddVolumeLighting(first, asset);
                GsplatVolumeLighting secondLighting = AddVolumeLighting(second, asset);

                Assert.IsTrue(firstLighting.TryGetShaderData(out _));
                Assert.IsTrue(secondLighting.TryGetShaderData(out _));
                Texture3D shared = firstLighting.OpacityField;
                Assert.IsNotNull(shared);
                Assert.AreSame(shared, secondLighting.OpacityField);
                Assert.AreEqual(2, secondLighting.OpacityFieldUsers);

                Object.DestroyImmediate(first);
                Assert.IsTrue(shared, "The field must survive while another renderer uses it.");
                Assert.AreEqual(1, secondLighting.OpacityFieldUsers);

                Object.DestroyImmediate(second);
                Assert.IsFalse(shared, "The last user must release the field texture.");
            }
            finally
            {
                if (first)
                    Object.DestroyImmediate(first);
                if (second)
                    Object.DestroyImmediate(second);
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void MissingSplatDataKeepsLightingWithoutRebuildingEveryDraw()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.SplatCount = 4;
            asset.Bounds = new Bounds(Vector3.zero, Vector3.one);
            var gameObject = new GameObject("Gsplat volume lighting missing data test");
            int warnings = 0;
            void CountWarnings(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Warning && message.Contains("no CPU-side splat"))
                    ++warnings;
            }

            Application.logMessageReceived += CountWarnings;
            try
            {
                GsplatVolumeLighting lighting = AddVolumeLighting(gameObject, asset);

                for (int draw = 0; draw < 3; ++draw)
                {
                    Assert.IsTrue(lighting.TryGetShaderData(out var data));
                    Assert.AreEqual(0f, data.OcclusionParams.x);
                    Assert.AreSame(GsplatVolumeLighting.EmptyOpacityField, data.OpacityField);
                }

                Assert.IsNull(lighting.OpacityField);
                Assert.IsTrue(lighting.IsOpacityFieldUnavailable);
                Assert.AreEqual(1, warnings, "A failed field must not be rebuilt on every draw.");
            }
            finally
            {
                Application.logMessageReceived -= CountWarnings;
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void NonFiniteSplatCentersAreSkippedWhenBuildingOpacityField()
        {
            GsplatAssetUncompressed asset = CreateTwoSplatAsset();
            asset.Positions[0] = new Vector3(float.NaN, 0f, float.PositiveInfinity);
            var gameObject = new GameObject("Gsplat volume lighting non-finite test");
            try
            {
                GsplatVolumeLighting lighting = AddVolumeLighting(gameObject, asset);

                Assert.IsTrue(lighting.RebuildOpacityField());
                Assert.IsNotNull(lighting.OpacityField);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(asset);
            }
        }

        static GsplatAssetUncompressed CreateTwoSplatAsset()
        {
            var asset = ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.SplatCount = 2;
            asset.Bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            asset.Positions = new[] { new Vector3(-0.25f, 0f, 0f),
                new Vector3(0.25f, 0f, 0f) };
            asset.Colors = new[] { new Vector4(1f, 1f, 1f, 0.5f),
                new Vector4(1f, 1f, 1f, 0.75f) };
            asset.Scales = new[] { Vector3.one * 0.1f, Vector3.one * 0.1f };
            asset.Rotations = new[] { new Vector4(1f, 0f, 0f, 0f),
                new Vector4(1f, 0f, 0f, 0f) };
            return asset;
        }

        static GsplatVolumeLighting AddVolumeLighting(GameObject gameObject,
            GsplatAsset asset)
        {
            GsplatRenderer renderer = gameObject.AddComponent<GsplatRenderer>();
            renderer.GsplatAsset = asset;
            var lighting = gameObject.AddComponent<GsplatVolumeLighting>();
            lighting.FieldResolution = 16;
            lighting.FieldSmoothing = 0;
            return lighting;
        }
    }
}
