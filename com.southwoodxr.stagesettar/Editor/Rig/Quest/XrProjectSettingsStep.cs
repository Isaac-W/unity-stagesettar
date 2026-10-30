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
    /// passthrough camera images and the boundary suppressed) and the URP assets and renderers.
    /// </summary>
    /// <remarks>
    /// Evaluating and fixing share one pass (<see cref="Pass"/>), so what is reported missing is exactly what Run
    /// fixes. OpenXR features are found by type name, which keeps this assembly free of references to the optional
    /// packages (XR Hands, the Meta features) and lets the step say "not found" instead of failing to compile.
    /// Afterwards OpenXR's own public project-validation fixes are applied. The active build target is not
    /// switched (slow); a button offers it. Changing the input handler needs an editor restart, which is offered.
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
            k_meta + "AROcclusionFeature",
            k_meta + "DisplayUtilitiesFeature",
            k_boundary,
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
                if (camera != null) p.Need("Passthrough camera image support (CPU)", GetBool(camera, "m_CameraImageSupport"), () => SetBool(camera, "m_CameraImageSupport", true));
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
