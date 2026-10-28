using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// A backdrop that keeps plain spheres and corrects them per pixel against a captured depth
    /// panorama, rather than reshaping geometry to it.
    ///
    /// Displacing a mesh with the same data hits three limits that are structural rather than
    /// tuning: the mesh is coarser than the depth map, so most of the detail never reaches
    /// geometry; a triangle straddling a depth discontinuity can only stretch or be dropped, and
    /// both are visible; and silhouettes land wherever a vertex happens to be rather than where
    /// the edge is.
    ///
    /// Reprojecting sidesteps all three. There is no topology to stretch or drop, the full depth
    /// resolution is used, and edges resolve per pixel. What remains is disocclusion -- content
    /// hidden from the capture point genuinely was not recorded -- which shows as the occluder's
    /// edge smeared along the ray instead of as a hole. Softer, but still an artefact, and the
    /// reason to keep more than one shell: a further shell holds what a nearer one hid.
    /// </summary>
    [AddComponentMenu("StageSettAR/Splats/Splat Shell Reprojection")]
    public class SplatShellReprojection : SplatShells
    {
        [Header("Reprojection")]
        [Tooltip(
            "Samples taken along each view ray while looking for the surface.\n\n" +
            "This is a search, not a refinement: a pixel whose ray should now strike a wall, " +
            "because the viewer stepped sideways, has to find that wall from scratch. Too few steps " +
            "and thin surfaces are stepped straight over, which shows as a silhouette that breaks " +
            "up rather than moves.")]
        [Range(4, 64)] public int MarchSteps = 16;

        [Tooltip(
            "Bisections after a crossing is found, before the final interpolation.\n\n" +
            "Each one halves the interval the crossing is known to lie in, assuming nothing about " +
            "the surface -- which is what makes them safe across a silhouette, where the recorded " +
            "distance jumps and interpolating between the two ends is meaningless.\n\n" +
            "They are no longer what sets the accuracy, though. However far bisection is taken it " +
            "leaves an interval, an interval is a step, and a step is what reads as terracing. The " +
            "secant step that follows removes it outright, so these only have to get the bracket " +
            "small enough that a silhouette inside it cannot mislead that interpolation. Two is " +
            "usually enough; the samples are better spent on March Steps.")]
        [Range(0, 8)] public int RefineSteps = 2;

        [Tooltip(
            "Side of the depth map, which is square: depth is stored as an octahedral chart rather " +
            "than a lat-long panorama, so it covers the sphere with one unit square.\n\n" +
            "It steers the colour, so a depth texel much coarser than a colour texel drags a whole " +
            "block of colour with it and the result ripples.\n\n" +
            "Spend it on the worst direction, not the average. An octahedron spreads its texels " +
            "more evenly than a lat-long map, which crowds them at the poles and starves the " +
            "equator, but it is far from uniform itself: solid angle per texel varies five to one " +
            "across the chart, finest toward the axes and coarsest toward the diagonals, so its " +
            "poorest resolution is about two thirds of its average. 1536 holds around 6.5 texels " +
            "per degree everywhere, which beats a 2048 by 1024 panorama across the whole sphere " +
            "rather than only away from its equator.")]
        public int DepthResolution = 1536;

        [Tooltip(
            "Leave the outermost shell unreprojected.\n\n" +
            "It is the one band nothing bounds, so its depth range is invented rather than derived, " +
            "and its content is sky at no particular distance -- reprojecting it costs samples to " +
            "move things that were never anywhere in particular. Ignored with a single shell.")]
        public bool FlatOutermostShell = true;

        /// <summary>Whether this band keeps the plain sphere. Only the outermost, and never if it is the only one.</summary>
        bool IsFlatBand(int band) => FlatOutermostShell && BandCount > 1 && band >= BandCount - 1;

        public override bool NeedsDepthCapture(int band) => !IsFlatBand(band);

        /// <summary>
        /// Nearest surface, always. The alternative averages through the volume, which puts the
        /// recorded distance in empty space wherever a ray catches a near surface and a far one --
        /// and a reprojection aimed at empty space lands nowhere in particular.
        /// </summary>
        public override float DepthCaptureCutoff => 0.3f;

        static readonly int k_marchSteps = Shader.PropertyToID("_MarchSteps");
        static readonly int k_refineSteps = Shader.PropertyToID("_RefineSteps");

        protected override void WriteShellSettings(MaterialPropertyBlock block, int band)
        {
            block.SetFloat(k_marchSteps, MarchSteps);
            block.SetFloat(k_refineSteps, RefineSteps);
        }

        protected override void Reset()
        {
            base.Reset();

            // Shells exist to approximate depth, and this has the real thing. The ones that remain
            // are there to hold what nearer shells occlude, which is the one job reprojection
            // cannot do for itself. These values are what the SplatDemo scene settled on for a
            // room-scale capture: live splats inside 3 m, six shells packed toward the viewer.
            StartRadius = 3f;
            SpacingBias = 2f;
            ShellCount = 6;
            // Overlap exists so a surface crossing a boundary lands on both shells and ghosts
            // rather than tearing. Reprojection makes that a liability: both copies are drawn,
            // each corrected against its own band's depth, and the pair separate as the viewer
            // moves. Bands own their content outright here.
            BandOverlap = 0f;
            // The march already accounts for where the viewer is, using real depth, and it reads
            // the capture origin off the shell's own transform -- so sliding the shell would make
            // it measure from somewhere the panorama never saw. Off, and the runtime refuses to
            // move a shell carrying a depth map regardless.
            ParallaxCompensation = 0f;
        }
    }
}
