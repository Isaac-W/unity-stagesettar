using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Finds the user's camera and head so the toolkit components need no hookup.
    /// </summary>
    /// <remarks>
    /// Every component that needs "the user" asks here when its own field is empty. The StageSettAR
    /// rig tags its camera MainCamera and carries a Player-tagged Head under it, so on the rig both
    /// resolve at once. A scene with some other rig still works as long as its camera is tagged
    /// MainCamera, or it is the only active camera.
    /// </remarks>
    public static class ToolkitRig
    {
        public const string PlayerTag = "Player";

        /// <summary>The camera the user looks through, or null when the scene has none yet.</summary>
        public static Camera UserCamera
        {
            get
            {
                // Camera.main is cached by Unity, so this is cheap enough to call every frame.
                var main = Camera.main;
                if (main != null) return main;

                var source = GazeSource.Active;
                if (source != null && source.UserCamera != null) return source.UserCamera;

                // Last resort for rigs that forgot the MainCamera tag. Skips cameras that draw
                // into a texture (mirrors, minimaps), which are never the user's view.
                foreach (var cam in Camera.allCameras)
                    if (cam.targetTexture == null && cam.enabled) return cam;
                return null;
            }
        }

        /// <summary>
        /// The user's head: the Player-tagged object under the camera on the StageSettAR rig,
        /// otherwise the camera itself.
        /// </summary>
        public static Transform Head
        {
            get
            {
                // FindWithTag throws if the tag does not exist, which a missing Player tag in a
                // stripped-down project would turn into a spam of exceptions.
                try
                {
                    var player = GameObject.FindWithTag(PlayerTag);
                    if (player != null) return player.transform;
                }
                catch (UnityException) { }
                var cam = UserCamera;
                return cam != null ? cam.transform : null;
            }
        }

        /// <summary>The line the user is looking along, from the gaze source if there is one, else the camera.</summary>
        public static bool TryGetGazeRay(Camera overrideCamera, out Ray ray)
        {
            if (overrideCamera == null && GazeSource.Active != null && GazeSource.Active.TryGetRay(out ray))
                return true;

            var cam = overrideCamera != null ? overrideCamera : UserCamera;
            if (cam == null)
            {
                ray = default;
                return false;
            }
            ray = new Ray(cam.transform.position, cam.transform.forward);
            return true;
        }

        /// <summary>
        /// Whether <paramref name="other"/> (or the body it belongs to) carries <paramref name="tag"/>.
        /// An empty tag matches everything.
        /// </summary>
        public static bool HasTag(Collider other, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return true;
            try
            {
                if (other.CompareTag(tag)) return true;
                // A compound collider's children usually carry no tag of their own.
                var body = other.attachedRigidbody;
                return body != null && body.CompareTag(tag);
            }
            catch (UnityException)
            {
                // CompareTag throws for a tag that was never defined. Treat it as "no match"; the
                // inspector's tag drop-down is not available for plain strings.
                return false;
            }
        }

        // Drawn in the Scene view only, so a hidden zone or target can still be found and sized.
        internal static void DrawColliderGizmo(Collider collider, Color color)
        {
            if (collider == null) return;
            var old = Gizmos.matrix;
            Gizmos.matrix = collider.transform.localToWorldMatrix;
            var fill = new Color(color.r, color.g, color.b, 0.12f);
            switch (collider)
            {
                case BoxCollider box:
                    Gizmos.color = fill;
                    Gizmos.DrawCube(box.center, box.size);
                    Gizmos.color = color;
                    Gizmos.DrawWireCube(box.center, box.size);
                    break;
                case SphereCollider sphere:
                    Gizmos.color = fill;
                    Gizmos.DrawSphere(sphere.center, sphere.radius);
                    Gizmos.color = color;
                    Gizmos.DrawWireSphere(sphere.center, sphere.radius);
                    break;
                default:
                    Gizmos.matrix = old;
                    Gizmos.color = color;
                    Gizmos.DrawWireCube(collider.bounds.center, collider.bounds.size);
                    break;
            }
            Gizmos.matrix = old;
        }
    }
}
