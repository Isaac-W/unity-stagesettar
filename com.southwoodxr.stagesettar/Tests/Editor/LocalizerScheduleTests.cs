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
            for (double t = from; t <= to + 1e-9; t += 0.1) active = sch.Update(t, steady, pos, heading, s);
            return active;
        }

        // ---- rate ----

        [Test]
        public void BeforePlacementItInfersAtFullRate()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            Assert.IsTrue(sch.Update(0, false, Vector3.zero, 0f, s));
            Assert.IsTrue(sch.Due(0));
            sch.Inferred(0, s);
            Assert.IsFalse(sch.Due(0.2));
            Assert.IsTrue(sch.Due(0.25));
            Assert.IsTrue(sch.Update(100, false, Vector3.zero, 0f, s), "never rests before it is steady");
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
            Assert.IsFalse(sch.Update(10, true, Vector3.zero, 0f, s), "rest starts at once");
            Assert.IsFalse(Run(sch, 10.1, 39.9, true, s));
            Assert.IsTrue(sch.Update(40, true, Vector3.zero, 0f, s), "30 s after the rest began");
            Assert.IsTrue(sch.Bursting);
            Assert.IsTrue(Run(sch, 40.1, 42.9, true, s));
            Assert.IsFalse(sch.Update(43.1, true, Vector3.zero, 0f, s), "burst over after 3 s");
            Assert.IsFalse(Run(sch, 43.2, 72.9, true, s));
            Assert.IsTrue(sch.Update(73.2, true, Vector3.zero, 0f, s), "next period counts from the end of the burst");
        }

        [Test]
        public void AdaptiveBurstsEarlyWhenTheCameraMovedFar()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Update(0, true, Vector3.zero, 0f, s);
            Assert.IsFalse(sch.Update(1, true, new Vector3(1.9f, 0f, 0f), 0f, s));
            Assert.IsTrue(sch.Update(2, true, new Vector3(2.1f, 0f, 0f), 0f, s));
            Assert.IsTrue(sch.Bursting);
        }

        [Test]
        public void AdaptiveBurstsEarlyWhenTheHeadTurnedFar()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Update(0, true, Vector3.zero, 170f, s);
            Assert.IsFalse(sch.Update(1, true, Vector3.zero, -135f, s), "55 degrees across the wrap-around");
            Assert.IsTrue(sch.Update(2, true, Vector3.zero, -125f, s), "65 degrees");
        }

        [Test]
        public void ADistanceBurstMeasuresFromTheLastRefinement()
        {
            var s = Settings(StarLocalizer.Mode.Adaptive);
            var sch = new LocalizerSchedule();
            sch.Update(0, true, Vector3.zero, 0f, s);
            var far = new Vector3(3f, 0f, 0f);
            Assert.IsTrue(sch.Update(1, true, far, 0f, s));
            Assert.IsFalse(sch.Update(4.5, true, far, 0f, s), "burst over; the new place is the reference");
            Assert.IsFalse(Run(sch, 4.6, 20, true, s, far));
        }

        [Test]
        public void PeriodicIgnoresDistanceAndTurn()
        {
            var s = Settings(StarLocalizer.Mode.Periodic);
            var sch = new LocalizerSchedule();
            sch.Update(0, true, Vector3.zero, 0f, s);
            Assert.IsFalse(sch.Update(1, true, new Vector3(10f, 0f, 0f), 120f, s));
            Assert.IsTrue(sch.Update(30, true, Vector3.zero, 0f, s));
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
            sch.Update(500, true, Vector3.zero, 0f, s);
            Assert.AreEqual(0.0, sch.SecondsWithoutFix, 1e-6);
        }

        // ---- tracking jump ----

        [Test]
        public void OnlyAPerFrameDiscontinuityIsAJump()
        {
            var sch = new LocalizerSchedule();
            Assert.IsFalse(sch.TrackJump(new Vector3(50f, 0f, 0f), 1.5f), "the first sample has nothing to compare with");
            Assert.IsFalse(sch.TrackJump(new Vector3(50.1f, 0f, 0f), 1.5f));
            Assert.IsTrue(sch.TrackJump(new Vector3(52f, 0f, 0f), 1.5f));
            Assert.IsFalse(sch.TrackJump(new Vector3(52.05f, 0f, 0f), 1.5f), "compared with the sample before, not the one before the jump");
        }

        [Test]
        public void ALongWalkIsNotAJump()
        {
            var sch = new LocalizerSchedule();
            for (int i = 0; i < 100; i++)
                Assert.IsFalse(sch.TrackJump(new Vector3(0.02f * i, 0f, 0f), 1.5f), "step " + i);   // 2 m of walking
            sch.Reset();
            Assert.IsFalse(sch.TrackJump(new Vector3(40f, 0f, 0f), 1.5f), "a reset forgets the last position");
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
