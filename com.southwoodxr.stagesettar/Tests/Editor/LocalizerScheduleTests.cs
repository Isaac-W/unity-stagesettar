using NUnit.Framework;
using UnityEngine;
using Phase = StageSettAR.StarLocalizer.Phase;

namespace StageSettAR.Tests
{
    /// <summary>The localizer's phase scheduling and placement decisions, with plain numbers instead of a running localizer.</summary>
    public class LocalizerScheduleTests
    {
        static ScheduleSettings Settings() => new ScheduleSettings
        {
            startupHz = 4f, earlyEnabled = true, earlyIntervalSeconds = 0.5f,
            lateEnabled = true, lateByTime = true, lateByMove = true, lateByTurn = true,
            lateIntervalSeconds = 30f, lateMoveMeters = 2f, lateTurnDegrees = 60f, lateBurstSeconds = 3f, lateHz = 4f,
        };

        static LocalizerSchedule InLate(in ScheduleSettings s)
        {
            var sch = new LocalizerSchedule();
            sch.Enter(Phase.Late);
            sch.WantsFrames(0, Vector3.zero, 0f, s);   // the triggers are counted from here
            return sch;
        }

        /// <summary>Ticks every 0.1 s from <paramref name="from"/> to <paramref name="to"/> and returns the last answer.</summary>
        static bool Run(LocalizerSchedule sch, double from, double to, in ScheduleSettings s, Vector3 pos = default, float heading = 0f)
        {
            bool wanted = false;
            for (int i = 0; from + i * 0.1 <= to + 1e-9; i++) wanted = sch.WantsFrames(from + i * 0.1, pos, heading, s);
            return wanted;
        }

        // ---- rates ----

        [Test]
        public void StartupAndEarlyAlwaysWantFramesAtTheirOwnRate()
        {
            var s = Settings();
            var sch = new LocalizerSchedule();
            Assert.AreEqual(Phase.Startup, sch.Phase);
            Assert.IsTrue(Run(sch, 0, 120, s));
            sch.OnInferred(0, s);
            Assert.IsFalse(sch.ShouldInfer(0.2));
            Assert.IsTrue(sch.ShouldInfer(0.25), "4 Hz");

            sch.Enter(Phase.Early);
            Assert.IsTrue(Run(sch, 121, 240, s), "Early never rests");
            sch.OnInferred(10, s);
            Assert.IsFalse(sch.ShouldInfer(10.4));
            Assert.IsTrue(sch.ShouldInfer(10.5), "every 0.5 s");
        }

        // ---- phase switches ----

        [Test]
        public void PlacementLeadsToEarlyOrToLateWhenEarlyIsOff()
        {
            var s = Settings();
            Assert.AreEqual(Phase.Early, LocalizerSchedule.AfterPlacement(s));
            s.earlyEnabled = false;
            Assert.AreEqual(Phase.Late, LocalizerSchedule.AfterPlacement(s));
        }

        [Test]
        public void ResetStartsInStartup()
        {
            var s = Settings();
            var sch = InLate(s);
            sch.Reset();
            Assert.AreEqual(Phase.Startup, sch.Phase);
        }

        [Test]
        public void LateOffNeverWantsFrames()
        {
            var s = Settings();
            s.lateEnabled = false;
            var sch = InLate(s);
            Assert.IsFalse(Run(sch, 0, 200, s, new Vector3(50f, 0f, 0f), 170f));
        }

        // ---- late checks ----

        [Test]
        public void LateRestsThenChecksOnTheInterval()
        {
            var s = Settings();
            var sch = InLate(s);
            Assert.IsFalse(Run(sch, 0.1, 29.9, s), "no inference between checks");
            Assert.IsTrue(sch.WantsFrames(30, Vector3.zero, 0f, s), "30 s after Late began");
            Assert.IsTrue(sch.Checking);
            Assert.IsTrue(Run(sch, 30.1, 32.9, s));
            Assert.IsFalse(sch.WantsFrames(33.1, Vector3.zero, 0f, s), "check over after 3 s");
            Assert.IsFalse(Run(sch, 33.2, 62.9, s));
            Assert.IsTrue(sch.WantsFrames(63.2, Vector3.zero, 0f, s), "the next interval counts from the end of the check");
        }

        [Test]
        public void ACheckIsRateLimited()
        {
            var s = Settings();
            var sch = InLate(s);
            int taken = 0;
            double last = double.NegativeInfinity;
            for (int i = 0; 30 + i * 0.05 <= 33.0; i++)
            {
                double t = 30 + i * 0.05;
                if (!sch.WantsFrames(t, Vector3.zero, 0f, s) || !sch.ShouldInfer(t)) continue;
                Assert.GreaterOrEqual(t - last, 0.25 - 1e-9, "two frames closer than 1 / lateHz");
                sch.OnInferred(t, s);
                last = t;
                taken++;
            }
            Assert.AreEqual(12, taken, 1, "3 s at 4 Hz");
        }

        [Test]
        public void LateChecksEarlyWhenTheCameraMovedFar()
        {
            var s = Settings();
            var sch = InLate(s);
            Assert.IsFalse(sch.WantsFrames(1, new Vector3(1.9f, 0f, 0f), 0f, s));
            Assert.IsTrue(sch.WantsFrames(2, new Vector3(2.1f, 0f, 0f), 0f, s));
        }

        [Test]
        public void LateChecksEarlyWhenTheHeadTurnedFar()
        {
            var s = Settings();
            var sch = new LocalizerSchedule();
            sch.Enter(Phase.Late);
            sch.WantsFrames(0, Vector3.zero, 170f, s);
            Assert.IsFalse(sch.WantsFrames(1, Vector3.zero, -135f, s), "55 degrees across the wrap-around");
            Assert.IsTrue(sch.WantsFrames(2, Vector3.zero, -125f, s), "65 degrees");
        }

        [Test]
        public void AMoveCheckMeasuresFromTheLastCheck()
        {
            var s = Settings();
            var sch = InLate(s);
            var far = new Vector3(3f, 0f, 0f);
            Assert.IsTrue(sch.WantsFrames(1, far, 0f, s));
            Assert.IsFalse(sch.WantsFrames(4.5, far, 0f, s), "check over; the new place is the reference");
            Assert.IsFalse(Run(sch, 4.6, 20, s, far));
        }

        [Test]
        public void EachLateTriggerCanBeSwitchedOffOnItsOwn()
        {
            var s = Settings();
            s.lateByTime = false;
            var sch = InLate(s);
            Assert.IsFalse(Run(sch, 0.1, 100, s), "no interval trigger");
            Assert.IsTrue(sch.WantsFrames(101, new Vector3(3f, 0f, 0f), 0f, s), "the move trigger still works");

            s = Settings();
            s.lateByMove = false;
            sch = InLate(s);
            Assert.IsFalse(sch.WantsFrames(1, new Vector3(30f, 0f, 0f), 0f, s), "no move trigger");
            Assert.IsTrue(sch.WantsFrames(2, new Vector3(30f, 0f, 0f), 90f, s), "the turn trigger still works");

            s = Settings();
            s.lateByTurn = false;
            sch = InLate(s);
            Assert.IsFalse(sch.WantsFrames(1, Vector3.zero, 170f, s), "no turn trigger");
            Assert.IsTrue(sch.WantsFrames(30, Vector3.zero, 170f, s), "the interval still works");

            s = Settings();
            s.lateByTime = s.lateByMove = s.lateByTurn = false;
            sch = InLate(s);
            Assert.IsFalse(Run(sch, 0.1, 100, s, new Vector3(30f, 0f, 90f), 170f), "all triggers off: never checks");
        }

        [Test]
        public void ACheckEndsOnceEnoughFixesAgreeWithThePlacement()
        {
            var s = Settings();
            var sch = InLate(s);
            sch.WantsFrames(30, Vector3.zero, 0f, s);
            sch.CheckedFix(true, 3);
            sch.CheckedFix(true, 3);
            Assert.IsTrue(sch.Checking);
            sch.CheckedFix(true, 3);
            Assert.IsFalse(sch.Checking);
            Assert.IsFalse(sch.WantsFrames(31, Vector3.zero, 0f, s));
        }

        [Test]
        public void ADeviatingFixRestartsTheAgreementCount()
        {
            var s = Settings();
            var sch = InLate(s);
            sch.WantsFrames(30, Vector3.zero, 0f, s);
            sch.CheckedFix(true, 3);
            sch.CheckedFix(true, 3);
            sch.CheckedFix(false, 3);
            sch.CheckedFix(true, 3);
            Assert.IsTrue(sch.Checking);
        }

        // ---- tracking ----

        [Test]
        public void NoFramesWhileTrackingIsLost()
        {
            var s = Settings();
            var sch = new LocalizerSchedule();
            Assert.IsTrue(sch.WantsFrames(0, Vector3.zero, 0f, s));
            Assert.AreEqual(LocalizerSchedule.TrackingChange.Lost, sch.UpdateTracking(false, true));
            Assert.IsTrue(sch.TrackingLost);
            Assert.IsFalse(Run(sch, 1, 500, s), "never gives up, and never looks either");
            Assert.AreEqual(LocalizerSchedule.TrackingChange.None, sch.UpdateTracking(false, true), "reported once");
        }

        [Test]
        public void TrackingLostWhileWornNeedsARestartWhenItComesBack()
        {
            var sch = new LocalizerSchedule();
            sch.Enter(Phase.Late);
            sch.UpdateTracking(false, true);
            Assert.AreEqual(LocalizerSchedule.TrackingChange.RestartNeeded, sch.UpdateTracking(true, true));
            Assert.IsFalse(sch.TrackingLost);
            Assert.AreEqual(LocalizerSchedule.TrackingChange.None, sch.UpdateTracking(true, true));
        }

        [Test]
        public void TakingTheHeadsetOffKeepsThePhaseAndThePlacement()
        {
            var s = Settings();
            foreach (var phase in new[] { Phase.Early, Phase.Late })
            {
                var sch = new LocalizerSchedule();
                sch.Enter(phase);
                sch.UpdateTracking(false, false);
                Assert.AreEqual(LocalizerSchedule.TrackingChange.Resumed, sch.UpdateTracking(true, true));
                Assert.AreEqual(phase, sch.Phase, "Early carries on, Late carries on");
                Assert.IsTrue(phase == Phase.Early ? sch.WantsFrames(1, Vector3.zero, 0f, s) : !sch.Checking);
            }
        }

        [Test]
        public void ResumingCountsTheLateTriggersFromNow()
        {
            var s = Settings();
            var sch = InLate(s);
            sch.UpdateTracking(false, false);
            sch.UpdateTracking(true, true);
            Assert.IsFalse(sch.WantsFrames(500, new Vector3(40f, 0f, 0f), 0f, s), "the place where the headset was put on again is the reference");
            Assert.IsFalse(Run(sch, 500.1, 529.9, s, new Vector3(40f, 0f, 0f)));
            Assert.IsTrue(sch.WantsFrames(530, new Vector3(40f, 0f, 0f), 0f, s));
        }

        // ---- tracking jump ----

        [Test]
        public void OnlyAPerFrameDiscontinuityIsAJump()
        {
            var sch = new LocalizerSchedule();
            Assert.IsFalse(sch.TrackJump(new Vector3(50f, 0f, 0f), 1.5f, 0.0), "the first sample has nothing to compare with");
            Assert.IsFalse(sch.TrackJump(new Vector3(50.1f, 0f, 0f), 1.5f, 0.1));
            Assert.IsTrue(sch.TrackJump(new Vector3(52f, 0f, 0f), 1.5f, 0.2));
            Assert.IsFalse(sch.TrackJump(new Vector3(52.05f, 0f, 0f), 1.5f, 0.3), "compared with the sample before, not the one before the jump");
        }

        [Test]
        public void ALongWalkIsNotAJump()
        {
            var sch = new LocalizerSchedule();
            for (int i = 0; i < 100; i++)
                Assert.IsFalse(sch.TrackJump(new Vector3(0.02f * i, 0f, 0f), 1.5f, 0.016 * i), "step " + i);   // 2 m of walking
        }

        [Test]
        public void ResetKeepsTheJumpBaseline()
        {
            var sch = new LocalizerSchedule();
            sch.TrackJump(Vector3.zero, 1.5f, 1.0);
            sch.Reset();   // the component was disabled while tracking was reset
            Assert.IsTrue(sch.TrackJump(new Vector3(40f, 0f, 0f), 1.5f, 500.0));
        }

        [Test]
        public void ASampleTooLongAgoIsNotComparedWith()
        {
            var sch = new LocalizerSchedule();
            sch.TrackJump(Vector3.zero, 1.5f, 0.0, 2.0);
            Assert.IsFalse(sch.TrackJump(new Vector3(5f, 0f, 0f), 1.5f, 30.0, 2.0), "a walk during a rest");
            Assert.IsTrue(sch.TrackJump(new Vector3(20f, 0f, 0f), 1.5f, 30.25, 2.0));
        }

        [Test]
        public void OnlyFramesCapturedBeforeTheJumpAreStale()
        {
            var sch = new LocalizerSchedule();
            Assert.IsFalse(sch.PredatesJump(0.0), "no jump yet");
            sch.TrackJump(Vector3.zero, 1.5f, 10.0);
            Assert.IsTrue(sch.TrackJump(new Vector3(5f, 0f, 0f), 1.5f, 10.1));
            Assert.IsTrue(sch.PredatesJump(9.9), "captured before the last good sample");
            Assert.IsFalse(sch.PredatesJump(10.05), "captured between the samples, so possibly after the reset");
            Assert.IsFalse(sch.PredatesJump(10.2), "captured after the jump was seen");
        }

        [Test]
        public void AFrameServedAtTheJumpIsNotStale()
        {
            // A source whose pose changes exactly when a frame is served (the scan source): the frame is stamped
            // within the same rendered frame in which the new pose is first sampled.
            var sch = new LocalizerSchedule();
            sch.TrackJump(Vector3.zero, 1.5f, 10.00);
            double frameStamp = 10.05;
            Assert.IsTrue(sch.TrackJump(new Vector3(8f, 0f, 0f), 1.5f, 10.06));
            Assert.IsFalse(sch.PredatesJump(frameStamp));
        }

        [Test]
        public void ASecondJumpMovesTheCutoffForward()
        {
            var sch = new LocalizerSchedule();
            sch.TrackJump(Vector3.zero, 1.5f, 1.0);
            sch.TrackJump(new Vector3(5f, 0f, 0f), 1.5f, 2.0);
            sch.TrackJump(new Vector3(-5f, 0f, 0f), 1.5f, 3.0);
            Assert.IsTrue(sch.PredatesJump(2.5));
        }

        // ---- agreement and deviation ----

        static Alignment At(float x, float yawDeg, float sigma = 0f) =>
            new Alignment { valid = true, scale = 1f, translation = new Vector3(x, 0f, 0f), yawDeg = yawDeg, positionSigma = sigma, yawSigmaDeg = sigma };

        static readonly Vector3 Centroid = Vector3.zero;

        [Test]
        public void FixesAgreeWithinBothLimits()
        {
            Assert.IsTrue(PlacementCheck.Agree(At(0f, 0f), At(0.09f, 2.9f), Centroid, 0.1f, 3f));
            Assert.IsFalse(PlacementCheck.Agree(At(0f, 0f), At(0.11f, 0f), Centroid, 0.1f, 3f), "too far");
            Assert.IsFalse(PlacementCheck.Agree(At(0f, 0f), At(0f, 3.1f), Centroid, 0.1f, 3f), "too much turn");
        }

        [Test]
        public void AngleDifferenceWrapsAround()
        {
            PlacementCheck.Difference(At(0f, 179f), At(0f, -179f), Centroid, out _, out float deg);
            Assert.AreEqual(2f, deg, 1e-3f);
        }

        [Test]
        public void DifferenceIsMeasuredAtTheLandmarks()
        {
            // A small turn moves a centroid far from the map origin a lot, and one at the origin not at all.
            PlacementCheck.Difference(At(0f, 0f), At(0f, 2f), new Vector3(0f, 0f, 10f), out float far, out _);
            PlacementCheck.Difference(At(0f, 0f), At(0f, 2f), Centroid, out float near, out _);
            Assert.AreEqual(0.35f, far, 0.01f);
            Assert.AreEqual(0f, near, 1e-4f);
        }

        [Test]
        public void ALateFixInsideTheRefitLimitsLeavesThePlacementAlone()
        {
            Assert.IsFalse(PlacementCheck.Beyond(At(0f, 0f), At(0.04f, 1.5f), Centroid, 0.05f, 2f));
        }

        [Test]
        public void ALateFixBeyondTheRefitLimitsIsADeviation()
        {
            Assert.IsTrue(PlacementCheck.Beyond(At(0f, 0f), At(0.06f, 0f), Centroid, 0.05f, 2f), "moved");
            Assert.IsTrue(PlacementCheck.Beyond(At(0f, 0f), At(0f, 2.5f), Centroid, 0.05f, 2f), "turned");
        }

        [Test]
        public void ADeviationMustExceedTheFixesOwnUncertainty()
        {
            Assert.IsFalse(PlacementCheck.Beyond(At(0f, 0f, 0.02f), At(0.1f, 0f, 0.02f), Centroid, 0.05f, 2f), "0.05 + 3 x 0.04 = 0.17");
            Assert.IsTrue(PlacementCheck.Beyond(At(0f, 0f, 0.02f), At(0.2f, 0f, 0.02f), Centroid, 0.05f, 2f));
            Assert.IsTrue(PlacementCheck.Beyond(At(0f, 0f, float.PositiveInfinity), At(0.06f, 0f, float.PositiveInfinity), Centroid, 0.05f, 2f), "unknown does not widen the limits");
        }

        [Test]
        public void TheEarlyGateIsWiderThanTheLateLimits()
        {
            Alignment cur = At(0f, 0f), fix = At(0.5f, 10f);
            Assert.IsFalse(PlacementCheck.Beyond(cur, fix, Centroid, 0.9f, 18f), "Early refines with it");
            Assert.IsTrue(PlacementCheck.Beyond(cur, fix, Centroid, 0.05f, 2f), "Late takes it as a deviation");
        }

        // ---- convergence ----

        static CoverageTracker Covered(int fixes, float turnDeg, float moveMeters)
        {
            var c = new CoverageTracker();
            for (int i = 0; i < fixes; i++)
            {
                float f = fixes > 1 ? i / (float)(fixes - 1) : 0f;
                c.Add(new Vector3(moveMeters * f, 0f, 0f), turnDeg * f);
            }
            return c;
        }

        [Test]
        public void ConvergesOnTurnWithEnoughFixes()
        {
            Assert.IsTrue(Covered(4, 30f, 0f).IsComplete(4, 30f, 0.5f));
        }

        [Test]
        public void ConvergesOnTravelWithEnoughFixes()
        {
            Assert.IsTrue(Covered(4, 0f, 0.5f).IsComplete(4, 30f, 0.5f));
        }

        [Test]
        public void DoesNotConvergeWithoutEnoughFixesOrCoverage()
        {
            Assert.IsFalse(Covered(3, 90f, 3f).IsComplete(4, 30f, 0.5f), "too few fixes");
            Assert.IsFalse(Covered(10, 10f, 0.2f).IsComplete(4, 30f, 0.5f), "standing still");
        }

        [Test]
        public void ATurnEitherWayAddsUp()
        {
            var c = new CoverageTracker();
            c.Add(Vector3.zero, 0f);
            c.Add(Vector3.zero, -20f);
            c.Add(Vector3.zero, 20f);
            Assert.AreEqual(40f, c.TurnDegrees, 1e-3f);
            var wrap = new CoverageTracker();
            wrap.Add(Vector3.zero, 350f);
            wrap.Add(Vector3.zero, 10f);
            Assert.AreEqual(20f, wrap.TurnDegrees, 1e-3f);
        }

        [Test]
        public void TravelCountsOnlyAlongTheFloor()
        {
            var c = new CoverageTracker();
            c.Add(new Vector3(0f, 0f, 0f), 0f);
            c.Add(new Vector3(0f, 1.5f, 0f), 0f);   // crouching or standing on a step
            Assert.AreEqual(0f, c.MoveMeters, 1e-4f);
            c.Add(new Vector3(0.3f, 0.5f, 0.4f), 0f);
            Assert.AreEqual(0.5f, c.MoveMeters, 1e-4f);
        }

        [Test]
        public void ResetStartsCoverageOver()
        {
            var c = Covered(5, 40f, 1f);
            c.Reset();
            Assert.AreEqual(0, c.Count);
            Assert.AreEqual(0f, c.TurnDegrees);
            Assert.AreEqual(0f, c.MoveMeters);
            Assert.IsFalse(c.IsComplete(4, 30f, 0.5f));
        }
    }
}
