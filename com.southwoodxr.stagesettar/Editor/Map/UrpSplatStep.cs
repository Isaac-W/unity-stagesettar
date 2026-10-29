using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Gsplat;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Makes every URP renderer that the project can actually use (Graphics settings and every quality
    /// level) sort splats with <see cref="StageSettARSplatURPFeature"/>, and deactivates UnitySplats' own
    /// feature, so splats are sorted once.
    /// </summary>
    /// <remarks>
    /// Lives in the gated Map assembly, so it only exists once UnitySplats is installed; the Setup window
    /// finds it through TypeCache like every other step. A renderer whose StageSettAR feature was switched
    /// off on purpose is reported but left alone.
    /// </remarks>
    public sealed class UrpSplatStep : ISetupStep
    {
        const string k_gsplatFeature = "Gsplat.GsplatURPFeature";

        enum State
        {
            Ready,
            /// <summary>Our feature is present but switched off by the user.</summary>
            Disabled,
            /// <summary>Our feature is absent, or the stock one is also active.</summary>
            NeedsWork,
        }

        static SetupStatus s_status;
        static string s_detail = "";
        static double s_checked = double.NegativeInfinity;

        public int Order => 20;
        public string Title => "Splat rendering (URP)";
        public string ActionLabel => "Configure";

        public SetupStatus GetStatus(out string detail)
        {
            // Loading renderer assets is not free and this runs per repaint; the project can change under us, so re-check every 2 s.
            if (EditorApplication.timeSinceStartup - s_checked > 2)
            {
                s_checked = EditorApplication.timeSinceStartup;
                s_status = Evaluate(out s_detail);
            }
            detail = s_detail;
            return s_status;
        }

        static SetupStatus Evaluate(out string detail)
        {
            var renderers = UsedRenderers();
            if (renderers.Count == 0)
            {
                detail = "No URP renderer is assigned in Project Settings > Graphics or Quality.";
                return SetupStatus.Blocked;
            }

            var fixable = 0;
            var locked = new List<string>();
            var disabled = new List<string>();
            foreach (var r in renderers)
            {
                var state = Inspect(r);
                if (state == State.Disabled) disabled.Add(r.name);
                else if (state == State.NeedsWork)
                {
                    if (IsEditable(r)) fixable++;
                    else locked.Add(r.name);
                }
            }

            var note = "";
            if (disabled.Count > 0) note += $" Switched off on purpose, left as is: {string.Join(", ", disabled)}.";
            if (locked.Count > 0) note += $" Cannot be edited (read-only or in a package): {string.Join(", ", locked)}. Assign a renderer from Assets/ in Quality settings.";

            if (fixable > 0)
            {
                detail = $"{fixable} of {renderers.Count} URP renderer(s) still need the StageSettAR splat feature.{note}";
                return SetupStatus.Needed;
            }
            if (locked.Count > 0)
            {
                detail = $"{locked.Count} of {renderers.Count} URP renderer(s) need the StageSettAR splat feature.{note}";
                return SetupStatus.Blocked;
            }
            detail = $"{renderers.Count} URP renderer(s) are set up.{note}";
            return SetupStatus.Done;
        }

        public void Run()
        {
            var changed = false;
            foreach (var data in UsedRenderers())
            {
                if (Inspect(data) != State.NeedsWork) continue;
                if (!IsEditable(data))
                {
                    Debug.LogWarning($"[StageSettAR] {data.name} is read-only or in a package; add the StageSettAR Splat Sort feature to it by hand, or use a renderer from Assets/.", data);
                    continue;
                }

                var hasOurs = false;
                foreach (var f in data.rendererFeatures)
                {
                    if (!f) continue;
                    if (f is StageSettARSplatURPFeature) hasOurs = true;
                    else if (IsStock(f) && f.isActive)
                    {
                        f.SetActive(false);
                        EditorUtility.SetDirty(f);
                        Debug.Log($"[StageSettAR] Deactivated Gsplat URP Feature in {data.name}", data);
                    }
                }
                if (!hasOurs)
                {
                    AddFeature(data);
                    Debug.Log($"[StageSettAR] Added StageSettAR Splat Sort feature to {data.name}", data);
                }
                EditorUtility.SetDirty(data);
                changed = true;
            }
            if (changed) AssetDatabase.SaveAssets();
            s_checked = double.NegativeInfinity;

            var settings = GsplatSettings.Instance;
            if (!settings || !settings.Valid)
                Debug.LogWarning("[StageSettAR] GsplatSettings is missing or invalid; splats will not render. Check Project Settings > Gsplat.");
        }

        static bool IsStock(ScriptableRendererFeature f) => f.GetType().FullName == k_gsplatFeature;

        static State Inspect(ScriptableRendererData data)
        {
            bool ours = false, oursActive = false, stockActive = false;
            foreach (var f in data.rendererFeatures)
            {
                if (!f) continue;
                if (f is StageSettARSplatURPFeature) { ours = true; oursActive |= f.isActive; }
                else if (IsStock(f) && f.isActive) stockActive = true;
            }
            if (ours && !oursActive) return State.Disabled;
            return oursActive && !stockActive ? State.Ready : State.NeedsWork;
        }

        // Every renderer of the default pipeline asset and of each quality level's asset. A quality level
        // without its own asset uses the default one, which is already included.
        static List<ScriptableRendererData> UsedRenderers()
        {
            var list = new List<ScriptableRendererData>();
            var seen = new HashSet<RenderPipelineAsset>();
            void AddPipeline(RenderPipelineAsset a)
            {
                if (a is not UniversalRenderPipelineAsset || !seen.Add(a)) return;
                // Read through the serialized list: it holds every slot, not only the ones the pipeline has built.
                var slots = new SerializedObject(a).FindProperty("m_RendererDataList");
                for (var i = 0; slots != null && i < slots.arraySize; i++)
                    if (slots.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableRendererData r && !list.Contains(r))
                        list.Add(r);
            }
            AddPipeline(GraphicsSettings.defaultRenderPipeline);
            for (var i = 0; i < QualitySettings.names.Length; i++) AddPipeline(QualitySettings.GetRenderPipelineAssetAt(i));
            return list;
        }

        static bool IsEditable(ScriptableRendererData data)
        {
            if (!EditorUtility.IsPersistent(data)) return false;
            var path = AssetDatabase.GetAssetPath(data);
            if (path.StartsWith("Assets/", System.StringComparison.Ordinal)) return !new FileInfo(path).IsReadOnly;
            var pkg = PackageInfo.FindForAssetPath(path);
            return pkg != null && pkg.source is PackageSource.Embedded or PackageSource.Local;
        }

        // Mirrors URP's ScriptableRendererDataEditor.AddComponent: sub-asset + feature list + local-id map.
        static void AddFeature(ScriptableRendererData data)
        {
            var feature = ScriptableObject.CreateInstance<StageSettARSplatURPFeature>();
            feature.name = nameof(StageSettARSplatURPFeature);
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(data);
            var list = so.FindProperty("m_RendererFeatures");
            var ids = so.FindProperty("m_RendererFeatureMap");
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
            ids.arraySize++;
            ids.GetArrayElementAtIndex(ids.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
