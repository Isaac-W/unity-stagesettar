using NUnit.Framework;
using UnityEngine;

namespace StageSettAR.Tests
{
    /// <summary>The localizer's scheduling decisions and convergence criterion, with plain numbers instead of a running localizer.</summary>
    public class LocalizerScheduleTests
    {
        static ScheduleSettings Settings(StarLocalizer.Mode mode) => new ScheduleSettings
        {
            mode = mode, maxInferenceHz = 4f, periodSeconds = 30f, burstSeconds = 3f,
            refineDistanceMeters = 2f, refineTurnDegrees = 60f, lostAfterSeconds = 15f, trackingJumpMeters = 1.5f,
        };

        /// <summary>Ticks every 0.1 s from <paramref name="from"/> to <paramref name="to"/> and returns the last answer.</summary>
        static bool Run(LocalizerSchedule sch, double from, double to, bool steady, in ScheduleSettings s, Vector3 pos = default, float heading = 0f)
        {
            bool active = false;
            for (int i = 0; from + i * 0.1 <= to + 1e-9; i++) active = sch.Tick(from + i * 0.1, steady, pos, heading, s);
            return active;
        }

        // ---- rate ----

        [Test]
        public void BeforePlacementItInfersAtFullRate()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            Assert.IsTrue(sch.Tick(0, false, Vector3.zero, 0f, s));
            Assert.IsTrue(sch.ShouldInfer(0));
            sch.OnInferred(0, s);
            Assert.IsFalse(sch.ShouldInfer(0.2));
            Assert.IsTrue(sch.ShouldInfer(0.25));
            Assert.IsTrue(sch.Tick(100, false, Vector3.zero, 0f, s), "never rests before it is steady");
        }

        [Test]
        public void AdaptiveStaysAtFullRateUntilConverged()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            Assert.IsTrue(Run(sch, 0, 120, false, s));
        }

        // ---- bursts ----

        [Test]
        public void ConvergedAdaptiveRestsThenBurstsOnThePeriod()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            Assert.IsFalse(sch.Tick(10, true, Vector3.zero, 0f, s), "rest starts at once");
            Assert.IsFalse(Run(sch, 10.1, 39.9, true, s));
            Assert.IsTrue(sch.Tick(40, true, Vector3.zero, 0f, s), "30 s after the rest began");
            Assert.IsTrue(sch.Bursting);
            Assert.IsTrue(Run(sch, 40.1, 42.9, true, s));
            Assert.IsFalse(sch.Tick(43.1, true, Vector3.zero, 0f, s), "burst over after 3 s");
            Assert.IsFalse(Run(sch, 43.2, 72.9, true, s));
            Assert.IsTrue(sch.Tick(73.2, true, Vector3.zero, 0f, s), "next period counts from the end of the burst");
        }

        [Test]
        public void AdaptiveBurstsEarlyWhenTheCameraMovedFar()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            Assert.IsFalse(sch.Tick(1, true, new Vector3(1.9f, 0f, 0f), 0f, s));
            Assert.IsTrue(sch.Tick(2, true, new Vector3(2.1f, 0f, 0f), 0f, s));
            Assert.IsTrue(sch.Bursting);
        }

        [Test]
        public void AdaptiveBurstsEarlyWhenTheHeadTurnedFar()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 170f, s);
            Assert.IsFalse(sch.Tick(1, true, Vector3.zero, -135f, s), "55 degrees across the wrap-around");
            Assert.IsTrue(sch.Tick(2, true, Vector3.zero, -125f, s), "65 degrees");
        }

        [Test]
        public void ADistanceBurstMeasuresFromTheLastRefinement()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            var far = new Vector3(3f, 0f, 0f);
            Assert.IsTrue(sch.Tick(1, true, far, 0f, s));
            Assert.IsFalse(sch.Tick(4.5, true, far, 0f, s), "burst over; the new place is the reference");
            Assert.IsFalse(Run(sch, 4.6, 20, true, s, far));
        }

        [Test]
        public void PeriodicIgnoresDistanceAndTurn()
        {
            var s = Settings(StarLocalizer.Mode.Periodic);
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            Assert.IsFalse(sch.Tick(1, true, new Vector3(10f, 0f, 0f), 120f, s));
            Assert.IsTrue(sch.Tick(30, true, Vector3.zero, 0f, s));
        }

        [Test]
        public void ContinuousNeverRests()
        {
            var s = Settings(StarLocalizer.Mode.Continuous);
            var sch = new LocalizerSchedule();
            Assert.IsTrue(Run(sch, 0, 100, true, s));
        }

        // ---- lost ----

        [Test]
        public void RestingDoesNotCountTowardsLost()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.FixAccepted();
            Run(sch, 0, 29.9, true, s);
            Assert.AreEqual(0.0, sch.SecondsWithoutFix, 1e-6);
            Run(sch, 30, 33.2, true, s);   // one burst
            Assert.AreEqual(3.0, sch.SecondsWithoutFix, 0.2);
            Run(sch, 33.3, 62.9, true, s); // resting again
            Assert.AreEqual(3.0, sch.SecondsWithoutFix, 0.2);
            Assert.IsFalse(sch.TimedOut(s));
        }

        [Test]
        public void SearchingCountsTowardsLostAndAFixClearsIt()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            Run(sch, 0, 14.9, false, s);
            Assert.IsFalse(sch.TimedOut(s));
            Run(sch, 15, 16, false, s);
            Assert.IsTrue(sch.TimedOut(s));
            sch.FixAccepted();
            Assert.IsFalse(sch.TimedOut(s));
        }

        [Test]
        public void ResetForgetsTheTimeBeforeAPause()
        {
            var s = Settings(StarLocalizer.Mode.Continuous);
            var sch = new LocalizerSchedule();
            Run(sch, 0, 10, true, s);
            sch.Reset();
            sch.Tick(500, true, Vector3.zero, 0f, s);
            Assert.AreEqual(0.0, sch.SecondsWithoutFix, 1e-6);
        }

        // ---- interrupted and repeated bursts ----

        [Test]
        public void ABurstEndsWhenTheFitIsNoLongerSteady()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            Assert.IsTrue(sch.Tick(30, true, Vector3.zero, 0f, s));
            Assert.IsTrue(sch.Bursting);
            // A re-seed or Lose makes the localizer not steady in the middle of the burst: full rate, no burst.
            Assert.IsTrue(sch.Tick(31, false, Vector3.zero, 0f, s));
            Assert.IsFalse(sch.Bursting);
            Assert.IsTrue(Run(sch, 31.1, 60, false, s), "stays at full rate while not steady");
        }

        [Test]
        public void RestStartsOverOnceSteadyAgain()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            sch.Tick(30, true, Vector3.zero, 0f, s);   // burst
            sch.Tick(31, false, Vector3.zero, 0f, s);  // interrupted
            Assert.IsFalse(sch.Tick(50, true, Vector3.zero, 0f, s), "steady again: rests at once");
            Assert.IsFalse(Run(sch, 50.1, 79.9, true, s), "a whole new period, not the old one");
            Assert.IsTrue(sch.Tick(80, true, Vector3.zero, 0f, s));
        }

        [Test]
        public void BurstsWithoutAFixAddUpToLost()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.FixAccepted();
            // Bursts of 3 s start about every 33 s: four of them are under the 15 s limit, the fifth goes over.
            Run(sch, 0, 135, true, s);
            Assert.AreEqual(12.4, sch.SecondsWithoutFix, 0.6);
            Assert.IsFalse(sch.TimedOut(s));
            Run(sch, 135.1, 170, true, s);
            Assert.IsTrue(sch.TimedOut(s));
        }

        [Test]
        public void ABurstIsRateLimited()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            int taken = 0;
            double last = double.NegativeInfinity;
            for (int i = 0; 30 + i * 0.05 <= 33.0; i++)
            {
                double t = 30 + i * 0.05;
                if (!sch.Tick(t, true, Vector3.zero, 0f, s) || !sch.ShouldInfer(t)) continue;
                Assert.GreaterOrEqual(t - last, 0.25 - 1e-9, "two frames closer than 1 / maxInferenceHz");
                sch.OnInferred(t, s);
                last = t;
                taken++;
            }
            Assert.AreEqual(12, taken, 1, "3 s at 4 Hz");
        }

        [Test]
        public void TheEarlyTriggersAreOffAtZero()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            s.refineDistanceMeters = 0f;
            s.refineTurnDegrees = 0f;
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            Assert.IsFalse(sch.Tick(1, true, new Vector3(100f, 0f, 0f), 170f, s));
            Assert.IsFalse(sch.Tick(29, true, new Vector3(-100f, 0f, 50f), -170f, s));
            Assert.IsTrue(sch.Tick(30, true, Vector3.zero, 0f, s), "the period still works");
        }

        [Test]
        public void EachEarlyTriggerWorksWithoutTheOther()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            s.refineTurnDegrees = 0f;
            var sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            Assert.IsFalse(sch.Tick(1, true, Vector3.zero, 170f, s), "turn trigger is off");
            Assert.IsTrue(sch.Tick(2, true, new Vector3(3f, 0f, 0f), 170f, s));

            s = Settings(StarLocalizer.Mode.Adaptive);
            s.refineDistanceMeters = 0f;
            sch = new LocalizerSchedule();
            sch.Tick(0, true, Vector3.zero, 0f, s);
            Assert.IsFalse(sch.Tick(1, true, new Vector3(30f, 0f, 0f), 0f, s), "distance trigger is off");
            Assert.IsTrue(sch.Tick(2, true, new Vector3(30f, 0f, 0f), 90f, s));
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

        [Test]
        public void ProgressRisesWithCoverage()
        {
            var c = Covered(2, 15f, 0f);
            Assert.AreEqual(0.5f, c.FixesProgress(4), 1e-4f);
            Assert.AreEqual(0.5f, c.SpreadProgress(30f, 0.5f), 1e-4f);
            Assert.AreEqual(1f, Covered(4, 0f, 1f).SpreadProgress(30f, 0.5f), 1e-4f);
        }
    }
}
