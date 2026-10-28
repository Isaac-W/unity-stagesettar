using UnityEditor;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Turns on the Meta Quest OpenXR features the rig relies on: camera (passthrough) with
    /// CPU image support, and boundary visibility set to suppress the Guardian while passthrough
    /// is showing, so a whole room or site can be walked.
    /// </summary>
    /// <remarks>
    /// meta-openxr adds the camera and <c>com.oculus.permission.BOUNDARY_VISIBILITY</c> manifest
    /// entries itself when these features are enabled. Everything else about Quest builds (API
    /// Vulkan, URP settings, the Meta Quest feature) is covered by Meta's own Project
    /// Validation, which this step does not repeat.
    /// </remarks>
    public sealed class QuestSettingsStep : ISetupStep
    {
        public int Order => 50;
        public string Title => "Quest settings";
        public string ActionLabel => "Enable";

        const AndroidSdkVersions MinApiLevel = (AndroidSdkVersions)32;

        SetupStatus _status;
        string _detail;
        double _checked = -10;

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
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null) return;

            var camera = settings.GetFeature<ARCameraFeature>();
            var boundary = settings.GetFeature<BoundaryVisibilityFeature>();
            if (camera != null)
            {
                camera.enabled = true;
                SetBool(camera, "m_CameraImageSupport", true);
            }
            if (boundary != null)
            {
                boundary.enabled = true;
                SetBool(boundary, "m_SuppressVisibility", true);
            }
            // Meta's camera image access needs API level 32; the MR template already uses it.
            if (PlayerSettings.Android.minSdkVersion < MinApiLevel) PlayerSettings.Android.minSdkVersion = MinApiLevel;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            _checked = -10;
        }

        static SetupStatus Evaluate(out string detail)
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            var camera = settings != null ? settings.GetFeature<ARCameraFeature>() : null;
            var boundary = settings != null ? settings.GetFeature<BoundaryVisibilityFeature>() : null;
            if (camera == null || boundary == null)
            {
                detail = "Turn on OpenXR for Android in Project Settings > XR Plug-in Management, then use Window > XR > Project Validation (Meta's own checks).";
                return SetupStatus.Blocked;
            }
            if (camera.enabled && boundary.enabled && GetBool(camera, "m_CameraImageSupport") && GetBool(boundary, "m_SuppressVisibility")
                && PlayerSettings.Android.minSdkVersion >= MinApiLevel)
            {
                detail = "Passthrough camera images are on and the Quest boundary is hidden while passthrough shows. Other build checks: Window > XR > Project Validation.";
                return SetupStatus.Done;
            }
            detail = "Enable passthrough camera images and hide the Quest boundary so you can walk the whole space. Other build checks: Window > XR > Project Validation.";
            return SetupStatus.Needed;
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
            so.ApplyModifiedProperties();
        }
    }
}
