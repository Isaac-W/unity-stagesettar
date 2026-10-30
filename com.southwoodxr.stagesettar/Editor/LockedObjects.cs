using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Objects the package places for reference that should be seen but not moved or clicked: everything under a
    /// map's root (splat, floor image, floor collider) except what you put in its content object, and the rig's
    /// editor character. Their transforms are greyed out and kept off the move/rotate/scale tools, and the Scene
    /// view never picks them, so clicks reach the content around them. The rig's Localizing Screen is only kept
    /// out of Scene-view clicks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applied by rule, not stored: Unity drops HideFlags when a prefab is saved, and Scene-view picking is per
    /// editor session, so neither can be baked into the map or rig prefab. Instead every open scene and prefab is
    /// scanned whenever one opens and, throttled to one scan per burst, whenever the hierarchy changes; an object
    /// that stops matching (dragged out of the map, say) is unlocked again. Locked objects stay selectable in the
    /// Hierarchy and can be deleted.
    /// </para>
    /// <para>
    /// Under a map root, the content object (<see cref="StarLocalizer.content"/>) is locked itself but its children
    /// are yours. The Localizing Screen's type lives in the optional UI assembly, so it is looked up by name.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class LockedObjects
    {
        static bool s_pending;
        static readonly System.Type s_localizingScreen = System.Type.GetType("StageSettAR.LocalizingScreen, StageSettAR.UI");

        // What the last scan locked or made unpickable, so objects that stop matching can be given back.
        static readonly HashSet<Transform> s_locked = new();
        static readonly HashSet<GameObject> s_unpickable = new();

        static LockedObjects()
        {
            EditorSceneManager.sceneOpened += (_, _) => Schedule();
            PrefabStage.prefabStageOpened += _ => Schedule();
            EditorApplication.hierarchyChanged += Schedule;
            Schedule();
        }

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
            var lockSet = new HashSet<Transform>();
            var noPick = new HashSet<GameObject>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) Collect(root, lockSet, noPick);
            }
            // The prefab stage lives in its own preview scene, outside the loop above.
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null) Collect(stage.prefabContentsRoot, lockSet, noPick);

            // Flags are compared before writing: setting them can raise hierarchyChanged, and an unchanged scan must
            // not schedule another one.
            foreach (var t in s_locked)
                if (t != null && !lockSet.Contains(t)) SetLocked(t, false);
            foreach (var t in lockSet) SetLocked(t, true);
            s_locked.Clear();
            s_locked.UnionWith(lockSet);

            var visibility = SceneVisibilityManager.instance;
            foreach (var go in s_unpickable)
                if (go != null && !noPick.Contains(go) && visibility.IsPickingDisabled(go, false)) visibility.EnablePicking(go, true);
            foreach (var go in noPick)
                if (!visibility.IsPickingDisabled(go, true)) visibility.DisablePicking(go, true);
            s_unpickable.Clear();
            s_unpickable.UnionWith(noPick);
        }

        static void Collect(GameObject root, HashSet<Transform> lockSet, HashSet<GameObject> noPick)
        {
            foreach (var loc in root.GetComponentsInChildren<StarLocalizer>(true))
            {
                var mapRoot = loc.mapRoot != null ? loc.mapRoot : loc.transform;
                var content = ToolkitContent(loc, mapRoot);
                foreach (Transform child in mapRoot)
                {
                    if (loc.transform.IsChildOf(child)) continue; // the localizer itself sits below: leave that branch alone
                    if (child == content)
                    {
                        lockSet.Add(child); // the folder stays put; what you put in it does not
                        continue;
                    }
                    foreach (var t in child.GetComponentsInChildren<Transform>(true)) lockSet.Add(t);
                    noPick.Add(child.gameObject);
                }
            }

            foreach (var character in root.GetComponentsInChildren<EditorCharacter>(true))
            {
                foreach (var t in character.GetComponentsInChildren<Transform>(true)) lockSet.Add(t);
                noPick.Add(character.gameObject);
            }

            if (s_localizingScreen != null)
                foreach (var screen in root.GetComponentsInChildren(s_localizingScreen, true))
                    noPick.Add(screen.gameObject);
        }

        /// <summary>A map's content folder: <see cref="StarLocalizer.content"/>, or for maps imported before it existed, the child named Content.</summary>
        public static Transform ToolkitContent(StarLocalizer loc, Transform mapRoot) =>
            loc.content != null ? loc.content : mapRoot.Find("Content");

        static void SetLocked(Transform t, bool locked)
        {
            var flags = locked ? t.hideFlags | HideFlags.NotEditable : t.hideFlags & ~HideFlags.NotEditable;
            if (flags != t.hideFlags) t.hideFlags = flags;
        }
    }
}
