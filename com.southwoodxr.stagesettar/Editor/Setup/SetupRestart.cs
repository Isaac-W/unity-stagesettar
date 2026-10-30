using System.IO;
using UnityEditor;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Lets a Setup step ask for an editor restart (some Player settings, such as the active input handler,
    /// only take effect after one). <see cref="SetupRunAll"/> stops when a step asked for one, so the user
    /// restarts once and clicks "Set up everything" again instead of the run continuing half-applied.
    /// </summary>
    public static class SetupRestart
    {
        internal const string Key = "StageSettAR.RestartRequested";

        /// <summary>A step asked for a restart during this editor session and it has not happened yet.</summary>
        public static bool Pending => SessionState.GetBool(Key, false);

        /// <summary>Records that a restart is needed and offers it right away.</summary>
        public static void Prompt(string reason)
        {
            SessionState.SetBool(Key, true);
            if (!EditorUtility.DisplayDialog("StageSettAR", reason + "\n\nRestart the editor now? Your scenes will be saved first.", "Restart now", "Later"))
                return;
            if (!EditorApplication.SaveCurrentSceneIfUserWantsTo()) return;
            AssetDatabase.SaveAssets();
            // ProjectSettings are written by Save Project; the reopened editor must read the new value.
            EditorApplication.ExecuteMenuItem("File/Save Project");
            EditorApplication.OpenProject(Directory.GetCurrentDirectory());
        }
    }
}
