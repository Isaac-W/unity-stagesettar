using System;
using System.Collections.Generic;
using Gsplat;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Replaces a splat's far field with a baked panorama on an inside-out sphere.
    ///
    /// Beyond some distance a Gaussian cloud stops earning its cost: it is the least detailed part
    /// of the scene and, because the far splats are large, the most expensive to blend. Capturing
    /// it once from the viewer's position and hanging it on a shell trades that entire population
    /// for one textured sphere.
    ///
    /// The cost is parallax. A panorama is only correct from the point it was captured, so the cut
    /// radius has to be far enough out that head movement across the play area does not shear it.
    /// Roughly: error is atan(playRadius / cutRadius), imperceptible under about 2 degrees and
    /// obviously a painted backdrop past about 5. Stereo can be ignored -- a 6.5cm IPD at 15m is
    /// a quarter of a degree -- which is what makes a single mono capture viable.
    ///
    /// Baking is editor-only (select the splat and use Tools > StageSettAR > Bake Panoramic Shells,
    /// or the Bake button on this component); this component just carries the settings and enough
    /// bookkeeping to undo the result. Ported from the UnitySplats fork's GsplatBackdrop.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("StageSettAR/Splats/Splat Shells")]
    public class SplatShells : MonoBehaviour
    {
        /// <summary>
        /// One baked shell. Today a bake produces exactly one, but the far field could instead be
        /// split into concentric bands, each captured separately and hung at its own distance, to
        /// win back some depth. The shader is already indifferent to how many of these exist, so
        /// that stays a tooling change rather than a rewrite.
        /// </summary>
        [Serializable]
        public class Shell
        {
            [Tooltip("Splats nearer than this stay live; everything beyond it lives in this shell's panorama.")]
            public float InnerRadius;

            [Tooltip("World radius the shell mesh sits at. Further out means less parallax against distant content.")]
            public float SphereRadius;

            [Tooltip("Fraction of viewer movement this shell follows. See SplatShells.BandParallaxFactor.")]
            public float ParallaxFactor;

            public Texture2D Panorama;

            [Tooltip("Coverage mask, when colour and alpha are stored separately. Null otherwise.")]
            public Texture2D CoverageMask;

            [Tooltip("Depth panorama, when the shell reprojects per pixel. Null otherwise.")]
            public Texture2D DepthMap;
            public Material Material;
            public Mesh Mesh;
            public GameObject Instance;
            public GsplatCutout Cutout;
        }

        [Header("Source")]
        [Tooltip("Splat renderer whose far field is replaced. Found automatically on this object, its parents or its children when left empty.")]
        public GsplatRenderer Renderer;

        [Tooltip(
            "Where the panorama is captured from. This must be where the viewer's head will be, " +
            "not the splat's pivot, because the capture is only correct from this one point.")]
        public Transform CaptureOrigin;

        [Header("Shells")]
        [Tooltip(
            "Splats beyond this world radius from the capture origin are cut and replaced by the " +
            "shells. This is also where the innermost band begins.")]
        [Min(0.1f)] public float StartRadius = 15f;

        [Tooltip(
            "Where the outermost band begins. Bands tile from the start radius to here, and the " +
            "outermost one runs from here to infinity so nothing falls off the far edge. Ignored " +
            "with a single shell.")]
        [Min(0.2f)] public float EndRadius = 60f;

        [Tooltip(
            "How hard band boundaries are pulled toward the viewer.\n\n" +
            "0 spaces them evenly in ratio, which equalises relative error. 1 spaces them evenly " +
            "in 1/r, which equalises the angular error each band carries and is the even-handed " +
            "choice. Above 1 deliberately over-weights the near field: those bands get better than " +
            "their share and the far ones worse, which is usually the trade you want, since near " +
            "content fills more of the view, slides faster when you move, and is where a surface " +
            "crossing a boundary is most obviously torn.")]
        [Range(0f, 5f)] public float SpacingBias = 1f;

        [Tooltip(
            "How far each shell's capture reaches back inside the shell in front of it, as a " +
            "fraction of its own inner radius.\n\n" +
            "Without it, a surface crossing a boundary is handed to one shell on one side and the " +
            "next on the other, and the two parallax differently, so it tears along the join. " +
            "Overlapping puts the content on both, which trades that tear for a doubled image in " +
            "the overlap -- soft rather than broken.\n\n" +
            "The cost is that splats in the overlap are drawn twice, and premultiplied blending is " +
            "not idempotent, so the band reads slightly denser. Keep it small.")]
        [Range(0f, 0.25f)] public float BandOverlap = 0.01f;

        [Tooltip(
            "Let the innermost shell overlap too, reaching back inside the start radius.\n\n" +
            "Off by default, because what lies inside that radius is not another shell but live " +
            "splats: capturing them puts them on the shell as well, and they then draw twice, once " +
            "flat at the shell's distance and once properly. Whether that reads as a doubled image " +
            "depends on how far the viewer moves -- the shell's copy only separates from the real " +
            "one with parallax, so at the capture point they sit on top of each other.\n\n" +
            "Worth turning on when the join at the start radius tears, which is the same fault the " +
            "other boundaries have and the same trade: a soft double instead of a hard break. The " +
            "reprojected backdrop makes it a better trade, since the shell copy then moves roughly " +
            "with the real splats rather than staying flat.")]
        public bool OverlapInnermostBand;

        [Tooltip(
            "How many concentric shells the far field is split into. One is a single panorama. " +
            "More shells recover depth: each is captured separately and hung at its own distance, " +
            "so content parallaxes roughly correctly instead of being flattened onto one surface.")]
        [Range(1, 20)] public int ShellCount = 6;


        [Tooltip(
            "Multiplier for the outermost shell only. Every inner shell sits exactly at its band's " +
            "inner radius, which makes each seam parallax-free. The outermost band has nothing " +
            "beyond it to seam against, so pushing it out trades error at its inner edge for less " +
            "error on genuinely distant content.")]
        [Range(1f, 8f)] public float SphereRadiusMultiplier = 1f;

        [Header("Capture")]
        [Tooltip(
            "Width of the equirectangular capture; height is half this. Matching a Quest display " +
            "would need about 8192, but the far field is blurry background, so 4096 is usually " +
            "indistinguishable and a quarter of the memory.")]
        public int Resolution = 4096;

        [Tooltip(
            "Temporarily clear Splat Downscale Factor for the capture. That setting exists to buy " +
            "back fill rate, which a panorama does not need, so baking without it gives a denser " +
            "sky. Turn it off if the seam at the cut radius shows a density step.")]
        public bool BakeAtFullQuality = true;


        [Tooltip(
            "Half-width, in degrees, of the cross-fade across each cube face join. 0 is a hard " +
            "cube. Splats disagree at face boundaries because their shape is projected through an " +
            "approximation that is worst 45 degrees off-axis, which is exactly where the joins " +
            "are. This does not fix that -- only narrower captures would -- it removes the hard " +
            "edge the error draws itself along, spreading it over a band instead.")]
        [Range(0f, 15f)] public float SeamFeatherDegrees = 2f;

        [Tooltip(
            "Radius, in depth-map texels, of the blur applied to the captured depth before it is " +
            "saved. 0 leaves it raw.\n\n" +
            "Splats are ellipsoids, so the nearest-surface depth across a cluster of them is a " +
            "field of overlapping bumps rather than the smooth surface the object really has. " +
            "Smoothing across a few texels turns the bumps back into the surface, and does more " +
            "for the look of a reprojected shell than extra bits of depth precision do.\n\n" +
            "It also grows coverage outward slightly, because validity is blurred alongside the " +
            "distance. That matters: the soft tail of a splat is painted by the colour mask but " +
            "was rejected by the depth capture cutoff, so without a distance to march to it gets " +
            "clipped -- which is why reprojected splats read as flat discs with hard edges instead " +
            "of gaussians.")]
        [Range(0f, 4f)] public float DepthSmoothing = 1.5f;

        [Header("Output")]
        [Tooltip(
            "Store colour as a JPEG and coverage as a separate lossless mask, instead of one RGBA " +
            "PNG. Shrinks the files on disk by roughly an order of magnitude.\n\n" +
            "Build size and VRAM do not change -- Unity transcodes to a GPU format on import and " +
            "ships that either way -- so this buys repo size, not runtime.")]
        public bool SplitColourAndAlpha = true;

        [Tooltip(
            "JPEG quality for the colour half. The colour is un-premultiplied before encoding, so " +
            "ringing cannot push it above its own alpha, but low settings still soften the far " +
            "field. Below about 80 is a false economy: the mask is lossless and dominates the " +
            "total anyway.")]
        [Range(1, 100)] public int JpegQuality = 90;



        [Tooltip(
            "Folder for the generated panoramas, materials and meshes. Left empty, they go to " +
            "Assets/StageSettAR/Shells/<splat name>.")]
        public string OutputFolder = "";

        [Header("Runtime")]
        [Tooltip(
            "How much of the analytic parallax correction to apply. Each shell follows a fraction " +
            "of the viewer's movement, which makes content inside a band shift and loom at close " +
            "to the right rate instead of at its shell's rate. 0 is off.\n\n" +
            "The trade is that content sitting exactly on a seam wants no correction at all, so " +
            "raising this improves the middle of every band and degrades its edges. Find where it " +
            "stops helping; halfway is a reasonable guess.")]
        [Range(0f, 1f)] public float ParallaxCompensation = 1f;

        [Tooltip("Viewer to track. Defaults to the main camera.")]
        public Transform Viewer;

        [Tooltip("Capture origin at bake time, in renderer-local space, so it survives the splat moving.")]
        [HideInInspector] public Vector3 BakedOriginLocal;

        [Tooltip("Shells produced by the last bake.")]
        public List<Shell> Shells = new();

        /// <summary>
        /// Assets written by the current bake, cleared at the start of each one. Re-baking writes
        /// new files beside the old rather than overwriting them, and the older files are then
        /// deliberately forgotten: reverting should undo the bake that produced what is in the
        /// scene now, not sweep up every run that came before it.
        /// </summary>
        [HideInInspector] public List<string> GeneratedAssetPaths = new();

        public bool HasBake => Shells.Count > 0;

        MaterialPropertyBlock m_shellBlock;

        /// <summary>
        /// Pushes live settings onto the baked shells.
        ///
        /// Anything written only while baking cannot be tuned without baking again, which for a
        /// slider is no use at all. These go through a MaterialPropertyBlock so a shared material
        /// asset is never touched and the values do not persist into the project.
        /// </summary>
        public void ApplyShellSettings()
        {
            if (Shells.Count == 0)
                return;

            m_shellBlock ??= new MaterialPropertyBlock();
            for (int band = 0; band < Shells.Count; band++)
            {
                var shell = Shells[band];
                if (shell?.Instance == null || !shell.Instance.TryGetComponent(out Renderer renderer))
                    continue;

                // Read first: other components drive these blocks too, the fades among them.
                renderer.GetPropertyBlock(m_shellBlock);
                WriteShellSettings(m_shellBlock, band);
                renderer.SetPropertyBlock(m_shellBlock);
            }
        }

        /// <summary>Settings a subclass wants live on its shells. Nothing to do for a plain sphere.</summary>
        protected virtual void WriteShellSettings(MaterialPropertyBlock block, int band) { }

        void OnEnable() => ApplyShellSettings();

#if UNITY_EDITOR
        void OnValidate() => ApplyShellSettings();
#endif

        /// <summary>Renderer this component acts on: the assigned one, else this object's, a parent's or a child's.</summary>
        public GsplatRenderer ResolveRenderer()
        {
            if (Renderer)
                return Renderer;
            if (TryGetComponent(out GsplatRenderer here))
                return here;
            var found = GetComponentInParent<GsplatRenderer>();
            return found ? found : GetComponentInChildren<GsplatRenderer>();
        }

        protected virtual void Reset() => Renderer = ResolveRenderer();

        public int BandCount => Mathf.Max(ShellCount, 1);

        /// <summary>
        /// Inner radius of band <paramref name="index"/>. Bands tile from the start radius to the
        /// end radius, spaced geometrically because parallax error scales with distance ratio
        /// rather than distance, so equal ratios cost equal error. The outermost band starts at
        /// the end radius and has no outer bound.
        /// </summary>
        public float BandInnerRadius(int index)
        {
            if (index <= 0 || BandCount == 1)
                return StartRadius;

            float end = Mathf.Max(EndRadius, StartRadius * 1.01f);
            float t = index / (float)(BandCount - 1);

            // Boundaries even in r^-k. At k = 1 that is even in inverse distance, which equalises
            // angular error, since a shell at R misplaces content at d by an angle proportional to
            // |1/d - 1/R|. Raising k crowds them inward: the near bands get narrower than an equal
            // share and the far ones wider. As k approaches zero the spacing tends to even ratios,
            // which is a different limit entirely and has to be taken directly.
            float k = Mathf.Max(SpacingBias, 0f);
            if (k < 0.05f)
                return StartRadius * Mathf.Pow(end / StartRadius, t);

            float inverse = Mathf.Lerp(Mathf.Pow(StartRadius, -k), Mathf.Pow(end, -k), t);
            return Mathf.Pow(inverse, -1f / k);
        }

        /// <summary>
        /// Radius the band's capture actually starts at, which reaches back inside the shell in
        /// front of it by <see cref="BandOverlap"/>. Band 0 reaches back into the live splats
        /// instead of into another shell, so it only does so when
        /// <see cref="OverlapInnermostBand"/> says to.
        /// </summary>
        public float BandCaptureInnerRadius(int index) =>
            index > 0 || OverlapInnermostBand
                ? BandInnerRadius(index) * (1f - Mathf.Clamp01(BandOverlap))
                : BandInnerRadius(index);

        /// <summary>
        /// Outer radius of band <paramref name="index"/>, or 0 for the outermost band, which runs
        /// to infinity and so needs no bounding cutout.
        /// </summary>
        public float BandOuterRadius(int index) =>
            index >= BandCount - 1 ? 0f : BandInnerRadius(index + 1);

        /// <summary>
        /// Radius the band's shell mesh sits at. Inner bands sit at their own inner radius so the
        /// seam against the band inside them is parallax-free; only the outermost, which has
        /// nothing beyond it to seam against, takes the multiplier.
        /// </summary>
        public float BandShellRadius(int index) =>
            index >= BandCount - 1
                ? BandInnerRadius(index) * Mathf.Max(SphereRadiusMultiplier, 1f)
                : BandInnerRadius(index);

        /// <summary>
        /// Render queue for a band. Shells composite back to front, so the outermost draws first
        /// and each band inside it one step later, with the live splats last at 3000.
        /// </summary>
        public int BandRenderQueue(int index) => 2900 + (BandCount - 1 - index);

        // ------------------------------------------------------------------ shell shape

        /// <summary>
        /// Whether a band needs a depth panorama captured alongside its colour one. Costs a second
        /// pass, so the base backdrop, whose shells are plain spheres, never asks -- and a subclass
        /// can decline per band where displacement would not pay for itself.
        /// </summary>
        public virtual bool NeedsDepthCapture(int band) => false;

        /// <summary>
        /// Per-splat alpha a splat must exceed to count toward a nearest-surface depth capture.
        /// Zero captures the alpha-weighted mean through the volume instead.
        /// </summary>
        public virtual float DepthCaptureCutoff => 0f;

        /// <summary>
        /// How far past its inner radius the outermost band's depth range reaches, as a multiple.
        /// Every other band is bounded by the next one; this one has nothing beyond it, so it needs
        /// a stated end. Content further out clamps there, which is the sky sphere and is fine.
        /// </summary>
        const float k_openBandFarMultiple = 4f;

        /// <summary>Range depth is normalized against for a band: 0 at Near, 1 at Far.</summary>
        public void DepthCaptureRange(int band, out float near, out float far)
        {
            near = BandCaptureInnerRadius(band);
            float outer = BandOuterRadius(band);
            far = outer > 0f ? outer : near * k_openBandFarMultiple;
        }

        /// <summary>
        /// Builds the mesh a shell hangs on: a unit sphere, scaled to the shell radius by the
        /// instance transform.
        ///
        /// The shader recovers its sampling direction from normalize(positionOS), so the mapping
        /// depends only on the direction of each vertex and not on its distance.
        /// </summary>
        public static Mesh CreateShellMesh()
        {
            var mesh = BuildUnitSphere(64, 32);
            mesh.name = "BackdropSphere";
            return mesh;
        }

        /// <summary>
        /// Outward-wound UV sphere. Wound to match Unity's own, so the backdrop shader's Cull Front
        /// keeps the inside-facing half -- winding it the other way leaves a shell visible only
        /// from outside.
        /// </summary>
        static Mesh BuildUnitSphere(int longitudes, int latitudes)
        {
            var vertices = new List<Vector3>((longitudes + 1) * (latitudes + 1));
            var triangles = new List<int>(longitudes * latitudes * 6);

            for (int y = 0; y <= latitudes; y++)
            {
                float polar = y / (float)latitudes * Mathf.PI;
                for (int x = 0; x <= longitudes; x++)
                {
                    float azimuth = x / (float)longitudes * Mathf.PI * 2f;
                    vertices.Add(new Vector3(
                        Mathf.Sin(polar) * Mathf.Cos(azimuth),
                        Mathf.Cos(polar),
                        Mathf.Sin(polar) * Mathf.Sin(azimuth)));
                }
            }

            for (int y = 0; y < latitudes; y++)
            for (int x = 0; x < longitudes; x++)
            {
                int a = y * (longitudes + 1) + x;
                int b = a + longitudes + 1;
                triangles.Add(a); triangles.Add(a + 1); triangles.Add(b);
                triangles.Add(a + 1); triangles.Add(b + 1); triangles.Add(b);
            }

            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Fraction of the viewer's movement this band's shell should follow.
        ///
        /// Move the viewer by p and translate a shell of radius R by a*p, and a texel on it appears
        /// to shift by (1-a)*p/R instead of the p/d that content genuinely at distance d demands.
        /// Equate them and a = 1 - R/d. The same constant also fixes the radial case: approaching
        /// content at d should magnify it by d/(d-p), and the translated shell magnifies by
        /// R/(R-(1-a)p), which agrees under exactly the same condition. So one factor per shell
        /// corrects both the sideways shift and the looming.
        ///
        /// A band is taken to sit at the geometric mean of its bounds. Because bands are spaced
        /// geometrically, every interior band ends up with the same factor, 1 - 1/sqrt(ratio).
        /// The outermost band has no outer bound, so it is treated as reaching three times its
        /// inner radius -- far enough to behave nearly like a skybox without going fully rigid.
        /// </summary>
        public float BandParallaxFactor(int index)
        {
            float inner = BandInnerRadius(index);
            float outer = BandOuterRadius(index);

            // Harmonic mean, for the same reason the spacing is: error lives in 1/r, so the depth
            // that best represents a band is the one halfway across it in inverse distance. It
            // also settles the outermost band without an invented number -- with no outer bound,
            // 1/outer is zero and the representative depth falls out as twice the inner radius.
            float representative = outer > 0f
                ? 2f * inner * outer / (inner + outer)
                : 2f * inner;

            return Mathf.Clamp01(1f - BandShellRadius(index) / Mathf.Max(representative, 0.001f));
        }

        /// <summary>
        /// Angular error a viewer straying <paramref name="playRadius"/> from the capture origin
        /// sees against content sitting at the cut radius.
        /// </summary>
        public float ParallaxErrorDegrees(float playRadius) =>
            Mathf.Atan2(playRadius, Mathf.Max(StartRadius, 0.001f)) * Mathf.Rad2Deg;

        /// <summary>
        /// Slides each shell along with the viewer by its own fraction. Runs only in play mode:
        /// nudging these transforms in the editor would leave the scene permanently dirty for a
        /// purely visual effect that cannot be judged from a static viewpoint anyway.
        /// </summary>
        void LateUpdate()
        {
            if (Shells.Count == 0)
                return;

            var renderer = ResolveRenderer();
            if (!renderer)
                return;

            Vector3 origin = renderer.transform.TransformPoint(BakedOriginLocal);
            Vector3 offset = Vector3.zero;

            if (Application.isPlaying && ParallaxCompensation > 0f)
            {
                var viewer = Viewer ? Viewer : Camera.main ? Camera.main.transform : null;
                if (viewer)
                    offset = viewer.position - origin;
            }

            foreach (var shell in Shells)
            {
                if (shell?.Instance == null)
                    continue;

                // A reprojecting shell reads the capture origin off its own transform, so moving it
                // would have the march measure depth from a point the panorama was never captured
                // from. Sliding one does not merely double-correct, it invalidates the geometry the
                // correction is built on.
                Vector3 target = shell.DepthMap
                    ? origin
                    : origin + offset * (shell.ParallaxFactor * ParallaxCompensation);
                // Only write on a real change, so an idle head does not dirty the scene.
                if ((shell.Instance.transform.position - target).sqrMagnitude > 1e-10f)
                    shell.Instance.transform.position = target;
            }
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Vector3 origin = CaptureOrigin ? CaptureOrigin.position : transform.position;

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireSphere(origin, StartRadius);

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
            for (int i = 0; i < BandCount; i++)
                Gizmos.DrawWireSphere(origin, BandShellRadius(i));
        }
#endif
    }
}
