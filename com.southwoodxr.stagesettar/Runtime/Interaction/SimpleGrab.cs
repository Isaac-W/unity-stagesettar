#pragma warning disable CS0649 // serialized fields are filled by Unity
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace StageSettAR
{
    /// <summary>How a <see cref="SimpleGrab"/> object responds to the user's hands.</summary>
    public enum GrabMode
    {
        [InspectorName("Grabbing + Physics")] GrabWithPhysics,
        [InspectorName("Grabbing Only")] GrabOnly,
        [InspectorName("No Grabbing")] NoGrab,
    }

    /// <summary>
    /// "The user can pick this up (or just touch it), and I get told when they do."
    /// </summary>
    /// <remarks>
    /// <para>
    /// Put it on an object with a collider. It adds the XR Grab Interactable (or Simple Interactable
    /// for No Grabbing) itself when the scene starts, so none of the XR Interaction Toolkit settings
    /// appear and removing this component removes all of it. An interactable you already added by
    /// hand is used as it is.
    /// </para>
    /// <para>
    /// Grabbing + Physics lets go with the hand's velocity, so the object can be thrown, and it
    /// falls or floats as its Rigidbody says: gravity is whatever you set, restored on release.
    /// Grabbing Only holds the object rigidly with no throw. No Grabbing keeps touch events but the
    /// hands can never take hold.
    /// </para>
    /// <para>
    /// Touching means a hand is within reach of the object (so pointing a ray at it from across the
    /// room is not a touch, and neither is looking at it). With Grab From A Distance off, a ray
    /// cannot select the object either; the user has to reach for it.
    /// </para>
    /// <para>
    /// It needs the rig's interactors and an XR Interaction Manager (the StageSettAR rig
    /// has both). Hands only poke an object that also has an XR Poke Filter.
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/Simple Grab")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class SimpleGrab : MonoBehaviour
    {
        [Tooltip("Grabbing + Physics: can be thrown, keeps its gravity. Grabbing Only: held rigidly, " +
                 "no throw. No Grabbing: touch events only.")]
        [SerializeField] GrabMode mode = GrabMode.GrabWithPhysics;

        [Tooltip("Off (default): the user must reach out and take it in their hand. On: they can also " +
                 "pick it up by pointing at it from afar.")]
        [SerializeField] bool grabFromDistance;

        [Tooltip("A hand has just come within reach of this object.")]
        public UnityEvent onTouchStart;
        [Tooltip("The last hand has moved away from this object.")]
        public UnityEvent onTouchEnd;
        [Tooltip("The user has just picked this up.")]
        public UnityEvent onGrabStart;
        [Tooltip("The user has let go of this.")]
        public UnityEvent onGrabEnd;

        InteractableHost _host;
        bool _touching;
        bool _grabbing;

        /// <summary>How the object responds to hands. Changing it takes effect immediately.</summary>
        public GrabMode Mode
        {
            get => mode;
            set
            {
                mode = value;
                if (_host != null) _host.Refresh();
            }
        }

        /// <summary>Whether the user may pick it up by pointing from afar. Takes effect immediately.</summary>
        public bool GrabFromDistance
        {
            get => grabFromDistance;
            set => grabFromDistance = value; // read live by the select filter
        }

        /// <summary>Whether a hand is within reach right now.</summary>
        public bool IsTouched => _touching;

        /// <summary>Whether the user is holding it right now.</summary>
        public bool IsGrabbed => _grabbing;

        void OnEnable()
        {
            _host = InteractableHost.Acquire(gameObject);
            _host.WarnIfNoCollider(nameof(SimpleGrab));
            _host.Register(this);
        }

        void OnDisable()
        {
            // Anything switched on by a Start event would otherwise stay on forever.
            EndTouch();
            EndGrab();
            if (_host != null) _host.Unregister(this);
            _host = null;
        }

        void OnValidate()
        {
            // Mode edited in the inspector while playing.
            if (Application.isPlaying && _host != null) _host.Refresh();
        }

        void Update()
        {
            // Polled rather than subscribed: the interactable can be swapped for another when the mode
            // changes, and a far hand can become a near one without leaving the hover.
            var target = _host != null ? _host.Interactable : null;
            bool touching = target != null && CountNear(target) > 0;
            bool grabbing = target != null && target.isSelected;

            if (touching && !_touching) { _touching = true; onTouchStart?.Invoke(); }
            else if (!touching) EndTouch();

            if (grabbing && !_grabbing) { _grabbing = true; onGrabStart?.Invoke(); }
            else if (!grabbing) EndGrab();
        }

        void EndTouch()
        {
            if (!_touching) return;
            _touching = false;
            onTouchEnd?.Invoke();
        }

        void EndGrab()
        {
            if (!_grabbing) return;
            _grabbing = false;
            onGrabEnd?.Invoke();
        }

        static int CountNear(XRBaseInteractable target)
        {
            int count = 0;
            var hovering = target.interactorsHovering;
            for (int i = 0; i < hovering.Count; i++)
                if (InteractableHost.IsNear(hovering[i], target)) count++;

            // A hand holding the object counts even if it is not listed as hovering it.
            var selecting = target.interactorsSelecting;
            for (int i = 0; i < selecting.Count; i++)
            {
                var s = selecting[i];
                if (s is IXRHoverInteractor h && target.IsHovered(h)) continue;
                if (InteractableHost.IsNear(s, target)) count++;
            }
            return count;
        }
    }
}
