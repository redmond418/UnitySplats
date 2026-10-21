// Copyright (c) 2026 ARLOOPA
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gsplat.Editor
{
    /// <summary>Manual armature authoring and live proxy-mesh animation for Gaussian splats.</summary>
    public sealed class GsplatRiggingWindow : EditorWindow
    {
        enum PaintMode { Draw, Add, Subtract, Smooth }

        readonly struct BrushHit
        {
            public readonly int Index;
            public readonly float Radius01;
            public readonly float Depth;
            public readonly long Cell;

            public BrushHit(int index, float radius01, float depth, long cell)
            {
                Index = index;
                Radius01 = radius01;
                Depth = depth;
                Cell = cell;
            }
        }

        const int k_sceneControlHash = 0x47535247; // GSRG
        const int k_overlayPointLimit = 12000;
        const float k_frontDepthFraction = 0.0125f;

        [SerializeField] GsplatRenderer m_renderer;
        [SerializeField] bool m_paintEnabled;
        [SerializeField] PaintMode m_paintMode = PaintMode.Draw;
        [SerializeField] float m_brushRadius = 45f;
        [SerializeField] float m_strength = 0.35f;
        [SerializeField] float m_targetWeight = 1f;
        [SerializeField] bool m_xray;
        [SerializeField] bool m_showWeightOverlay = true;
        [SerializeField] int m_selectedBone = 1;
        [SerializeField] string m_boneSearch = string.Empty;
        [SerializeField] Transform m_proxyRoot;

        Vector2 m_scroll;
        Vector2 m_boneScroll;
        GsplatAsset m_cachedCenterAsset;
        Vector3[] m_centers = Array.Empty<Vector3>();
        readonly List<BrushHit> m_brushHits = new();
        readonly Dictionary<long, float> m_nearestDepthByCell = new();
        bool m_strokeActive;
        bool m_placingBone;
        Transform m_placementParent;
        string m_status = "Select a Gaussian renderer to begin.";
        int m_affectedSplats;

        // GsplatScenePicker uses this flag to avoid stealing clicks from either editor mode.
        internal static bool IsSceneRiggingActive { get; private set; }

        GsplatRig Rig => m_renderer ? m_renderer.GetComponent<GsplatRig>() : null;
        GsplatRigAsset RigAsset => Rig ? Rig.RigAsset : null;
        GsplatProxyAnimator ProxyAnimator =>
            m_renderer ? m_renderer.GetComponent<GsplatProxyAnimator>() : null;

        [MenuItem("Window/Gaussian Splat/Rigging")]
        static void Open()
        {
            var window = GetWindow<GsplatRiggingWindow>();
            window.titleContent = new GUIContent("Gaussian Rigging");
            window.minSize = new Vector2(380f, 600f);
            window.Show();
        }

        internal static void OpenFor(GsplatRenderer renderer)
        {
            Open();
            var window = GetWindow<GsplatRiggingWindow>();
            window.SetRenderer(renderer);
            window.Focus();
        }

        void OnEnable()
        {
            SceneView.duringSceneGui -= DuringSceneGui;
            SceneView.duringSceneGui += DuringSceneGui;
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
            AdoptSelection(false);
            UpdateInteractionState();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGui;
            Selection.selectionChanged -= OnSelectionChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            IsSceneRiggingActive = false;
            m_strokeActive = false;
            m_placingBone = false;
        }

        void OnSelectionChanged()
        {
            AdoptSelection(true);
            Repaint();
        }

        void AdoptSelection(bool keepCurrentWhenUnrelated)
        {
            GameObject selected = Selection.activeGameObject;
            GsplatRenderer candidate = selected
                ? selected.GetComponentInParent<GsplatRenderer>()
                : null;
            if (candidate || !keepCurrentWhenUnrelated)
                SetRenderer(candidate);
        }

        void SetRenderer(GsplatRenderer renderer)
        {
            if (m_renderer == renderer)
            {
                if (!m_proxyRoot)
                    m_proxyRoot = FindSuggestedProxyRoot(renderer);
                return;
            }
            m_renderer = renderer;
            m_cachedCenterAsset = null;
            m_centers = Array.Empty<Vector3>();
            m_selectedBone = 1;
            m_affectedSplats = 0;
            m_paintEnabled = false;
            m_placingBone = false;
            m_placementParent = null;
            m_proxyRoot = FindSuggestedProxyRoot(renderer);
            m_status = renderer
                ? "Choose a manual painted rig or live proxy-mesh animation."
                : "Select a Gaussian renderer to begin.";
            UpdateInteractionState();
            SceneView.RepaintAll();
        }

        void OnGUI()
        {
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            DrawTargetSection();
            EditorGUILayout.Space(8f);
            DrawRigSection();
            EditorGUILayout.Space(8f);
            DrawProxyAnimationSection();
            EditorGUILayout.Space(8f);
            DrawBoneSection();
            EditorGUILayout.Space(8f);
            DrawPaintSection();
            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(m_status, MessageType.Info);
            EditorGUILayout.EndScrollView();
            UpdateInteractionState();
        }

        void DrawTargetSection()
        {
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var renderer = (GsplatRenderer)EditorGUILayout.ObjectField(
                "Gaussian Renderer", m_renderer, typeof(GsplatRenderer), true);
            if (EditorGUI.EndChangeCheck())
                SetRenderer(renderer);

            if (!m_renderer)
                return;
            if (!m_renderer.GsplatAsset)
                EditorGUILayout.HelpBox("The selected renderer has no Gaussian asset.", MessageType.Warning);

            EditorGUI.BeginChangeCheck();
            GsplatRenderer.GsplatDeformationSource deformationSource =
                (GsplatRenderer.GsplatDeformationSource)EditorGUILayout.EnumPopup(
                    "Animation Source", m_renderer.DeformationSource);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(m_renderer, "Set Gaussian Animation Source");
                m_renderer.DeformationSource = deformationSource;
                m_renderer.ForceRefresh();
                EditorUtility.SetDirty(m_renderer);
            }

            GsplatRig rig = Rig;
            if (!rig)
            {
                if (GUILayout.Button("Add Manual Gaussian Rig", GUILayout.Height(26f)))
                {
                    rig = Undo.AddComponent<GsplatRig>(m_renderer.gameObject);
                    Undo.RecordObject(m_renderer, "Use Manual Gaussian Rig");
                    m_renderer.DeformationSource = GsplatRenderer.GsplatDeformationSource.ManualRig;
                    EditorUtility.SetDirty(m_renderer);
                    m_status = "Create an armature root. The Gaussians are driven directly by its bones.";
                }
            }

            if (rig)
            {
                EditorGUI.BeginChangeCheck();
                Transform armatureRoot = (Transform)EditorGUILayout.ObjectField(
                    "Manual Armature Root", rig.ArmatureRoot, typeof(Transform), true);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(rig, "Set Gaussian Armature Root");
                    rig.ArmatureRoot = armatureRoot;
                    rig.RebuildBindings();
                    EditorUtility.SetDirty(rig);
                    SceneView.RepaintAll();
                }

                if (!rig.ArmatureRoot && GUILayout.Button("Create Manual Armature Root"))
                    CreateArmatureRoot();
            }
        }

        void DrawRigSection()
        {
            EditorGUILayout.LabelField("Manual Rig Data", EditorStyles.boldLabel);
            GsplatRig rig = Rig;
            using (new EditorGUI.DisabledScope(!rig))
            {
                EditorGUI.BeginChangeCheck();
                GsplatRigAsset asset = (GsplatRigAsset)EditorGUILayout.ObjectField(
                    "Rig Asset", rig ? rig.RigAsset : null, typeof(GsplatRigAsset), false);
                if (EditorGUI.EndChangeCheck() && rig)
                {
                    Undo.RecordObject(rig, "Assign Gaussian Rig");
                    rig.RigAsset = asset;
                    rig.RebuildBindings();
                    EditorUtility.SetDirty(rig);
                    ClampSelectedBone();
                    RefreshCenters();
                    UpdateCoverage();
                }

                using (new EditorGUI.DisabledScope(!CanCreateRig(out _)))
                {
                    if (!RigAsset)
                    {
                        if (GUILayout.Button("Create Empty Rig Asset…", GUILayout.Height(28f)))
                            CreateRigAsset();
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Sync Manual Bones"))
                                SynchronizeRigAsset(false, "Synchronized bone groups.");
                            if (GUILayout.Button("Set Bind Pose"))
                                SetCurrentBindPose();
                        }
                    }
                }
            }

            if (RigAsset)
            {
                EditorGUILayout.LabelField("Gaussians", RigAsset.SplatCount.ToString("N0"));
                EditorGUILayout.LabelField("Bones", Mathf.Max(0, RigAsset.BoneCount - 1).ToString("N0"));
            }
        }

        void DrawProxyAnimationSection()
        {
            EditorGUILayout.LabelField("Live Proxy Mesh Animation", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Uses the backup proxy algorithm directly: each Gaussian binds to the nearest " +
                "triangle in the proxy mesh's original bind pose and follows that mesh's bones " +
                "live. All child skinned and rigid meshes are supported. Enable Read/Write on " +
                "every proxy mesh, and align its original bind pose with the Gaussian.",
                MessageType.None);

            if (!m_renderer)
                return;

            GsplatProxyAnimator proxyAnimator = ProxyAnimator;
            if (!proxyAnimator)
            {
                if (GUILayout.Button("Add Live Proxy Mesh Animator", GUILayout.Height(28f)))
                {
                    proxyAnimator = Undo.AddComponent<GsplatProxyAnimator>(m_renderer.gameObject);
                    proxyAnimator.ProxyRoot = m_proxyRoot ? m_proxyRoot : FindSuggestedProxyRoot(m_renderer);
                    Undo.RecordObject(m_renderer, "Use Proxy Mesh Animation");
                    m_renderer.DeformationSource = GsplatRenderer.GsplatDeformationSource.ProxyMesh;
                    EditorUtility.SetDirty(proxyAnimator);
                    EditorUtility.SetDirty(m_renderer);
                    proxyAnimator.RebuildBindings();
                    m_status = "Live proxy animator added. It uses the proxy's original mesh bind pose.";
                }
                return;
            }

            EditorGUI.BeginChangeCheck();
            Transform proxyRoot = (Transform)EditorGUILayout.ObjectField(
                "Proxy Mesh Root", proxyAnimator.ProxyRoot, typeof(Transform), true);
            SkinnedMeshRenderer primaryRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "Primary Skinned Mesh", proxyAnimator.ProxyRenderer,
                typeof(SkinnedMeshRenderer), true);
            bool animateInEditMode = EditorGUILayout.Toggle(
                "Animate In Edit Mode", proxyAnimator.AnimateInEditMode);
            bool hideProxyRenderer = EditorGUILayout.Toggle(
                "Hide All Proxy Renderers", proxyAnimator.HideProxyRenderer);
            bool validate = EditorGUILayout.Toggle(
                "Validate Against Proxy", proxyAnimator.ValidateSkinningAgainstProxy);
            float boundsPadding = EditorGUILayout.FloatField(
                "Bounds Padding", proxyAnimator.BoundsPadding);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(proxyAnimator, "Configure Live Proxy Animation");
                proxyAnimator.ProxyRoot = proxyRoot;
                proxyAnimator.ProxyRenderer = primaryRenderer;
                proxyAnimator.AnimateInEditMode = animateInEditMode;
                proxyAnimator.HideProxyRenderer = hideProxyRenderer;
                proxyAnimator.ValidateSkinningAgainstProxy = validate;
                proxyAnimator.BoundsPadding = Mathf.Max(0f, boundsPadding);
                m_proxyRoot = proxyRoot;
                EditorUtility.SetDirty(proxyAnimator);
                proxyAnimator.RebuildBindings();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Use Proxy Mesh Animation", GUILayout.Height(28f)))
                {
                    Undo.RecordObject(m_renderer, "Use Proxy Mesh Animation");
                    m_renderer.DeformationSource = GsplatRenderer.GsplatDeformationSource.ProxyMesh;
                    EditorUtility.SetDirty(m_renderer);
                    proxyAnimator.RebuildBindings();
                    m_status = "Live proxy-mesh animation is active.";
                }
                if (GUILayout.Button("Rebuild Proxy Bindings", GUILayout.Height(28f)))
                {
                    proxyAnimator.RebuildBindings();
                    m_status = "Rebuilt live proxy bindings from the original mesh bind pose.";
                }
            }

            if (!proxyAnimator.ProxyRoot)
                EditorGUILayout.HelpBox("Assign a Proxy Mesh Root.", MessageType.Warning);
            else if (proxyAnimator.ProxyRoot.GetComponentsInChildren<Renderer>(true).Length == 0)
                EditorGUILayout.HelpBox("The proxy root contains no supported mesh renderers.",
                    MessageType.Warning);
        }

        void DrawBoneSection()
        {
            EditorGUILayout.LabelField("Armature", EditorStyles.boldLabel);
            GsplatRig rig = Rig;
            bool canEdit = rig && rig.ArmatureRoot && !Application.isPlaying;
            using (new EditorGUI.DisabledScope(!canEdit))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add Root Bone"))
                        BeginBonePlacement(rig.ArmatureRoot);

                    Transform selected = rig?.ResolveRigBone(m_selectedBone);
                    using (new EditorGUI.DisabledScope(!selected))
                    {
                        if (GUILayout.Button("Extrude Child Bone"))
                            BeginBonePlacement(selected);
                    }
                }
            }

            if (m_placingBone)
                EditorGUILayout.HelpBox(
                    "Click a Gaussian in the Scene view to place the new bone. Press Esc to cancel.",
                    MessageType.Info);

            GsplatRigAsset asset = RigAsset;
            if (!asset || asset.BoneCount <= 1)
            {
                EditorGUILayout.HelpBox(
                    "Add bones, then Sync Bone Groups. Bone 0 is an internal unassigned group and is not shown.",
                    MessageType.None);
                return;
            }

            m_boneSearch = EditorGUILayout.TextField("Search", m_boneSearch);
            m_boneScroll = EditorGUILayout.BeginScrollView(m_boneScroll,
                GUILayout.MinHeight(110f), GUILayout.MaxHeight(210f));
            for (int i = 1; i < asset.BoneCount; ++i)
            {
                string boneName = i < asset.BoneNames.Count ? asset.BoneNames[i] : $"Bone {i}";
                string path = asset.BonePaths[i];
                if (!string.IsNullOrWhiteSpace(m_boneSearch) &&
                    boneName.IndexOf(m_boneSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf(m_boneSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool selected = GUILayout.Toggle(m_selectedBone == i, boneName,
                        "Button", GUILayout.Height(20f));
                    if (selected && m_selectedBone != i)
                    {
                        m_selectedBone = i;
                        UpdateCoverage();
                        SceneView.RepaintAll();
                    }
                    if (GUILayout.Button("◎", GUILayout.Width(28f)))
                        FocusBone(i);
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Weighted Gaussians", m_affectedSplats.ToString("N0"));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select Bone"))
                    FocusBone(m_selectedBone);
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    if (GUILayout.Button("Delete Bone"))
                        DeleteSelectedBone();
                }
            }
        }

        void DrawPaintSection()
        {
            EditorGUILayout.LabelField("Weight Paint", EditorStyles.boldLabel);
            bool canPaint = RigAsset && RigAsset.BoneCount > 1 && !Application.isPlaying;
            using (new EditorGUI.DisabledScope(!canPaint))
            {
                m_paintEnabled = GUILayout.Toggle(m_paintEnabled,
                    m_paintEnabled ? "Weight Paint Active" : "Enter Weight Paint",
                    "Button", GUILayout.Height(30f));
                m_paintMode = (PaintMode)EditorGUILayout.EnumPopup("Blend", m_paintMode);
                m_brushRadius = EditorGUILayout.Slider("Radius", m_brushRadius, 4f, 200f);
                m_strength = EditorGUILayout.Slider("Strength", m_strength, 0.01f, 1f);
                using (new EditorGUI.DisabledScope(m_paintMode != PaintMode.Draw))
                    m_targetWeight = EditorGUILayout.Slider("Weight", m_targetWeight, 0f, 1f);
                m_xray = EditorGUILayout.ToggleLeft("X-Ray (paint through the splat)", m_xray);
                m_showWeightOverlay = EditorGUILayout.ToggleLeft(
                    "Show selected-bone weights", m_showWeightOverlay);

                if (GUILayout.Button("Normalize All Weights"))
                {
                    Undo.RegisterCompleteObjectUndo(RigAsset, "Normalize Gaussian Weights");
                    RigAsset.NormalizeAll();
                    CommitAssetChange("Normalized all Gaussian weights.");
                }
            }

            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Exit Play mode before editing the rig.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox(
                    "Paint in the Scene view. Shift subtracts; Ctrl/Cmd smooths. " +
                    "Use [ and ] to resize the brush. Weight colors run blue → cyan → yellow → red.",
                    MessageType.None);
        }

        void CreateArmatureRoot()
        {
            GsplatRig rig = Rig;
            if (!rig || !m_renderer)
                return;
            var rootObject = new GameObject("Gaussian Armature");
            Undo.RegisterCreatedObjectUndo(rootObject, "Create Gaussian Armature");
            Undo.SetTransformParent(rootObject.transform, m_renderer.transform,
                "Parent Gaussian Armature");
            rootObject.transform.localPosition = Vector3.zero;
            rootObject.transform.localRotation = Quaternion.identity;
            rootObject.transform.localScale = Vector3.one;
            Undo.RecordObject(rig, "Assign Gaussian Armature");
            rig.ArmatureRoot = rootObject.transform;
            rig.RebuildBindings();
            EditorUtility.SetDirty(rig);
            Undo.RecordObject(m_renderer, "Use Manual Gaussian Rig");
            m_renderer.DeformationSource = GsplatRenderer.GsplatDeformationSource.ManualRig;
            EditorUtility.SetDirty(m_renderer);
            Selection.activeGameObject = rootObject;
            m_status = "Armature created. Add a root bone, then create the rig asset.";
            SceneView.RepaintAll();
        }

        bool CanCreateRig(out string reason)
        {
            if (!m_renderer || !m_renderer.GsplatAsset)
                reason = "Assign a Gaussian renderer and asset.";
            else if (!Rig || !Rig.ArmatureRoot)
                reason = "Create or assign an Armature Root.";
            else
            {
                reason = null;
                return true;
            }
            return false;
        }

        static Transform FindSuggestedProxyRoot(GsplatRenderer renderer)
        {
            if (!renderer)
                return null;
            Renderer proxyRenderer = renderer.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (!proxyRenderer)
                proxyRenderer = renderer.GetComponentInChildren<MeshRenderer>(true);
            if (!proxyRenderer)
                return null;
            Transform candidate = proxyRenderer.transform;
            while (candidate.parent && candidate.parent != renderer.transform)
                candidate = candidate.parent;
            return candidate;
        }

        void CreateRigAsset()
        {
            if (!CanCreateRig(out string reason))
            {
                EditorUtility.DisplayDialog("Gaussian Rigging", reason, "OK");
                return;
            }

            string defaultName = $"{m_renderer.GsplatAsset.name} Rig.asset";
            string path = EditorUtility.SaveFilePanelInProject("Save Gaussian Rig",
                defaultName, "asset", "Choose where the painted Gaussian rig will be saved.");
            if (string.IsNullOrEmpty(path))
                return;

            var asset = CreateInstance<GsplatRigAsset>();
            asset.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(asset, path);
            Undo.RegisterCreatedObjectUndo(asset, "Create Gaussian Rig");
            Undo.RecordObject(Rig, "Assign Gaussian Rig");
            Rig.RigAsset = asset;
            Undo.RecordObject(m_renderer, "Use Manual Gaussian Rig");
            m_renderer.DeformationSource = GsplatRenderer.GsplatDeformationSource.ManualRig;
            EditorUtility.SetDirty(m_renderer);
            SynchronizeRigAsset(true, $"Created '{asset.name}'. Add or select a bone and paint its weights.");
            Selection.activeGameObject = m_renderer.gameObject;
        }

        void SynchronizeRigAsset(bool updateBindPose, string status)
        {
            GsplatRig rig = Rig;
            GsplatRigAsset asset = RigAsset;
            if (!rig || !asset || !rig.ArmatureRoot || !m_renderer.GsplatAsset)
                return;

            Undo.RegisterCompleteObjectUndo(asset,
                updateBindPose ? "Set Gaussian Bind Pose" : "Sync Gaussian Bones");

            string[] oldPaths = asset.CopyBonePaths();
            Matrix4x4[] oldBind = asset.CopyBindTransforms();
            GsplatRigWeight[] oldWeights = asset.CopyWeights();
            var oldIndexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 1; i < oldPaths.Length; ++i)
                if (!string.IsNullOrEmpty(oldPaths[i]))
                    oldIndexByPath[oldPaths[i]] = i;

            Transform[] bones = GetArmatureBones(rig.ArmatureRoot);
            var paths = new string[bones.Length + 1];
            var names = new string[bones.Length + 1];
            var bind = new Matrix4x4[bones.Length + 1];
            paths[0] = string.Empty;
            names[0] = "Unassigned";
            bind[0] = Matrix4x4.identity;
            var newIndexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < bones.Length; ++i)
            {
                int paletteIndex = i + 1;
                string bonePath = GetRelativePath(rig.ArmatureRoot, bones[i]);
                paths[paletteIndex] = bonePath;
                names[paletteIndex] = bones[i].name;
                newIndexByPath[bonePath] = paletteIndex;
                if (!updateBindPose && oldIndexByPath.TryGetValue(bonePath, out int oldIndex) &&
                    oldIndex < oldBind.Length)
                    bind[paletteIndex] = oldBind[oldIndex];
                else
                    bind[paletteIndex] = bones[i].worldToLocalMatrix *
                                         m_renderer.transform.localToWorldMatrix;
            }

            int splatCount = checked((int)m_renderer.GsplatAsset.SplatCount);
            var weights = new GsplatRigWeight[splatCount];
            bool preserve = oldWeights.Length == splatCount && oldPaths.Length > 0;
            for (int i = 0; i < splatCount; ++i)
                weights[i] = preserve
                    ? RemapWeight(oldWeights[i], oldPaths, newIndexByPath)
                    : new GsplatRigWeight { BoneIndex0 = 0, Weight0 = 1f };

            asset.SetData(m_renderer.GsplatAsset, paths, names, bind, weights);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            rig.RebuildBindings();
            EditorUtility.SetDirty(rig);
            ClampSelectedBone();
            RefreshCenters();
            UpdateCoverage();
            m_status = status;
            SceneView.RepaintAll();
            Repaint();
        }

        void SetCurrentBindPose()
        {
            GsplatRig rig = Rig;
            GsplatRigAsset asset = RigAsset;
            if (!rig || !asset || !rig.ArmatureRoot || !m_renderer.GsplatAsset)
                return;

            string[] paths = asset.CopyBonePaths();
            string[] names = asset.CopyBoneNames();
            Matrix4x4[] bind = asset.CopyBindTransforms();
            GsplatRigWeight[] weights = asset.CopyWeights();
            if (paths.Length != bind.Length)
                return;

            Undo.RegisterCompleteObjectUndo(asset, "Set Gaussian Bind Pose");
            bind[0] = Matrix4x4.identity;
            for (int i = 1; i < paths.Length; ++i)
            {
                Transform bone = rig.ArmatureRoot.Find(paths[i]);
                if (!bone)
                {
                    EditorUtility.DisplayDialog("Gaussian Bind Pose",
                        $"Bone '{paths[i]}' cannot be found under '{rig.ArmatureRoot.name}'.", "OK");
                    return;
                }
                bind[i] = bone.worldToLocalMatrix * m_renderer.transform.localToWorldMatrix;
            }

            asset.SetData(m_renderer.GsplatAsset, paths, names, bind, weights);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            rig.RebuildBindings();
            m_status = "Stored the current skeleton pose as the Gaussian bind pose.";
            SceneView.RepaintAll();
            Repaint();
        }

        static GsplatRigWeight RemapWeight(GsplatRigWeight source, string[] oldPaths,
            Dictionary<string, int> newIndexByPath)
        {
            int Remap(int oldIndex)
            {
                if (oldIndex <= 0 || oldIndex >= oldPaths.Length)
                    return 0;
                return newIndexByPath.TryGetValue(oldPaths[oldIndex], out int newIndex)
                    ? newIndex
                    : 0;
            }

            return GsplatRigWeight.FromInfluences(
                Remap(source.BoneIndex0), source.Weight0,
                Remap(source.BoneIndex1), source.Weight1,
                Remap(source.BoneIndex2), source.Weight2,
                Remap(source.BoneIndex3), source.Weight3);
        }

        static Transform[] GetArmatureBones(Transform root)
        {
            if (!root)
                return Array.Empty<Transform>();
            Transform[] hierarchy = root.GetComponentsInChildren<Transform>(true);
            if (hierarchy.Length <= 1)
                return Array.Empty<Transform>();
            var bones = new Transform[hierarchy.Length - 1];
            Array.Copy(hierarchy, 1, bones, 0, bones.Length);
            return bones;
        }

        static string GetRelativePath(Transform root, Transform child)
        {
            var parts = new List<string>();
            for (Transform current = child; current && current != root; current = current.parent)
                parts.Add(current.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        void BeginBonePlacement(Transform parent)
        {
            if (!parent || !m_renderer || !m_renderer.GsplatAsset)
                return;
            m_paintEnabled = false;
            m_placingBone = true;
            m_placementParent = parent;
            RefreshCenters();
            m_status = parent == Rig.ArmatureRoot
                ? "Click a Gaussian to place a root bone."
                : $"Click a Gaussian to extrude a child from '{parent.name}'.";
            UpdateInteractionState();
            SceneView.RepaintAll();
        }

        void PlaceBone(Vector3 worldPosition)
        {
            if (!m_placementParent || !Rig)
                return;
            string boneName = GameObjectUtility.GetUniqueNameForSibling(
                m_placementParent, "Bone");
            var boneObject = new GameObject(boneName);
            Undo.RegisterCreatedObjectUndo(boneObject, "Add Gaussian Bone");
            Undo.SetTransformParent(boneObject.transform, m_placementParent,
                "Parent Gaussian Bone");
            boneObject.transform.position = worldPosition;
            boneObject.transform.localRotation = Quaternion.identity;
            boneObject.transform.localScale = Vector3.one;
            m_placingBone = false;
            m_placementParent = null;

            if (RigAsset)
            {
                SynchronizeRigAsset(false, $"Added bone '{boneName}'.");
                string path = GetRelativePath(Rig.ArmatureRoot, boneObject.transform);
                for (int i = 1; i < RigAsset.BoneCount; ++i)
                    if (RigAsset.BonePaths[i] == path)
                    {
                        m_selectedBone = i;
                        break;
                    }
            }
            else
                m_status = $"Added bone '{boneName}'. Create a rig asset to make it paintable.";

            Selection.activeGameObject = boneObject;
            UpdateCoverage();
            UpdateInteractionState();
            SceneView.RepaintAll();
            Repaint();
        }

        void DeleteSelectedBone()
        {
            Transform bone = Rig?.ResolveRigBone(m_selectedBone);
            if (!bone)
                return;
            if (!EditorUtility.DisplayDialog("Delete Gaussian Bone",
                    $"Delete '{bone.name}' and all of its child bones? Their weights will become unassigned.",
                    "Delete", "Cancel"))
                return;
            string boneName = bone.name;
            Undo.DestroyObjectImmediate(bone.gameObject);
            SynchronizeRigAsset(false, $"Deleted bone '{boneName}'.");
        }

        void FocusBone(int boneIndex)
        {
            Transform bone = Rig?.ResolveRigBone(boneIndex);
            if (!bone)
                return;
            Selection.activeTransform = bone;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        void ClampSelectedBone()
        {
            GsplatRigAsset asset = RigAsset;
            m_selectedBone = asset && asset.BoneCount > 1
                ? Mathf.Clamp(m_selectedBone, 1, asset.BoneCount - 1)
                : 1;
        }

        void DuringSceneGui(SceneView sceneView)
        {
            UpdateInteractionState();
            if (!m_renderer || !sceneView || !sceneView.camera)
                return;

            RefreshCenters();
            Event current = Event.current;
            if (current == null)
                return;

            int controlId = GUIUtility.GetControlID(k_sceneControlHash, FocusType.Passive);
            bool interacting = m_placingBone || IsPaintReady();
            if (current.type == EventType.Layout && interacting && !current.alt)
                HandleUtility.AddDefaultControl(controlId);

            if (current.type == EventType.Repaint)
            {
                DrawArmatureOverlay();
                if (m_placingBone)
                {
                    if (TryFindNearestSplat(sceneView.camera, current.mousePosition,
                            out Vector3 placement))
                        DrawPlacementPreview(placement);
                }
                else if (IsPaintReady())
                    DrawPaintOverlay(sceneView, current.mousePosition);
            }

            if (m_placingBone)
            {
                if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
                {
                    m_placingBone = false;
                    m_placementParent = null;
                    m_status = "Bone placement cancelled.";
                    current.Use();
                    UpdateInteractionState();
                    Repaint();
                }
                else if (!current.alt && current.button == 0 && current.type == EventType.MouseDown &&
                         TryFindNearestSplat(sceneView.camera, current.mousePosition,
                             out Vector3 placement))
                {
                    PlaceBone(placement);
                    current.Use();
                }
                return;
            }

            if (!IsPaintReady())
                return;
            HandleBrushShortcuts(current);
            if (current.alt || current.button != 0)
                return;
            if (current.type == EventType.MouseDown)
            {
                GUIUtility.hotControl = controlId;
                m_strokeActive = true;
                Undo.RegisterCompleteObjectUndo(RigAsset, "Paint Gaussian Weights");
                PaintAt(sceneView, current.mousePosition, current);
                current.Use();
            }
            else if (current.type == EventType.MouseDrag &&
                     m_strokeActive && GUIUtility.hotControl == controlId)
            {
                PaintAt(sceneView, current.mousePosition, current);
                current.Use();
            }
            else if (current.type == EventType.MouseUp && m_strokeActive)
            {
                m_strokeActive = false;
                if (GUIUtility.hotControl == controlId)
                    GUIUtility.hotControl = 0;
                CommitAssetChange("Painted Gaussian weights.");
                current.Use();
            }
        }

        bool TryFindNearestSplat(Camera camera, Vector2 mousePosition, out Vector3 worldPosition)
        {
            worldPosition = default;
            if (m_centers.Length == 0)
                return false;
            float bestDistance = float.PositiveInfinity;
            Matrix4x4 localToWorld = m_renderer.transform.localToWorldMatrix;
            for (int i = 0; i < m_centers.Length; ++i)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(m_centers[i]);
                if (camera.WorldToViewportPoint(world).z <= 0f)
                    continue;
                float distance = (HandleUtility.WorldToGUIPoint(world) - mousePosition).sqrMagnitude;
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                worldPosition = world;
            }
            return bestDistance <= 35f * 35f;
        }

        static void DrawPlacementPreview(Vector3 position)
        {
            Handles.color = new Color(0.2f, 1f, 0.35f, 1f);
            float size = HandleUtility.GetHandleSize(position) * 0.12f;
            Handles.SphereHandleCap(0, position, Quaternion.identity, size, EventType.Repaint);
        }

        void DrawArmatureOverlay()
        {
            GsplatRig rig = Rig;
            if (!rig || !rig.ArmatureRoot)
                return;
            Transform selected = rig.ResolveRigBone(m_selectedBone);
            var displayedBones = new List<Transform>();
            if (RigAsset && RigAsset.BoneCount > 1)
            {
                for (int i = 1; i < RigAsset.BoneCount; ++i)
                {
                    Transform bone = rig.ResolveRigBone(i);
                    if (bone)
                        displayedBones.Add(bone);
                }
            }
            else
                displayedBones.AddRange(GetArmatureBones(rig.ArmatureRoot));

            foreach (Transform bone in displayedBones)
            {
                bool isSelected = bone == selected;
                Handles.color = isSelected
                    ? new Color(1f, 0.55f, 0.05f, 1f)
                    : new Color(0.95f, 0.72f, 0.25f, 0.7f);
                float size = HandleUtility.GetHandleSize(bone.position) * (isSelected ? 0.09f : 0.06f);
                Handles.SphereHandleCap(0, bone.position, Quaternion.identity, size,
                    EventType.Repaint);
                if (bone.parent && bone.parent != rig.ArmatureRoot)
                    Handles.DrawAAPolyLine(isSelected ? 4f : 2f,
                        bone.position, bone.parent.position);
                if (isSelected)
                    Handles.Label(bone.position, $"  {bone.name}");
            }

            if (m_placingBone && m_placementParent && m_placementParent != rig.ArmatureRoot)
            {
                Handles.color = new Color(0.2f, 1f, 0.35f, 0.85f);
                Handles.Label(m_placementParent.position, $"  Extruding from {m_placementParent.name}");
            }
        }

        void HandleBrushShortcuts(Event current)
        {
            if (current.type != EventType.KeyDown)
                return;
            if (current.keyCode == KeyCode.LeftBracket)
            {
                m_brushRadius = Mathf.Max(4f, m_brushRadius - 5f);
                current.Use();
                Repaint();
            }
            else if (current.keyCode == KeyCode.RightBracket)
            {
                m_brushRadius = Mathf.Min(200f, m_brushRadius + 5f);
                current.Use();
                Repaint();
            }
        }

        void PaintAt(SceneView sceneView, Vector2 mousePosition, Event current)
        {
            CollectBrushHits(sceneView.camera, mousePosition);
            if (m_brushHits.Count == 0)
                return;

            PaintMode mode = current.control || current.command
                ? PaintMode.Smooth
                : current.shift ? PaintMode.Subtract : m_paintMode;
            float smoothTarget = 0f;
            if (mode == PaintMode.Smooth)
            {
                foreach (BrushHit hit in m_brushHits)
                    smoothTarget += RigAsset.GetBoneWeight(hit.Index, m_selectedBone);
                smoothTarget /= m_brushHits.Count;
            }

            int changed = 0;
            foreach (BrushHit hit in m_brushHits)
            {
                float falloff = 1f - Mathf.SmoothStep(0f, 1f, hit.Radius01);
                float influence = Mathf.Clamp01(m_strength * falloff);
                float currentWeight = RigAsset.GetBoneWeight(hit.Index, m_selectedBone);
                float next = mode switch
                {
                    PaintMode.Draw => Mathf.Lerp(currentWeight, m_targetWeight, influence),
                    PaintMode.Add => currentWeight + (1f - currentWeight) * influence,
                    PaintMode.Subtract => currentWeight * (1f - influence),
                    PaintMode.Smooth => Mathf.Lerp(currentWeight, smoothTarget, influence),
                    _ => currentWeight
                };
                if (Mathf.Abs(next - currentWeight) <= 1e-5f)
                    continue;
                RigAsset.SetBoneWeight(hit.Index, m_selectedBone, next, true);
                changed++;
            }
            if (changed == 0)
                return;
            EditorUtility.SetDirty(RigAsset);
            m_status = $"Painted {changed:N0} Gaussians ({mode}).";
            sceneView.Repaint();
            Repaint();
        }

        void CollectBrushHits(Camera camera, Vector2 mousePosition)
        {
            m_brushHits.Clear();
            m_nearestDepthByCell.Clear();
            float radiusSquared = m_brushRadius * m_brushRadius;
            Matrix4x4 localToWorld = m_renderer.transform.localToWorldMatrix;
            for (int i = 0; i < m_centers.Length; ++i)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(m_centers[i]);
                Vector3 viewport = camera.WorldToViewportPoint(world);
                if (viewport.z <= 0f)
                    continue;
                Vector2 gui = HandleUtility.WorldToGUIPoint(world);
                float distanceSquared = (gui - mousePosition).sqrMagnitude;
                if (distanceSquared > radiusSquared)
                    continue;
                int cellX = Mathf.FloorToInt(gui.x / 6f);
                int cellY = Mathf.FloorToInt(gui.y / 6f);
                long cell = ((long)cellX << 32) ^ (uint)cellY;
                float radius01 = Mathf.Sqrt(distanceSquared) / m_brushRadius;
                m_brushHits.Add(new BrushHit(i, radius01, viewport.z, cell));
                if (!m_nearestDepthByCell.TryGetValue(cell, out float depth) || viewport.z < depth)
                    m_nearestDepthByCell[cell] = viewport.z;
            }

            if (m_xray || m_brushHits.Count == 0)
                return;
            float worldDiagonal = m_renderer.transform.TransformVector(
                m_renderer.GsplatAsset.Bounds.size).magnitude;
            float depthTolerance = Mathf.Max(0.0001f, worldDiagonal * k_frontDepthFraction);
            int write = 0;
            for (int i = 0; i < m_brushHits.Count; ++i)
            {
                BrushHit hit = m_brushHits[i];
                if (hit.Depth > m_nearestDepthByCell[hit.Cell] + depthTolerance)
                    continue;
                m_brushHits[write++] = hit;
            }
            if (write < m_brushHits.Count)
                m_brushHits.RemoveRange(write, m_brushHits.Count - write);
        }

        void DrawPaintOverlay(SceneView sceneView, Vector2 mousePosition)
        {
            GsplatRigAsset asset = RigAsset;
            Handles.BeginGUI();
            if (m_showWeightOverlay)
            {
                int step = Mathf.Max(1, m_centers.Length / k_overlayPointLimit);
                Matrix4x4 localToWorld = m_renderer.transform.localToWorldMatrix;
                for (int i = 0; i < m_centers.Length; i += step)
                {
                    float weight = asset.GetBoneWeight(i, m_selectedBone);
                    Vector3 world = localToWorld.MultiplyPoint3x4(m_centers[i]);
                    if (sceneView.camera.WorldToViewportPoint(world).z <= 0f)
                        continue;
                    Vector2 gui = HandleUtility.WorldToGUIPoint(world);
                    EditorGUI.DrawRect(new Rect(gui.x - 1.5f, gui.y - 1.5f, 3f, 3f),
                        WeightColor(weight));
                }
            }

            var circle = new Vector3[49];
            for (int i = 0; i < circle.Length; ++i)
            {
                float angle = i * Mathf.PI * 2f / (circle.Length - 1);
                circle[i] = mousePosition + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) *
                    m_brushRadius;
            }
            Handles.color = Color.white;
            Handles.DrawAAPolyLine(2f, circle);
            Handles.EndGUI();
        }

        static Color WeightColor(float weight)
        {
            weight = Mathf.Clamp01(weight);
            if (weight < 0.33f)
                return Color.Lerp(new Color(0.05f, 0.1f, 0.8f), Color.cyan, weight / 0.33f);
            if (weight < 0.66f)
                return Color.Lerp(Color.cyan, Color.yellow, (weight - 0.33f) / 0.33f);
            return Color.Lerp(Color.yellow, Color.red, (weight - 0.66f) / 0.34f);
        }

        void RefreshCenters()
        {
            if (!m_renderer || !m_renderer.GsplatAsset)
            {
                m_cachedCenterAsset = null;
                m_centers = Array.Empty<Vector3>();
                return;
            }
            if (m_cachedCenterAsset == m_renderer.GsplatAsset &&
                m_centers.Length == m_renderer.GsplatAsset.SplatCount)
                return;
            m_cachedCenterAsset = m_renderer.GsplatAsset;
            try
            {
                m_centers = GsplatRig.GetSplatCenters(m_cachedCenterAsset);
            }
            catch (Exception exception)
            {
                m_centers = Array.Empty<Vector3>();
                m_status = exception.Message;
            }
        }

        void UpdateCoverage()
        {
            GsplatRigAsset asset = RigAsset;
            if (!asset || m_selectedBone <= 0 || m_selectedBone >= asset.BoneCount)
            {
                m_affectedSplats = 0;
                return;
            }
            int count = 0;
            for (int i = 0; i < asset.SplatCount; ++i)
                if (asset.GetBoneWeight(i, m_selectedBone) > 0.001f)
                    count++;
            m_affectedSplats = count;
        }

        void CommitAssetChange(string status)
        {
            if (!RigAsset)
                return;
            EditorUtility.SetDirty(RigAsset);
            AssetDatabase.SaveAssetIfDirty(RigAsset);
            Rig?.RebuildBindings();
            UpdateCoverage();
            m_status = status;
            SceneView.RepaintAll();
            Repaint();
        }

        void OnUndoRedo()
        {
            Rig?.RebuildBindings();
            ClampSelectedBone();
            UpdateCoverage();
            SceneView.RepaintAll();
            Repaint();
        }

        bool IsPaintReady() => m_paintEnabled && !Application.isPlaying &&
                               m_renderer && RigAsset && m_selectedBone > 0 &&
                               m_selectedBone < RigAsset.BoneCount && m_centers.Length > 0;

        void UpdateInteractionState()
        {
            if (Application.isPlaying || !RigAsset || RigAsset.BoneCount <= 1)
                m_paintEnabled = false;
            IsSceneRiggingActive = m_placingBone || IsPaintReady();
        }
    }
}
