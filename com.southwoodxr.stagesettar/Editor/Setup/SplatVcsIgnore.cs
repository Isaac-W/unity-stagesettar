using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Keeps the embedded UnitySplats copy (Packages/com.arloopa.unitysplats) out of git and Unity Version Control.
    /// Git: a nested .gitignore inside the package. Plastic only reads the workspace-root ignore.conf, which may sit
    /// above the Unity project, so a marked block is added there (and only if that file already exists).
    /// </summary>
    [InitializeOnLoad]
    static class SplatVcsIgnore
    {
        const string k_begin = "# StageSettAR begin";
        const string k_end = "# StageSettAR end";
        const string k_gitComment = "# Added by StageSettAR: downloaded copy, not tracked. Turn off in Tools > StageSettAR > Setup.";

        static double s_next;
        const string k_warnedKey = "StageSettAR.PlasticNoIgnoreConfWarned";

        static SplatVcsIgnore()
        {
            EditorApplication.update += Tick;
        }

        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        public static string PackageDir => Path.Combine(ProjectRoot, "Packages", SplatDependencyStep.UnitySplatsId);

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < s_next) return;
            s_next = EditorApplication.timeSinceStartup + 10;
            Apply();
        }

        public static void Apply()
        {
            try
            {
                var dir = PackageDir;
                // A clone with its own .git is the student's fork, not our download: leave it alone.
                var want = StageSettARSettings.instance.IgnoreSplatsInVcs && Directory.Exists(dir) &&
                           !Directory.Exists(Path.Combine(dir, ".git")) && !File.Exists(Path.Combine(dir, ".git"));
                ApplyGit(dir, want);
                ApplyPlastic(want);
            }
            catch (Exception e) { Debug.LogWarning("[StageSettAR] Could not update VCS ignore rules: " + e.Message); }
        }

        static void ApplyGit(string dir, bool want)
        {
            var file = Path.Combine(dir, ".gitignore");
            if (want)
            {
                // UnitySplats ships its own .gitignore (Unity's project template, meant for its repo root), which inside
                // Packages/ ignores almost nothing; replace it with ours rather than leave the copy tracked.
                if (!File.Exists(file) || !File.ReadAllText(file).StartsWith(k_gitComment, StringComparison.Ordinal))
                    File.WriteAllText(file, k_gitComment + "\n*\n");
            }
            else if (File.Exists(file) && File.ReadAllText(file).StartsWith(k_gitComment, StringComparison.Ordinal))
                File.Delete(file);
        }

        static void ApplyPlastic(bool want)
        {
            var root = ProjectRoot;
            for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
            {
                if (!Directory.Exists(Path.Combine(d.FullName, ".plastic"))) continue;
                var conf = Path.Combine(d.FullName, "ignore.conf");
                var rel = Path.GetRelativePath(d.FullName, root).Replace('\\', '/');
                var rule = "/" + (rel == "." ? "" : rel + "/") + "Packages/" + SplatDependencyStep.UnitySplatsId;
                if (!File.Exists(conf))
                {
                    // Never create it: Unity writes its default one (Library, Temp, ...) only when none exists, so ours
                    // would stop that and get Library checked in. Say so once, instead of silently tracking the copy.
                    if (want && !SessionState.GetBool(k_warnedKey, false))
                    {
                        SessionState.SetBool(k_warnedKey, true);
                        Debug.LogWarning($"[StageSettAR] This Unity Version Control workspace ({d.FullName}) has no ignore.conf, so the downloaded " +
                                         $"UnitySplats copy would be checked in. Create ignore.conf (Unity's Version Control window does this), or add: {rule}");
                    }
                    return;
                }

                var text = File.ReadAllText(conf);
                var nl = text.Contains("\r\n") ? "\r\n" : "\n";
                var stripped = Regex.Replace(text, @"(\r?\n)?" + Regex.Escape(k_begin) + @".*?" + Regex.Escape(k_end) + @"[ \t]*(\r?\n)?",
                    "$1", RegexOptions.Singleline);
                var result = stripped;
                if (want)
                {
                    if (result.Length > 0 && !result.EndsWith("\n", StringComparison.Ordinal)) result += nl;
                    result += k_begin + nl + rule + nl + k_end + nl;
                }
                if (result != text) File.WriteAllText(conf, result);
                return;
            }
        }
    }
}
