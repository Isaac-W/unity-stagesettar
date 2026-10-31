using NUnit.Framework;

namespace StageSettAR.Tests
{
    public class LensMountsTests
    {
        [TestCase("Meta Quest 3")]
        [TestCase("Oculus Quest3")]
        [TestCase("Quest 3")]
        [TestCase("META QUEST 3")]
        public void Quest3_IsMeasured(string model)
        {
            Assert.IsTrue(LensMounts.TryGetMeasured(model, null, out var mount, out var name));
            Assert.AreEqual("Quest 3", name);
            Assert.AreEqual(LensMounts.Quest3.position, mount.position);
            Assert.IsFalse(LensMounts.IsQuest3S(model));
        }

        [TestCase("Meta Quest 3S")]
        [TestCase("Oculus Quest3S")]
        [TestCase("quest 3s")]
        public void Quest3S_IsRecognisedButNotMeasured(string model)
        {
            Assert.IsFalse(LensMounts.TryGetMeasured(model, null, out _, out _));
            Assert.IsTrue(LensMounts.IsQuest3S(model));
        }

        [TestCase("Pixel 8")]
        [TestCase("Oculus Quest 2")]
        [TestCase("")]
        [TestCase(null)]
        public void OtherDevices_AreNeither(string model)
        {
            Assert.IsFalse(LensMounts.TryGetMeasured(model, null, out _, out _));
            Assert.IsFalse(LensMounts.IsQuest3S(model));
        }

        [Test]
        public void Codename_IdentifiesQuest3EvenWithoutAModelString()
        {
            Assert.IsTrue(LensMounts.TryGetMeasured("", "eureka", out _, out var name));
            Assert.AreEqual("Quest 3", name);
            Assert.IsTrue(LensMounts.TryGetMeasured("Something odd", " EUREKA", out _, out _));
        }

        [Test]
        public void Quest3S_ModelWinsOverAMatchingCodename()
        {
            Assert.IsFalse(LensMounts.TryGetMeasured("Meta Quest 3S", "eureka", out _, out _));
        }

        [Test]
        public void OtherCodenames_AreNotMeasured()
        {
            // Quest 1, 2 and Pro codenames: miramar, hollywood, seacliff.
            foreach (var codename in new[] { "miramar", "hollywood", "seacliff" })
                Assert.IsFalse(LensMounts.TryGetMeasured("", codename, out _, out _));
        }
    }
}
