using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// Fades a GameObject and everything under it in or out, without altering its materials.
    ///
    /// Attach, point it at a target if it is not the object itself, and call Play (fade in) or
    /// PlayReverse (fade out). It covers the two cases that actually come up: UI is driven through
    /// CanvasGroup.alpha, and renderers through a MaterialPropertyBlock. Neither instantiates a material or leaves anything behind, so a shared
    /// material stays shared and an object can be faded independently of others using the same one.
    ///
    /// The alternative, a shader that knows how to fade itself, falls apart the moment the target
    /// is a hierarchy, uses a shader you did not write, or is UI. This works on all three because it
    /// only touches values Unity already gives every renderer.
    ///
    /// For sequences: <see cref="FadeIn"/> and <see cref="FadeOut"/> start the fade, and
    /// <see cref="onFadedIn"/>, <see cref="onFadedOut"/> and <see cref="onComplete"/> report when it
    /// ends. Calling them while the object is inactive finishes at once and still raises the events.
    /// </summary>
    [AddComponentMenu("StageSettAR/Object Fade")]
    [DisallowMultipleComponent]
    public sealed class ObjectFade : MonoBehaviour
    {
        public enum Blending
        {
            /// <summary>Guess from the shader. Gsplat backdrops are premultiplied; everything else is not.</summary>
            Auto,

            /// <summary>Scale alpha only. Correct for ordinary transparent materials.</summary>
            StraightAlpha,

            /// <summary>Scale every channel. Correct for premultiplied content like the splat backdrops.</summary>
            Premultiplied
        }

        [Tooltip("What to fade. Defaults to this object and all its children.")]
        public GameObject target;

        public AnimationCurve curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Min(0f)] public float duration = 1f;
        [Min(0f)] public float delay = 0f;
        public bool useUnscaledTime = false;

        [Tooltip("Start invisible, so nothing shows until something plays this.")]
        public bool startHidden = true;

        public bool playOnAwake = false;

        [Tooltip(
            "Block UI interaction while faded out. Without it an invisible panel still swallows " +
            "pointer and gaze events, which is maddening to debug.")]
        public bool blockInteractionWhileHidden = true;

        [Tooltip(
            "Disable colliders while faded out. A CanvasGroup only stops UI raycasts; a mesh that has " +
            "faded to nothing still has a collider, and interactors, gaze and physics raycasts all " +
            "still find it.")]
        public bool disableCollidersWhileHidden = true;

        [Tooltip(
            "Objects that cannot fade, switched off and on instead.\n\n" +
            "Some things have no alpha to drive: an XR composition layer is composited by the runtime " +
            "rather than rendered by Unity, and an opaque material has nothing to fade. Listing them " +
            "here hides them outright at the same moment everything else reaches zero. They are left " +
            "out of the fade entirely, so nothing tries to do both.")]
        public List<GameObject> hardHide = new();

        [Tooltip("How a renderer's colour should be scaled. Auto reads the shader name.")]
        public Blending alphaBlending = Blending.Auto;

        [Tooltip("Raised when the fade in reaches fully visible.")]
        public UnityEvent onFadedIn;

        [Tooltip("Raised when the fade out reaches fully invisible.")]
        public UnityEvent onFadedOut;

        [Tooltip("Raised when either fade ends.")]
        public UnityEvent onComplete;

        // Tried in order. The first one a material actually has is the one driven.
        static readonly int[] k_colourProperties =
        {
            Shader.PropertyToID("_Tint"),       // Gsplat backdrop shells
            Shader.PropertyToID("_BaseColor"),  // URP Lit / Unlit
            Shader.PropertyToID("_Color"),      // built-in, sprites, particles
            Shader.PropertyToID("_FaceColor"),  // TextMeshPro in world space
            Shader.PropertyToID("_TintColor")
        };

        const float k_liveThreshold = 0.001f;

        class Blocked
        {
            public Collider Collider;
            public bool WasEnabled;
        }

        class Faded
        {
            public Renderer Renderer;

            /// <summary>Set when the renderer is a sprite, which is tinted directly rather than by property.</summary>
            public SpriteRenderer Sprite;

            public int Property;
            public Color Original;
            public bool Premultiplied;
        }

        readonly List<Faded> _renderers = new();
        readonly List<CanvasGroup> _groups = new();
        readonly List<Blocked> _colliders = new();
        readonly List<(GameObject Object, bool WasActive)> _hardHidden = new();
        bool _built;
        bool _collidersSuppressed;
        bool _hardHiddenSuppressed;
        MaterialPropertyBlock _block;
        Coroutine _running;

        /// <summary>0 is invisible, 1 is fully present.</summary>
        public float Alpha { get; private set; } = 1f;

        public bool IsPlaying => _running != null;

        void Awake()
        {
            Rebuild();
            Apply(startHidden ? 0f : 1f);
            if (playOnAwake)
                FadeIn();
        }

        void OnDestroy()
        {
            // Property blocks and disabled colliders both outlive the component, leaving objects stuck
            // invisible and untouchable.
            Apply(1f);
            RestoreColliders();
            RestoreHardHidden();
        }

        /// <summary>
        /// Recollects what will be faded. Call after adding, enabling or replacing anything under the
        /// target, backdrop shells created by a bake, for instance, appear after this first runs.
        /// </summary>
        public void Rebuild()
        {
            _block ??= new MaterialPropertyBlock();
            _built = true;

            // Put anything already suppressed back before re-reading, or a rebuild while hidden would
            // record "disabled" as the state to restore to and the colliders would never come back.
            RestoreColliders();
            RestoreHardHidden();

            _renderers.Clear();
            _groups.Clear();
            _colliders.Clear();
            _hardHidden.Clear();

            foreach (var hidden in hardHide)
                if (hidden)
                    _hardHidden.Add((hidden, hidden.activeSelf));

            var root = (target ? target : gameObject).transform;

            // One CanvasGroup on the root, not one per Canvas beneath it.
            //
            // Unity resolves a Graphic's group by walking up the transform hierarchy, and it does not
            // stop at Canvas boundaries, so a single group above everything covers every canvas under
            // the target, including the case this was originally wrong about, where the target sits
            // *inside* a canvas and has none of its own. Per-Canvas groups also multiplied: a canvas
            // nested in another ended up at alpha squared.
            if (root.GetComponentInChildren<CanvasRenderer>(true))
            {
                if (!root.TryGetComponent(out CanvasGroup group))
                    group = root.gameObject.AddComponent<CanvasGroup>();
                _groups.Add(group);
            }

            // Deliberately not skipping renderers that sit under a Canvas. UI elements derive from
            // Graphic rather than Renderer, so they were never in this list to double-handle, while a
            // SpriteRenderer or MeshRenderer parented into a canvas, which is how a logo usually ends
            // up on a panel, is invisible to CanvasGroup and needs driving here.
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (IsHardHidden(renderer.transform))
                    continue;
                // A SpriteRenderer tints through vertex colours, so its material's _Color is ignored
                // and a property block does nothing at all. It has to be driven through its own colour.
                if (renderer is SpriteRenderer sprite)
                {
                    _renderers.Add(new Faded
                    {
                        Renderer = renderer,
                        Sprite = sprite,
                        Original = sprite.color
                    });
                    continue;
                }

                var material = renderer.sharedMaterial;
                if (!material)
                    continue;

                foreach (int property in k_colourProperties)
                {
                    if (!material.HasProperty(property))
                        continue;

                    _renderers.Add(new Faded
                    {
                        Renderer = renderer,
                        Property = property,
                        Original = material.GetColor(property),
                        Premultiplied = IsPremultiplied(material)
                    });
                    break;
                }
            }

            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                // Switching the object off already takes its colliders with it.
                if (IsHardHidden(collider.transform))
                    continue;
                _colliders.Add(new Blocked { Collider = collider, WasEnabled = collider.enabled });
            }
        }

        bool IsHardHidden(Transform candidate)
        {
            foreach ((GameObject hidden, bool _) in _hardHidden)
                if (hidden && candidate.IsChildOf(hidden.transform))
                    return true;
            return false;
        }

        /// <summary>Re-activates only the objects this component switched off.</summary>
        void RestoreHardHidden()
        {
            if (!_hardHiddenSuppressed)
                return;

            foreach ((GameObject hidden, bool wasActive) in _hardHidden)
                if (hidden)
                    hidden.SetActive(wasActive);
            _hardHiddenSuppressed = false;
        }

        /// <summary>Re-enables only the colliders this component turned off.</summary>
        void RestoreColliders()
        {
            if (!_collidersSuppressed)
                return;

            foreach (var blocked in _colliders)
                if (blocked.Collider)
                    blocked.Collider.enabled = blocked.WasEnabled;
            _collidersSuppressed = false;
        }

        bool IsPremultiplied(Material material) => alphaBlending switch
        {
            Blending.Premultiplied => true,
            Blending.StraightAlpha => false,
            // Premultiplied content carries colour already scaled by its own coverage, so fading it
            // means scaling all four channels. Doing that to a straight-alpha material would darken it
            // toward black as it faded rather than letting it become transparent.
            _ => material.shader && material.shader.name.StartsWith("Gsplat/")
        };

        /// <summary>Fades in, from hidden to fully present.</summary>
        public void FadeIn() => Play(reverse: false);

        /// <summary>Fades out, from present to hidden.</summary>
        public void FadeOut() => Play(reverse: true);

        /// <inheritdoc cref="FadeIn"/>
        public void Play() => Play(reverse: false);

        /// <inheritdoc cref="FadeOut"/>
        public void PlayReverse() => Play(reverse: true);

        public void Play(bool reverse)
        {
            if (_running != null)
                StopCoroutine(_running);
            _running = null;

            if (!isActiveAndEnabled)
            {
                // A coroutine cannot run on an inactive object. Jump to the end state instead of
                // silently doing nothing, which would leave a sequence waiting forever.
            if (!_built) Rebuild();
                Apply(Curve(reverse ? 0f : 1f));
                Finish(reverse);
                return;
            }
            _running = StartCoroutine(Run(reverse));
        }

        float Curve(float t) => curve.Evaluate(t);

        void Finish(bool reverse)
        {
            if (reverse) onFadedOut?.Invoke();
            else onFadedIn?.Invoke();
            onComplete?.Invoke();
        }

        public void Stop()
        {
            if (_running == null)
                return;
            StopCoroutine(_running);
            _running = null;
        }

        public void Show()
        {
            Stop();
            Apply(1f);
        }

        public void Hide()
        {
            Stop();
            Apply(0f);
        }

        IEnumerator Run(bool reverse)
        {
            float elapsed = -delay;
            while (elapsed < duration)
            {
                elapsed += useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

                // Nothing changes during the delay; the fade starts from wherever it was.
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

        void Apply(float alpha)
        {
            Alpha = Mathf.Clamp01(alpha);

            foreach (var group in _groups)
            {
                if (!group)
                    continue;
                group.alpha = Alpha;
                if (blockInteractionWhileHidden)
                {
                    group.interactable = Alpha > k_liveThreshold;
                    group.blocksRaycasts = Alpha > k_liveThreshold;
                }
            }

            if (disableCollidersWhileHidden)
            {
                // Only written on the transition, so this never fights anything else toggling a
                // collider while the object is visible.
                bool live = Alpha > k_liveThreshold;
                if (!live && !_collidersSuppressed)
                {
                    foreach (var blocked in _colliders)
                        if (blocked.Collider)
                            blocked.Collider.enabled = false;
                    _collidersSuppressed = true;
                }
                else if (live)
                {
                    RestoreColliders();
                }
            }

            if (_hardHidden.Count > 0)
            {
                bool live = Alpha > k_liveThreshold;
                if (!live && !_hardHiddenSuppressed)
                {
                    foreach ((GameObject hidden, bool _) in _hardHidden)
                        if (hidden)
                            hidden.SetActive(false);
                    _hardHiddenSuppressed = true;
                }
                else if (live)
                {
                    RestoreHardHidden();
                }
            }

            foreach (var faded in _renderers)
            {
                if (!faded.Renderer)
                    continue;

                if (faded.Sprite)
                {
                    Color tint = faded.Original;
                    faded.Sprite.color = new Color(tint.r, tint.g, tint.b, tint.a * Alpha);
                    continue;
                }

                Color colour = faded.Original;
                colour = faded.Premultiplied
                    ? colour * Alpha
                    : new Color(colour.r, colour.g, colour.b, colour.a * Alpha);

                faded.Renderer.GetPropertyBlock(_block);
                _block.SetColor(faded.Property, colour);
                faded.Renderer.SetPropertyBlock(_block);
            }
        }
    }
}
