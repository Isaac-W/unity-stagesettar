using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Tools &gt; StageSettAR &gt; No-Code: adds reference versions of the four HW 4 toolkit components,
    /// for students who didn't finish theirs.
    /// </summary>
    /// <remarks>
    /// The references ship inside the package as .cs.txt files in Editor/NoCode/Templates. Unity doesn't
    /// compile those, so they can't clash with the package's own classes. Adding copies the missing ones
    /// into Assets/Scripts/NoCodeReference as .cs files. Any component the project already defines is skipped.
    /// </remarks>
    public static class NoCodeMenu
    {
        const string OutputFolder = "Assets/Scripts/NoCodeReference";
        static readonly string[] Components = { "OnTriggerAction", "TimedAction", "CollideAction", "SequenceAction" };

        [MenuItem("Tools/StageSettAR/No-Code/Add Reference Scripts", false, 41)]
        public static void AddReferenceScripts()
        {
            string templateFolder = TemplateFolder();
            if (templateFolder == null)
            {
                EditorUtility.DisplayDialog("Add Reference Scripts",
                    "Could not find the StageSettAR package, so the reference scripts can't be added.", "OK");
                return;
            }

            string[] existing = Components.Where(Exists).ToArray();
            string[] missing = Components.Except(existing).ToArray();
            if (missing.Length == 0)
            {
                EditorUtility.DisplayDialog("Add Reference Scripts",
                    "This project already has all four components, so nothing was added.", "OK");
                return;
            }

            string message = "This adds the reference version of:\n  " + string.Join("\n  ", missing) +
                             "\n\nto " + OutputFolder + ".";
            if (existing.Length > 0)
            {
                message += "\n\nThese already exist and are kept, not replaced:\n  " + string.Join("\n  ", existing);
            }
            if (!EditorUtility.DisplayDialog("Add Reference Scripts", message, "Add", "Cancel"))
            {
                return;
            }

            Directory.CreateDirectory(OutputFolder);
            foreach (string name in missing)
            {
                string template = Path.Combine(templateFolder, name + ".cs.txt");
                if (!File.Exists(template))
                {
                    Debug.LogError("No-Code: missing template " + template);
                    continue;
                }
                File.Copy(template, Path.Combine(OutputFolder, name + ".cs"));
            }
            AssetDatabase.Refresh();
            Debug.Log("No-Code: added " + string.Join(", ", missing) + " to " + OutputFolder + ".");
        }

        // Use this before adding your own version of a component, or Unity reports a duplicate class.
        [MenuItem("Tools/StageSettAR/No-Code/Remove Reference Scripts", false, 42)]
        public static void RemoveReferenceScripts()
        {
            if (!AssetDatabase.IsValidFolder(OutputFolder))
            {
                EditorUtility.DisplayDialog("Remove Reference Scripts", "There are no reference scripts to remove.", "OK");
                return;
            }
            if (EditorUtility.DisplayDialog("Remove Reference Scripts",
                    "This deletes " + OutputFolder + ". Any objects using those components will show \"Missing Script\" until you add your own versions.",
                    "Remove", "Cancel"))
            {
                AssetDatabase.DeleteAsset(OutputFolder);
            }
        }

        // The templates folder is found through the package, so it works for embedded and file: packages.
        static string TemplateFolder()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NoCodeMenu).Assembly);
            if (info == null)
            {
                return null;
            }
            // resolvedPath is the real folder on disk; "Packages/<name>" only exists virtually for file: packages.
            return Path.Combine(info.resolvedPath, "Editor", "NoCode", "Templates");
        }

        // A component counts as present if a script file already sits in the output folder, or if a
        // global-namespace class with that name is compiled outside the StageSettAR assemblies.
        // The package ships StageSettAR.OnTriggerAction and StageSettAR.TimedAction, which must not count.
        private static bool Exists(string name)
        {
            if (File.Exists(Path.Combine(OutputFolder, name + ".cs")))
            {
                return true;
            }
            return TypeCache.GetTypesDerivedFrom<MonoBehaviour>().Any(t =>
                t.Name == name &&
                string.IsNullOrEmpty(t.Namespace) &&
                !t.Assembly.GetName().Name.StartsWith("StageSettAR"));
        }
    }
}
