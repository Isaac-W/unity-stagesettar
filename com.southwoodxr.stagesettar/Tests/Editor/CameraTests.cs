using NUnit.Framework;
using UnityEngine;

namespace StageSettAR.Tests
{
    public class CameraTests
    {
        const float Tol = 1e-4f;

        // ------------------------------------------------------------ lens mount

        [Test]
        public void MeasuredMountIsTheDocumentedPose()
        {
            var m = Conventions.QuestRgbLeftMount;
            Assert.AreEqual(-0.047f, m.position.x, Tol);
            Assert.AreEqual(-0.023f, m.position.y, Tol);
            Assert.AreEqual(0.043f, m.position.z, Tol);
            // Euler(10.94, 0.20, -0.15): 10.9 degrees down
            Assert.Less(Quaternion.Angle(Quaternion.Euler(10.94f, 0.20f, -0.15f), m.rotation), 0.1f);
            Vector3 look = m.rotation * Vector3.forward;
            Assert.AreEqual(-Mathf.Sin(10.94f * Mathf.Deg2Rad), look.y, 2e-3f);   // below the horizon
            Assert.Greater(look.z, 0.98f);
        }

        [Test]
        public void MountedPoseOnALevelHeadIsTheMount()
        {
            var m = Conventions.QuestRgbLeftMount;
            var p = Conventions.MountedPose(new Pose(Vector3.zero, Quaternion.identity), m);
            Assert.AreEqual(0f, (p.position - m.position).magnitude, Tol);
            Assert.AreEqual(0f, Quaternion.Angle(p.rotation, m.rotation), 1e-3f);
        }

        [Test]
        public void MountedPoseFollowsTheHead()
        {
            // Head at (1, 2, 3) turned 90 degrees about +y: Unity maps (a, b, c) to (c, b, -a), so the
            // mount (-0.047, -0.023, 0.043) lands at (0.043, -0.023, 0.047) from the head.
            var head = new Pose(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 90f, 0f));
            var p = Conventions.MountedPose(head, Conventions.QuestRgbLeftMount);
            Assert.AreEqual(1.043f, p.position.x, Tol);
            Assert.AreEqual(1.977f, p.position.y, Tol);
            Assert.AreEqual(3.047f, p.position.z, Tol);
            // Looking along +x and 10.94 degrees down.
            Vector3 look = p.rotation * Vector3.forward;
            Assert.AreEqual(Mathf.Cos(10.94f * Mathf.Deg2Rad), look.x, 2e-3f);
            Assert.AreEqual(-Mathf.Sin(10.94f * Mathf.Deg2Rad), look.y, 2e-3f);
            Assert.AreEqual(0f, look.z, 5e-3f);
        }

        // ------------------------------------------------------------ intrinsics through turn, scale, crop

        [Test]
        public void IntrinsicsThroughTurnScaleAndCrop()
        {
            // 1280 x 1280 frame, one clockwise turn, network input 800 x 448 trained at focal 685.9.
            var k = new CameraIntrinsics(867.7f, 867.7f, 639.5f, 639.5f, 1280, 1280);
            var g = StarDetector.Geometry(k, 1280, 1280, 1, 685.9f, 800, 448);

            // turn: fx' = fy, cx' = H - cy
            const double fx1 = 867.7, cx1 = 1280 - 639.5, cy1 = 639.5;
            double s = 685.9 / fx1;                          // 0.79048
            double cw = 800 / s, ch = 448 / s;               // 1012.04 x 566.74
            double x0 = (1280 - cw) / 2, y0 = (1280 - ch) / 2;

            Assert.AreEqual(1280f, g.rotatedW, Tol);
            Assert.AreEqual(1280f, g.rotatedH, Tol);
            Assert.AreEqual(s, g.scale, 1e-5);
            Assert.AreEqual(0.79048, g.scale, 1e-4);
            Assert.AreEqual(cw, g.cropW, 0.01);
            Assert.AreEqual(133.98, g.x0, 0.01);
            Assert.AreEqual(2, g.taps);

            Assert.AreEqual(685.9, g.net.fx, 0.01);          // fx'' = fx' s = the map focal
            Assert.AreEqual(685.9, g.net.fy, 0.01);
            Assert.AreEqual((cx1 - x0) * s, g.net.cx, 0.01);
            Assert.AreEqual((cy1 - y0) * s, g.net.cy, 0.01);
            Assert.AreEqual(400.4, g.net.cx, 0.05);
            Assert.AreEqual(223.6, g.net.cy, 0.05);
            Assert.AreEqual(800, g.net.width);
            Assert.AreEqual(448, g.net.height);
        }

        [Test]
        public void IntrinsicsWithoutTurnAndWithPortraitSizeSwap()
        {
            var k = new CameraIntrinsics(500f, 520f, 300f, 200f, 640, 480);
            var g0 = StarDetector.Geometry(k, 640, 480, 0, 500f, 640, 480);
            Assert.AreEqual(1f, g0.scale, 1e-6f);
            Assert.AreEqual(300f, g0.net.cx, 1e-3f);
            Assert.AreEqual(200f, g0.net.cy, 1e-3f);
            Assert.AreEqual(520f, g0.net.fy, 1e-3f);

            var g1 = StarDetector.Geometry(k, 640, 480, 1, 520f, 480, 640);   // turned: 480 x 640, fx' = 520
            Assert.AreEqual(480f, g1.rotatedW, Tol);
            Assert.AreEqual(640f, g1.rotatedH, Tol);
            Assert.AreEqual(480f - 200f, g1.net.cx, 1e-3f);
            Assert.AreEqual(300f, g1.net.cy, 1e-3f);
            Assert.AreEqual(500f, g1.net.fy, 1e-3f);
        }

        // ------------------------------------------------------------ automatic conversion size

        [Test]
        public void AutoWidthIsFocalRatioRoundedUpToEight()
        {
            // 1280 * 685.9 / 867.7 = 1011.8 -> 1012 -> 1016
            Assert.AreEqual(1016, CameraSource.AutoWidth(1280, 867.7f, 685.9f));
            // exactly a multiple of 8 stays (640 * 0.5 = 320)
            Assert.AreEqual(320, CameraSource.AutoWidth(640, 800f, 400f));
            // just above one rounds up to the next
            Assert.AreEqual(328, CameraSource.AutoWidth(641, 800f, 400f));   // ceil(320.5) = 321 -> 328
        }

        [Test]
        public void AutoWidthNeverExceedsTheImage()
        {
            Assert.AreEqual(1280, CameraSource.AutoWidth(1280, 400f, 900f));
            Assert.AreEqual(1277, CameraSource.AutoWidth(1277, 400f, 900f));   // image itself not a multiple of 8
        }

        [Test]
        public void AutoWidthWithoutInformationKeepsTheImage()
        {
            Assert.AreEqual(1280, CameraSource.AutoWidth(1280, 0f, 685.9f));
            Assert.AreEqual(1280, CameraSource.AutoWidth(1280, 867.7f, 0f));
        }

        // ------------------------------------------------------------ YUV reference

        [Test]
        public void YuvGreyHasNoColour()
        {
            var c = StarDetector.YuvToRgb(0.5f, 0.5f, 0.5f);
            Assert.AreEqual(0.5f, c.x, Tol);
            Assert.AreEqual(0.5f, c.y, Tol);
            Assert.AreEqual(0.5f, c.z, Tol);
        }

        [Test]
        public void YuvPureRedRoundTrips()
        {
            // BT.601 full range of (1, 0, 0): Y = 0.299, Cb = 0.5 - 0.168736, Cr = 1.0
            var c = StarDetector.YuvToRgb(0.299f, 0.5f - 0.168736f, 1f);
            Assert.AreEqual(1f, c.x, 1e-3f);
            Assert.AreEqual(0f, c.y, 1e-3f);
            Assert.AreEqual(0f, c.z, 1e-3f);
        }

        [Test]
        public void YuvIsClampedAndUsesTheFullRangeCoefficients()
        {
            var c = StarDetector.YuvToRgb(0.5f, 0.5f, 1f);   // Cr - 0.5 = 0.5
            Assert.AreEqual(1f, c.x, Tol);                      // 0.5 + 1.402 * 0.5 clamps
            Assert.AreEqual(0.5f - 0.714136f * 0.5f, c.y, Tol);
            Assert.AreEqual(0.5f, c.z, Tol);
            var d = StarDetector.YuvToRgb(0f, 1f, 0.5f);       // Cb - 0.5 = 0.5: blue 1.772 * 0.5 -> 0.886
            Assert.AreEqual(0f, d.x, Tol);
            Assert.AreEqual(0f, d.y, Tol);                      // -0.344136 * 0.5 clamps
            Assert.AreEqual(0.886f, d.z, Tol);
        }

        [Test]
        public void FrameImageSizeFallsBackToTheTexture()
        {
            var f = new CameraFrame { width = 1280, height = 960 };
            Assert.AreEqual(1280, f.ImageWidth);
            Assert.AreEqual(960, f.ImageHeight);
            Assert.AreEqual(CameraImageFormat.Rgb, default(CameraFrame).format);
        }
    }
}
