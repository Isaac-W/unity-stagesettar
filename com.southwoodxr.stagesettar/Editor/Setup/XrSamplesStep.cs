using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Imports the package samples the rig is built from (XR Hands "HandVisualizer", XRI "Starter Assets" and
    /// "Hands Interaction Demo": controllers, hands, input actions and presets), the same ones the Unity
    /// Mixed Reality template ships, plus XRI's "XR Interaction Simulator" (a simulated headset and hands for
    /// Play mode without a headset; the XR project settings step enables it). They are Package Manager samples,
    /// so a plain project has none of them.
    /// </summary>
    /// <remarks>
    /// Lives in the ungated assembly and uses only Package Manager's sample API, so it needs no reference to
    /// the XR packages. Importing can trigger a recompile; all of them are imported in one run.
    /// </remarks>
    public sealed class XrSamplesStep : ISetupStep
    {
        // In dependency order: Hands Interaction Demo needs Starter Assets and HandVisualizer.
        static readonly (string package, string sample)[] k_samples =
        {
            (XrPackagesStep.HandsId, "HandVisualizer"),
            (XrPackagesStep.XriId, "Starter Assets"),
            (XrPackagesStep.XriId, "Hands Interaction Demo"),
            (XrPackagesStep.XriId, "XR Interaction Simulator"),
        };

        static SetupStatus s_status;
        static string s_detail = "";
        static double s_checked = double.NegativeInfinity;

        // Before the XR project settings step (Order 7): it points XRI's simulator settings
        // at this sample's prefab, so the sample must be imported first. Needs only the XR packages (Order 5).
        public int Order => 6;
        public string Title => "XR sample assets";
        public string ActionLabel => "Import";

        public SetupStatus GetStatus(out string detail)
        {
            // Listing samples touches Package Manager and this runs per repaint.
            if (EditorApplication.timeSinceStartup - s_checked > 2)
            {
                s_checked = EditorApplication.timeSinceStartup;
                s_status = Evaluate(out s_detail);
            }
            detail = s_detail;
            return s_status;
        }

        public void Run()
        {
            var versions = Versions();
            foreach (var (package, name) in k_samples)
            {
                if (!versions.TryGetValue(package, out var version)) continue;
                if (!TryFind(package, version, name, out var sample) || sample.isImported) continue;
                if (sample.Import()) Debug.Log($"[StageSettAR] Imported {package} sample '{name}'");
                else Debug.LogWarning($"[StageSettAR] Could not import {package} sample '{name}'.");
            }
            s_checked = double.NegativeInfinity;
        }

        static SetupStatus Evaluate(out string detail)
        {
            var versions = Versions();
            var missingPackages = k_samples.Select(s => s.package).Distinct().Where(p => !versions.ContainsKey(p)).ToList();
            if (missingPackages.Count > 0)
            {
                detail = "Install the XR packages first (needs " + string.Join(", ", missingPackages) + ").";
                return SetupStatus.Blocked;
            }

            var todo = new List<string>();
            var unavailable = new List<string>();
            try
            {
                foreach (var (package, name) in k_samples)
                {
                    if (!TryFind(package, versions[package], name, out var sample)) unavailable.Add(name);
                    else if (!sample.isImported) todo.Add(name);
                }
            }
            catch (System.Exception e)
            {
                detail = "Package Manager could not list the samples yet: " + e.Message;
                return SetupStatus.Blocked;
            }

            if (todo.Count > 0)
            {
                detail = "Import " + string.Join(", ", todo) + " (controller and hand prefabs, input actions, XR Interaction Simulator).";
                return SetupStatus.Needed;
            }
            if (unavailable.Count > 0)
            {
                detail = "The installed packages have no sample named " + string.Join(", ", unavailable) + ". Update them in Package Manager.";
                return SetupStatus.Blocked;
            }
            detail = "HandVisualizer, Starter Assets, Hands Interaction Demo and XR Interaction Simulator are imported.";
            return SetupStatus.Done;
        }

        static Dictionary<string, string> Versions()
        {
            var ids = k_samples.Select(s => s.package).ToHashSet();
            var versions = new Dictionary<string, string>();
            foreach (var p in PackageInfo.GetAllRegisteredPackages())
                if (ids.Contains(p.name)) versions[p.name] = p.version;
            return versions;
        }

        static bool TryFind(string package, string version, string displayName, out Sample found)
        {
            foreach (var sample in Sample.FindByPackage(package, version))
            {
                if (sample.displayName != displayName) continue;
                found = sample;
                return true;
            }
            found = default;
            return false;
        }
    }
}
