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
    /// In <see cref="Mode.Adaptive"/> (the default) the first confident fix places the map and every further fix refines it at full
    /// rate until enough coverage has been seen (head turn or travel, over enough agreeing fixes); after that the localizer only
    /// refines in short bursts, when time has passed or the user has moved or turned a lot (see <see cref="LocalizerSchedule"/>).
    /// </remarks>
    [AddComponentMenu("StageSettAR/STAR Localizer")]
    public sealed class StarLocalizer : MonoBehaviour
    {
        public enum Mode { OneShot, Periodic, Continuous, Adaptive }
        public enum LocalizerState { Idle, Initializing, Localized, Lost, Failed }
        public enum Fusion { Joint, PerFrame }
        // Advanced tuning lives in these groups, which the Inspector shows as collapsed foldouts. The defaults suit a
        // Quest 3 in a room-sized space; most projects never open them.

        [Serializable]
        public sealed class TimingSettings
        {
            [Tooltip("Periodic and Adaptive modes: seconds of rest between refinement bursts. Default 30: the headset's own tracking drifts slowly, so refining twice a minute is plenty.")]
            public float periodSeconds = 30f;
            [Tooltip("Periodic and Adaptive modes: how long one burst lasts. Default 3: about a dozen fixes at the default speed, enough to refit.")]
            public float burstSeconds = 3f;
            [Tooltip("Adaptive mode only: refine early once the camera has moved this many metres since the last refinement. Default 2: drift shows when the user moves away. 0 turns it off.")]
            public float refineDistanceMeters = 2f;
            [Tooltip("Adaptive mode only: refine early once the head has turned this many degrees since the last refinement. Default 60. 0 turns it off.")]
            public float refineTurnDegrees = 60f;
            [Tooltip("Most camera frames analysed per second. Default 4: enough for a steady fit and cheap enough to leave the headset's frame rate alone.")]
            public float maxInferenceHz = 4f;
            [Tooltip("Give up ('not in this room') if the room is not found within this many seconds of starting. Default 25: long enough to look all around once or twice.")]
            public float failAfterSeconds = 25f;
            [Tooltip("The room counts as lost after this many seconds without a good fix. Default 15: shorter hiccups (looking at the floor, a dark moment) should not make the map disappear. In Periodic and Adaptive modes only the time spent in bursts counts.")]
            public float lostAfterSeconds = 15f;
            [Tooltip("Seconds a corrected map position takes to slide into place (about 95 % of the way). Default 0.5: quick enough to follow, slow enough that nobody sees a jump.")]
            public float glideSeconds = 0.5f;
            [Tooltip("Slide every map move into place over Glide Seconds. Default on: refinements are not seen as jumps. Off: every move is applied at once. A move after tracking was reset always snaps.")]
            public bool smoothMoves = true;
        }

        [Serializable]
        public sealed class GatingSettings
        {
            [Tooltip("OneShot, Periodic and Continuous modes: ask the user to look around before trusting the room. Default on: one lucky wrong fix could otherwise put the map in the wrong place. Off: the first accepted fix is used.")]
            public bool requireLookAround = true;
            [Tooltip("The agreeing fixes must all arrive within this many seconds. Default 10: enough time for a slow look around.")]
            public float lookAroundSeconds = 10f;
            [Tooltip("How many separate fixes must agree. Default 4: one wrong fix agreeing with three others by chance is very unlikely. Adaptive mode: fixes needed before the map counts as converged.")]
            public int minFixes = 4;
            [Tooltip("How far the head must turn during those fixes, in degrees. Default 30: fixes from one direction can share the same mistake; a turn rules that out. Adaptive mode: turn or travel is enough to converge.")]
            public float minTurnDeg = 30f;
            [Tooltip("Adaptive mode only: the camera travelling this many metres during the agreeing fixes counts as much as a head turn. Default 0.5: seen from two places, a wrong match cannot keep agreeing.")]
            public float minMoveMeters = 0.5f;
            [Tooltip("Two fixes agree if the map lands within this many metres of each other... Default 0.1: well above normal noise (a few centimetres), well below a wrong match.")]
            public float agreementMeters = 0.1f;
            [Tooltip("...and within this many degrees of turn. Default 3: the same reasoning for angle.")]
            public float agreementDegrees = 3f;
            [Tooltip("Fixes less confident than this (0 to 1) are ignored. Default 0.5: drops guesses made from too few landmarks.")]
            [Range(0f, 1f)] public float minConfidence = 0.5f;
            [Tooltip("Once found, a new fix that would move the map by more than this many metres (plus a margin for its own uncertainty) is rejected. Default 0.3: real drift is small, so a bigger move is a bad fix. Adaptive mode allows three times as much until converged.")]
            public float gateMeters = 0.3f;
            [Tooltip("...or turn it by more than this many degrees. Default 6. Three rejected fixes in a row that agree with each other restart the alignment from them.")]
            public float gateDegrees = 6f;
            [Tooltip("If the headset moves farther than this many metres from one rendered frame to the next, its tracking was reset and the map is found again. Default 1.5: no person moves that far in one frame.")]
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
        [Tooltip("Adaptive (the default): place the map at the first good fix, refine at full rate until enough of the room has been seen, then refine in short bursts " +
                 "every so often or when the user has moved or turned a lot. OneShot: localize once, then stop. Periodic: short refinement bursts on a timer. Continuous: never stop.")]
        public Mode mode = Mode.Adaptive;

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
        [Tooltip("Starts when enough of the room has been seen for the placement to be trusted (Adaptive mode), or together with On Localized in the other modes. " +
                 "Starts again after the room was lost and found again.")]
        public UnityEvent onConverged = new UnityEvent();
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

        /// <summary>True once enough of the room has been seen for the placement to be trusted. In Adaptive mode that is
        /// coverage (see <see cref="CoverageProgress"/>); in the other modes it is true as soon as the room is found.
        /// False again after the room is lost.</summary>
        public bool IsConverged => _converged;

        /// <summary>How many recent fixes agree with each other so far (Adaptive: accepted fixes since the first placement).</summary>
        public int AgreeingFixes { get { if (Adaptive) return _coverage.Count; RefreshProgress(); return _agreeing; } }
        /// <summary>How many agreeing fixes are needed before the room counts as found (Adaptive: as converged).</summary>
        public int FixesNeeded => Adaptive || (debug.fusion == Fusion.Joint && gating.requireLookAround) ? Math.Max(1, gating.minFixes) : 1;
        /// <summary>How far the head has turned across the agreeing fixes, in degrees.</summary>
        public float TurnDegrees { get { if (Adaptive) return _coverage.TurnDegrees; RefreshProgress(); return _turnDeg; } }
        /// <summary>Agreeing fixes so far compared with what is needed, 0 to 1.</summary>
        public float FixesProgress => Done ? 1f : Adaptive ? _coverage.FixesProgress(gating.minFixes) : Mathf.Clamp01(AgreeingFixes / (float)FixesNeeded);
        /// <summary>Head turn so far compared with what is needed, 0 to 1. Always 1 when the look-around is not required.
        /// In Adaptive mode the camera travelling far enough counts as well.</summary>
        public float TurnProgress =>
            Done || debug.fusion == Fusion.PerFrame ? 1f
            : Adaptive ? _coverage.SpreadProgress(gating.minTurnDeg, gating.minMoveMeters)
            : !gating.requireLookAround || gating.minTurnDeg <= 0f ? 1f : Mathf.Clamp01(TurnDegrees / gating.minTurnDeg);
        /// <summary>One number, 0 to 1, for a progress bar while the room is being found: the average of fix progress and turn progress.
        /// 1 once localized. In Adaptive mode the room is found at the first fix, so use <see cref="CoverageProgress"/> for the refinement after it.</summary>
        public float SearchProgress => State == LocalizerState.Localized ? 1f : Adaptive ? 0f : 0.5f * (FixesProgress + TurnProgress);
        /// <summary>One number, 0 to 1, for how much of the room has been covered since the map was placed. 1 once <see cref="IsConverged"/>, 0 before the first placement.</summary>
        public float CoverageProgress => _converged ? 1f : State != LocalizerState.Localized ? 0f : 0.5f * (FixesProgress + TurnProgress);
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
        const float ConvergingGateFactor = 3f;   // Adaptive: how much wider the gate is until converged

        StarDetector _detector;
        StarMap _detectorMap; // the map _detector was built for: its network and landmark count
        AlignmentSolver _solver;
        SessionScaleEstimator _scale;
        StarMap _scaleMap;   // the map _scale was learned on
        CancellationTokenSource _cts;
        Vector3d[] _exportLandmarks;
        Vector3 _centroid;
        Alignment _current, _target, _shown;
        bool _placed, _visible = true, _warmed, _warnedNoPose;
        readonly List<Candidate> _cands = new List<Candidate>();
        readonly List<GameObject> _hidden = new List<GameObject>();
        long _lastId;
        double _initStart;
        bool _resume, _converged;
        readonly LocalizerSchedule _schedule = new LocalizerSchedule();
        readonly CoverageTracker _coverage = new CoverageTracker();
        int _epoch;   // changes when the alignment is discarded, so a frame that was being analysed at the time is dropped
        Vector3 _headPos;   // the tracked camera in tracking space, sampled every frame
        float _headHeading;
        int _rejects, _initFixes;
        Alignment _lastReject;
        bool _everLocalized;
        int _agreeing, _progressFrame = -1;
        float _turnDeg;

        Transform Root => mapRoot ? mapRoot : transform;

        // Adaptive needs the joint fit; Per Frame has no fit to converge, so Adaptive then behaves like Continuous.
        bool Adaptive => mode == Mode.Adaptive && debug.fusion == Fusion.Joint;
        bool Done => Adaptive ? _converged : State == LocalizerState.Localized;

        ScheduleSettings ScheduleNow => new ScheduleSettings
        {
            mode = mode == Mode.Adaptive && !Adaptive ? Mode.Continuous : mode,
            maxInferenceHz = timing.maxInferenceHz, periodSeconds = timing.periodSeconds, burstSeconds = timing.burstSeconds,
            refineDistanceMeters = timing.refineDistanceMeters, refineTurnDegrees = timing.refineTurnDegrees,
            lostAfterSeconds = timing.lostAfterSeconds, trackingJumpMeters = gating.trackingJumpMeters,
        };

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
            _schedule.Reset();   // the time spent disabled is not time without a fix
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
            _initStart = -1; _schedule.Reset(); _epoch++;
            _converged = false; _coverage.Reset();
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
                    if (State == LocalizerState.Localized && mode == Mode.OneShot) return;
                    ScheduleSettings sched = ScheduleNow;
                    bool steady = State == LocalizerState.Localized && (!Adaptive || _converged);
                    if (!_schedule.Update(now, steady, _headPos, _headHeading, sched)) continue;
                    if (State == LocalizerState.Localized && _schedule.TimedOut(sched)) Lose(now, false);
                    if (!_schedule.Due(now) || _detector.Busy || !cameraSource.IsReady) continue;
                    if (!cameraSource.TryGetFrame(_lastId, out CameraFrame frame)) continue;
                    _lastId = frame.id;
                    if (!frame.hasPose)
                    {
                        if (!_warnedNoPose) { _warnedNoPose = true; Debug.LogWarning("[StageSettAR] Camera source delivers frames without a pose; they are skipped."); }
                        continue;
                    }
                    _schedule.Inferred(now, sched);
                    if (State == LocalizerState.Initializing && _initStart < 0) _initStart = now;

                    int epoch = _epoch;
                    _detector.LayersPerFrame = advanced.layersPerFrame;
                    _detector.InputScale = advanced.inputScale;
                    Detections d = await _detector.DetectAsync(frame, ct);
                    ct.ThrowIfCancellationRequested();
                    if (epoch != _epoch) continue;   // tracking was reset while the frame was being analysed
                    await ProcessFrame(work, frame, d, epoch, ct);
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

        async Awaitable ProcessFrame(PnpWork w, CameraFrame frame, Detections d, int epoch, CancellationToken ct)
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
            if (epoch != _epoch) return;   // the alignment was discarded meanwhile; this frame belongs to the old one
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
                _schedule.FixAccepted();
                _initFixes++;
                Place(a, true);
                if (State != LocalizerState.Localized) Found(now);
                return;
            }

            if (_current.valid)
            {
                // Until converged (Adaptive) the placement may be off by more than drift, so the gate is wider; wild fixes still fail it.
                if (WithinGate(_current, a, Adaptive && !_converged ? ConvergingGateFactor : 1f))
                {
                    _rejects = 0;
                    _solver.AddFrame(d, w.solver.InlierMask, frame.cameraPose, now);
                    _scale.Add(new Vector3d(cameraInMap.position), new Vector3d(frame.cameraPose.position), _current.yawDeg, a.positionSigma);
                    _scale.Advance(now);
                    // A changed scale is solved into the window too, which pivots the map about where the user has been.
                    Alignment fit = SolveWindow(_current);
                    if (fit.valid) { _current = fit; Place(fit, false); }
                    _schedule.FixAccepted();
                    if (Adaptive) AddCoverage(frame.cameraPose);
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

            _schedule.FixAccepted();
            _initFixes++;
            if (Adaptive)
            {
                // Initializing / Lost: the first confident fix places the map. The single fix is protected by the confidence
                // threshold, the wide gate and the re-seed after three agreeing rejects, and waiting for a second one would
                // only delay the map by a quarter of a second or more.
                SeedFrom(w, frame, d, a, cameraInMap.position, now);
                Place(_current, !_visible || !_placed);
                _rejects = 0;
                Found(now);
                AddCoverage(frame.cameraPose);
                return;
            }

            // Initializing / Lost: collect fixes and look for a consensus.
            // Not yet in the solver window: frames from a wrong (non-consensus) cluster would pollute the first Solve.
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
            ShowChildren();
            SetState(LocalizerState.Localized);
            onLocalized.Invoke();
            if (!_everLocalized) { _everLocalized = true; onFirstLocalized.Invoke(); }
            if (!Adaptive) SetConverged();   // the look-around consensus already is the convergence
        }

        void AddCoverage(in Pose camera)
        {
            _coverage.Add(camera.position, Heading(camera));
            if (_coverage.IsComplete(gating.minFixes, gating.minTurnDeg, gating.minMoveMeters)) SetConverged();
        }

        void SetConverged()
        {
            if (_converged) return;
            _converged = true;
            onConverged.Invoke();
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

        /// <summary>Starts a new fit from this one frame: <see cref="_current"/> becomes the alignment to place.</summary>
        void SeedFrom(PnpWork w, CameraFrame frame, Detections d, Alignment a, Vector3 mapPos, double now)
        {
            _solver.Clear();
            _scale.BeginSegment();
            _solver.AddFrame(d, w.solver.InlierMask, frame.cameraPose, now);
            _scale.Add(new Vector3d(mapPos), new Vector3d(frame.cameraPose.position), a.yawDeg, a.positionSigma);
            _scale.Advance(now);
            Alignment fit = SolveWindow(a);
            _current = fit.valid ? fit : a;
        }

        void ReseedFrom(PnpWork w, CameraFrame frame, Detections d, Alignment a, Vector3 mapPos, double now)
        {
            Debug.Log("[StageSettAR] Fixes consistently disagree with the current alignment; re-seeding from them.");
            SeedFrom(w, frame, d, a, mapPos, now);
            Place(_current, false);
            _rejects = 0;
            _schedule.FixAccepted();
            if (!Adaptive) return;
            // A restarted fit has to be confirmed again, at full rate.
            _converged = false;
            _coverage.Reset();
            AddCoverage(frame.cameraPose);
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

        /// <summary>Writes the 2D-3D pairs of <paramref name="d"/> (export-frame landmark, network pixel, score) into the solver's input arrays.</summary>
        public static void FillCorrespondences(Detections d, Vector3d[] landmarks, Vector3d[] p3, Vector2d[] p2, double[] score)
        {
            for (int i = 0; i < d.count; i++)
            {
                p3[i] = landmarks[d.landmark[i]];
                p2[i] = d.pixel[i];
                score[i] = d.score[i];
            }
        }

        async Awaitable<PnpResult> SolvePnp(PnpWork w, Detections d, bool hasPrior, Matrix3x3d priorR, Vector3d priorT)
        {
            int n = d.count;
            FillCorrespondences(d, _exportLandmarks, w.p3, w.p2, w.score);
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

        bool WithinGate(in Alignment cur, in Alignment fix, float factor)
        {
            Difference(cur, fix, out float m, out float deg);
            return m <= gating.gateMeters * factor + 3f * (Known(cur.positionSigma) + Known(fix.positionSigma))
                && deg <= gating.gateDegrees * factor + 3f * (Known(cur.yawSigmaDeg) + Known(fix.yawSigmaDeg));
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

        static float Heading(Pose p) => Heading(p.rotation * Vector3.forward);

        static float Heading(Vector3 f)
        {
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        // ------------------------------------------------------------------ state

        void Lose(double now, bool discontinuity)
        {
            if (State == LocalizerState.Localized) { SetState(LocalizerState.Lost); onLost.Invoke(); }
            _current = default; _cands.Clear(); _rejects = 0; _progressFrame = -1; _epoch++;
            _converged = false; _coverage.Reset();   // the next alignment is a new fit and has to be confirmed again
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
            _target = a;
            if (snap || !_placed || !timing.smoothMoves || timing.glideSeconds <= 0f) _shown = a;
            _placed = true;
        }

        /// <summary>The tracked camera in tracking space. False when the camera source cannot say.</summary>
        bool SampleHead()
        {
            if (cameraSource == null || !cameraSource.TryGetCurrentPose(out Pose pose)) return false;
            _headPos = pose.position;
            _headHeading = Heading(pose);
            return true;
        }

        /// <summary>Every frame, so the map follows XR Origin locomotion (the alignment is in tracking space).</summary>
        void LateUpdate()
        {
            // The jump test looks at every frame, not only the analysed ones, so a long walk never looks like a jump.
            if (SampleHead() && _schedule.TrackJump(_headPos, gating.trackingJumpMeters) && Localizing)
                Lose(Time.realtimeSinceStartupAsDouble, true);

            if (!_placed) return;
            if (timing.smoothMoves && timing.glideSeconds > 0f)
            {
                // Exponential approach: independent of the frame rate, and a new target only changes where the map is heading,
                // so a target that arrives mid-glide causes no restart. About 95 % of the way after glideSeconds.
                float k = 1f - Mathf.Exp(-3f * Time.unscaledDeltaTime / timing.glideSeconds);
                _shown.yawDeg += Mathf.DeltaAngle(_shown.yawDeg, _target.yawDeg) * k;
                _shown.translation = Vector3.Lerp(_shown.translation, _target.translation, k);
                _shown.scale = Mathf.Lerp(_shown.scale, _target.scale, k);
            }
            else _shown = _target;
            Alignment s = _shown;
            Transform ts = cameraSource ? cameraSource.TrackingSpace : null;
            Matrix4x4 m = (ts ? ts.localToWorldMatrix : Matrix4x4.identity) * s.MapToTracking;
            Root.SetPositionAndRotation(m.GetPosition(), m.rotation);
            if (debug.fusion == Fusion.Joint) Root.localScale = Vector3.one * s.scale;
        }

        // True while the run loop is going and has something to lose (OneShot stops looking once it has localized).
        bool Localizing =>
            _solver != null && _cts != null && !_cts.IsCancellationRequested
            && (State == LocalizerState.Initializing || State == LocalizerState.Lost || (State == LocalizerState.Localized && mode != Mode.OneShot));

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
