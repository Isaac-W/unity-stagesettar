using System;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// How much of the room the accepted fixes of the Early phase have covered: how many, how far the head turned
    /// across them and how far the camera travelled. Enough of either turn or travel (with enough fixes) means the
    /// placement can be trusted and the Late phase can begin.
    /// </summary>
    internal sealed class CoverageTracker
    {
        float _firstHeading, _minRel, _maxRel;
        Vector3 _min, _max;

        public int Count { get; private set; }

        /// <summary>Spread of the headings seen, in degrees (measured from the first, so a turn either way counts).</summary>
        public float TurnDegrees => Count > 0 ? _maxRel - _minRel : 0f;

        /// <summary>Size of the box around the camera positions seen on the floor plane (x and z), in metres. Height is
        /// left out because crouching or looking around changes it without covering any more of the room.</summary>
        public float MoveMeters => Count > 0 ? new Vector2(_max.x - _min.x, _max.z - _min.z).magnitude : 0f;

        public void Reset() => Count = 0;

        public void Add(Vector3 pos, float headingDeg)
        {
            if (Count == 0)
            {
                _firstHeading = headingDeg;
                _minRel = _maxRel = 0f;
                _min = _max = pos;
            }
            else
            {
                float rel = Mathf.DeltaAngle(_firstHeading, headingDeg);
                _minRel = Mathf.Min(_minRel, rel);
                _maxRel = Mathf.Max(_maxRel, rel);
                _min = Vector3.Min(_min, pos);
                _max = Vector3.Max(_max, pos);
            }
            Count++;
        }

        public bool IsComplete(int minFixes, float minTurnDeg, float minMoveMeters) =>
            Count >= Math.Max(1, minFixes) && (TurnDegrees >= minTurnDeg || MoveMeters >= minMoveMeters);
    }
}
