using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Configures a plain Unity 3D (URP) project the way the Mixed Reality template is set up for Meta Quest
    /// passthrough: Player settings, XR Plug-in Management (OpenXR for Android and for Standalone, so Quest Link
    /// works), the OpenXR features (Quest support, hands, controller profiles, the AR Foundation features with
    /// passthrough camera images and the boundary suppressed), the URP assets and renderers, and XRI's XR Interaction
    /// Simulator (simulated headset and hands when pressing Play without a headset).
    /// </summary>
    /// <remarks>
    /// Evaluating and fixing share one pass (<see cref="Pass"/>), so what is reported missing is exactly what Run
    /// fixes. OpenXR features are found by type name, which keeps this assembly free of references to the optional
    /// packages (XR Hands, the Meta features) and lets the step say "not found" instead of failing to compile.
    /// Afterwards OpenXR's own public project-validation fixes are applied. The active build target is not
    /// switched (slow); a button offers it. Changing the input handler needs an editor restart, which is offered.
    /// The simulator settings are skipped until the sample's prefab is imported (the samples step, Order 6, runs first).
    /// </remarks>
    public sealed class XrProjectSettingsStep : ISetupStep, ISetupStepOptions
    {
        const AndroidSdkVersions k_minApiLevel = (AndroidSdkVersions)32;
        const string k_generalSettingsPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";

        const string k_meta = "UnityEngine.XR.OpenXR.Features.Meta.";
        const string k_profiles = "UnityEngine.XR.OpenXR.Features.Interactions.";
        const string k_hands = "UnityEngine.XR.Hands.OpenXR.";
        const string k_camera = k_meta + "ARCameraFeature";
        const string k_boundary = k_meta + "BoundaryVisibilityFeature";
        const string k_occlusion = k_meta + "AROcclusionFeature";

        static readonly BuildTargetGroup[] k_groups = { BuildTargetGroup.Android, BuildTargetGroup.Standalone };

        // Hand tracking and the Touch controllers, for the headset itself and for Quest Link.
        static readonly string[] k_commonFeatures =
        {
            k_hands + "HandTracking",
            k_hands + "MetaHandTrackingAim",
            k_profiles + "OculusTouchControllerProfile",
            k_profiles + "MetaQuestTouchPlusControllerProfile",
        };

        static readonly string[] k_androidFeatures =
        {
            "UnityEngine.XR.OpenXR.Features.MetaQuestSupport.MetaQuestFeature",
            k_meta + "ARSessionFeature",
            k_camera,
            k_meta + "ARPlaneFeature",
            k_meta + "ARAnchorFeature",
            k_meta + "ARRaycastFeature",
            k_meta + "ARBoundingBoxFeature",
            k_meta + "ARMeshFeature",
            k_occlusion,
            k_meta + "DisplayUtilitiesFeature",
            k_boundary,
            k_profiles + "HandInteractionProfile", // hand-tracking input through the OpenXR action system, on the headset only
        };

        static bool s_restartNeeded;

        SetupStatus _status;
        string _detail;
        double _checked = -10;

        public int Order => 7;
        public string Title => "XR project settings";
        public string ActionLabel => "Configure";

        public SetupStatus GetStatus(out string detail)
        {
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
            var pass = new Pass { Fix = true };
            Apply(pass);
            ApplyOpenXrValidationFixes();
            AssetDatabase.SaveAssets();
            _checked = -10;
            Debug.Log("[StageSettAR] Applied XR project settings (Player, XR Plug-in Management, OpenXR features, URP).");

            if (s_restartNeeded)
            {
                s_restartNeeded = false;
                SetupRestart.Prompt("Active Input Handling was set to the Input System, which Unity only applies after an editor restart.");
            }
        }

        /// <summary>Quest builds need the Android target; it is slow to switch, so the user chooses when.</summary>
        public void DrawOptions()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return;
            if (!GUILayout.Button("Switch to Android (for Quest builds)", GUILayout.Height(20))) return;
            if (!EditorUtility.DisplayDialog("StageSettAR", "Switching the build target reimports assets and can take a while. Switch to Android now?", "Switch", "Cancel")) return;
            EditorApplication.delayCall += () => EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }

        static SetupStatus Evaluate(out string detail)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                detail = "Install the Android Build Support module (with OpenJDK and the Android SDK) for this editor in Unity Hub, then reopen the project.";
                return SetupStatus.Blocked;
            }

            var pass = new Pass();
            Apply(pass);
            var note = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? ""
                : $" The active build target is {EditorUserBuildSettings.activeBuildTarget}; switch to Android before building for Quest.";
            if (SetupRestart.Pending) note += " Restart the editor to finish applying the input setting.";
            const string other = " Other build checks: Window > XR > Project Validation.";

            if (pass.Todo.Count == 0 && pass.Stuck.Count == 0)
            {
                detail = "Player, XR Plug-in Management (OpenXR), Quest OpenXR features and URP are configured." + note + other;
                return SetupStatus.Done;
            }
            if (pass.Todo.Count == 0)
            {
                detail = "Needs a manual change: " + Summary(pass.Stuck) + note;
                return SetupStatus.Blocked;
            }
            detail = "Still to configure: " + Summary(pass.Todo) + "." + (pass.Stuck.Count > 0 ? " Needs a manual change: " + Summary(pass.Stuck) : "") + note;
            return SetupStatus.Needed;
        }

        static string Summary(List<string> items) =>
            items.Count <= 5 ? string.Join("; ", items) : string.Join("; ", items.Take(5)) + $"; and {items.Count - 5} more";

        /// <summary>
        /// One walk over every setting. Without <see cref="Fix"/> it lists what is wrong; with it, it also repairs it.
        /// Todo is what Run can fix, Stuck what only the user can.
        /// </summary>
        sealed class Pass
        {
            public bool Fix;
            public readonly List<string> Todo = new();
            public readonly List<string> Stuck = new();

            public void Need(string what, bool ok, Action fix)
            {
                if (ok) return;
                if (Fix && fix != null)
                {
                    try { fix(); return; }
                    catch (Exception e) { Debug.LogException(e); }
                }
                Todo.Add(what);
            }
        }

        static void Apply(Pass p)
        {
            Player(p);
            Management(p);
            OpenXr(p);
            Urp(p);
            EditorQuality(p);
            Simulator(p);
        }

        // ---- Player ----

        static void Player(Pass p)
        {
            p.Need("Linear color space", PlayerSettings.colorSpace == ColorSpace.Linear, () => PlayerSettings.colorSpace = ColorSpace.Linear);

            var so = PlayerSettingsObject();
            var handler = so?.FindProperty("activeInputHandler");
            // 0 is the old Input Manager only; XRI needs the Input System, and "both" (2) keeps it available.
            p.Need("Input System input handling", handler == null || handler.intValue != 0, () =>
            {
                handler.intValue = 1;
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                s_restartNeeded = true;
            });

            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            p.Need("Android Vulkan only", !PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) && apis.Length == 1 && apis[0] == UnityEngine.Rendering.GraphicsDeviceType.Vulkan, () =>
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan });
            });
            p.Need("Android IL2CPP", PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP,
                () => PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP));
            p.Need("Android ARM64", PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64,
                () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64);
            // Meta's camera image access needs API level 32.
            p.Need("Android minimum API level 32", PlayerSettings.Android.minSdkVersion >= k_minApiLevel,
                () => PlayerSettings.Android.minSdkVersion = k_minApiLevel);
        }

        static SerializedObject PlayerSettingsObject()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            return assets != null && assets.Length > 0 ? new SerializedObject(assets[0]) : null;
        }

        // ---- XR Plug-in Management ----

        static void Management(Pass p)
        {
            var container = GeneralSettings(p.Fix);
            foreach (var group in k_groups)
            {
                var g = group;
                var general = container != null ? container.SettingsForBuildTarget(g) : null;
                var manager = general != null ? general.Manager : null;
                var hasOpenXr = manager != null && manager.activeLoaders.Any(l => l is OpenXRLoader);
                p.Need($"{g}: OpenXR as the XR plug-in", hasOpenXr, () => AssignOpenXr(container, g));

                // Re-read: assigning the loader may have just created the settings.
                general = container != null ? container.SettingsForBuildTarget(g) : null;
                p.Need($"{g}: initialize XR on startup", general != null && general.InitManagerOnStart, () =>
                {
                    general.InitManagerOnStart = true;
                    EditorUtility.SetDirty(general);
                });
            }
        }

        // The value of XRGeneralSettings' k_SettingsKey, which is obsolete from XR Plug-in Management 4.7 and its
        // replacement (settingsKey) does not exist before it, so the literal serves both.
        const string k_loaderSettingsKey = "com.unity.xr.management.loader_settings";

        // Mirrors what the XR Plug-in Management settings page does on first open: one asset in Assets/XR, registered
        // in the build settings under the key the runtime looks it up by.
        static XRGeneralSettingsPerBuildTarget GeneralSettings(bool create)
        {
            EditorBuildSettings.TryGetConfigObject(k_loaderSettingsKey, out XRGeneralSettingsPerBuildTarget container);
            if (container != null) return container;

            var guids = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
            if (guids.Length > 0) container = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(guids[0]));
            if (!create) return container;

            if (container == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/XR")) AssetDatabase.CreateFolder("Assets", "XR");
                container = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(container, k_generalSettingsPath);
                AssetDatabase.SaveAssets();
            }
            EditorBuildSettings.AddConfigObject(k_loaderSettingsKey, container, true);
            return container;
        }

        static void AssignOpenXr(XRGeneralSettingsPerBuildTarget container, BuildTargetGroup group)
        {
            if (container == null) throw new InvalidOperationException("XR Plug-in Management settings could not be created.");
            if (!container.HasManagerSettingsForBuildTarget(group)) container.CreateDefaultManagerSettingsForBuildTarget(group);
            var manager = container.ManagerSettingsForBuildTarget(group);
            if (!XRPackageMetadataStore.AssignLoader(manager, typeof(OpenXRLoader).FullName, group))
                throw new InvalidOperationException($"Could not assign the OpenXR loader for {group}.");
            EditorUtility.SetDirty(container);
        }

        // ---- OpenXR features ----

        static void OpenXr(Pass p)
        {
            foreach (var group in k_groups)
            {
                var g = group;
                var names = g == BuildTargetGroup.Android ? k_commonFeatures.Concat(k_androidFeatures).ToArray() : k_commonFeatures;
                var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(g);
                // The settings asset and the feature instances are created by OpenXR's feature refresh (what its settings page runs when opened).
                if (p.Fix && (settings == null || names.Any(n => FindFeature(settings, n) == null)))
                {
                    FeatureHelpers.RefreshFeatures(g);
                    settings = OpenXRSettings.GetSettingsForBuildTargetGroup(g);
                }
                if (settings == null)
                {
                    p.Need($"{g}: OpenXR settings", false, null);
                    continue;
                }

                foreach (var name in names)
                {
                    var feature = FindFeature(settings, name);
                    var label = name.Substring(name.LastIndexOf('.') + 1);
                    if (feature == null) p.Todo.Add($"{g}: {label} not found (is its package installed?)");
                    else p.Need($"{g}: {label}", feature.enabled, () => { feature.enabled = true; EditorUtility.SetDirty(feature); });
                }

                p.Need($"{g}: single-pass instanced rendering", settings.renderMode == OpenXRSettings.RenderMode.SinglePassInstanced, () =>
                {
                    settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                    EditorUtility.SetDirty(settings);
                });

                if (g != BuildTargetGroup.Android) continue;
                var camera = FindFeature(settings, k_camera);
                var boundary = FindFeature(settings, k_boundary);
                var occlusion = FindFeature(settings, k_occlusion);
                if (camera != null) p.Need("Passthrough camera image support (CPU)", GetBool(camera, "m_CameraImageSupport"), () => SetBool(camera, "m_CameraImageSupport", true));
                // Passthrough from the first frame, before Unity's splash screen, rather than black until the app starts.
                if (camera != null) p.Need("Passthrough shown before the splash screen", GetBool(camera, "m_PassthroughPreSplashScreen"), () => SetBool(camera, "m_PassthroughPreSplashScreen", true));
                // Environment depth with the hands cut out, so virtual objects can be hidden by the room but not by the user's own hands.
                if (occlusion != null) p.Need("Occlusion: hand removal", GetBool(occlusion, "m_EnableHandRemoval"), () => SetBool(occlusion, "m_EnableHandRemoval", true));
                if (boundary != null) p.Need("Boundary hidden while passthrough shows", GetBool(boundary, "m_SuppressVisibility"), () => SetBool(boundary, "m_SuppressVisibility", true));
            }
        }

        static OpenXRFeature FindFeature(OpenXRSettings settings, string fullTypeName)
        {
            foreach (var f in settings.GetFeatures())
                if (f != null && f.GetType().FullName == fullTypeName) return f;
            return null;
        }

        // OpenXR's own validation (Window > XR > OpenXR > Project Validation) is public; apply its automatic fixes.
        // Best effort: some rules throw while merely being checked (OpenXR 1.19's Meta Quest rules read the feature from
        // the selected build target, which has none when that is not Android), and that must not fail the step.
        static void ApplyOpenXrValidationFixes()
        {
            foreach (var group in k_groups)
            {
                var issues = new List<OpenXRFeature.ValidationRule>();
                try { OpenXRProjectValidation.GetCurrentValidationIssues(issues, group); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StageSettAR] OpenXR's project validation could not run for {group} ({e.GetType().Name}); " +
                                     "the settings above are applied. Switch to Android, then check Project Settings > XR Plug-in Management > Project Validation.");
                    continue;
                }
                foreach (var rule in issues)
                {
                    // The restart rules open their own dialogs and relaunch the editor; leave those to the user.
                    if (rule.fixIt == null || !rule.fixItAutomatic || rule.message == null || rule.message.IndexOf("restart", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    try
                    {
                        rule.fixIt();
                        Debug.Log($"[StageSettAR] OpenXR validation ({group}): {rule.message}");
                    }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
        }

        // ---- XR Interaction Simulator ----

        // XRI's settings class is internal and this assembly has no XRI reference, so it is reached by name. It is a
        // ScriptableSettings asset (Assets/XRI/Settings/Resources/XRDeviceSimulatorSettings.asset) read at runtime by
        // XRInteractionSimulatorLoader, the same asset the XRI project settings page edits.
        const string k_simulatorSettingsType = "UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation.XRDeviceSimulatorSettings, Unity.XR.Interaction.Toolkit";
        const string k_simulatorPrefabName = "XR Interaction Simulator";

        static void Simulator(Pass p)
        {
            // Nothing to point the settings at until the sample is imported.
            var prefab = FindSimulatorPrefab();
            var type = Type.GetType(k_simulatorSettingsType);
            if (prefab == null || type == null) return;

            var settings = LoadSimulatorSettings(type);
            if (settings != null && new SerializedObject(settings).FindProperty("m_SimulatorPrefab") == null)
            {
                p.Stuck.Add("XR Interaction Simulator: XRI's settings have no m_SimulatorPrefab field; enable it in Project Settings > XR Plug-in Management > XR Interaction Toolkit");
                return;
            }

            p.Need("XR Interaction Simulator in Play mode", settings != null && SimulatorConfigured(settings, prefab), () =>
            {
                // Instance creates the asset when missing (in edit mode); its defaults would leave the simulator off.
                settings ??= type.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)
                    ?.GetValue(null) as ScriptableObject;
                if (settings == null || !EditorUtility.IsPersistent(settings)) throw new InvalidOperationException("XRI's simulator settings asset could not be created.");

                var so = new SerializedObject(settings);
                // Instantiate automatically, only in the editor (not in builds), the new simulator rather than the classic Device Simulator.
                so.FindProperty("m_AutomaticallyInstantiateSimulatorPrefab").boolValue = true;
                so.FindProperty("m_AutomaticallyInstantiateInEditorOnly").boolValue = true;
                so.FindProperty("m_UseClassic").boolValue = false;
                so.FindProperty("m_SimulatorPrefab").objectReferenceValue = prefab;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
            });
        }

        static bool SimulatorConfigured(ScriptableObject settings, GameObject prefab)
        {
            var so = new SerializedObject(settings);
            return so.FindProperty("m_AutomaticallyInstantiateSimulatorPrefab").boolValue
                && so.FindProperty("m_AutomaticallyInstantiateInEditorOnly").boolValue
                && !so.FindProperty("m_UseClassic").boolValue
                && so.FindProperty("m_SimulatorPrefab").objectReferenceValue == prefab;
        }

        static ScriptableObject LoadSimulatorSettings(Type type)
        {
            var guids = AssetDatabase.FindAssets("t:" + type.Name);
            return guids.Length > 0 ? AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(guids[0]), type) as ScriptableObject : null;
        }

        // The sample's prefab, by name like the rig builder finds its origin prefab; the classic "XR Device Simulator" prefab has another name.
        static GameObject FindSimulatorPrefab()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Samples")) return null;
            foreach (var guid in AssetDatabase.FindAssets(k_simulatorPrefabName + " t:Prefab", new[] { "Assets/Samples" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != k_simulatorPrefabName) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent("XRInteractionSimulator") != null) return prefab;
            }
            return null;
        }

        // ---- URP ----

        static void Urp(Pass p)
        {
            // HDR off and 4x MSAA are for the headset; apply them to the pipeline assets Android builds can use.
            foreach (var asset in AndroidPipelines())
            {
                var a = asset;
                var ok = !a.supportsHDR && a.msaaSampleCount == 4;
                if (!ok && !IsEditable(a)) { p.Stuck.Add($"{a.name} is read-only or in a package (HDR off, 4x MSAA); assign an asset from Assets/ in Quality settings"); continue; }
                p.Need($"{a.name}: HDR off, 4x MSAA", ok, () =>
                {
                    a.supportsHDR = false;
                    a.msaaSampleCount = 4;
                    EditorUtility.SetDirty(a);
                });
            }

            var renderers = TargetRenderers();
            if (renderers.Count == 0) p.Stuck.Add("assign a URP renderer in Project Settings > Graphics or Quality");
            foreach (var renderer in renderers)
            {
                var r = renderer;
                var missing = new List<Type>();
                if (!r.rendererFeatures.Any(f => f is UnityEngine.XR.ARFoundation.ARBackgroundRendererFeature)) missing.Add(typeof(UnityEngine.XR.ARFoundation.ARBackgroundRendererFeature));
                if (!r.rendererFeatures.Any(f => f is UnityEngine.XR.ARFoundation.ARCommandBufferSupportRendererFeature)) missing.Add(typeof(UnityEngine.XR.ARFoundation.ARCommandBufferSupportRendererFeature));
                if (missing.Count == 0) continue;
                if (!IsEditable(r)) { p.Stuck.Add($"{r.name} is read-only or in a package (AR renderer features); assign a renderer from Assets/ in Quality settings"); continue; }
                p.Need($"{r.name}: AR renderer features", false, () =>
                {
                    foreach (var type in missing) AddFeature(r, type);
                    EditorUtility.SetDirty(r);
                });
            }

            // Screen-space ambient occlusion costs too much on the headset and does not suit passthrough, so the
            // renderers Android builds use (the Mobile pipeline's) do without it.
            foreach (var renderer in AndroidRenderers())
            {
                var r = renderer;
                var ssao = r.rendererFeatures.Where(f => f is ScreenSpaceAmbientOcclusion).ToList();
                if (ssao.Count == 0) continue;
                if (!IsEditable(r)) { p.Stuck.Add($"{r.name} is read-only or in a package (Screen Space Ambient Occlusion); assign a renderer from Assets/ in Quality settings"); continue; }
                p.Need($"{r.name}: no Screen Space Ambient Occlusion", false, () =>
                {
                    foreach (var f in ssao) RemoveFeature(r, f);
                });
            }
        }

        // ---- editor quality ----

        // Play mode in the editor should look like the headset, so Standalone uses the quality level (and with it the
        // URP asset and renderer) that Android uses, not the template's separate desktop one. That level has to stop
        // excluding Standalone to be selectable there. Read and written through the quality settings asset because
        // the per-platform defaults and exclusions have no public API.
        static void EditorQuality(Pass p)
        {
            if (!EditorQualityState(false, out var known) || !known) return;
            p.Need("Editor Play mode uses the Android quality level (the Mobile renderer)", false, () => EditorQualityState(true, out _));
        }

        // Returns whether the editor already uses the Android level; known is false when the asset has no usable layout.
        // With fix, makes it so.
        static bool EditorQualityState(bool fix, out bool known)
        {
            known = false;
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (assets == null || assets.Length == 0) return false;
            var so = new SerializedObject(assets[0]);
            var defaults = so.FindProperty("m_PerPlatformDefaultQuality");
            var levels = so.FindProperty("m_QualitySettings");
            var current = so.FindProperty("m_CurrentQuality");
            if (defaults == null || levels == null || current == null) return false;

            SerializedProperty android = null, standalone = null;
            for (var i = 0; i < defaults.arraySize; i++)
            {
                var entry = defaults.GetArrayElementAtIndex(i);
                var key = entry.FindPropertyRelative("first");
                var value = entry.FindPropertyRelative("second");
                if (key == null || value == null) continue;
                if (key.stringValue == "Android") android = value;
                else if (key.stringValue == "Standalone") standalone = value;
            }
            if (android == null || standalone == null || android.intValue < 0 || android.intValue >= levels.arraySize) return false;
            known = true;

            var level = android.intValue;
            var excluded = levels.GetArrayElementAtIndex(level).FindPropertyRelative("excludedTargetPlatforms");
            var at = -1;
            for (var i = 0; excluded != null && i < excluded.arraySize; i++)
                if (excluded.GetArrayElementAtIndex(i).stringValue == "Standalone") at = i;

            var ok = standalone.intValue == level && current.intValue == level && at < 0;
            if (!fix || ok) return ok;

            if (at >= 0) excluded.DeleteArrayElementAtIndex(at);
            standalone.intValue = level;
            current.intValue = level;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            return true;
        }

        // The pipeline assets used by quality levels that are available on Android; a level without its own asset uses the default one.
        static List<UniversalRenderPipelineAsset> AndroidPipelines()
        {
            var list = new List<UniversalRenderPipelineAsset>();
            void Add(RenderPipelineAsset a)
            {
                if (a is UniversalRenderPipelineAsset urp && !list.Contains(urp)) list.Add(urp);
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            var levels = assets != null && assets.Length > 0 ? new SerializedObject(assets[0]).FindProperty("m_QualitySettings") : null;
            for (var i = 0; i < QualitySettings.names.Length; i++)
            {
                if (levels != null && i < levels.arraySize)
                {
                    var excluded = levels.GetArrayElementAtIndex(i).FindPropertyRelative("excludedTargetPlatforms");
                    var skip = false;
                    for (var j = 0; excluded != null && j < excluded.arraySize; j++) skip |= excluded.GetArrayElementAtIndex(j).stringValue == "Android";
                    if (skip) continue;
                }
                var asset = QualitySettings.GetRenderPipelineAssetAt(i);
                Add(asset ? asset : GraphicsSettings.defaultRenderPipeline);
            }
            if (list.Count == 0) Add(GraphicsSettings.defaultRenderPipeline);
            return list;
        }

        // The renderers of the pipeline assets Android builds use.
        static List<ScriptableRendererData> AndroidRenderers()
        {
            var list = new List<ScriptableRendererData>();
            foreach (var asset in AndroidPipelines())
            {
                // Read through the serialized list: it holds every slot, not only the ones the pipeline has built.
                var slots = new SerializedObject(asset).FindProperty("m_RendererDataList");
                for (var i = 0; slots != null && i < slots.arraySize; i++)
                    if (slots.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableRendererData r && !list.Contains(r))
                        list.Add(r);
            }
            return list;
        }

        // Every renderer of the default pipeline asset and of each quality level's asset, plus every URP renderer asset
        // under Assets/ that nothing points at yet (same set the splat step uses): one a quality preset or script
        // switches to later would otherwise show no passthrough.
        static List<ScriptableRendererData> TargetRenderers()
        {
            var list = new List<ScriptableRendererData>();
            var seen = new HashSet<RenderPipelineAsset>();
            void AddPipeline(RenderPipelineAsset a)
            {
                if (a is not UniversalRenderPipelineAsset || !seen.Add(a)) return;
                // Read through the serialized list: it holds every slot, not only the ones the pipeline has built.
                var slots = new SerializedObject(a).FindProperty("m_RendererDataList");
                for (var i = 0; slots != null && i < slots.arraySize; i++)
                    if (slots.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableRendererData r && !list.Contains(r))
                        list.Add(r);
            }
            AddPipeline(GraphicsSettings.defaultRenderPipeline);
            for (var i = 0; i < QualitySettings.names.Length; i++) AddPipeline(QualitySettings.GetRenderPipelineAssetAt(i));
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
                if (AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(AssetDatabase.GUIDToAssetPath(guid)) is { } r && !list.Contains(r))
                    list.Add(r);
            return list;
        }

        static bool IsEditable(Object asset)
        {
            if (!EditorUtility.IsPersistent(asset)) return false;
            var path = AssetDatabase.GetAssetPath(asset);
            if (path.StartsWith("Assets/", StringComparison.Ordinal)) return !new FileInfo(path).IsReadOnly;
            var pkg = PackageInfo.FindForAssetPath(path);
            return pkg != null && pkg.source is PackageSource.Embedded or PackageSource.Local;
        }

        // Mirrors URP's ScriptableRendererDataEditor.AddComponent: sub-asset + feature list + local-id map.
        static void AddFeature(ScriptableRendererData data, Type type)
        {
            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(type);
            feature.name = type.Name;
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(data);
            var list = so.FindProperty("m_RendererFeatures");
            var ids = so.FindProperty("m_RendererFeatureMap");
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
            ids.arraySize++;
            ids.GetArrayElementAtIndex(ids.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[StageSettAR] Added {type.Name} to {data.name}", data);
        }

        // Mirrors URP's ScriptableRendererDataEditor.RemoveComponent: the feature list and local-id map entries, then the sub-asset.
        static void RemoveFeature(ScriptableRendererData data, ScriptableRendererFeature feature)
        {
            var so = new SerializedObject(data);
            var list = so.FindProperty("m_RendererFeatures");
            var ids = so.FindProperty("m_RendererFeatureMap");
            for (var i = 0; i < list.arraySize; i++)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue != feature) continue;
                // An object-reference slot is only cleared by the first delete; null it so the second is not needed.
                list.GetArrayElementAtIndex(i).objectReferenceValue = null;
                list.DeleteArrayElementAtIndex(i);
                if (i < ids.arraySize) ids.DeleteArrayElementAtIndex(i);
                break;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            var name = feature.GetType().Name;
            AssetDatabase.RemoveObjectFromAsset(feature);
            Object.DestroyImmediate(feature, true);
            EditorUtility.SetDirty(data);
            Debug.Log($"[StageSettAR] Removed {name} from {data.name}", data);
        }

        static bool GetBool(Object obj, string field)
        {
            var p = new SerializedObject(obj).FindProperty(field);
            return p != null && p.boolValue;
        }

        static void SetBool(Object obj, string field, bool value)
        {
            var so = new SerializedObject(obj);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[StageSettAR] {obj.GetType().Name} has no field {field}; set it in Project Settings."); return; }
            p.boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(obj);
        }
    }
}
