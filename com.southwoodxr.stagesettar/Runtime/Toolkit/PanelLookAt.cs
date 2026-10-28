using UnityEngine;

namespace StageSettAR
{
    /// <summary>When the panel turns to face the camera.</summary>
    public enum LookAtMode
    {
        /// <summary>Always facing, from the moment it is enabled.</summary>
        Always = 0,

        /// <summary>Only while the user looks at or near it; returns to rest afterwards.</summary>
        WhenGazed = 1,
    }

    /// <summary>
    /// "Turn this to face the user, always or only while they are looking near it."
    /// </summary>
    /// <remarks>
    /// Put it on a panel, sign, or world-space UI element. In When Gazed mode it needs a collider to
    /// look at, and it turns back to its authored rotation once the gaze leaves.
    /// <para>
    /// Yaw Only keeps it upright, which is what readable text wants: it swings to face the user but
    /// never tips, so a user looking down from above still reads level text. Turn it off for a panel
    /// that should square up to the camera whatever height it is viewed from.
    /// </para>
    /// <para>
    /// A Unity Quad and a world-space Canvas are readable from their back (-Z) side, so the default
    /// face offset turns them half a circle to put their readable side towards the user.
    /// </para>
    /// </remarks>
    // Runs before PanelDynamicTilt on purpose. This decides the panel's base orientation; the
    // tilt is a lean applied on top of it, so it has to be computed first.
    [DefaultExecutionOrder(-10)]
    [AddComponentMenu("StageSettAR/Panel Look At")]
    [DisallowMultipleComponent]
    public sealed class PanelLookAt : MonoBehaviour
    {
        [Header("Behaviour")]
        [Tooltip("Always faces the camera from the start. When Gazed only turns while the user " +
                 "is looking at it, and turns back when they look away.")]
        public LookAtMode mode = LookAtMode.Always;

        [Tooltip("Keep the panel upright, turning only about the world up axis. This is what " +
                 "readable text wants. Off squares the panel up to the camera on every axis, " +
                 "which tips it when viewed from above or below.")]
        public bool yawOnly = true;

        [Tooltip("Turn rate in degrees per second. 0 snaps instantly.")]
        public float degreesPerSecond = 180f;

        [Tooltip("Extra rotation applied after facing the camera, in degrees. The default Y = 180 " +
                 "suits a Quad or world-space Canvas; a Unity Plane faces +Y, so it wants X = 90 " +
                 "(and Y = 180 if it comes out backwards).")]
        public Vector3 faceOffsetEuler = new Vector3(0f, 180f, 0f);

        [Header("Gaze (When Gazed mode)")]
        [Tooltip("The collider the user has to look at. Found automatically on this object or its children when empty.")]
        public Collider gazeCollider;

        [Tooltip("How far the user can be and still trigger the turn, in metres.")]
        public float maxDistance = 20f;

        [Tooltip("Ray needs the user to look straight at it. Sphere sweeps a ball of Gaze Radius " +
                 "along the same line, so looking NEAR it counts, which is usually what you " +
                 "want here, since a panel edge-on is a small target.")]
        public GazeShape gazeShape = GazeShape.Sphere;

        [Tooltip("Radius of that ball, in metres, when Gaze Shape is Sphere.")]
        public float gazeRadius = 0.25f;

        [Tooltip("A solid object between the user and the panel stops the turn. Triggers never " +
                 "block; they are not solid.")]
        public bool blockedBySolidObjects = true;

        [Tooltip("Which layers count as solid, when the above is on.")]
        public LayerMask blockedBy = ~0;

        [Tooltip("Seconds to keep facing after the gaze leaves, before turning back. Stops a " +
                 "panel snapping away the instant the user glances off it.")]
        public float holdSeconds = 0.4f;

        [Tooltip("Face this camera instead of the user's. Almost always empty.")]
        public Camera faceCamera;

        // The rotation the panel was authored with, and what it returns to.
        Quaternion _rest;
        bool _captured;
        float _heldUntil;
        bool _warned;

        // The facing this has settled on, tracked here rather than read back off the transform.
        // PanelDynamicTilt writes a lean into the transform on top of this, and reading that back
        // would fold the lean into the facing and let it accumulate.
        Quaternion _current;
        bool _started;

        /// <summary>
        /// The base orientation this wants, without anything layered on top.
        /// </summary>
        /// <remarks>
        /// PanelDynamicTilt composes its lean onto this instead of onto the authored rotation, so
        /// the two can be used together: this one aims the panel, that one leans it.
        /// </remarks>
        public Quaternion CurrentLocalRotation => _started ? _current : transform.localRotation;

        /// <summary>Whether the panel is currently being looked at.</summary>
        public bool IsGazedAt { get; private set; }

        void Reset()
        {
            gazeCollider = GetComponentInChildren<Collider>();
        }

        void OnEnable()
        {
            Capture();
            IsGazedAt = false;
            _heldUntil = 0f;
            _current = _rest;
            _started = true;
        }

        void Capture()
        {
            if (_captured) return;
            _rest = transform.localRotation;
            _captured = true;
        }

        void Update()
        {
            var cam = faceCamera != null ? faceCamera : ToolkitRig.UserCamera;
            if (cam == null) return;
            Capture();

            bool facing = mode == LookAtMode.Always || ShouldFace();

            // Target in local space, so the panel can sit under a moving parent without the
            // facing fighting the parent's rotation.
            Quaternion target = facing ? LocalFacing(cam) : _rest;

            _current = degreesPerSecond > 0f
                ? Quaternion.RotateTowards(_current, target, degreesPerSecond * Time.deltaTime)
                : target;

            // Written even when a PanelDynamicTilt is present: it overwrites this in the same
            // frame with the same value plus its lean, so this one still works on its own.
            transform.localRotation = _current;
        }

        /// <summary>Whether the gaze is on the panel, including the hold-on grace period.</summary>
        bool ShouldFace()
        {
            if (gazeCollider == null) gazeCollider = GetComponentInChildren<Collider>();
            if (gazeCollider == null)
            {
                // Nothing to look at, so When Gazed can never become true. Say so once rather
                // than sitting there doing nothing.
                if (_warned) return false;
                _warned = true;
                Debug.LogWarning($"[PanelLookAt] {name}: When Gazed mode needs a collider. Add " +
                                 "one, or assign Gaze Collider, or use Always.", this);
                return false;
            }

            IsGazedAt = GazeProbe.Look(faceCamera, gazeCollider, gazeShape, gazeRadius, maxDistance,
                                       blockedBySolidObjects, blockedBy, out _);
            if (IsGazedAt) _heldUntil = Time.time + Mathf.Max(0f, holdSeconds);
            return IsGazedAt || Time.time < _heldUntil;
        }

        /// <summary>The local rotation that points the panel at the camera.</summary>
        Quaternion LocalFacing(Camera cam)
        {
            Vector3 toCamera = cam.transform.position - transform.position;
            if (yawOnly)
            {
                // Flatten against world up, so the panel turns but never tips. Falling back to
                // rest matters: directly above or below, the flattened direction is zero length
                // and LookRotation would spin the panel arbitrarily.
                toCamera.y = 0f;
            }
            if (toCamera.sqrMagnitude <= 1e-6f) return _rest;

            Quaternion world = Quaternion.LookRotation(toCamera.normalized, Vector3.up)
                               * Quaternion.Euler(faceOffsetEuler);

            // World to local, so a parented panel is not thrown off by its parent's rotation.
            Transform parent = transform.parent;
            return parent != null ? Quaternion.Inverse(parent.rotation) * world : world;
        }
    }
}
