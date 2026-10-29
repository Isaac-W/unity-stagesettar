using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using static StageSettAR.StarLocalizer;

namespace StageSettAR
{
    /// <summary>
    /// A small panel that tells the user what the STAR Localizer is doing: looking for the room (the Startup phase
    /// before the first placement), lost tracking, or gave up. It goes away as soon as the map is shown, since the
    /// later refinement needs no message.
    /// </summary>
    /// <remarks>
    /// The panel floats a little below eye level and follows the user's view softly (it uses
    /// <see cref="TagAlong"/>), so the real room stays visible. It finds the STAR Localizer in the scene
    /// by itself, also when one is loaded or created later. With no localizer it stays hidden, so a scene
    /// without a map is not affected. The StageSettAR Rig has one ready-made under its root; the
    /// parts (canvas group, text) are filled in by the menu builder, or by hand.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Localizing Screen")]
    [RequireComponent(typeof(TagAlong))]
    public sealed class LocalizingScreen : MonoBehaviour
    {
        [Header("Words")]
        public string searchingText = "Finding the room… look around slowly";
        public string lostText = "Lost tracking, look around";
        public string failedText = "Couldn't find this room. Are you in the right place?";

        [Header("Timing")]
        [Tooltip("Seconds the panel takes to fade in or out.")]
        public float fadeSeconds = 0.4f;
        [Tooltip("After the localizer gives up, search again by itself after this many seconds. 0 means never.")]
        public float retryAfterSeconds = 5f;

        [Header("Parts")]
        [Tooltip("The STAR Localizer to show. Found automatically when empty.")]
        public StarLocalizer localizer;
        [Tooltip("Fades the whole panel. Needed.")]
        public CanvasGroup group;
        [Tooltip("The message. Needed.")]
        public TMP_Text label;

        const float FindEverySeconds = 1f;

        Canvas _canvas;
        LocalizerState _shown = LocalizerState.Idle;
        bool _hadLocalizer;
        float _nextFind, _retryAt;

        void Reset() => ApplyDefaultPlacement();

        /// <summary>Puts the panel about 1 m ahead and a little below eye level, turned so canvas text reads from the front.</summary>
        public void ApplyDefaultPlacement()
        {
            var tag = GetComponent<TagAlong>();
            tag.useInitialOffset = false;
            tag.offset = new Vector3(0f, -0.25f, 1f);
            tag.flipForward = true;
            tag.fixedHeight = false;
        }

        void OnEnable()
        {
            _canvas = GetComponent<Canvas>();
            SceneManager.sceneLoaded += OnSceneLoaded;
            _nextFind = 0f;
            SetAlpha(0f);
        }

        void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _nextFind = 0f;

        void Update()
        {
            if (group == null || label == null) return;
            if (localizer == null && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + FindEverySeconds;
                localizer = FindFirstObjectByType<StarLocalizer>();
            }

            float target = 0f;
            if (localizer != null)
            {
                var state = localizer.State;
                if (state != _shown || !_hadLocalizer) Enter(state);
                _hadLocalizer = true;
                target = TargetAlpha(state);
                if (state == LocalizerState.Failed && retryAfterSeconds > 0f && Time.unscaledTime >= _retryAt)
                {
                    _retryAt = Time.unscaledTime + retryAfterSeconds; // in case Relocalize cannot start
                    localizer.Relocalize();
                }
            }
            else _hadLocalizer = false;

            float step = fadeSeconds > 0f ? Time.unscaledDeltaTime / fadeSeconds : 1f;
            SetAlpha(Mathf.MoveTowards(group.alpha, target, step));
        }

        void Enter(LocalizerState state)
        {
            _shown = state;
            switch (state)
            {
                case LocalizerState.Initializing: label.text = searchingText; break;
                case LocalizerState.Lost: label.text = lostText; break;
                case LocalizerState.Failed:
                    label.text = failedText;
                    _retryAt = Time.unscaledTime + retryAfterSeconds;
                    break;
            }
        }

        float TargetAlpha(LocalizerState state)
        {
            switch (state)
            {
                // A map that is already shown (after Relocalize) needs no message while it is looked for again.
                case LocalizerState.Initializing: return localizer.CurrentAlignment.valid ? 0f : 1f;
                case LocalizerState.Lost:
                case LocalizerState.Failed: return 1f;
                default: return 0f;
            }
        }

        void SetAlpha(float alpha)
        {
            if (group == null) return;
            group.alpha = alpha;
            // A hidden canvas costs no drawing, and a world-space panel should never catch pointer rays.
            group.blocksRaycasts = false;
            group.interactable = false;
            if (_canvas != null) _canvas.enabled = alpha > 0.001f;
        }
    }
}
