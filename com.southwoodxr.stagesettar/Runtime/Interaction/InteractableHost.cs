using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace StageSettAR
{
    /// <summary>
    /// Owns the one XR interactable that <see cref="SimpleGrab"/> and <see cref="SimpleGaze"/> share.
    /// Students never see it: it is added and removed by those two components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both components need an interactable on the same object, and which one it is depends on both:
    /// grabbing needs an XRGrabInteractable, gaze alone is happy with an XRSimpleInteractable. Putting
    /// that decision here means the two work together whichever order they were added in, and a
    /// mode change can swap the interactable without either component holding a stale reference.
    /// Neither component subscribes to interactable events for the same reason: they read its
    /// hover and select lists each frame instead, which survives the swap.
    /// </para>
    /// <para>
    /// An interactable the user added by hand is reused, never replaced or destroyed. Only one this
    /// host created ("owned") is rebuilt and removed.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    sealed class InteractableHost : MonoBehaviour
    {
        // How close a hand must be to the object to count as touching it, in metres. A bit over the
        // Near-Far interactor's default near cast radius (0.1 m), which is the reach of a hand.
        const float NearDistance = 0.15f;

        SimpleGrab _grab;
        SimpleGaze _gaze;
        XRBaseInteractable _interactable;
        bool _owned;
        bool _warnedNoCollider;
        bool _warnedNoGrabInteractable;

        // One instance each, added to whichever interactable is current. They read live state, so a
        // mode change never has to touch the filter lists.
        XRSelectFilterDelegate _selectFilter;
        XRHoverFilterDelegate _hoverFilter;

        // Interactables we destroyed this frame. Destroy is deferred, so they would still be found by
        // TryGetComponent and wrongly adopted as the user's own.
        readonly List<XRBaseInteractable> _discarded = new();

        /// <summary>The current interactable, or null until a client has registered.</summary>
        public XRBaseInteractable Interactable => _interactable;

        public static InteractableHost Acquire(GameObject go)
        {
            if (!go.TryGetComponent(out InteractableHost host))
            {
                host = go.AddComponent<InteractableHost>();
                host.hideFlags = HideFlags.HideInInspector;
            }
            return host;
        }

        public void Register(SimpleGrab grab)
        {
            _grab = grab;
            Refresh();
        }

        public void Register(SimpleGaze gaze)
        {
            _gaze = gaze;
            Refresh();
        }

        public void Unregister(SimpleGrab grab)
        {
            if (_grab != grab) return;
            _grab = null;
            Refresh();
        }

        public void Unregister(SimpleGaze gaze)
        {
            if (_gaze != gaze) return;
            _gaze = null;
            Refresh();
        }

        /// <summary>Brings the interactable in line with what the registered components want.</summary>
        public void Refresh()
        {
            if (_grab == null && _gaze == null)
            {
                // The host itself goes in LateUpdate, not now: a component disabled and re-enabled in
                // the same frame would otherwise find a host that is already on its way out.
                Release();
                return;
            }

            bool needGrab = _grab != null && _grab.Mode != GrabMode.NoGrab;

            // Rebuild only what we made. XRGrabInteractable cannot be turned into an
            // XRSimpleInteractable in place, so a switch between grabbing and not destroys it.
            if (_interactable != null && _owned && needGrab != (_interactable is XRGrabInteractable))
                Release();

            if (_interactable == null)
            {
                var existing = FindUserInteractable();
                if (existing != null)
                {
                    _interactable = existing;
                    _owned = false;
                }
                else
                {
                    // Awake runs inside AddComponent and collects the child colliders, so the
                    // colliders must already exist. They do: this runs from the clients' OnEnable.
                    // The interactable's own OnEnable then finds the interaction manager.
                    _interactable = needGrab ? gameObject.AddComponent<XRGrabInteractable>()
                                             : gameObject.AddComponent<XRSimpleInteractable>();
                    _owned = true;
                }
                AddFilters();
            }

            Configure(needGrab);
        }

        void Configure(bool needGrab)
        {
            if (_gaze != null)
            {
                _interactable.allowGazeInteraction = true;
                _interactable.allowGazeSelect = false;
            }
            else if (_owned)
            {
                _interactable.allowGazeInteraction = false;
            }

            if (!needGrab) return;

            if (_interactable is XRGrabInteractable grabbable)
            {
                bool physics = _grab.Mode == GrabMode.GrabWithPhysics;
                grabbable.movementType = physics ? XRBaseInteractable.MovementType.VelocityTracking
                                                 : XRBaseInteractable.MovementType.Instantaneous;
                grabbable.throwOnDetach = physics;
                grabbable.useDynamicAttach = true;
                // Release puts the rigidbody back as it was before the grab (XRI saves useGravity and
                // isKinematic on select). Do not force gravity on, so a floating object stays floating.
                grabbable.forceGravityOnDetach = false;
            }
            else if (!_warnedNoGrabInteractable)
            {
                _warnedNoGrabInteractable = true;
                Debug.LogWarning($"[SimpleGrab] {name}: this object already has an interactable that " +
                                 "is not an XR Grab Interactable, so it cannot be grabbed. Remove it " +
                                 "and Simple Grab will make the right one.", this);
            }
        }

        XRBaseInteractable FindUserInteractable()
        {
            foreach (var candidate in GetComponents<XRBaseInteractable>())
                if (!_discarded.Contains(candidate)) return candidate;
            return null;
        }

        void LateUpdate()
        {
            _discarded.RemoveAll(c => c == null);
            if (_grab == null && _gaze == null) Destroy(this);
        }

        void AddFilters()
        {
            _selectFilter ??= new XRSelectFilterDelegate(CanSelect);
            _hoverFilter ??= new XRHoverFilterDelegate(CanHover);
            _interactable.selectFilters.Add(_selectFilter);
            _interactable.hoverFilters.Add(_hoverFilter);
        }

        // Takes the interactable off: our filters always, the component itself only if we made it.
        void Release()
        {
            if (_interactable == null) return;

            if (_selectFilter != null) _interactable.selectFilters.Remove(_selectFilter);
            if (_hoverFilter != null) _interactable.hoverFilters.Remove(_hoverFilter);

            if (_owned)
            {
                // Destroy waits until the end of the frame. Disabling first unregisters it from the
                // manager now (which also drops any grab in progress), so it cannot linger beside its
                // replacement.
                _interactable.enabled = false;
                _discarded.Add(_interactable);
                Destroy(_interactable);
            }
            _interactable = null;
            _owned = false;
        }

        void OnDestroy() => Release();

        bool CanSelect(IXRSelectInteractor interactor, IXRSelectInteractable _)
        {
            if (interactor is XRGazeInteractor) return false;
            if (_grab == null) return !_owned; // a gaze-only object of ours is never grabbed
            if (_grab.Mode == GrabMode.NoGrab) return false;
            return _grab.GrabFromDistance || IsNear(interactor, _interactable);
        }

        bool CanHover(IXRHoverInteractor interactor, IXRHoverInteractable _)
        {
            // A gaze-only object ignores hands. With a SimpleGrab it must still be touchable.
            if (_owned && _grab == null && _gaze != null) return interactor is XRGazeInteractor;
            return true;
        }

        /// <summary>Warns once, by object name, when there is nothing for the user to point at.</summary>
        public void WarnIfNoCollider(string who)
        {
            if (_warnedNoCollider) return;
            foreach (var col in GetComponentsInChildren<Collider>())
                if (!col.isTrigger) return;
            _warnedNoCollider = true;
            Debug.LogWarning($"[{who}] {name}: no collider on this object or its children, so it " +
                             "can never be reached. Add one (trigger colliders do not count).", this);
        }

        /// <summary>
        /// Whether this interactor is reaching the target with the hand rather than pointing at it
        /// from afar. The one definition used for both touching and distance grabbing.
        /// </summary>
        /// <remarks>
        /// Gaze and ray interactors are always far; direct and poke are always near. The Near-Far
        /// interactor does both, and exposes the region only while it is selecting, so before that
        /// (and for hover) it is judged by how far the target is from its attach point: within a
        /// hand's reach is near.
        /// </remarks>
        public static bool IsNear(IXRInteractor interactor, XRBaseInteractable target)
        {
            if (interactor is XRGazeInteractor) return false;
            if (interactor is NearFarInteractor nearFar)
            {
                if (target != null && nearFar.hasSelection && target.IsSelected(nearFar))
                    return nearFar.selectionRegion.Value == NearFarInteractor.Region.Near;
                return WithinReach(interactor, target);
            }
            if (interactor is XRRayInteractor) return false;
            if (interactor is XRDirectInteractor || interactor is XRPokeInteractor) return true;
            return WithinReach(interactor, target);
        }

        static bool WithinReach(IXRInteractor interactor, XRBaseInteractable target)
        {
            if (target == null) return false;
            var attach = interactor.GetAttachTransform(target);
            if (attach == null) return false;

            var p = attach.position;
            float best = float.MaxValue;
            foreach (var col in target.colliders)
            {
                if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
                // ClosestPoint is not supported on concave mesh colliders; their bounds are close enough.
                var q = col is MeshCollider mesh && !mesh.convex ? col.bounds.ClosestPoint(p) : col.ClosestPoint(p);
                best = Mathf.Min(best, (p - q).sqrMagnitude);
            }
            return best <= NearDistance * NearDistance;
        }
    }
}
