using UnityEngine;

namespace StageSettAR
{
    /// <summary>How the gaze is tested against the target.</summary>
    public enum GazeShape
    {
        /// <summary>A thin line. The user has to look straight at the collider.</summary>
        Ray = 0,

        /// <summary>A ball swept along that line. Looking near the collider counts.</summary>
        Sphere = 1,
    }

    /// <summary>
    /// The shared "is the user looking at this?" test, used by OnGazeAction, PanelDynamicTilt
    /// and PanelLookAt so all three agree about what counts as a look.
    /// </summary>
    /// <remarks>
    /// Not a component. It exists so the fiddly parts live in one place: hitting a collider that may
    /// be a trigger, optionally widening the aim into a swept sphere, and letting solid objects
    /// block the gaze while triggers do not.
    /// </remarks>
    public static class GazeProbe
    {
        // Reused so the casts do not allocate every frame. Safe to share: each call finishes
        // with them before anything else can run.
        static readonly RaycastHit[] s_SphereHits = new RaycastHit[64];
        static readonly RaycastHit[] s_Blockers = new RaycastHit[32];

        // Ignore Raycast is where the rig puts the user's own head, and where students put things
        // that should be see-through. It never blocks a gaze, whatever the blocked-by mask says.
        const int IgnoreRaycastBit = 1 << 2;

        /// <summary>
        /// Whether the user is looking at the target, and where the gaze landed on it.
        /// </summary>
        /// <param name="overrideCamera">Look through this camera instead of the shared gaze source. Null for the usual case.</param>
        public static bool Look(Camera overrideCamera, Collider target, GazeShape shape, float radius,
                                float maxDistance, bool blockedBySolidObjects, LayerMask blockedBy,
                                out RaycastHit hit)
        {
            hit = default;
            if (target == null || !target.enabled || !target.gameObject.activeInHierarchy) return false;
            if (!ToolkitRig.TryGetGazeRay(overrideCamera, out var ray)) return false;

            if (!Hits(ray, target, shape, radius, maxDistance, out hit)) return false;
            return !Blocked(ray.origin, target, hit, blockedBySolidObjects, blockedBy);
        }

        /// <summary>Whether the gaze reaches the target, by whichever shape is selected.</summary>
        static bool Hits(Ray ray, Collider target, GazeShape shape, float radius,
                         float maxDistance, out RaycastHit hit)
        {
            if (shape == GazeShape.Ray || radius <= 0f)
            {
                // Tests this collider alone, so no layer mask or trigger setting can veto it.
                return target.Raycast(ray, out hit, maxDistance);
            }

            hit = default;

            // There is no Collider.SphereCast, so this has to go through Physics and then pick
            // the target back out. Restricting the mask to the target's own layer keeps the
            // buffer from filling with scenery before the target appears in it, and Collide is
            // explicit because the target is very often a trigger.
            int mask = 1 << target.gameObject.layer;
            int count = Physics.SphereCastNonAlloc(ray, radius, s_SphereHits, maxDistance, mask,
                                                   QueryTriggerInteraction.Collide);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (s_SphereHits[i].collider != target) continue;
                if (found && s_SphereHits[i].distance >= hit.distance) continue;
                hit = s_SphereHits[i];
                found = true;
            }

            // A ball that already overlaps the collider at its start (the user's head inside a big
            // target) reports distance 0 and a zero point. It is certainly "looking near it", so
            // report the nearest point on the collider rather than a hit at the world origin.
            if (found && hit.distance <= 0f)
            {
                hit.point = target.ClosestPoint(ray.origin);
                hit.distance = Vector3.Distance(ray.origin, hit.point);
            }
            return found;
        }

        /// <summary>Whether something solid sits between the user and the target.</summary>
        static bool Blocked(Vector3 origin, Collider target, RaycastHit hit,
                            bool blockedBySolidObjects, LayerMask blockedBy)
        {
            if (!blockedBySolidObjects) return false;

            // Aim the probe at where the gaze actually landed, not straight ahead: a sphere cast
            // can catch the target off to one side, and probing down the centre line would then
            // be asking about a different part of the room.
            Vector3 toHit = hit.point - origin;
            if (hit.distance <= 0f || toHit.sqrMagnitude <= 1e-6f) return false;

            // Stop just short of the target, so the target's own front face is never the thing
            // found to be blocking it.
            float reach = toHit.magnitude - 0.001f;
            if (reach <= 0f) return false;

            var ray = new Ray(origin, toHit.normalized);

            // Ignore, not UseGlobal: triggers are not solid, and leaving this to the project
            // setting would make a wall and another gaze target behave the same way.
            int count = Physics.RaycastNonAlloc(ray, s_Blockers, reach, blockedBy.value & ~IgnoreRaycastBit,
                                                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Transform other = s_Blockers[i].transform;
                // A solid collider belonging to the target itself is part of the thing being
                // looked at, not something in front of it.
                if (other == target.transform || other.IsChildOf(target.transform)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The collider's extent in its own local space, centre and size.
        /// </summary>
        /// <remarks>
        /// Deliberately not <c>Collider.bounds</c>, which is an axis-aligned box in world space:
        /// for a rotated panel that box is bigger than the panel and aligned to the wrong axes,
        /// so measuring across the face with it gives the wrong answer. These are asked of the
        /// collider's own shape instead.
        /// </remarks>
        public static Bounds LocalBounds(Collider target)
        {
            switch (target)
            {
                case BoxCollider box:
                    return new Bounds(box.center, box.size);
                case SphereCollider sphere:
                    return new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                case CapsuleCollider capsule:
                {
                    float d = capsule.radius * 2f;
                    var size = new Vector3(d, d, d);
                    size[Mathf.Clamp(capsule.direction, 0, 2)] = Mathf.Max(capsule.height, d);
                    return new Bounds(capsule.center, size);
                }
                case MeshCollider mesh when mesh.sharedMesh != null:
                    return mesh.sharedMesh.bounds;
                default:
                    // Nothing better available: fall back to the world box, undoing the scale so
                    // it is at least in the right units.
                    Vector3 world = target.bounds.size;
                    Vector3 scale = target.transform.lossyScale;
                    return new Bounds(Vector3.zero, new Vector3(
                        scale.x != 0f ? world.x / Mathf.Abs(scale.x) : world.x,
                        scale.y != 0f ? world.y / Mathf.Abs(scale.y) : world.y,
                        scale.z != 0f ? world.z / Mathf.Abs(scale.z) : world.z));
            }
        }

        /// <summary>
        /// Which local axis the panel's face points along: the thinnest one.
        /// </summary>
        /// <remarks>
        /// A Unity Plane faces local +Y, a Quad or a Canvas faces local +Z, and an imported
        /// panel faces whatever the artist modelled. Measuring instead of assuming means the
        /// panel scripts work on all of them with no setting to get wrong.
        /// </remarks>
        public static int ThinnestAxis(Bounds local)
        {
            Vector3 size = local.size;
            if (size.x <= size.y && size.x <= size.z) return 0;
            return size.y <= size.z ? 1 : 2;
        }
    }
}
