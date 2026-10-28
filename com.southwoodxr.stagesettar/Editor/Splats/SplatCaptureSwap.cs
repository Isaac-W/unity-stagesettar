using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gsplat;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Lets a bake capture depth out of stock UnitySplats.
    ///
    /// Baking shells needs two things UnitySplats does not do: sort splats by distance from the
    /// camera, so the six cube faces of a panorama agree at their seams, and draw distance instead
    /// of colour, so a depth panorama can be recorded through the same coverage and blending as the
    /// colour one. This package carries both as variants of upstream's own shaders (see
    /// Editor/Splats/Shaders) and, for the length of a bake only, swaps them in through
    /// UnitySplats' public settings: <see cref="GsplatSettings.Materials"/> is replaced entry by
    /// entry with in-memory <see cref="GsplatMaterial"/> objects whose DefaultMaterial uses the
    /// capture shader and whose CalcDepthShader is the radial variant. Everything the renderers do
    /// reads those through <c>GsplatAsset.GsplatMaterial</c> on every call, so no renderer, asset
    /// or scene object has to be touched.
    ///
    /// Restoring is guarded three ways: <see cref="Handle.Dispose"/> from a finally block, a
    /// SessionState record of the original asset GUIDs that is replayed after a domain reload,
    /// and a repair of any empty settings entry from UnitySplats' default assets, which covers a
    /// settings file that was saved while swapped.
    /// </summary>
    [InitializeOnLoad]
    static class SplatCaptureSwap
    {
        public const string DepthKeyword = "GSPLAT_DEPTH_CAPTURE";

        const string k_gsplatPackage = "com.arloopa.unitysplats";
        const string k_captureShader = "Hidden/StageSettAR/GsplatCapture";
        const string k_activeKey = "StageSettAR.SplatCaptureSwap.Active";
        const string k_originalsKey = "StageSettAR.SplatCaptureSwap.Originals";
        const string k_globalSortKey = "StageSettAR.SplatCaptureSwap.GlobalSort";

        // The capture shaders #include these from the installed UnitySplats; their absence means
        // an older UnitySplats (before the skinning and volume lighting changes of 2.0), whose
        // shaders the variants do not match.
        static readonly string[] k_requiredIncludes =
        {
            "Runtime/Shaders/Gsplat.hlsl", "Runtime/Shaders/GsplatSkinning.hlsl",
            "Runtime/Shaders/GsplatVolumeLighting.hlsl"
        };

        static Handle s_active;

        static SplatCaptureSwap()
        {
            // The settings asset is not safe to touch while the editor is still loading.
            EditorApplication.delayCall += RestoreLeftover;
        }

        /// <summary>
        /// Whether this UnitySplats can be swapped to capture variants. When it cannot, a bake
        /// falls back to colour only: no depth panorama, no reprojection, and planar sorting.
        /// </summary>
        public static bool IsSupported(out string reason)
        {
            reason = null;
            var settings = GsplatSettings.Instance;
            if (!settings || settings.Materials == null)
            {
                reason = "UnitySplats settings are missing.";
                return false;
            }

            var package = PackageInfo.FindForAssembly(typeof(GsplatSettings).Assembly);
            if (package == null || package.name != k_gsplatPackage)
            {
                reason = $"UnitySplats is not installed as the {k_gsplatPackage} package.";
                return false;
            }

            foreach (string include in k_requiredIncludes)
                if (!File.Exists(Path.Combine(package.resolvedPath, include)))
                {
                    reason = $"This UnitySplats ({package.version}) predates the shaders the capture " +
                             $"variants build on (missing {include}). UnitySplats 2.0 is supported.";
                    return false;
                }

            var shader = Shader.Find(k_captureShader);
            if (!shader || !shader.isSupported)
            {
                reason = $"The capture shader '{k_captureShader}' is missing or not supported on this " +
                         "graphics API. Check the console for shader compile errors.";
                return false;
            }

            if (!SystemInfo.supportsComputeShaders || LoadCompute(CompressionMode.Spark) == null ||
                LoadCompute(CompressionMode.Uncompressed) == null)
            {
                reason = "The radial-depth compute shaders could not be loaded.";
                return false;
            }

            foreach (CompressionMode mode in Enum.GetValues(typeof(CompressionMode)))
            {
                int slot = (int)mode;
                if (slot >= settings.Materials.Length || !settings.Materials[slot] ||
                    !settings.Materials[slot].DefaultMaterial)
                {
                    reason = $"UnitySplats has no material for {mode} splats.";
                    return false;
                }
            }

            return true;
        }

        static ComputeShader LoadCompute(CompressionMode mode)
        {
            var own = PackageInfo.FindForAssembly(typeof(SplatCaptureSwap).Assembly);
            if (own == null)
                return null;
            string file = mode == CompressionMode.Spark
                ? "StageSettARCalcDepthSparkCapture.compute"
                : "StageSettARCalcDepthCapture.compute";
            return AssetDatabase.LoadAssetAtPath<ComputeShader>($"{own.assetPath}/Editor/Splats/Shaders/{file}");
        }

        /// <summary>Swaps the capture variants in. Dispose the result to put UnitySplats back.</summary>
        public static Handle Begin()
        {
            if (s_active != null)
                throw new InvalidOperationException("A capture swap is already active.");
            if (!IsSupported(out string reason))
                throw new InvalidOperationException(reason);

            s_active = new Handle();
            return s_active;
        }

        sealed class Swapped
        {
            public int Slot;
            public GsplatMaterial Original;
            public GsplatMaterial Capture;
        }

        public sealed class Handle : IDisposable
        {
            readonly List<Swapped> m_swapped = new();
            readonly bool m_globalSort;
            bool m_disposed;

            internal Handle()
            {
                var settings = GsplatSettings.Instance;
                var shader = Shader.Find(k_captureShader);
                var guids = new StringBuilder();

                m_globalSort = settings.EnableGlobalSort;
                SessionState.SetBool(k_globalSortKey, m_globalSort);

                try
                {
                    foreach (CompressionMode mode in Enum.GetValues(typeof(CompressionMode)))
                    {
                        int slot = (int)mode;
                        var original = settings.Materials[slot];
                        string path = AssetDatabase.GetAssetPath(original);
                        guids.Append(slot).Append('=').Append(AssetDatabase.AssetPathToGUID(path)).Append(';');

                        var capture = ScriptableObject.CreateInstance<GsplatMaterial>();
                        capture.name = original.name + " (capture)";
                        capture.hideFlags = HideFlags.HideAndDontSave;
                        capture.DefaultMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        // The keywords pick the data layout and SH band; the clones GsplatMaterial
                        // makes from this carry them along.
                        foreach (string keyword in original.DefaultMaterial.shaderKeywords)
                            capture.DefaultMaterial.EnableKeyword(keyword);
                        capture.DefaultMaterial.renderQueue = original.DefaultMaterial.renderQueue;
                        capture.CalcDepthShader = LoadCompute(mode);
                        capture.InitOrderShader = original.InitOrderShader;
                        m_swapped.Add(new Swapped { Slot = slot, Original = original, Capture = capture });
                    }

                    // Persisted before anything is replaced, so a reload at any point after this
                    // can find its way back.
                    SessionState.SetString(k_originalsKey, guids.ToString());
                    SessionState.SetBool(k_activeKey, true);

                    foreach (var swapped in m_swapped)
                        settings.Materials[swapped.Slot] = swapped.Capture;
                    // Merging renderers into one draw would bypass the swapped materials, and
                    // with only the baked splat visible it never engages anyway. Off to be sure.
                    settings.EnableGlobalSort = false;
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            /// <summary>Turns the depth-writing fragment output on or off for every capture material.</summary>
            public void SetDepthCapture(bool on)
            {
                foreach (var swapped in m_swapped)
                    foreach (var row in swapped.Capture.Materials)
                        foreach (var material in row)
                            if (material)
                            {
                                if (on)
                                    material.EnableKeyword(DepthKeyword);
                                else
                                    material.DisableKeyword(DepthKeyword);
                            }
            }

            public void Dispose()
            {
                if (m_disposed)
                    return;
                m_disposed = true;

                var settings = GsplatSettings.Instance;
                if (settings)
                {
                    foreach (var swapped in m_swapped)
                        if (swapped.Slot < settings.Materials.Length)
                            settings.Materials[swapped.Slot] = swapped.Original;
                    settings.EnableGlobalSort = m_globalSort;
                }

                foreach (var swapped in m_swapped)
                {
                    foreach (var row in swapped.Capture.Materials)
                        foreach (var material in row)
                            if (material)
                                UnityEngine.Object.DestroyImmediate(material);
                    UnityEngine.Object.DestroyImmediate(swapped.Capture.DefaultMaterial);
                    UnityEngine.Object.DestroyImmediate(swapped.Capture);
                }

                m_swapped.Clear();
                SessionState.EraseBool(k_activeKey);
                SessionState.EraseString(k_originalsKey);
                SessionState.EraseBool(k_globalSortKey);
                s_active = null;
            }
        }

        /// <summary>
        /// After a domain reload or an editor restart the Handle is gone, but the settings asset
        /// may still hold the in-memory capture entries (or nothing, if it was saved that way).
        /// </summary>
        static void RestoreLeftover()
        {
            if (s_active != null)
                return;

            var settings = GsplatSettings.Instance;
            if (!settings || settings.Materials == null)
                return;

            if (SessionState.GetBool(k_activeKey, false))
            {
                foreach (string pair in SessionState.GetString(k_originalsKey, "").Split(';'))
                {
                    int split = pair.IndexOf('=');
                    if (split < 0 || !int.TryParse(pair.Substring(0, split), out int slot) ||
                        slot < 0 || slot >= settings.Materials.Length)
                        continue;

                    string path = AssetDatabase.GUIDToAssetPath(pair.Substring(split + 1));
                    var original = string.IsNullOrEmpty(path)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<GsplatMaterial>(path);
                    if (original)
                        settings.Materials[slot] = original;
                }

                settings.EnableGlobalSort = SessionState.GetBool(k_globalSortKey, settings.EnableGlobalSort);
                SessionState.EraseBool(k_activeKey);
                SessionState.EraseString(k_originalsKey);
                SessionState.EraseBool(k_globalSortKey);
                Debug.LogWarning("[StageSettAR] A shell bake was interrupted; UnitySplats' materials were restored.");
            }

            // A settings file saved while swapped has lost the in-memory entries. Put back the
            // stock assets the package itself would have chosen.
            for (int slot = 0; slot < settings.Materials.Length; slot++)
            {
                if (settings.Materials[slot])
                    continue;
                string name = slot == (int)CompressionMode.Spark ? "GsplatSpark" : "GsplatUncompressed";
                var stock = AssetDatabase.LoadAssetAtPath<GsplatMaterial>(
                    $"Packages/{k_gsplatPackage}/Runtime/Materials/{name}.asset");
                if (stock)
                {
                    settings.Materials[slot] = stock;
                    Debug.LogWarning($"[StageSettAR] UnitySplats had no material for slot {slot}; restored the default.");
                }
            }
        }
    }
}
