#pragma warning disable CS0649 // serialized fields are filled by Unity
using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// "When this points roughly along that, do Y." Compares one of this object's axes with an axis
    /// of another object (or a world axis) and fires events as the angle between them closes and
    /// opens. Good for "the lid is open", "the user's palm faces up", "the head is tilted back".
    /// </summary>
    /// <remarks>
    /// The two thresholds are a hysteresis band: On fires at or below <see cref="onAngle"/>, Off only
    /// at or above <see cref="offAngle"/>, so a value hovering at the edge does not flicker.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Do On Angle")]
    public sealed class DoOnAngle : MonoBehaviour
    {
        public enum Axis { X, Y, Z, NegX, NegY, NegZ }

        [Header("Source")]
        [Tooltip("The object whose axis is measured. This object when empty.")]
        [SerializeField] Transform sourceTransform;
        [SerializeField] Axis sourceAxis = Axis.Y;

        [Header("Target")]
        [Tooltip("The object to compare against. If left empty, the world axis is used instead.")]
        [InspectorName("Target (Optional)")]
        [SerializeField] Transform targetTransform;
        [SerializeField] Axis targetAxis = Axis.Y;

        [Header("Thresholds")]
        [Tooltip("Angle (degrees) at or below which the on event fires.")]
        [SerializeField, Range(0f, 180f)] float onAngle = 45f;
        [Tooltip("Angle (degrees) at or above which the off event fires. Keep it larger than On Angle.")]
        [SerializeField, Range(0f, 180f)] float offAngle = 60f;

        [Header("Events")]
        public UnityEvent onAngleEntered = new UnityEvent();
        public UnityEvent onAngleExited = new UnityEvent();

        [Header("Debug")]
        [SerializeField] bool showGizmos = true;
        [SerializeField, Min(0.05f)] float gizmoLength = 0.3f;

        bool _isOn;

        /// <summary>Whether the angle is currently within the On threshold.</summary>
        public bool IsOn => _isOn;

        Transform Source => sourceTransform != null ? sourceTransform : transform;

        void OnValidate()
        {
            if (offAngle < onAngle) offAngle = onAngle;
        }

        void Update()
        {
            float angle = Vector3.Angle(Direction(Source, sourceAxis), Direction(targetTransform, targetAxis));

            if (!_isOn && angle <= onAngle)
            {
                _isOn = true;
                onAngleEntered?.Invoke();
            }
            else if (_isOn && angle >= offAngle)
            {
                _isOn = false;
                onAngleExited?.Invoke();
            }
        }

        static Vector3 Direction(Transform source, Axis axis)
        {
            Vector3 local = axis switch
            {
                Axis.X => Vector3.right,
                Axis.Y => Vector3.up,
                Axis.Z => Vector3.forward,
                Axis.NegX => Vector3.left,
                Axis.NegY => Vector3.down,
                Axis.NegZ => Vector3.back,
                _ => Vector3.up,
            };
            return source != null ? source.TransformDirection(local) : local;
        }

        void OnDrawGizmosSelected()
        {
            if (!showGizmos) return;

            Vector3 origin = Source.position;
            Vector3 sourceDir = Direction(Source, sourceAxis);
            Vector3 targetDir = Direction(targetTransform, targetAxis);

            Gizmos.color = _isOn ? Color.green : Color.yellow;
            Gizmos.DrawRay(origin, sourceDir * gizmoLength);
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(origin, targetDir * gizmoLength);

            DrawCone(origin, targetDir, offAngle, gizmoLength, Color.red);
            DrawCone(origin, targetDir, onAngle, gizmoLength, Color.green);
        }

        // A swing cone around the axis, like a physics joint's angular limit.
        static void DrawCone(Vector3 origin, Vector3 axis, float angleDeg, float length, Color color)
        {
            const int spokes = 12;
            float capRadius = length * Mathf.Tan(Mathf.Min(angleDeg, 89f) * Mathf.Deg2Rad);
            Vector3 capCenter = origin + axis * length;

            Vector3 perp = Vector3.Cross(axis, Vector3.up);
            if (perp.sqrMagnitude < 1e-4f) perp = Vector3.Cross(axis, Vector3.right);
            perp.Normalize();
            Vector3 perp2 = Vector3.Cross(axis, perp).normalized;

            Gizmos.color = color;
            Vector3 previous = capCenter + perp * capRadius;
            for (int i = 1; i <= spokes; i++)
            {
                float t = i * Mathf.PI * 2f / spokes;
                Vector3 rim = capCenter + (perp * Mathf.Cos(t) + perp2 * Mathf.Sin(t)) * capRadius;
                Gizmos.DrawLine(previous, rim);
                Gizmos.DrawLine(origin, rim);
                previous = rim;
            }
        }
    }
}
