// Forest tile-grid Inspector: one place to configure the prefab and its managed source.
// SPDX-License-Identifier: MIT

using Gsplat;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Gsplat.Editor
{
    [CustomEditor(typeof(GsplatLodTileGrid))]
    public sealed class GsplatLodTileGridEditor : UnityEditor.Editor
    {
        Foldout m_tileDataFoldout;
        GsplatLodStreamer m_currentTemplate;

        public override VisualElement CreateInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();

            var root = new VisualElement();
            root.Add(new HelpBox(
                "Place this prefab once. The grid controls shared layout, LOD and budgets; the child tile source supplies the manifest and rendering settings.",
                HelpBoxMessageType.Info));

            AddFoldout(root, "Grid", serializedObject, true,
                nameof(GsplatLodTileGrid.GridSize),
                nameof(GsplatLodTileGrid.TileSpacingMeters),
                nameof(GsplatLodTileGrid.FollowCameraOnXZ),
                nameof(GsplatLodTileGrid.GridCamera),
                nameof(GsplatLodTileGrid.TileTemplate));

            AddFoldout(root, "Shared budgets", serializedObject, true,
                nameof(GsplatLodTileGrid.TotalActiveSplatBudget),
                nameof(GsplatLodTileGrid.TotalEstimatedResidentGpuMB),
                nameof(GsplatLodTileGrid.CacheCooldownSeconds));

            AddFoldout(root, "LOD quality", serializedObject, true,
                nameof(GsplatLodTileGrid.LodBaseDistance),
                nameof(GsplatLodTileGrid.LodMultiplier),
                nameof(GsplatLodTileGrid.LodRangeMin),
                nameof(GsplatLodTileGrid.LodRangeMax),
                nameof(GsplatLodTileGrid.LodBehindPenalty),
                nameof(GsplatLodTileGrid.LodHysteresis),
                nameof(GsplatLodTileGrid.LodUpdateIntervalFrames),
                nameof(GsplatLodTileGrid.LodUnderfillLimit));

            AddFoldout(root, "Predictive prefetch (optional)", serializedObject, false,
                nameof(GsplatLodTileGrid.EnablePredictivePrefetch),
                nameof(GsplatLodTileGrid.PredictionHorizonSeconds),
                nameof(GsplatLodTileGrid.PredictiveLodRefinementLevels),
                nameof(GsplatLodTileGrid.PredictionFrustumPaddingMeters),
                nameof(GsplatLodTileGrid.PredictivePrefetchSplatBudgetPerTile),
                nameof(GsplatLodTileGrid.PredictiveResidentBudgetMB),
                nameof(GsplatLodTileGrid.PredictionVelocitySmoothingSeconds),
                nameof(GsplatLodTileGrid.MaxPredictionSpeedMetersPerSecond),
                nameof(GsplatLodTileGrid.MaxPredictionAngularSpeedDegreesPerSecond));

            m_tileDataFoldout = new Foldout
            {
                text = "Tile data and rendering",
                value = true,
                viewDataKey = "GsplatLodTileGrid.TileDataAndRendering"
            };
            root.Add(m_tileDataFoldout);
            RefreshTemplateFields();

            root.TrackSerializedObjectValue(serializedObject, _ =>
            {
                serializedObject.UpdateIfRequiredOrScript();
                var nextTemplate = serializedObject.FindProperty(nameof(GsplatLodTileGrid.TileTemplate))
                    ?.objectReferenceValue as GsplatLodStreamer;
                if (nextTemplate == m_currentTemplate) return;

                m_currentTemplate = nextTemplate;
                RefreshTemplateFields();
            });

            return root;
        }

        void RefreshTemplateFields()
        {
            m_tileDataFoldout.Clear();
            m_currentTemplate = serializedObject.FindProperty(nameof(GsplatLodTileGrid.TileTemplate))
                ?.objectReferenceValue as GsplatLodStreamer;
            if (!m_currentTemplate)
            {
                m_tileDataFoldout.Add(new HelpBox(
                    "Assign the managed tile-source child above to edit its data and rendering settings.",
                    HelpBoxMessageType.Warning));
                return;
            }

            var source = new SerializedObject(m_currentTemplate);
            source.UpdateIfRequiredOrScript();
            AddFields(m_tileDataFoldout, source,
                nameof(GsplatLodStreamer.ManifestAsset),
                nameof(GsplatLodStreamer.ManifestUri),
                nameof(GsplatLodStreamer.SourceCoordinates),
                nameof(GsplatLodStreamer.Compression),
                nameof(GsplatLodStreamer.MaxRetries),
                nameof(GsplatLodStreamer.RetryDelayMilliseconds),
                nameof(GsplatLodStreamer.FailureCooldownSeconds),
                nameof(GsplatLodStreamer.AsyncGpuUpload),
                nameof(GsplatLodStreamer.FrustumCulling),
                nameof(GsplatLodStreamer.FrustumPadding),
                nameof(GsplatLodStreamer.SHDegree),
                nameof(GsplatLodStreamer.Brightness),
                nameof(GsplatLodStreamer.SplatDownscaleFactor),
                nameof(GsplatLodStreamer.GammaToLinear),
                nameof(GsplatLodStreamer.RenderOrder));
            m_tileDataFoldout.Bind(source);
        }

        static void AddFoldout(VisualElement root, string title, SerializedObject source,
            bool expanded, params string[] propertyNames)
        {
            var foldout = new Foldout
            {
                text = title,
                value = expanded,
                viewDataKey = "GsplatLodTileGrid." + title.Replace(' ', '.')
            };
            AddFields(foldout, source, propertyNames);
            foldout.Bind(source);
            root.Add(foldout);
        }

        static void AddFields(VisualElement root, SerializedObject source, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                SerializedProperty property = source.FindProperty(propertyName);
                if (property != null) root.Add(new PropertyField(property));
            }
        }
    }
}
