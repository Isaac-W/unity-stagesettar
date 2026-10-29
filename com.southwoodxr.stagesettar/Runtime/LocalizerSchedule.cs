using System;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>The numbers <see cref="LocalizerSchedule"/> works from. The localizer reads them from its settings every frame.</summary>
    internal struct ScheduleSettings
    {
        public StarLocalizer.Mode mode;
        public float maxInferenceHz, periodSeconds, burstSeconds;
        public float refineDistanceMeters, refineTurnDegrees;
        public float lostAfterSeconds, trackingJumpMeters;
    }

    /// <summary>
    /// Decides when <see cref="StarLocalizer"/> looks at the camera: how fast, when to rest, when a rest ends in a
    /// refinement burst, and how long it has gone without a good fix. It knows nothing about Unity objects, so it can be
    /// tested with plain numbers.
    /// </summary>
    /// <remarks>
    /// Only Periodic and Adaptive ever rest, and only once <c>steady</c> (Periodic: localized; Adaptive: converged).
    /// Time spent resting does not count towards <see cref="TimedOut"/>.
    /// </remarks>
    internal sealed class LocalizerSchedule
    {
        double _nextInfer, _nextBurst, _burstEnd, _lastTick, _sinceFix;
        bool _burst, _wasSteady, _wasActive, _hasTick, _hasPos;
        Vector3 _lastPos, _refPos;
        float _refHeading;

        /// <summary>Seconds spent inferring since the last accepted fix.</summary>
        public double SecondsWithoutFix => _sinceFix;

        /// <summary>True while a refinement burst is running.</summary>
        public bool Bursting => _burst;

        /// <summary>Forgets everything, for a fresh start or after the loop was paused.</summary>
        public void Reset()
        {
            _nextInfer = _nextBurst = _burstEnd = _lastTick = _sinceFix = 0.0;
            _burst = _wasSteady = _wasActive = _hasTick = _hasPos = false;
        }

        public void FixAccepted() => _sinceFix = 0.0;

        public bool TimedOut(in ScheduleSettings s) => _sinceFix > s.lostAfterSeconds;

        /// <summary>
        /// Call every frame with the tracked camera position. True when the camera moved farther than the limit since the
        /// previous call, which no person can do in one frame: the headset's tracking was reset.
        /// </summary>
        public bool TrackJump(Vector3 pos, float jumpMeters)
        {
            bool jump = _hasPos && (pos - _lastPos).magnitude > jumpMeters;
            _lastPos = pos;
            _hasPos = true;
            return jump;
        }

        /// <summary>
        /// Call every frame. Returns whether frames are wanted at all right now; <see cref="Due"/> then applies the rate limit.
        /// <paramref name="pos"/> and <paramref name="heading"/> (degrees) are the tracked camera's, for the distance and turn triggers.
        /// </summary>
        public bool Update(double now, bool steady, Vector3 pos, float heading, in ScheduleSettings s)
        {
            // The time since the last call counts only if it was spent inferring, so resting never leads to Lost.
            if (_hasTick && _wasActive) _sinceFix += now - _lastTick;
            _lastTick = now;
            _hasTick = true;

            bool rests = s.mode == StarLocalizer.Mode.Periodic || s.mode == StarLocalizer.Mode.Adaptive;
            if (!steady || !rests)
            {
                _wasSteady = steady;
                _burst = false;
            }
            else
            {
                if (!_wasSteady) { _wasSteady = true; _burst = false; StartRest(now, pos, heading, s); }
                if (_burst)
                {
                    if (now > _burstEnd) { _burst = false; StartRest(now, pos, heading, s); }
                }
                else if (BurstDue(now, pos, heading, s))
                {
                    _burst = true;
                    _burstEnd = now + s.burstSeconds;
                }
            }

            _wasActive = !steady || !rests || _burst;
            return _wasActive;
        }

        /// <summary>True when enough time has passed since the last <see cref="Inferred"/> for another frame.</summary>
        public bool Due(double now) => now >= _nextInfer;

        public void Inferred(double now, in ScheduleSettings s) => _nextInfer = now + 1.0 / Math.Max(0.1f, s.maxInferenceHz);

        void StartRest(double now, Vector3 pos, float heading, in ScheduleSettings s)
        {
            _nextBurst = now + s.periodSeconds;
            _refPos = pos;
            _refHeading = heading;
        }

        // Adaptive also refines early when the user has gone somewhere new, because that is where drift shows.
        bool BurstDue(double now, Vector3 pos, float heading, in ScheduleSettings s)
        {
            if (now >= _nextBurst) return true;
            if (s.mode != StarLocalizer.Mode.Adaptive) return false;
            if (s.refineDistanceMeters > 0f && (pos - _refPos).magnitude > s.refineDistanceMeters) return true;
            return s.refineTurnDegrees > 0f && Mathf.Abs(Mathf.DeltaAngle(_refHeading, heading)) > s.refineTurnDegrees;
        }
    }

    /// <summary>
    /// How much of the room the accepted fixes of an Adaptive start-up have covered: how many, how far the head turned
    /// across them and how far the camera travelled. Enough of either turn or travel (with enough fixes) means the
    /// placement can be trusted and refinement can slow down.
    /// </summary>
    internal sealed class CoverageTracker
    {
        float _firstHeading, _minRel, _maxRel;
        Vector3 _min, _max;

        public int Count { get; private set; }

        /// <summary>Spread of the headings seen, in degrees (measured from the first, so a turn either way counts).</summary>
        public float TurnDegrees => Count > 0 ? _maxRel - _minRel : 0f;

        /// <summary>Size of the box around the camera positions seen, in metres.</summary>
        public float MoveMeters => Count > 0 ? (_max - _min).magnitude : 0f;

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

        public float FixesProgress(int minFixes) => Ratio(Count, Math.Max(1, minFixes));

        /// <summary>The better of turn and travel compared with what each needs, 0 to 1.</summary>
        public float SpreadProgress(float minTurnDeg, float minMoveMeters) =>
            Mathf.Max(Ratio(TurnDegrees, minTurnDeg), Ratio(MoveMeters, minMoveMeters));

        static float Ratio(float value, float needed) => needed <= 0f ? 1f : Mathf.Clamp01(value / needed);
    }
}
