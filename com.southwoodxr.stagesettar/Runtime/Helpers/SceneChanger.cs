using UnityEngine;
using UnityEngine.SceneManagement;

namespace StageSettAR.Helpers
{
    // Scenes must be added to File > Build Profiles > Scene List or they can't be loaded.
    [AddComponentMenu("StageSettAR/Helpers/Scene Changer")]
    public class SceneChanger : MonoBehaviour
    {
        [Tooltip("Exact name of the scene to load with LoadScene(). It must be in File > Build Profiles > Scene List.")]
        public string sceneName;

        public void LoadScene()
        {
            LoadSceneByName(sceneName);
        }

        public void LoadSceneByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                Debug.LogWarning($"{gameObject.name}: SceneChanger has no scene name set.", this);
                return;
            }
            SceneManager.LoadScene(name);
        }

        public void LoadNextScene()
        {
            int next = (SceneManager.GetActiveScene().buildIndex + 1) % SceneManager.sceneCountInBuildSettings;
            SceneManager.LoadScene(next);
        }

        public void ReloadScene()
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }
}
