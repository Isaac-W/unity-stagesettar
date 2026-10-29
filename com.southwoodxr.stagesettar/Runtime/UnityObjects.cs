using UnityEngine;

namespace StageSettAR
{
    /// <summary>Small helpers for Unity objects.</summary>
    public static class UnityObjects
    {
        /// <summary>
        /// Destroys an object we created: <c>Destroy</c> in play mode, where it is deferred to the end of the frame, and
        /// <c>DestroyImmediate</c> otherwise (<c>Destroy</c> logs an error in edit mode). Pass <paramref name="immediate"/>
        /// for a loop that makes many objects in one frame, so they do not all stay alive until the frame ends.
        /// </summary>
        public static void Discard(Object o, bool immediate = false)
        {
            if (o == null) return;
            if (immediate || !Application.isPlaying) Object.DestroyImmediate(o); else Object.Destroy(o);
        }
    }
}
