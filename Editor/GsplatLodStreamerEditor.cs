// Keep the managed tile-source component editable through its grid root Inspector.
// SPDX-License-Identifier: MIT

using Gsplat;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Gsplat.Editor
{
    [CustomEditor(typeof(GsplatLodStreamer))]
    public sealed class GsplatLodStreamerEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            GsplatLodStreamer streamer = (GsplatLodStreamer)target;
            GsplatLodTileGrid owner = FindGridOwner(streamer);
            if (owner)
            {
                root.Add(new HelpBox(
                    $"This child is managed by '{owner.name}'. Select the grid root to edit its LOD, budget, source and rendering settings.",
                    HelpBoxMessageType.Info));
                var selectOwner = new Button(() => Selection.activeGameObject = owner.gameObject)
                {
                    text = "Select grid root"
                };
                root.Add(selectOwner);
                return root;
            }

            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            return root;
        }

        static GsplatLodTileGrid FindGridOwner(GsplatLodStreamer streamer)
        {
            for (Transform current = streamer.transform; current; current = current.parent)
            {
                GsplatLodTileGrid grid = current.GetComponent<GsplatLodTileGrid>();
                if (grid && grid.TileTemplate == streamer) return grid;
            }

            foreach (GsplatLodTileGrid grid in Resources.FindObjectsOfTypeAll<GsplatLodTileGrid>())
                if (grid && grid.TileTemplate == streamer) return grid;

            return null;
        }
    }
}
