using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using static StageSettAR.StarLocalizer;

namespace StageSettAR
{
    /// <summary>
    /// A small panel that tells the user what the STAR Localizer is doing: looking for the room, found it,
    /// lost it, or gave up. With the Adaptive mode the panel goes away as soon as the map is first placed, since the
    /// search is short and the later refinement needs no message.
    /// </summary>
    /// <remarks>
    /// The panel floats a little below eye level and follows the user's view softly (it uses
    /// <see cref="TagAlong"/>), so the real room stays visible. It finds the STAR Localizer in the scene
    /// by itself, also when one is loaded or created later. With no localizer it stays hidden, so a scene
    /// without a map is not affected. The StageSettAR Rig has one ready-made under its root; the
    /// parts (canvas group, text, bar) are filled in by the menu builder, or by hand.
    /// </remarks>
    [AddComponentMenu("StageSettAR/Localizing Screen")]
    [RequireComponent(typeof(TagAlong))]
    public sealed class LocalizingScreen : MonoBehaviour
    {
        [Header("Words")]
        public string searchingText = "Finding the room… look around slowly";
        public string foundText = "Room found";
        public string lostText = "Lost the room, look around";
        public string failedText = "Couldn't find this room. Are you in the right place?";

        [Header("Timing")]
        [Tooltip("How long 'Room found' stays before the panel fades away.")]
        public float foundSeconds = 1.5f;
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
        [Tooltip("The whole progress bar (background and fill). Hidden when no search is running.")]
        public GameObject bar;
        [Tooltip("The part of the bar that fills; its width follows the search progress.")]
        public RectTransform barFill;

        const float FindEverySeconds = 1f;

        Canvas _canvas;
        LocalizerState _shown = LocalizerState.Idle;
        bool _hadLocalizer;
        float _nextFind, _holdUntil, _retryAt, _fill;

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
                // The first time a localizer is seen, only an ongoing search or a failure is worth showing;
                // a room that was found before this screen existed needs no message.
                if (state != _shown || !_hadLocalizer) Enter(state, firstSight: !_hadLocalizer);
                _hadLocalizer = true;
                target = TargetAlpha(state);
                if (state == LocalizerState.Failed && retryAfterSeconds > 0f && Time.unscaledTime >= _retryAt)
                {
                    _retryAt = Time.unscaledTime + retryAfterSeconds; // in case Relocalize cannot start
                    localizer.Relocalize();
                }
                UpdateBar(state);
            }
            else _hadLocalizer = false;

            float step = fadeSeconds > 0f ? Time.unscaledDeltaTime / fadeSeconds : 1f;
            SetAlpha(Mathf.MoveTowards(group.alpha, target, step));
        }

        void Enter(LocalizerState state, bool firstSight)
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
                case LocalizerState.Localized:
                    label.text = foundText;
                    _holdUntil = firstSight || localizer.IsAdaptive ? 0f : Time.unscaledTime + foundSeconds;
                    break;
            }
            if (state != LocalizerState.Initializing && state != LocalizerState.Lost) _fill = 0f;
        }

        float TargetAlpha(LocalizerState state)
        {
            switch (state)
            {
                case LocalizerState.Initializing:
                case LocalizerState.Lost:
                case LocalizerState.Failed: return 1f;
                case LocalizerState.Localized: return Time.unscaledTime < _holdUntil ? 1f : 0f;
                default: return 0f;
            }
        }

        void UpdateBar(LocalizerState state)
        {
            bool searching = state == LocalizerState.Initializing || state == LocalizerState.Lost;
            // Adaptive finds the room at the first fix, so there is no progress to show.
            bool showBar = searching && !localizer.IsAdaptive;
            if (bar != null && bar.activeSelf != showBar) bar.SetActive(showBar);
            if (!showBar || barFill == null) return;
            // Eased, because the real progress moves in steps as each fix arrives.
            _fill = Mathf.MoveTowards(_fill, localizer.SearchProgress, Time.unscaledDeltaTime * 1.5f);
            barFill.anchorMax = new Vector2(_fill, 1f);
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
