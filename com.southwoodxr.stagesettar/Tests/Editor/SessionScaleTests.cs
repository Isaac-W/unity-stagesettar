using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace StageSettAR.Tests
{
    /// <summary>The session scale estimator on synthetic trajectories of a world with a known scale error.</summary>
    public class SessionScaleTests
    {
        const double TrueScale = 1.04, Noise = 0.035;
        const double Dt = 0.25;   // seconds between fixes

        // ---- helpers ----

        static double Gauss(System.Random r)
        {
            double u = 1 - r.NextDouble(), v = r.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
        }

        /// <summary>A walk around the room in tracking space, about 0.5 m/s: spread of roughly 2 m.</summary>
        static Vector3d Walk(int i, int phase = 0)
        {
            double a = 0.05 * (i + phase);
            return new Vector3d(2.5 * Math.Sin(a), 1.6 + 0.02 * Math.Sin(0.7 * i), 1.5 * Math.Sin(2 * a));
        }

        /// <summary>
        /// A fix of a world where tracking = scale · Ry(yaw) · map + translation: the camera is exactly at
        /// <paramref name="t"/> in tracking space and the PnP map position carries the noise (<paramref name="noise"/> is the
        /// RMS over the three axes). Returns what <see cref="SessionScaleEstimator.Add"/> returned.
        /// </summary>
        static bool Fix(SessionScaleEstimator est, System.Random r, Vector3d t, double scale, double yawDeg, Vector3d translation,
                        double noise = Noise, Vector3d offset = default)
        {
            double axis = noise / Math.Sqrt(3);
            Vector3d c = (1 / scale) * (Matrix3x3d.RotationY(-yawDeg * Math.PI / 180) * (t - translation));
            c = c + offset + new Vector3d(axis * Gauss(r), axis * Gauss(r), axis * Gauss(r));
            return est.Add(c, t, yawDeg, noise);
        }

        static SessionScaleEstimator Walked(int fixes, double scale, int seed, SessionScaleSettings settings)
        {
            var est = new SessionScaleEstimator(settings);
            var r = new System.Random(seed);
            for (int i = 0; i < fixes; i++) Fix(est, r, Walk(i), scale, 25, new Vector3d(1.5, 0.2, -2));
            return est;
        }

        // ---- tests ----

        [Test]
        public void WalkingConvergesToTheTrueScale()
        {
            SessionScaleEstimator est = Walked(200, TrueScale, 1, SessionScaleSettings.Default);
            Assert.IsTrue(est.IsObservable);
            Assert.AreEqual(200, est.FixCount);
            Assert.AreEqual(TrueScale, est.Scale, 0.005);
            Assert.Greater(est.SpreadMeters, 1.0);
            Assert.Greater(est.ScaleSigma, 0);
            Assert.Less(est.ScaleSigma, 0.01);
        }

        [Test]
        public void StationaryStaysAtThePriorAndIsNotObservable()
        {
            foreach (double prior in new[] { 1.0, 1.02 })
            {
                SessionScaleSettings settings = SessionScaleSettings.Default;
                settings.priorScale = (float)prior;
                var est = new SessionScaleEstimator(settings);
                var r = new System.Random(2);
                for (int i = 0; i < 200; i++)
                {
                    // Headset jitter of a millimetre or so while standing still.
                    var t = new Vector3d(0.3 + 0.001 * Gauss(r), 1.6 + 0.001 * Gauss(r), -0.5 + 0.001 * Gauss(r));
                    Fix(est, r, t, TrueScale, 25, new Vector3d(1.5, 0.2, -2));
                    est.Advance(i * Dt);
                }
                Assert.IsFalse(est.IsObservable);
                Assert.AreEqual(prior, est.Scale, 1e-6);
                Assert.AreEqual(prior, est.AppliedScale, 1e-6);
                Assert.Less(est.SpreadMeters, 0.01);
            }
        }

        [Test]
        public void TurningTheHeadOnTheSpotDoesNotChangeTheScale()
        {
            // The camera sits ~5 cm in front of the head's pivot, so a full-circle head turn moves it round a 5 cm circle.
            var est = new SessionScaleEstimator(SessionScaleSettings.Default);
            var r = new System.Random(3);
            var pivot = new Vector3d(0.2, 1.6, 0.4);
            for (int i = 0; i < 200; i++)
            {
                double turn = 0.1 * i;
                Vector3d t = pivot + new Vector3d(0.05 * Math.Sin(turn), 0, 0.05 * Math.Cos(turn));
                Fix(est, r, t, TrueScale, 25, new Vector3d(1.5, 0.2, -2));
                est.Advance(i * Dt);
            }
            Assert.IsFalse(est.IsObservable);
            Assert.Less(est.SpreadMeters, 0.1);
            Assert.AreEqual(1.0, est.Scale, 1e-6);
            Assert.AreEqual(1.0, est.AppliedScale, 1e-6);
        }

        [Test]
        public void SegmentsWithDifferentYawAndTranslationStillConverge()
        {
            var est = new SessionScaleEstimator(SessionScaleSettings.Default);
            var r = new System.Random(4);
            for (int i = 0; i < 120; i++) Fix(est, r, Walk(i), TrueScale, 30, new Vector3d(1, 0.0, -2));
            est.BeginSegment();   // tracking was reset, the alignment is a new one
            for (int i = 0; i < 120; i++) Fix(est, r, Walk(i, 40), TrueScale, -110, new Vector3d(-3, 0.2, 4));
            est.BeginSegment();
            est.BeginSegment();   // an empty segment is harmless
            Assert.AreEqual(240, est.FixCount);
            Assert.IsTrue(est.IsObservable);
            Assert.AreEqual(TrueScale, est.Scale, 0.005);
        }

        [Test]
        public void OutliersAreRejected()
        {
            var est = new SessionScaleEstimator(SessionScaleSettings.Default);
            var r = new System.Random(6);
            int outliers = 0;
            for (int i = 0; i < 200; i++)
            {
                bool bad = i >= 30 && i % 10 == 0;
                bool accepted = Fix(est, r, Walk(i), TrueScale, 25, new Vector3d(1.5, 0.2, -2), Noise, bad ? new Vector3d(0.6, 0, 0) : default);
                if (bad) { outliers++; Assert.IsFalse(accepted, "outlier at fix " + i); }
            }
            Assert.AreEqual(17, outliers);
            Assert.LessOrEqual(est.FixCount, 200 - outliers);
            Assert.GreaterOrEqual(est.FixCount, 170, "genuine fixes are kept");
            Assert.AreEqual(TrueScale, est.Scale, 0.005);

            // Before the scale is observable nothing is judged, so the same fix is taken.
            var fresh = new SessionScaleEstimator(SessionScaleSettings.Default);
            Assert.IsTrue(fresh.Add(new Vector3d(100, 0, 0), new Vector3d(0, 0, 0), 0, Noise));
            // Garbage is never taken.
            Assert.IsFalse(fresh.Add(new Vector3d(double.NaN, 0, 0), new Vector3d(0, 0, 0), 0, Noise));
        }

        [Test]
        public void AppliedScaleRespectsTheRateLimit()
        {
            SessionScaleSettings settings = SessionScaleSettings.Default;
            var est = new SessionScaleEstimator(settings);
            var r = new System.Random(7);
            double prev = est.AppliedScale, maxStep = 0;
            bool everObservable = false;
            for (int i = 0; i < 240; i++)
            {
                Fix(est, r, Walk(i), TrueScale, 25, new Vector3d(1.5, 0.2, -2));
                est.Advance(i * Dt);
                if (!everObservable)
                {
                    if (est.IsObservable) everObservable = true;
                    else Assert.AreEqual(1.0, est.AppliedScale, 1e-12, "no change before it is observable");
                }
                double step = Math.Abs(est.AppliedScale - prev);
                Assert.LessOrEqual(step, settings.maxRatePerSecond * Dt + 1e-9, "fix " + i);
                maxStep = Math.Max(maxStep, step);
                prev = est.AppliedScale;
            }
            Assert.IsTrue(everObservable);
            Assert.AreEqual(settings.maxRatePerSecond * Dt, maxStep, 1e-9, "it did move at the limit while catching up");
            Assert.AreEqual(est.Scale, est.AppliedScale, 0.003, "and caught up in the end");
            Assert.Greater(est.AppliedScale, 1.03);
        }

        [Test]
        public void AppliedScaleIsClampedAndWarnsOnce()
        {
            SessionScaleSettings settings = SessionScaleSettings.Default;
            settings.maxRatePerSecond = 0f;   // no rate limit: reach the limit quickly

            LogAssert.Expect(LogType.Warning, new Regex("outside"));
            SessionScaleEstimator big = Walked(150, 1.25, 8, settings);
            Assert.Greater(big.Scale, 1.2);
            big.Advance(0);
            big.Advance(1);
            Assert.AreEqual(1.1, big.AppliedScale, 1e-9);

            LogAssert.Expect(LogType.Warning, new Regex("outside"));
            SessionScaleEstimator small = Walked(150, 0.8, 9, settings);
            Assert.Less(small.Scale, 0.85);
            small.Advance(0);
            small.Advance(1);
            Assert.AreEqual(0.9, small.AppliedScale, 1e-9);
        }

        [Test]
        public void DisabledEstimatorLearnsNothing()
        {
            SessionScaleSettings settings = SessionScaleSettings.Default;
            Assert.IsTrue(settings.enabled, "on by default");
            settings.enabled = false;
            SessionScaleEstimator est = Walked(200, TrueScale, 11, settings);
            est.Advance(0);
            est.Advance(10);
            Assert.AreEqual(0, est.FixCount);
            Assert.IsFalse(est.IsObservable);
            Assert.AreEqual(1.0, est.Scale, 1e-12);
            Assert.AreEqual(1.0, est.AppliedScale, 1e-12);
        }

        [Test]
        public void ResetReturnsToThePrior()
        {
            SessionScaleSettings settings = SessionScaleSettings.Default;
            settings.maxRatePerSecond = 0f;
            SessionScaleEstimator est = Walked(150, TrueScale, 10, settings);
            est.Advance(0);
            Assert.IsTrue(est.IsObservable);
            Assert.AreEqual(TrueScale, est.AppliedScale, 0.01);
            est.Reset();
            Assert.IsFalse(est.IsObservable);
            Assert.AreEqual(0, est.FixCount);
            Assert.AreEqual(1.0, est.Scale, 1e-12);
            Assert.AreEqual(1.0, est.AppliedScale, 1e-12);
        }
    }
}
