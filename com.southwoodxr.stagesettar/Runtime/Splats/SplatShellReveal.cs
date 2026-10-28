using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// Fades baked <see cref="SplatShells"/> in or out, together or one after another. Ported from
    /// the UnitySplats fork's GsplatBackdropFade.
    ///
    /// Reads the shell list from the <see cref="SplatShells"/> rather than scanning the
    /// hierarchy, so the shells arrive already ordered by distance and a staggered fade can run
    /// near-to-far without guessing which child is which.
    ///
    /// Drives them through a MaterialPropertyBlock. The shell materials are assets on disk shared
    /// by every band, so tinting the material itself would fade all of them at once and write the
    /// change into the project.
    ///
    /// From code: <see cref="Show"/> and <see cref="Hide"/> animate, <see cref="Complete"/> and
    /// <see cref="Rewind"/> snap, and <see cref="onComplete"/> fires when an animated run ends.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("StageSettAR/Splats/Splat Shell Reveal")]
    public class SplatShellReveal : MonoBehaviour
    {
        public enum Order
        {
            /// <summary>Every shell fades on the same schedule. Same as an overlap of 1.</summary>
            Together,

            /// <summary>Nearest first. The world builds outward from the viewer.</summary>
            NearToFar,

            /// <summary>Farthest first. The sky arrives, then the middle distance closes in.</summary>
            FarToNear
        }

        [Tooltip("Shells to fade. Found automatically on this object, its parents or its children when left empty.")]
        public SplatShells Shells;

        public Order Sequence = Order.NearToFar;

        [Tooltip("Time for the whole sequence, from the first shell starting to the last finishing.")]
        [Min(0f)] public float Duration = 3f;

        [Tooltip(
            "How much each shell's fade overlaps the next.\n\n" +
            "0 is strictly sequential: a shell finishes before the one behind it begins, and each " +
            "gets an equal share of the duration. 1 fades them all at once, each taking the whole " +
            "duration. In between, they overlap by that fraction of their own length.\n\n" +
            "The total is unaffected either way -- lowering this makes each shell quicker rather " +
            "than the sequence longer.")]
        [Range(0f, 1f)] public float Overlap = 0.5f;

        [Min(0f)] public float Delay = 0f;

        public AnimationCurve Curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        public bool UseUnscaledTime = false;

        [Tooltip("Start with every shell invisible, so nothing shows until something plays this.")]
        public bool StartHidden = true;

        public bool PlayOnAwake = false;

        [Tooltip("Fired when an animated Show or Hide finishes (not for Complete or Rewind).")]
        public UnityEvent onComplete;

        static readonly int k_tint = Shader.PropertyToID("_Tint");

        readonly List<Renderer> m_shells = new();
        MaterialPropertyBlock m_block;
        Coroutine m_running;

        /// <summary>Position along the current or last run, 0 at its start and 1 once every shell has finished.</summary>
        public float Progress { get; private set; }

        public bool IsPlaying => m_running != null;

        /// <summary>
        /// Where the shells are, or are heading: true after Show (and while it runs), false after
        /// Hide or Rewind. Starts as the opposite of <see cref="StartHidden"/>.
        /// </summary>
        public bool IsShown { get; private set; }

        /// <summary>How long a full run takes. Duration covers the whole sequence, so this is
        /// simply the delay in front of it.</summary>
        public float TotalDuration => Delay + Duration;

        /// <summary>
        /// Splits the duration between the shells: how long one shell's fade lasts, and how long
        /// after it the next one starts.
        ///
        /// A sequence of n shells, each lasting d and starting s after the last, spans
        /// d + (n-1)s. Overlap sets s as a fraction of d -- none of it when they run together, all
        /// of it when they run end to end -- and d follows from holding the span at Duration. So
        /// the slider redistributes the time rather than adding any.
        /// </summary>
        void ShellTiming(out float each, out float step)
        {
            float overlap = Sequence == Order.Together ? 1f : Mathf.Clamp01(Overlap);
            float spread = 1f + Mathf.Max(m_shells.Count - 1, 0) * (1f - overlap);
            each = Duration / spread;
            step = each * (1f - overlap);
        }

        void Reset() => Shells = FindShells();

        void Awake()
        {
            Rebuild();
            IsShown = !StartHidden;
            SetAll(IsShown ? 1f : 0f);
            if (PlayOnAwake)
                Play();
        }

        // Disabling ends the coroutine without finishing it. Land on the end state and report it, as Play does on an
        // inactive object, so a sequence waiting on onComplete does not hang.
        void OnDisable()
        {
            if (m_running == null)
                return;
            StopCoroutine(m_running);
            m_running = null;
            SetAll(IsShown ? 1f : 0f);
            onComplete?.Invoke();
        }

        void OnDestroy()
        {
            // Property blocks outlive the component, so a scene left mid-fade would stay that way.
            SetAll(1f);
        }

        SplatShells FindShells()
        {
            if (Shells)
                return Shells;
            if (TryGetComponent(out SplatShells here))
                return here;
            var found = GetComponentInParent<SplatShells>();
            return found ? found : GetComponentInChildren<SplatShells>();
        }

        /// <summary>
        /// Recollects the shells. Baking creates them, so anything that ran before a bake is
        /// holding an empty list until this is called again.
        /// </summary>
        public void Rebuild()
        {
            m_block ??= new MaterialPropertyBlock();
            m_shells.Clear();

            var source = FindShells();
            if (!source)
                return;

            Shells = source;

            // Shells come out innermost first, which is the order they were baked in.
            foreach (var shell in source.Shells)
                if (shell?.Instance && shell.Instance.TryGetComponent(out Renderer renderer))
                    m_shells.Add(renderer);

            if (Sequence == Order.FarToNear)
                m_shells.Reverse();
        }

        /// <summary>Fades the shells in.</summary>
        public void Play() => Play(true);

        /// <summary>Fades the shells back out, keeping the same ordering.</summary>
        public void PlayReverse() => Play(false);

        /// <summary>Fades the shells in (true) or out (false), then fires <see cref="onComplete"/>.</summary>
        public void Play(bool show)
        {
            IsShown = show;
            if (!isActiveAndEnabled)
            {
                // A coroutine cannot run here, and a sequence waiting on onComplete would hang.
                // Land on the end state instead.
                Rebuild();
                SetAll(show ? 1f : 0f);
                onComplete?.Invoke();
                return;
            }

            if (m_running != null)
                StopCoroutine(m_running);
            m_running = StartCoroutine(Run(!show));
        }

        public void Show() => Play(true);

        public void Hide() => Play(false);

        /// <summary>Freezes wherever the fade currently is.</summary>
        public void Stop()
        {
            if (m_running == null)
                return;
            StopCoroutine(m_running);
            m_running = null;
        }

        /// <summary>Stops and snaps every shell fully visible. No event.</summary>
        public void Complete()
        {
            Stop();
            Rebuild();
            IsShown = true;
            SetAll(1f);
        }

        /// <summary>Stops and snaps every shell invisible. No event.</summary>
        public void Rewind()
        {
            Stop();
            Rebuild();
            IsShown = false;
            SetAll(0f);
        }

        IEnumerator Run(bool reverse)
        {
            Rebuild();

            float total = TotalDuration;
            float elapsed = 0f;
            while (elapsed < total)
            {
                elapsed += UseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                Evaluate(elapsed, reverse);
                yield return null;
            }

            Evaluate(total, reverse);
            m_running = null;
            onComplete?.Invoke();
        }

        void Evaluate(float elapsed, bool reverse)
        {
            float total = TotalDuration;
            Progress = total > 0f ? Mathf.Clamp01(elapsed / total) : 1f;

            ShellTiming(out float each, out float step);
            for (int i = 0; i < m_shells.Count; i++)
            {
                // Each shell runs the same curve over the same length, offset by its place in the
                // order. Reversing plays the sequence backwards as a whole rather than reversing
                // each shell in place, so the world unbuilds the way it was built.
                float start = Delay + i * step;
                float t = each > 0f
                    ? Mathf.Clamp01((elapsed - start) / each)
                    : (elapsed >= start ? 1f : 0f);
                Apply(m_shells[i], Curve.Evaluate(reverse ? 1f - t : t));
            }
        }

        void SetAll(float alpha)
        {
            Progress = Mathf.Clamp01(alpha);
            foreach (var shell in m_shells)
                Apply(shell, alpha);
        }

        void Apply(Renderer shell, float alpha)
        {
            if (!shell)
                return;

            // The capture is premultiplied, so a fade scales every channel. Scaling alpha alone
            // would leave the colour at full strength over partial coverage and read as a glow.
            float a = Mathf.Clamp01(alpha);
            m_block ??= new MaterialPropertyBlock();
            shell.GetPropertyBlock(m_block);
            m_block.SetColor(k_tint, new Color(a, a, a, a));
            shell.SetPropertyBlock(m_block);

            // Switched off rather than merely faded to nothing. A tint of zero still rasterizes
            // the shell, and a reprojected one searches the depth panorama along every ray before
            // discovering that its contribution will be multiplied away -- tens of dependent
            // texture reads per pixel, per shell, to draw nothing. A staggered fade leaves most of
            // the shells in exactly that state for most of its run, and StartHidden leaves all of
            // them there indefinitely.
            shell.enabled = a > 0f;
        }
    }
}
