using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// "When the user walks into this zone (or touches this object), do Y."
    /// </summary>
    /// <remarks>
    /// <para>
    /// Put it on an object with a collider set to Is Trigger. It only reacts to objects tagged
    /// <see cref="triggerTagName"/>, and the rig's Head carries the MainCamera tag, so the default means
    /// "the user's head". Clear the tag to react to anything.
    /// </para>
    /// <para>
    /// On Enter fires when the first matching collider arrives and On Exit when the last one leaves,
    /// so a user with two colliders does not fire it twice. The same events also fire for solid
    /// collisions (a collider that is not a trigger), which replaces the old Collide Action. For
    /// collisions at least one of the two objects needs a Rigidbody.
    /// </para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/On Trigger Action")]
    [DisallowMultipleComponent]
    public sealed class OnTriggerAction : MonoBehaviour
    {
        [Tooltip("Only objects with this tag count. MainCamera is the user's head on the StageSettAR rig. Empty means anything.")]
        public string triggerTagName = ToolkitRig.UserTag;

        [Tooltip("Fire On Enter only the first time. On Exit still fires.")]
        public bool enterOnce;

        [Tooltip("Draw the zone in the Scene view. Gizmos never show in the headset.")]
        public bool showGizmo = true;

        public UnityEvent onEnter = new UnityEvent();
        public UnityEvent onExit = new UnityEvent();

        readonly HashSet<Collider> _inside = new();
        readonly List<Collider> _scratch = new();
        bool _fired;

        /// <summary>Whether a matching object is inside the zone right now.</summary>
        public bool IsInside
        {
            get
            {
                Prune();
                return _inside.Count > 0;
            }
        }

        void Reset()
        {
            var zone = GetComponent<Collider>();
            if (zone != null) zone.isTrigger = true;
        }

        void OnDisable()
        {
            // Unity sends no exit event for a collider that is switched off, so anything still
            // counted as inside would stay inside forever. Let go of them, and tell the scene.
            bool wasInside = _inside.Count > 0;
            _inside.Clear();
            if (wasInside) onExit?.Invoke();
        }

        void OnTriggerEnter(Collider other) => Enter(other);
        void OnTriggerExit(Collider other) => Exit(other);
        void OnCollisionEnter(Collision collision) => Enter(collision.collider);
        void OnCollisionExit(Collision collision) => Exit(collision.collider);

        void Update()
        {
            // Catches the case above while the zone itself stays enabled: the user's collider is
            // switched off or destroyed mid-zone.
            Prune();
        }

        void Enter(Collider other)
        {
            if (!ToolkitRig.HasTag(other, triggerTagName)) return;
            if (!_inside.Add(other) || _inside.Count != 1) return;
            if (enterOnce && _fired) return;
            _fired = true;
            onEnter?.Invoke();
        }

        void Exit(Collider other)
        {
            if (!_inside.Remove(other) || _inside.Count != 0) return;
            onExit?.Invoke();
        }

        void Prune()
        {
            if (_inside.Count == 0) return;
            _scratch.Clear();
            foreach (var c in _inside)
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) _scratch.Add(c);
            if (_scratch.Count == 0) return;
            foreach (var c in _scratch) _inside.Remove(c);
            if (_inside.Count == 0) onExit?.Invoke();
        }

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            ToolkitRig.DrawColliderGizmo(GetComponent<Collider>(), _inside.Count > 0 ? Color.green : new Color(0.2f, 0.7f, 1f));
        }
    }
}
