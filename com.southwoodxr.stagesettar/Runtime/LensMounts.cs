using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Where the passthrough camera sensor sits relative to the headset's camera, for the headsets that have been
    /// measured, and how to tell which headset this is.
    /// </summary>
    /// <remarks>
    /// Only a measurement belongs here: a guessed mount would be off by a different amount, and nothing would say
    /// so. A headset that is recognised but not measured (the Quest 3S so far) uses the Quest 3 mount and a
    /// warning, so people know to tune it. A headset is told apart two ways. Its Android device codename
    /// (<c>android.os.Build.DEVICE</c>, "eureka" for the Quest 3, as in Meta's own tooling) is exact. Its display
    /// model (<c>SystemInfo.deviceModel</c>) is matched by substring, ignoring case and spaces, so "Oculus Quest3",
    /// "Meta Quest 3" and "Quest 3" all count; "3S" is checked first because it contains "3". The 3S's codename is
    /// not known yet: the camera source's warning and debug log print both strings so it can be added.
    /// </remarks>
    public static class LensMounts
    {
        /// <summary>Quest 3, left RGB camera. See <see cref="Conventions.QuestRgbLeftMount"/> for how it was measured.</summary>
        public static Pose Quest3 => Conventions.QuestRgbLeftMount;

        /// <summary>Android's device codename for the Quest 3 (<c>android.os.Build.DEVICE</c>).</summary>
        public const string Quest3Codename = "eureka";

        /// <summary>The measured mount for this device, if it has one. The codename may be empty (not Android).</summary>
        public static bool TryGetMeasured(string deviceModel, string codename, out Pose mount, out string name)
        {
            mount = Pose.identity;
            name = null;
            if (IsQuest3S(deviceModel)) return false;
            if (Normalize(codename) != Quest3Codename && !Normalize(deviceModel).Contains("quest3")) return false;
            mount = Quest3;
            name = "Quest 3";
            return true;
        }

        /// <summary>A Quest 3S, which has not been measured yet.</summary>
        public static bool IsQuest3S(string deviceModel) => Normalize(deviceModel).Contains("quest3s");

        static string Normalize(string s) => (s ?? "").Replace(" ", "").ToLowerInvariant();
    }
}
