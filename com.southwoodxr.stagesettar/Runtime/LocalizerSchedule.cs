using System;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>The numbers <see cref="LocalizerSchedule"/> works from. The localizer reads them from its settings every frame.</summary>
    internal struct ScheduleSettings
    {
        public float startupHz;
        public bool earlyEnabled;
        public float earlyIntervalSeconds;
        public bool lateEnabled, lateByTime, lateByMove, lateByTurn;
        public float lateIntervalSeconds, lateMoveMeters, lateTurnDegrees, lateBurstSeconds, lateHz;
    }

    /// <summary>
    /// Decides when <see cref="StarLocalizer"/> looks at the camera, in which phase it is, and whether the device has
    /// tracking. It knows nothing about Unity objects, so it can be tested with plain numbers.
    /// </summary>
    /// <remarks>
    /// Startup and Early infer all the time, at their own rate. Late rests, and a short check starts when the
    /// interval is up or the camera moved or turned far (each trigger can be off); a check ends when it times out or the
    /// localizer calls <see cref="EndCheck"/>. The localizer moves between phases with <see cref="Enter"/>;
    /// <see cref="AfterPlacement"/> says where to go when Early is switched off. Missing fixes never end a phase: only
    /// the device losing tracking does (see <see cref="UpdateTracking"/>).
    /// </remarks>
    internal sealed class LocalizerSchedule
    {
        /// <summary>What <see cref="LocalizerSchedule.UpdateTracking"/> found.</summary>
        public enum TrackingChange
        {
            None,
            /// <summary>Tracking just went away.</summary>
            Lost,
            /// <summary>Tracking is back and the placement can be kept: the headset was only taken off.</summary>
            Resumed,
            /// <summary>Tracking is back after it was lost for real: the old placement cannot be trusted.</summary>
            RestartNeeded,
        }

        double _nextInfer, _nextCheck, _checkEnd;
        double _lastPosTime, _jumpTime = double.NegativeInfinity;
        bool _checking, _checkNow, _needsReference, _hasPos, _trackingLost, _resetNeeded;
        Vector3 _lastPos, _refPos;
        float _refHeading;

        public StarLocalizer.Phase Phase { get; private set; }

        /// <summary>True while the device has no tracking. No frames are wanted then.</summary>
        public bool TrackingLost => _trackingLost;

        /// <summary>True while a Late check is running.</summary>
        public bool Checking => _checking;

        /// <summary>How many Late checks have started so far, so the localizer can tell a new check from the one it was in.</summary>
        public int Checks { get; private set; }

        /// <summary>The phase after the map was placed: Early, or Late when Early is off.</summary>
        public static StarLocalizer.Phase AfterPlacement(in ScheduleSettings s) =>
            s.earlyEnabled ? StarLocalizer.Phase.Early : StarLocalizer.Phase.Late;

        /// <summary>
        /// Forgets the timing and starts in Startup, for a fresh start. The tracking-jump baseline is kept, so a
        /// tracking reset that happened while the loop was paused is still noticed at the next sample.
        /// </summary>
        public void Reset()
        {
            _nextInfer = 0.0;
            _trackingLost = _resetNeeded = false;
            Enter(StarLocalizer.Phase.Startup);
        }

        /// <summary>Carries on in the same phase after a pause, with the Late triggers counted from now.</summary>
        public void Resume() => Enter(Phase);

        public void Enter(StarLocalizer.Phase phase)
        {
            Phase = phase;
            _checking = _checkNow = false;
            _needsReference = true;
        }

        /// <summary>
        /// Call every frame with what the camera source says. Tracking going away is reported once. When it comes back,
        /// the placement is only kept if the headset was simply taken off: tracking that went away while the headset was
        /// on may have been reset, and then the map would be in the wrong place. A reset that goes unnoticed here is
        /// still caught by <see cref="TrackJump"/>. Tracking coming back also forgets the jump baseline, because the
        /// headset may have been carried anywhere meanwhile. A kept placement is checked at once, so a changed space is
        /// caught by a refit: Late starts a check right away and Early takes its next frame without waiting.
        /// </summary>
        public TrackingChange UpdateTracking(bool tracking, bool worn)
        {
            if (!tracking && !_trackingLost)
            {
                _trackingLost = true;
                _resetNeeded = worn;
                return TrackingChange.Lost;
            }
            if (tracking && _trackingLost)
            {
                _trackingLost = false;
                ForgetPosition();
                if (_resetNeeded) { _resetNeeded = false; return TrackingChange.RestartNeeded; }
                Resume();
                _nextInfer = 0.0;
                _checkNow = true;
                return TrackingChange.Resumed;
            }
            return TrackingChange.None;
        }

        /// <summary>Ends the running Late check, and the next one is counted from now.</summary>
        public void EndCheck()
        {
            _checking = false;
            _needsReference = true;
        }

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

        /// <summary>Drops the previous sample, so the next one is not compared with a position from before the camera was carried away.</summary>
        public void ForgetPosition() => _hasPos = false;

        /// <summary>True for a frame captured before the last tracking reset: its pose belongs to the old tracking space.</summary>
        public bool PredatesJump(double captureTime) => captureTime < _jumpTime;

        /// <summary>
        /// Call every frame. Returns whether frames are wanted at all right now; <see cref="ShouldInfer"/> then applies the rate.
        /// <paramref name="pos"/> and <paramref name="heading"/> (degrees) are the tracked camera's, for the Late triggers.
        /// </summary>
        public bool WantsFrames(double now, Vector3 pos, float heading, in ScheduleSettings s)
        {
            if (_trackingLost) return false;
            return Phase != StarLocalizer.Phase.Late || LateWantsFrames(now, pos, heading, s);
        }

        /// <summary>True when enough time has passed since the last <see cref="OnInferred"/> for another frame.</summary>
        public bool ShouldInfer(double now) => now >= _nextInfer;

        /// <summary>Call when a frame was taken for analysis, to start the wait for the next one.</summary>
        public void OnInferred(double now, in ScheduleSettings s) => _nextInfer = now + Interval(s);

        double Interval(in ScheduleSettings s)
        {
            switch (Phase)
            {
                case StarLocalizer.Phase.Startup: return 1.0 / Math.Max(0.1f, s.startupHz);
                case StarLocalizer.Phase.Early: return s.earlyIntervalSeconds;
                default: return 1.0 / Math.Max(0.1f, s.lateHz);
            }
        }

        bool LateWantsFrames(double now, Vector3 pos, float heading, in ScheduleSettings s)
        {
            if (!s.lateEnabled) return false;
            if (_checking && now > _checkEnd) EndCheck();
            if (_needsReference)
            {
                _needsReference = false;
                _nextCheck = now + s.lateIntervalSeconds;
                _refPos = pos;
                _refHeading = heading;
            }
            if (!_checking && (_checkNow || CheckDue(now, pos, heading, s)))
            {
                _checking = true;
                _checkNow = false;
                _checkEnd = now + s.lateBurstSeconds;
                Checks++;
            }
            return _checking;
        }

        // Drift shows when the user has gone somewhere new, so moving or turning far starts a check as well as the clock.
        bool CheckDue(double now, Vector3 pos, float heading, in ScheduleSettings s) =>
            (s.lateByTime && now >= _nextCheck)
            || (s.lateByMove && (pos - _refPos).magnitude > s.lateMoveMeters)
            || (s.lateByTurn && Mathf.Abs(Mathf.DeltaAngle(_refHeading, heading)) > s.lateTurnDegrees);
    }

    /// <summary>
    /// Compares two alignments by what they do to the map: how far the landmark centroid shifts and how far the yaw differs.
    /// Pure, so it can be tested with plain numbers.
    /// </summary>
    internal static class PlacementCheck
    {
        public static void Difference(in Alignment a, in Alignment b, Vector3 centroid, out float meters, out float degrees)
        {
            meters = (a.MapToTracking.MultiplyPoint3x4(centroid) - b.MapToTracking.MultiplyPoint3x4(centroid)).magnitude;
            degrees = Mathf.Abs(Mathf.DeltaAngle(a.yawDeg, b.yawDeg));
        }

        /// <summary>Two fixes agree if they put the map within both limits of each other.</summary>
        public static bool Agree(in Alignment a, in Alignment b, Vector3 centroid, float meters, float degrees)
        {
            Difference(a, b, centroid, out float m, out float deg);
            return m <= meters && deg <= degrees;
        }

        /// <summary>
        /// True when <paramref name="fix"/> puts the map farther from <paramref name="current"/> than the limits plus a margin
        /// for the uncertainty of both. This is the Early gate: a fix beyond it is rejected.
        /// </summary>
        public static bool Beyond(in Alignment current, in Alignment fix, Vector3 centroid, float meters, float degrees)
        {
            Difference(current, fix, centroid, out float m, out float deg);
            return m > meters + 3f * (Known(current.positionSigma) + Known(fix.positionSigma))
                || deg > degrees + 3f * (Known(current.yawSigmaDeg) + Known(fix.yawSigmaDeg));
        }

        /// <summary>
        /// The Late test: true when <paramref name="fix"/> (the middle of fixes that agree with each other) puts the map
        /// farther from <paramref name="current"/> than the limits plus one sigma of its own noise. Anything smaller is not
        /// worth moving the map for.
        /// </summary>
        public static bool Deviates(in Alignment current, in Alignment fix, Vector3 centroid, float meters, float degrees)
        {
            Difference(current, fix, centroid, out float m, out float deg);
            return m > meters + Known(fix.positionSigma) || deg > degrees + Known(fix.yawSigmaDeg);
        }

        // A solve whose covariance could not be inverted reports an infinite sigma. Unknown must not widen the limits
        // to accept anything, so it falls back to the fixed part.
        static float Known(float sigma) => float.IsFinite(sigma) ? sigma : 0f;
    }
}
