using System.Collections.Generic;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Replays a recorded <see cref="StarScan"/> as a camera, so the real <see cref="StarLocalizer"/> can be tested on
    /// it in the Editor. Every frame carries its ground-truth pose, so with the map root at the identity relative to
    /// <see cref="TrackingSpace"/> a correct localization leaves it there; <see cref="TryGetMapRootError"/> shows by how much it is off.
    /// </summary>
    [AddComponentMenu("StageSettAR/Star Scan Camera Source")]
    public sealed class StarScanCameraSource : CameraSource
    {
        [Header("Scan")]
        [Tooltip("A .starscan file or an unpacked scan folder.")]
        [SerializeField] string scanPath = "";
        [Tooltip("Deliver only the held-out test frames (test.txt, else every 10th), not every frame.")]
        [SerializeField] bool testFramesOnly = true;
        [SerializeField] bool loop = true;
        [Tooltip("Centre-crops each frame to this aspect ratio, to imitate a camera with a different shape. The ground-truth pose is unchanged.")]
        [SerializeField] CropAspect cropAspect = CropAspect.None;
        [Tooltip("Width and height of the ratio when Crop Aspect is Custom.")]
        [SerializeField] Vector2 customCrop = new Vector2(4f, 3f);
        [Tooltip("Frames delivered per second.")]
        [SerializeField, Min(0.1f)] float framesPerSecond = 5f;

        [Header("Pose")]
        [Tooltip("Frame poses are expressed relative to this transform; empty means world space. The map root should start at the identity relative to it.")]
        [SerializeField] Transform trackingSpace;

        StarScan _scan;
        List<int> _order;
        int _next;
        Texture2D _texture;
        long _id;
        double _nextFrameTime;
        int _currentIndex = -1;
        Pose _groundTruth;

        public override Transform TrackingSpace => trackingSpace;
        public override bool IsReady => _order != null && _order.Count > 0;

        /// <summary>The scan's own index of the frame delivered last, or -1 before the first.</summary>
        public int CurrentFrameIndex => _currentIndex;

        /// <summary>Ground-truth pose of the camera that took the current frame (Unity, relative to <see cref="TrackingSpace"/>).</summary>
        public Pose CurrentGroundTruth => _groundTruth;

        public string ScanPath { get => scanPath; set => scanPath = value; }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(scanPath)) { Debug.LogWarning("StarScanCameraSource has no scan path.", this); return; }
            _scan = StarScan.Open(scanPath);
            _order = testFramesOnly ? _scan.TestFrames() : _scan.PlacedFrames();
            _next = 0;
            _currentIndex = -1;
        }

        void OnDisable()
        {
            UnityObjects.Discard(_texture);
            _texture = null;
            _scan?.Dispose();
            _scan = null;
            _order = null;
        }

        public override bool TryGetFrame(long afterId, out CameraFrame frame)
        {
            frame = default;
            if (!IsReady) return false;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextFrameTime) return false;
            if (_next >= _order.Count)
            {
                if (!loop) return false;
                _next = 0;
            }
            _nextFrameTime = now + 1.0 / framesPerSecond;

            int position = _order[_next++];
            UprightFrame up = _scan.LoadUpright(position, StarScan.AspectRatio(cropAspect, customCrop.x, customCrop.y));
            UnityObjects.Discard(_texture);   // the previous frame stays valid until this call
            _texture = up.texture;
            _currentIndex = _scan.Frames[position].index;
            _groundTruth = Conventions.OpenCvCameraToUnity(up.R, up.t);

            if (_id <= afterId) _id = afterId + 1; else _id++;
            frame.texture = _texture;
            frame.flipY = true;   // decoded images are upright with row 0 at the bottom
            frame.intrinsics = up.intrinsics;
            frame.id = _id;
            frame.timestamp = now;
            frame.cameraPose = _groundTruth;
            frame.hasPose = true;
            return true;
        }

        /// <summary>
        /// How far <paramref name="mapRoot"/> is from the identity relative to <see cref="TrackingSpace"/>, which is where
        /// a perfect localization puts it. False when the map root is missing.
        /// </summary>
        public bool TryGetMapRootError(Transform mapRoot, out float meters, out float degrees)
        {
            meters = degrees = 0f;
            if (mapRoot == null) return false;
            Vector3 p = mapRoot.position;
            Quaternion r = mapRoot.rotation;
            if (trackingSpace != null)
            {
                p = trackingSpace.InverseTransformPoint(p);
                r = Quaternion.Inverse(trackingSpace.rotation) * r;
            }
            meters = p.magnitude;
            degrees = Quaternion.Angle(r, Quaternion.identity);
            return true;
        }
    }
}
