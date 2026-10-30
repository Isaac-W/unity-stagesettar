#pragma warning disable CS0649 // serialized fields are filled by Unity
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace StageSettAR
{
    /// <summary>
    /// "When the user looks at this, do Y. When they look away, do Z", through the rig's gaze interactor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Put it on an object with a collider. It adds an XR Simple Interactable itself when the scene
    /// starts, set to react to gaze and never to select, and ignores hands unless the object also has
    /// a <see cref="SimpleGrab"/> (the two share one interactable, in either order).
    /// </para>
    /// <para>
    /// On a Quest 3 without eye tracking the XR Gaze Interactor follows the head instead, so the user
    /// looks by turning their head. That is the rig's gaze pose source, not a setting here. For gaze
    /// that needs no XR Interactor at all (the XR Device Simulator, say), use On Gaze Action.
    /// </para>
    /// <para>
    /// It needs the XR Gaze Interactor and an XR Interaction Manager from the StageSettAR rig
    /// (built from the XR Interaction Toolkit samples).
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/Simple Gaze")]
    [DisallowMultipleComponent]
    public sealed class SimpleGaze : MonoBehaviour
    {
        [Tooltip("The user has just started looking at this.")]
        public UnityEvent onGazeStart = new UnityEvent();
        [Tooltip("The last gaze has moved off this.")]
        public UnityEvent onGazeEnd = new UnityEvent();

        InteractableHost _host;
        bool _gazing;

        /// <summary>Whether the user is looking at it right now.</summary>
        public bool IsGazedAt => _gazing;

        void OnEnable()
        {
            _host = InteractableHost.Acquire(gameObject);
            _host.WarnIfNoCollider(nameof(SimpleGaze));
            _host.Register(this);
        }

        void OnDisable()
        {
            // Anything switched on by Gaze Start would otherwise stay on forever.
            EndGaze();
            if (_host != null) _host.Unregister(this);
            _host = null;
        }

        void Update()
        {
            // Polled rather than subscribed, so a SimpleGrab swapping the interactable cannot strand us.
            var target = _host != null ? _host.Interactable : null;
            bool gazing = false;
            if (target != null)
            {
                var hovering = target.interactorsHovering;
                for (int i = 0; i < hovering.Count && !gazing; i++)
                    gazing = hovering[i] is XRGazeInteractor;
            }

            if (gazing && !_gazing) { _gazing = true; onGazeStart?.Invoke(); }
            else if (!gazing) EndGaze();
        }

        void EndGaze()
        {
            if (!_gazing) return;
            _gazing = false;
            onGazeEnd?.Invoke();
        }
    }
}
