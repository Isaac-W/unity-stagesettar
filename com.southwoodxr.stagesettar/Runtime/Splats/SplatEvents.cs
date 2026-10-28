using System;
using Gsplat;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace StageSettAR
{
    /// <summary>
    /// Tells you when a splat is loaded and when it is actually on screen, implemented from outside
    /// <see cref="GsplatRenderer"/> (stock UnitySplats has no such events). Replaces the fork's
    /// OnUploadComplete / OnFirstRendered.
    ///
    /// Uploaded: every splat is resident on the GPU (<c>SplatCount</c> reached the asset's count).
    /// With Async Upload this can be many frames after the asset is assigned.
    /// First render: the renderer had splats to draw and a camera has finished rendering since.
    /// That is the one to hang a reveal on. Both are raised from Update, never from inside a
    /// render callback, so listeners are free to change the scene.
    ///
    /// From code: <see cref="IsUploaded"/> / <see cref="HasRendered"/>, or
    /// <see cref="WhenRendered"/> to run something once, immediately if it has already happened.
    /// </summary>
    [AddComponentMenu("StageSettAR/Splats/Splat Events")]
    public class SplatEvents : MonoBehaviour
    {
        [Tooltip("Splat to watch. Found automatically on this object, its parents or its children when left empty.")]
        public GsplatRenderer Renderer;

        [Tooltip("Every splat is resident on the GPU.")]
        public UnityEvent onUploaded;

        [Tooltip("The splats have been drawn at least once, so they are on screen rather than merely loaded. Hang a reveal on this.")]
        public UnityEvent onFirstRender;

        public bool IsUploaded { get; private set; }
        public bool HasRendered { get; private set; }

        bool m_drawable;
        bool m_cameraFinished;
        event Action m_renderedOnce;

        void Reset() => Renderer = ResolveRenderer();

        void OnEnable() => RenderPipelineManager.endCameraRendering += OnEndCamera;

        void OnDisable() => RenderPipelineManager.endCameraRendering -= OnEndCamera;

        public GsplatRenderer ResolveRenderer()
        {
            if (Renderer)
                return Renderer;
            if (TryGetComponent(out GsplatRenderer here))
                return here;
            var found = GetComponentInParent<GsplatRenderer>();
            return found ? found : GetComponentInChildren<GsplatRenderer>();
        }

        /// <summary>Runs <paramref name="action"/> once the splat has rendered; now, if it already has.</summary>
        public void WhenRendered(Action action)
        {
            if (HasRendered)
                action?.Invoke();
            else
                m_renderedOnce += action;
        }

        void OnEndCamera(ScriptableRenderContext context, Camera camera)
        {
            // Only noted here: the draw was submitted before rendering, so a camera finishing
            // after the renderer became drawable has put the splats on screen.
            if (m_drawable)
                m_cameraFinished = true;
        }

        void Update()
        {
            var renderer = ResolveRenderer();
            if (!renderer || !renderer.GsplatAsset)
            {
                IsUploaded = false;
                HasRendered = false;
                m_drawable = false;
                m_cameraFinished = false;
                return;
            }

            // A new asset loads and appears again, so the events are worth firing again.
            if (renderer.GsplatAsset.SplatCount == 0)
                return;

            bool uploaded = renderer.SplatCount == renderer.GsplatAsset.SplatCount;
            if (uploaded && !IsUploaded)
            {
                IsUploaded = true;
                onUploaded?.Invoke();
            }
            else if (!uploaded)
                IsUploaded = false;

            if (HasRendered)
                return;

            m_drawable = renderer.isActiveAndEnabled && renderer.Valid && renderer.ResidentActiveSplatCount > 0;
            if (m_drawable && m_cameraFinished)
            {
                HasRendered = true;
                onFirstRender?.Invoke();
                var queued = m_renderedOnce;
                m_renderedOnce = null;
                queued?.Invoke();
            }
        }
    }
}
