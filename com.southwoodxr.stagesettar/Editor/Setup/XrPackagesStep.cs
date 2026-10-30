using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Installs the Unity-registry packages Quest passthrough MR needs (input, XR Management, OpenXR, Meta OpenXR,
    /// AR Foundation, XR Interaction Toolkit, XR Hands) into a plain Unity 3D (URP) project, in one request so
    /// Package Manager resolves them together. Packages already present are not requested again.
    /// Packages are added by name so Unity picks the version verified for this editor.
    /// </summary>
    public sealed class XrPackagesStep : ISetupStep, ISetupStepOptions
    {
        public const string InputSystemId = "com.unity.inputsystem";
        public const string ManagementId = "com.unity.xr.management";
        public const string ArFoundationId = "com.unity.xr.arfoundation";
        public const string XriId = "com.unity.xr.interaction.toolkit";
        public const string HandsId = "com.unity.xr.hands";
        public const string OpenXrId = "com.unity.xr.openxr";
        public const string MetaOpenXrId = "com.unity.xr.meta-openxr";
        public const string ArCoreId = "com.unity.xr.arcore";
        public const string ArKitId = "com.unity.xr.arkit";

        // Required packages and the oldest version that works (null: any version).
        static readonly (string id, Version min)[] k_required =
        {
            (InputSystemId, null),
            (ManagementId, null),
            (OpenXrId, null),
            (MetaOpenXrId, new Version(2, 4, 0)),
            (ArFoundationId, new Version(6, 0, 0)),
            (XriId, new Version(3, 0, 0)),
            (HandsId, null),
        };

        static bool s_dirty = true;
        static readonly Dictionary<string, string> s_versions = new();
        static AddAndRemoveRequest s_request;
        static string s_requestLabel;

        static XrPackagesStep()
        {
            Events.registeredPackages += _ => s_dirty = true;
        }

        public int Order => 5;
        public string Title => "XR packages";
        public string ActionLabel => "Install";

        public SetupStatus GetStatus(out string detail)
        {
            Refresh();
            if (s_request != null) { detail = "Installing " + s_requestLabel + "…"; return SetupStatus.Blocked; }

            var problems = new List<string>();
            foreach (var (id, min) in k_required)
            {
                if (!s_versions.TryGetValue(id, out var version)) problems.Add(id + " (missing)");
                else if (IsTooOld(version, min)) problems.Add($"{id} (found {version}, needs {min} or newer)");
            }
            if (problems.Count == 0) { detail = "Input System, XR Management, OpenXR, Meta OpenXR, AR Foundation, XR Interaction Toolkit and XR Hands are installed."; return SetupStatus.Done; }

            detail = "Needs: " + string.Join(", ", problems) + ". Needs an internet connection.";
            return SetupStatus.Needed;
        }

        public void Run()
        {
            Refresh();
            var ids = new List<string>();
            foreach (var (id, min) in k_required)
                if (!s_versions.TryGetValue(id, out var version) || IsTooOld(version, min)) ids.Add(id);
            Install(ids.ToArray());
        }

        /// <summary>The phone packages are optional, so they get their own buttons instead of blocking the step.</summary>
        public void DrawOptions()
        {
            Refresh();
            using (new EditorGUI.DisabledScope(s_request != null))
            {
                EditorGUILayout.BeginHorizontal();
                OptionButton("Add ARCore (Android phones)", ArCoreId);
                OptionButton("Add ARKit (iOS)", ArKitId);
                EditorGUILayout.EndHorizontal();
            }
        }

        static void OptionButton(string label, string id)
        {
            var installed = s_versions.ContainsKey(id);
            using (new EditorGUI.DisabledScope(installed))
                if (GUILayout.Button(installed ? id + " installed" : label, GUILayout.Height(20)))
                    Install(new[] { id });
        }

        static void Refresh()
        {
            if (!s_dirty) return;
            s_dirty = false;
            s_versions.Clear();
            foreach (var p in PackageInfo.GetAllRegisteredPackages()) s_versions[p.name] = p.version;
        }

        static bool IsTooOld(string version, Version min)
        {
            if (min == null) return false;
            // Drop a pre-release or build suffix such as "-pre.1" before comparing.
            var core = version.Split('-', '+')[0];
            return Version.TryParse(core, out var parsed) && parsed < min;
        }

        static void Install(string[] ids)
        {
            if (s_request != null || ids.Length == 0) return;
            s_requestLabel = string.Join(", ", ids);
            s_request = Client.AddAndRemove(ids);
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (!s_request.IsCompleted)
            {
                EditorUtility.DisplayProgressBar("StageSettAR", "Installing " + s_requestLabel + "…", 0.5f);
                return;
            }

            var request = s_request;
            var label = s_requestLabel;
            EditorApplication.update -= Poll;
            EditorUtility.ClearProgressBar();
            s_request = null;
            s_dirty = true;
            if (request.Status == StatusCode.Success) Debug.Log("[StageSettAR] Installed " + label);
            else Fail($"Could not install {label}: {request.Error?.message}. Check your internet connection and try again.");
        }

        static void Fail(string message)
        {
            Debug.LogError("[StageSettAR] " + message);
            EditorUtility.DisplayDialog("StageSettAR", message, "OK");
        }
    }
}
