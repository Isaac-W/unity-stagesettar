using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StageSettAR.Editor
{
    /// <summary>Takes <see cref="EditorOnlyVisual"/> renderers out of scenes as they are built.</summary>
    sealed class EditorOnlyVisualStripper : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Entering play mode processes scenes too; the editor keeps the picture.
            if (report == null) return;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var marker in root.GetComponentsInChildren<EditorOnlyVisual>(true))
                    EditorOnlyVisual.Strip(marker.gameObject);
        }
    }
}
