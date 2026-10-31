#pragma warning disable CS0649 // fields are filled by Unity serialization / JsonUtility
using System;
using System.Text;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace StageSettAR
{
    /// <summary>
    /// <see cref="CameraSource"/> over AR Foundation's CPU camera image: Meta Quest 3
    /// (meta-openxr passthrough camera), ARCore and ARKit.
    /// </summary>
    /// <remarks>
    /// <para><b>Image.</b> Where the platform delivers YUV 4:2:0 (ARCore <c>AndroidYuv420_888</c>, ARKit full-range
    /// bi-planar) the planes are uploaded as they are, at the camera's resolution, into R8 / RG8 textures and
    /// converted to RGB on the GPU by the preprocess shader in the same pass that turns, scales and crops the
    /// image (BT.601 full range). Anything else, or any failure, falls back to an asynchronous RGBA32 conversion
    /// with <c>Transformation.None</c>, sized from the map's focal length (<see cref="CameraSource.TargetFocal"/>).
    /// Either way rows arrive top-first, so <see cref="CameraFrame.flipY"/> is false, and nothing is mirrored, so
    /// the intrinsics only need rescaling from <c>XRCameraIntrinsics.resolution</c> to the delivered size.</para>
    /// <para><b>Capture pose.</b> The camera transform's pose in tracking space is recorded every
    /// frame just before rendering. <c>XRCpuImage.timestamp</c> is on an unspecified clock
    /// (device uptime for ARCore/ARKit, a runtime clock on Quest), so the capture instant is
    /// mapped onto Unity's clock with a minimum-delay estimate: the smallest observed
    /// (arrival time - image timestamp) is assumed to be a fixed minimum latency. That holds for
    /// any clock epoch but not for the latency itself. Residual error is the difference between
    /// the real and assumed minimum latency (plus about half a frame of pose-sampling
    /// jitter, and the fact that the sampled head pose is the display-predicted one);
    /// expect 10-30 ms, i.e. a centimetre or so while walking. Tune with
    /// <see cref="timeOffsetSeconds"/> (positive = the image was captured later than estimated).</para>
    /// <para><b>Lens mount.</b> The sensor is not at the head pose: on Quest 3 the left RGB camera sits
    /// about 5 cm left of and 2 cm below the XR camera and looks about 11 degrees down, which is
    /// roughly 19 cm of error per metre of distance if ignored. The mount is a full pose (position and
    /// rotation) relative to the XR camera's transform, the same transform whose pose is sampled
    /// here, in Unity camera-local axes (x right, y up, z forward). It comes from the platform when AR
    /// Foundation provides it (<see cref="TryGetProvidedMount"/>; it does not yet), otherwise from
    /// <see cref="MountSettings"/>: by default the preset for the headset it runs on (<see cref="LensMounts"/>;
    /// measured on a Quest 3, left camera, the one AR Foundation delivers), or the custom values. On phones the camera transform already is the sensor, apart from a roll that follows the screen
    /// orientation, so the mount is identity there.</para>
    /// </remarks>
    [AddComponentMenu("StageSettAR/AR Foundation Camera Source")]
    public sealed class ArFoundationCameraSource : CameraSource
    {
        [Tooltip("The camera manager on the XR camera. Found automatically when empty.")]
        [SerializeField] ARCameraManager cameraManager;

        [Tooltip("The XR Origin whose space poses are expressed in. Found automatically when empty.")]
        [SerializeField] XROrigin xrOrigin;

        [Tooltip("Camera resolution to ask the platform for; the closest available configuration is used, preferring the same aspect ratio. " +
                 "Zero means the highest. Ignored where the platform has no choice (e.g. Quest).")]
        [SerializeField] Vector2Int requestedResolution;

        [SerializeField] ManualIntrinsics manualIntrinsics;

        [Tooltip("Added to the estimated capture time, in seconds. Positive means the image was captured later than estimated.")]
        [SerializeField] float timeOffsetSeconds;

        [Tooltip("Where the camera sensor sits relative to the headset's camera. Auto picks the measured values for the headset it runs on; choose Custom to use your own.")]
        [SerializeField] MountSettings mount = new MountSettings();

        [Tooltip("How camera images are brought to the GPU. The defaults pick the cheapest way automatically.")]
        [SerializeField] ImageSettings image = new ImageSettings();

        [Tooltip("For finding out what the camera and the lens mount look like on a device.")]
        [SerializeField] DebugSettings debug = new DebugSettings();

        [Serializable]
        public sealed class MountSettings
        {
            [Tooltip("Auto: the measured values for the headset this runs on (Quest 3), or the Quest 3 values with a warning on a headset " +
                     "that has not been measured, such as a Quest 3S. Custom: the Position and Euler Angles below. Ignored on phones, where the camera is the sensor.")]
            public LensMountPreset preset = LensMountPreset.Auto;
            [Tooltip("Custom only. Where the passthrough RGB camera sits relative to the head camera, in metres (x right, y up, z forward). " +
                     "Starts at the Quest 3 values (left camera).")]
            public Vector3 position = new Vector3(-0.047f, -0.023f, 0.043f);
            [Tooltip("Custom only. How the passthrough RGB camera is tilted relative to the head camera, in degrees. About 11 on x means looking 11 degrees down. " +
                     "Starts at the Quest 3 values (left camera).")]
            public Vector3 eulerAngles = new Vector3(10.94f, 0.20f, -0.15f);
        }

        /// <summary>Which lens mount <see cref="MountSettings"/> uses.</summary>
        public enum LensMountPreset
        {
            [InspectorName("Auto (by device)")] Auto,
            [InspectorName("Quest 3")] Quest3,
            Custom,
        }

        [Serializable]
        public sealed class ImageSettings
        {
            [Tooltip("Largest width of the converted image when the camera has to be converted to colour on the processor. " +
                     "Default 0: automatic, just big enough for the map's network. Raise or lower only to experiment.")]
            [Min(0)] public int maxWidth = 0;
            [Tooltip("Send the camera's raw brightness and colour planes to the GPU and convert them there. Default on: faster. " +
                     "Turn off to always convert on the processor. Formats that cannot be sent raw are converted on the processor anyway.")]
            public bool gpuYuvConversion = true;
        }

        [Serializable]
        public sealed class DebugSettings
        {
            [Tooltip("Write what the camera delivers (resolutions, image format, intrinsics) and which lens mount is used to the log once at start, " +
                     "prefixed [StageSettAR]. Default off.")]
            public bool logDiagnostics;
        }

        // Guess at the delay between exposure and the frame becoming visible to us; see class remarks.
        const double AssumedMinLatency = 0.03;
        // Without a target focal, the old fixed cap on the converted width.
        const int FallbackMaxWidth = 1280;
        // A kept frame older than this (seconds) is replaced by a new one instead of being handed out.
        const double MaxFrameAge = 0.1;

        struct PoseSample { public double time; public Vector3 position; public Quaternion rotation; }
        const int RingSize = 256; // > 2 s at 90 Hz

        readonly PoseSample[] _ring = new PoseSample[RingSize];
        int _ringHead, _ringCount;

        Camera _camera;
        Texture2D _texture;                       // RGBA fallback
        Texture2D _yTex, _uTex, _vTex;            // raw YUV planes
        NativeArray<byte> _stage;                 // padding for plane buffers that stop short of the last row
        CameraFrame _frame;
        long _nextId = 1;
        bool _wanted;                             // a consumer asked for a frame that is not there yet

        XRCpuImage.AsyncConversion _conversion;
        bool _converting;
        double _convStamp;
        CameraIntrinsics _convK;
        Vector2Int _convSize;

        double _lastStamp = double.NaN;           // newest image counted towards the latency estimate
        double _lastConvertedStamp = double.NaN;  // newest image handed to conversion, so it is not converted twice
        double _minDelta = double.NaN, _lastArrival;

        Pose _lens = Pose.identity;
        string _lensSource = "";
        bool _lensResolved, _configChecked, _loggedFirst, _loggedDiagnostics, _yuvFailed;
        bool _permitted;
        bool _sessionTracked;                     // the session has reported SessionTracking at least once
        bool _focused = true, _paused, _worn;
        int _wornFrame = -1;
        InputDevice _head;
#if UNITY_ANDROID && !UNITY_EDITOR
        bool _requested;
#endif

        public override bool IsReady => _permitted && _frame.id > 0;

        public override Transform TrackingSpace => xrOrigin != null ? xrOrigin.transform : null;

        // AR Foundation reports tracking that is missing as a session that is initializing again, but a provider may sit
        // in that state from the start, so a loss only counts once the session was seen tracking. Of the reasons it gives
        // while not tracking, None, Initializing and Relocalizing are brief, and a short drop must not restart the search.
        // Needs checking on Quest meta-openxr: whether it reports SessionTracking and a useful notTrackingReason.
        public override bool IsTracking
        {
            get
            {
                if (!IsWorn) return false;
                ARSessionState state = ARSession.state;
                if (state == ARSessionState.SessionTracking) _sessionTracked = true;
                if (!_sessionTracked || state == ARSessionState.SessionTracking) return true;
                NotTrackingReason reason = ARSession.notTrackingReason;
                return reason == NotTrackingReason.None || reason == NotTrackingReason.Initializing || reason == NotTrackingReason.Relocalizing;
            }
        }

        // Not worn means the headset is off a head or the app was paused or lost focus. Taking a Quest off moves the OpenXR
        // session out of focus (the session-state route, so no Meta SDK is needed), and on a phone the same events mean the
        // app went to the background. Either way tracking pauses and the placement is kept. Read at most once per frame.
        public override bool IsWorn
        {
            get
            {
                if (_wornFrame != Time.frameCount)
                {
                    _wornFrame = Time.frameCount;
                    if (!_head.isValid) _head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                    // Phones, and headsets without the feature, have no presence signal and count as present.
                    bool present = !_head.isValid || !_head.TryGetFeatureValue(CommonUsages.userPresence, out bool p) || p;
                    _worn = present && _focused && !_paused;
                }
                return _worn;
            }
        }

        void OnApplicationPause(bool paused) => _paused = paused;

        void OnApplicationFocus(bool focused) => _focused = focused;

        public override bool TryGetCurrentPose(out Pose pose)
        {
            pose = Pose.identity;
            // Like Publish, wait until the first frame has resolved the lens: resolving it earlier could pick the phone
            // lens on a headset whose XR device is not active yet.
            if (_ringCount == 0 || !_lensResolved) return false;
            ref var newest = ref _ring[(_ringHead - 1 + RingSize) % RingSize];
            pose = ToSensorPose(new Pose(newest.position, newest.rotation));
            return true;
        }

        // Images are only acquired and uploaded when asked for (the localizer wants a few per second, the camera
        // delivers many more). A miss records the request and the next Update serves it.
        public override bool TryGetFrame(long afterId, out CameraFrame frame)
        {
            frame = _frame;
            if (_frame.id > afterId && Time.realtimeSinceStartupAsDouble - _frame.timestamp < MaxFrameAge) return true;
            _wanted = true;
            return false;
        }

        void OnEnable()
        {
            if (cameraManager == null) cameraManager = FindFirstObjectByType<ARCameraManager>();
            if (xrOrigin == null) xrOrigin = FindFirstObjectByType<XROrigin>();
            if (cameraManager == null || xrOrigin == null)
            {
                Debug.LogError("[StageSettAR] ArFoundationCameraSource needs an ARCameraManager and an XROrigin in the scene.");
                enabled = false;
                return;
            }
            _camera = cameraManager.GetComponent<Camera>();
            if (_camera == null) _camera = xrOrigin.Camera;
            if (_camera == null)
            {
                Debug.LogError("[StageSettAR] ArFoundationCameraSource could not find the AR camera: put the ARCameraManager on it, or set the XROrigin's Camera.");
                enabled = false;
                return;
            }
            _ringCount = _ringHead = 0;
            Application.onBeforeRender += SamplePose;

            _permitted = CameraPermissionGate.IsGranted;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= SamplePose;
            DisposeConversion();
        }

        void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
            if (_yTex != null) Destroy(_yTex);
            if (_uTex != null) Destroy(_uTex);
            if (_vTex != null) Destroy(_vTex);
            if (_stage.IsCreated) _stage.Dispose();
        }

        void Update()
        {
            if (!_permitted)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                RequestPermissions();
                _permitted = CameraPermissionGate.IsGranted;
                if (!_permitted) return;
                // Fallback only: a camera subsystem that started before the grant may never deliver images, which is
                // why the build should start with a CameraPermissionGate scene. The manager must stay enabled while
                // waiting (on Quest it is what shows passthrough); toggle it once in the hope it restarts the camera.
                if (cameraManager.enabled)
                {
                    cameraManager.enabled = false;
                    cameraManager.enabled = true;
                }
#endif
            }
            if (cameraManager.subsystem == null || !cameraManager.subsystem.running) return;

            if (!_configChecked) SelectConfiguration();
            if (_converting) PollConversion();

            // Every image is counted towards the latency estimate, wanted or not; only a wanted one is converted.
            // The first frame is fetched unasked because IsReady waits for it, and a consumer only asks once ready.
            if (!cameraManager.TryAcquireLatestCpuImage(out var cpu)) return;
            using (cpu)
            {
                CountLatency(cpu.timestamp);
                if (!_converting && (_wanted || _frame.id == 0)) TryBeginConversion(cpu);
            } // disposing the image is safe while a conversion is in flight
        }

        // After TrackedPoseDriver (order 0), so the camera transform is already this frame's.
        [BeforeRenderOrder(10000)]
        void SamplePose()
        {
            var ts = xrOrigin.transform;
            var cam = _camera.transform;
            ref var s = ref _ring[_ringHead];
            s.time = Time.realtimeSinceStartupAsDouble;
            s.position = ts.InverseTransformPoint(cam.position);
            s.rotation = Quaternion.Inverse(ts.rotation) * cam.rotation;
            _ringHead = (_ringHead + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
        }

        void SelectConfiguration()
        {
            _configChecked = true;
            try
            {
                using var configs = cameraManager.GetConfigurations(Allocator.Temp);
                if (debug.logDiagnostics)
                {
                    var sb = new StringBuilder($"[StageSettAR] Camera configurations ({configs.Length}):");
                    foreach (var c in configs) sb.Append("\n  ").Append(Describe(c));
                    Debug.Log(sb.ToString());
                }
                if (configs.Length == 0) return;
                var sizes = new Vector2Int[configs.Length];
                for (int i = 0; i < sizes.Length; i++) sizes[i] = new Vector2Int(configs[i].width, configs[i].height);
                int index = ClosestResolution(sizes, requestedResolution);
                if (index < 0) return;
                var best = configs[index];
                if (cameraManager.currentConfiguration != best) cameraManager.currentConfiguration = best;
                Debug.Log($"[StageSettAR] Camera configuration: {best}");
                if (debug.logDiagnostics)
                {
                    var current = cameraManager.currentConfiguration;
                    Debug.Log($"[StageSettAR] Chosen camera configuration: {(current.HasValue ? Describe(current.Value) : "none reported")}");
                }
            }
            catch (Exception e) when (e is NotSupportedException || e is InvalidOperationException || e is ArgumentException)
            {
                Debug.Log($"[StageSettAR] Camera configuration not changed: {e.Message}");
            }
        }

        static string Describe(XRCameraConfiguration c) =>
            $"{c.width}x{c.height} @ {(c.framerate.HasValue ? c.framerate.Value.ToString() : "unknown")} fps";

        /// <summary>
        /// Feeds the minimum-delay estimate (see class remarks) with an image's timestamp. Called for every image the
        /// camera delivers, so the estimate sees the full frame rate, and an image is only counted once.
        /// </summary>
        void CountLatency(double stamp)
        {
            if (stamp == _lastStamp) return; // same image as last update
            _lastStamp = stamp;

            double now = Time.realtimeSinceStartupAsDouble;
            double delta = now - stamp;
            // Let the minimum creep up so one early outlier cannot pin the estimate forever.
            _minDelta = double.IsNaN(_minDelta) ? delta : Math.Min(delta, _minDelta + 0.0005 * (now - _lastArrival));
            _lastArrival = now;
        }

        void TryBeginConversion(XRCpuImage cpu)
        {
            double stamp = cpu.timestamp;
            if (stamp == _lastConvertedStamp) return; // same image as last conversion
            _lastConvertedStamp = stamp;

            // Fetched here, in the frame the image was acquired, so they belong to it. At the camera's own resolution.
            if (!manualIntrinsics.TryResolve(cpu.width, cpu.height, out var native))
            {
                if (!cameraManager.TryGetIntrinsics(out var xr) || xr.resolution.x <= 0) return;
                native = new CameraIntrinsics(xr.focalLength.x, xr.focalLength.y,
                                              xr.principalPoint.x, xr.principalPoint.y,
                                              xr.resolution.x, xr.resolution.y).Scaled(cpu.width, cpu.height);
            }
            if (!native.IsValid) return;

            if (debug.logDiagnostics && !_loggedDiagnostics)
            {
                _loggedDiagnostics = true;
                LogDiagnostics(cpu, native);
            }

            if (image.gpuYuvConversion && !_yuvFailed && TryPublishYuv(cpu, native, stamp)) return;

            if (!cpu.FormatSupported(TextureFormat.RGBA32)) return;

            var size = ConversionSize(cpu.width, cpu.height, Mathf.Min(native.fx, native.fy));
            _conversion = cpu.ConvertAsync(new XRCpuImage.ConversionParams(cpu, TextureFormat.RGBA32)
            {
                outputDimensions = size,
                transformation = XRCpuImage.Transformation.None,
            });
            _converting = true;
            _convStamp = stamp;
            _convK = native.Scaled(size.x, size.y);
            _convSize = size;
        }

        /// <summary>
        /// Size of the RGBA fallback image: the explicit cap if set, otherwise just enough for the map's focal length.
        /// The source does not know the map's turn, which decides whether fx or fy is the relevant focal length, so
        /// the smaller of the two is passed: it gives the wider image.
        /// </summary>
        Vector2Int ConversionSize(int width, int height, float focal)
        {
            int w;
            if (image.maxWidth > 0) w = Mathf.Min(width, image.maxWidth);
            else
            {
                w = AutoWidth(width, focal, TargetFocal);
                if (!(TargetFocal > 0f)) w = Mathf.Min(w, FallbackMaxWidth);
            }
            return new Vector2Int(Mathf.Max(1, w), Mathf.Max(1, Mathf.RoundToInt((float)height * w / width)));
        }

        void PollConversion()
        {
            var status = _conversion.status;
            if (!status.IsDone()) return;

            if (status == XRCpuImage.AsyncConversionStatus.Ready)
            {
                var data = _conversion.GetData<byte>();
                if (data.Length == _convSize.x * _convSize.y * 4)
                    PublishRgba(data);
            }
            DisposeConversion();
        }

        void PublishRgba(NativeArray<byte> data)
        {
            if (_texture == null || _texture.width != _convSize.x || _texture.height != _convSize.y)
            {
                if (_texture != null) Destroy(_texture);
                // Linear: the bytes are used as stored, no sRGB decoding when sampled.
                _texture = new Texture2D(_convSize.x, _convSize.y, TextureFormat.RGBA32, false, true)
                {
                    name = "StageSettAR camera", wrapMode = TextureWrapMode.Clamp,
                };
            }
            _texture.LoadRawTextureData(data);
            _texture.Apply(false);

            Publish(new CameraFrame { texture = _texture, intrinsics = _convK }, _convStamp, "RGBA");
        }

        // ---------------------------------------------------------------- raw YUV upload

        static bool TryYuvFormat(XRCpuImage cpu, out CameraImageFormat format)
        {
            format = CameraImageFormat.Rgb;
            if (cpu.format == XRCpuImage.Format.AndroidYuv420_888 && cpu.planeCount == 3) format = CameraImageFormat.YuvPlanar;
            else if (cpu.format == XRCpuImage.Format.IosYpCbCr420_8BiPlanarFullRange && cpu.planeCount == 2) format = CameraImageFormat.YuvSemiPlanar;
            return format != CameraImageFormat.Rgb;
        }

        /// <summary>
        /// Uploads the planes of a YUV 4:2:0 image as they are and publishes the frame. False (and nothing published)
        /// for other formats, or when the layout is not one the shader understands, in which case the RGBA conversion
        /// is used instead (permanently, after a failure).
        /// </summary>
        bool TryPublishYuv(XRCpuImage cpu, CameraIntrinsics k, double stamp)
        {
            if (!TryYuvFormat(cpu, out var format)) return false;
            try
            {
                if (!SystemInfo.SupportsTextureFormat(TextureFormat.R8) ||
                    (format == CameraImageFormat.YuvSemiPlanar && !SystemInfo.SupportsTextureFormat(TextureFormat.RG16)))
                    return YuvUnsupported("the GPU lacks R8/RG8 textures");

                int chromaRows = (cpu.height + 1) / 2;
                var yPlane = cpu.GetPlane(0);
                if (yPlane.pixelStride != 1) return YuvUnsupported($"luma pixel stride is {yPlane.pixelStride}");

                int chromaStride = 1;
                Texture chroma, chromaV = null;
                if (format == CameraImageFormat.YuvSemiPlanar)
                {
                    var cbcr = cpu.GetPlane(1);
                    if (cbcr.pixelStride != 2 || cbcr.rowStride % 2 != 0) return YuvUnsupported($"CbCr pixel stride {cbcr.pixelStride}, row stride {cbcr.rowStride}");
                    UploadPlane(ref _yTex, TextureFormat.R8, yPlane.rowStride, cpu.height, yPlane.data, FilterMode.Bilinear, "StageSettAR camera Y");
                    UploadPlane(ref _uTex, TextureFormat.RG16, cbcr.rowStride / 2, chromaRows, cbcr.data, FilterMode.Point, "StageSettAR camera CbCr");
                    chroma = _uTex;
                }
                else
                {
                    // Cb and Cr as raw rows: with pixel stride 2 they interleave (NV12 / NV21 share memory), so each is read
                    // at every second texel, which also makes the order of the two irrelevant.
                    var cb = cpu.GetPlane(1);
                    var cr = cpu.GetPlane(2);
                    if (cb.rowStride != cr.rowStride || cb.pixelStride != cr.pixelStride || cb.pixelStride < 1 || cb.pixelStride > 2)
                        return YuvUnsupported($"chroma strides differ or are unusual (row {cb.rowStride}/{cr.rowStride}, pixel {cb.pixelStride}/{cr.pixelStride})");
                    UploadPlane(ref _yTex, TextureFormat.R8, yPlane.rowStride, cpu.height, yPlane.data, FilterMode.Bilinear, "StageSettAR camera Y");
                    UploadPlane(ref _uTex, TextureFormat.R8, cb.rowStride, chromaRows, cb.data, FilterMode.Point, "StageSettAR camera Cb");
                    UploadPlane(ref _vTex, TextureFormat.R8, cr.rowStride, chromaRows, cr.data, FilterMode.Point, "StageSettAR camera Cr");
                    chroma = _uTex;
                    chromaV = _vTex;
                    chromaStride = cb.pixelStride;
                }

                Publish(new CameraFrame
                {
                    texture = _yTex, format = format, chroma = chroma, chromaV = chromaV, chromaPixelStride = chromaStride,
                    width = cpu.width, height = cpu.height, intrinsics = k,
                }, stamp, format == CameraImageFormat.YuvPlanar ? "YUV planar" : "YUV semi-planar");
                return true;
            }
            catch (Exception e)
            {
                return YuvUnsupported(e.Message);
            }
        }

        bool YuvUnsupported(string why)
        {
            _yuvFailed = true;
            Debug.LogWarning($"[StageSettAR] Raw YUV upload is not possible ({why}); converting to RGBA on the processor instead.");
            return false;
        }

        /// <summary>Copies a plane's rows (stride included) into a reused texture of <paramref name="width"/> x <paramref name="height"/> texels.</summary>
        void UploadPlane(ref Texture2D tex, TextureFormat format, int width, int height, NativeArray<byte> data, FilterMode filter, string name)
        {
            if (tex == null || tex.width != width || tex.height != height || tex.format != format)
            {
                if (tex != null) Destroy(tex);
                tex = new Texture2D(width, height, format, false, true)
                {
                    name = name, wrapMode = TextureWrapMode.Clamp, filterMode = filter,
                };
            }
            int need = width * height * (format == TextureFormat.RG16 ? 2 : 1);
            if (data.Length >= need) tex.SetPixelData(data.GetSubArray(0, need), 0);
            else
            {
                // The buffer ends inside the last row (only the used part of it is there); the rest is never read.
                if (!_stage.IsCreated || _stage.Length < need)
                {
                    if (_stage.IsCreated) _stage.Dispose();
                    _stage = new NativeArray<byte>(need, Allocator.Persistent);
                }
                NativeArray<byte>.Copy(data, 0, _stage, 0, data.Length);
                tex.SetPixelData(_stage.GetSubArray(0, need), 0);
            }
            tex.Apply(false);
        }

        // ---------------------------------------------------------------- publishing

        void Publish(CameraFrame frame, double stamp, string path)
        {
            if (!_lensResolved) ResolveLens();

            // A clock that makes no sense (unset, wildly off) must not send the pose lookup into the past or future.
            double now = Time.realtimeSinceStartupAsDouble;
            double capture = stamp + _minDelta - AssumedMinLatency;
            if (!(capture <= now && now - capture < 1.0)) capture = now - AssumedMinLatency;
            capture += timeOffsetSeconds;

            frame.flipY = false; // rows are top-first, see class remarks
            frame.id = _nextId++;
            _wanted = false;
            frame.timestamp = capture;
            frame.hasPose = TryPoseAt(capture, out var head);
            if (frame.hasPose) frame.cameraPose = ToSensorPose(head);
            _frame = frame;

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Debug.Log($"[StageSettAR] First camera frame {frame.ImageWidth}x{frame.ImageHeight} ({path}), intrinsics {frame.intrinsics}, " +
                          $"lens offset {_lens.position:F3} / {_lens.rotation.eulerAngles:F1}, clock delta {_minDelta:F3}s.");
            }
        }

        void DisposeConversion()
        {
            if (!_converting) return;
            _conversion.Dispose();
            _converting = false;
        }

        /// <summary>Interpolated head pose in tracking space at Unity-clock time <paramref name="t"/>.</summary>
        bool TryPoseAt(double t, out Pose pose)
        {
            pose = default;
            if (_ringCount == 0) return false;

            int newest = (_ringHead - 1 + RingSize) % RingSize;
            if (t >= _ring[newest].time) { pose = new Pose(_ring[newest].position, _ring[newest].rotation); return true; }

            for (int i = 1; i < _ringCount; i++)
            {
                int a = (newest - i + RingSize) % RingSize, b = (a + 1) % RingSize;
                if (_ring[a].time > t) continue;
                float u = (float)((t - _ring[a].time) / Math.Max(1e-6, _ring[b].time - _ring[a].time));
                pose = new Pose(Vector3.Lerp(_ring[a].position, _ring[b].position, u),
                                Quaternion.Slerp(_ring[a].rotation, _ring[b].rotation, u));
                return true;
            }
            // Older than anything recorded: tolerate a frame of slack, otherwise refuse.
            int oldest = (newest - _ringCount + 1 + RingSize) % RingSize;
            if (_ring[oldest].time - t > 0.05) return false;
            pose = new Pose(_ring[oldest].position, _ring[oldest].rotation);
            return true;
        }

        Pose ToSensorPose(Pose head)
        {
            Pose mounted = Conventions.MountedPose(head, _lens);
            var rotation = mounted.rotation;
            if (Application.isMobilePlatform && !XRSettings.isDeviceActive)
            {
                // Phone: the camera transform is display-oriented, the sensor image is not.
                // Image up is the device's +x axis whatever the screen orientation.
                float roll = Screen.orientation switch
                {
                    ScreenOrientation.Portrait => -90f,
                    ScreenOrientation.PortraitUpsideDown => 90f,
                    ScreenOrientation.LandscapeRight => 180f,
                    _ => 0f,
                };
                rotation *= Quaternion.Euler(0f, 0f, roll);
            }
            return new Pose(mounted.position, rotation);
        }

        // ---------------------------------------------------------------- lens mount

        /// <summary>
        /// The camera's mount relative to the XR camera (Unity camera-local axes), if the platform provides it.
        /// AR Foundation 6 does not: <c>XRCameraIntrinsics</c> has only focal length, principal point and resolution,
        /// and there is no extrinsics or lens-pose API. This is the one place to plug a provider in (for example
        /// Android Camera2 lens pose); until then <see cref="MountSettings"/> is used.
        /// </summary>
        static bool TryGetProvidedMount(out Pose mount)
        {
            mount = Pose.identity;
            return false;
        }

        void ResolveLens()
        {
            _lensResolved = true;
            if (Application.isMobilePlatform && !XRSettings.isDeviceActive)
            {
                _lens = Pose.identity; // phone: the camera transform is the sensor
                _lensSource = "phone (identity)";
            }
            else if (TryGetProvidedMount(out var provided))
            {
                _lens = provided;
                _lensSource = "provided";
            }
            else
            {
                _lens = ResolvePreset(out _lensSource);
            }
            if (debug.logDiagnostics)
                Debug.Log($"[StageSettAR] Lens mount used: {_lensSource}, position {_lens.position:F3}, euler {_lens.rotation.eulerAngles:F2}; device model '{SystemInfo.deviceModel}', codename '{DeviceCodename()}'.");
        }

        // android.os.Build.DEVICE, the codename Meta's tooling matches ("eureka" is the Quest 3); SystemInfo only has the display model.
        static string DeviceCodename()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var build = new AndroidJavaClass("android.os.Build");
                return build.GetStatic<string>("DEVICE") ?? "";
            }
            catch (Exception) { return ""; }
#else
            return "";
#endif
        }

        Pose ResolvePreset(out string source)
        {
            switch (mount.preset)
            {
                case LensMountPreset.Custom:
                    source = "custom";
                    return new Pose(mount.position, Quaternion.Euler(mount.eulerAngles));
                case LensMountPreset.Quest3:
                    source = "Quest 3 preset";
                    return LensMounts.Quest3;
            }

            var model = SystemInfo.deviceModel;
            var codename = DeviceCodename();
            if (LensMounts.TryGetMeasured(model, codename, out var measured, out var name))
            {
                source = "auto: " + name;
                return measured;
            }

            // Not measured: the Quest 3 values are the best available, and a headset that is off by a few centimetres
            // is easier to fix when it is known why. The editor has no passthrough camera to be wrong about.
            source = $"auto: no measurement for '{model}' / '{codename}', Quest 3 values";
            if (!Application.isEditor)
            {
                var what = LensMounts.IsQuest3S(model) ? "This looks like a Quest 3S, which has not been measured yet" : "Unrecognised headset";
                what += $" (model '{model}', codename '{codename}')";
                Debug.LogWarning($"[StageSettAR] {what}. Using the Quest 3 lens mount, so content may sit a little off. " +
                                 "On the AR Foundation Camera Source set Mount > Preset to Custom and adjust Position and Euler Angles until it lines up.", this);
            }
            return LensMounts.Quest3;
        }

        // ---------------------------------------------------------------- diagnostics

        void LogDiagnostics(XRCpuImage cpu, CameraIntrinsics native)
        {
            var sb = new StringBuilder($"[StageSettAR] First CPU image {cpu.width}x{cpu.height}, format {cpu.format}, {cpu.planeCount} plane(s)");
            for (int i = 0; i < cpu.planeCount; i++)
            {
                var p = cpu.GetPlane(i);
                sb.Append($"\n  plane {i}: rowStride {p.rowStride}, pixelStride {p.pixelStride}, {p.data.Length} bytes");
            }
            bool yuv = TryYuvFormat(cpu, out var layout);
            sb.Append($"\n  upload: {(image.gpuYuvConversion && yuv ? "raw " + layout + " planes, converted on the GPU" : "RGBA32 conversion")}");
            if (!yuv || !image.gpuYuvConversion)
            {
                var size = ConversionSize(cpu.width, cpu.height, Mathf.Min(native.fx, native.fy));
                sb.Append($", {size.x}x{size.y} (target focal {TargetFocal:F1}, maxWidth {image.maxWidth})");
            }
            Debug.Log(sb.ToString());

            if (cameraManager.TryGetIntrinsics(out var xr))
                Debug.Log($"[StageSettAR] Intrinsics reported: fx {xr.focalLength.x:F2}, fy {xr.focalLength.y:F2}, cx {xr.principalPoint.x:F2}, cy {xr.principalPoint.y:F2}, " +
                          $"resolution {xr.resolution.x}x{xr.resolution.y}, fx/width {xr.focalLength.x / Mathf.Max(1, xr.resolution.x):F4}. " +
                          $"Used at the image size: {native}, fx/width {native.fx / native.width:F4}.");
            else
                Debug.Log($"[StageSettAR] Intrinsics: none reported by the camera; using {native}, fx/width {native.fx / native.width:F4}.");

            if (!_lensResolved) ResolveLens();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void RequestPermissions()
        {
            if (_requested) return;
            _requested = true;
            Debug.LogWarning("[StageSettAR] Camera permission was not granted before this scene started, so the camera may never " +
                             "deliver images. Put the camera permission scene first in the build (Tools > StageSettAR > Setup).");
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionDenied += p => Debug.LogWarning($"[StageSettAR] {p} was denied; the camera cannot start without it.");
            Permission.RequestUserPermissions(CameraPermissionGate.Required, callbacks);
        }
#endif
    }
}
