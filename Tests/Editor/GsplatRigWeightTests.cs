// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class GsplatRigWeightTests
    {
        [Test]
        public void SetWeight_NormalizesOtherInfluences()
        {
            var weight = GsplatRigWeight.FromInfluences(1, 0.5f, 2, 0.5f, 0, 0f, 0, 0f);

            weight.SetWeight(2, 0.25f, true);

            Assert.That(weight.GetWeight(2), Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(weight.GetWeight(1), Is.EqualTo(0.75f).Within(1e-6f));
        }

        [Test]
        public void FromInfluences_MergesDuplicateBoneSlots()
        {
            GsplatRigWeight weight = GsplatRigWeight.FromInfluences(
                3, 0.2f, 4, 0.3f, 3, 0.4f, 5, 0.1f);

            Assert.That(weight.GetWeight(3), Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(weight.GetWeight(4), Is.EqualTo(0.3f).Within(1e-6f));
            Assert.That(weight.GetWeight(5), Is.EqualTo(0.1f).Within(1e-6f));
        }

        [Test]
        public void Normalize_UsesIdentityFallbackForEmptyWeight()
        {
            var weight = new GsplatRigWeight();

            weight.Normalize();

            Assert.That(weight.BoneIndex0, Is.EqualTo(0));
            Assert.That(weight.Weight0, Is.EqualTo(1f));
        }

        [Test]
        public void ProxyTriangleBlend_UsesBarycentricBoneWeights()
        {
            var a = new GsplatProxyAnimator.SkinWeightData(
                new BoneWeight { boneIndex0 = 1, weight0 = 1f }, 4);
            var b = new GsplatProxyAnimator.SkinWeightData(
                new BoneWeight { boneIndex0 = 2, weight0 = 1f }, 4);
            var c = new GsplatProxyAnimator.SkinWeightData(
                new BoneWeight { boneIndex0 = 3, weight0 = 1f }, 4);
            var indices = new uint[12];
            var weights = new float[12];

            GsplatProxyAnimator.SkinWeightData result =
                GsplatProxyAnimator.SkinWeightData.Interpolate(
                    a, b, c, new Vector3(0.2f, 0.3f, 0.5f), indices, weights);

            Assert.That(result.BoneIndex0, Is.EqualTo(3));
            Assert.That(result.Weight0, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(result.BoneIndex1, Is.EqualTo(2));
            Assert.That(result.Weight1, Is.EqualTo(0.3f).Within(1e-6f));
            Assert.That(result.BoneIndex2, Is.EqualTo(1));
            Assert.That(result.Weight2, Is.EqualTo(0.2f).Within(1e-6f));
        }
    }
}
