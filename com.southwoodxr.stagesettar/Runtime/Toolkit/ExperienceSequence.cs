using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// A whole timeline in one inspector: a list of steps, each one "wait for something, then do
    /// something", run one after another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instead of chaining Timed Actions, Trigger Zones and Gaze Targets through each other's events,
    /// list the steps here. A step waits for its condition (a number of seconds, the user entering a
    /// Trigger Zone, the user looking at a Gaze Target, or a call to <see cref="Continue"/> from any
    /// event), then runs its own <c>Then</c> event, then the next step starts waiting. Steps can run
    /// anything a UnityEvent can: show an object, play a video or sound, start a fade.
    /// </para>
    /// <para>
    /// Zone and gaze steps point at the object that carries the On Trigger Action or On Gaze Action
    /// (or a parent of it). The same zone can still have its own events as well.
    /// </para>
    /// <para>
    /// A sequence is also a step source: <see cref="onFinished"/> fires after the last step, and with
    /// Loop on it starts over. It can be started from anywhere with <see cref="Play"/>, for instance
    /// from the map's On Localized event.
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/Experience Sequence")]
    public sealed class ExperienceSequence : MonoBehaviour
    {
        /// <summary>What a step waits for before it runs.</summary>
        public enum WaitFor
        {
            /// <summary>Nothing: run straight away.</summary>
            Nothing = 0,

            /// <summary>A number of seconds.</summary>
            Seconds = 1,

            /// <summary>The user to be inside a Trigger Zone (an object with On Trigger Action).</summary>
            UserInZone = 2,

            /// <summary>The user to look at a Gaze Target (an object with On Gaze Action).</summary>
            UserLooksAt = 3,

            /// <summary>Until something calls <see cref="ExperienceSequence.Continue"/>.</summary>
            Continue = 4,
        }

        [Serializable]
        public class Step
        {
            [Tooltip("A name for your own reference. Shown in the list.")]
            public string label = "Step";

            [Tooltip("What this step waits for before it runs.")]
            public WaitFor waitFor = WaitFor.Seconds;

            [Tooltip("Seconds to wait (when Wait For is Seconds).")]
            [Min(0f)] public float seconds = 1f;

            [Tooltip("The Trigger Zone or Gaze Target to wait for (when Wait For is User In Zone or User Looks At).")]
            public GameObject target;

            [Tooltip("What to do once the wait is over.")]
            public UnityEvent then;
        }

        [Tooltip("The steps, top to bottom.")]
        public List<Step> steps = new();

        [Tooltip("Start by itself when the scene starts. Turn off to start it with Play() from another event.")]
        public bool playOnStart = true;

        [Tooltip("Start again from the first step after the last one.")]
        public bool loop;

        [Tooltip("Ignore Time.timeScale for the timed waits.")]
        public bool useUnscaledTime;

        [Tooltip("Raised when a step begins waiting. The index is its position in the list.")]
        public UnityEvent<int> onStepStarted;

        [Tooltip("Raised after the last step has run.")]
        public UnityEvent onFinished;

        int _index = -1;
        float _waited;
        bool _running;
        bool _continue;
        bool _stepStarted;
        OnTriggerAction _zone;
        OnGazeAction _gaze;
        bool _warned;

        /// <summary>Whether the sequence is running.</summary>
        public bool IsRunning => _running;

        /// <summary>Index of the step being waited on, or -1 when not running.</summary>
        public int CurrentStep => _running ? _index : -1;

        void Start()
        {
            if (playOnStart) Play();
        }

        void Update()
        {
            if (!_running) return;
            if (_index >= steps.Count)
            {
                Finish();
                return;
            }

            var step = steps[_index];
            if (!_stepStarted) BeginStep(step);

            _waited += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (!Satisfied(step)) return;

            // Advance before invoking: the event may call Stop, Play or Continue, and each of them
            // has to see a consistent position.
            int done = _index;
            _index++;
            _stepStarted = false;
            _continue = false;
            step.then?.Invoke();

            // The event may have stopped or restarted the sequence; leave it alone if so.
            if (!_running || _index != done + 1) return;
            if (_index >= steps.Count) Finish();
        }

        void BeginStep(Step step)
        {
            _stepStarted = true;
            _waited = 0f;
            _continue = false;
            _zone = null;
            _gaze = null;
            if (step.waitFor == WaitFor.UserInZone && step.target != null)
                _zone = step.target.GetComponentInParent<OnTriggerAction>() ?? step.target.GetComponentInChildren<OnTriggerAction>();
            if (step.waitFor == WaitFor.UserLooksAt && step.target != null)
                _gaze = step.target.GetComponentInParent<OnGazeAction>() ?? step.target.GetComponentInChildren<OnGazeAction>();
            onStepStarted?.Invoke(_index);
        }

        bool Satisfied(Step step)
        {
            // Continue() and Skip() end whatever the step is waiting for.
            if (_continue) return true;
            switch (step.waitFor)
            {
                case WaitFor.Nothing:
                    return true;
                case WaitFor.Seconds:
                    return _waited >= step.seconds;
                case WaitFor.UserInZone:
                    if (_zone == null) { WarnMissing(step, "an object with On Trigger Action"); return false; }
                    return _zone.IsInside;
                case WaitFor.UserLooksAt:
                    if (_gaze == null) { WarnMissing(step, "an object with On Gaze Action"); return false; }
                    return _gaze.IsGazedAt;
                case WaitFor.Continue:
                    return false;
                default:
                    return true;
            }
        }

        void WarnMissing(Step step, string needed)
        {
            // Once per run: a step stuck forever with no explanation is the worst failure here.
            if (_warned) return;
            _warned = true;
            Debug.LogWarning($"[ExperienceSequence] {name}: step {_index + 1} '{step.label}' waits on " +
                             $"{needed}, but Target is empty or has none. The sequence will wait forever.", this);
        }

        void Finish()
        {
            _running = false;
            onFinished?.Invoke();
            // After the event, so a loop restarted by onFinished handlers does not run twice.
            if (loop && !_running && steps.Count > 0) Play();
        }

        /// <summary>Start from the first step. Restarts if already running.</summary>
        public void Play()
        {
            _index = 0;
            _waited = 0f;
            _continue = false;
            _stepStarted = false;
            _warned = false;
            _running = true;
            if (steps.Count == 0) Finish();
        }

        /// <summary>Stop where it is. Nothing further fires.</summary>
        public void Stop() => _running = false;

        /// <summary>
        /// Finish the current wait now, whatever it was waiting for (a "skip" or "next" button). For
        /// steps whose Wait For is Continue this is the only way they proceed. It only counts while
        /// a step is waiting; calling it earlier is ignored.
        /// </summary>
        public void Continue() => _continue = true;

        /// <summary>Skip the current step's wait and run its event.</summary>
        public void Skip() => _continue = true;
    }
}
