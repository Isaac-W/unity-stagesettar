#pragma warning disable CS0649 // fields are filled by Unity serialization / JsonUtility
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace StageSettAR
{
    /// <summary>
    /// Camera source for the Editor and desktop: a webcam or a still image. Meant for testing the
    /// localizer without a headset; the pose can come from a Transform you move by hand.
    /// </summary>
    [AddComponentMenu("StageSettAR/Texture Camera Source")]
    public sealed class TextureCameraSource : CameraSource
    {
        [Header("Image")]
        [Tooltip("A still image to deliver. Leave empty to use a webcam.")]
        [SerializeField] Texture stillImage;
        [Tooltip("Seconds between re-deliveries of the still image, so the localizer sees it as a stream.")]
        [SerializeField] float stillIntervalSeconds = 0.5f;
        [Tooltip("Webcam device name; empty picks the first device.")]
        [SerializeField] string deviceName = "";
        [SerializeField] Vector2Int requestedResolution = new Vector2Int(1280, 720);

        [Header("Intrinsics")]
        [Tooltip("Calibrated intrinsics; wins over the field of view guess.")]
        [SerializeField] ManualIntrinsics manualIntrinsics;
        [Tooltip("Horizontal field of view used when no manual intrinsics are given (square pixels, centred principal point).")]
        [SerializeField, Range(20f, 140f)] float horizontalFov = 70f;

        [Header("Pose (optional)")]
        [Tooltip("Where the camera is, e.g. a transform you drag in the scene. Without it frames carry no pose.")]
        [SerializeField] Transform cameraTransform;
        [Tooltip("Pose is expressed relative to this transform; empty means world space.")]
        [SerializeField] Transform trackingSpace;

        WebCamTexture _webcam;
        long _id;
        double _nextStill;

        public override Transform TrackingSpace => trackingSpace;
        public override bool IsReady => stillImage != null || (_webcam != null && _webcam.isPlaying && _webcam.width > 16);

        void OnEnable()
        {
            if (stillImage != null) return;
#if UNITY_ANDROID
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera)) Permission.RequestUserPermission(Permission.Camera);
#endif
            _webcam = new WebCamTexture(deviceName, requestedResolution.x, requestedResolution.y);
            _webcam.Play();
        }

        void OnDisable()
        {
            if (_webcam == null) return;
            _webcam.Stop();
            Destroy(_webcam);
            _webcam = null;
        }

        public override bool TryGetFrame(long afterId, out CameraFrame frame)
        {
            frame = default;
            if (!IsReady) return false;

            Texture tex;
            if (stillImage != null)
            {
                if (Time.realtimeSinceStartupAsDouble < _nextStill) return false;
                _nextStill = Time.realtimeSinceStartupAsDouble + stillIntervalSeconds;
                tex = stillImage;
            }
            else
            {
                if (!_webcam.didUpdateThisFrame) return false;
                tex = _webcam;
            }
            if (_id <= afterId) _id = afterId + 1; else _id++;

            if (!manualIntrinsics.TryResolve(tex.width, tex.height, out var k))
            {
                float f = tex.width * 0.5f / Mathf.Tan(horizontalFov * 0.5f * Mathf.Deg2Rad);
                k = new CameraIntrinsics(f, f, tex.width * 0.5f, tex.height * 0.5f, tex.width, tex.height);
            }

            frame.texture = tex;
            // Unity textures keep the image upright with row 0 at the bottom, except a mirrored webcam.
            frame.flipY = _webcam == null || !_webcam.videoVerticallyMirrored;
            frame.intrinsics = k;
            frame.id = _id;
            frame.timestamp = Time.realtimeSinceStartupAsDouble;
            if (cameraTransform != null)
            {
                frame.hasPose = true;
                Transform t = cameraTransform;
                frame.cameraPose = trackingSpace == null
                    ? new Pose(t.position, t.rotation)
                    : new Pose(trackingSpace.InverseTransformPoint(t.position), Quaternion.Inverse(trackingSpace.rotation) * t.rotation);
            }
            return true;
        }
    }
}
