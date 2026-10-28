using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// "Close the view down to nothing, or open it back up, through a shrinking circle."
    /// </summary>
    /// <remarks>
    /// Attach anywhere, it finds the main camera unless you point it at one. Cover() closes the
    /// circle until the colour fills the view; Clear() opens it back up. Wire either to a
    /// UnityEvent, or tick Start Covered to open the scene with an iris-in.
    ///
    /// Also known as an iris wipe or vignette wipe. Gentler than a plain fade in a headset,
    /// because the user keeps a clear centre to look at for most of the transition, and
    /// narrowing the periphery is itself a comfort technique during motion.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Camera Circle Wipe")]
    public class CameraCircleWipe : CameraOverlay
    {
        [Header("Wipe")]
        [Tooltip("How soft the circle's edge is, as a fraction of the radius. 0 is a hard " +
                 "cut-out, 0.1 is a gentle vignette. Some softness hides the fact that the " +
                 "circle is made of triangles.")]
        [Range(0.0001f, 1f)] public float edgeSoftness = 0.06f;

        [Tooltip("Radius the circle opens out to when fully clear. A corner of the view sits at " +
                 "about 1.42, so 1.5 guarantees no ring of colour is left in the corners; lower " +
                 "leaves a permanent vignette, which can be what you want.")]
        [Range(0.5f, 2f)] public float openRadius = 1.55f;

        /// <inheritdoc/>
        /// <remarks>
        /// The alpha stays put and the hole does the work: at <c>amount</c> 0 the hole is wide
        /// enough to clear the corners, and at 1 it has closed to nothing. The edge is softened
        /// in proportion to the radius so the vignette does not turn into a hard ring as it
        /// shrinks, with a floor, or the last moments of the close would alias badly.
        /// </remarks>
        protected override void Draw(Material target, Color tint, float amount)
        {
            float radius = Mathf.Lerp(openRadius, 0f, amount);
            float softness = Mathf.Max(radius * edgeSoftness, 0.002f);

            // The soft edge is a ramp measured outward from the radius, so a radius of zero
            // still leaves the very centre clear, a pinhole in the middle of what is supposed
            // to be a solid screen, sitting exactly where the user is looking. Sliding the
            // radius below zero as it closes brings the ramp in with it.
            Write(target, tint, radius - softness * amount, softness);
        }
    }
}
