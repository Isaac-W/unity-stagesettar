using UnityEngine;

namespace StageSettAR
{
    /// <summary>How <see cref="AlignmentGlide"/> moves the shown map towards its target.</summary>
    internal struct GlideSettings
    {
        public bool smooth;
        public float seconds;
        /// <summary>A target that moves the pivot point farther than this (metres) is applied at once. 0 turns it off.</summary>
        public float snapMeters;
        /// <summary>The same for the yaw difference, in degrees. 0 turns it off.</summary>
        public float snapDegrees;
    }

    /// <summary>The easing that carries the shown map to its target. Pure, so it can be tested with plain numbers.</summary>
    internal static class AlignmentGlide
    {
        /// <summary>
        /// The placement to show after <paramref name="dt"/> seconds. It is an exponential approach, independent of the
        /// frame rate, and a new target only changes where the map is heading, so a target that arrives mid-glide causes
        /// no restart. About 95 % of the way after <see cref="GlideSettings.seconds"/>. A target that is too far away
        /// to be a refinement, or any target with smoothing off, is applied at once. The size of a move is measured at
        /// <paramref name="pivot"/> (a point in tracking space, normally where the user is): how far the map content
        /// there would shift. The map origin would be misleading, since far from it a small turn shifts the origin a lot.
        /// </summary>
        public static Alignment Step(in Alignment shown, in Alignment target, Vector3 pivot, float dt, in GlideSettings g)
        {
            if (!g.smooth || g.seconds <= 0f || TooFar(shown, target, pivot, g)) return target;

            float k = 1f - Mathf.Exp(-3f * Mathf.Max(0f, dt) / g.seconds);
            Alignment next = shown;
            next.yawDeg += Mathf.DeltaAngle(shown.yawDeg, target.yawDeg) * k;
            next.translation = Vector3.Lerp(shown.translation, target.translation, k);
            return next;
        }

        static bool TooFar(in Alignment shown, in Alignment target, Vector3 pivot, in GlideSettings g) =>
            (g.snapMeters > 0f && PivotShift(shown, target, pivot) > g.snapMeters)
            || (g.snapDegrees > 0f && Mathf.Abs(Mathf.DeltaAngle(shown.yawDeg, target.yawDeg)) > g.snapDegrees);

        // The map point that is at the pivot now, and how far the target moves it.
        static float PivotShift(in Alignment shown, in Alignment target, Vector3 pivot)
        {
            Vector3 mapPoint = shown.MapToTracking.inverse.MultiplyPoint3x4(pivot);
            return (target.MapToTracking.MultiplyPoint3x4(mapPoint) - pivot).magnitude;
        }
    }
}
