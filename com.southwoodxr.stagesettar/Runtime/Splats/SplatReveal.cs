using UnityEngine;
using UnityEngine.Events;
using Gsplat;

namespace StageSettAR
{
    /// <summary>
    /// Animates a splat in or out by driving <see cref="GsplatRenderer.SplatDownscaleFactor"/>.
    /// Ported from the UnitySplats fork's GsplatReveal.
    ///
    /// That field is the cheapest thing on the renderer to animate. Draw() reads it fresh every
    /// frame into a MaterialPropertyBlock, and it never reaches DispatchInitOrder, so there is no
    /// order rebuild and no synchronous count readback. Animating a cutout's density or radius
    /// looks similar and costs a GPU stall on every frame it changes.
    ///
    /// Because the factor shrinks splats rather than fading them, the midpoint reads as sparse
    /// dots assembling into a surface, and whatever sits behind the splat shows through the gaps
    /// while it does.
    ///
    /// From code: <see cref="Show"/> and <see cref="Hide"/> animate, <see cref="Complete"/> and
    /// <see cref="Rewind"/> snap, and <see cref="onComplete"/> fires when an animated run ends.
    /// </summary>
    [AddComponentMenu("StageSettAR/Splats/Splat Reveal")]
    public class SplatReveal : MonoBehaviour
    {
        [Tooltip("Splat to animate. Found automatically on this object, its parents or its children when left empty.")]
        public GsplatRenderer Renderer;

        [Header("Animation")]
        [Tooltip(
            "Reveal over normalized time. 0 is fully hidden, 1 is the renderer's authored size. " +
            "Overshooting past 1 briefly makes splats larger than authored, which reads as a pop.")]
        public AnimationCurve Curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("Seconds from hidden to fully revealed. 0 snaps.")]
        [Min(0f)] public float Duration = 2f;

        [Tooltip("Seconds to wait after Play before the curve starts advancing.")]
        [Min(0f)] public float Delay = 0f;

        [Tooltip("Advance on unscaled time, so the reveal still runs while the game is paused.")]
        public bool UseUnscaledTime = false;

        [Header("Startup")]
        [Tooltip("Begin the reveal automatically on Awake. Turn off when something else, such as an intro sequence, plays it.")]
        public bool PlayOnAwake = true;

        [Tooltip(
            "Push the hidden state on Awake. Leave this on whenever there is a delay or the reveal " +
            "is triggered later, otherwise the splat renders at full size for the frames before " +
            "the animation takes over.")]
        public bool ApplyStartValueOnAwake = true;

        [Space]
        [Tooltip("Fired when an animated Show or Hide finishes (not for Complete or Rewind).")]
        public UnityEvent onComplete;

        /// <summary>
        /// The renderer's own downscale factor, captured before this component first writes to it.
        /// The reveal finishes here rather than at zero: an authored downscale is usually a
        /// deliberate fill-rate tradeoff, and completing an animation should not quietly undo it.
        /// </summary>
        float m_authoredDownscale;

        bool m_captured;
        bool m_playing;
        bool m_reverse;
        float m_progress;
        float m_delayRemaining;

        public bool IsPlaying => m_playing;

        /// <summary>True once fully revealed, or while a reveal is heading there.</summary>
        public bool IsShown => m_playing ? !m_reverse : m_progress > 0f;

        /// <summary>Normalized position along the curve, 0 hidden to 1 revealed.</summary>
        public float Progress
        {
            get => m_progress;
            set
            {
                m_progress = Mathf.Clamp01(value);
                Apply();
            }
        }

        void Reset() => Renderer = ResolveRenderer();

        void Awake()
        {
            var renderer = ResolveRenderer();
            if (!renderer)
            {
                Debug.LogWarning($"[SplatReveal] No GsplatRenderer found for '{name}'.", this);
                enabled = false;
                return;
            }

            Capture(renderer);

            if (ApplyStartValueOnAwake || PlayOnAwake)
            {
                m_progress = 0f;
                Apply();
            }
            else
            {
                // Not hidden at the start, so the state is "revealed".
                m_progress = 1f;
            }

            if (PlayOnAwake)
                Play();
        }

        void OnDestroy()
        {
            // Leave the splat as the scene authored it rather than stranded mid-animation.
            if (!m_captured)
                return;
            var renderer = ResolveRenderer();
            if (renderer)
                renderer.SplatDownscaleFactor = m_authoredDownscale;
        }

        void Update()
        {
            if (!m_playing)
                return;

            float delta = UseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

            if (m_delayRemaining > 0f)
            {
                m_delayRemaining -= delta;
                if (m_delayRemaining > 0f)
                    return;
                // Spend the remainder of this frame on the curve instead of losing it.
                delta = -m_delayRemaining;
                m_delayRemaining = 0f;
            }

            float step = Duration > 0f ? delta / Duration : 1f;
            m_progress = Mathf.Clamp01(m_progress + (m_reverse ? -step : step));
            Apply();

            if (m_reverse ? m_progress <= 0f : m_progress >= 1f)
            {
                m_playing = false;
                onComplete?.Invoke();
            }
        }

        /// <summary>Reveal from the current position forward.</summary>
        public void Play() => Play(true);

        /// <summary>Hide again, running the curve backwards from the current position.</summary>
        public void PlayReverse() => Play(false);

        public void Show() => Play(true);

        public void Hide() => Play(false);

        /// <summary>Reveal (true) or hide (false) with the animation, then fire <see cref="onComplete"/>.</summary>
        public void Play(bool show)
        {
            if (!isActiveAndEnabled)
            {
                // Update will not run here, and a sequence waiting on onComplete would hang.
                // Land on the end state instead.
                m_progress = show ? 1f : 0f;
                m_playing = false;
                Apply();
                onComplete?.Invoke();
                return;
            }

            m_reverse = !show;
            m_delayRemaining = Delay;
            m_playing = true;
            // Restarting from a finished state replays rather than completing instantly.
            if (show && m_progress >= 1f)
                m_progress = 0f;
            else if (!show && m_progress <= 0f)
                m_progress = 1f;
            Apply();
        }

        /// <summary>Freeze wherever the animation currently is.</summary>
        public void Stop() => m_playing = false;

        /// <summary>Stop and snap back to hidden. No event.</summary>
        public void Rewind()
        {
            m_playing = false;
            m_progress = 0f;
            Apply();
        }

        /// <summary>Stop and snap to fully revealed. No event.</summary>
        public void Complete()
        {
            m_playing = false;
            m_progress = 1f;
            Apply();
        }

        /// <summary>
        /// Re-read the renderer's downscale factor as the reveal's end point. Call this if the
        /// authored value is changed at runtime while this component is idle.
        /// </summary>
        public void RecaptureAuthoredValue()
        {
            m_captured = false;
            var renderer = ResolveRenderer();
            if (renderer)
                Capture(renderer);
        }

        /// <summary>The renderer this acts on: the assigned one, else this object's, a parent's or a child's.</summary>
        public GsplatRenderer ResolveRenderer()
        {
            if (Renderer)
                return Renderer;
            if (TryGetComponent(out GsplatRenderer here))
                return here;
            var found = GetComponentInParent<GsplatRenderer>();
            return found ? found : GetComponentInChildren<GsplatRenderer>();
        }

        void Capture(GsplatRenderer renderer)
        {
            if (m_captured)
                return;
            m_authoredDownscale = Mathf.Clamp01(renderer.SplatDownscaleFactor);
            m_captured = true;
        }

        void Apply()
        {
            var renderer = ResolveRenderer();
            if (!renderer)
                return;

            Capture(renderer);

            float reveal = Curve != null && Curve.length > 0 ? Curve.Evaluate(m_progress) : m_progress;
            // Unclamped so a curve that overshoots can push past the authored size, but the result
            // is clamped: the Range attribute on SplatDownscaleFactor is inspector-only, and a
            // negative factor drives _ScaleFactor above 1, where the fragment shader's A > 1
            // discard starts carving chunks out of every quad.
            // Hidden is a downscale of one, always: the quad collapses to a point and the
            // gaussian falloff cancels with it, so the splat is gone rather than merely small.
            // Full is whatever the renderer was authored with, captured once so the reveal ends on
            // the quality setting rather than overwriting it.
            renderer.SplatDownscaleFactor =
                Mathf.Clamp01(Mathf.LerpUnclamped(1f, m_authoredDownscale, reveal));
        }

#if UNITY_EDITOR
        [ContextMenu("Preview: Hidden")]
        void PreviewHidden() => EditorPreview(0f);

        [ContextMenu("Preview: Revealed")]
        void PreviewRevealed() => EditorPreview(1f);

        void EditorPreview(float progress)
        {
            var renderer = ResolveRenderer();
            if (!renderer)
                return;
            UnityEditor.Undo.RecordObject(renderer, "Preview Splat Reveal");
            Capture(renderer);
            m_progress = progress;
            Apply();
            UnityEditor.EditorUtility.SetDirty(renderer);
        }
#endif
    }
}
