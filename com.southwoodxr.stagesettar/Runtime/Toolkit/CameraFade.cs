using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// "Fade the view to a colour, and back."
    /// </summary>
    /// <remarks>
    /// Attach anywhere, it finds the main camera unless you point it at one. Call Cover() to
    /// fade to the colour and Clear() to fade back, or wire either to a UnityEvent. Tick Start
    /// Covered to have the scene fade up from black when it loads.
    ///
    /// This is the standard VR comfort move: fade out before a teleport or a scene change, fade
    /// back in after. Doing it as a head-locked quad rather than a post-process means it survives
    /// pipeline changes and renders correctly to both eyes.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Camera Fade")]
    [DisallowMultipleComponent]
    public class CameraFade : CameraOverlay
    {
        /// <inheritdoc/>
        /// <remarks>
        /// A negative radius puts the hole behind the centre of the quad, so no part of it is
        /// clear and the whole view takes the colour. The fade itself is entirely in the alpha.
        /// </remarks>
        protected override void Draw(Material target, Color tint, float amount)
        {
            tint.a *= amount;
            Write(target, tint, -1f, 1f);
        }
    }
}
