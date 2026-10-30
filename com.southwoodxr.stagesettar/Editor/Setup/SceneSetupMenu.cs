using System;
using UnityEditor;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Tools > StageSettAR > Set Up Current Scene. The command itself lives in the Rig assembly, which only
    /// exists once AR Foundation and XRI are installed, so it is found by name (like the boundary keeper);
    /// without it this points the user to the Setup window, which installs the packages.
    /// </summary>
    static class SceneSetupMenu
    {
        [MenuItem("Tools/StageSettAR/Set Up Current Scene", false, 1)]
        static void SetUp()
        {
            var type = Type.GetType("StageSettAR.Editor.SceneSetupCommand, StageSettAR.Editor.Rig");
            var run = type?.GetMethod("Run", Type.EmptyTypes);
            if (run == null)
            {
                if (EditorUtility.DisplayDialog("StageSettAR", "The XR packages the rig needs are not installed yet. Install them from Tools > StageSettAR > Setup….", "Open Setup", "Cancel"))
                    SetupWindow.Open();
                return;
            }
            run.Invoke(null, null);
        }
    }
}
