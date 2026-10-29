using UnityEngine;

namespace StageSettAR
{
    /// <summary>Small helpers for Unity objects.</summary>
    public static class UnityObjects
    {
        /// <summary>Destroys an object we created: <c>Destroy</c> in play mode, where it is deferred, <c>DestroyImmediate</c> otherwise (<c>Destroy</c> logs an error in edit mode).</summary>
        public static void Discard(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
        }
    }
}
