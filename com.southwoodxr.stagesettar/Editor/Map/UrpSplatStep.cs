using System.Collections.Generic;
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
    /// Makes every editable URP renderer sort splats with <see cref="StageSettARSplatURPFeature"/>
    /// and deactivates UnitySplats' own feature, so splats are sorted once.
    /// </summary>
    public sealed class UrpSplatStep : ISetupStep
    {
        const string k_gsplatFeature = "Gsplat.GsplatURPFeature";

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
                var renderers = FindRenderers();
                var pending = 0;
                foreach (var r in renderers) if (NeedsWork(r)) pending++;
                if (renderers.Count == 0) { s_status = SetupStatus.Blocked; s_detail = "No URP renderer asset found in Assets/."; }
                else if (pending > 0) { s_status = SetupStatus.Needed; s_detail = $"{pending} of {renderers.Count} URP renderer(s) still need the StageSettAR splat feature."; }
                else { s_status = SetupStatus.Done; s_detail = $"{renderers.Count} URP renderer(s) are set up."; }
            }
            detail = s_detail;
            return s_status;
        }

        public void Run() => Configure();

        /// <summary>Used by the setup window, the import dialog and the menu.</summary>
        public static void Configure()
        {
            var renderers = FindRenderers();
            if (renderers.Count == 0)
                Debug.LogWarning("[StageSettAR] No editable URP renderer data found; nothing to configure.");

            foreach (var data in renderers)
            {
                var changed = false;
                var has = false;
                foreach (var f in data.rendererFeatures)
                {
                    if (!f) continue;
                    if (f is StageSettARSplatURPFeature)
                    {
                        has = true;
                        if (!f.isActive) { f.SetActive(true); EditorUtility.SetDirty(f); changed = true; }
                    }
                    else if (f.GetType().FullName == k_gsplatFeature && f.isActive)
                    {
                        f.SetActive(false);
                        EditorUtility.SetDirty(f);
                        changed = true;
                        Debug.Log($"[StageSettAR] Deactivated Gsplat URP Feature in {data.name}", data);
                    }
                }
                if (!has)
                {
                    AddFeature(data);
                    changed = true;
                    Debug.Log($"[StageSettAR] Added StageSettAR Splat Sort feature to {data.name}", data);
                }
                if (changed) { EditorUtility.SetDirty(data); AssetDatabase.SaveAssets(); }
            }
            s_checked = double.NegativeInfinity;

            var settings = GsplatSettings.Instance;
            if (!settings || !settings.Valid)
                Debug.LogWarning("[StageSettAR] GsplatSettings is missing or invalid; splats will not render. Check Project Settings > Gsplat.");
        }

        static bool NeedsWork(ScriptableRendererData data)
        {
            var has = false;
            foreach (var f in data.rendererFeatures)
            {
                if (!f) continue;
                if (f is StageSettARSplatURPFeature) has |= f.isActive;
                else if (f.GetType().FullName == k_gsplatFeature && f.isActive) return true;
            }
            return !has;
        }

        // Renderers of the project's pipeline assets plus every UniversalRendererData asset, minus read-only package ones.
        static List<ScriptableRendererData> FindRenderers()
        {
            var set = new HashSet<ScriptableRendererData>();
            void Add(ScriptableRendererData r)
            {
                if (r && EditorUtility.IsPersistent(r) && IsEditable(AssetDatabase.GetAssetPath(r))) set.Add(r);
            }
            void AddPipeline(RenderPipelineAsset a)
            {
                if (a is UniversalRenderPipelineAsset u) foreach (var r in u.rendererDataList) Add(r);
            }
            AddPipeline(GraphicsSettings.defaultRenderPipeline);
            for (var i = 0; i < QualitySettings.names.Length; i++) AddPipeline(QualitySettings.GetRenderPipelineAssetAt(i));
            foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
                Add(AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(AssetDatabase.GUIDToAssetPath(guid)));
            return new List<ScriptableRendererData>(set);
        }

        static bool IsEditable(string path)
        {
            if (path.StartsWith("Assets/", System.StringComparison.Ordinal)) return true;
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
