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
    /// A steady schedule rests, starts a burst when the period is up (or, in Adaptive, the camera moved or turned far),
    /// and goes back to resting when the burst ends; losing steadiness ends either at once and returns to full rate.
    /// Time spent resting does not count towards <see cref="TimedOut"/>.
    /// </remarks>
    internal sealed class LocalizerSchedule
    {
        double _nextInfer, _nextBurst, _burstEnd, _lastTick, _sinceFix;
        double _lastPosTime, _jumpTime = double.NegativeInfinity;
        bool _burst, _wasSteady, _wasActive, _hasTick, _hasPos;
        Vector3 _lastPos, _refPos;
        float _refHeading;

        /// <summary>Seconds spent inferring since the last accepted fix.</summary>
        public double SecondsWithoutFix => _sinceFix;

        /// <summary>True while a refinement burst is running.</summary>
        public bool Bursting => _burst;

        /// <summary>
        /// Forgets the timing, for a fresh start or after the loop was paused. The tracking-jump baseline is kept, so a
        /// tracking reset that happened while the loop was paused is still noticed at the next sample.
        /// </summary>
        public void Reset()
        {
            _nextInfer = _nextBurst = _burstEnd = _lastTick = _sinceFix = 0.0;
            _burst = _wasSteady = _wasActive = _hasTick = false;
        }

        public void FixAccepted() => _sinceFix = 0.0;

        public bool TimedOut(in ScheduleSettings s) => _sinceFix > s.lostAfterSeconds;

        /// <summary>
        /// Call with every sample of the tracked camera position. True when the camera moved farther than the limit since the
        /// previous sample, which no person can do in one frame: the headset's tracking was reset. A previous sample older
        /// than <paramref name="maxGapSeconds"/> is not compared, because a walk in between is not a reset.
        /// </summary>
        public bool TrackJump(Vector3 pos, float jumpMeters, double time, double maxGapSeconds = double.PositiveInfinity)
        {
            bool jump = _hasPos && time - _lastPosTime <= maxGapSeconds && (pos - _lastPos).magnitude > jumpMeters;
            // The reset happened somewhere between the two samples. Only frames captured before the earlier one are
            // certainly from the old tracking; a frame captured in between may already be from the new one.
            if (jump) _jumpTime = _lastPosTime;
            _lastPos = pos;
            _lastPosTime = time;
            _hasPos = true;
            return jump;
        }

        /// <summary>True for a frame captured before the last tracking reset: its pose belongs to the old tracking space.</summary>
        public bool PredatesJump(double captureTime) => captureTime < _jumpTime;

        /// <summary>
        /// Call every frame. Returns whether frames are wanted at all right now; <see cref="ShouldInfer"/> then applies the rate limit.
        /// <paramref name="pos"/> and <paramref name="heading"/> (degrees) are the tracked camera's, for the distance and turn triggers.
        /// </summary>
        public bool Tick(double now, bool steady, Vector3 pos, float heading, in ScheduleSettings s)
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

        /// <summary>True when enough time has passed since the last <see cref="OnInferred"/> for another frame.</summary>
        public bool ShouldInfer(double now) => now >= _nextInfer;

        /// <summary>Call when a frame was taken for analysis, to start the wait for the next one.</summary>
        public void OnInferred(double now, in ScheduleSettings s) => _nextInfer = now + 1.0 / Math.Max(0.1f, s.maxInferenceHz);

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
}
