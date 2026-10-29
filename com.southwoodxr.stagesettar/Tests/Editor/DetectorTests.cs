using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEngine;

namespace StageSettAR.Tests
{
    public class DetectorTests
    {
        // 3 landmarks + reject, a 4 x 5 grid of 8 px cells; reject sits at 5 everywhere.
        const int Classes = 4, Gh = 4, Gw = 5, Cell = 8, Hw = Gh * Gw;

        static float[] Baseline()
        {
            var z = new float[Classes * Hw];
            for (int c = 0; c < Hw; c++) z[(Classes - 1) * Hw + c] = 5f;
            return z;
        }

        static void Set(float[] z, int cls, int r, int c, float v) => z[cls * Hw + r * Gw + c] = v;

        static Detections Reference(float[] z)
        {
            var d = new Detections();
            StarDetector.ReadoutReference(z, Classes, Gh, Gw, Cell, d);
            return d;
        }

        [Test]
        public void PeakGivesCellCentreAndScore()
        {
            var z = Baseline();
            Set(z, 0, 1, 2, 10f);
            var d = Reference(z);
            Assert.AreEqual(1, d.count);
            Assert.AreEqual(0, d.landmark[0]);
            Assert.AreEqual(20.0, d.pixel[0].x, 0.01);
            Assert.AreEqual(12.0, d.pixel[0].y, 0.01);
            double e = System.Math.Exp(10), plane = e / (e + 19), cls = e / (e + System.Math.Exp(5) + 2);
            Assert.AreEqual(plane * cls, d.score[0], 1e-6);
        }

        [Test]
        public void SubpixelIsSoftmaxWeightedBetweenCells()
        {
            var z = Baseline();
            Set(z, 0, 1, 2, 10f);
            Set(z, 0, 1, 3, 10f);   // tie: first cell is the peak, the neighbour pulls halfway
            var d = Reference(z);
            Assert.AreEqual(24.0, d.pixel[0].x, 0.01);
            Assert.AreEqual(12.0, d.pixel[0].y, 0.01);
        }

        [Test]
        public void BorderWindowClampsRowAndColumnSeparately()
        {
            var z = Baseline();
            Set(z, 1, 0, 0, 10f);
            Set(z, 1, 0, 1, 10f);
            var d = Reference(z);
            // clamped 3x3 around (0,0) counts cell (0,0) four times and (0,1) twice
            Assert.AreEqual((4 * 4.0 + 2 * 12.0) / 6.0, d.pixel[0].x, 0.01);
            Assert.AreEqual(4.0, d.pixel[0].y, 0.01);
        }

        [Test]
        public void LandmarkThatIsNeverTheTopClassIsNotDetected()
        {
            var z = Baseline();
            Set(z, 0, 1, 2, 10f);
            Set(z, 2, 3, 4, 4f);    // strongest cell of landmark 2 still loses to reject (5)
            var d = Reference(z);
            Assert.AreEqual(1, d.count);
            Assert.AreEqual(0, d.landmark[0]);
        }

        [Test]
        public void GraphReadoutMatchesReference()
        {
            const int classes = 9, gh = 5, gw = 7, hw = gh * gw;
            var rng = new System.Random(7);
            var z = new float[classes * hw];
            for (int i = 0; i < z.Length; i++) z[i] = (float)(rng.NextDouble() * 6 - 3);
            for (int c = 0; c < hw; c++) z[(classes - 1) * hw + c] += 2f;   // sparse detections

            var want = new Detections();
            StarDetector.ReadoutReference(z, classes, gh, gw, Cell, want);
            Assert.Greater(want.count, 0);

            var got = new Detections();
            using (var worker = new Worker(StarDetector.BuildReadoutModel(classes, gh, gw, Cell), BackendType.CPU))
            using (var input = new Tensor<float>(new TensorShape(1, classes, gh, gw), z))
            {
                worker.Schedule(input);
                using var output = ((Tensor<float>)worker.PeekOutput()).ReadbackAndClone();
                StarDetector.DecodeReadout(output.DownloadToArray(), classes, gh, gw, got);
            }

            Assert.AreEqual(want.count, got.count);
            for (int i = 0; i < want.count; i++)
            {
                Assert.AreEqual(want.landmark[i], got.landmark[i]);
                Assert.AreEqual(want.pixel[i].x, got.pixel[i].x, 1e-3);
                Assert.AreEqual(want.pixel[i].y, got.pixel[i].y, 1e-3);
                Assert.AreEqual(want.score[i], got.score[i], 1e-4);
            }
        }

        [Test]
        public void DecodeIgnoresGarbageClassIndices()
        {
            // 2 landmarks + reject, a 1 x 4 grid: valid 1, NaN, negative, past the last class.
            var packed = new float[4 + 3 * 2];
            packed[0] = 1f; packed[1] = float.NaN; packed[2] = -3f; packed[3] = 99f;
            var d = new Detections();
            Assert.DoesNotThrow(() => StarDetector.DecodeReadout(packed, 3, 1, 4, d));
            Assert.AreEqual(1, d.count);
            Assert.AreEqual(1, d.landmark[0]);
        }

        [Test]
        public void LevelCameraNeedsNoTurnAndUntrainedOrientationFallsBack()
        {
            var map = ScriptableObject.CreateInstance<StarMap>();
            try
            {
                var level = new Pose(Vector3.zero, Quaternion.identity);
                map.rotations = new[] { 0, 1 };
                Assert.AreEqual(0, StarDetector.ChooseQuarterTurns(map, level, true));
                map.rotations = new[] { 1 };
                map.defaultRotation = 1;
                Assert.AreEqual(1, StarDetector.ChooseQuarterTurns(map, level, true));
                Assert.AreEqual(1, StarDetector.ChooseQuarterTurns(map, level, false));
            }
            finally { Object.DestroyImmediate(map); }
        }
    }
}
