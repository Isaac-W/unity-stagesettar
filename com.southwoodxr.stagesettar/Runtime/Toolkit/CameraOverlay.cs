using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// Shared machinery for CameraFade and CameraCircleWipe. Not attached directly: add one of those.
    /// </summary>
    /// <remarks>
    /// Builds a small quad, parents it to the camera, and keeps it filling the view. Everything the
    /// two effects do is a matter of driving the material's colour and hole radius.
    /// <para>
    /// Play from a UnityEvent with <see cref="Cover()"/> and <see cref="Clear()"/>, or from code with
    /// the overloads that take a duration. <see cref="onCovered"/> fires when the view has become fully
    /// covered, <see cref="onCleared"/> when it has become fully clear, and <see cref="onComplete"/>
    /// after either, so a sequence can chain on whichever it needs.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public abstract class CameraOverlay : MonoBehaviour
    {
        [Tooltip("The camera to cover. Found automatically (the user's camera) when empty.")]
        public Camera targetCamera;

        [Tooltip("The colour faded to. Its alpha is the fully-covered opacity, so leave it at 1 " +
                 "unless you want the fade to stop short of solid.")]
        public Color color = Color.black;

        [Tooltip("Seconds the effect takes by default, when triggered from a UnityEvent that " +
                 "cannot pass a duration.")]
        public float duration = 0.5f;

        [Tooltip("Start fully covered, so the scene can fade in from black on load.")]
        public bool startCovered = false;

        [Header("Overlay")]
        [Tooltip("How far in front of the camera the quad sits, in metres. Close enough to be " +
                 "unobstructed, far enough to clear the near clip plane. 0.3 suits any headset.")]
        public float distance = 0.3f;

        [Tooltip("How much larger than the computed field of view to make the quad. A headset's " +
                 "real projection is wider and less symmetric than Camera.fieldOfView reports, " +
                 "so there is margin here on purpose, a fade with a visible edge is a bug.")]
        [Range(1f, 4f)] public float coverage = 2f;

        [Tooltip("Render queue. 4000 is Unity's Overlay queue and already draws after ordinary " +
                 "transparent geometry; raise it if something still gets in front.")]
        public int renderQueue = 4000;

        [Tooltip("Sorting order, which is what decides the winner against sprites and world-space " +
                 "UI, those sort by this before depth is considered, so a plain render queue is " +
                 "not enough on its own. Maxed out by default. If the thing in front sits on a " +
                 "higher Sorting LAYER, put that layer's name below as well.")]
        public int sortingOrder = 32767;

        [Tooltip("Sorting layer to sort within. Leave empty for Default. Set this to match the " +
                 "layer of whatever is drawing over the fade.")]
        public string sortingLayer = "";

        [Tooltip("The overlay shader. Assigned automatically; the package keeps a copy in a " +
                 "Resources folder so a build cannot strip it.")]
        public Shader overlayShader;

        [Tooltip("Raised when the view has become fully covered.")]
        public UnityEvent onCovered;

        [Tooltip("Raised when the view has become fully clear.")]
        public UnityEvent onCleared;

        [Tooltip("Raised when either of the above happens.")]
        public UnityEvent onComplete;

        // The quad, its renderer and its material. Built once, on demand.
        private Transform quad;
        private MeshRenderer quadRenderer;
        private Material material;

        // 0 = clear, 1 = fully covered. What this means visually is up to the subclass.
        private float progress;
        private bool running;
        private bool initialised;
        private float from;
        private float to;
        private float elapsed;
        private float span;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int RadiusId = Shader.PropertyToID("_Radius");
        private static readonly int SoftnessId = Shader.PropertyToID("_Softness");

        /// <summary>How covered the view is: 0 clear, 1 fully covered.</summary>
        public float Progress => progress;

        /// <summary>Whether an effect is playing.</summary>
        public bool IsPlaying => running;

        /// <summary>Push <paramref name="amount"/> (0 clear, 1 covered) into the material.</summary>
        protected abstract void Draw(Material target, Color tint, float amount);

        const string ShaderResource = "StageSettARCameraOverlay";

        protected virtual void Reset()
        {
            overlayShader = Resources.Load<Shader>(ShaderResource);
        }

        protected virtual void OnEnable()
        {
            // Only the first enable applies Start Covered; turning the component back on later keeps
            // whatever state the view was left in rather than snapping back to black, and carries on
            // with a fade that was playing, so its events still fire.
            if (!initialised) progress = startCovered ? 1f : 0f;
            initialised = true;
            Refresh();
        }

        protected virtual void OnDisable()
        {
            // The quad is a separate object and would otherwise stay on the lens after this is
            // switched off. It comes back from Refresh when the component is enabled again.
            if (quadRenderer != null) quadRenderer.enabled = false;
        }

        protected virtual void OnDestroy()
        {
            // Materials made at runtime are not collected with the GameObject.
            if (material != null) Destroy(material);
            if (quad != null) Destroy(quad.gameObject);
        }

        /// <summary>Cover the view over <paramref name="seconds"/>.</summary>
        public void Cover(float seconds) => Play(1f, seconds);

        /// <summary>Clear the view over <paramref name="seconds"/>.</summary>
        public void Clear(float seconds) => Play(0f, seconds);

        /// <summary>Cover the view over the configured duration. For UnityEvents.</summary>
        public void Cover() => Play(1f, duration);

        /// <summary>Clear the view over the configured duration. For UnityEvents.</summary>
        public void Clear() => Play(0f, duration);

        /// <summary>Jump straight to covered or clear, with no animation.</summary>
        public void Set(bool covered)
        {
            running = false;
            progress = covered ? 1f : 0f;
            Refresh();
        }

        /// <summary>Animate to covered (1) or clear (0) over <paramref name="seconds"/>.</summary>
        public void Play(float target, float seconds)
        {
            from = progress;
            to = Mathf.Clamp01(target);
            span = Mathf.Max(0f, seconds);
            elapsed = 0f;

            if (span <= 0f)
            {
                progress = to;
                running = false;
                Refresh();
                Finish();
                return;
            }
            running = true;
        }

        protected virtual void LateUpdate()
        {
            // Late, not Update: the camera has been moved by tracking by now, so the quad lands
            // where the head actually is rather than a frame behind it.
            if (running)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / span);

                // Smoothed rather than linear. A linear fade starts and stops abruptly, and the
                // stop is the part that gets noticed.
                progress = Mathf.Lerp(from, to, t * t * (3f - 2f * t));

                if (t >= 1f)
                {
                    progress = to;
                    running = false;
                    Refresh();
                    Finish();
                    return;
                }
            }
            Refresh();
        }

        private void Finish()
        {
            if (progress >= 1f) onCovered?.Invoke();
            else if (progress <= 0f) onCleared?.Invoke();
            onComplete?.Invoke();
        }

        /// <summary>Put the quad where it belongs and draw the current state onto it.</summary>
        private void Refresh()
        {
            Camera cam = ResolveCamera();
            if (cam == null) return;
            if (!Build(cam)) return;

            // Nothing to draw and nothing to pay for. Also stops a fully clear overlay from
            // costing a transparent full-screen quad every frame.
            bool visible = progress > 0.0001f;
            if (quadRenderer.enabled != visible) quadRenderer.enabled = visible;
            if (!visible) return;

            // Sized to the camera each frame, so a changed field of view or a switch to a
            // different camera cannot leave a gap at the edges.
            float halfHeight = distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float half = halfHeight * Mathf.Max(cam.aspect, 1f) * coverage;

            // Re-applied every frame rather than once at build: these are the knobs reached for
            // when something is drawing over the fade, and tweaking them in play mode should show
            // its effect immediately.
            if (material.renderQueue != renderQueue) material.renderQueue = renderQueue;
            if (quadRenderer.sortingOrder != sortingOrder) quadRenderer.sortingOrder = sortingOrder;
            if (quadRenderer.sortingLayerName != Layer()) quadRenderer.sortingLayerName = Layer();

            // Square on purpose: the hole stays a circle rather than an ellipse, and the quad is
            // oversized anyway so the extra never shows.
            quad.localPosition = new Vector3(0f, 0f, distance);
            quad.localRotation = Quaternion.identity;
            quad.localScale = new Vector3(half * 2f, half * 2f, 1f);

            Draw(material, color, progress);
        }

        private bool Build(Camera cam)
        {
            if (quad != null)
            {
                // Follow the camera if it has been swapped out from under us.
                if (quad.parent != cam.transform) quad.SetParent(cam.transform, false);
                return true;
            }

            Shader shader = overlayShader != null ? overlayShader : Resources.Load<Shader>(ShaderResource);
            if (shader == null)
            {
                Debug.LogError($"[{GetType().Name}] {name}: the overlay shader is missing. Assign " +
                               "Overlay Shader to StageSettAR/CameraOverlay.", this);
                enabled = false;
                return false;
            }

            material = new Material(shader) { name = "CameraOverlay (runtime)" };

            var go = new GameObject($"{GetType().Name} Overlay")
            {
                // Not saved into the scene: it is rebuilt on enable, and a stale copy left
                // behind by a crash would sit in front of the camera forever.
                hideFlags = HideFlags.DontSave,
            };
            quad = go.transform;
            quad.SetParent(cam.transform, false);

            go.AddComponent<MeshFilter>().sharedMesh = Quad();
            quadRenderer = go.AddComponent<MeshRenderer>();
            quadRenderer.sharedMaterial = material;
            quadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            quadRenderer.receiveShadows = false;
            quadRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            quadRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            quadRenderer.enabled = false;
            material.renderQueue = renderQueue;
            quadRenderer.sortingOrder = sortingOrder;
            quadRenderer.sortingLayerName = Layer();
            return true;
        }

        /// <summary>A unit quad facing the camera, built rather than referenced.</summary>
        private static Mesh Quad()
        {
            var mesh = new Mesh { name = "CameraOverlay Quad" };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            });
            mesh.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            // Generous, so the quad is never culled by a bounds test while it sits on the lens.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);
            return mesh;
        }

        private string Layer() => string.IsNullOrEmpty(sortingLayer) ? "Default" : sortingLayer;

        private Camera ResolveCamera()
        {
            if (targetCamera != null) return targetCamera;
            targetCamera = ToolkitRig.UserCamera;
            return targetCamera;
        }

        protected static void Write(Material target, Color tint, float radius, float softness)
        {
            target.SetColor(ColorId, tint);
            target.SetFloat(RadiusId, radius);
            target.SetFloat(SoftnessId, softness);
        }
    }
}
