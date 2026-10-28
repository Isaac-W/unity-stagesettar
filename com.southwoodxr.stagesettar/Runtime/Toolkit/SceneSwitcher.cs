#pragma warning disable CS0649 // serialized fields are filled by Unity
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace StageSettAR
{
    /// <summary>
    /// "Go to another scene." Wire its methods to any UnityEvent: Next Scene, Previous Scene, Go To Scene
    /// (type the scene's name), Restart Current Scene, Restart Application, Quit Application.
    /// </summary>
    /// <remarks>
    /// Scenes must be listed in the build (File &gt; Build Profiles &gt; Scene List). If a Camera Fade
    /// is in the scene it fades to black first, so the cut is not seen in the headset.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Scene Switcher")]
    public sealed class SceneSwitcher : MonoBehaviour
    {
        [Tooltip("Delay in seconds before a scene switch actually happens.")]
        [SerializeField, Min(0f)] float switchDelaySeconds;

        [Tooltip("Fade to the colour of the scene's Camera Fade before switching. Does nothing when the scene has none.")]
        [SerializeField] bool fadeOut = true;

        [Tooltip("How long that fade takes, in seconds.")]
        [SerializeField, Min(0f)] float fadeSeconds = 0.5f;

        [Tooltip("Invoked immediately when a switch is triggered, before the delay and fade.")]
        [SerializeField] UnityEvent onSwitchTriggered;

        bool _triggered;

        public float SwitchDelaySeconds
        {
            get => switchDelaySeconds;
            set => switchDelaySeconds = value;
        }

        public void NextScene()
        {
            int next = SceneManager.GetActiveScene().buildIndex + 1;
            if (next >= SceneManager.sceneCountInBuildSettings)
            {
                Debug.LogWarning("[SceneSwitcher] Already at the last scene in the build settings.", this);
                return;
            }
            TriggerSwitch(() => SceneManager.LoadScene(next));
        }

        public void PreviousScene()
        {
            int previous = SceneManager.GetActiveScene().buildIndex - 1;
            if (previous < 0)
            {
                Debug.LogWarning("[SceneSwitcher] Already at the first scene in the build settings.", this);
                return;
            }
            TriggerSwitch(() => SceneManager.LoadScene(previous));
        }

        public void GoToScene(string sceneName)
        {
            if (!TryGetBuildIndexForScene(sceneName, out int buildIndex))
            {
                Debug.LogError($"[SceneSwitcher] Scene '{sceneName}' is not in the build settings. " +
                               "Add it under File > Build Profiles > Scene List.", this);
                return;
            }
            TriggerSwitch(() => SceneManager.LoadScene(buildIndex));
        }

        public void RestartCurrentScene() =>
            TriggerSwitch(() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex));

        public void RestartApplication() => TriggerSwitch(() => SceneManager.LoadScene(0));

        public void QuitApplication()
        {
            TriggerSwitch(() =>
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            });
        }

        void TriggerSwitch(UnityAction action)
        {
            if (_triggered) return;
            _triggered = true;
            onSwitchTriggered?.Invoke();
            StartCoroutine(Switch(action));
        }

        IEnumerator Switch(UnityAction action)
        {
            // Real time throughout: a switch must still happen if the scene has been slowed or
            // paused, and the fade itself runs on unscaled time.
            if (fadeOut && fadeSeconds > 0f)
            {
                var fade = FindFirstObjectByType<CameraFade>();
                if (fade != null)
                {
                    fade.Cover(fadeSeconds);
                    yield return new WaitForSecondsRealtime(fadeSeconds);
                }
            }
            if (switchDelaySeconds > 0f) yield return new WaitForSecondsRealtime(switchDelaySeconds);
            action();
        }

        static bool TryGetBuildIndexForScene(string sceneName, out int buildIndex)
        {
            buildIndex = -1;
            if (string.IsNullOrEmpty(sceneName)) return false;

            bool matchByPath = sceneName.Contains("/");
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                bool isMatch = matchByPath
                    ? path.EndsWith("/" + sceneName + ".unity")
                    : Path.GetFileNameWithoutExtension(path) == sceneName;

                if (!isMatch) continue;
                buildIndex = i;
                return true;
            }
            return false;
        }
    }
}
