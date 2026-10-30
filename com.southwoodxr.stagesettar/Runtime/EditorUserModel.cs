#pragma warning disable CS0649 // fields are filled by Unity serialization / the rig builder
using UnityEngine;
using UnityEngine.Rendering;

namespace StageSettAR
{
    /// <summary>
    /// A stand-in body for the user, so you can see where their head is and which way they face
    /// when placing content in the Scene view.
    /// </summary>
    /// <remarks>
    /// The head takes the camera's full pose. The body takes only its heading and position on the
    /// ground, and stretches from the tracking floor up to the head. The model is hidden from the
    /// camera it follows (so it never blocks the user's view in play mode or the XR Device
    /// Simulator) but stays visible to the Scene view. Its GameObject is tagged EditorOnly, so
    /// Unity leaves it out of builds.
    /// New rigs use <see cref="EditorCharacter"/> (a person model) instead; this stays so scenes made
    /// with the primitive model keep working.
    /// </remarks>
    [ExecuteAlways]
    [AddComponentMenu("StageSettAR/Editor User Model")]
    public sealed class EditorUserModel : MonoBehaviour
    {
        [Tooltip("The XR camera to follow. Found automatically (first camera under this object's parent) when empty.")]
        [SerializeField] Camera followCamera;

        [Tooltip("Transform whose height is the floor (the XR Origin). This object's parent when empty.")]
        [SerializeField] Transform floor;

        [Tooltip("Follows the camera's full pose.")]
        [SerializeField] Transform head;

        [Tooltip("Follows the camera's heading and ground position.")]
        [SerializeField] Transform body;

        [Tooltip("Width and depth of the body, in metres.")]
        [SerializeField, Min(0.05f)] Vector2 bodySize = new Vector2(0.4f, 0.25f);

        [Tooltip("Gap between the top of the body and the camera, in metres.")]
        [SerializeField, Min(0f)] float neckGap = 0.12f;

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

        /// <summary>Moves the model to the camera's current pose.</summary>
        public void Follow()
        {
            if (followCamera == null)
            {
                var parent = transform.parent;
                if (parent != null) followCamera = parent.GetComponentInChildren<Camera>(true);
                if (followCamera == null) return;
            }
            if (head == null || body == null) return;
            var origin = floor != null ? floor : transform.parent;
            float floorY = origin != null ? origin.position.y : 0f;

            var cam = followCamera.transform;
            head.SetPositionAndRotation(cam.position, cam.rotation);

            // Heading: the camera's forward flattened onto the ground; when looking straight up or down, its up.
            var forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(cam.up, Vector3.up) * (cam.forward.y > 0f ? -1f : 1f);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            var yaw = Quaternion.LookRotation(forward.normalized, Vector3.up);

            // Unity's capsule primitive is 2 m tall and 1 m wide at scale 1.
            float height = Mathf.Max(0.2f, cam.position.y - neckGap - floorY);
            body.SetPositionAndRotation(new Vector3(cam.position.x, floorY + height * 0.5f, cam.position.z), yaw);
            body.localScale = new Vector3(bodySize.x, height * 0.5f, bodySize.y);
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
