#pragma warning disable CS0649 // serialized fields are filled by Unity
using System.Collections.Generic;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Moves a target around a looping path, losing speed on the climbs and gaining it on the
    /// descents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways to use it, both fine. Put it on the moving object and leave <see cref="_target"/>
    /// empty, and the waypoints are read in the <b>parent's</b> space. Or put it on a separate
    /// "path" object and assign what you want moved, and they are read in <b>that</b> object's
    /// space. Either way the route lives in a frame the follower does not move, so moving or
    /// rotating that frame carries the whole route -- which is what you want when the route
    /// belongs to a room that itself gets placed by localization.
    /// </para>
    /// <para>
    /// The route is a closed Catmull-Rom spline rather than the waypoints joined by straight
    /// lines. A polyline reads as machinery: it holds a heading, then changes it instantly at a
    /// corner. The spline passes through every point you place while arriving and leaving with a
    /// continuous heading.
    /// </para>
    /// <para>
    /// Speed comes from <b>height, not from the clock</b>. It is the rollercoaster rule --
    /// trade height for speed, v = sqrt(v_top^2 + 2g*drop) -- which is worth the square root for
    /// two reasons. It slows into every climb and accelerates out of every fall without any of
    /// that being authored per-segment. And because speed depends only on where the follower is,
    /// never on how it got there, a lap always looks like the last one and no error accumulates
    /// over a long session.
    /// </para>
    /// <para>
    /// Nothing here renders. The path is drawn with gizmos, so it shows in the Scene view and in
    /// no build.
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/Path Follower")]
    public sealed class PathFollower : MonoBehaviour
    {
        [Header("What moves")]
        [Tooltip("The object to move along the path. Leave it empty to move THIS object, in " +
                 "which case the waypoints are read in the parent's space so the route does not " +
                 "travel along with the follower.")]
        [SerializeField] Transform _target;

        [Tooltip("Turn the target to face the way it is going.")]
        [SerializeField] bool _faceForward = true;

        [Tooltip("How quickly it swings round to face the new heading, in degrees per second. " +
                 "0 snaps straight to it. Too low and the heading lags behind the travel " +
                 "direction, which reads as the thing sliding sideways like a drifting car.")]
        [SerializeField, Range(0f, 1080f)] float _turnDegreesPerSecond;

        [Header("Route")]
        [Tooltip("Waypoints. Drag them in the Scene view; the route always closes back to the " +
                 "first. They are read in the parent's space when Target is empty, and in this " +
                 "object's space when it is not.")]
        [SerializeField]
        List<Vector3> _points = new()
        {
            new Vector3(-1f, 0f, -1f),
            new Vector3(1f, 0f, -1f),
            new Vector3(1f, 0.6f, 1f),
            new Vector3(-1f, 0.6f, 1f),
        };

        [Tooltip("Spline samples per segment. More is a smoother route and a slightly longer " +
                 "rebuild; it costs nothing per frame.")]
        [SerializeField, Range(4, 64)] int _samplesPerSegment = 16;

        [Header("Speed")]
        [Tooltip("Speed at the highest point of the route, in units per second. Everything else " +
                 "is faster than this, so it sets the pace of the whole lap.")]
        [SerializeField] float _speedAtTop = 0.35f;

        [Tooltip("How strongly height trades for speed. This is gravity, but it is a dial rather " +
                 "than a constant -- 9.81 is right for a falling rock and far too eager for " +
                 "anything that is supposed to look like it is flying.")]
        [SerializeField] float _gravity = 2f;

        [Tooltip("Never go slower than this, whatever the height says. Without it a route with a " +
                 "tall climb can crawl almost to a stop near the top.")]
        [SerializeField] float _minSpeed = 0.05f;

        [Tooltip("Never go faster than this. 0 means no limit.")]
        [SerializeField] float _maxSpeed = 3f;

        [Header("Start")]
        [Tooltip("Where on the lap to begin, 0 to 1. Give each follower on a shared path a " +
                 "different value and they spread out around it.")]
        [SerializeField, Range(0f, 1f)] float _startAt;

        [Tooltip("Randomise the start instead, which is easier when spawning several.")]
        [SerializeField] bool _randomStart;

        [Tooltip("Start moving as soon as the scene starts. Turn off to start it with Play(), for instance from a trigger zone.")]
        [SerializeField] bool _playOnStart = true;

        [Header("Gizmos")]
        [SerializeField] bool _drawPath = true;
        [SerializeField] Color _pathColour = new(0.4f, 0.85f, 1f, 0.9f);

        // The route flattened to a polyline, with the distance along it at each sample. Every
        // query is a lookup here rather than a spline evaluation, which is what makes moving by
        // *distance* cheap -- and moving by distance is the whole point, since a spline's
        // parameter is not proportional to length.
        readonly List<Vector3> _samples = new();
        readonly List<float> _distances = new();
        float _length;
        float _topHeight;
        Vector3 _upInLocal = Vector3.up;

        float _travelled;
        int _builtFrom = -1;
        bool _moving;

        /// <summary>Whether the follower is moving.</summary>
        public bool IsMoving => _moving;

        /// <summary>Start or resume moving. For UnityEvents.</summary>
        public void Play() => _moving = true;

        /// <summary>Stop where it is. Play() carries on from there.</summary>
        public void Pause() => _moving = false;

        /// <summary>Move something else along the route, from code.</summary>
        public void SetTarget(Transform target) => _target = target;

        /// <summary>Total length of the closed route, in local units.</summary>
        public float Length => _length;

        /// <summary>Waypoints, in <see cref="Frame"/>'s space.</summary>
        public List<Vector3> Points => _points;

        /// <summary>The transform actually moved.</summary>
        public Transform Mover => _target != null ? _target : transform;

        /// <summary>
        /// The space the waypoints live in.
        /// </summary>
        /// <remarks>
        /// Whatever moves cannot also define the route, or the route travels with it and the
        /// follower chases its own tail without ever appearing to move. So when this component
        /// moves its own object the waypoints are read in the parent's space instead, and with
        /// no parent they are world space.
        /// </remarks>
        public Transform Frame => Mover == transform ? transform.parent : transform;

        Vector3 ToWorld(Vector3 point)
        {
            Transform frame = Frame;
            return frame != null ? frame.TransformPoint(point) : point;
        }

        Vector3 UpInPathSpace()
        {
            Transform frame = Frame;
            return frame != null ? frame.InverseTransformDirection(Vector3.up) : Vector3.up;
        }

        void OnEnable()
        {
            Rebuild();
            _travelled = (_randomStart ? Random.value : _startAt) * _length;
            _moving = _playOnStart;

            // Silence here is the worst outcome: nothing moves and there is no clue why.
            if (_points == null || _points.Count < 3)
            {
                Debug.LogWarning(
                    $"[PathFollower] {name} has {_points?.Count ?? 0} waypoints; a closed route " +
                    "needs at least 3. Nothing will move.", this);
            }
            else if (_length <= 0f)
            {
                Debug.LogWarning(
                    $"[PathFollower] {name} has a zero-length route -- the waypoints are all in " +
                    "the same place. Nothing will move.", this);
            }
        }

        void Update()
        {
            if (!_moving || _samples.Count < 2) return;
            Transform mover = Mover;

            // The route is stored in path space but "up" is a world direction, so it has to be
            // brought into that space to measure height against -- and it changes whenever the
            // path is rotated.
            Vector3 up = UpInPathSpace();
            if ((up - _upInLocal).sqrMagnitude > 1e-8f)
            {
                _upInLocal = up;
                MeasureTop();
            }

            Vector3 local = PointAt(_travelled);
            float speed = SpeedAt(local);
            _travelled = Repeat(_travelled + speed * Time.deltaTime, _length);

            Vector3 next = PointAt(_travelled);
            mover.position = ToWorld(next);

            if (_faceForward)
            {
                // Central difference over a span tied to the route's own length. A fixed small
                // lookahead is worse than useless: below the sample spacing it never leaves the
                // current straight segment, so the heading is constant along each one and jumps
                // at every join -- which a turn-rate limit then smears into a sideways slide.
                // At least one sample spacing, so the two probes always straddle a join rather
                // than landing inside one straight segment.
                float spacing = _length / Mathf.Max(1, _samples.Count - 1);
                float h = Mathf.Max(_length * 0.01f, spacing, 1e-4f);
                Vector3 heading = ToWorld(PointAt(_travelled + h)) - ToWorld(PointAt(_travelled - h));
                if (heading.sqrMagnitude > 1e-8f)
                {
                    Quaternion want = Quaternion.LookRotation(heading.normalized, Vector3.up);
                    mover.rotation = _turnDegreesPerSecond <= 0f
                        ? want
                        : Quaternion.RotateTowards(mover.rotation, want,
                                                   _turnDegreesPerSecond * Time.deltaTime);
                }
            }
        }

        /// <summary>
        /// Speed at a point on the route, from how far below the summit it sits.
        /// </summary>
        /// <remarks>
        /// v = sqrt(v_top^2 + 2g*drop). The follower is treated as if it had just enough energy
        /// to crest the highest point at <see cref="_speedAtTop"/> and to have been trading
        /// height for speed ever since.
        /// </remarks>
        float SpeedAt(Vector3 localPoint)
        {
            float drop = _topHeight - Vector3.Dot(localPoint, _upInLocal);
            float squared = _speedAtTop * _speedAtTop + 2f * _gravity * Mathf.Max(0f, drop);
            float speed = Mathf.Sqrt(Mathf.Max(0f, squared));
            speed = Mathf.Max(speed, _minSpeed);
            return _maxSpeed > 0f ? Mathf.Min(speed, _maxSpeed) : speed;
        }

        /// <summary>Position at a distance along the route, in local space.</summary>
        public Vector3 PointAt(float distance)
        {
            if (_samples.Count == 0) return Vector3.zero;
            if (_samples.Count == 1 || _length <= 0f) return _samples[0];

            distance = Repeat(distance, _length);

            // Samples are evenly spaced in spline parameter, not in length, so the distance
            // table has to be searched rather than indexed.
            int low = 0;
            int high = _distances.Count - 1;
            while (low < high)
            {
                int mid = (low + high) >> 1;
                if (_distances[mid] < distance) low = mid + 1;
                else high = mid;
            }

            int b = Mathf.Clamp(low, 1, _samples.Count - 1);
            int a = b - 1;
            float span = _distances[b] - _distances[a];
            float t = span > 1e-6f ? (distance - _distances[a]) / span : 0f;
            return Vector3.Lerp(_samples[a], _samples[b], t);
        }

        /// <summary>Re-sample the spline. Call after changing <see cref="Points"/>.</summary>
        public void Rebuild()
        {
            _samples.Clear();
            _distances.Clear();
            _length = 0f;
            if (_points == null || _points.Count < 2) return;

            int count = _points.Count;
            int per = Mathf.Max(1, _samplesPerSegment);

            for (int i = 0; i < count; i++)
            {
                // Closed loop, so every neighbour wraps.
                Vector3 p0 = _points[(i - 1 + count) % count];
                Vector3 p1 = _points[i];
                Vector3 p2 = _points[(i + 1) % count];
                Vector3 p3 = _points[(i + 2) % count];

                for (int s = 0; s < per; s++)
                {
                    _samples.Add(CatmullRom(p0, p1, p2, p3, (float)s / per));
                }
            }
            _samples.Add(_samples[0]);          // close it

            _distances.Add(0f);
            for (int i = 1; i < _samples.Count; i++)
            {
                _length += Vector3.Distance(_samples[i - 1], _samples[i]);
                _distances.Add(_length);
            }

            _upInLocal = UpInPathSpace();
            MeasureTop();
            _builtFrom = count;
        }

        void MeasureTop()
        {
            _topHeight = float.NegativeInfinity;
            foreach (var sample in _samples)
            {
                float h = Vector3.Dot(sample, _upInLocal);
                if (h > _topHeight) _topHeight = h;
            }
        }

        /// <summary>
        /// Catmull-Rom through <paramref name="p1"/> and <paramref name="p2"/>.
        /// </summary>
        /// <remarks>
        /// Chosen over Bezier because it interpolates: the curve passes through the waypoints
        /// you place, so dragging a handle puts the route where the handle is rather than
        /// somewhere near it.
        /// </remarks>
        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2f * p1)
                           + (-p0 + p2) * t
                           + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                           + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        static float Repeat(float value, float length)
        {
            if (length <= 0f) return 0f;
            value %= length;
            return value < 0f ? value + length : value;
        }

        void OnValidate()
        {
            // Cheap enough to redo on any inspector edit, and it keeps the gizmo honest while
            // numbers are being typed.
            if (isActiveAndEnabled || !Application.isPlaying) Rebuild();
        }

        void OnDrawGizmos()
        {
            if (!_drawPath) return;
            if (_samples.Count < 2 || _builtFrom != (_points?.Count ?? 0)) Rebuild();
            if (_samples.Count < 2) return;

            Transform frame = Frame;
            Gizmos.matrix = frame != null ? frame.localToWorldMatrix : Matrix4x4.identity;
            Gizmos.color = _pathColour;
            for (int i = 1; i < _samples.Count; i++)
            {
                Gizmos.DrawLine(_samples[i - 1], _samples[i]);
            }

            // Speed, shown rather than described: a tick every so often, longer where the
            // follower is moving faster.
            Gizmos.color = new Color(_pathColour.r, _pathColour.g, _pathColour.b, 0.35f);
            const int Ticks = 48;
            for (int i = 0; i < Ticks; i++)
            {
                float d = _length * i / Ticks;
                Vector3 at = PointAt(d);
                Vector3 ahead = PointAt(d + 0.01f);
                Vector3 along = (ahead - at).normalized;
                Vector3 side = Vector3.Cross(along, _upInLocal).normalized;
                float scale = SpeedAt(at) * 0.05f;
                Gizmos.DrawLine(at - side * scale, at + side * scale);
            }

            Gizmos.color = _pathColour;
            foreach (var p in _points) Gizmos.DrawSphere(p, 0.02f);

            // Where the follower believes it is. If this sits on the line but the moved object
            // does not, the route is right and something else is offsetting the object -- a
            // child mesh away from its origin, or another script writing the same transform.
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(PointAt(_travelled), 0.05f);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
