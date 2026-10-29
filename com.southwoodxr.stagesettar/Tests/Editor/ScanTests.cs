using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace StageSettAR.Tests
{
    /// <summary>Scan reading, the stored-to-upright conversion and the holdout maths.</summary>
    public class ScanTests
    {
        const double Inf = double.PositiveInfinity;

        // ---- a tiny scan on disk ----

        string _dir;

        [TearDown]
        public void DeleteTempScan()
        {
            if (_dir != null && Directory.Exists(_dir)) Directory.Delete(_dir, true);
            _dir = null;
        }

        /// <summary>A scan of <paramref name="count"/> frames (index = position) with identity-ish poses at x = index; <paramref name="extra"/> adds or overrides files.</summary>
        string WriteScan(int count, Dictionary<string, string> extra = null)
        {
            _dir = Path.Combine(Path.GetTempPath(), "starscan-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var files = new Dictionary<string, string>
            {
                ["meta.json"] = "{\"format\":\"starscan\",\"version\":2,\"name\":\"tiny\",\"image\":{\"width\":8,\"height\":4}," +
                                "\"intrinsics\":{\"fx\":10,\"fy\":11,\"cx\":4,\"cy\":2},\"rotation\":1}",
            };
            var poses = new StringBuilder("# index time_ms tx ty tz qx qy qz qw\n\n");
            for (int i = 0; i < count; i++) poses.Append($"{i} {i * 100} {i} 0 0 0 0 0 1\n");
            files["poses.txt"] = poses.ToString();
            if (extra != null) foreach (var kv in extra) files[kv.Key] = kv.Value;
            foreach (var kv in files) File.WriteAllText(Path.Combine(_dir, kv.Key), kv.Value);
            return _dir;
        }

        [Test]
        public void ParsesMetaPosesAndOverrides()
        {
            WriteScan(3, new Dictionary<string, string>
            {
                ["poses.txt"] = "# index time_ms tx ty tz qx qy qz qw\n0 0 1 2 3 0 0 0 1\n\n1 100 0 0 0 0 2 0 2\n2 200 0 0 0 0 0 0 1\n",
                ["intrinsics.txt"] = "1 20 21 5 6\n",
                ["rotations.txt"] = "2 0\n",
                ["segments.txt"] = "# index segment\n2 3\n",
                ["unplaced.txt"] = "0\n",
            });
            using var scan = StarScan.Open(_dir);
            Assert.AreEqual("tiny", scan.Name);
            Assert.AreEqual(3, scan.Frames.Count);

            var f0 = scan.Frames[0];
            Assert.AreEqual(1.0, f0.t.x); Assert.AreEqual(3.0, f0.t.z);
            Assert.AreEqual(1, f0.rotation);                       // meta default
            Assert.AreEqual(10f, f0.K.fx); Assert.AreEqual(8, f0.K.width);
            Assert.IsTrue(f0.unplaced); Assert.IsFalse(f0.HasGroundTruth);

            var f1 = scan.Frames[1];
            Assert.AreEqual(100, f1.timeMs);
            Assert.AreEqual(1.0, f1.R.m02, 1e-6);                  // (0,2,0,2) normalizes to 90 degrees about y
            Assert.AreEqual(21f, f1.K.fy); Assert.AreEqual(6f, f1.K.cy);
            Assert.IsTrue(f1.HasGroundTruth);

            var f2 = scan.Frames[2];
            Assert.AreEqual(0, f2.rotation);                       // overridden
            Assert.AreEqual(3, f2.segment); Assert.IsFalse(f2.HasGroundTruth);
        }

        [Test]
        public void RejectsOtherFormatsAndNewerVersions()
        {
            WriteScan(1, new Dictionary<string, string> { ["meta.json"] = "{\"format\":\"other\",\"version\":1}" });
            Assert.Throws<InvalidDataException>(() => StarScan.Open(_dir));
            DeleteTempScan();
            WriteScan(1, new Dictionary<string, string>
            {
                ["meta.json"] = "{\"format\":\"starscan\",\"version\":3,\"image\":{\"width\":8,\"height\":4},\"intrinsics\":{\"fx\":1,\"fy\":1,\"cx\":0,\"cy\":0}}",
            });
            Assert.Throws<InvalidDataException>(() => StarScan.Open(_dir));
        }

        [Test]
        public void TestSplitIsEveryTenthFrameByDefault()
        {
            WriteScan(25);
            using var scan = StarScan.Open(_dir);
            CollectionAssert.AreEqual(new[] { 9, 19 }, scan.TestFrames());
        }

        [Test]
        public void TestSplitUsesTestFileAndSkipsFramesWithoutGroundTruth()
        {
            WriteScan(25, new Dictionary<string, string>
            {
                ["test.txt"] = "2\n4\n6\n8\n",
                ["unplaced.txt"] = "2\n",
                ["untracked.txt"] = "4\n",
                ["segments.txt"] = "6 1\n",
            });
            using var scan = StarScan.Open(_dir);
            CollectionAssert.AreEqual(new[] { 8 }, scan.TestFrames());
        }

        [Test]
        public void ReadsAZipToo()
        {
            WriteScan(12);
            string zipPath = _dir + ".starscan";
            try
            {
                ZipFile.CreateFromDirectory(_dir, zipPath, CompressionLevel.NoCompression, false);
                using var scan = StarScan.Open(zipPath);
                Assert.AreEqual(12, scan.Frames.Count);
                CollectionAssert.AreEqual(new[] { 9 }, scan.TestFrames());
            }
            finally { File.Delete(zipPath); }
        }

        // ---- stored to upright ----

        [Test]
        public void UprightImageHoldsTheStoredPixelsTurnedBack()
        {
            // Stored 4 x 2 image, one clockwise turn from an upright 2 x 4 one. PNG bytes under the .jpg name keep the pixels exact.
            var stored = new Texture2D(4, 2, TextureFormat.RGB24, false);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 4; x++) stored.SetPixel(x, 1 - y, new Color32((byte)(x * 60), (byte)(y * 100), 7, 255));   // (x, y) top-left
            stored.Apply();
            byte[] png = stored.EncodeToPNG();
            WriteScan(1, new Dictionary<string, string>
            {
                ["meta.json"] = "{\"format\":\"starscan\",\"version\":2,\"image\":{\"width\":4,\"height\":2}," +
                                "\"intrinsics\":{\"fx\":10,\"fy\":11,\"cx\":1.5,\"cy\":0.5},\"rotation\":1}",
            });
            Directory.CreateDirectory(Path.Combine(_dir, "frames"));
            File.WriteAllBytes(Path.Combine(_dir, "frames", "000000.jpg"), png);

            using var scan = StarScan.Open(_dir);
            UprightFrame up = scan.LoadUpright(0);
            try
            {
                Assert.AreEqual(2, up.texture.width);
                Assert.AreEqual(4, up.texture.height);
                Assert.AreEqual(2, up.intrinsics.width);
                for (int y = 0; y < 2; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        Vector2 p = StarScan.TurnPixelCcw(new Vector2(x + 0.5f, y + 0.5f), 4, 2, 1);
                        Color32 got = up.texture.GetPixel((int)p.x, 3 - (int)p.y);
                        Assert.AreEqual(x * 60, got.r); Assert.AreEqual(y * 100, got.g);
                    }
            }
            finally { UnityObjects.Discard(up.texture); UnityObjects.Discard(stored); }
        }

        [Test]
        public void CropKeepsTheImageContentAndShiftsThePrincipalPoint()
        {
            // 8 x 4 stored image, no turn, cropped to 1:1: a 4 x 4 window starting at x = 2.
            var stored = new Texture2D(8, 4, TextureFormat.RGB24, false);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 8; x++) stored.SetPixel(x, 3 - y, new Color32((byte)(x * 30), (byte)(y * 60), 9, 255));
            stored.Apply();
            byte[] png = stored.EncodeToPNG();
            WriteScan(1, new Dictionary<string, string>
            {
                ["meta.json"] = "{\"format\":\"starscan\",\"version\":2,\"image\":{\"width\":8,\"height\":4}," +
                                "\"intrinsics\":{\"fx\":10,\"fy\":11,\"cx\":4,\"cy\":2},\"rotation\":0}",
            });
            Directory.CreateDirectory(Path.Combine(_dir, "frames"));
            File.WriteAllBytes(Path.Combine(_dir, "frames", "000000.jpg"), png);

            using var scan = StarScan.Open(_dir);
            UprightFrame full = scan.LoadUpright(0);
            UprightFrame crop = scan.LoadUpright(0, StarScan.AspectRatio(CropAspect.Square, 0, 0));
            try
            {
                Assert.AreEqual(4, crop.texture.width); Assert.AreEqual(4, crop.texture.height);
                Assert.AreEqual(10f, crop.intrinsics.fx); Assert.AreEqual(11f, crop.intrinsics.fy);
                Assert.AreEqual(2f, crop.intrinsics.cx); Assert.AreEqual(2f, crop.intrinsics.cy);
                Assert.AreEqual(4, crop.intrinsics.width);
                Assert.AreEqual(0.0, (crop.R.Transpose() * full.R).Trace - 3, 1e-12);   // GT pose unchanged

                // A GT point projects to pixel p in the full frame and p - (2, 0) in the crop, and both pixels hold the same colour.
                var world = new Vector3d(0.1, -0.05, 2.0);
                Vector2 pf = Project(full.R, full.t, full.intrinsics, world);
                Vector2 pc = Project(crop.R, crop.t, crop.intrinsics, world);
                Assert.AreEqual(pf.x - 2f, pc.x, 1e-4); Assert.AreEqual(pf.y, pc.y, 1e-4);
                int xf = (int)pf.x, yf = (int)pf.y, xc = (int)pc.x, yc = (int)pc.y;
                Color32 a = full.texture.GetPixel(xf, 3 - yf), b = crop.texture.GetPixel(xc, 3 - yc);
                Assert.AreEqual(a.r, b.r); Assert.AreEqual(a.g, b.g);
            }
            finally { UnityObjects.Discard(full.texture); UnityObjects.Discard(crop.texture); UnityObjects.Discard(stored); }
        }

        [Test]
        public void CropRectIsTheLargestCentredWindow()
        {
            StarScan.CropRect(800, 448, 1f, out int x0, out int y0, out int w, out int h);
            Assert.AreEqual((176, 0, 448, 448), (x0, y0, w, h));
            StarScan.CropRect(800, 448, 4f / 3f, out x0, out y0, out w, out h);
            Assert.AreEqual((101, 0, 597, 448), (x0, y0, w, h));
            StarScan.CropRect(448, 800, 1f, out x0, out y0, out w, out h);   // portrait: crop the height
            Assert.AreEqual((0, 176, 448, 448), (x0, y0, w, h));
            StarScan.CropRect(800, 448, 0f, out x0, out y0, out w, out h);
            Assert.AreEqual((0, 0, 800, 448), (x0, y0, w, h));
        }

        /// <summary>Projects <paramref name="world"/> into a camera (OpenCV, camera-to-world R, t) with intrinsics k.</summary>
        static Vector2 Project(Matrix3x3d r, Vector3d t, CameraIntrinsics k, Vector3d world)
        {
            Vector3d c = r.Transpose() * (world - t);
            return new Vector2((float)(k.fx * c.x / c.z + k.cx), (float)(k.fy * c.y / c.z + k.cy));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void StoredPoseAndIntrinsicsProjectLikeTheUprightOnes(int q)
        {
            // Upright camera, then the stored one as the scan writes it: image turned q times clockwise, R_stored = R_up M^q.
            var kUp = new CameraIntrinsics(450f, 452f, 321f, 170f, 640, 352);
            Matrix3x3d rUp = Matrix3x3d.Rodrigues(0.3, -0.2, 0.5);
            var t = new Vector3d(0.4, -0.1, 0.2);
            Matrix3x3d rStored = rUp * Conventions.QuarterTurnCameraRotation(q);
            var kStored = kUp;
            for (int i = 0; i < q; i++)
                kStored = new CameraIntrinsics(kStored.fy, kStored.fx, kStored.height - kStored.cy, kStored.cx, kStored.height, kStored.width);

            StarScan.StoredToUpright(kStored, rStored, q, out CameraIntrinsics k, out Matrix3x3d r);
            Assert.AreEqual(kUp.fx, k.fx, 1e-3); Assert.AreEqual(kUp.fy, k.fy, 1e-3);
            Assert.AreEqual(kUp.cx, k.cx, 1e-3); Assert.AreEqual(kUp.cy, k.cy, 1e-3);
            Assert.AreEqual(kUp.width, k.width); Assert.AreEqual(kUp.height, k.height);
            Assert.AreEqual(0.0, (r.Transpose() * rUp).Trace - 3, 1e-9);

            // A world point seen at pixel p in the stored image is at the pixel p turned back in the upright one.
            var world = new Vector3d(0.5, 0.3, 4.0);
            Vector2 inStored = Project(rStored, t, kStored, world);
            Vector2 turned = StarScan.TurnPixelCcw(inStored, kStored.width, kStored.height, q);
            Vector2 inUpright = Project(r, t, k, world);
            Assert.AreEqual(inUpright.x, turned.x, 1e-3);
            Assert.AreEqual(inUpright.y, turned.y, 1e-3);
        }

        // ---- holdout maths ----

        [Test]
        public void PoseDistanceIsCentreDistanceAndRotationAngle()
        {
            Matrix3x3d turn = Matrix3x3d.Rodrigues(0, 0, Math.PI / 2);
            PoseMetrics.Distance(Matrix3x3d.Identity, new Vector3d(0, 0, 0), turn, new Vector3d(0.03, 0.04, 0), out double m, out double deg);
            Assert.AreEqual(0.05, m, 1e-12);
            Assert.AreEqual(90.0, deg, 1e-9);
        }

        [Test]
        public void PoseDistanceClampsTraceRoundoff()
        {
            // A trace a hair above 3 would make acos return NaN without the clamp.
            var almostIdentity = new Matrix3x3d(1.0000001, 0, 0, 0, 1, 0, 0, 0, 1);
            PoseMetrics.Distance(Matrix3x3d.Identity, new Vector3d(0, 0, 0), almostIdentity, new Vector3d(0, 0, 0), out _, out double deg);
            Assert.AreEqual(0.0, deg, 1e-9);
        }

        [Test]
        public void MedianHandlesEvenCountsAndInfinity()
        {
            Assert.AreEqual(2.0, PoseMetrics.Median(new[] { Inf, 1.0, 2.0 }));
            Assert.AreEqual(2.5, PoseMetrics.Median(new[] { 3.0, 1.0, 2.0, 4.0 }));
            Assert.AreEqual(2.5, PoseMetrics.Median(new[] { 1.0, 2.0, 3.0, Inf }));
            Assert.AreEqual(Inf, PoseMetrics.Median(new[] { 1.0, Inf, Inf, Inf }));
            Assert.IsNaN(PoseMetrics.Median(new double[0]));
        }

        [Test]
        public void ScoreCountsLimitsInclusiveAndFailuresAsMisses()
        {
            var meters = new[] { 0.05, 0.10, 0.20, Inf };
            var degrees = new[] { 5.0, 10.0, 4.0, Inf };
            HoldoutScore s = PoseMetrics.Score(meters, degrees);
            Assert.AreEqual(4, s.frames);
            Assert.AreEqual(25.0, s.recall5cm5deg, 1e-9);
            Assert.AreEqual(50.0, s.recall10cm10deg, 1e-9);
            Assert.AreEqual(15.0, s.medianCm, 1e-9);
            Assert.AreEqual(7.5, s.medianDeg, 1e-9);
        }

        [Test]
        public void ScoreMedianIsInfiniteWhenMostFramesFail()
        {
            HoldoutScore s = PoseMetrics.Score(new[] { 0.01, Inf, Inf }, new[] { 1.0, Inf, Inf });
            Assert.AreEqual(Inf, s.medianCm);
            Assert.AreEqual(Inf, s.medianDeg);
            Assert.AreEqual(100.0 / 3, s.recall5cm5deg, 1e-9);
        }
    }
}
