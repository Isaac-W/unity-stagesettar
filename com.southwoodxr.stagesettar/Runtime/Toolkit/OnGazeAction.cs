using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// "When the user looks at this, do Y. When they look away, do Z."
    /// </summary>
    /// <remarks>
    /// <para>
    /// Put it on any object with a collider (a child's collider counts too), or point
    /// <see cref="gazeCollider"/> at one somewhere else. Nothing else is needed: it finds the user
    /// itself.
    /// </para>
    /// <para>
    /// It does its own raycast out of the user's gaze source, so it needs no eye tracking, no XR
    /// Interactor and no Interaction Manager. That is why it works in the XR Device Simulator, where
    /// the simulated headset moves the camera but reports no eye gaze.
    /// </para>
    /// <para>
    /// Solid objects in the way block the gaze; triggers do not. So a wall between the user and this
    /// object stops it firing, while a trigger volume in front of it, or another gaze target, does
    /// not, and two gaze targets one behind the other both fire.
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/On Gaze Action")]
    [DisallowMultipleComponent]
    public sealed class OnGazeAction : MonoBehaviour
    {
        [Tooltip("The collider that counts as being looked at. Found automatically on this object or its children when empty.")]
        public Collider gazeCollider;

        [Tooltip("How far the user can be and still look at it, in metres.")]
        public float maxDistance = 20f;

        [Tooltip("Ray needs the user to look straight at the collider. Sphere sweeps a ball of " +
                 "Gaze Radius along the same line, so looking near it counts: much kinder on " +
                 "small or distant targets, and on head-only aiming with no eye tracking.")]
        public GazeShape gazeShape = GazeShape.Sphere;

        [Tooltip("Radius of that ball, in metres, when Gaze Shape is Sphere. This is slack in " +
                 "world units, not degrees, so it forgives more the closer the user is: 0.1 m is " +
                 "about 2 degrees of aim at 3 m away, and about 6 degrees at 1 m.")]
        public float gazeRadius = 0.1f;

        [Tooltip("Seconds the user must hold their gaze before On Enter fires. 0 fires the " +
                 "instant they look at it, which is twitchy: a quarter second or so stops a " +
                 "glance in passing from triggering it.")]
        public float dwellSeconds = 0.25f;

        [Tooltip("A solid object between the user and this one stops the gaze. Colliders marked " +
                 "Is Trigger never block (they are not solid), so other gaze targets and " +
                 "trigger volumes do not get in the way.")]
        public bool blockedBySolidObjects = true;

        [Tooltip("Which layers count as solid, when the above is on. Take a layer out to let the " +
                 "user look through it: useful for a floor, a splat mesh, or scenery that " +
                 "should not hide the target. Ignore Raycast never blocks.")]
        public LayerMask blockedBy = ~0;

        [Tooltip("Fire On Exit once at startup, before anything has been looked at. Use it to " +
                 "put the scene in its not-yet-gazed state (hiding whatever On Enter reveals) " +
                 "without having to author that state by hand and keep the two in step.")]
        public bool fireExitOnStart;

        [Tooltip("Look through this camera instead of the user's gaze source. Almost always empty.")]
        public Camera gazeCamera;

        [Tooltip("Draw the target in the Scene view, and the gaze line in play mode (green when it is hitting).")]
        public bool showGizmos = true;

        public UnityEvent onEnter;
        public UnityEvent onExit;

        bool _gazedAt;
        float _heldFor;
        bool _warned;

        /// <summary>Whether the user is looking at it right now (after any dwell).</summary>
        public bool IsGazedAt => _gazedAt;

        void Reset()
        {
            gazeCollider = GetComponentInChildren<Collider>();
        }

        void Start()
        {
            // Start, not OnEnable: the objects this event drives have all had their Awake by
            // now, and it fires once for the scene rather than again on every re-enable.
            if (fireExitOnStart) onExit?.Invoke();
        }

        void OnEnable()
        {
            // Start clean, so re-enabling cannot leave it thinking the gaze is still held.
            _gazedAt = false;
            _heldFor = 0f;
        }

        void OnDisable()
        {
            // Anything switched on by On Enter would otherwise stay on forever.
            if (!_gazedAt) return;
            _gazedAt = false;
            _heldFor = 0f;
            onExit?.Invoke();
        }

        void Update()
        {
            var target = ResolveCollider();
            if (target == null)
            {
                Warn();
                return;
            }

            bool looking = GazeProbe.Look(gazeCamera, target, gazeShape, gazeRadius, maxDistance,
                                          blockedBySolidObjects, blockedBy, out _);

            if (looking)
            {
                if (_gazedAt) return;
                _heldFor += Time.deltaTime;
                if (_heldFor < dwellSeconds) return;
                _gazedAt = true;
                onEnter?.Invoke();
                return;
            }

            _heldFor = 0f;
            if (!_gazedAt) return;
            _gazedAt = false;
            onExit?.Invoke();
        }

        Collider ResolveCollider()
        {
            if (gazeCollider == null) gazeCollider = GetComponentInChildren<Collider>();
            return gazeCollider;
        }

        void Warn()
        {
            // Once only: this runs every frame, and the whole problem being reported is that
            // nothing happens, so it should say why instead of staying silent.
            if (_warned) return;
            _warned = true;
            Debug.LogWarning($"[OnGazeAction] {name}: no collider. Add one to this object, " +
                             "or assign Gaze Collider.", this);
        }

        void OnDrawGizmos()
        {
            if (!showGizmos) return;
            ToolkitRig.DrawColliderGizmo(ResolveCollider(), _gazedAt ? Color.green : new Color(1f, 0.8f, 0.2f));
        }
    }
}
