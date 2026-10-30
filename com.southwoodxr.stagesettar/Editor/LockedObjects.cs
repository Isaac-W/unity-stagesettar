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
    /// <para>
    /// The rig's character is also recognised by its <see cref="EditorCharacter"/> component: it is a nested
    /// instance of the model prefab, and Unity does not keep a HideFlags change on a prefab instance, so its
    /// Transform comes back editable. EditorCharacter re-poses it every frame, which keeps it in place anyway.
    /// </para>
    /// <para>
    /// The rig's Localizing Screen is only kept out of Scene-view clicks (a panel floating in front of the
    /// camera would win them); its transform stays editable. Its type lives in the optional UI assembly, so it
    /// is looked up by name.
    /// </para>
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

        static readonly System.Type s_localizingScreen = System.Type.GetType("StageSettAR.LocalizingScreen, StageSettAR.UI");

        static bool IsUnpickable(Transform t) =>
            IsLocked(t) || t.TryGetComponent(out EditorCharacter _) ||
            (s_localizingScreen != null && t.GetComponent(s_localizingScreen) != null);

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
                if (IsUnpickable(t) && !visibility.IsPickingDisabled(t.gameObject, true))
                    visibility.DisablePicking(t.gameObject, true);
        }
    }
}
