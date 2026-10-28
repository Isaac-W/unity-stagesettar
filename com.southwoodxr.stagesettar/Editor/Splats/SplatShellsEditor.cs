
using System.Collections.Generic;
using System.IO;
using Gsplat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Inspector and baker for <see cref="SplatShells"/>; ported from the UnitySplats fork's
    /// GsplatBackdropEditor. The one real change is how depth is captured: the fork patched
    /// UnitySplats' core, this swaps in capture variants of its shaders for the length of the bake
    /// (see <see cref="SplatCaptureSwap"/>), so stock UnitySplats 2.0 is enough.
    /// </summary>
    [CustomEditor(typeof(SplatShells), true)]
    public class SplatShellsEditor : UnityEditor.Editor
    {
        const string k_shaderName = "StageSettAR/Shells";
        const string k_stitchShaderName = "Hidden/StageSettAR/ShellStitch";
        const int k_stitchPass = 0;
        const int k_unpremultiplyPass = 1;
        const int k_dilatePass = 2;
        const int k_resolveDepthPass = 3;
        const int k_smoothDepthPass = 4;
        static readonly int k_depthCaptureNear = Shader.PropertyToID("_DepthCaptureNear");
        static readonly int k_depthCaptureFar = Shader.PropertyToID("_DepthCaptureFar");
        static readonly int k_depthCaptureCutoff = Shader.PropertyToID("_DepthCaptureCutoff");

        // ------------------------------------------------------------------ entry points

        const string k_defaultRoot = "Assets/StageSettAR/Shells";

        [MenuItem("Tools/StageSettAR/Bake Panoramic Shells", false, 120)]
        static void BakeSelected()
        {
            var renderer = SelectedRenderer();
            if (!renderer)
                return;
            Selection.activeGameObject = BakeFor(renderer).gameObject;
        }

        /// <summary>
        /// Bakes shells for a splat in the open scene, the same as the menu item does for the
        /// selection. The bake runs over the next editor frames, not before this returns.
        /// </summary>
        public static SplatShells BakeFor(GsplatRenderer renderer)
        {
            // Whatever shells component is already on the splat keeps its settings; otherwise add
            // the reprojecting one with its defaults, which is what a student almost always wants.
            var shells = renderer.GetComponentInChildren<SplatShells>();
            if (!shells)
                shells = Undo.AddComponent<SplatShellReprojection>(renderer.gameObject);
            shells.Renderer = renderer;
            Bake(shells, renderer);
            return shells;
        }

        [MenuItem("Tools/StageSettAR/Bake Panoramic Shells", true)]
        static bool BakeSelectedValid() => SelectedRenderer() != null;

        static GsplatRenderer SelectedRenderer()
        {
            var selected = Selection.activeGameObject;
            if (!selected)
                return null;
            var renderer = selected.GetComponentInParent<GsplatRenderer>();
            return renderer ? renderer : selected.GetComponentInChildren<GsplatRenderer>();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "Shells", "BakedOriginLocal");
            serializedObject.ApplyModifiedProperties();

            var shells = (SplatShells)target;
            var renderer = shells.ResolveRenderer();

            EditorGUILayout.Space();
            DrawDiagnostics(shells, renderer);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(renderer == null || targets.Length > 1))
            {
                if (GUILayout.Button(shells.HasBake ? "Re-bake Panoramic Shells" : "Bake Panoramic Shells", GUILayout.Height(28)))
                    Bake(shells, renderer);
            }

            using (new EditorGUI.DisabledScope(!shells.HasBake))
            {
                if (GUILayout.Button("Revert Bake"))
                {
                    var choice = PromptForAssets(shells, "Revert Bake",
                        "The shells will be removed from the scene.");
                    if (choice != AssetChoice.Cancel)
                        Revert(shells, choice == AssetChoice.Delete);
                }
            }
        }

        static void DrawDiagnostics(SplatShells shells, GsplatRenderer renderer)
        {
            if (renderer == null)
            {
                EditorGUILayout.HelpBox("No GsplatRenderer found on this object or its parents.", MessageType.Error);
                return;
            }

            if (!SplatCaptureSwap.IsSupported(out string unsupported))
                EditorGUILayout.HelpBox(
                    "Depth capture is unavailable, so a bake will produce colour-only shells: no " +
                    "depth panorama and no reprojection, and the cube-face joins may show faintly.\n\n" +
                    unsupported,
                    MessageType.Warning);

            if (shells.CaptureOrigin == null)
                EditorGUILayout.HelpBox(
                    "No capture origin set, so the bake will capture from this object's position. " +
                    "A panorama is only correct from the point it was captured; set this to where " +
                    "the viewer's head will actually be.",
                    MessageType.Warning);

            EditorGUILayout.LabelField(
                "Parallax at start radius",
                $"{shells.ParallaxErrorDegrees(0.75f):F1}° seated  •  " +
                $"{shells.ParallaxErrorDegrees(1.5f):F1}° roomscale");
            EditorGUILayout.LabelField("Panorama", $"{shells.Resolution} x {shells.Resolution / 2}");
            float megabytes = shells.BandCount * shells.Resolution *
                              (shells.Resolution / 2) * 0.35f / (1024 * 1024);
            EditorGUILayout.LabelField("Memory", $"~{megabytes:F0} MB compressed");
            if (megabytes > 64f)
                EditorGUILayout.HelpBox(
                    $"{shells.BandCount} shells at this resolution is about {megabytes:F0} MB of " +
                    "texture, and every shell is a full-screen transparent layer to blend. Shell " +
                    "count and resolution trade against each other: more, smaller shells usually " +
                    "read better than fewer sharp ones, because the far field is blurry anyway.",
                    MessageType.Warning);

            if (shells.ParallaxCompensation > 0f)
                DrawParallaxDiagnostics(shells);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shells", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                for (int band = 0; band < shells.BandCount; band++)
                {
                    float outer = shells.BandOuterRadius(band);
                    EditorGUILayout.LabelField(
                        $"{band}",
                        $"covers {shells.BandCaptureInnerRadius(band):F1} – {(outer > 0f ? $"{outer:F1}" : "∞")} m" +
                        $"   •   hung at {shells.BandShellRadius(band):F1} m" +
                        $"   •   follows {shells.BandParallaxFactor(band) * shells.ParallaxCompensation:P0}" +
                        $"   •   queue {shells.BandRenderQueue(band)}");
                }
            }

            // Gradient cutouts thin the far field, so baking through one captures a sparse sky --
            // and once it is a panorama there is no splat count left for the thinning to save.
            foreach (var cutout in renderer.Cutouts)
            {
                // By name: stock UnitySplats has no Gradient type, forks do, and a name compares
                // fine either way.
                if (cutout.m_Type.ToString() != "Gradient")
                    continue;

                EditorGUILayout.HelpBox(
                    $"'{cutout.name}' is a Gradient cutout and is thinning the far field. Existing " +
                    "cutouts are kept during the bake, so the panorama will capture the thinned " +
                    "result and look sparse. Since the panorama costs the same however dense it is, " +
                    "consider disabling that cutout for the bake, or shrinking it inside the cut radius.",
                    MessageType.Warning);
                break;
            }
        }

        /// <summary>
        /// Parallax compensation fails silently in three ways -- a bake older than the feature, a
        /// static editor, or no viewer to track -- and all three look identical from the outside:
        /// nothing moves. Say which.
        /// </summary>
        static void DrawParallaxDiagnostics(SplatShells shells)
        {
            if (shells.HasBake && shells.Shells.TrueForAll(shell => shell == null || shell.ParallaxFactor <= 0f))
            {
                EditorGUILayout.HelpBox(
                    "These shells were baked before parallax compensation existed, so they carry no " +
                    "follow factor and will not move. Re-bake.",
                    MessageType.Warning);
                return;
            }

            var viewer = shells.Viewer
                ? shells.Viewer
                : Camera.main ? Camera.main.transform : null;

            if (!viewer)
            {
                EditorGUILayout.HelpBox(
                    "No viewer. The Viewer field is empty and there is no camera tagged MainCamera " +
                    "to fall back on, so the shells have nothing to follow. Assign Viewer.",
                    MessageType.Warning);
                return;
            }

            // Whether the effect is even perceptible is a separate question from whether it is
            // running, and at these radii it is easy to be looking for something far too large.
            float shift = shells.Shells.Count > 0
                ? Mathf.Atan2(shells.Shells[0].ParallaxFactor * shells.ParallaxCompensation,
                    Mathf.Max(shells.Shells[0].SphereRadius, 0.001f)) * Mathf.Rad2Deg
                : 0f;
            EditorGUILayout.LabelField("Innermost shift", $"{shift:F2}° per metre of movement");
        }

        // ------------------------------------------------------------------ bake

        static void Bake(SplatShells shells, GsplatRenderer renderer)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Bake Panoramic Shells", "Stop play mode before baking.", "OK");
                return;
            }

            if (!renderer.GsplatAsset)
            {
                EditorUtility.DisplayDialog("Bake Panoramic Shells", "The renderer has no splat asset assigned.", "OK");
                return;
            }

            var choice = PromptForAssets(shells, "Re-bake Panoramic Shells",
                "The existing shells will be replaced.");
            if (choice == AssetChoice.Cancel)
                return;

            Revert(shells, choice == AssetChoice.Delete);

            // Forget the previous bake's files rather than accumulating them. Reverting should
            // only ever offer to undo the bake that produced what is currently in the scene;
            // sweeping up older runs as well would be a bigger, less predictable action than the
            // button appears to be, and those files may be in use elsewhere by now.
            shells.GeneratedAssetPaths.Clear();
            new BakeJob(shells, renderer).Start();
        }

        /// <summary>
        /// The bake has to straddle editor frames. GsplatRenderer.Update only rebuilds the cutout
        /// order; the draw itself is submitted by GsplatSorter from the player loop, so capturing
        /// inside the button handler would render a submission made before the capture cutout
        /// existed and bake the whole splat into the panorama. Each band therefore configures its
        /// cutouts, pumps the player loop until the surviving count settles, and only then
        /// captures, before moving on to the next band.
        /// </summary>
        class BakeJob
        {
            enum Step { ConfigureBand, Settle, CaptureBand, Build }

            const int k_maxTicksPerBand = 400;
            const int k_stableTicksRequired = 3;
            // Submissions made before the cutout changed are still live for the current frame.
            const int k_settleTicksAfterStable = 2;

            readonly SplatShells m_shells;
            readonly GsplatRenderer m_renderer;
            readonly RestoreState m_restore;
            readonly bool m_depthSupported;
            SplatCaptureSwap.Handle m_swap;
            readonly Vector3 m_origin;
            readonly string m_folder;
            readonly int m_bandCount;
            readonly List<BandResult> m_results = new();
            readonly List<GsplatCutout> m_bandCutouts = new();

            Step m_step = Step.ConfigureBand;
            int m_band;
            uint m_previousCount = uint.MaxValue;
            int m_stableTicks;
            int m_extraTicks;
            int m_ticks;
            bool m_finished;
            bool m_locked;

            struct BandResult
            {
                public int Index;
                public PanoramaPaths Paths;
                public string DepthPath;
            }

            public BakeJob(SplatShells shells, GsplatRenderer renderer)
            {
                m_shells = shells;
                m_renderer = renderer;
                m_restore = new RestoreState(renderer);
                m_origin = shells.CaptureOrigin
                    ? shells.CaptureOrigin.position
                    : shells.transform.position;
                m_folder = ResolveOutputFolder(shells, renderer);
                m_bandCount = shells.BandCount;
                m_depthSupported = SplatCaptureSwap.IsSupported(out string unsupported);
                if (!m_depthSupported)
                    Debug.LogWarning("[SplatShells] Baking colour-only shells (no depth, no reprojection). " +
                                     unsupported);
            }

            public void Start()
            {
                try
                {
                    // The bake spans many editor frames and keeps scene state in this object, so a
                    // script reload or entering play mode would strand it half done.
                    EditorApplication.LockReloadAssemblies();
                    m_locked = true;
                    EditorApplication.playModeStateChanged += OnPlayModeChanged;

                    // Sorting by distance from the camera rather than depth along its forward axis
                    // gives every cube face one shared order, so the same direction composites
                    // identically on both sides of a face boundary. Runtime rendering is untouched:
                    // the capture variants are only installed until Finish.
                    if (m_depthSupported)
                        m_swap = SplatCaptureSwap.Begin();
                    m_restore.HideEverythingExcept(m_renderer.transform, m_shells.transform);
                    m_restore.ApplyCaptureQuality(m_shells.BakeAtFullQuality);
                    EditorApplication.update += Tick;
                }
                catch (System.Exception exception)
                {
                    Fail(exception);
                }
            }

            void Tick()
            {
                try
                {
                    switch (m_step)
                    {
                        case Step.ConfigureBand: ConfigureBand(); break;
                        case Step.Settle: Settle(); break;
                        case Step.CaptureBand: CaptureBand(); break;
                        case Step.Build: Build(); break;
                    }
                }
                catch (System.Exception exception)
                {
                    Fail(exception);
                }
            }

            void ConfigureBand()
            {
                ClearBandCutouts();

                // The capture reaches slightly inside the shell in front, so a surface crossing the
                // join appears on both and ghosts rather than tears. The shell itself still sits at
                // the nominal inner radius.
                float inner = m_shells.BandCaptureInnerRadius(m_band);
                float outer = m_shells.BandOuterRadius(m_band);

                // Existing cutouts stay exactly as authored: anything already trimmed away should
                // stay trimmed rather than reappearing in a panorama. These are layered on top,
                // and because IsSplatCut unions the cuts, an inverted sphere at the inner radius
                // plus a normal one at the outer radius leaves precisely the band between them.
                m_bandCutouts.Add(CreateCutout(m_renderer, m_origin, inner, invert: true,
                    "__StageSettARShellBandInner"));
                if (outer > 0f)
                    m_bandCutouts.Add(CreateCutout(m_renderer, m_origin, outer, invert: false,
                        "__StageSettARShellBandOuter"));

                m_renderer.ForceRefresh();
                m_previousCount = uint.MaxValue;
                m_stableTicks = 0;
                m_extraTicks = 0;
                m_ticks = 0;
                m_step = Step.Settle;
            }

            void Settle()
            {
                if (++m_ticks > k_maxTicksPerBand)
                    throw new System.TimeoutException(
                        "The splat upload and cutout pass did not settle. Check that the renderer " +
                        "is enabled and its asset finished importing.");

                Progress($"Band {m_band + 1} of {m_bandCount}: rebuilding splat order…");

                m_renderer.Update();
                EditorApplication.QueuePlayerLoopUpdate();

                bool uploaded = m_renderer.SplatCount == m_renderer.GsplatAsset.SplatCount;
                uint remaining = m_renderer.ResidentActiveSplatCount;
                m_stableTicks = uploaded && remaining == m_previousCount ? m_stableTicks + 1 : 0;
                m_previousCount = remaining;

                if (m_stableTicks < k_stableTicksRequired)
                    return;
                if (m_extraTicks++ < k_settleTicksAfterStable)
                    return;

                m_step = Step.CaptureBand;
            }

            void CaptureBand()
            {
                // An empty band is not a failure: a ratio that overshoots the cloud's extent just
                // means the outer bands have nothing in them. Skip rather than bake a blank shell.
                if (m_previousCount == 0)
                {
                    Debug.Log($"[SplatShells] Band {m_band + 1} contains no splats; skipping.");
                }
                else
                {
                    Progress($"Band {m_band + 1} of {m_bandCount}: capturing panorama…");
                    var result = new BandResult { Index = m_band, Paths = CaptureBandTexture(m_band) };
                    if (m_depthSupported && m_shells.NeedsDepthCapture(m_band))
                        result.DepthPath = CaptureBandDepth(m_band);
                    m_results.Add(result);
                }

                ClearBandCutouts();
                m_band++;
                m_step = m_band < m_bandCount ? Step.ConfigureBand : Step.Build;
            }

            /// <summary>
            /// Second pass over the same band with the splat shader emitting distance instead of
            /// colour. Same geometry, same coverage, same blend, so depth lines up with colour
            /// texel for texel.
            ///
            /// The draw is submitted by hand immediately beforehand. Splats go through
            /// Graphics.RenderMeshPrimitives, which GsplatSorter issues once per frame from the
            /// player loop, and a second capture in the same frame has no reason to still be
            /// inside that window.
            /// </summary>
            string CaptureBandDepth(int band)
            {
                GameObject cameraObject = null;
                RenderTexture cubeTarget = null;
                RenderTexture accumulated = null;
                RenderTexture resolved = null;

                Progress($"Band {band + 1} of {m_bandCount}: capturing depth\u2026");

                m_shells.DepthCaptureRange(band, out float near, out float far);
                m_swap.SetDepthCapture(true);
                Shader.SetGlobalFloat(k_depthCaptureNear, near);
                Shader.SetGlobalFloat(k_depthCaptureFar, far);
                Shader.SetGlobalFloat(k_depthCaptureCutoff, m_shells.DepthCaptureCutoff);

                try
                {
                    cameraObject = CreateCaptureCamera(m_origin, m_shells.StartRadius);
                    var camera = cameraObject.GetComponent<Camera>();

                    // Square: the depth map is octahedral, which covers the sphere with one
                    // unit-square chart rather than a two-to-one lat-long rectangle.
                    int width = Mathf.Max(64, DepthPanoramaWidth(m_shells));
                    int height = width;

                    int faceSize = DepthCubeFaceSize(width);
                    // Linear, and this matters. The project renders in linear space, so an sRGB
                    // target encodes whatever is written to it, the PNG holds the encoded byte, and
                    // the shader -- importing the map as non-colour data -- reads that byte straight
                    // back as if it were the distance. Depth landed about a fifth of a band too far
                    // out that way. It is a measurement, so it is stored as one.
                    cubeTarget = new RenderTexture(faceSize, faceSize, 24, RenderTextureFormat.ARGB32,
                                                   RenderTextureReadWrite.Linear)
                    {
                        dimension = TextureDimension.Cube,
                        name = "StageSettARShellDepthCube"
                    };

                    m_renderer.Draw();
                    RenderCubeFaceByFace(camera, cubeTarget);

                    accumulated = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32,
                                                    RenderTextureReadWrite.Linear);
                    resolved = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32,
                                                 RenderTextureReadWrite.Linear);

                    Stitch(cubeTarget, accumulated, m_shells.SeamFeatherDegrees, octahedral: true);
                    BlitPass(accumulated, resolved, k_resolveDepthPass);

                    float smoothing = m_shells.DepthSmoothing;
                    if (smoothing > 0.01f)
                    {
                        float uStep = 1f / width;
                        float vStep = 1f / height;

                        // Separable, so the cost is two passes rather than the square of one, and
                        // the pair ping-pongs between the two targets already in hand.
                        BlitPass(resolved, accumulated, k_smoothDepthPass, material =>
                        {
                            material.SetVector("_BlurStep", new Vector4(uStep, 0f, 0f, 0f));
                            material.SetFloat("_BlurRadius", smoothing);
                        });
                        BlitPass(accumulated, resolved, k_smoothDepthPass, material =>
                        {
                            material.SetVector("_BlurStep", new Vector4(0f, vStep, 0f, 0f));
                            material.SetFloat("_BlurRadius", smoothing);
                        });
                    }

                    string texturePath = WriteDepthMap(resolved, m_folder,
                        m_renderer.name + (m_bandCount > 1 ? $"_Band{band}" : string.Empty));
                    m_shells.GeneratedAssetPaths.Add(texturePath);

                    // Read back only to check the capture is not empty. Cheap, editor-only, and it
                    // has caught a silently broken depth pass more than once.
                    ReportDepth(band, ReadDepth(resolved, near, far), near, far);
                    return texturePath;
                }
                finally
                {
                    // Cleared, not left set: these are globals and would otherwise leak into
                    // ordinary rendering after the bake.
                    Shader.SetGlobalFloat(k_depthCaptureCutoff, 0f);
                    m_swap.SetDepthCapture(false);
                    if (cameraObject)
                        Object.DestroyImmediate(cameraObject);
                    ReleaseTarget(cubeTarget);
                    ReleaseTarget(accumulated);
                    ReleaseTarget(resolved);
                }
            }

            PanoramaPaths CaptureBandTexture(int band)
            {
                GameObject cameraObject = null;
                RenderTexture cubeTarget = null;
                RenderTexture equirectTarget = null;

                try
                {
                    cameraObject = CreateCaptureCamera(m_origin, m_shells.StartRadius);
                    var camera = cameraObject.GetComponent<Camera>();

                    int faceSize = CubeFaceSize(m_shells.Resolution);
                    cubeTarget = new RenderTexture(faceSize, faceSize, 24, RenderTextureFormat.ARGB32)
                    {
                        dimension = TextureDimension.Cube,
                        name = "StageSettARShellCube"
                    };
                    RenderCubeFaceByFace(camera, cubeTarget);

                    int width = Mathf.Max(64, m_shells.Resolution);
                    equirectTarget = new RenderTexture(width, width / 2, 0, RenderTextureFormat.ARGB32)
                    {
                        name = "StageSettARShellEquirect"
                    };
                    string suffix = m_bandCount > 1 ? $"_Band{band}" : string.Empty;

                    StitchToEquirect(cubeTarget, equirectTarget, m_shells.SeamFeatherDegrees);
                    var written = WritePanorama(equirectTarget, m_folder, m_renderer.name + suffix,
                        m_shells.SplitColourAndAlpha, m_shells.JpegQuality);
                    m_shells.GeneratedAssetPaths.Add(written.ColourPath);
                    if (!string.IsNullOrEmpty(written.MaskPath))
                        m_shells.GeneratedAssetPaths.Add(written.MaskPath);
                    return written;
                }
                finally
                {
                    if (cameraObject)
                        Object.DestroyImmediate(cameraObject);
                    ReleaseTarget(cubeTarget);
                    ReleaseTarget(equirectTarget);
                }
            }

            void Build()
            {
                if (m_results.Count == 0)
                    throw new System.InvalidOperationException(
                        $"No splats survive beyond a radius of {m_shells.StartRadius}, so there is " +
                        "nothing to capture. Reduce Start Radius.");

                Progress("Writing assets…");

                // Put the scene back before building the result, so the created objects are not
                // caught by the hide list and the renderer keeps its authored settings.
                Cleanup();
                BuildShells(m_shells, m_renderer, m_origin, m_folder, m_results.ConvertAll(
                    r => (r.Index, r.Paths, r.DepthPath)));
                Finish();
                Debug.Log($"[SplatShells] Baked {m_results.Count} shell(s) into {m_folder}." +
                          (m_depthSupported ? "" : " Colour only: depth capture was unavailable."), m_shells);
            }

            void Progress(string message) =>
                EditorUtility.DisplayProgressBar("Bake Panoramic Shells", message,
                    Mathf.Clamp01((m_band + 0.5f) / m_bandCount));

            void ClearBandCutouts()
            {
                foreach (var cutout in m_bandCutouts)
                    if (cutout)
                        Object.DestroyImmediate(cutout.gameObject);
                m_bandCutouts.Clear();
            }

            void Cleanup()
            {
                ClearBandCutouts();
                m_restore.Undo();
                // After the restore, which refreshes the renderer: it should sort with the stock
                // shaders again.
                m_swap?.Dispose();
                m_swap = null;
            }

            void OnPlayModeChanged(PlayModeStateChange change)
            {
                if (change == PlayModeStateChange.ExitingEditMode)
                    Fail(new System.InvalidOperationException("Entering play mode cancelled the bake."));
            }

            void Fail(System.Exception exception)
            {
                Debug.LogException(exception, m_shells);
                EditorUtility.DisplayDialog("Bake Panoramic Shells", $"Bake failed:\n\n{exception.Message}", "OK");
                Finish();
            }

            void Finish()
            {
                if (m_finished)
                    return;
                m_finished = true;

                EditorApplication.update -= Tick;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                Cleanup();
                if (m_locked)
                    EditorApplication.UnlockReloadAssemblies();
                m_locked = false;
                EditorUtility.ClearProgressBar();
                if (m_shells)
                    EditorSceneManager.MarkSceneDirty(m_shells.gameObject.scene);
            }
        }

        static void ReleaseTarget(RenderTexture target)
        {
            if (!target)
                return;
            // Graphics.Blit leaves its destination bound, so a target released straight after one
            // is still RenderTexture.active and Unity warns about releasing it.
            if (RenderTexture.active == target)
                RenderTexture.active = null;
            target.Release();
            Object.DestroyImmediate(target);
        }

        /// <summary>
        /// Cube face size needed to feed an equirect of <paramref name="equirectWidth"/> without
        /// softening it.
        ///
        /// A 90-degree perspective face samples angle non-uniformly: x = f*tan(theta) with
        /// f = N/2, so pixels per radian is f*sec^2(theta) -- lowest dead centre and twice that by
        /// the 45-degree corner. The equirect it is resampled into is uniform at W/360 per degree,
        /// so the binding constraint is the face centre, where N*(pi/180)/2 per degree must reach
        /// W/360. That gives N = W/pi.
        ///
        /// Undersampling here does not look like uniform blur. It leaves six soft patches with
        /// sharp joins, which reads as a cube stretched onto the sky. It also interacts with the
        /// splat shader's two-pixel cull, which is measured in pixels, so fewer pixels per degree
        /// at face centres drops more small splats there -- the same cube footprint again, in
        /// density rather than sharpness. Rounding up to a power of two leaves headroom for both.
        /// </summary>
        static int CubeFaceSize(int equirectWidth) =>
            Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(equirectWidth / Mathf.PI)), 256, 4096);

        /// <summary>
        /// World-space rotations matching Unity's cubemap face order: +X, -X, +Y, -Y, +Z, -Z.
        /// </summary>
        static readonly Quaternion[] k_faceRotations =
        {
            Quaternion.Euler(0f, 90f, 0f),
            Quaternion.Euler(0f, -90f, 0f),
            Quaternion.Euler(-90f, 0f, 0f),
            Quaternion.Euler(90f, 0f, 0f),
            Quaternion.identity,
            Quaternion.Euler(0f, 180f, 0f)
        };

        /// <summary>
        /// Renders the six faces one at a time, turning the camera to face each one first.
        ///
        /// RenderToCubemap always renders axis-aligned faces and ignores the camera's rotation, so
        /// rotating it changes nothing about the image. It does change what GsplatSorter reads:
        /// DispatchSort orders splats by camera.worldToCameraMatrix, so rendering all six faces in
        /// one call sorts them once, for whichever way the camera happened to be pointing. Five of
        /// the six then blend back-to-front along the wrong axis, and because the blend is
        /// order-dependent they come out a different colour -- which shows up as mismatched lines
        /// meeting three at a time along the cube's edges and corners.
        ///
        /// ComputeSortRequired is set once per player-loop frame and nothing clears it, so each
        /// face's render performs its own sort.
        /// </summary>
        static void RenderCubeFaceByFace(Camera camera, RenderTexture target)
        {
            for (int face = 0; face < k_faceRotations.Length; face++)
            {
                camera.transform.rotation = k_faceRotations[face];
                if (!camera.RenderToCubemap(target, 1 << face))
                    throw new System.InvalidOperationException(
                        "Camera.RenderToCubemap failed; this platform or pipeline does not support it.");
            }
        }

        static GameObject CreateCaptureCamera(Vector3 origin, float cutRadius)
        {
            var cameraObject = new GameObject("__StageSettARShellCamera") { hideFlags = HideFlags.HideAndDontSave };
            cameraObject.transform.SetPositionAndRotation(origin, Quaternion.identity);

            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            // Transparent black. The splat shader writes premultiplied colour, so the capture keeps
            // its holes and the shells can let the real background through where the far field
            // had nothing. Clearing to anything opaque here would paint the sky into every gap.
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.nearClipPlane = Mathf.Max(0.01f, cutRadius * 0.01f);
            camera.farClipPlane = Mathf.Max(1000f, cutRadius * 100f);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.cullingMask = ~0;

            // Post-processing would tone-map and grade the capture, so the panorama would be
            // graded twice once it is displayed. Opaque and depth copies are dead weight here.
            var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.renderShadows = false;
            cameraData.requiresColorOption = CameraOverrideOption.Off;
            cameraData.requiresDepthOption = CameraOverrideOption.Off;

            return cameraObject;
        }

        static GsplatCutout CreateCutout(GsplatRenderer renderer, Vector3 origin, float radius, bool invert,
            string name)
        {
            var cutoutObject = new GameObject(name);
            // A child of the splat, alongside the shells, so the whole bake moves and is deleted as
            // one thing. It also keeps the cutout inside what HideEverythingExcept treats as part
            // of the splat -- a sibling is neither an ancestor nor a descendant, so it would be
            // switched off during a bake.
            cutoutObject.transform.SetParent(renderer.transform, worldPositionStays: false);
            cutoutObject.transform.SetPositionAndRotation(origin, Quaternion.identity);
            SetWorldScale(cutoutObject.transform, radius);

            var cutout = cutoutObject.AddComponent<GsplatCutout>();
            cutout.m_Type = GsplatCutout.Type.Ellipsoid;
            cutout.m_Invert = invert;
            // Specific rather than Parent even though it is now a child: Parent resolves through
            // the hierarchy at gather time, so it would silently stop applying if the shells were
            // ever regrouped under an intermediate transform.
            cutout.m_Target = GsplatCutout.Target.Specific;
            cutout.m_SpecifcRenderer = renderer;
            return cutout;
        }

        static void SetWorldScale(Transform transform, float worldRadius)
        {
            Vector3 parentScale = transform.parent ? transform.parent.lossyScale : Vector3.one;
            transform.localScale = new Vector3(
                worldRadius / Mathf.Max(Mathf.Abs(parentScale.x), 1e-6f),
                worldRadius / Mathf.Max(Mathf.Abs(parentScale.y), 1e-6f),
                worldRadius / Mathf.Max(Mathf.Abs(parentScale.z), 1e-6f));
        }

        /// <summary>
        /// Flattens the captured cube into the equirect, cross-fading the face joins.
        ///
        /// Replaces RenderTexture.ConvertToEquirect, which gives no access to the stitch and whose
        /// mapping is undocumented -- the shader's direction is instead the exact inverse of what
        /// SplatShells.shader samples with, so the two cannot drift apart.
        /// </summary>
        static void StitchToEquirect(RenderTexture cubeTarget, RenderTexture equirectTarget,
            float featherDegrees) => Stitch(cubeTarget, equirectTarget, featherDegrees, false);

        static void Stitch(RenderTexture cubeTarget, RenderTexture flatTarget,
            float featherDegrees, bool octahedral)
        {
            var shader = Shader.Find(k_stitchShaderName);
            if (!shader)
                throw new System.InvalidOperationException($"Shader '{k_stitchShaderName}' not found.");

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                material.SetTexture("_CubeFaces", cubeTarget);
                // Joins sit at 45 degrees, and the shader measures in tan units, so the feather
                // half-width has to be converted rather than passed straight through.
                float feather = featherDegrees > 0f
                    ? Mathf.Tan((45f + featherDegrees) * Mathf.Deg2Rad) - 1f
                    : 0f;
                material.SetFloat("_FeatherTan", feather);
                material.SetFloat("_Octahedral", octahedral ? 1f : 0f);
                Graphics.Blit(null, flatTarget, material, k_stitchPass);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// Range distance is normalized against for a band. Per band rather than global, because a
        /// tight range is what makes an eight-bit target sufficient: 15 to 24 metres over 255 steps
        /// is twelve centimetres, where one scale covering everything to the horizon would be
        /// nearly two metres and would terrace a wall visibly. A quarter of headroom past the outer
        /// edge, since expected depth is an average and a splat straddling the boundary pulls it out.
        /// </summary>
        static int DepthPanoramaWidth(SplatShells shells) => shells switch
        {
            // Reprojection samples it per pixel and uses it to steer the colour, so a depth texel
            // far coarser than a colour texel drags a whole block of colour around with it.
            SplatShellReprojection reprojected => Mathf.Max(256, reprojected.DepthResolution),
            _ => 512
        };

        /// <summary>
        /// Cube face size for a depth capture. The octahedral chart's densest direction is its
        /// diagonal, so the face has to keep up with that rather than with the edge.
        /// </summary>
        static int DepthCubeFaceSize(int width) => Mathf.Max(64, Mathf.NextPowerOfTwo(width * 3 / 4));

        static void BlitPass(RenderTexture source, RenderTexture destination, int pass) =>
            BlitPass(source, destination, pass, null);

        static void BlitPass(RenderTexture source, RenderTexture destination, int pass,
                             System.Action<Material> configure)
        {
            var shader = Shader.Find(k_stitchShaderName);
            if (!shader)
                throw new System.InvalidOperationException($"Shader '{k_stitchShaderName}' not found.");

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                material.SetTexture("_Source", source);
                configure?.Invoke(material);
                Graphics.Blit(null, destination, material, pass);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// Saves the resolved depth panorama as an asset for the shader to sample: red carries the
        /// normalized distance and alpha marks whether anything was recorded that way.
        ///
        /// Uncompressed and non-sRGB, deliberately. This is data, not colour -- a gamma curve would
        /// bend the distances, and block compression across a depth ramp produces exactly the
        /// stepped artefacts reprojection then amplifies.
        /// </summary>
        static string WriteDepthMap(RenderTexture source, string folder, string name)
        {
            var readback = ReadBack(source, TextureFormat.RGBA32);
            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{folder}/{SanitizeFileName(name)}_ShellDepth.png");
            int width = readback.width;

            File.WriteAllBytes(path, readback.EncodeToPNG());
            Object.DestroyImmediate(readback);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                // No mips: minification would average across silhouettes and invent distances
                // halfway between a roofline and the sky behind it.
                importer.mipmapEnabled = false;
                // Clamped on both axes. Unlike a lat-long panorama this chart does not tile in
                // either direction: its border folds back onto itself, so repeating would wrap a
                // direction round to an unrelated one.
                importer.wrapModeU = TextureWrapMode.Clamp;
                importer.wrapModeV = TextureWrapMode.Clamp;
                importer.maxTextureSize = Mathf.Max(512, Mathf.NextPowerOfTwo(width));
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            return path;
        }

        /// <summary>Distance per texel in metres, or 0 where nothing was hit.</summary>
        static float[] ReadDepth(RenderTexture source, float near, float far)
        {
            var previous = RenderTexture.active;
            var readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readback.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            var pixels = readback.GetPixels32();
            var depth = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                depth[i] = pixels[i].a > 128 ? Mathf.Lerp(near, far, pixels[i].r / 255f) : 0f;

            Object.DestroyImmediate(readback);
            return depth;
        }

        /// <summary>
        /// Says what the depth capture produced. A capture that silently comes back empty and one
        /// that works look identical from the outside once the bake finishes, so this is the only
        /// thing that tells them apart.
        /// </summary>
        static void ReportDepth(int band, float[] depth, float near, float far)
        {
            int valid = 0;
            float min = float.MaxValue, max = 0f;
            foreach (float value in depth)
            {
                if (value <= 0f)
                    continue;
                valid++;
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }

            if (valid == 0)
            {
                Debug.LogWarning($"[SplatShells] Band {band}: depth capture is empty.");
                return;
            }

            Debug.Log($"[SplatShells] Band {band}: depth on {100f * valid / depth.Length:F1}% of " +
                      $"texels, {min:F1} to {max:F1} m (band {near:F1}-{far:F1}).");
        }

        public struct PanoramaPaths
        {
            public string ColourPath;
            public string MaskPath;
        }

        /// <summary>
        /// Writes the stitched panorama out, either as one RGBA PNG or as a JPEG plus a lossless
        /// coverage mask.
        ///
        /// Splitting is worth roughly an order of magnitude on disk and nothing at all at runtime,
        /// since Unity transcodes to a GPU format on import and ships that regardless. It is also
        /// the only way JPEG is usable here: the panorama is premultiplied, and lossy ringing
        /// around an alpha edge would push colour above its own alpha and glow. The colour is
        /// un-premultiplied and dilated into its transparent regions first, so the encoder has
        /// something continuous to work on instead of a cliff into black.
        /// </summary>
        static PanoramaPaths WritePanorama(RenderTexture source, string folder, string rendererName,
            bool split, int jpegQuality)
        {
            if (!split)
                return new PanoramaPaths
                {
                    ColourPath = WriteTexture(ReadBack(source, TextureFormat.RGBA32),
                        $"{folder}/{SanitizeFileName(rendererName)}_Shell.png",
                        jpegQuality: 0, sRGB: true, alphaIsCoverage: true)
                };

            RenderTexture colourTarget = null;
            try
            {
                colourTarget = PrepareColourForLossyEncode(source);
                var paths = new PanoramaPaths
                {
                    ColourPath = WriteTexture(ReadBack(colourTarget, TextureFormat.RGB24),
                        $"{folder}/{SanitizeFileName(rendererName)}_Shell.jpg",
                        jpegQuality, sRGB: true, alphaIsCoverage: false),
                    MaskPath = WriteTexture(ExtractCoverage(ReadBack(source, TextureFormat.RGBA32)),
                        $"{folder}/{SanitizeFileName(rendererName)}_ShellMask.png",
                        jpegQuality: 0, sRGB: false, alphaIsCoverage: false)
                };
                return paths;
            }
            finally
            {
                ReleaseTarget(colourTarget);
            }
        }

        /// <summary>Un-premultiplies, then floods colour outward so the encoder never sees a cliff.</summary>
        static RenderTexture PrepareColourForLossyEncode(RenderTexture source)
        {
            var shader = Shader.Find(k_stitchShaderName);
            if (!shader)
                throw new System.InvalidOperationException($"Shader '{k_stitchShaderName}' not found.");

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var a = new RenderTexture(source.descriptor) { name = "StageSettARShellColourA" };
            var b = new RenderTexture(source.descriptor) { name = "StageSettARShellColourB" };
            try
            {
                material.SetTexture("_Source", source);
                Graphics.Blit(null, a, material, k_unpremultiplyPass);

                // Enough to cover the fringe an 8x8 JPEG block can reach across, with headroom.
                for (int i = 0; i < 12; i++)
                {
                    material.SetTexture("_Source", a);
                    Graphics.Blit(null, b, material, k_dilatePass);
                    (a, b) = (b, a);
                }

                ReleaseTarget(b);
                return a;
            }
            catch
            {
                ReleaseTarget(a);
                ReleaseTarget(b);
                throw;
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        static Texture2D ReadBack(RenderTexture source, TextureFormat format)
        {
            var previous = RenderTexture.active;
            var readback = new Texture2D(source.width, source.height, format, false);
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readback.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            return readback;
        }

        /// <summary>Alpha, replicated to RGB. PNG collapses identical channels, so the cost is small.</summary>
        static Texture2D ExtractCoverage(Texture2D source)
        {
            var pixels = source.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                byte coverage = pixels[i].a;
                pixels[i] = new Color32(coverage, coverage, coverage, 255);
            }

            var mask = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            mask.SetPixels32(pixels);
            mask.Apply(false, false);
            Object.DestroyImmediate(source);
            return mask;
        }

        static string WriteTexture(Texture2D texture, string path, int jpegQuality, bool sRGB,
            bool alphaIsCoverage)
        {
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            int width = texture.width;
            byte[] encoded = jpegQuality > 0
                ? texture.EncodeToJPG(jpegQuality)
                : texture.EncodeToPNG();
            File.WriteAllBytes(path, encoded);
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                // The mask is data, not colour: sampling it through an sRGB curve would bend
                // coverage and change how the far field fades.
                importer.sRGBTexture = sRGB;
                importer.alphaSource = alphaIsCoverage
                    ? TextureImporterAlphaSource.FromInput
                    : TextureImporterAlphaSource.None;
                // The capture is premultiplied. Alpha-is-transparency dilates colour into
                // transparent texels to fix straight-alpha fringing, which would only smear light
                // into the gaps here.
                importer.alphaIsTransparency = false;
                importer.mipmapEnabled = true;
                importer.wrapModeU = TextureWrapMode.Repeat;
                importer.wrapModeV = TextureWrapMode.Clamp;
                importer.maxTextureSize = Mathf.Max(2048, Mathf.NextPowerOfTwo(width));
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
            }

            return path;
        }

        static void BuildShells(SplatShells shells, GsplatRenderer renderer, Vector3 origin,
            string folder,
            List<(int Band, PanoramaPaths Paths, string DepthPath)> captures)
        {
            var shader = Shader.Find(k_shaderName);
            if (!shader)
                throw new System.InvalidOperationException($"Shader '{k_shaderName}' not found.");


            foreach ((int band, PanoramaPaths paths, string depthPath) in captures)
            {
                var mesh = SplatShells.CreateShellMesh();
                string meshPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{folder}/{SanitizeFileName(renderer.name)}_{mesh.name}.asset");
                AssetDatabase.CreateAsset(mesh, meshPath);
                shells.GeneratedAssetPaths.Add(meshPath);

                var panorama = AssetDatabase.LoadAssetAtPath<Texture2D>(paths.ColourPath);
                var mask = string.IsNullOrEmpty(paths.MaskPath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<Texture2D>(paths.MaskPath);
                string suffix = shells.BandCount > 1 ? $"_Band{band}" : string.Empty;

                var material = new Material(shader) { name = $"{renderer.name}_Shell{suffix}" };
                material.SetTexture("_MainTex", panorama);
                if (mask)
                {
                    material.SetTexture("_AlphaTex", mask);
                    material.SetFloat("_SplitAlpha", 1f);
                    material.EnableKeyword("GSPLAT_SPLIT_ALPHA");
                }

                Texture2D depthMap = null;
                if (!string.IsNullOrEmpty(depthPath))
                {
                    depthMap = AssetDatabase.LoadAssetAtPath<Texture2D>(depthPath);
                    shells.DepthCaptureRange(band, out float depthNear, out float depthFar);
                    material.SetTexture("_DepthTex", depthMap);
                    material.SetFloat("_DepthNear", depthNear);
                    material.SetFloat("_DepthFar", depthFar);
                    material.SetFloat("_Reproject", 1f);
                    if (shells is SplatShellReprojection reprojected)
                    {
                        material.SetFloat("_MarchSteps", reprojected.MarchSteps);
                        material.SetFloat("_RefineSteps", reprojected.RefineSteps);
                    }
                    material.EnableKeyword("GSPLAT_BACKDROP_REPROJECT");
                }
                // Shells are transparent and overlap, so they must composite back to front. The
                // outermost draws first and each band inside it one step later; the live splats,
                // always nearer than every shell, land last at the Transparent queue's 3000.
                material.renderQueue = shells.BandRenderQueue(band);
                string materialPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{folder}/{SanitizeFileName(renderer.name)}_Shell{suffix}.mat");
                AssetDatabase.CreateAsset(material, materialPath);
                shells.GeneratedAssetPaths.Add(materialPath);

                float shellRadius = shells.BandShellRadius(band);
                var instance = new GameObject($"{renderer.name} Shell{suffix}");
                Undo.RegisterCreatedObjectUndo(instance, "Bake Panoramic Shells");
                instance.transform.SetParent(renderer.transform, worldPositionStays: false);
                // Identity world rotation: the panorama was captured along world axes and the
                // shader samples object space, so the two only line up if the shell starts
                // unrotated. Later rotations of the splat then carry the shells with it.
                instance.transform.SetPositionAndRotation(origin, Quaternion.identity);
                SetWorldScale(instance.transform, shellRadius);

                instance.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = instance.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = material;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
                meshRenderer.lightProbeUsage = LightProbeUsage.Off;
                meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                shells.Shells.Add(new SplatShells.Shell
                {
                    InnerRadius = shells.BandInnerRadius(band),
                    SphereRadius = shellRadius,
                    // Baked in rather than derived at runtime, so changing the shell count after a
                    // bake cannot silently re-interpret shells that are already on disk.
                    ParallaxFactor = shells.BandParallaxFactor(band),
                    Panorama = panorama,
                    CoverageMask = mask,
                    DepthMap = depthMap,
                    Material = material,
                    Mesh = mesh,
                    Instance = instance
                });
            }

            // Kept in renderer-local space so the capture origin survives the splat being moved.
            shells.BakedOriginLocal = renderer.transform.InverseTransformPoint(origin);

            // One cutout covers every band: all of them were baked, so everything past the start
            // radius is now on a shell and none of it needs to stay live.
            var cutout = CreateCutout(renderer, origin, shells.StartRadius, invert: false,
                "Shells Cutout");
            Undo.RegisterCreatedObjectUndo(cutout.gameObject, "Bake Panoramic Shells");
            if (shells.Shells.Count > 0)
                shells.Shells[0].Cutout = cutout;

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(shells);
            renderer.ForceRefresh();
        }

        static string ResolveOutputFolder(SplatShells shells, GsplatRenderer renderer)
        {
            // One folder per splat, so a project with several baked splats keeps them apart.
            string folder = !string.IsNullOrWhiteSpace(shells.OutputFolder)
                ? shells.OutputFolder.Replace('\\', '/').TrimEnd('/')
                : $"{k_defaultRoot}/{SanitizeFileName(renderer.name)}";
            EnsureFolder(folder);
            return folder;
        }

        static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
                return;

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static string SanitizeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return name.Replace(' ', '_');
        }

        // ---------------------------------------------------------------- revert

        enum AssetChoice { Delete, Keep, Cancel }

        /// <summary>
        /// Three-way rather than a yes/no: deleting is irreversible and another scene may
        /// reference the files, so backing out has to remain available after seeing the count.
        /// </summary>
        static AssetChoice PromptForAssets(SplatShells shells, string title, string lead)
        {
            if (shells.GeneratedAssetPaths.Count == 0)
                return AssetChoice.Keep;

            int choice = EditorUtility.DisplayDialogComplex(
                title,
                $"{lead}\n\n{shells.GeneratedAssetPaths.Count} files were generated by the " +
                "previous bake (panoramas, materials, meshes). Delete them too?\n\nFiles from " +
                "earlier bakes are left alone. This cannot be undone, and anything else " +
                "referencing them will lose it.",
                "Delete Files", "Cancel", "Keep Files");

            return choice switch
            {
                0 => AssetChoice.Delete,
                1 => AssetChoice.Cancel,
                _ => AssetChoice.Keep
            };
        }

        static void Revert(SplatShells shells, bool deleteAssets)
        {
            foreach (var shell in shells.Shells)
            {
                if (shell == null)
                    continue;
                if (shell.Instance)
                    Undo.DestroyObjectImmediate(shell.Instance);
                if (shell.Cutout)
                    Undo.DestroyObjectImmediate(shell.Cutout.gameObject);
            }

            shells.Shells.Clear();

            if (deleteAssets)
            {
                // Scene objects first, above: deleting a material still assigned to a live renderer
                // would leave it pink until the next revert.
                var failed = new List<string>();
                AssetDatabase.DeleteAssets(shells.GeneratedAssetPaths.ToArray(), failed);
                if (failed.Count > 0)
                    Debug.LogWarning(
                        $"[SplatShells] Could not delete {failed.Count} generated file(s): " +
                        string.Join(", ", failed), shells);
                shells.GeneratedAssetPaths.Clear();
                AssetDatabase.Refresh();
            }

            EditorUtility.SetDirty(shells);

            var renderer = shells.ResolveRenderer();
            if (renderer)
                renderer.ForceRefresh();
        }

        // ------------------------------------------------------- scene state guard

        /// <summary>
        /// Everything the bake disturbs, and how to put it back. Restoring runs from a finally
        /// block, so a failed capture cannot strand the scene with half the objects hidden.
        /// </summary>
        class RestoreState
        {
            readonly GsplatRenderer m_renderer;
            readonly List<GameObject> m_hidden = new();

            readonly float m_downscale;
            readonly bool m_asyncUpload;
            readonly bool m_updateBounds;
            readonly GsplatRenderer.GsplatSortMode m_sortMode;
            readonly uint m_sortRefresh;
            readonly uint m_cutoutRefresh;

            bool m_undone;
            SplatLod m_lod;

            public RestoreState(GsplatRenderer renderer)
            {
                m_renderer = renderer;

                m_downscale = renderer.SplatDownscaleFactor;
                m_asyncUpload = renderer.AsyncUpload;
                m_updateBounds = renderer.CutoutsUpdateBounds;
                m_sortMode = renderer.SortMode;
                m_sortRefresh = renderer.SortRefreshRate;
                m_cutoutRefresh = renderer.CutoutsRefreshRate;
            }

            public void HideEverythingExcept(Transform keep, Transform alsoKeep)
            {
                foreach (var root in keep.gameObject.scene.GetRootGameObjects())
                {
                    foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (!candidate.gameObject.activeSelf)
                            continue;
                        if (IsRelated(candidate, keep) || IsRelated(candidate, alsoKeep))
                            continue;
                        if (candidate.parent != null && !candidate.parent.gameObject.activeInHierarchy)
                            continue;

                        candidate.gameObject.SetActive(false);
                        m_hidden.Add(candidate.gameObject);
                    }
                }
            }

            static bool IsRelated(Transform candidate, Transform anchor)
            {
                if (anchor == null)
                    return false;
                // Ancestors must stay active or the splat goes with them; descendants of the splat
                // are part of it.
                return candidate.IsChildOf(anchor) || anchor.IsChildOf(candidate);
            }

            public void ApplyCaptureQuality(bool fullQuality)
            {
                // Splat LOD selects splats through active ranges, which would override the band
                // cutouts the bake relies on. Off for the length of the bake.
                m_lod = m_renderer.GetComponentInChildren<SplatLod>();
                if (m_lod && m_lod.enabled)
                    m_lod.enabled = false;
                else
                    m_lod = null;

                if (fullQuality)
                    m_renderer.SplatDownscaleFactor = 0f;

                // Synchronous upload and per-frame refresh let the bake pump Update() to
                // completion instead of waiting on editor frames.
                m_renderer.AsyncUpload = false;
                m_renderer.CutoutsUpdateBounds = false;
                m_renderer.SortMode = GsplatRenderer.GsplatSortMode.Always;
                m_renderer.SortRefreshRate = 1;
                m_renderer.CutoutsRefreshRate = 1;
            }

            public void Undo()
            {
                if (m_undone)
                    return;
                m_undone = true;

                foreach (var hidden in m_hidden)
                    if (hidden)
                        hidden.SetActive(true);
                m_hidden.Clear();

                if (!m_renderer)
                    return;

                if (m_lod)
                    m_lod.enabled = true;
                m_renderer.SplatDownscaleFactor = m_downscale;
                m_renderer.AsyncUpload = m_asyncUpload;
                m_renderer.CutoutsUpdateBounds = m_updateBounds;
                m_renderer.SortMode = m_sortMode;
                m_renderer.SortRefreshRate = m_sortRefresh;
                m_renderer.CutoutsRefreshRate = m_cutoutRefresh;
                m_renderer.ForceRefresh();
            }
        }
    }
}
