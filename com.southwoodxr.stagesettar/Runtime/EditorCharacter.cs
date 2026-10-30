#pragma warning disable CS0649 // fields are filled by Unity serialization / the rig builder
using UnityEngine;
using UnityEngine.Rendering;

namespace StageSettAR
{
    /// <summary>
    /// Marks the rig's stand-in character (a person model parented to the XR camera) and keeps it from
    /// ever getting in the user's way: hidden from the camera it is under, and upright.
    /// </summary>
    /// <remarks>
    /// The character's eyes sit at the camera, so in the Scene view you can see where the user's head is,
    /// which way they face and how tall they stand when placing content. It is hidden from the camera it
    /// follows (so it never blocks the view in play mode or the XR Device Simulator) but stays visible to
    /// the Scene view and any other camera. The model follows the camera's position and heading only:
    /// pitch and roll are ignored, so looking up or down does not tip the body over. Its GameObject is
    /// tagged EditorOnly, so Unity leaves it, and this component, out of builds.
    /// </remarks>
    [ExecuteAlways]
    [AddComponentMenu("StageSettAR/Editor Character")]
    public sealed class EditorCharacter : MonoBehaviour
    {
        [Tooltip("The XR camera the character follows and is hidden from. The camera this object is under when empty.")]
        [SerializeField] Camera followCamera;

        [Tooltip("Height of the camera above the character's feet, in metres (the model's eye height).")]
        [SerializeField, Min(0.1f)] float eyeHeight = 1.6f;

        Renderer[] _renderers;

        void OnEnable()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            SetHidden(false);
        }

        void LateUpdate() => Follow();

        /// <summary>Stands the character under the camera, facing the way the camera's heading does.</summary>
        public void Follow()
        {
            if (followCamera == null)
            {
                followCamera = GetComponentInParent<Camera>(true);
                if (followCamera == null) return;
            }

            var cam = followCamera.transform;

            // Heading: the camera's forward flattened onto the ground; when looking straight up or down, its up.
            var forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(cam.up, Vector3.up) * (cam.forward.y > 0f ? -1f : 1f);
            if (forward.sqrMagnitude < 1e-6f) return; // exactly vertical and rolled: keep the last heading
            var yaw = Quaternion.LookRotation(forward.normalized, Vector3.up);
            var position = cam.position + Vector3.down * (eyeHeight * Mathf.Abs(cam.lossyScale.y));

            // Only written when it differs: in edit mode an unchanged write could still dirty the scene.
            if ((transform.position - position).sqrMagnitude > 1e-8f || Quaternion.Angle(transform.rotation, yaw) > 0.01f)
                transform.SetPositionAndRotation(position, yaw);
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera == followCamera) SetHidden(true);
        }

        void OnEndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera == followCamera) SetHidden(false);
        }

        void SetHidden(bool hidden)
        {
            if (_renderers == null) return;
            foreach (var r in _renderers)
                if (r != null) r.forceRenderingOff = hidden;
        }
    }
}
