using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// "Keep this in front of me." Makes an object follow the user's view with a delay, an offset
    /// and a dead zone, so a panel drifts after you instead of being glued to your face.
    /// </summary>
    /// <remarks>
    /// Finds the user's camera itself. By default the object keeps the spot it was placed at relative
    /// to the user (Use Initial Offset). The object turns to face the user; text on a Canvas or Quad
    /// is readable from its back side, so those want Flip Forward on (the Info Panel menu item sets it).
    /// </remarks>
    [AddComponentMenu("StageSettAR/Tag Along")]
    public sealed class TagAlong : MonoBehaviour
    {
        [Tooltip("The user's transform. Found automatically (the main camera) when empty.")]
        public Transform userTransform;

        [Space(10)]
        [Tooltip("How fast the object moves towards the target position.")]
        public float followSpeed = 3f;

        [Tooltip("How far the user has to look/move away from the object to make it move.")]
        public float deadzone = 0.1f;

        [Space(10)]
        [Tooltip("Keep the spot the object was placed at, relative to the user's heading. Otherwise use the offset below.")]
        public bool useInitialOffset = true;

        [Tooltip("Where you want the object, relative to where the user faces. Default: 1 m ahead.")]
        public Vector3 offset = new Vector3(0, 0, 1);

        [Space(10)]
        [Tooltip("Whether the object should follow the user's yaw (left-right) rotation.")]
        public bool followYaw = true;

        [Tooltip("Whether the object should follow the user's pitch (up-down) rotation.")]
        public bool followPitch;

        [Space(10)]
        [Tooltip("Keep the object's height fixed in the world.")]
        public bool fixedHeight;

        [Tooltip("By default the object's forward vector points towards the user. Turn this on to flip it (Canvas and Quad text reads from behind).")]
        public bool flipForward;

        Vector3 _initialPosition;
        Vector3 _initialEuler;
        Vector3 _currentTarget;
        bool _started;

        void Start()
        {
            _initialPosition = transform.position;
            _initialEuler = transform.eulerAngles;
            TryBegin();
        }

        // The camera may not exist yet at Start (a rig spawned later), so beginning is retried.
        bool TryBegin()
        {
            if (_started) return true;
            if (userTransform == null)
            {
                var cam = ToolkitRig.UserCamera;
                if (cam == null) return false;
                userTransform = cam.transform;
            }

            if (useInitialOffset)
            {
                // Stored in the user's heading frame. The raw world offset would be right only if
                // the user happened to be facing along +Z when the scene started.
                offset = Quaternion.Inverse(Heading()) * (transform.position - userTransform.position);
            }

            transform.rotation = LookAtUser();
            _currentTarget = Target();
            transform.position = _currentTarget;
            _started = true;
            return true;
        }

        Quaternion Heading() =>
            Quaternion.Euler(followPitch ? userTransform.eulerAngles.x : 0f,
                             followYaw ? userTransform.eulerAngles.y : 0f, 0f);

        Quaternion LookAtUser()
        {
            Vector3 toUser = (userTransform.position - transform.position) * (flipForward ? -1f : 1f);
            if (toUser.sqrMagnitude < 1e-6f) return transform.rotation;
            Vector3 euler = Quaternion.LookRotation(toUser, Vector3.up).eulerAngles;

            // Reset the angles that are not being followed to how the object was placed.
            return Quaternion.Euler(followPitch ? euler.x : _initialEuler.x,
                                    followYaw ? euler.y : _initialEuler.y,
                                    euler.z);
        }

        Vector3 Target()
        {
            Vector3 target = userTransform.position + Heading() * offset;
            if (fixedHeight) target.y = _initialPosition.y;
            return target;
        }

        void Update()
        {
            if (!TryBegin()) return;

            transform.rotation = LookAtUser();
            Vector3 newTarget = Target();

            // Only retarget when the wanted spot has moved away from the last one, or the object
            // has been left behind it. Between those the object just finishes its glide.
            Vector3 here = fixedHeight ? Flatten(transform.position) : transform.position;
            Vector3 there = fixedHeight ? Flatten(newTarget) : newTarget;
            if (Vector3.Distance(_currentTarget, newTarget) > deadzone || Vector3.Distance(here, there) > deadzone)
                _currentTarget = newTarget;

            transform.position = SlerpAround(transform.position, _currentTarget, userTransform.position,
                                             Mathf.Clamp01(followSpeed * Time.deltaTime));
        }

        /// <summary>Slerps between two points, but around <paramref name="origin"/> (the user), so the object swings round them instead of cutting through them.</summary>
        static Vector3 SlerpAround(Vector3 from, Vector3 to, Vector3 origin, float t) =>
            Vector3.Slerp(from - origin, to - origin, t) + origin;

        static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
