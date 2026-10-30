using System;
using System.Collections.Generic;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Turns the open scene into a StageSettAR scene in one go: project settings the rig needs, the other
    /// cameras and XR rigs out, the StageSettAR rig in, plus a directional light if there is none.
    /// </summary>
    /// <remarks>
    /// Reached from Tools > StageSettAR > Set Up Current Scene (a menu in the always-compiled Setup
    /// folder that finds this class by name, so a project without the XR packages gets a pointer to the
    /// Setup window instead) and from the Setup window's "Set up current scene" row. Scene changes are
    /// one Undo step and leave the scene dirty but unsaved. Project steps are run from an explicit
    /// allow-list only: anything that installs a package would reload the domain in the middle of the
    /// scene changes, so those stay in the Setup window.
    /// </remarks>
    public static class SceneSetupCommand
    {
        // Settings and renderer assets only; never a package install.
        static readonly string[] AllowedSteps = { "XrProjectSettingsStep", "UrpSplatStep" };

        const string UndoName = "Set Up StageSettAR Scene";

        /// <summary>Runs the whole command on the active scene.</summary>
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Tell("Leave play mode first."); return; }
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) { Tell("Leave prefab editing first: this sets up a scene."); return; }
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) { Tell("There is no open scene."); return; }

            // Without the rig and without the XR samples to build it from there is nothing to do; the dialog sends the user to the Setup window.
            if (!RigBuilder.RigExists && !RigBuilder.SamplesReady) { RigBuilder.EnsureRig(); return; }

            if (PackagesMissing())
            {
                int choice = EditorUtility.DisplayDialogComplex("StageSettAR",
                    "Some XR packages the rig expects are not installed. Install them from Tools > StageSettAR > Setup… first, or set the scene up anyway.",
                    "Set up anyway", "Cancel", "Open Setup");
                if (choice == 1) return;
                if (choice == 2) { SetupWindow.Open(); return; }
            }

            var removals = FindRemovals(scene);
            if (removals.Count > 0 && !EditorUtility.DisplayDialog("Set up current scene",
                    "These objects will be removed from '" + SceneName(scene) + "' (Undo brings them back):\n\n" + Describe(removals)
                    + "\n\nThe StageSettAR rig replaces them.", "Set Up", "Cancel"))
                return;

            if (!RigBuilder.EnsureRig()) return;
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigBuilder.RigPath);
            if (rigPrefab == null) { Debug.LogError("[StageSettAR] Could not load " + RigBuilder.RigPath); return; }

            var steps = RunProjectSteps();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            var group = Undo.GetCurrentGroup();

            var removed = new List<string>();
            foreach (var go in removals)
            {
                if (go == null) continue; // already gone with a parent removed before it
                removed.Add(go.name);
                Remove(go);
            }
            var added = new List<string>();
            RigBuilder.PopulateScene(scene, rigPrefab, added, true);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            var rig = FindRig(scene);
            if (rig != null) { Selection.activeGameObject = rig; EditorGUIUtility.PingObject(rig); }
            Debug.Log("[StageSettAR] Set up '" + SceneName(scene) + "'. Project settings: " + (steps.Count > 0 ? string.Join(", ", steps) : "already in order")
                + ". Removed: " + (removed.Count > 0 ? string.Join(", ", removed) : "nothing")
                + ". Added: " + (added.Count > 0 ? string.Join(", ", added) : "nothing (the scene already had everything)")
                + ". The scene is modified but not saved.");
        }

        [MenuItem("GameObject/StageSettAR/XR Rig", false, 0)]
        static void AddRigMenu()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) { Tell("There is no open scene."); return; }
            if (!RigBuilder.EnsureRig()) return;
            if (RigBuilder.HasRig(scene) && !EditorUtility.DisplayDialog("StageSettAR", "This scene already has a StageSettAR rig. Add another?", "Add", "Cancel")) return;

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigBuilder.RigPath);
            if (rigPrefab == null) return;
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
            Undo.RegisterCreatedObjectUndo(rig, "Add XR Rig");
            Selection.activeGameObject = rig;
        }

        // ---- scene analysis ----

        // Other XR rigs (the whole prefab instance if there is one, so its AR Session and Event System go too), cameras that
        // act as the main camera, and stray Event Systems. Anything inside the StageSettAR rig that is kept is left alone.
        static List<GameObject> FindRemovals(Scene scene)
        {
            var roots = scene.GetRootGameObjects();
            var origins = roots.SelectMany(r => r.GetComponentsInChildren<XROrigin>(true)).ToList();
            var keptOrigin = origins.FirstOrDefault(RigBuilder.IsRigOrigin);
            var keptRoot = keptOrigin != null ? RemovalTarget(keptOrigin.gameObject).transform : null;

            var remove = new List<GameObject>();
            foreach (var origin in origins)
            {
                if (origin == keptOrigin || (keptRoot != null && origin.transform.IsChildOf(keptRoot))) continue;
                remove.Add(RemovalTarget(origin.gameObject));
            }
            foreach (var cam in roots.SelectMany(r => r.GetComponentsInChildren<Camera>(true)))
            {
                if (keptRoot != null && cam.transform.IsChildOf(keptRoot)) continue;
                if (!cam.CompareTag("MainCamera") && cam.gameObject.name != "Main Camera") continue;
                remove.Add(cam.gameObject);
            }
            // The rig brings its own Event System with the XR UI input module; a second one would take over UI input.
            foreach (var es in roots.SelectMany(r => r.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(true)))
            {
                if (keptRoot != null && es.transform.IsChildOf(keptRoot)) continue;
                remove.Add(es.gameObject);
            }

            // Distinct, and nothing that goes anyway with an object above it.
            remove = remove.Distinct().ToList();
            return remove.Where(go => !remove.Any(other => other != go && go.transform.IsChildOf(other.transform))).ToList();
        }

        // The whole prefab instance an origin belongs to (another XR rig prefab holds its own AR Session,
        // Event System and Interaction Manager too), or just the origin's object.
        static GameObject RemovalTarget(GameObject origin)
        {
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(origin);
            return root != null ? root : origin;
        }

        static GameObject FindRig(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var origin in root.GetComponentsInChildren<XROrigin>(true))
                    if (RigBuilder.IsRigOrigin(origin)) return RemovalTarget(origin.gameObject);
            return null;
        }

        // A child of a prefab instance cannot be deleted on its own, so a camera inside one is switched off instead.
        static bool IsLocked(GameObject go) =>
            PrefabUtility.IsPartOfPrefabInstance(go) && PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go;

        static void Remove(GameObject go)
        {
            if (IsLocked(go))
            {
                Undo.RecordObject(go, UndoName);
                go.SetActive(false);
            }
            else Undo.DestroyObjectImmediate(go);
        }

        static string Describe(List<GameObject> removals)
        {
            const int max = 12;
            var lines = removals.Take(max).Select(go => "  - " + go.name + (IsLocked(go) ? " (part of a prefab; deactivated instead)" : ""));
            var text = string.Join("\n", lines);
            return removals.Count > max ? text + "\n  … and " + (removals.Count - max) + " more" : text;
        }

        static string SceneName(Scene scene) => string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;

        static void Tell(string message) => EditorUtility.DisplayDialog("StageSettAR", message, "OK");

        // ---- project settings ----

        static bool PackagesMissing()
        {
            try { return new XrPackagesStep().GetStatus(out _) == SetupStatus.Needed; }
            catch (Exception e) { Debug.LogException(e); return false; }
        }

        static List<string> RunProjectSteps()
        {
            var ran = new List<string>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<ISetupStep>())
            {
                if (type.IsAbstract || type.IsInterface || Array.IndexOf(AllowedSteps, type.Name) < 0) continue;
                try
                {
                    var step = (ISetupStep)Activator.CreateInstance(type);
                    if (step.GetStatus(out _) != SetupStatus.Needed) continue;
                    step.Run();
                    ran.Add(step.Title);
                }
                catch (Exception e) { Debug.LogException(e); }
            }
            return ran;
        }
    }

    /// <summary>Setup window row for <see cref="SceneSetupCommand"/>: done once the open scene has a StageSettAR rig.</summary>
    public sealed class SceneSetupStep : ISetupStep
    {
        public int Order => 42;
        public string Title => "Set up current scene";
        public string ActionLabel => "Set up";
        public bool OpensWindow => false;

        SetupStatus _status;
        string _detail;
        double _checked = -10;

        public SetupStatus GetStatus(out string detail)
        {
            // Scans the open scene, so not on every repaint.
            if (EditorApplication.timeSinceStartup - _checked > 1.0)
            {
                _checked = EditorApplication.timeSinceStartup;
                _status = Evaluate(out _detail);
            }
            detail = _detail;
            return _status;
        }

        public void Run()
        {
            SceneSetupCommand.Run();
            _checked = -10;
        }

        static SetupStatus Evaluate(out string detail)
        {
            if (!RigBuilder.RigExists && !RigBuilder.SamplesReady)
            {
                detail = RigBuilder.SamplesMissing;
                return SetupStatus.Blocked;
            }
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                detail = "Open a scene first.";
                return SetupStatus.Blocked;
            }
            if (RigBuilder.HasRig(scene))
            {
                detail = "'" + scene.name + "' has the StageSettAR rig. Tools > StageSettAR > Set Up Current Scene checks it again.";
                return SetupStatus.Done;
            }
            detail = "Make the open scene a StageSettAR scene: XR project settings, the StageSettAR rig instead of other cameras and XR rigs, and a light if missing. Nothing is saved for you.";
            return SetupStatus.Needed;
        }
    }
}
