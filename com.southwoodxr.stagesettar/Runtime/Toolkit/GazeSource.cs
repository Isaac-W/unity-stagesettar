#pragma warning disable CS0649 // serialized fields are filled by Unity
using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// The one place that says where the user is looking. The rig puts it on the Head object, so every
    /// gaze component (On Gaze Action, Panel Look At, Panel Dynamic Tilt, Experience Sequence) shares
    /// the same line and the same debug display.
    /// </summary>
    /// <remarks>
    /// Today the line is the camera's forward axis, which is what head-only aiming and the XR Device
    /// Simulator give. It is a component rather than a constant so eye tracking can later replace
    /// <see cref="TryGetRay"/> without touching any of the components that use it. Without one in the
    /// scene the gaze components fall back to the main camera, so nothing needs setting up.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("StageSettAR/Gaze Source")]
    public sealed class GazeSource : MonoBehaviour
    {
        [Tooltip("The camera the user looks through. Found automatically (a camera on this object or above it, then the main camera) when empty.")]
        [SerializeField] Camera userCamera;

        [Tooltip("Draw the gaze line in the Scene view while playing.")]
        [SerializeField] bool showDebugRay;

        [Tooltip("Length of the debug line, in metres.")]
        [SerializeField, Min(0.1f)] float debugLength = 5f;

        /// <summary>The live gaze source, or null when the scene has none.</summary>
        public static GazeSource Active { get; private set; }

        /// <summary>The camera this source follows.</summary>
        public Camera UserCamera
        {
            get
            {
                if (userCamera == null) userCamera = GetComponentInParent<Camera>();
                if (userCamera == null) userCamera = Camera.main;
                return userCamera;
            }
        }

        /// <summary>The line the user is looking along. False until a camera exists.</summary>
        public bool TryGetRay(out Ray ray)
        {
            var cam = UserCamera;
            if (cam == null)
            {
                ray = default;
                return false;
            }
            ray = new Ray(cam.transform.position, cam.transform.forward);
            return true;
        }

        void OnEnable() => Active = this;

        void OnDisable()
        {
            if (Active == this) Active = null;
        }

        void Update()
        {
            if (showDebugRay && TryGetRay(out var ray))
                Debug.DrawRay(ray.origin, ray.direction * debugLength, Color.cyan);
        }
    }
}
