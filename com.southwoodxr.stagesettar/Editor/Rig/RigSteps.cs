using System;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.SceneTemplate;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Builds the StageSettAR rig prefab out of the XR Interaction Toolkit samples, and the scene template
    /// that contains it.
    /// </summary>
    /// <remarks>
    /// The rig is a plain prefab: a root with the XR plumbing (input actions, interaction manager, event
    /// system, AR session) and, nested inside it, the Hands Interaction Demo's "XR Origin Hands (XR Rig)" prefab
    /// (which itself nests the Starter Assets' "XR Origin (XR Rig)"). The origin stays a nested prefab instance
    /// so a sample update still flows into the rig. Needs no template: a plain 3D (URP) project works once
    /// the samples are imported (Setup window: XR samples).
    /// Objects are found by component type, never by name, so a sample update does not silently break the
    /// build. ControllerInputActionManager is matched by type name because it lives in the Starter Assets
    /// sample assembly, which this package cannot reference.
    /// </remarks>
    public static class RigBuilder
    {
        public const string Folder = "Assets/StageSettAR";
        public const string RigPath = Folder + "/StageSettAR Rig.prefab";
        public const string ScenePath = Folder + "/StageSettAR Scene.unity";
        public const string TemplatePath = Folder + "/StageSettAR Scene.scenetemplate";

        /// <summary>Shown while the samples are not imported. Matches the Setup window's row title.</summary>
        public const string SamplesMissing = "Import the XR samples first (Setup window: XR samples)";

        // Eye height of the editor character and of the camera in the Scene view (metres).
        const float EyeHeight = 1.6f;

        const string OriginPrefabName = "XR Origin Hands (XR Rig)";
        const string InputActionsName = "XRI Default Input Actions";

        static GameObject s_OriginPrefab;
        static InputActionAsset s_InputActions;
        static double s_LookupTime = -10;

        /// <summary>The Hands Interaction Demo's origin prefab, or null while that sample is not imported.</summary>
        public static GameObject FindOriginPrefab()
        {
            RefreshSamples();
            return s_OriginPrefab;
        }

        /// <summary>The Starter Assets' "XRI Default Input Actions", or null while that sample is not imported.</summary>
        public static InputActionAsset FindInputActions()
        {
            RefreshSamples();
            return s_InputActions;
        }

        /// <summary>True when both sample assets the rig is built from exist in the project.</summary>
        public static bool SamplesReady => FindOriginPrefab() != null && FindInputActions() != null;

        // Name search is slow, so not on every repaint (the Setup window asks on each one). A cached asset that
        // was deleted compares equal to null, which forces the next lookup straight away.
        static void RefreshSamples()
        {
            bool stale = s_OriginPrefab == null || s_InputActions == null;
            if (!stale && EditorApplication.timeSinceStartup - s_LookupTime < 5.0) return;
            if (stale && EditorApplication.timeSinceStartup - s_LookupTime < 1.0) return;
            s_LookupTime = EditorApplication.timeSinceStartup;

            var prefabPath = FindAssetPath("t:Prefab", OriginPrefabName, ".prefab", "Hands Interaction Demo");
            s_OriginPrefab = prefabPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) : null;
            var actionsPath = FindAssetPath("t:InputActionAsset", InputActionsName, ".inputactions", "Starter Assets");
            s_InputActions = actionsPath != null ? AssetDatabase.LoadAssetAtPath<InputActionAsset>(actionsPath) : null;
        }

        // Any version folder under Assets (Samples/<package>/<version>/<sample>/...), matched on the exact
        // file name. A hit inside the expected sample folder wins over a copy somebody made elsewhere.
        static string FindAssetPath(string filter, string name, string extension, string preferredFolder)
        {
            string found = null;
            foreach (var guid in AssetDatabase.FindAssets(name + " " + filter, new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(path) != name) continue;
                if (path.Contains("/" + preferredFolder + "/")) return path;
                found ??= path;
            }
            return found;
        }

        public static bool RigExists => File.Exists(RigPath);
        public static bool TemplateExists => File.Exists(TemplatePath);

        [MenuItem("Tools/StageSettAR/Rebuild XR Rig")]
        static void RebuildMenu()
        {
            if (!SamplesReady) { TellSamplesMissing(); return; }
            if (RigExists && !EditorUtility.DisplayDialog("Rebuild XR rig",
                    "This replaces 'StageSettAR Rig.prefab' with a fresh copy. Changes you made to the prefab itself will be lost (scene instances keep working).",
                    "Rebuild", "Cancel"))
                return;
            BuildRig();
        }

        // The samples are imported from the Setup window, so that is where the dialog sends the user.
        static void TellSamplesMissing()
        {
            if (EditorUtility.DisplayDialog("StageSettAR", SamplesMissing + ".", "Open Setup", "Cancel"))
                SetupWindow.Open();
        }

        public static void BuildRig()
        {
            var originPrefab = FindOriginPrefab();
            var inputActions = FindInputActions();
            if (originPrefab == null || inputActions == null)
            {
                Debug.LogError("[StageSettAR] " + SamplesMissing + ". Needed: the Hands Interaction Demo's '" + OriginPrefabName
                    + "' prefab and the Starter Assets' '" + InputActionsName + "'.");
                return;
            }

            EnsureFolder();

            // A preview scene keeps the temporary instance out of the scenes the user has open.
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var rig = new GameObject("StageSettAR Rig");
                SceneManager.MoveGameObjectToScene(rig, scene);
                AddPlumbing(rig, inputActions);

                // Nested, not unpacked: sample updates keep flowing into the rig through the instance.
                PrefabUtility.InstantiatePrefab(originPrefab, rig.transform);
                Customize(rig);
                RecordNestedOverrides(rig);

                // Over an existing file this keeps the asset's GUID, so scene instances survive a rebuild.
                PrefabUtility.SaveAsPrefabAsset(rig, RigPath, out bool saved);
                if (!saved) Debug.LogError("[StageSettAR] Could not save " + RigPath);
                else Debug.Log("[StageSettAR] Built " + RigPath + " from the XR samples (" + AssetDatabase.GetAssetPath(originPrefab) + ").");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // The objects every XRI + AR Foundation scene needs once, next to the origin: one place that enables
        // the input actions, the interaction manager, a UI event system driven by XR pointers, and the AR session.
        static void AddPlumbing(GameObject rig, InputActionAsset inputActions)
        {
            var actionManager = new GameObject("Input Action Manager");
            actionManager.transform.SetParent(rig.transform, false);
            var manager = actionManager.AddComponent<InputActionManager>();
            var managerObject = new SerializedObject(manager);
            var assets = managerObject.FindProperty("m_ActionAssets");
            assets.arraySize = 1;
            assets.GetArrayElementAtIndex(0).objectReferenceValue = inputActions;
            managerObject.ApplyModifiedPropertiesWithoutUndo();

            var interactionManager = new GameObject("XR Interaction Manager");
            interactionManager.transform.SetParent(rig.transform, false);
            interactionManager.AddComponent<XRInteractionManager>();

            AddEventSystem(rig, inputActions);

            var sessionObject = new GameObject("AR Session");
            sessionObject.transform.SetParent(rig.transform, false);
            var session = sessionObject.AddComponent<ARSession>();
            SetBool(session, "m_AttemptUpdate", true);
            SetBool(session, "m_MatchFrameRate", true);
            SetEnum(session, "m_TrackingMode", (int)TrackingMode.PositionAndRotation);
            sessionObject.AddComponent<ARInputManager>();
        }

        // XR pointers drive world-space canvases through XRUIInputModule; the XRI UI actions come from the same
        // input asset the Input Action Manager enables. Mouse, touch, gamepad and joystick are off: on the headset
        // only the XR input is wanted, and the module would otherwise also poll the legacy Input Manager.
        static void AddEventSystem(GameObject rig, InputActionAsset inputActions)
        {
            var eventSystem = new GameObject("EventSystem");
            eventSystem.transform.SetParent(rig.transform, false);
            eventSystem.AddComponent<EventSystem>();
            var module = eventSystem.AddComponent<XRUIInputModule>();

            var so = new SerializedObject(module);
            SetProperty(so, "m_ActiveInputMode", p => p.intValue = (int)XRUIInputModule.ActiveInputMode.InputSystemActions);
            SetProperty(so, "m_EnableXRInput", p => p.boolValue = true);
            SetProperty(so, "m_EnableMouseInput", p => p.boolValue = false);
            SetProperty(so, "m_EnableTouchInput", p => p.boolValue = false);
            SetProperty(so, "m_EnableGamepadInput", p => p.boolValue = false);
            SetProperty(so, "m_EnableJoystickInput", p => p.boolValue = false);
            SetProperty(so, "m_PointAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "Point"));
            SetProperty(so, "m_LeftClickAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "Click"));
            SetProperty(so, "m_MiddleClickAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "MiddleClick"));
            SetProperty(so, "m_RightClickAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "RightClick"));
            SetProperty(so, "m_ScrollWheelAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "ScrollWheel"));
            SetProperty(so, "m_NavigateAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "Navigate"));
            SetProperty(so, "m_SubmitAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "Submit"));
            SetProperty(so, "m_CancelAction", p => p.objectReferenceValue = FindActionReference(inputActions, "XRI UI", "Cancel"));
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // The importer adds one InputActionReference sub-asset per action (named "Map/Action"), plus a hidden
        // duplicate kept for old references; a fresh reference should point at the visible one.
        static InputActionReference FindActionReference(InputActionAsset asset, string map, string action)
        {
            InputActionReference hidden = null;
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(asset)))
            {
                if (sub is not InputActionReference reference || reference.action == null) continue;
                if (reference.action.actionMap?.name != map || reference.action.name != action) continue;
                if ((reference.hideFlags & HideFlags.HideInHierarchy) == 0) return reference;
                hidden ??= reference;
            }
            if (hidden == null) Debug.LogWarning($"[StageSettAR] '{asset.name}' has no action '{map}/{action}' for the event system.");
            return hidden;
        }

        // Direct property changes (name, tag, active state, transforms, component fields) on objects inside the nested sample
        // and character instances are only kept as overrides once recorded. Recording an unchanged object adds nothing.
        static void RecordNestedOverrides(GameObject rig)
        {
            foreach (var t in rig.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(t.gameObject)) continue;
                PrefabUtility.RecordPrefabInstancePropertyModifications(t.gameObject);
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && !PrefabUtility.IsAddedComponentOverride(c)) PrefabUtility.RecordPrefabInstancePropertyModifications(c);
            }
        }

        static void Customize(GameObject rig)
        {
            DisableLocomotion(rig);

            var origin = rig.GetComponentInChildren<XROrigin>(true);
            if (origin == null) throw new InvalidOperationException("The rig prefab has no XROrigin.");
            var cam = origin.Camera;
            if (cam == null) throw new InvalidOperationException("The XROrigin has no camera.");

            // Without locomotion the controller would sit at the origin as a stray collider.
            var characterController = origin.GetComponent<CharacterController>();
            if (characterController != null)
            {
                characterController.enabled = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(characterController);
            }
            SetEnum(origin, "m_RequestedTrackingOriginMode", (int)XROrigin.TrackingOriginMode.Floor);
            SetCameraHeight(origin);

            if (!cam.CompareTag("MainCamera"))
            {
                cam.tag = "MainCamera";
                PrefabUtility.RecordPrefabInstancePropertyModifications(cam.gameObject);
            }
            AddArComponents(origin, cam);
            var head = AddHeadCollider(cam);
            AddGazeSource(head, cam);
            AddScreenEffects(cam);
            AddBoundaryKeeper(rig);
            AddLocalizingScreen(rig);

            var cameraManager = cam.GetComponent<ARCameraManager>();
            var source = cam.gameObject.GetComponent<ArFoundationCameraSource>();
            if (source == null) source = cam.gameObject.AddComponent<ArFoundationCameraSource>();
            SetReference(source, "cameraManager", cameraManager);
            SetReference(source, "xrOrigin", origin);

            AddEditorCharacter(cam);
        }

        // The samples are plain XR rigs; what makes this one an AR rig is added on top of them. The camera
        // shows the passthrough: transparent solid-colour background, the AR camera components, and no
        // post-processing (it needs a per-eye full-screen pass the Quest cannot spare). The origin gets the
        // plane and anchor managers: planes stay off until a feature asks for them (and there is no plane
        // prefab: StageSettAR draws none), anchors are on because localization places the world with one.
        static void AddArComponents(XROrigin origin, Camera cam)
        {
            var cameraObject = new SerializedObject(cam);
            SetProperty(cameraObject, "m_ClearFlags", p => p.intValue = (int)CameraClearFlags.SolidColor);
            SetProperty(cameraObject, "m_BackGroundColor", p => p.colorValue = new Color(0f, 0f, 0f, 0f));
            cameraObject.ApplyModifiedPropertiesWithoutUndo();

            var cameraManager = GetOrAdd<ARCameraManager>(cam.gameObject);
            var managerObject = new SerializedObject(cameraManager);
            SetProperty(managerObject, "m_FacingDirection", p => p.intValue = (int)CameraFacingDirection.World);
            SetProperty(managerObject, "m_AutoFocus", p => p.boolValue = true);
            SetProperty(managerObject, "m_LightEstimation", p => p.intValue = (int)LightEstimation.None);
            managerObject.ApplyModifiedPropertiesWithoutUndo();
            GetOrAdd<ARCameraBackground>(cam.gameObject);

            var cameraData = GetOrAdd<UniversalAdditionalCameraData>(cam.gameObject);
            SetBool(cameraData, "m_RenderPostProcessing", false);

            var planes = GetOrAdd<ARPlaneManager>(origin.gameObject);
            planes.enabled = false;
            GetOrAdd<ARAnchorManager>(origin.gameObject).enabled = true;
        }

        static void DisableLocomotion(GameObject rig)
        {
            var off = new List<GameObject>();
            foreach (var m in rig.GetComponentsInChildren<LocomotionMediator>(true)) off.Add(m.gameObject);
            foreach (var c in rig.GetComponentsInChildren<ClimbTeleportInteractor>(true)) off.Add(c.gameObject);

            // Move, Turn, Teleportation, Climb, Grab Move, Jump and Gravity are children of the mediator's
            // object, so that already covers them; this catches a provider placed anywhere else. Gravity
            // is also switched off on its own so a re-enabled rig does not pull the user to the floor.
            foreach (var provider in rig.GetComponentsInChildren<LocomotionProvider>(true))
            {
                off.Add(provider.gameObject);
                if (provider is GravityProvider gravity) SetBool(gravity, "m_UseGravity", false);
            }

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

            // The rig root and the XR Origin object carry the camera and the origin; they stay active.
            foreach (var go in off)
            {
                if (go == rig || go.GetComponent<XROrigin>() != null) continue;
                go.SetActive(false);
                PrefabUtility.RecordPrefabInstancePropertyModifications(go);
            }
        }

        // In Floor mode XR Origin puts the camera offset at 0 on device (the headset reports the real eye
        // height) and only moves it in play mode, so the offset object's saved height is what the Scene view
        // shows: eye height, so content is placed against a user of normal size. The Y offset itself is what
        // Device and Unbounded modes use, which is also the fallback when a platform has no floor.
        static void SetCameraHeight(XROrigin origin)
        {
            SetFloat(origin, "m_CameraYOffset", EyeHeight);
            var offset = origin.CameraFloorOffsetObject;
            if (offset == null) { Debug.LogWarning("[StageSettAR] The XROrigin has no Camera Floor Offset Object; the Scene view camera stays at the origin."); return; }
            var position = offset.transform.localPosition;
            position.y = EyeHeight;
            offset.transform.localPosition = position;
            PrefabUtility.RecordPrefabInstancePropertyModifications(offset.transform);
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

        // A person model (Mixamo, Generic rig) under the camera with its eyes at the camera, so the Scene
        // view shows where the user's head is and which way they face. It is the camera's child so it
        // travels with it; EditorCharacter hides it from that camera and keeps it upright, and
        // LockedObjects keeps its transform fixed and it out of Scene-view clicks. Tagged EditorOnly: not in builds.
        static void AddEditorCharacter(Camera cam)
        {
            var model = LoadCharacterModel();
            if (model == null) { Debug.LogWarning("[StageSettAR] Models/character.fbx was not found in the package; the rig has no editor character."); return; }

            var character = (GameObject)PrefabUtility.InstantiatePrefab(model, cam.transform);
            character.name = "Editor Character";
            character.tag = "EditorOnly";
            character.transform.localPosition = new Vector3(0f, -EyeHeight, 0f);
            character.transform.localRotation = Quaternion.identity;
            foreach (var collider in character.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            }

            var marker = character.AddComponent<EditorCharacter>();
            SetReference(marker, "followCamera", cam);
            marker.Follow();
            LockedObjects.Lock(character);
            PrefabUtility.RecordPrefabInstancePropertyModifications(character.transform);
        }

        static GameObject LoadCharacterModel()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RigBuilder).Assembly);
            var root = package != null ? package.assetPath : "Packages/com.southwoodxr.stagesettar";
            return AssetDatabase.LoadAssetAtPath<GameObject>(root + "/Models/character.fbx");
        }

        static void SetProperty(SerializedObject so, string field, Action<SerializedProperty> set)
        {
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[StageSettAR] {so.targetObject.GetType().Name} has no field '{field}'."); return; }
            set(p);
        }

        static void SetReference(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[StageSettAR] {target.GetType().Name} has no field '{field}'."); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetBool(Object target, string field, bool value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[StageSettAR] {target.GetType().Name} has no field '{field}'."); return; }
            p.boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetFloat(Object target, string field, float value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[StageSettAR] {target.GetType().Name} has no field '{field}'."); return; }
            p.floatValue = value;
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

        /// <summary>Builds the rig prefab when it is missing. False (after telling the user why) when it cannot exist.</summary>
        internal static bool EnsureRig()
        {
            if (RigExists) return true;
            if (!SamplesReady)
            {
                TellSamplesMissing();
                return false;
            }
            BuildRig();
            return RigExists;
        }

        // ---- scene contents (shared by the scene template and Tools > StageSettAR > Set Up Current Scene) ----

        /// <summary>True when the XROrigin belongs to a StageSettAR rig (a plain XRI sample rig has no gaze source).</summary>
        internal static bool IsRigOrigin(XROrigin origin) => origin != null && origin.GetComponentInChildren<GazeSource>(true) != null;

        internal static bool HasRig(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var origin in root.GetComponentsInChildren<XROrigin>(true))
                    if (IsRigOrigin(origin)) return true;
            return false;
        }

        static bool HasDirectionalLight(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                    if (light.type == LightType.Directional) return true;
            return false;
        }

        /// <summary>
        /// Puts what a StageSettAR scene needs into <paramref name="scene"/>: the rig, and a directional light
        /// when it has none. Each is added only if missing; <paramref name="added"/> collects what was.
        /// </summary>
        /// <param name="record">Register the new objects with Undo (for a scene the user is editing).</param>
        internal static void PopulateScene(Scene scene, GameObject rigPrefab, List<string> added = null, bool record = false)
        {
            if (!HasRig(scene))
            {
                var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
                RecordCreated(rig, record);
                added?.Add("StageSettAR Rig");
            }

            // The light of the URP 3D template's default scene. No global volume: the rig renders without
            // post-processing, and a URP project that wants one already has it in its own scene.
            if (!HasDirectionalLight(scene))
            {
                var lightObject = new GameObject("Directional Light");
                SceneManager.MoveGameObjectToScene(lightObject, scene);
                lightObject.transform.SetPositionAndRotation(new Vector3(0f, 3f, 0f), Quaternion.Euler(50f, -30f, 0f));
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.95686275f, 0.8392157f);
                light.intensity = 1f;
                light.shadows = LightShadows.Soft;
                RecordCreated(lightObject, record);
                added?.Add("Directional Light");
            }
        }

        static void RecordCreated(GameObject go, bool record)
        {
            if (record) Undo.RegisterCreatedObjectUndo(go, "Set Up StageSettAR Scene");
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
                PopulateScene(scene, rigPrefab);
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
            template.description = "A Quest passthrough scene with the StageSettAR rig (built from the XR Interaction Toolkit samples): head collider (tag Player), gaze source, screen fades, a Localizing Screen that guides the user while the room is found, no locomotion, and an editor-only character standing under the camera.";
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
            if (!RigBuilder.SamplesReady)
            {
                detail = RigBuilder.SamplesMissing;
                return SetupStatus.Blocked;
            }
            detail = "Make the headset rig from the XR samples: AR session, passthrough camera, hands and controllers, a head collider, gaze source, screen fades, a Localizing Screen for while the room is found, an editor-only character under the camera, and no locomotion.";
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
                detail = "File > New Scene > StageSettAR starts a scene with the rig and a light.";
                return SetupStatus.Done;
            }
            detail = "Add a 'StageSettAR' entry to File > New Scene.";
            return SetupStatus.Needed;
        }

        public void Run() => RigBuilder.BuildSceneTemplate();
    }
}
