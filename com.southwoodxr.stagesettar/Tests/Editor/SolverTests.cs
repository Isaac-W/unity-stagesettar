using System;
using NUnit.Framework;
using UnityEngine;

namespace StageSettAR.Tests
{
    /// <summary>Conventions, PnP and alignment on synthetic worlds with known answers.</summary>
    public class SolverTests
    {
        const int W = 640, H = 352;
        const double Fx = 450, Fy = 452, Cx = 321, Cy = 170;

        // ---- helpers ----

        static double Gauss(System.Random r)
        {
            double u = 1 - r.NextDouble(), v = r.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
        }

        /// <summary>Unity camera looking from <paramref name="pos"/> at <paramref name="target"/> with +y up.</summary>
        static Pose LookAt(Vector3 pos, Vector3 target)
        {
            Vector3d f = new Vector3d(target - pos);
            f = (1 / f.Length) * f;
            Vector3d right = Vector3d.Cross(new Vector3d(0, 1, 0), f);
            right = (1 / right.Length) * right;
            Vector3d up = Vector3d.Cross(f, right);
            return new Pose(pos, Matrix3x3d.FromColumns(right, up, f).ToQuaternion());
        }

        static Vector3[] Room(System.Random r, int n)
        {
            var p = new Vector3[n];
            for (int i = 0; i < n; i++)
                p[i] = new Vector3((float)(r.NextDouble() * 8 - 4), (float)(r.NextDouble() * 2.5), (float)(r.NextDouble() * 6 - 3));
            return p;
        }

        /// <summary>Pixel in the (turned) network image of a point given in the camera's Unity frame.</summary>
        static bool ProjectNet(Vector3d pUnityCam, int q, out double u, out double v)
        {
            Vector3d cv = new Vector3d(pUnityCam.x, -pUnityCam.y, pUnityCam.z);
            Vector3d net = Conventions.QuarterTurnCameraRotation(q).Transpose() * cv;
            u = v = 0;
            if (net.z <= 0.05) return false;
            u = Fx * net.x / net.z + Cx;
            v = Fy * net.y / net.z + Cy;
            return u >= 0 && u < W && v >= 0 && v < H;
        }

        static Vector3d ToCamera(Pose camera, Vector3 p) =>
            Matrix3x3d.FromQuaternion(camera.rotation).Transpose() * new Vector3d(p - camera.position);

        // ---- conventions ----

        [Test]
        public void OpenCvUnityRoundTrip()
        {
            var r = new System.Random(1);
            for (int i = 0; i < 20; i++)
            {
                Matrix3x3d R = Matrix3x3d.Rodrigues(Gauss(r), Gauss(r), Gauss(r));
                var t = new Vector3d(Gauss(r), Gauss(r), Gauss(r));
                Conventions.UnityToOpenCvCamera(Conventions.OpenCvCameraToUnity(R, t), out Matrix3x3d R2, out Vector3d t2);
                Assert.AreEqual(0, (t - t2).Length, 1e-5);
                Matrix3x3d d = R.Transpose() * R2;
                Assert.AreEqual(3, d.Trace, 1e-5);
            }
        }

        [Test]
        public void UnityPoseProjectsLandmarksToTheOpenCvPixel()
        {
            var r = new System.Random(2);
            Matrix3x3d R = Matrix3x3d.Rodrigues(0.2, -0.7, 0.1);
            var t = new Vector3d(0.5, 1.5, 0.3);
            Pose pose = Conventions.OpenCvCameraToUnity(R, t);
            for (int i = 0; i < 50; i++)
            {
                var pExport = new Vector3((float)(Gauss(r) + 1), (float)(Gauss(r) + 1), (float)(Gauss(r) - 3));
                Vector3d pc = R.Transpose() * (new Vector3d(pExport) - t);
                if (pc.z < 0.2) continue;
                double uCv = Fx * pc.x / pc.z + Cx, vCv = Fy * pc.y / pc.z + Cy;

                Vector3d pu = ToCamera(pose, Conventions.ExportToUnity(pExport));
                double uU = Fx * pu.x / pu.z + Cx, vU = Cy - Fy * pu.y / pu.z;
                Assert.AreEqual(uCv, uU, 1e-3);
                Assert.AreEqual(vCv, vU, 1e-3);
            }
            Assert.AreEqual(new Vector3(1, 2, -3).x, Conventions.UnityToExport(Conventions.ExportToUnity(new Vector3(1, 2, -3))).x);
        }

        [Test]
        public void StarMapStoresExportLandmarksAndExposesUnityOnes()
        {
            var map = ScriptableObject.CreateInstance<StarMap>();
            try
            {
                map.landmarksExport = new[] { new Vector3(1, 2, 3), new Vector3(-4, 5, -6) };
                Assert.AreEqual(2, map.LandmarkCount);
                for (int i = 0; i < map.LandmarkCount; i++)
                {
                    Assert.AreEqual(Conventions.ExportToUnity(map.landmarksExport[i]), map.LandmarkUnity(i));
                    Assert.AreEqual(map.LandmarkUnity(i), map.LandmarksUnity[i]);
                }
                Assert.AreEqual(new Vector3(1, 2, 3), map.landmarksExport[0], "export values are stored verbatim");
            }
            finally { UnityEngine.Object.DestroyImmediate(map); }
        }

        [Test]
        public void QuarterTurnMatchesStarMobileRotateCamera()
        {
            // The network image is the camera image turned q times clockwise: (u, v) -> (h - v, u) per turn.
            Matrix3x3d R = Matrix3x3d.Rodrigues(0.3, 0.2, -0.4);
            var t = new Vector3d(0.1, 0.2, 0.3);
            var P = new Vector3d(1.0, -0.5, 4.0);
            for (int q = 0; q < 4; q++)
            {
                Vector3d pc = R.Transpose() * (P - t);
                double u = Fx * pc.x / pc.z + Cx, v = Fy * pc.y / pc.z + Cy;
                double fx = Fx, fy = Fy, cx = Cx, cy = Cy, w = W, h = H;
                for (int i = 0; i < q; i++)
                {
                    (u, v) = (h - v, u);
                    (fx, fy, cx, cy) = (fy, fx, h - cy, cx);
                    (w, h) = (h, w);
                }
                Matrix3x3d Rnet = R * Conventions.QuarterTurnCameraRotation(q);
                Vector3d pn = Rnet.Transpose() * (P - t);
                Assert.AreEqual(u, fx * pn.x / pn.z + cx, 1e-6, "u, q=" + q);
                Assert.AreEqual(v, fy * pn.y / pn.z + cy, 1e-6, "v, q=" + q);
            }
        }

        // ---- PnP ----

        struct PnpScene
        {
            public int n;
            public Vector3d[] p3;
            public Vector2d[] p2;
            public double[] score;
            public Matrix3x3d R;
            public Vector3d t;
        }

        static PnpScene MakePnpScene(int seed, double noisePx, double outlierFraction)
        {
            var r = new System.Random(seed);
            // Camera in the export frame (right-handed): look along -z (WebXR forward) with a yaw and slight pitch.
            Matrix3x3d R = Matrix3x3d.Rodrigues(0, 0.5, 0) * Matrix3x3d.Rodrigues(0.12, 0, 0) * new Matrix3x3d(1, 0, 0, 0, -1, 0, 0, 0, -1);
            var t = new Vector3d(0.7, 1.4, 2.0);
            var s = new PnpScene { p3 = new Vector3d[300], p2 = new Vector2d[300], score = new double[300], R = R, t = t };
            Matrix3x3d Rt = R.Transpose();
            for (int attempts = 0; s.n < 80 && attempts < 20000; attempts++)
            {
                var p = new Vector3d(r.NextDouble() * 8 - 4, r.NextDouble() * 2.5, r.NextDouble() * 8 - 6);
                Vector3d pc = Rt * (p - t);
                if (pc.z < 0.5) continue;
                double u = Fx * pc.x / pc.z + Cx, v = Fy * pc.y / pc.z + Cy;
                if (u < 0 || u >= W || v < 0 || v >= H) continue;
                bool outlier = r.NextDouble() < outlierFraction;
                s.p3[s.n] = p;
                s.p2[s.n] = outlier ? new Vector2d(r.NextDouble() * W, r.NextDouble() * H)
                                    : new Vector2d(u + noisePx * Gauss(r), v + noisePx * Gauss(r));
                s.score[s.n] = outlier ? 0.2 + 0.4 * r.NextDouble() : 0.5 + 0.5 * r.NextDouble();
                s.n++;
            }
            Assert.GreaterOrEqual(s.n, 60, "scene needs enough visible landmarks");
            return s;
        }

        static void AssertPose(in PnpResult res, in PnpScene s, double maxMetres, double maxDeg)
        {
            Assert.IsTrue(res.success);
            Assert.Less((res.t - s.t).Length, maxMetres);
            Matrix3x3d d = s.R.Transpose() * res.R;
            double deg = Math.Acos(Math.Max(-1, Math.Min(1, (d.Trace - 1) / 2))) * 180 / Math.PI;
            Assert.Less(deg, maxDeg);
        }

        [Test]
        public void PnpRecoversPoseWithNoiseAndOutliers()
        {
            var pnp = new StarPnp(300, PnpSettings.Default);
            for (int seed = 10; seed < 20; seed++)
            {
                PnpScene s = MakePnpScene(seed, 0.5, 0.4);
                PnpResult res = pnp.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H);
                AssertPose(res, s, 0.01, 0.1);
                Assert.Less(res.positionSigma, 0.02);
                Assert.Less(res.rmsPx, 1.5);
            }
        }

        [Test]
        public void PnpMarksTheRightInliers()
        {
            PnpScene s = MakePnpScene(30, 0.3, 0.4);
            var pnp = new StarPnp(300, PnpSettings.Default);
            PnpResult res = pnp.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H);
            int agree = 0;
            for (int i = 0; i < s.n; i++)
            {
                Vector3d pc = s.R.Transpose() * (s.p3[i] - s.t);
                double e = Math.Sqrt(Math.Pow(Fx * pc.x / pc.z + Cx - s.p2[i].x, 2) + Math.Pow(Fy * pc.y / pc.z + Cy - s.p2[i].y, 2));
                if ((e < 3) == pnp.InlierMask[i]) agree++;
            }
            Assert.GreaterOrEqual(agree, s.n - 3);
            Assert.GreaterOrEqual(res.inliers, (int)(0.5 * s.n));
        }

        [Test]
        public void PnpConfidenceIsHighForCleanDataAndLowForNoise()
        {
            var pnp = new StarPnp(300, PnpSettings.Default);
            PnpScene clean = MakePnpScene(40, 0.3, 0.0);
            PnpResult good = pnp.Solve(clean.n, clean.p3, clean.p2, clean.score, Fx, Fy, Cx, Cy, W, H);
            Assert.Greater(good.confidence, 0.9);

            // Pure noise: random pixels for every landmark must not look like a confident pose.
            PnpScene junk = MakePnpScene(42, 0.3, 1.0);
            PnpResult bad = pnp.Solve(junk.n, junk.p3, junk.p2, junk.score, Fx, Fy, Cx, Cy, W, H);
            Assert.Less(bad.confidence, 0.5);
        }

        [Test]
        public void PnpUsesThePriorAndSurvivesABadOne()
        {
            var pnp = new StarPnp(300, PnpSettings.Default);
            PnpScene s = MakePnpScene(50, 0.5, 0.4);

            // A slightly-off prior, as a tracking-predicted pose would be.
            Matrix3x3d Rp = Matrix3x3d.Rodrigues(0.004, -0.006, 0.002) * s.R;
            var tp = new Vector3d(s.t.x + 0.02, s.t.y - 0.01, s.t.z + 0.015);
            PnpResult withPrior = pnp.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H, Rp, tp);
            AssertPose(withPrior, s, 0.01, 0.1);

            // An absurd prior must only cost a hypothesis, not the answer.
            PnpResult bad = pnp.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H, Matrix3x3d.Identity, new Vector3d(50, 50, 50));
            AssertPose(bad, s, 0.01, 0.1);

            // A prior that is the truth is accepted outright, even when almost everything is an outlier.
            PnpScene hard = MakePnpScene(51, 0.5, 0.8);
            PnpResult hardRes = pnp.Solve(hard.n, hard.p3, hard.p2, hard.score, Fx, Fy, Cx, Cy, W, H, hard.R, hard.t);
            AssertPose(hardRes, hard, 0.02, 0.2);
        }

        [Test]
        public void PnpFailsWithTooFewPoints()
        {
            var pnp = new StarPnp(10, PnpSettings.Default);
            PnpScene s = MakePnpScene(60, 0.3, 0);
            Assert.IsFalse(pnp.Solve(3, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H).success);
            Assert.IsFalse(pnp.Solve(5, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H).success, "fewer than minInliers");
        }

        [Test]
        public void P3pRecoversThePoseFromThreePoints()
        {
            PnpScene s = MakePnpScene(70, 0, 0);
            var pnp = new StarPnp(10, PnpSettings.Default);
            var R = new Matrix3x3d[4];
            var t = new Vector3d[4];
            int count = pnp.SolveP3p(s.p3, s.p2, Fx, Fy, Cx, Cy, R, t);
            Assert.GreaterOrEqual(count, 1);
            Assert.LessOrEqual(count, 4);
            bool found = false;
            for (int k = 0; k < count; k++)
            {
                Matrix3x3d d = s.R.Transpose() * R[k];
                double deg = Math.Acos(Math.Max(-1, Math.Min(1, (d.Trace - 1) / 2))) * 180 / Math.PI;
                found |= (t[k] - s.t).Length < 1e-5 && deg < 1e-3;
            }
            Assert.IsTrue(found, "one of the P3P solutions is the true pose");
        }

        [Test]
        public void PnpSolvesWithExactlyFourCorrespondences()
        {
            PnpScene s = MakePnpScene(71, 0, 0);
            PnpSettings settings = PnpSettings.Default;
            settings.minInliers = 4;
            var pnp = new StarPnp(10, settings);
            PnpResult res = pnp.Solve(4, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H);
            AssertPose(res, s, 1e-3, 0.05);
            Assert.AreEqual(4, res.inliers);
        }

        [Test]
        public void PnpIsReproducibleWithAFixedSeed()
        {
            PnpScene s = MakePnpScene(72, 0.5, 0.5);
            PnpSettings settings = PnpSettings.Default;
            settings.seed = 12345;
            var a = new StarPnp(300, settings);
            var b = new StarPnp(300, settings);
            PnpResult r1 = a.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H);
            PnpResult r2 = a.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H);
            PnpResult r3 = b.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H);
            // Same seed and same inputs run the same deterministic code, so the doubles must match bit for bit.
            foreach (PnpResult r in new[] { r2, r3 })
            {
                Assert.AreEqual(r1.t.x, r.t.x);
                Assert.AreEqual(r1.t.y, r.t.y);
                Assert.AreEqual(r1.t.z, r.t.z);
                Assert.AreEqual(r1.inliers, r.inliers);
                Assert.AreEqual(r1.confidence, r.confidence);
            }
        }

        [Test]
        public void PnpRefitWeightsDetectionsByScoreSquared()
        {
            // Half the detections are shifted 3 px (beyond the 2 px Huber knee) and have a fifth of the score.
            // Only the weighted first refit sees the score: with score^2 weights the true points end up almost
            // exactly on the pose, with score weights or none they are pulled well past the tight re-selection
            // gate, so the true half would be lost. The final refit is unweighted and cannot repair that.
            PnpScene s = MakePnpScene(73, 0, 0);
            int trueCount = 0;
            for (int i = 0; i < s.n; i++)
            {
                if (i % 2 == 0) { s.score[i] = 1; trueCount++; }
                else { s.score[i] = 0.2; s.p2[i] = new Vector2d(s.p2[i].x + 3, s.p2[i].y); }
            }
            PnpSettings settings = PnpSettings.Default;
            settings.reselectPx = 0.25;
            var pnp = new StarPnp(300, settings);
            PnpResult res = pnp.Solve(s.n, s.p3, s.p2, s.score, Fx, Fy, Cx, Cy, W, H);
            AssertPose(res, s, 1e-3, 0.05);
            Assert.AreEqual(trueCount, res.inliers);
            for (int i = 0; i < s.n; i++) Assert.AreEqual(i % 2 == 0, pnp.InlierMask[i], "point " + i);
        }

        // ---- alignment ----

        struct AlignScene
        {
            public Vector3[] map;
            public AlignmentSolver solver;
            public Alignment truth;
        }

        /// <summary>Several frames, different cameras and quarter turns, of a world whose map->tracking transform is known.</summary>
        static AlignScene MakeAlignScene(AlignmentSettings settings, Alignment truth, int seed, double noisePx, int[] turns)
        {
            var r = new System.Random(seed);
            Vector3[] map = Room(r, 200);
            var sc = new AlignScene { map = map, truth = truth, solver = new AlignmentSolver(settings, map) };
            Matrix4x4 T = truth.MapToTracking;
            var d = new Detections(map.Length);
            for (int f = 0; f < turns.Length; f++)
            {
                // Camera in tracking space, looking at a point inside the (transformed) room.
                Vector3 camPos = new Vector3((float)(r.NextDouble() * 4 - 2), 1.5f + (float)(r.NextDouble() * 0.2), (float)(r.NextDouble() * 4 - 2));
                Vector3 target = T.MultiplyPoint3x4(new Vector3((float)(r.NextDouble() * 6 - 3), 1f, (float)(r.NextDouble() * 4 - 2)));
                Pose cam = LookAt(camPos, target);

                d.count = 0;
                d.quarterTurns = turns[f];
                d.netIntrinsics = new CameraIntrinsics((float)Fx, (float)Fy, (float)Cx, (float)Cy, W, H);
                for (int l = 0; l < map.Length; l++)
                {
                    if (!ProjectNet(ToCamera(cam, T.MultiplyPoint3x4(map[l])), turns[f], out double u, out double v)) continue;
                    d.landmark[d.count] = l;
                    d.pixel[d.count] = new Vector2d(u + noisePx * Gauss(r), v + noisePx * Gauss(r));
                    d.score[d.count] = 1;
                    d.count++;
                }
                Assert.GreaterOrEqual(d.count, 8, "frame " + f + " sees too few landmarks");
                sc.solver.AddFrame(d, null, cam, f);
            }
            return sc;
        }

        static Alignment Truth(float yaw, Vector3 t) =>
            new Alignment { valid = true, yawDeg = yaw, translation = t };

        static Alignment Perturbed(Alignment a, float yaw, Vector3 t)
        {
            a.yawDeg += yaw; a.translation += t;
            return a;
        }

        [Test]
        public void AlignmentRecoversYawAndTranslationAcrossQuarterTurns()
        {
            Alignment truth = Truth(37f, new Vector3(1.2f, -0.3f, -2f));
            AlignScene sc = MakeAlignScene(AlignmentSettings.Default, truth, 5, 0.5, new[] { 0, 1, 2, 3, 1, 0 });
            Assert.AreEqual(6, sc.solver.FrameCount);
            Alignment fit = sc.solver.Solve(Perturbed(truth, 4f, new Vector3(0.3f, -0.1f, 0.25f)));
            Assert.IsTrue(fit.valid);
            Assert.AreEqual(truth.yawDeg, fit.yawDeg, 0.1);
            Assert.Less((fit.translation - truth.translation).magnitude, 0.02f);
            Assert.Less(fit.rmsPx, 1.2);
            Assert.Less(fit.positionSigma, 0.05f);
            Assert.Greater(fit.positionSigma, 0f);
        }

        [Test]
        public void AlignmentSurvivesOutlierCorrespondences()
        {
            Alignment truth = Truth(-120f, new Vector3(-0.5f, 0.1f, 1.5f));
            AlignScene sc = MakeAlignScene(AlignmentSettings.Default, truth, 6, 0.5, new[] { 1, 1, 0, 2 });
            // A frame full of wrong landmarks must not drag the fit (Huber + inlier gate).
            var r = new System.Random(9);
            var d = new Detections(40) { count = 40, quarterTurns = 1, netIntrinsics = new CameraIntrinsics((float)Fx, (float)Fy, (float)Cx, (float)Cy, W, H) };
            for (int i = 0; i < 40; i++)
            {
                d.landmark[i] = r.Next(sc.map.Length);
                d.pixel[i] = new Vector2d(r.NextDouble() * W, r.NextDouble() * H);
            }
            sc.solver.AddFrame(d, null, LookAt(new Vector3(0, 1.5f, 0), new Vector3(0, 1, 3)), 10);
            Alignment fit = sc.solver.Solve(Perturbed(truth, -3f, new Vector3(0.2f, 0, -0.2f)));
            Assert.AreEqual(truth.yawDeg, fit.yawDeg, 0.2);
            Assert.Less((fit.translation - truth.translation).magnitude, 0.04f);
        }

        [Test]
        public void AlignmentEvictsOldFramesAndIgnoresInvalidStart()
        {
            AlignmentSettings settings = AlignmentSettings.Default;
            settings.maxFrames = 3;
            AlignScene sc = MakeAlignScene(settings, Truth(0f, Vector3.zero), 8, 0.3, new[] { 0, 0, 0, 0, 0 });
            Assert.AreEqual(3, sc.solver.FrameCount);
            Assert.IsFalse(sc.solver.Solve(default).valid);
            sc.solver.Clear();
            Assert.AreEqual(0, sc.solver.FrameCount);
        }

        [Test]
        public void SingleFixIsExactForYawOnlyWorldsAndMapsTheCameraCentreUnderTilt()
        {
            var cameraInMap = new Pose(new Vector3(1f, 1.6f, -2f), Quaternion.Euler(10f, 70f, 0f));
            Alignment truth = Truth(-33f, new Vector3(2f, 0.5f, -1f));
            Quaternion yaw = Quaternion.Euler(0f, truth.yawDeg, 0f);

            var exact = new Pose(truth.MapToTracking.MultiplyPoint3x4(cameraInMap.position), yaw * cameraInMap.rotation);
            Alignment a = AlignmentSolver.FromSingleFix(cameraInMap, exact);
            Assert.IsTrue(a.valid);
            Assert.AreEqual(truth.yawDeg, a.yawDeg, 1e-3);
            Assert.Less((a.translation - truth.translation).magnitude, 1e-4f);

            // Tracking says the camera is tilted 3 degrees more than the map pose: yaw-only, camera still lands on its tracked centre.
            var tilted = new Pose(exact.position, Quaternion.Euler(3f, 0f, 0f) * exact.rotation);
            Alignment b = AlignmentSolver.FromSingleFix(cameraInMap, tilted);
            Assert.Less((b.MapToTracking.MultiplyPoint3x4(cameraInMap.position) - tilted.position).magnitude, 1e-4f);
            Assert.AreEqual(truth.yawDeg, b.yawDeg, 3.5);
            Assert.AreEqual(0f, Vector3.Dot(b.MapToTracking.MultiplyVector(Vector3.up), Vector3.right), 1e-6f);
        }
    }
}
