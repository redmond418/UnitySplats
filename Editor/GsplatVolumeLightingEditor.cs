// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Editor
{
    [CustomEditor(typeof(GsplatVolumeLighting), true)]
    [CanEditMultipleObjects]
    internal sealed class GsplatVolumeLightingEditor : UnityEditor.Editor
    {
        static readonly string[] s_allRelightingProperties =
        {
            "RelightingMode", "ProxyMesh", "ProxySourceCamera", "ProxyTextureScale",
            "ProxyBlend", "ProxyBrightness", "ProxyBackground", "m_proxyBinding",
            "m_proxySettingsInitialized", "LightMode", "AutomaticLightLimit",
            "SingleLightOverride", "Blend", "AmbientTint", "AmbientStrength",
            "DirectStrength", "DiffuseWrap", "FieldResolution", "FieldDensity",
            "FieldSmoothing", "ShadowStrength", "ShadowSteps", "ShadowBias"
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty relightingMode = serializedObject.FindProperty("RelightingMode");
            EditorGUILayout.PropertyField(relightingMode, new GUIContent("Mode"));

            bool proxyMode = !relightingMode.hasMultipleDifferentValues &&
                             (GsplatRelightingMode)relightingMode.enumValueIndex ==
                             GsplatRelightingMode.ProxyMesh;
            if (proxyMode)
                DrawProxyInspector();
            else
                DrawVolumeInspector();

            DrawPropertiesExcluding(serializedObject,
                new[] { "m_Script" }.Concat(s_allRelightingProperties).ToArray());
            bool changed = serializedObject.ApplyModifiedProperties();

            if (proxyMode)
                DrawProxyStatus(changed);
            else
                DrawVolumeStatus();

            if (changed)
                SceneView.RepaintAll();
        }

        void DrawVolumeInspector()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Volume Relighting", EditorStyles.boldLabel);
            SerializedProperty lightMode = serializedObject.FindProperty("LightMode");
            EditorGUILayout.PropertyField(lightMode);
            if (!lightMode.hasMultipleDifferentValues &&
                (GsplatVolumeLightMode)lightMode.enumValueIndex ==
                GsplatVolumeLightMode.SingleOverride)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(
                    "SingleLightOverride"));
            }
            else
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(
                    "AutomaticLightLimit"));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Blend"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AmbientTint"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AmbientStrength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("DirectStrength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("DiffuseWrap"));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Opacity Self-Occlusion", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FieldResolution"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FieldDensity"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FieldSmoothing"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ShadowStrength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ShadowSteps"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ShadowBias"));
        }

        void DrawVolumeStatus()
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Volume mode is the default. It shades the Gaussians directly, automatically combines affecting lights, and needs no proxy mesh.",
                MessageType.Info);

            if (targets.Length == 1)
            {
                var lighting = (GsplatVolumeLighting)target;
                int lightCount = lighting.ActiveLightCount;
                if (lightCount == 0)
                    EditorGUILayout.HelpBox(
                        "No active directional, point, or spot light affects this renderer. Only Ambient Tint and Ambient Strength will be used.",
                        MessageType.Warning);
                else if (lighting.LightMode == GsplatVolumeLightMode.Automatic)
                    EditorGUILayout.LabelField(
                        $"Automatic lights: {lightCount}; strongest is {lighting.PrimaryLight.name}",
                        EditorStyles.miniLabel);
                else
                    EditorGUILayout.LabelField(
                        $"Single light override: {lighting.PrimaryLight.name}",
                        EditorStyles.miniLabel);

                string status = lighting.OpacityField
                    ? $"Opacity field ready: {lighting.OpacityField.width}³" +
                      (lighting.OpacityFieldUsers > 1
                          ? $", shared by {lighting.OpacityFieldUsers} renderers"
                          : string.Empty)
                    : lighting.IsOpacityFieldUnavailable
                        ? "Opacity field unavailable; lighting without self-occlusion."
                        : "Opacity field builds lazily when the renderer draws.";
                EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

                if (GUILayout.Button(new GUIContent("Rebuild Gaussian Opacity Field",
                        "Recreates the self-occlusion volume after asset data or field settings change.")))
                {
                    lighting.RebuildOpacityField();
                    SceneView.RepaintAll();
                }
            }
        }

        void DrawProxyInspector()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Proxy Mesh Relighting", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ProxyMesh"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ProxySourceCamera"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ProxyTextureScale"),
                new GUIContent("Texture Scale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ProxyBlend"),
                new GUIContent("Blend"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ProxyBrightness"),
                new GUIContent("Brightness"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ProxyBackground"),
                new GUIContent("Background"));
        }

        void DrawProxyStatus(bool changed)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Proxy Mesh mode uses the existing Lit proxy capture. Assign an aligned mesh child; its backend, material, layer, and camera capture are configured automatically without deleting the Volume settings.",
                MessageType.Info);

            if (targets.Length != 1)
                return;

            var relighting = (GsplatVolumeLighting)target;
            if (!relighting.ProxyMesh)
            {
                EditorGUILayout.HelpBox("Assign an aligned proxy mesh child.", MessageType.Warning);
                return;
            }

            if (changed)
                GsplatRelightingSetup.ConfigureUnified(relighting, false);

            bool configured = relighting.ProxyBinding &&
                              relighting.ProxyBinding.RelightingSource;
            EditorGUILayout.LabelField(configured
                    ? "Proxy relighting backend ready."
                    : "Proxy relighting needs configuration.",
                EditorStyles.miniLabel);
            if (GUILayout.Button("Configure Proxy Mesh Relighting"))
            {
                GsplatRelightingSetup.ConfigureUnified(relighting);
                SceneView.RepaintAll();
            }
        }
    }
}
