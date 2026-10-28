using UnityEditor;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Draggable waypoints for <see cref="PathFollower"/>, plus add and remove in the Scene view.
    /// </summary>
    [CustomEditor(typeof(PathFollower))]
    public sealed class PathFollowerEditor : UnityEditor.Editor
    {
        const float PickSize = 0.05f;
        int _selected = -1;

        void OnSceneGUI()
        {
            var follower = (PathFollower)target;
            var points = follower.Points;
            if (points == null) return;

            // Same frame the follower evaluates the route in, or the handles sit somewhere the
            // path is not.
            Transform frame = follower.Frame;
            Handles.matrix = frame != null ? frame.localToWorldMatrix : Matrix4x4.identity;

            for (int i = 0; i < points.Count; i++)
            {
                // Handle size in world units, so a point stays grabbable whatever the zoom.
                float size = HandleUtility.GetHandleSize(points[i]) * PickSize;

                Handles.color = i == _selected ? Color.yellow : new Color(0.4f, 0.85f, 1f);
                if (Handles.Button(points[i], Quaternion.identity, size, size, Handles.DotHandleCap))
                {
                    _selected = i;
                    Repaint();
                }

                Handles.Label(points[i] + Vector3.up * size * 2f, i.ToString());

                if (i != _selected) continue;

                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(points[i], Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(follower, "Move Path Point");
                    points[i] = moved;
                    follower.Rebuild();
                    EditorUtility.SetDirty(follower);
                }
            }

            Handles.matrix = Matrix4x4.identity;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var follower = (PathFollower)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Route", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"{follower.Points.Count} points, {follower.Length:F2} units around.\n" +
                "Click a point in the Scene view to select it, then drag its handle. The route " +
                "always closes back to the first point.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add point"))
                {
                    Undo.RecordObject(follower, "Add Path Point");
                    var points = follower.Points;
                    if (points.Count == 0) points.Add(Vector3.zero);
                    // Halfway along the segment after the selected point, which is where someone
                    // reaching for "add" almost always means.
                    int at = _selected >= 0 && _selected < points.Count ? _selected : points.Count - 1;
                    int next = (at + 1) % points.Count;
                    points.Insert(at + 1, points.Count > 0
                        ? Vector3.Lerp(points[at], points[next], 0.5f)
                        : Vector3.zero);
                    _selected = at + 1;
                    follower.Rebuild();
                    EditorUtility.SetDirty(follower);
                }

                using (new EditorGUI.DisabledScope(_selected < 0 || follower.Points.Count <= 3))
                {
                    if (GUILayout.Button("Remove selected"))
                    {
                        Undo.RecordObject(follower, "Remove Path Point");
                        follower.Points.RemoveAt(_selected);
                        _selected = -1;
                        follower.Rebuild();
                        EditorUtility.SetDirty(follower);
                    }
                }
            }

            if (follower.Points.Count <= 3)
            {
                EditorGUILayout.HelpBox(
                    "Three points is the fewest a closed spline can be built from.",
                    MessageType.None);
            }
        }
    }
}
