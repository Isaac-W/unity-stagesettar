using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Objects the package places for reference (the rig's editor character, a map's splat, floor image and
    /// floor collider) that should be seen but not moved or clicked: their transform is fixed relative to
    /// their parent, and the Scene view never picks them, so clicks reach the content around them.
    /// </summary>
    /// <remarks>
    /// A locked object is marked only by its Transform being <see cref="HideFlags.NotEditable"/>, which is
    /// saved with the scene or prefab, greys out the Transform inspector and keeps the move/rotate/scale tools
    /// off it. Scene-view picking is per editor session (Scene Visibility), not saved, so it is re-applied to
    /// every such object whenever a scene or prefab opens and, throttled to one scan per burst, whenever the
    /// hierarchy changes. Locked objects stay selectable in the Hierarchy and can be deleted.
    /// </remarks>
    [InitializeOnLoad]
    public static class LockedObjects
    {
        static bool s_pending;

        static LockedObjects()
        {
            EditorSceneManager.sceneOpened += (_, _) => Schedule();
            PrefabStage.prefabStageOpened += _ => Schedule();
            EditorApplication.hierarchyChanged += Schedule;
            Schedule();
        }

        /// <summary>Fixes the object's transform where it is and keeps it out of Scene-view clicks.</summary>
        public static void Lock(GameObject go)
        {
            go.transform.hideFlags |= HideFlags.NotEditable;
            Schedule();
        }

        static bool IsLocked(Transform t) => (t.hideFlags & HideFlags.NotEditable) != 0;

        // Many hierarchy changes arrive in a burst; one scan after them is enough.
        static void Schedule()
        {
            if (s_pending) return;
            s_pending = true;
            EditorApplication.delayCall += () =>
            {
                s_pending = false;
                Apply();
            };
        }

        static void Apply()
        {
            var visibility = SceneVisibilityManager.instance;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    DisableIn(visibility, root);
            }

            // The prefab stage lives in its own preview scene, outside the loop above.
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null) DisableIn(visibility, stage.prefabContentsRoot);
        }

        // Checked first: switching picking changes the hierarchy, which would otherwise schedule another scan forever.
        static void DisableIn(SceneVisibilityManager visibility, GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (IsLocked(t) && !visibility.IsPickingDisabled(t.gameObject, true))
                    visibility.DisablePicking(t.gameObject, true);
        }
    }
}
