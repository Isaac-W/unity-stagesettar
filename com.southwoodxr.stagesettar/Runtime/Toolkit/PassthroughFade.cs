using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// Fades the camera background from transparent to a solid colour, taking the scene from Meta
    /// Passthrough to a fully virtual backdrop, and back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quest Passthrough is an underlay: the runtime composites it behind whatever the app draws,
    /// wherever the app's alpha is less than one. So there is nothing to fade out directly. Raising
    /// the camera's clear alpha is what pushes it away, and dropping it back is what brings it in.
    /// </para>
    /// <para>
    /// The colour is premultiplied on the way out. OpenXR's alpha-blend environment mode expects
    /// premultiplied alpha, while Unity's camera clear writes whatever RGBA it is handed, so a naive
    /// lerp of the alpha alone shows the colour at full intensity through the middle of the fade.
    /// </para>
    /// <para>
    /// The Quest boundary is only hidden while passthrough is rendering, so it may reappear once
    /// this has gone fully virtual. The rig's Boundary Visibility Keeper keeps asking for it to be
    /// hidden again.
    /// </para>
    /// <para>
    /// For sequences: <see cref="FadeToVirtual"/> and <see cref="FadeToPassthrough"/> start the fade;
    /// <see cref="onFadedToVirtual"/>, <see cref="onFadedToPassthrough"/> and <see cref="onComplete"/>
    /// report when it ends.
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/Passthrough Fade")]
    public sealed class PassthroughFade : MonoBehaviour
    {
        [Tooltip("The camera whose background is faded. Found automatically (this object or above, then the user's camera) when empty.")]
        public Camera targetCamera;

        [Tooltip("Colour to end on. Alpha is ignored; the fade drives it.")]
        public Color background = new Color(0.157f, 0.447f, 0.792f, 1f);

        [Tooltip("Shape of the fade. X is normalized time, Y is how opaque the background is.")]
        public AnimationCurve curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Min(0f)] public float duration = 3f;
        [Min(0f)] public float delay;

        [Tooltip("Ignore Time.timeScale, so a paused or slowed scene still fades.")]
        public bool useUnscaledTime = true;

        [Tooltip("Start fully transparent on Awake, so Passthrough is visible before anything runs.")]
        public bool startTransparent = true;

        [Tooltip("Fade to the solid colour as soon as the scene starts.")]
        public bool playOnAwake;

        [Tooltip("Raised when the background has become fully solid.")]
        public UnityEvent onFadedToVirtual = new UnityEvent();

        [Tooltip("Raised when the background has become fully transparent (Passthrough fully visible).")]
        public UnityEvent onFadedToPassthrough = new UnityEvent();

        [Tooltip("Raised when either fade ends.")]
        public UnityEvent onComplete = new UnityEvent();

        Coroutine _running;

        /// <summary>0 is full Passthrough, 1 is the solid colour.</summary>
        public float Opacity { get; private set; }

        /// <summary>Whether a fade is in progress.</summary>
        public bool IsPlaying => _running != null;

        void Awake()
        {
            var cam = ResolveCamera();
            if (cam == null) return;

            // Solid Colour, not Skybox: a skybox is opaque and would hide Passthrough outright.
            cam.clearFlags = CameraClearFlags.SolidColor;

            if (startTransparent) Apply(0f);
            if (playOnAwake) FadeToVirtual();
        }

        /// <summary>Fade from Passthrough to the solid colour.</summary>
        public void FadeToVirtual() => Play(reverse: false);

        /// <summary>Fade the background back out, returning the scene to Passthrough.</summary>
        public void FadeToPassthrough() => Play(reverse: true);

        /// <inheritdoc cref="FadeToVirtual"/>
        public void Play() => Play(reverse: false);

        /// <inheritdoc cref="FadeToPassthrough"/>
        public void PlayReverse() => Play(reverse: true);

        public void Play(bool reverse)
        {
            if (_running != null) StopCoroutine(_running);
            if (!isActiveAndEnabled)
            {
                // Nothing can run a fade on an inactive object, so finish it at once rather than
                // leaving a sequence waiting for an event that will never come.
                Apply(reverse ? 0f : 1f);
                Finish(reverse);
                return;
            }
            _running = StartCoroutine(Run(reverse));
        }

        public void Stop()
        {
            if (_running == null) return;
            StopCoroutine(_running);
            _running = null;
        }

        /// <summary>Jumps straight to full Passthrough without animating.</summary>
        public void ShowPassthrough()
        {
            Stop();
            Apply(0f);
        }

        /// <summary>Jumps straight to the solid colour without animating.</summary>
        public void HidePassthrough()
        {
            Stop();
            Apply(1f);
        }

        IEnumerator Run(bool reverse)
        {
            float elapsed = -delay;
            while (elapsed < duration)
            {
                elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

                // Held at the starting value through the delay, not jumped to it: a fade that
                // begins with a delay should not change anything until the delay is over.
                if (elapsed > 0f)
                {
                    float t = Mathf.Clamp01(duration > 0f ? elapsed / duration : 1f);
                    Apply(curve.Evaluate(reverse ? 1f - t : t));
                }
                yield return null;
            }

            Apply(curve.Evaluate(reverse ? 0f : 1f));
            _running = null;
            Finish(reverse);
        }

        void Finish(bool reverse)
        {
            if (reverse) onFadedToPassthrough?.Invoke();
            else onFadedToVirtual?.Invoke();
            onComplete?.Invoke();
        }

        Camera ResolveCamera()
        {
            if (targetCamera == null) targetCamera = GetComponentInParent<Camera>();
            if (targetCamera == null) targetCamera = ToolkitRig.UserCamera;
            return targetCamera;
        }

        void Apply(float opacity)
        {
            Opacity = Mathf.Clamp01(opacity);
            var cam = ResolveCamera();
            if (cam == null) return;
            cam.backgroundColor = new Color(
                background.r * Opacity,
                background.g * Opacity,
                background.b * Opacity,
                Opacity);
        }
    }
}
