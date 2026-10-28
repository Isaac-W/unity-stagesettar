using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// "After time X, do Y." Optionally repeating.
    /// </summary>
    /// <remarks>
    /// Counts with Time.deltaTime in Update instead of a coroutine, so it can be configured entirely
    /// in the Inspector and survives being disabled and enabled. For several timed steps in a row use
    /// Experience Sequence instead of chaining several of these.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Timed Action")]
    public sealed class TimedAction : MonoBehaviour
    {
        [Min(0f)] public float duration = 1f;

        [Tooltip("Start counting when the scene starts. Otherwise call Play() from an event.")]
        public bool startOnAwake;

        [Tooltip("Count again after each time is up.")]
        public bool repeat;

        [Tooltip("When repeating, stop after this many times. 0 repeats forever.")]
        [Min(0)] public int repeatCount;

        [Tooltip("Ignore Time.timeScale.")]
        public bool useUnscaledTime;

        public UnityEvent onTimerComplete;

        float _elapsed;
        int _completedCycles;
        bool _running;

        /// <summary>Whether the timer is counting.</summary>
        public bool IsRunning => _running;

        void Start()
        {
            if (startOnAwake) Play();
        }

        void Update()
        {
            if (!_running) return;

            _elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (_elapsed < duration) return;

            // Subtracted, not zeroed: the overshoot belongs to the next cycle, so a repeating
            // timer keeps its average period instead of running slow by up to a frame each time.
            _elapsed = repeat && duration > 0f ? _elapsed - duration : 0f;
            _completedCycles++;
            if (!repeat || (repeatCount > 0 && _completedCycles >= repeatCount)) _running = false;
            onTimerComplete?.Invoke();
        }

        /// <summary>Start (or restart) counting from zero.</summary>
        public void Play()
        {
            _elapsed = 0f;
            _completedCycles = 0;
            _running = true;
        }

        /// <summary>Same as <see cref="Play"/>. Kept for the name used in the original toolkit.</summary>
        public void StartTimer() => Play();

        /// <summary>Stop counting without firing.</summary>
        public void Stop() => _running = false;
    }
}
