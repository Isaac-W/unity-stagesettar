using NUnit.Framework;
using UnityEngine;

namespace StageSettAR.Tests
{
    /// <summary>The easing of the shown map towards its target, and the moves that are too large to ease.</summary>
    public class AlignmentGlideTests
    {
        static GlideSettings Settings() => new GlideSettings { smooth = true, seconds = 0.5f, snapMeters = 1f, snapDegrees = 20f };

        static Alignment At(float x, float yawDeg) =>
            new Alignment { valid = true, translation = new Vector3(x, 0f, 0f), yawDeg = yawDeg };

        [Test]
        public void ItIsAbout95PercentThereAfterTheGlideTime()
        {
            Alignment a = AlignmentGlide.Step(At(0f, 0f), At(0.5f, 10f), Vector3.zero, 0.5f, Settings());
            float left = Mathf.Exp(-3f);
            Assert.AreEqual(0.5f * (1f - left), a.translation.x, 1e-4f);
            Assert.AreEqual(10f * (1f - left), a.yawDeg, 1e-3f);
        }

        [Test]
        public void ItKeepsGettingCloserAndNeverOvershoots()
        {
            Alignment shown = At(0f, 0f), target = At(0.8f, 15f);
            for (int i = 0; i < 200; i++)
            {
                Alignment next = AlignmentGlide.Step(shown, target, Vector3.zero, 0.016f, Settings());
                Assert.GreaterOrEqual(next.translation.x, shown.translation.x);
                Assert.LessOrEqual(next.translation.x, 0.8f);
                shown = next;
            }
            Assert.AreEqual(0.8f, shown.translation.x, 1e-3f);
            Assert.AreEqual(15f, shown.yawDeg, 1e-2f);
        }

        [Test]
        public void ItDoesNotDependOnTheFrameRate()
        {
            Alignment from = At(0f, 0f), to = At(0.9f, 18f);
            Alignment one = AlignmentGlide.Step(from, to, Vector3.zero, 0.2f, Settings());
            Alignment two = AlignmentGlide.Step(AlignmentGlide.Step(from, to, Vector3.zero, 0.1f, Settings()), to, Vector3.zero, 0.1f, Settings());
            Assert.AreEqual(one.translation.x, two.translation.x, 1e-5f);
            Assert.AreEqual(one.yawDeg, two.yawDeg, 1e-4f);
        }

        [Test]
        public void ANoTimeStepChangesNothing()
        {
            Alignment a = AlignmentGlide.Step(At(0f, 0f), At(0.5f, 10f), Vector3.zero, 0f, Settings());
            Assert.AreEqual(0f, a.translation.x);
            Assert.AreEqual(0f, a.yawDeg);
        }

        [Test]
        public void AMoveAboveTheDistanceLimitIsAppliedAtOnce()
        {
            Alignment target = At(1.01f, 0f);
            Assert.AreEqual(1.01f, AlignmentGlide.Step(At(0f, 0f), target, Vector3.zero, 0.016f, Settings()).translation.x);
            Assert.Less(AlignmentGlide.Step(At(0f, 0f), At(0.99f, 0f), Vector3.zero, 0.016f, Settings()).translation.x, 0.99f);
        }

        [Test]
        public void ASmallTurnFarFromTheOriginDoesNotSnap()
        {
            // 10 degrees about the origin moves the origin's neighbourhood very little, but a point 20 m away by 3.5 m;
            // judged at the user standing there, the move is large, and judged at the user standing near the origin it is not.
            Alignment shown = At(0f, 0f), turned = At(0f, 10f);
            Assert.Less(AlignmentGlide.Step(shown, turned, new Vector3(0f, 0f, 1f), 0.016f, Settings()).yawDeg, 10f, "near the origin: glides");
            Assert.AreEqual(10f, AlignmentGlide.Step(shown, turned, new Vector3(0f, 0f, 20f), 0.016f, Settings()).yawDeg, "far away: snaps");
        }

        [Test]
        public void AShiftOfTheOriginThatLeavesTheUserInPlaceDoesNotSnap()
        {
            // A 10 degree turn about the user moves the origin by 3.5 m but the user's surroundings not at all.
            Vector3 user = new Vector3(0f, 0f, 20f);
            Alignment shown = At(0f, 0f);
            Alignment turned = shown;
            turned.yawDeg = 10f;
            turned.translation = user - Quaternion.Euler(0f, 10f, 0f) * user;
            Alignment next = AlignmentGlide.Step(shown, turned, user, 0.016f, Settings());
            Assert.Less(next.yawDeg, 10f);
            Assert.Less(next.translation.magnitude, turned.translation.magnitude);
        }

        [Test]
        public void ATurnAboveTheAngleLimitIsAppliedAtOnce()
        {
            Assert.AreEqual(20.5f, AlignmentGlide.Step(At(0f, 0f), At(0f, 20.5f), Vector3.zero, 0.016f, Settings()).yawDeg);
            Assert.Less(AlignmentGlide.Step(At(0f, 0f), At(0f, 19f), Vector3.zero, 0.016f, Settings()).yawDeg, 19f);
        }

        [Test]
        public void TheAngleIsMeasuredAcrossTheWrapAround()
        {
            Assert.Less(AlignmentGlide.Step(At(0f, 350f), At(0f, 10f), Vector3.zero, 0.016f, Settings()).yawDeg, 360f, "20 degrees apart: glides");
            Assert.AreEqual(100f, AlignmentGlide.Step(At(0f, 350f), At(0f, 100f), Vector3.zero, 0.016f, Settings()).yawDeg, "110 degrees apart: snaps");
        }

        [Test]
        public void AZeroLimitTurnsSnappingOff()
        {
            var g = Settings();
            g.snapMeters = 0f;
            g.snapDegrees = 0f;
            Alignment a = AlignmentGlide.Step(At(0f, 0f), At(50f, 170f), Vector3.zero, 0.016f, g);
            Assert.Less(a.translation.x, 50f);
            Assert.Less(a.yawDeg, 170f);
        }

        [Test]
        public void WithSmoothingOffTheTargetIsAppliedAtOnce()
        {
            var g = Settings();
            g.smooth = false;
            Alignment a = AlignmentGlide.Step(At(0f, 0f), At(0.1f, 1f), Vector3.zero, 0.016f, g);
            Assert.AreEqual(0.1f, a.translation.x);
            Assert.AreEqual(1f, a.yawDeg);
            g.smooth = true;
            g.seconds = 0f;
            Assert.AreEqual(0.1f, AlignmentGlide.Step(At(0f, 0f), At(0.1f, 1f), Vector3.zero, 0.016f, g).translation.x, "no glide time means no glide");
        }
    }
}
