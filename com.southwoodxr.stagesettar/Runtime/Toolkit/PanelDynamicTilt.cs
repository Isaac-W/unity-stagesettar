using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// "Tilt this panel according to where on it the user is looking."
    /// </summary>
    /// <remarks>
    /// Put it on a plane, quad, or world-space UI panel that has a collider. Look near the top
    /// and the top leans away (or toward you, if you flip Tilt Towards Gaze); look at the middle
    /// and it sits flat. It eases back to its authored rotation when the gaze leaves.
    /// <para>
    /// The panel's own face direction is measured from its collider, so this works on a Unity
    /// Plane (which faces local +Y), a Quad or Canvas (local +Z), or an imported panel facing
    /// whatever the artist modelled, with no axis setting to get wrong.
    /// </para>
    /// </remarks>
    // Runs after PanelLookAt, which decides the panel's base orientation when both are present.
    [DefaultExecutionOrder(0)]
    [AddComponentMenu("StageSettAR/Panel Dynamic Tilt")]
    [DisallowMultipleComponent]
    public sealed class PanelDynamicTilt : MonoBehaviour
    {
        [Tooltip("The collider that counts as the panel surface. Found automatically on this object or its children when empty.")]
        public Collider gazeCollider;

        [Tooltip("How far the user can be and still tilt it, in metres.")]
        public float maxDistance = 20f;

        [Tooltip("Ray needs the user to look straight at the panel. Sphere sweeps a ball of " +
                 "Gaze Radius along the same line, so looking near it counts.")]
        public GazeShape gazeShape = GazeShape.Ray;

        [Tooltip("Radius of that ball, in metres, when Gaze Shape is Sphere.")]
        public float gazeRadius = 0.1f;

        [Tooltip("A solid object between the user and the panel stops the tilt. Triggers never " +
                 "block; they are not solid.")]
        public bool blockedBySolidObjects = true;

        [Tooltip("Which layers count as solid, when the above is on.")]
        public LayerMask blockedBy = ~0;

        [Header("Tilt")]
        [Tooltip("How far it leans at the very edge of the panel, in degrees. Small is better: " +
                 "5 to 10 reads as a responsive surface, while 30 reads as a swinging sign.")]
        [Range(0f, 60f)] public float maxTiltAngle = 8f;

        [Tooltip("Off: the part being looked at leans AWAY, as if the gaze pressed it inward. " +
                 "On: that part leans TOWARD the user instead, as if drawn to the gaze.")]
        public bool tiltTowardsGaze;

        [Tooltip("Seconds for the tilt to settle. This is a time constant, so it covers about " +
                 "95% of the way in three of them: 0.12 feels immediate but not jumpy. 0 snaps.")]
        [Range(0f, 1f)] public float responseSeconds = 0.12f;

        [Tooltip("Ignore gaze this close to the middle, as a fraction of the half-width. Stops " +
                 "the panel drifting on tiny eye movements when the user is looking straight at " +
                 "the centre. 0 responds everywhere.")]
        [Range(0f, 0.9f)] public float deadZone = 0.1f;

        [Tooltip("Look through this camera instead of the user's gaze source. Almost always empty.")]
        public Camera gazeCamera;

        [Tooltip("Draw the panel's measured right/up axes in the Scene view.")]
        public bool showDebugAxes;

        // The rotation the panel was authored with. Tilt is a delta from this, so any rotation
        // set in the Inspector survives; overwriting localRotation outright would throw it away.
        Quaternion _rest;
        bool _captured;

        // Current tilt, eased toward the target each frame.
        float _tiltRight;
        float _tiltUp;

        // Set when a PanelLookAt shares this object. The two compose rather than compete: that
        // one aims the panel, this one leans it, and the lean is applied on top of the aim.
        PanelLookAt _lookAt;

        /// <summary>
        /// The orientation the lean is applied on top of.
        /// </summary>
        /// <remarks>
        /// The authored rotation normally, but a live PanelLookAt overrides it. Otherwise this
        /// would keep forcing the panel back to the rotation it was authored with and the facing
        /// would never take effect.
        /// </remarks>
        Quaternion Base => _lookAt != null && _lookAt.enabled ? _lookAt.CurrentLocalRotation : _rest;

        /// <summary>Whether the panel is currently being looked at.</summary>
        public bool IsGazedAt { get; private set; }

        void Reset()
        {
            gazeCollider = GetComponentInChildren<Collider>();
        }

        void OnEnable()
        {
            _lookAt = GetComponent<PanelLookAt>();
            Capture();
            _tiltRight = 0f;
            _tiltUp = 0f;
            IsGazedAt = false;
        }

        void OnDisable()
        {
            // Hand the panel back without its lean. Base, not rest: with a PanelLookAt present
            // that one still owns the orientation, and snapping to the authored rotation here
            // would jerk the panel away from where it is aiming.
            if (_captured) transform.localRotation = Base;
        }

        void Capture()
        {
            if (_captured) return;
            _rest = transform.localRotation;
            _captured = true;
        }

        void Update()
        {
            if (gazeCollider == null) gazeCollider = GetComponentInChildren<Collider>();
            var cam = gazeCamera != null ? gazeCamera : ToolkitRig.UserCamera;
            if (cam == null || gazeCollider == null) return;
            Capture();

            float wantRight = 0f;
            float wantUp = 0f;

            bool haveSurface = Surface(cam, gazeCollider, out Vector3 right, out Vector3 up, out Bounds local);

            IsGazedAt = GazeProbe.Look(gazeCamera, gazeCollider, gazeShape, gazeRadius, maxDistance,
                                       blockedBySolidObjects, blockedBy, out RaycastHit hit);
            if (IsGazedAt && haveSurface)
            {
                // Where the gaze landed, in the panel's own space, as -1..1 across each axis.
                Vector3 point = transform.InverseTransformPoint(hit.point) - local.center;
                float u = Offset(point, right, local);
                float v = Offset(point, up, local);

                // Signs derived rather than guessed, because they are easy to get backwards.
                // Cross(right, up) is the panel's normal, and with right/up oriented to the
                // viewer that normal points away from them. A positive turn about right carries
                // the top toward the normal (away), while a positive turn about up carries
                // the right edge toward the viewer, hence the opposite sign on that one.
                float sign = tiltTowardsGaze ? -1f : 1f;
                wantRight = sign * v * maxTiltAngle;
                wantUp = -sign * u * maxTiltAngle;
            }

            // Exponential ease: no fixed duration to tune, and it behaves the same for a small
            // correction as for a large one.
            float blend = responseSeconds > 0f ? 1f - Mathf.Exp(-Time.deltaTime / responseSeconds) : 1f;
            _tiltRight = Mathf.Lerp(_tiltRight, wantRight, blend);
            _tiltUp = Mathf.Lerp(_tiltUp, wantUp, blend);

            Quaternion basis = Base;
            if (!haveSurface)
            {
                transform.localRotation = basis;
                return;
            }

            // Both leans are expressed about the panel's own axes, so they compose in local
            // space and whatever set the base orientation stays untouched underneath.
            transform.localRotation = basis
                                      * Quaternion.AngleAxis(_tiltRight, right)
                                      * Quaternion.AngleAxis(_tiltUp, up);

            if (showDebugAxes)
            {
                Vector3 centre = transform.position;
                Debug.DrawRay(centre, transform.TransformDirection(right) * 0.25f, Color.red);
                Debug.DrawRay(centre, transform.TransformDirection(up) * 0.25f, Color.green);
            }
        }

        /// <summary>
        /// The panel's in-plane axes, in local space, oriented to the viewer.
        /// </summary>
        /// <remarks>
        /// The face is the thinnest axis of the collider; the other two lie in the surface. Which
        /// of those two is "up" is decided by comparing them against the camera's own up, so the
        /// tilt matches what the user perceives as up rather than whichever axis the model was
        /// built along. Both are then signed to point up and right from the viewer's side, so a
        /// panel facing away from the camera does not tilt backwards.
        /// </remarks>
        bool Surface(Camera cam, Collider target, out Vector3 right, out Vector3 up, out Bounds local)
        {
            right = Vector3.right;
            up = Vector3.up;
            local = GazeProbe.LocalBounds(target);

            int faceAxis = GazeProbe.ThinnestAxis(local);
            int a = (faceAxis + 1) % 3;
            int b = (faceAxis + 2) % 3;

            Vector3 axisA = Vector3.zero;
            Vector3 axisB = Vector3.zero;
            axisA[a] = 1f;
            axisB[b] = 1f;

            // Whichever in-plane axis lines up better with the camera's up is the panel's up.
            // Measured against the RESTING orientation, not the current one. Deciding this from
            // a panel that is already leaning lets the answer change as it leans, which feeds
            // back into the lean: the classic way a smoothed effect turns into a wobble.
            Quaternion basis = Base;
            Quaternion restWorld = transform.parent != null ? transform.parent.rotation * basis : basis;
            Vector3 camUp = cam.transform.up;
            float alignA = Mathf.Abs(Vector3.Dot(restWorld * axisA, camUp));
            float alignB = Mathf.Abs(Vector3.Dot(restWorld * axisB, camUp));
            if (alignA >= alignB)
            {
                up = axisA;
                right = axisB;
            }
            else
            {
                up = axisB;
                right = axisA;
            }

            // Point them the viewer's way round.
            if (Vector3.Dot(restWorld * up, camUp) < 0f) up = -up;
            if (Vector3.Dot(restWorld * right, cam.transform.right) < 0f) right = -right;

            // A degenerate collider (a sphere, or a panel with no thin axis) has no
            // meaningful surface to lean about.
            Vector3 size = local.size;
            return size[a] > 1e-5f && size[b] > 1e-5f;
        }

        /// <summary>How far along <paramref name="axis"/> the point sits, as -1..1.</summary>
        float Offset(Vector3 point, Vector3 axis, Bounds local)
        {
            int index = axis.x != 0f ? 0 : axis.y != 0f ? 1 : 2;
            float half = local.size[index] * 0.5f;
            if (half <= 1e-5f) return 0f;

            float along = Vector3.Dot(point, axis) / half;

            // Re-map so the dead zone is genuinely ignored rather than merely small: outside it,
            // the response still runs the full 0..1 by the edge.
            float magnitude = Mathf.Abs(along);
            if (magnitude <= deadZone) return 0f;
            float scaled = (magnitude - deadZone) / Mathf.Max(1f - deadZone, 1e-5f);
            return Mathf.Clamp01(scaled) * Mathf.Sign(along);
        }
    }
}
