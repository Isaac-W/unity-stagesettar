using UnityEngine;
using UnityEngine.SceneManagement;

namespace StageSettAR
{
    /// <summary>
    /// Asks for the camera permissions in a scene of its own, then loads the next scene once they are granted.
    /// </summary>
    /// <remarks>
    /// <para>A camera subsystem that starts before the permission is granted does not retry: on Quest it reports a
    /// camera that never opens, and nothing says why. Asking from the scene that uses the camera is therefore too
    /// late, because the permission dialog takes focus and the grant arrives after the camera has started. So the
    /// first scene in the build holds nothing but this component, and the real scene loads with the permission
    /// already in hand. Tools ▸ StageSettAR ▸ Setup ▸ "Camera permission scene" makes that scene.</para>
    /// <para>The grant is polled rather than taken from <c>PermissionCallbacks</c>: the callbacks are not delivered
    /// on every Android version, and a missed grant would look like a denial.</para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/Camera Permission Gate")]
    [DefaultExecutionOrder(-1000)]
    public sealed class CameraPermissionGate : MonoBehaviour
    {
        /// <summary>Horizon OS gate on the passthrough camera. Quest only; other devices do not define it.</summary>
        public const string HeadsetCamera = "horizonos.permission.HEADSET_CAMERA";

        /// <summary>Every permission the camera sources need, all requested together.</summary>
        public static readonly string[] Required = { HeadsetCamera, "android.permission.CAMERA" };

        [Tooltip("Scene to load once permission is granted. Empty loads the next scene in the build settings.")]
        public string nextScene = "";

        bool _transitioned, _focusLost, _deniedLogged;

        /// <summary>True when every permission in <see cref="Required"/> is granted. Always true off Android.</summary>
        public static bool IsGranted
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                foreach (var p in Required)
                    if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(p)) return false;
#endif
                return true;
            }
        }

        void Awake()
        {
            if (IsGranted) { LoadNext(); return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            UnityEngine.Android.Permission.RequestUserPermissions(Required);
            Debug.Log($"[StageSettAR] Requested {string.Join(", ", Required)}.", this);
#endif
        }

        void Update()
        {
            if (IsGranted) LoadNext();
        }

        // The dialog takes focus. Back in focus and still not granted means the user said no.
        void OnApplicationFocus(bool focused)
        {
            if (!focused) { _focusLost = true; return; }
            if (!_focusLost || _deniedLogged || IsGranted) return;
            _deniedLogged = true;
            Debug.LogWarning("[StageSettAR] Camera permission was not granted, so localization cannot run. " +
                             "Grant it in the headset's app permissions, then restart the app.", this);
        }

        void LoadNext()
        {
            if (_transitioned) return;
            _transitioned = true;
            if (!string.IsNullOrWhiteSpace(nextScene))
            {
                Debug.Log($"[StageSettAR] Camera permission granted; loading '{nextScene}'.", this);
                SceneManager.LoadScene(nextScene);
                return;
            }
            int next = SceneManager.GetActiveScene().buildIndex + 1;
            if (next < SceneManager.sceneCountInBuildSettings)
            {
                Debug.Log($"[StageSettAR] Camera permission granted; loading build scene {next}.", this);
                SceneManager.LoadScene(next);
                return;
            }
            Debug.LogWarning("[StageSettAR] Camera permission granted, but there is no next scene in the build settings. " +
                             "Add the experience scenes after this one, or set Next Scene.", this);
        }
    }
}
