using Gsplat;
using UnityEditor;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>Shared by the Splat LOD inspector and the counts window.</summary>
    static class SplatCounts
    {
        public static void Draw(GsplatRenderer renderer)
        {
            if (!renderer || !renderer.GsplatAsset)
            {
                EditorGUILayout.HelpBox("No splat selected.", MessageType.Info);
                return;
            }

            uint total = renderer.GsplatAsset.SplatCount;
            uint uploaded = renderer.SplatCount;
            uint visible = renderer.ResidentActiveSplatCount;
            EditorGUILayout.LabelField("In asset", total.ToString("N0"));
            EditorGUILayout.LabelField("Uploaded", $"{uploaded:N0}  ({Percent(uploaded, total)})");
            // From the last cutout or LOD pass, so it can lag by up to the cutout refresh rate.
            EditorGUILayout.LabelField("After cutouts / LOD", $"{visible:N0}  ({Percent(visible, uploaded)} of uploaded)");
            EditorGUILayout.LabelField("Cutouts", renderer.Cutouts.Length.ToString());
            if (renderer.HasActiveRanges)
                EditorGUILayout.LabelField("Selected by ranges", $"{renderer.ActiveSplatCount:N0} in {renderer.ActiveRanges.Count:N0} range(s)");
            if (uploaded > 0 && visible == 0)
                EditorGUILayout.HelpBox("Everything is removed. Check the cutouts and Splat LOD settings.", MessageType.Warning);
        }

        static string Percent(uint part, uint whole) => whole == 0 ? "-" : $"{100.0 * part / whole:F1}%";
    }

    [CustomEditor(typeof(SplatLod))]
    public class SplatLodEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var lod = (SplatLod)target;
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck() && lod.Falloff != SplatLod.Preset.Custom)
                lod.ApplyPreset();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live counts", EditorStyles.boldLabel);
            SplatCounts.Draw(lod.ResolveRenderer());
            if (GUILayout.Button("Recompute now"))
                lod.Refresh();
        }

        public override bool RequiresConstantRepaint() => true;
    }

    /// <summary>Uploaded / visible / after-cutout counts for the selected splat.</summary>
    public sealed class SplatCountsWindow : EditorWindow
    {
        [MenuItem("Tools/StageSettAR/Splat Counts", false, 61)]
        static void Open() => GetWindow<SplatCountsWindow>("Splat Counts");

        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            var selected = Selection.activeGameObject;
            var renderer = selected ? selected.GetComponentInChildren<GsplatRenderer>() : null;
            if (!renderer && selected)
                renderer = selected.GetComponentInParent<GsplatRenderer>();
            SplatCounts.Draw(renderer);
        }
    }
}
