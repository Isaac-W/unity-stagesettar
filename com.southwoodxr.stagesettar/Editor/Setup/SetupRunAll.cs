using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>
    /// The "Set up everything" button: runs every step that is still needed, in Order, one at a time.
    /// Package installs, sample imports and compiles reload the domain, so progress lives in
    /// <see cref="SessionState"/> and an [InitializeOnLoadMethod] picks the run up again afterwards.
    /// </summary>
    /// <remarks>
    /// A step runs at most once per run: if it is still Needed after running (and after a grace period
    /// for asynchronous work such as a package resolve) the run stops instead of looping. It also stops
    /// at a Blocked step, at an error, and when a step asked for an editor restart
    /// (<see cref="SetupRestart"/>). Steps whose OpensWindow is false (the open scene, not the project)
    /// are not part of it.
    /// </remarks>
    public static class SetupRunAll
    {
        const string k_active = "StageSettAR.RunAll.Active";
        const string k_current = "StageSettAR.RunAll.Current";
        const string k_title = "StageSettAR.RunAll.CurrentTitle";
        const string k_started = "StageSettAR.RunAll.CurrentStarted";
        const string k_attempted = "StageSettAR.RunAll.Attempted";
        const string k_message = "StageSettAR.RunAll.Message";

        const double k_interval = 0.5;
        // Package and sample work reloads the domain a moment after it reports done, so let the editor sit idle before moving on.
        const double k_idleBeforeAction = 3;
        // A Blocked step is normally an install in flight; give up after this long.
        const double k_blockedTimeout = 15 * 60;
        // A step that is still Needed right after Run may have started something asynchronous (Client.Resolve).
        const double k_neededGrace = 30;

        static bool s_subscribed;
        static double s_nextTick, s_idleSince = -1;

        public static bool Active => SessionState.GetBool(k_active, false);

        /// <summary>Title of the step being run or waited for, for the window.</summary>
        public static string CurrentTitle => SessionState.GetString(k_title, "");

        /// <summary>Outcome of the last run (why it stopped, or that it finished).</summary>
        public static string Message => SessionState.GetString(k_message, "");

        [InitializeOnLoadMethod]
        static void Resume()
        {
            if (Active) Subscribe();
        }

        public static void Start()
        {
            if (Active) return;
            SessionState.SetBool(k_active, true);
            SessionState.SetString(k_attempted, "");
            SessionState.SetString(k_current, "");
            SessionState.SetString(k_title, "");
            SessionState.SetString(k_message, "");
            SessionState.SetBool(SetupRestart.Key, false);
            s_idleSince = -1;
            Debug.Log("[StageSettAR] Set up everything: started.");
            Subscribe();
        }

        public static void Cancel()
        {
            if (Active) Stop("Stopped by you.");
        }

        static void Subscribe()
        {
            if (s_subscribed) return;
            s_subscribed = true;
            EditorApplication.update += Tick;
        }

        static void Stop(string message)
        {
            SessionState.SetBool(k_active, false);
            SessionState.SetString(k_current, "");
            SessionState.SetString(k_title, "");
            SessionState.SetString(k_message, message);
            if (s_subscribed) EditorApplication.update -= Tick;
            s_subscribed = false;
            Debug.Log("[StageSettAR] Set up everything: " + message);
        }

        static void Tick()
        {
            if (!Active)
            {
                EditorApplication.update -= Tick;
                s_subscribed = false;
                return;
            }
            var now = EditorApplication.timeSinceStartup;
            if (now < s_nextTick) return;
            s_nextTick = now + k_interval;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                s_idleSince = -1;
                return;
            }
            if (s_idleSince < 0) s_idleSince = now;

            try { Advance(now - s_idleSince >= k_idleBeforeAction); }
            catch (Exception e)
            {
                Debug.LogException(e);
                Stop("Stopped by an error: " + e.Message);
            }
        }

        // idle: the editor has been quiet long enough to start the next step. Waiting on the current one needs no idle time.
        static void Advance(bool idle)
        {
            // Steps are rediscovered every time (a package install adds assemblies, and fresh instances have no stale caches).
            var steps = SetupWindow.Discover().FindAll(s => s.OpensWindow);

            var currentName = SessionState.GetString(k_current, "");
            if (currentName.Length > 0)
            {
                var current = steps.Find(s => s.GetType().FullName == currentName);
                if (current != null)
                {
                    var status = SetupWindow.Query(current, out var detail);
                    long.TryParse(SessionState.GetString(k_started, "0"), out var ticks);
                    var waited = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
                    if (status == SetupStatus.Blocked && waited < k_blockedTimeout) return;
                    if (status == SetupStatus.Needed && waited < k_neededGrace) return;
                    if (status != SetupStatus.Done)
                    {
                        Stop($"'{current.Title}' did not finish: {detail}");
                        return;
                    }
                }
                SessionState.SetString(k_current, "");
                SessionState.SetString(k_title, "");
            }

            if (SessionState.GetBool(SetupRestart.Key, false))
            {
                Stop("Restart Unity, then click 'Set up everything' again to finish.");
                return;
            }
            if (!idle) return;

            var attempted = new HashSet<string>(SessionState.GetString(k_attempted, "").Split('|', StringSplitOptions.RemoveEmptyEntries));
            foreach (var step in steps)
            {
                var status = SetupWindow.Query(step, out var detail);
                if (status == SetupStatus.Done) continue;
                if (status == SetupStatus.Blocked)
                {
                    Stop($"Stopped at '{step.Title}': {detail}");
                    return;
                }

                var key = step.GetType().FullName;
                if (!attempted.Add(key))
                {
                    Stop($"'{step.Title}' is still needed after running it: {detail}");
                    return;
                }
                SessionState.SetString(k_attempted, string.Join("|", attempted));
                SessionState.SetString(k_current, key);
                SessionState.SetString(k_title, step.Title);
                SessionState.SetString(k_started, DateTime.UtcNow.Ticks.ToString());
                Debug.Log($"[StageSettAR] Set up everything: {step.Title}…");
                try { step.Run(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    Stop($"'{step.Title}' failed: {e.Message}");
                }
                return;
            }
            Stop("Every step is done.");
        }
    }
}
