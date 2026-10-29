using System;
using System.Collections.Generic;
using System.Threading;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Events;

namespace StageSettAR
{
    /// <summary>
    /// Places a STAR map into the headset's tracking space by moving <see cref="mapRoot"/>.
    /// The XR Origin and the camera are never touched: headset tracking stays smooth, and the map
    /// follows it with a slowly refined offset.
    /// </summary>
    /// <remarks>
    /// Per camera frame: detect landmarks, solve PnP (seeded with the pose the current alignment
    /// predicts), turn the pose into a map-to-tracking alignment using where the headset says the
    /// camera was, gate it against the current alignment, and refit a window of recent frames.
    /// Gravity is shared between map and tracking, so only yaw and translation are estimated per
    /// alignment. The map's scale is estimated once for the whole session from the history of fixes
    /// (see <see cref="SessionScaleEstimator"/>), takes headset tracking as the true metres, changes
    /// slowly, and is not touched while the user stands still.
    /// <see cref="Fusion.PerFrame"/> skips the gate and the refit and places the map straight from
    /// each fix, for comparing against the joint fit.
    /// </remarks>
    [AddComponentMenu("StageSettAR/STAR Localizer")]
    public sealed class StarLocalizer : MonoBehaviour
    {
        public enum Mode { OneShot, Periodic, Continuous }
        public enum LocalizerState { Idle, Initializing, Localized, Lost, Failed }
        public enum Fusion { Joint, PerFrame }
        // Advanced tuning lives in these groups, which the Inspector shows as collapsed foldouts. The defaults suit a
        // Quest 3 in a room-sized space; most projects never open them.

        [Serializable]
        public sealed class TimingSettings
        {
            [Tooltip("Periodic mode only: seconds of rest between refinement bursts. Default 30: the headset's own tracking drifts slowly, so refining twice a minute is plenty.")]
            public float periodSeconds = 30f;
            [Tooltip("Periodic mode only: how long one burst lasts. Default 3: about a dozen fixes at the default speed, enough to refit.")]
            public float burstSeconds = 3f;
            [Tooltip("Most camera frames analysed per second. Default 4: enough for a steady fit and cheap enough to leave the headset's frame rate alone.")]
            public float maxInferenceHz = 4f;
            [Tooltip("Give up ('not in this room') if the room is not found within this many seconds of starting. Default 25: long enough to look all around once or twice.")]
            public float failAfterSeconds = 25f;
            [Tooltip("The room counts as lost after this many seconds without a good fix. Default 15: shorter hiccups (looking at the floor, a dark moment) should not make the map disappear. In Periodic mode only the time spent in bursts counts.")]
            public float lostAfterSeconds = 15f;
            [Tooltip("Seconds over which a corrected map position is slid into place. Default 0.5: quick enough to follow, slow enough that nobody sees a jump.")]
            public float glideSeconds = 0.5f;
        }

        [Serializable]
        public sealed class GatingSettings
        {
            [Tooltip("Ask the user to look around before trusting the room. Default on: one lucky wrong fix could otherwise put the map in the wrong place. Off: the first accepted fix is used.")]
            public bool requireLookAround = true;
            [Tooltip("The agreeing fixes must all arrive within this many seconds. Default 10: enough time for a slow look around.")]
            public float lookAroundSeconds = 10f;
            [Tooltip("How many separate fixes must agree. Default 4: one wrong fix agreeing with three others by chance is very unlikely.")]
            public int minFixes = 4;
            [Tooltip("How far the head must turn during those fixes, in degrees. Default 30: fixes from one direction can share the same mistake; a turn rules that out.")]
            public float minTurnDeg = 30f;
            [Tooltip("Two fixes agree if the map lands within this many metres of each other... Default 0.1: well above normal noise (a few centimetres), well below a wrong match.")]
            public float agreementMeters = 0.1f;
            [Tooltip("...and within this many degrees of turn. Default 3: the same reasoning for angle.")]
            public float agreementDegrees = 3f;
            [Tooltip("Fixes less confident than this (0 to 1) are ignored. Default 0.5: drops guesses made from too few landmarks.")]
            [Range(0f, 1f)] public float minConfidence = 0.5f;
            [Tooltip("Once found, a new fix that would move the map by more than this many metres (plus a margin for its own uncertainty) is rejected. Default 0.3: real drift is small, so a bigger move is a bad fix.")]
            public float gateMeters = 0.3f;
            [Tooltip("...or turn it by more than this many degrees. Default 6. Three rejected fixes in a row that agree with each other restart the alignment from them.")]
            public float gateDegrees = 6f;
            [Tooltip("If the headset moves farther than this many metres between two frames, its tracking was reset and the map is found again. Default 1.5: no person moves that far in a quarter of a second.")]
            public float trackingJumpMeters = 1.5f;
        }

        [Serializable]
        public sealed class AdvancedSettings
        {
            [Tooltip("Where the landmark network runs. GPUCompute is right for Quest and desktop GPUs.")]
            public BackendType backend = BackendType.GPUCompute;
            [Tooltip("Network layers run per rendered frame, so no single frame pays for the whole network. 0 runs it all in one frame.")]
            public int layersPerFrame = 10;
            [Tooltip("1 feeds the network the map's trained focal length. Lower values shrink the whole image for speed but move away from the trained scale, so fewer landmarks are found.")]
            [Range(0.25f, 1f)] public float inputScale = 1f;
            [Tooltip("Pose-solver tuning. The defaults work for the Quest camera.")]
            public PnpSettings pnp = PnpSettings.Default;
            [Tooltip("How recent fixes are combined into one alignment. The defaults average the last 30 fixes (at most a minute old), which smooths noise without trusting drifting tracking for long.")]
            public AlignmentSettings alignment = AlignmentSettings.Default;
        }

        [Serializable]
        public sealed class DebugSettings
        {
            [Tooltip("Joint: combine recent fixes with headset tracking, reject outliers and glide the map into place. Steady; use this.\n\n" +
                     "Per Frame: put the map exactly where each single camera frame says, with no agreement check, look-around, gate, scale or smoothing. " +
                     "Jittery and can jump to a wrong fix; useful for seeing what one frame gets on its own.")]
            public Fusion fusion = Fusion.Joint;
        }

        [Header("Setup")]
        [Tooltip("The STAR map to find in the room.")]
        public StarMap map;
        [Tooltip("Where camera images and the headset's camera pose come from (for Quest, the ArFoundationCameraSource on the camera).")]
        public CameraSource cameraSource;
        [Tooltip("The object that gets moved into place (map content and splat live under it). Defaults to this object.")]
        public Transform mapRoot;
        [Tooltip("Start looking for the room as soon as the scene starts. Turn off to call StartLocalization yourself.")]
        public bool autoStart = true;
        [Tooltip("Keep the map root's children inactive until the first localization, so nothing appears in the wrong place.")]
        public bool hideUntilLocalized = true;

        [Header("Mode")]
        [Tooltip("OneShot: localize once, then stop. Periodic: short refinement bursts. Continuous: never stop (the default: the map stays accurate as tracking drifts).")]
        public Mode mode = Mode.Continuous;

        [Header("Tuning (optional)")]
        [Tooltip("How often to look, when to give up, and how fast corrections glide in.")]
        public TimingSettings timing = new TimingSettings();
        [Tooltip("How many fixes must agree before the room is trusted, and which new fixes are rejected afterwards.")]
        public GatingSettings gating = new GatingSettings();
        [Tooltip("The map's scale error, estimated over the whole session from how far the headset travelled against how far the fixes say it travelled. " +
                 "It changes slowly and only after enough walking; standing still or turning on the spot never changes it. Joint fusion only. Turn Enabled off to keep the map at exactly 1 unit per metre.")]
        public SessionScaleSettings sessionScale = new SessionScaleSettings();
        [Tooltip("Rarely needed: the network backend, pose solver and alignment fit.")]
        public AdvancedSettings advanced = new AdvancedSettings();
        [Tooltip("For comparing against the default fusion.")]
        public DebugSettings debug = new DebugSettings();

        [Header("Events")]
        [Tooltip("Starts when the localizer begins looking for the room (at start-up, or after Relocalize).")]
        public UnityEvent onLocalizing = new UnityEvent();
        [Tooltip("Starts the first time the room is found, and only that once. The place to hook up an intro: a fade, a reveal, a sound.")]
        public UnityEvent onFirstLocalized = new UnityEvent();
        [Tooltip("Starts every time the room is found, including after it was lost.")]
        public UnityEvent onLocalized = new UnityEvent();
        [Tooltip("Starts when the headset was localized and then lost the room.")]
        public UnityEvent onLost = new UnityEvent();
        [Tooltip("Starts when the localizer gives up. Passes a short reason.")]
        public UnityEvent<string> onFailed = new UnityEvent<string>();
        [Tooltip("Starts on every change of state (Idle, Initializing, Localized, Lost, Failed) and passes the new state.")]
        public UnityEvent<LocalizerState> onStateChanged = new UnityEvent<LocalizerState>();

        public LocalizerState State { get; private set; }
        /// <summary>The alignment the map root is gliding to (map to tracking space). Invalid until localized, and again
        /// after a tracking reset until the next fix, since the old one no longer describes the tracking space.</summary>
        public Alignment CurrentAlignment => _placed ? _target : default;
        /// <summary>Confidence of the last accepted fix.</summary>
        public float LastConfidence { get; private set; }

        /// <summary>The raw session scale estimate (tracking metres per map unit); the prior until <see cref="ScaleObservable"/>.</summary>
        public float ScaleEstimate => _scale != null ? (float)_scale.Scale : sessionScale.priorScale;
        /// <summary>The scale in use: the estimate, rate-limited and clamped.</summary>
        public float AppliedScale => _scale != null ? (float)_scale.AppliedScale : sessionScale.priorScale;
        /// <summary>Approximate standard deviation of <see cref="ScaleEstimate"/>. Infinity until something is known.</summary>
        public float ScaleSigma => _scale != null ? (float)_scale.ScaleSigma : float.PositiveInfinity;
        /// <summary>True once the headset has travelled far enough, over enough fixes, for the scale to be told.</summary>
        public bool ScaleObservable => _scale != null && _scale.IsObservable;

        // ---- search progress, for a UI that shows how close the room is to being found ----

        /// <summary>How many recent fixes agree with each other so far.</summary>
        public int AgreeingFixes { get { RefreshProgress(); return _agreeing; } }
        /// <summary>How many agreeing fixes are needed before the room counts as found.</summary>
        public int FixesNeeded => debug.fusion == Fusion.Joint && gating.requireLookAround ? Math.Max(1, gating.minFixes) : 1;
        /// <summary>How far the head has turned across the agreeing fixes, in degrees.</summary>
        public float TurnDegrees { get { RefreshProgress(); return _turnDeg; } }
        /// <summary>Agreeing fixes so far compared with what is needed, 0 to 1.</summary>
        public float FixesProgress => State == LocalizerState.Localized ? 1f : Mathf.Clamp01(AgreeingFixes / (float)FixesNeeded);
        /// <summary>Head turn so far compared with what is needed, 0 to 1. Always 1 when the look-around is not required.</summary>
        public float TurnProgress =>
            State == LocalizerState.Localized || debug.fusion == Fusion.PerFrame || !gating.requireLookAround || gating.minTurnDeg <= 0f ? 1f : Mathf.Clamp01(TurnDegrees / gating.minTurnDeg);
        /// <summary>One number, 0 to 1, for a progress bar: the average of fix progress and turn progress. 1 once localized.</summary>
        public float SearchProgress => State == LocalizerState.Localized ? 1f : 0.5f * (FixesProgress + TurnProgress);
        /// <summary>Seconds left before the localizer gives up and fails. 0 when it is not in the initial search.</summary>
        public float SecondsUntilGiveUp
        {
            get
            {
                if (State != LocalizerState.Initializing) return 0f;
                if (_initStart < 0) return timing.failAfterSeconds;
                return Mathf.Max(0f, timing.failAfterSeconds - (float)(Time.realtimeSinceStartupAsDouble - _initStart));
            }
        }

        // det holds only the PnP inliers (copied: detection buffers are reused), so a consensus can seed the solver later.
        struct Candidate { public Alignment a; public Pose cam; public Vector3 mapPos; public float sigma; public double time; public Detections det; }
        sealed class PnpWork
        {
            public StarPnp solver; public Vector3d[] p3; public Vector2d[] p2; public double[] score;
        }

        const int MaxCandidates = 32, RejectsToReseed = 3;

        StarDetector _detector;
        StarMap _detectorMap; // the map _detector was built for: its network and landmark count
        AlignmentSolver _solver;
        SessionScaleEstimator _scale;
        StarMap _scaleMap;   // the map _scale was learned on
        CancellationTokenSource _cts;
        Vector3d[] _exportLandmarks;
        Vector3 _centroid;
        Alignment _current, _target, _from;
        double _glideStart;
        bool _placed, _visible = true, _warmed, _warnedNoPose;
        readonly List<Candidate> _cands = new List<Candidate>();
        readonly List<GameObject> _hidden = new List<GameObject>();
        long _lastId;
        double _nextInfer, _initStart, _lastFix, _nextBurst, _burstEnd, _idleSince;
        bool _burst, _hasLastPos, _resume;
        Vector3 _lastPos;
        int _rejects, _initFixes;
        Alignment _lastReject;
        bool _everLocalized;
        int _agreeing, _progressFrame = -1;
        float _turnDeg;

        Transform Root => mapRoot ? mapRoot : transform;

        void Reset() { mapRoot = transform; }

        void Start() { if (autoStart) StartLocalization(); }

        // OnDisable stops the loop; carry on from where it was. A found alignment keeps being tracked rather than
        // searched for again, and a failure stays failed until the user retries.
        void OnEnable()
        {
            if (!_resume) return;
            _resume = false;
            if (State == LocalizerState.Failed) return;
            if (State != LocalizerState.Localized) { StartLocalization(); return; }
            if (mode == Mode.OneShot) return;
            _lastFix = _idleSince = Time.realtimeSinceStartupAsDouble; // the time spent disabled is not time without a fix
            _burst = false;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _ = Run(_cts.Token);
        }

        void OnDisable()
        {
            _resume = _cts != null && !_cts.IsCancellationRequested;
            StopLocalization();
        }

        void OnDestroy()
        {
            _cts?.Cancel();
            ShowChildren();
            // StarDetector.Dispose defers freeing until an in-flight detection ends, and the loop
            // checks its token after every await, so nothing touches it afterwards.
            _detector?.Dispose();
            _detector = null;
        }

        // ------------------------------------------------------------------ public control

        /// <summary>
        /// (Re)starts localization from scratch: forgets the fit, keeps the map where it is until a new one is found.
        /// The session scale is kept (it belongs to the map and the headset, not to one alignment) unless the map changed.
        /// </summary>
        public void StartLocalization()
        {
            if (map == null || cameraSource == null)
            {
                Debug.LogError("[StageSettAR] StarLocalizer needs a map and a camera source.", this);
                return;
            }
            if (map.LandmarkCount < 4)
            {
                _cts?.Cancel();
                Fail($"Map '{map.name}' has {map.LandmarkCount} landmarks; at least 4 are needed to solve a pose.");
                return;
            }
            cameraSource.TargetFocal = map.focal * advanced.inputScale;
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            _solver =new AlignmentSolver(advanced.alignment, map);
            if (_scale == null || _scaleMap != map) { _scale = new SessionScaleEstimator(sessionScale); _scaleMap = map; }
            else _scale.BeginSegment();
            _solver.SetScale(_scale.AppliedScale);
            _current = default; _cands.Clear(); _rejects = 0; _initFixes = 0; _progressFrame = -1;
            _initStart = -1; _hasLastPos = false; _burst = false;
            _centroid = Vector3.zero;
            foreach (var p in map.LandmarksUnity) _centroid += p;
            if (map.LandmarkCount > 0) _centroid /= map.LandmarkCount;
            if (hideUntilLocalized && !_placed) HideChildren();
            SetState(LocalizerState.Initializing);
            _ = Run(_cts.Token);
        }

        /// <summary>
        /// Forgets the session scale and returns to the prior. The current alignment keeps the map centre where it is
        /// and takes the prior scale; the next fixes refit it.
        /// </summary>
        public void ResetScale()
        {
            if (_scale == null) return;
            _scale.Reset();
            _cands.Clear();   // their alignments were made at the old scale
            if (_current.valid)
            {
                _current = WithScale(_current, (float)_scale.AppliedScale);
                _rejects = 0;
                Place(_current, false);
            }
        }

        /// <summary>Same as <see cref="StartLocalization"/>.</summary>
        public void Relocalize() => StartLocalization();

        /// <summary>Stops running the network. A found alignment stays in effect.</summary>
        public void StopLocalization()
        {
            _cts?.Cancel();
            if (State == LocalizerState.Initializing || State == LocalizerState.Lost) SetState(LocalizerState.Idle);
        }

        // ------------------------------------------------------------------ loop

        async Awaitable Run(CancellationToken ct)
        {
            try
            {
                _exportLandmarks = new Vector3d[map.LandmarkCount];
                for (int i = 0; i < _exportLandmarks.Length; i++) _exportLandmarks[i] = new Vector3d(map.landmarksExport[i]);
                // Each run owns its PnP solver: a cancelled run may still be inside its own worker-thread call.
                var work = new PnpWork
                {
                    solver = new StarPnp(map.LandmarkCount, advanced.pnp),
                    p3 = new Vector3d[map.LandmarkCount], p2 = new Vector2d[map.LandmarkCount], score = new double[map.LandmarkCount],
                };

                if (_detector != null && _detectorMap != map)
                {
                    // Safe while a cancelled run's detection is still in flight: freeing waits for it to end.
                    _detector.Dispose();
                    _detector = null;
                }
                if (_detector == null)
                {
                    _detector = new StarDetector(map, advanced.backend);
                    _detectorMap = map;
                    _warmed = false;
                }
                while (_detector.Busy) await Awaitable.NextFrameAsync(ct);
                _detector.LayersPerFrame = advanced.layersPerFrame;
                if (!_warmed) { _warmed = true; await _detector.WarmupAsync(ct); }
                _lastId = 0;

                while (true)
                {
                    await Awaitable.NextFrameAsync(ct);
                    double now = Time.realtimeSinceStartupAsDouble;

                    if (State == LocalizerState.Failed || State == LocalizerState.Idle) return;
                    if (State == LocalizerState.Localized && !ScheduleLocalized(now)) { if (mode == Mode.OneShot) return; continue; }
                    if (State == LocalizerState.Localized && now - _lastFix > timing.lostAfterSeconds) { Lose(now, false); }
                    if (now < _nextInfer || _detector.Busy || !cameraSource.IsReady) continue;
                    if (!cameraSource.TryGetFrame(_lastId, out CameraFrame frame)) continue;
                    _lastId = frame.id;
                    if (!frame.hasPose)
                    {
                        if (!_warnedNoPose) { _warnedNoPose = true; Debug.LogWarning("[StageSettAR] Camera source delivers frames without a pose; they are skipped."); }
                        continue;
                    }
                    _nextInfer = now + 1.0 / Math.Max(0.1f, timing.maxInferenceHz);
                    if (State == LocalizerState.Initializing && _initStart < 0) _initStart = now;
                    CheckJump(frame.cameraPose.position, now);

                    _detector.LayersPerFrame = advanced.layersPerFrame;
                    _detector.InputScale = advanced.inputScale;
                    Detections d = await _detector.DetectAsync(frame, ct);
                    await ProcessFrame(work, frame, d, ct);
                    ct.ThrowIfCancellationRequested();

                    if (State == LocalizerState.Initializing && now - _initStart > timing.failAfterSeconds)
                        Fail(_initFixes == 0
                            ? "Not in this room: no known landmarks were recognised."
                            : "Not in this room: the landmark fixes never agreed" + (gating.requireLookAround ? " (or the headset was not turned enough)." : "."));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested) { Debug.LogException(e, this); Fail("Localizer error: " + e.Message); }
            }
        }

        /// <summary>Periodic burst bookkeeping. False means "no inference now".</summary>
        bool ScheduleLocalized(double now)
        {
            if (mode == Mode.OneShot) return false;
            if (mode == Mode.Continuous) return true;
            if (!_burst)
            {
                if (now < _nextBurst) return false;
                // The wait between bursts is not time without a fix: only searching counts toward lostAfterSeconds.
                _burst = true; _burstEnd = now + timing.burstSeconds; _lastFix += now - _idleSince;
                return true;
            }
            if (now <= _burstEnd) return true;
            _burst = false; _nextBurst = now + timing.periodSeconds; _idleSince = now;
            return false;
        }

        /// <summary>Tracking reset: the old alignment no longer describes the tracking space.</summary>
        void CheckJump(Vector3 pos, double now)
        {
            bool jump = _hasLastPos && (pos - _lastPos).magnitude > gating.trackingJumpMeters;
            _lastPos = pos; _hasLastPos = true;
            if (jump) Lose(now, true);
        }

        async Awaitable ProcessFrame(PnpWork w, CameraFrame frame, Detections d, CancellationToken ct)
        {
            // Prior: where the current alignment says the camera should be, in the network camera's axes.
            bool hasPrior = _current.valid && debug.fusion == Fusion.Joint;
            Matrix3x3d priorR = default; Vector3d priorT = default;
            Matrix3x3d turn = Conventions.QuarterTurnCameraRotation(d.quarterTurns);
            if (hasPrior)
            {
                Vector3 p = _current.MapToTracking.inverse.MultiplyPoint3x4(frame.cameraPose.position);
                Pose inMap = new Pose(p, Quaternion.Euler(0f, -_current.yawDeg, 0f) * frame.cameraPose.rotation);
                Conventions.UnityToOpenCvCamera(inMap, out Matrix3x3d rc, out priorT);
                priorR = rc * turn;
            }

            PnpResult r = await SolvePnp(w, d, hasPrior, priorR, priorT);
            ct.ThrowIfCancellationRequested();
            double now = Time.realtimeSinceStartupAsDouble;
            if (!r.success || r.confidence < gating.minConfidence) return;

            Pose cameraInMap = Conventions.OpenCvCameraToUnity(r.R * turn.Transpose(), r.t);
            // Fixes are compared at the scale in use (Joint); Per Frame leaves the map at its own scale.
            Alignment a = AlignmentSolver.FromSingleFix(cameraInMap, frame.cameraPose, debug.fusion == Fusion.Joint ? (float)_scale.AppliedScale : 1f);
            a.positionSigma = (float)r.positionSigma; a.yawSigmaDeg = (float)r.rotationSigmaDeg;
            LastConfidence = (float)r.confidence;

            if (debug.fusion == Fusion.PerFrame)
            {
                _current = a;
                _lastFix = now;
                _initFixes++;
                Place(a, true);
                if (State != LocalizerState.Localized) Found(now);
                return;
            }

            if (_current.valid)
            {
                if (WithinGate(_current, a))
                {
                    _rejects = 0;
                    _solver.AddFrame(d, w.solver.InlierMask, frame.cameraPose, now);
                    _scale.Add(new Vector3d(cameraInMap.position), new Vector3d(frame.cameraPose.position), _current.yawDeg, a.positionSigma);
                    _scale.Advance(now);
                    // A changed scale is solved into the window too, which pivots the map about where the user has been.
                    Alignment fit = SolveWindow(_current);
                    if (fit.valid) { _current = fit; Place(fit, false); }
                    _lastFix = now;
                }
                else
                {
                    // Consecutive rejects that agree with each other mean the old alignment is stale.
                    _rejects = _rejects > 0 && Agree(_lastReject, a) ? _rejects + 1 : 1;
                    _lastReject = a;
                    if (_rejects >= RejectsToReseed) ReseedFrom(w, frame, d, a, cameraInMap.position, now);
                }
                return;
            }

            // Initializing / Lost: collect fixes and look for a consensus.
            // Not yet in the solver window: frames from a wrong (non-consensus) cluster would pollute the first Solve.
            _lastFix = now;
            _initFixes++;
            _cands.Add(new Candidate
            {
                a = a, cam = frame.cameraPose, mapPos = cameraInMap.position, sigma = a.positionSigma,
                time = now, det = CopyInliers(d, w.solver.InlierMask),
            });
            _cands.RemoveAll(c => c.time < now - gating.lookAroundSeconds);
            if (_cands.Count > MaxCandidates) _cands.RemoveAt(0);
            if (TryConsensus(out Alignment seed))
            {
                // Rebuild the window from the consensus cluster only.
                _solver.Clear();
                _scale.BeginSegment();
                foreach (var c in _cands)
                {
                    if (!Agree(seed, c.a)) continue;
                    _solver.AddFrame(c.det, null, c.cam, c.time);
                    _scale.Add(new Vector3d(c.mapPos), new Vector3d(c.cam.position), seed.yawDeg, c.sigma);
                }
                _scale.Advance(now);
                Alignment fit = SolveWindow(seed);
                _current = fit.valid ? fit : seed;
                _cands.Clear();
                bool snap = !_visible || !_placed;
                Place(_current, snap);
                _rejects = 0;
                Found(now);
            }
        }

        void Found(double now)
        {
            _nextBurst = now + timing.periodSeconds; _idleSince = now;
            ShowChildren();
            SetState(LocalizerState.Localized);
            onLocalized.Invoke();
            if (!_everLocalized) { _everLocalized = true; onFirstLocalized.Invoke(); }
        }

        static Detections CopyInliers(Detections d, bool[] mask)
        {
            var c = new Detections(d.count) { netIntrinsics = d.netIntrinsics, quarterTurns = d.quarterTurns, frame = d.frame };
            for (int i = 0; i < d.count; i++)
            {
                if (mask != null && !mask[i]) continue;
                c.landmark[c.count] = d.landmark[i]; c.pixel[c.count] = d.pixel[i]; c.score[c.count] = d.score[i];
                c.count++;
            }
            return c;
        }

        void ReseedFrom(PnpWork w, CameraFrame frame, Detections d, Alignment a, Vector3 mapPos, double now)
        {
            Debug.Log("[StageSettAR] Fixes consistently disagree with the current alignment; re-seeding from them.");
            _solver.Clear();
            _scale.BeginSegment();
            _solver.AddFrame(d, w.solver.InlierMask, frame.cameraPose, now);
            _scale.Add(new Vector3d(mapPos), new Vector3d(frame.cameraPose.position), a.yawDeg, a.positionSigma);
            _scale.Advance(now);
            Alignment fit = SolveWindow(a);
            _current = fit.valid ? fit : a;
            Place(_current, false);
            _rejects = 0; _lastFix = now;
        }

        /// <summary>Refits the window at the scale in use.</summary>
        Alignment SolveWindow(in Alignment initial)
        {
            _solver.SetScale(_scale.AppliedScale);
            return _solver.Solve(initial);
        }

        /// <summary>The same alignment at another scale, with the landmark centroid left where it was.</summary>
        Alignment WithScale(in Alignment a, float scale)
        {
            Alignment b = a;
            b.translation += (a.scale - scale) * (Quaternion.Euler(0f, a.yawDeg, 0f) * _centroid);
            b.scale = scale;
            return b;
        }

        async Awaitable<PnpResult> SolvePnp(PnpWork w, Detections d, bool hasPrior, Matrix3x3d priorR, Vector3d priorT)
        {
            int n = d.count;
            for (int i = 0; i < n; i++)
            {
                w.p3[i] = _exportLandmarks[d.landmark[i]];
                w.p2[i] = d.pixel[i];
                w.score[i] = d.score[i];
            }
            CameraIntrinsics k = d.netIntrinsics;
            PnpResult r = default;
            await Awaitable.BackgroundThreadAsync();
            try
            {
                r = w.solver.Solve(n, w.p3, w.p2, w.score, k.fx, k.fy, k.cx, k.cy, k.width, k.height,
                                   hasPrior ? priorR : (Matrix3x3d?)null, hasPrior ? priorT : (Vector3d?)null);
            }
            finally { await Awaitable.MainThreadAsync(); }
            return r;
        }

        // ------------------------------------------------------------------ agreement and gating

        /// <summary>How far apart two alignments put the map: the landmark centroid's shift and the yaw difference.</summary>
        void Difference(in Alignment a, in Alignment b, out float meters, out float degrees)
        {
            meters = (a.MapToTracking.MultiplyPoint3x4(_centroid) - b.MapToTracking.MultiplyPoint3x4(_centroid)).magnitude;
            degrees = Mathf.Abs(Mathf.DeltaAngle(a.yawDeg, b.yawDeg));
        }

        bool Agree(in Alignment a, in Alignment b)
        {
            Difference(a, b, out float m, out float deg);
            return m <= gating.agreementMeters && deg <= gating.agreementDegrees;
        }

        bool WithinGate(in Alignment cur, in Alignment fix)
        {
            Difference(cur, fix, out float m, out float deg);
            return m <= gating.gateMeters + 3f * (Known(cur.positionSigma) + Known(fix.positionSigma))
                && deg <= gating.gateDegrees + 3f * (Known(cur.yawSigmaDeg) + Known(fix.yawSigmaDeg));
        }

        // A solve whose covariance could not be inverted reports an infinite sigma. Unknown must not widen the gate
        // to accept anything, so it falls back to the fixed part of the gate.
        static float Known(float sigma) => float.IsFinite(sigma) ? sigma : 0f;

        /// <summary>Largest group of mutually agreeing fixes (around the best medoid), plus a head turn if required.</summary>
        bool TryConsensus(out Alignment seed)
        {
            seed = default;
            int best = BestCluster(out int bestCount);
            if (best < 0 || bestCount < FixesNeeded) return false;
            if (gating.requireLookAround && ClusterTurn(best) < gating.minTurnDeg) return false;
            seed = _cands[best].a;
            return true;
        }

        /// <summary>Index of the candidate that most others agree with (-1 if none) and how many agree with it.</summary>
        int BestCluster(out int bestCount)
        {
            int best = -1;
            bestCount = 0;
            for (int i = 0; i < _cands.Count; i++)
            {
                int c = 0;
                for (int j = 0; j < _cands.Count; j++) if (Agree(_cands[i].a, _cands[j].a)) c++;
                if (c > bestCount) { best = i; bestCount = c; }
            }
            return best;
        }

        /// <summary>Largest head yaw difference between two fixes of the cluster around candidate <paramref name="best"/>.</summary>
        float ClusterTurn(int best)
        {
            float maxTurn = 0f;
            for (int i = 0; i < _cands.Count; i++)
            {
                if (!Agree(_cands[best].a, _cands[i].a)) continue;
                for (int j = i + 1; j < _cands.Count; j++)
                    if (Agree(_cands[best].a, _cands[j].a))
                        maxTurn = Mathf.Max(maxTurn, Mathf.Abs(Mathf.DeltaAngle(Heading(_cands[i].cam), Heading(_cands[j].cam))));
            }
            return maxTurn;
        }

        /// <summary>
        /// Recomputes the progress numbers at most once per frame. Fixes older than the look-around
        /// window are dropped first, exactly as the next fix would drop them, so a UI does not show
        /// agreement that has already expired.
        /// </summary>
        void RefreshProgress()
        {
            if (_progressFrame == Time.frameCount) return;
            _progressFrame = Time.frameCount;
            _cands.RemoveAll(c => c.time < Time.realtimeSinceStartupAsDouble - gating.lookAroundSeconds);
            int best = BestCluster(out _agreeing);
            _turnDeg = best >= 0 && gating.requireLookAround ? ClusterTurn(best) : 0f;
        }

        static float Heading(Pose p)
        {
            Vector3 f = p.rotation * Vector3.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        // ------------------------------------------------------------------ state

        void Lose(double now, bool discontinuity)
        {
            if (State == LocalizerState.Localized) { SetState(LocalizerState.Lost); onLost.Invoke(); }
            _current = default; _cands.Clear(); _rejects = 0; _burst = false; _progressFrame = -1;
            _solver.Clear();
            _scale.BeginSegment();   // the next alignment is a new fit; the scale itself stays
            if (discontinuity)
            {
                if (hideUntilLocalized) HideChildren();
                _placed = false;   // snap to the new alignment instead of gliding from a stale one
                if (State == LocalizerState.Initializing) _initStart = now;
            }
        }

        void Fail(string reason)
        {
            SetState(LocalizerState.Failed);
            Debug.LogWarning("[StageSettAR] " + reason, this);
            onFailed.Invoke(reason);
        }

        // The one place the state changes, so onStateChanged and onLocalizing never miss a transition.
        void SetState(LocalizerState s)
        {
            if (State == s) return;
            Debug.Log($"[StageSettAR] {State} -> {s}", this);
            State = s;
            onStateChanged.Invoke(s);
            if (s == LocalizerState.Initializing) onLocalizing.Invoke();
        }

        // ------------------------------------------------------------------ placement

        void Place(in Alignment a, bool snap)
        {
            _from = snap || !_placed ? a : Shown(Time.realtimeSinceStartupAsDouble);
            _target = a;
            _glideStart = Time.realtimeSinceStartupAsDouble;
            _placed = true;
        }

        Alignment Shown(double now)
        {
            float k = timing.glideSeconds > 0f ? Mathf.Clamp01((float)((now - _glideStart) / timing.glideSeconds)) : 1f;
            k = k * k * (3f - 2f * k);
            return new Alignment
            {
                valid = true,
                yawDeg = _from.yawDeg + Mathf.DeltaAngle(_from.yawDeg, _target.yawDeg) * k,
                translation = Vector3.Lerp(_from.translation, _target.translation, k),
                scale = Mathf.Lerp(_from.scale, _target.scale, k),
            };
        }

        /// <summary>Every frame, so the map follows XR Origin locomotion (the alignment is in tracking space).</summary>
        void LateUpdate()
        {
            if (!_placed) return;
            Alignment s = Shown(Time.realtimeSinceStartupAsDouble);
            Transform ts = cameraSource ? cameraSource.TrackingSpace : null;
            Matrix4x4 m = (ts ? ts.localToWorldMatrix : Matrix4x4.identity) * s.MapToTracking;
            Root.SetPositionAndRotation(m.GetPosition(), m.rotation);
            if (debug.fusion == Fusion.Joint) Root.localScale = Vector3.one * s.scale;
        }

        // The localizer may live on the map root itself, so "hide" means deactivating the root's children.
        void HideChildren()
        {
            if (!_visible) return;
            _visible = false;
            foreach (Transform c in Root)
                if (c.gameObject.activeSelf) { c.gameObject.SetActive(false); _hidden.Add(c.gameObject); }
        }

        void ShowChildren()
        {
            if (_visible) return;
            _visible = true;
            foreach (var go in _hidden) if (go) go.SetActive(true);
            _hidden.Clear();
        }

        void OnDrawGizmosSelected()
        {
            if (map == null || map.landmarksExport == null) return;
            Gizmos.matrix = Root.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.8f);
            foreach (var p in map.LandmarksUnity) Gizmos.DrawSphere(p, 0.03f);
        }
    }
}
