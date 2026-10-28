using System;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.SceneTemplate;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing;
using Object = UnityEngine.Object;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Builds the StageSettAR rig as a prefab variant of the Mixed Reality template's
    /// "MR Interaction Setup", and the scene template that contains it.
    /// </summary>
    /// <remarks>
    /// Template objects are found by component type, never by name, so a template update does not
    /// silently break the build. Types from the template's own assemblies (GoalManager,
    /// ObjectSpawner, ControllerInputActionManager, ARContactSpawnTrigger) are matched by type name
    /// because those assemblies are project samples this package cannot reference.
    /// </remarks>
    public static class RigBuilder
    {
        public const string Folder = "Assets/StageSettAR";
        public const string RigPath = Folder + "/StageSettAR Rig.prefab";
        public const string ScenePath = Folder + "/StageSettAR Scene.unity";
        public const string TemplatePath = Folder + "/StageSettAR Scene.scenetemplate";
        const string BodyMaterialPath = Folder + "/StageSettAR User Model.mat";
        const string VisorMaterialPath = Folder + "/StageSettAR User Visor.mat";

        // MR Interaction Setup.prefab and SampleSceneProfile.asset, from the Mixed Reality template.
        const string BasePrefabGuid = "86a5bf68cb51f4e44bd3893bd22a2ba7";
        const string VolumeProfileGuid = "a6560a915ef98420e9faacc1c7438823";

        static string s_FallbackPath;
        static double s_FallbackTime = -10;

        /// <summary>Path of the template's rig prefab, or null when the project was not made from the Mixed Reality template.</summary>
        public static string FindBasePrefab()
        {
            var path = AssetDatabase.GUIDToAssetPath(BasePrefabGuid);
            if (!string.IsNullOrEmpty(path)) return path;

            // Name search is slow, so not on every repaint.
            if (EditorApplication.timeSinceStartup - s_FallbackTime > 5.0)
            {
                s_FallbackTime = EditorApplication.timeSinceStartup;
                s_FallbackPath = null;
                foreach (var guid in AssetDatabase.FindAssets("MR Interaction Setup t:Prefab"))
                {
                    var candidate = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(candidate) == "MR Interaction Setup") { s_FallbackPath = candidate; break; }
                }
            }
            return s_FallbackPath;
        }

        public static bool RigExists => File.Exists(RigPath);
        public static bool TemplateExists => File.Exists(TemplatePath);

        [MenuItem("Tools/StageSettAR/Rebuild XR Rig")]
        static void RebuildMenu()
        {
            if (FindBasePrefab() == null)
            {
                EditorUtility.DisplayDialog("StageSettAR", "Create the project from the Unity Hub 'Mixed Reality' template.", "OK");
                return;
            }
            if (RigExists && !EditorUtility.DisplayDialog("Rebuild XR rig",
                    "This replaces 'StageSettAR Rig.prefab' with a fresh copy. Changes you made to the prefab itself will be lost (scene instances keep working).",
                    "Rebuild", "Cancel"))
                return;
            BuildRig();
        }

        public static void BuildRig()
        {
            var basePath = FindBasePrefab();
            var basePrefab = basePath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(basePath) : null;
            if (basePrefab == null) { Debug.LogError("[StageSettAR] The Mixed Reality template's 'MR Interaction Setup' prefab was not found."); return; }

            EnsureFolder();
            var bodyMaterial = EnsureMaterial(BodyMaterialPath, new Color(0.25f, 0.6f, 1f));
            var visorMaterial = EnsureMaterial(VisorMaterialPath, new Color(1f, 0.55f, 0.1f));

            // A preview scene keeps the temporary instance out of the scenes the user has open.
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var rig = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab, scene);
                rig.name = "StageSettAR Rig";
                Customize(rig, bodyMaterial, visorMaterial);
                PrefabUtility.SaveAsPrefabAsset(rig, RigPath, out bool saved);
                if (!saved) Debug.LogError("[StageSettAR] Could not save " + RigPath);
                else Debug.Log("[StageSettAR] Built " + RigPath + " as a variant of " + basePath);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static void Customize(GameObject rig, Material bodyMaterial, Material visorMaterial)
        {
            RemoveTutorialObjects(rig);
            DisableLocomotion(rig);

            var origin = rig.GetComponentInChildren<XROrigin>(true);
            if (origin == null) throw new InvalidOperationException("The rig prefab has no XROrigin.");
            var cam = origin.Camera;
            if (cam == null) throw new InvalidOperationException("The XROrigin has no camera.");

            // Without locomotion the controller would sit at the origin as a stray collider.
            var characterController = origin.GetComponent<CharacterController>();
            if (characterController != null) characterController.enabled = false;
            SetEnum(origin, "m_RequestedTrackingOriginMode", (int)XROrigin.TrackingOriginMode.Floor);

            if (!cam.CompareTag("MainCamera")) cam.tag = "MainCamera";
            var head = AddHeadCollider(cam);
            AddGazeSource(head, cam);
            AddScreenEffects(cam);
            AddBoundaryKeeper(rig);
            AddLocalizingScreen(rig);

            var cameraManager = rig.GetComponentInChildren<ARCameraManager>(true);
            var cameraHost = cameraManager != null ? cameraManager.gameObject : cam.gameObject;
            var source = cameraHost.GetComponent<ArFoundationCameraSource>();
            if (source == null) source = cameraHost.AddComponent<ArFoundationCameraSource>();
            SetReference(source, "cameraManager", cameraManager);
            SetReference(source, "xrOrigin", origin);

            AddUserModel(origin, cam, bodyMaterial, visorMaterial);
        }

        static void RemoveTutorialObjects(GameObject rig)
        {
            // Gathered first: removing while enumerating would invalidate the list.
            var remove = new List<GameObject>();
            var deactivate = new List<GameObject>();
            foreach (var mb in rig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                switch (mb.GetType().Name)
                {
                    case "GoalManager":
                    case "ObjectSpawner":
                        if (mb.gameObject != rig) remove.Add(mb.gameObject);
                        break;
                    case "ARContactSpawnTrigger":
                        deactivate.Add(mb.gameObject);
                        break;
                }
            }

            foreach (var go in deactivate) go.SetActive(false);
            foreach (var go in remove)
            {
                var name = go.name;
                try { Object.DestroyImmediate(go); }
                catch (Exception e) { Debug.LogWarning($"[StageSettAR] Could not remove '{name}' from the prefab instance: {e.Message}"); }
                if (go == null) { Debug.Log($"[StageSettAR] Removed '{name}' from the rig."); continue; }
                go.SetActive(false);
                Debug.Log($"[StageSettAR] Could not remove '{name}', deactivated it instead.");
            }
        }

        static void DisableLocomotion(GameObject rig)
        {
            var off = new List<GameObject>();
            foreach (var m in rig.GetComponentsInChildren<LocomotionMediator>(true)) off.Add(m.gameObject);
            foreach (var c in rig.GetComponentsInChildren<ClimbTeleportInteractor>(true)) off.Add(c.gameObject);

            // The controller manager re-activates its teleport interactor and toggles the move, turn and
            // teleport actions. Dropping those references leaves it driving only the ray / near-far / UI parts.
            foreach (var mb in rig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "ControllerInputActionManager") continue;
                var so = new SerializedObject(mb);
                var teleport = so.FindProperty("m_TeleportInteractor");
                if (teleport != null && teleport.objectReferenceValue is Component tc) off.Add(tc.gameObject);
                foreach (var field in new[] { "m_TeleportInteractor", "m_TeleportMode", "m_TeleportModeCancel", "m_Turn", "m_SnapTurn", "m_Move" })
                {
                    var p = so.FindProperty(field);
                    if (p != null) p.objectReferenceValue = null;
                }
                foreach (var field in new[] { "m_SmoothMotionEnabled", "m_SmoothTurnEnabled" })
                {
                    var p = so.FindProperty(field);
                    if (p != null) p.boolValue = false;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            foreach (var go in off) go.SetActive(false);
        }

        static GameObject AddHeadCollider(Camera cam)
        {
            var head = new GameObject("Head");
            head.transform.SetParent(cam.transform, false);
            head.tag = "Player";
            head.layer = 2; // Ignore Raycast: pointers do not hit the user's own head.

            var sphere = head.AddComponent<SphereCollider>();
            sphere.radius = 0.12f;
            sphere.isTrigger = true;

            var body = head.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            return head;
        }

        // The gaze components (On Gaze Action, Panel Look At, Experience Sequence's look-at steps) read
        // their line from here, so students never assign a camera. It lives on the Head so the line
        // starts at the user's head, and eye tracking can later replace it in one place.
        static void AddGazeSource(GameObject head, Camera cam)
        {
            var gaze = GetOrAdd<GazeSource>(head);
            SetReference(gaze, "userCamera", cam);
        }

        // One object under the camera holding everything that draws over, or fades, the user's view:
        // a plain colour fade, a circle wipe, and the passthrough-to-virtual fade. The overlays build
        // their own quad on the camera at runtime; nothing here renders until something plays.
        static void AddScreenEffects(Camera cam)
        {
            var effects = new GameObject("Screen Effects");
            effects.transform.SetParent(cam.transform, false);

            var shader = Resources.Load<Shader>("StageSettARCameraOverlay");
            var fade = GetOrAdd<CameraFade>(effects);
            var wipe = GetOrAdd<CameraCircleWipe>(effects);
            foreach (CameraOverlay overlay in new CameraOverlay[] { fade, wipe })
            {
                SetReference(overlay, "targetCamera", cam);
                SetReference(overlay, "overlayShader", shader);
            }

            var passthrough = GetOrAdd<PassthroughFade>(effects);
            SetReference(passthrough, "targetCamera", cam);
        }

        // Meta's boundary is only hidden by an explicit request, and the request is lost whenever
        // passthrough goes away. The keeper lives in the Meta-only runtime assembly, which this one
        // cannot reference (it only exists with com.unity.xr.meta-openxr), so it is found by name.
        static void AddBoundaryKeeper(GameObject rig)
        {
            var type = Type.GetType("StageSettAR.BoundaryVisibilityKeeper, StageSettAR.Meta");
            if (type == null)
            {
                Debug.Log("[StageSettAR] Boundary Visibility Keeper skipped: com.unity.xr.meta-openxr 2.4 or newer is not installed.");
                return;
            }
            var keeper = new GameObject("Boundary Keeper");
            keeper.transform.SetParent(rig.transform, false);
            if (keeper.GetComponent(type) == null) keeper.AddComponent(type);
        }

        // The panel that tells the user the room is being found. Its builder needs UGUI and TextMeshPro, so
        // it lives in the Toolkit editor assembly (which only exists with them) and is found by name,
        // like the boundary keeper. Building it twice returns the one already there.
        static void AddLocalizingScreen(GameObject rig)
        {
            var type = Type.GetType("StageSettAR.Editor.LocalizingScreenBuilder, StageSettAR.Editor.Toolkit");
            var build = type?.GetMethod("Build", new[] { typeof(Transform) });
            if (build == null)
            {
                Debug.Log("[StageSettAR] Localizing Screen skipped: com.unity.ugui is not installed.");
                return;
            }
            build.Invoke(null, new object[] { rig.transform });
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }

        static void AddUserModel(XROrigin origin, Camera cam, Material bodyMaterial, Material visorMaterial)
        {
            var root = new GameObject("Editor User Model");
            root.transform.SetParent(origin.transform, false);
            root.tag = "EditorOnly"; // not included in builds

            var head = Primitive(PrimitiveType.Sphere, "Model Head", root.transform, bodyMaterial);
            head.transform.localScale = Vector3.one * 0.22f;
            var visor = Primitive(PrimitiveType.Cube, "Model Visor", head.transform, visorMaterial);
            visor.transform.localPosition = new Vector3(0f, 0.05f, 0.5f); // in head units: sticks out the front
            visor.transform.localScale = new Vector3(0.9f, 0.3f, 0.5f);
            var body = Primitive(PrimitiveType.Capsule, "Model Body", root.transform, bodyMaterial);

            var model = root.AddComponent<EditorUserModel>();
            SetReference(model, "followCamera", cam);
            SetReference(model, "floor", origin.transform);
            SetReference(model, "head", head.transform);
            SetReference(model, "body", body.transform);
            model.Follow();
        }

        static GameObject Primitive(PrimitiveType type, string name, Transform parent, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        static Material EnsureMaterial(string path, Color color)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path), color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void SetReference(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[StageSettAR] {target.GetType().Name} has no field '{field}'."); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetEnum(Object target, string field, int value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[StageSettAR] {target.GetType().Name} has no field '{field}'."); return; }
            p.intValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "StageSettAR");
        }

        // ---- scene template ----

        public static void BuildSceneTemplate()
        {
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
            if (rigPrefab == null) { Debug.LogError("[StageSettAR] Build the XR rig first."); return; }
            EnsureFolder();

            // Additive, so the scenes the user has open are left alone.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                PrefabUtility.InstantiatePrefab(rigPrefab, scene);

                // Light and volume profile as in the Mixed Reality template's SampleScene.
                var lightObject = new GameObject("Directional Light");
                SceneManager.MoveGameObjectToScene(lightObject, scene);
                lightObject.transform.SetPositionAndRotation(new Vector3(0f, 3f, 0f), Quaternion.Euler(50f, -30f, 0f));
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.95686275f, 0.8392157f);
                light.intensity = 1f;
                light.shadows = LightShadows.Soft;

                var volumeObject = new GameObject("Global Volume");
                SceneManager.MoveGameObjectToScene(volumeObject, scene);
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                var profilePath = AssetDatabase.GUIDToAssetPath(VolumeProfileGuid);
                if (!string.IsNullOrEmpty(profilePath)) volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);

                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.Refresh();

            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (sceneAsset == null) { Debug.LogError("[StageSettAR] Could not save " + ScenePath); return; }
            SceneTemplateService.CreateTemplateFromScene(sceneAsset, TemplatePath);

            var template = AssetDatabase.LoadAssetAtPath<SceneTemplateAsset>(TemplatePath);
            if (template == null) { Debug.LogError("[StageSettAR] Could not create " + TemplatePath); return; }
            template.templateName = "StageSettAR";
            template.description = "A Quest passthrough scene with the StageSettAR rig: head collider (tag Player), gaze source, screen fades, a Localizing Screen that guides the user while the room is found, no locomotion, and an editor-only user model.";
            template.addToDefaults = true;
            var dependencies = template.dependencies;
            if (dependencies != null)
            {
                for (int i = 0; i < dependencies.Length; i++)
                {
                    var d = dependencies[i];
                    if (d.dependency == rigPrefab) d.instantiationMode = TemplateInstantiationMode.Reference;
                    dependencies[i] = d;
                }
                template.dependencies = dependencies;
            }
            EditorUtility.SetDirty(template);
            AssetDatabase.SaveAssets();
            Debug.Log("[StageSettAR] Created " + TemplatePath + "; it is listed under File > New Scene.");
        }
    }

    public sealed class RigStep : ISetupStep
    {
        public int Order => 30;
        public string Title => "XR rig";
        public string ActionLabel => "Create";

        public SetupStatus GetStatus(out string detail)
        {
            if (RigBuilder.RigExists)
            {
                detail = "Assets/StageSettAR/StageSettAR Rig.prefab is ready. Tools > StageSettAR > Rebuild XR Rig replaces it.";
                return SetupStatus.Done;
            }
            if (RigBuilder.FindBasePrefab() == null)
            {
                detail = "Create the project from the Unity Hub 'Mixed Reality' template";
                return SetupStatus.Blocked;
            }
            detail = "Make the headset rig: passthrough camera, hands and controllers, a head collider, gaze source, screen fades, a Localizing Screen for while the room is found, and no locomotion.";
            return SetupStatus.Needed;
        }

        public void Run() => RigBuilder.BuildRig();
    }

    public sealed class SceneTemplateStep : ISetupStep
    {
        public int Order => 40;
        public string Title => "Scene template";
        public string ActionLabel => "Create";

        public SetupStatus GetStatus(out string detail)
        {
            if (!RigBuilder.RigExists)
            {
                detail = "Create the XR rig first.";
                return SetupStatus.Blocked;
            }
            if (RigBuilder.TemplateExists)
            {
                detail = "File > New Scene > StageSettAR starts a scene with the rig, a light and a volume.";
                return SetupStatus.Done;
            }
            detail = "Add a 'StageSettAR' entry to File > New Scene.";
            return SetupStatus.Needed;
        }

        public void Run() => RigBuilder.BuildSceneTemplate();
    }
}
