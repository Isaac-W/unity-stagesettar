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
    /// camera was, and fit a window of recent frames. Gravity is shared between map and tracking,
    /// so only yaw and translation are estimated per alignment. The map is placed at scale 1.
    /// How often this runs depends on the phase (see <see cref="Phase"/>):
    /// Startup collects fixes until a few agree, then places the map. Early refines the placement with
    /// every fix until enough of the room has been seen. Late only looks now and then, and refits when the
    /// map is found to have moved (see <see cref="LocalizerSchedule"/>).
    /// <see cref="Fusion.PerFrame"/> skips the agreement check, the gate and the refit and places the map straight from
    /// each fix, for comparing against the joint fit.
    /// </remarks>
    [AddComponentMenu("StageSettAR/STAR Localizer")]
    public sealed class StarLocalizer : MonoBehaviour
    {
        public enum Phase { Startup, Early, Late }
        public enum LocalizerState { Idle, Initializing, Localized, Lost, Failed }
        public enum Fusion { Joint, PerFrame }
        // Advanced tuning lives in these groups, which the Inspector shows as collapsed foldouts. The defaults suit a
        // Quest 3 in a room-sized space; most projects never open them.

        [Serializable]
        public sealed class StartupSettings
        {
            [Tooltip("Look for the room before showing the map. Off: the first confident fix places the map, with no agreement needed.")]
            public bool enabled = true;
            [Tooltip("Camera frames analysed per second while the room is being found. Default 4: quick to find the room, cheap enough to leave the headset's frame rate alone.")]
            public float hz = 4f;
            [Tooltip("How many confident fixes must agree before the map is first shown (and again after a tracking reset). Default 3: one lucky wrong fix cannot place the map.")]
            [Range(1, 16)] public int fixesToShow = 3;
            [Tooltip("Give up ('not in this room') if the room is not found within this many seconds of starting. Also applies when Startup is off, to the first placement. Default 25: long enough to look all around once or twice.")]
            public float failAfterSeconds = 25f;
        }

        [Serializable]
        public sealed class EarlySettings
        {
            [Tooltip("Refine the placement with every fix after the map is shown. Off: go straight to the Late phase.")]
            public bool enabled = true;
            [Tooltip("Seconds between analysed frames. Default 0.5: about twice a second is plenty to refine a placement.")]
            public float intervalSeconds = 0.5f;
            [Tooltip("The phase ends once at least this many fixes were accepted... Default 4: one wrong fix agreeing with three others by chance is very unlikely.")]
            public int minFixes = 4;
            [Tooltip("...spread over a head turn of this many degrees... Default 30: fixes from one direction can share the same mistake; a turn rules that out.")]
            public float minTurnDeg = 30f;
            [Tooltip("...or over this many metres of camera travel on the floor. Default 0.5: seen from two places, a wrong match cannot keep agreeing.")]
            public float minMoveMeters = 0.5f;
            [Tooltip("A new fix that would move the map by more than this many metres (plus a margin for its own uncertainty) is rejected. Default 0.9: wider than later on, because the first placement may be off by more than drift.")]
            public float gateMeters = 0.9f;
            [Tooltip("...or turn it by more than this many degrees. Default 18. Three rejected fixes in a row that agree with each other restart the alignment from them.")]
            public float gateDegrees = 18f;
        }

        [Serializable]
        public sealed class LateSettings
        {
            [Tooltip("Keep checking the placement now and then. Off: the localizer stops looking once Early ends, and the map keeps its placement.")]
            public bool enabled = true;
            [Tooltip("Check every Interval Seconds.")]
            public bool useInterval = true;
            [Tooltip("Seconds between checks. Default 30: the headset's own tracking drifts slowly, so looking twice a minute is plenty.")]
            public float intervalSeconds = 30f;
            [Tooltip("Check early once the camera has moved more than Move Meters since the last check.")]
            public bool useMove = true;
            [Tooltip("Default 2: drift shows when the user moves away.")]
            public float moveMeters = 2f;
            [Tooltip("Check early once the head has turned more than Turn Degrees since the last check.")]
            public bool useTurn = true;
            [Tooltip("Default 60.")]
            public float turnDegrees = 60f;
            [Tooltip("Camera frames analysed per second during a check. Default 4.")]
            public float hz = 4f;
            [Tooltip("A check ends after this many seconds, sooner once three fixes that agree with each other have been collected. Default 3: about a dozen frames at the default rate.")]
            public float burstSeconds = 3f;
            [Tooltip("The placement is refitted only if three fixes that agree with each other put the map farther than this many metres from it (plus at most one sigma of their own noise). Default 0.05: a smaller shift is not worth moving the map for.")]
            public float refitMeters = 0.05f;
            [Tooltip("...or turned it by more than this many degrees. Default 2.")]
            public float refitDegrees = 2f;
        }

        [Serializable]
        public sealed class TimingSettings
        {
            [Tooltip("Seconds a corrected map position takes to slide into place (about 95 % of the way). Default 0.5: quick enough to follow, slow enough that nobody sees a jump.")]
            public float glideSeconds = 0.5f;
            [Tooltip("Slide every map move into place over Glide Seconds. Default on: refinements are not seen as jumps. Off: every move is applied at once. A move after tracking was reset always snaps.")]
            public bool smoothMoves = true;
            [Tooltip("A map move longer than this many metres is applied at once instead of gliding. Default 1: a slide that long would look like the room drifting. 0 turns it off.")]
            public float snapAboveMeters = 1f;
            [Tooltip("A map turn larger than this many degrees is applied at once instead of gliding. Default 20. 0 turns it off.")]
            public float snapAboveDegrees = 20f;
        }

        [Serializable]
        public sealed class GatingSettings
        {
            [Tooltip("Fixes less confident than this (0 to 1) are ignored. Default 0.5: drops guesses made from too few landmarks.")]
            [Range(0f, 1f)] public float minConfidence = 0.5f;
            [Tooltip("Two fixes agree if the map lands within this many metres of each other... Default 0.1: well above normal noise (a few centimetres), well below a wrong match.")]
            public float agreementMeters = 0.1f;
            [Tooltip("...and within this many degrees of turn. Default 3: the same reasoning for angle.")]
            public float agreementDegrees = 3f;
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
                     "Per Frame: put the map exactly where each single camera frame says, with no agreement check, gate or smoothing, and the Early phase never ends. " +
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
        [Tooltip("The child of the map root where your objects go. In the editor the rest of the map root (splat, floor) is locked; this object's children are not.")]
        public Transform content;
        [Tooltip("Start looking for the room as soon as the scene starts. Turn off to call StartLocalization yourself.")]
        public bool autoStart = true;
        [Tooltip("Keep the map root's children inactive until the first localization, so nothing appears in the wrong place.")]
        public bool hideUntilLocalized = true;
        [Tooltip("Also localize in the editor's Play mode. Off (default): in the editor the localizer does nothing, so the map, splat and your content stay where you placed them and the Localizing Screen stays hidden while you test with the XR Interaction Simulator. Device builds always localize.")]
        public bool localizeInEditor;

        [Header("Phases")]
        [Tooltip("Finding the room: the map stays hidden until a few confident fixes agree, then it is placed and shown.")]
        public StartupSettings startup = new StartupSettings();
        [Tooltip("Right after the map is shown: every fix refines the placement, until enough of the room has been seen.")]
        public EarlySettings early = new EarlySettings();
        [Tooltip("Afterwards: a short check now and then, which refits the placement only if the map has moved.")]
        public LateSettings late = new LateSettings();

        [Header("Tuning (optional)")]
        [Tooltip("How fast corrections glide in.")]
        public TimingSettings timing = new TimingSettings();
        [Tooltip("Which fixes are trusted and when tracking counts as reset.")]
        public GatingSettings gating = new GatingSettings();
        [Tooltip("Rarely needed: the network backend, pose solver and alignment fit.")]
        public AdvancedSettings advanced = new AdvancedSettings();
        [Tooltip("For comparing against the default fusion.")]
        public DebugSettings debug = new DebugSettings();

        [Header("Events")]
        [Tooltip("Starts when the localizer begins looking for the room (at start-up, or after Relocalize).")]
        public UnityEvent onLocalizing = new UnityEvent();
        [Tooltip("Starts the first time the room is found, and only that once. The place to hook up an intro: a fade, a reveal, a sound.")]
        public UnityEvent onFirstLocalized = new UnityEvent();
        [Tooltip("Starts every time the room is found, including after tracking came back and the placement was kept.")]
        public UnityEvent onLocalized = new UnityEvent();
        [Tooltip("Starts when enough of the room has been seen for the placement to be trusted and the Late phase begins (at once when Early is off). " +
                 "Starts again after the map was placed anew, and after a re-seed restarted the Early phase.")]
        public UnityEvent onSettled = new UnityEvent();
        [Tooltip("Starts when the map was shown and the device then lost tracking (its own tracking was lost, the headset was taken off, or tracking was reset). Nothing is inferred until tracking is back.")]
        public UnityEvent onLost = new UnityEvent();
        [Tooltip("Starts when the localizer gives up. Passes a short reason.")]
        public UnityEvent<string> onFailed = new UnityEvent<string>();
        [Tooltip("Starts on every change of state (Idle, Initializing, Localized, Lost, Failed) and passes the new state.")]
        public UnityEvent<LocalizerState> onStateChanged = new UnityEvent<LocalizerState>();

        /// <summary>Initializing is the Startup phase before the first placement. Lost is the device having no tracking, or just having had it reset.</summary>
        public LocalizerState State { get; private set; }
        public Phase CurrentPhase => _schedule.Phase;
        /// <summary>The alignment the map root is gliding to (map to tracking space). Invalid until localized, and again
        /// after a tracking reset until the next fix, since the old one no longer describes the tracking space.</summary>
        public Alignment CurrentAlignment => _placed ? _target : default;
        /// <summary>Confidence of the last accepted fix.</summary>
        public float LastConfidence { get; private set; }

        /// <summary>Seconds left before the localizer gives up and fails. 0 when it is not in the initial search.</summary>
        public float SecondsUntilGiveUp
        {
            get
            {
                if (State != LocalizerState.Initializing) return 0f;
                if (_initStart < 0) return startup.failAfterSeconds;
                return Mathf.Max(0f, startup.failAfterSeconds - (float)(Time.realtimeSinceStartupAsDouble - _initStart));
            }
        }

        // det holds only the PnP inliers (copied: detection buffers are reused), so a consensus can seed the solver later.
        struct Candidate { public Alignment a; public Pose cam; public double time; public Detections det; }
        sealed class PnpWork
        {
            public StarPnp solver; public Vector3d[] p3; public Vector2d[] p2; public double[] score;
        }

        const int MaxCandidates = 32;
        const int ConsistentFixes = 3;           // fixes that must agree to re-seed (Early) or to judge a Late check
        const double CandidateSeconds = 10.0;    // fixes that are to agree must all be this recent
        const double FrameGapSeconds = 2.0;      // frames farther apart than this are not compared for a tracking jump

        StarDetector _detector;
        StarMap _detectorMap; // the map _detector was built for: its network and landmark count
        AlignmentSolver _solver;
        CancellationTokenSource _cts;
        Vector3d[] _exportLandmarks;
        Vector3 _centroid;
        Alignment _current, _target, _shown;
        bool _placed, _visible = true, _warmed, _warnedNoPose;
        readonly List<Candidate> _cands = new List<Candidate>();
        readonly List<GameObject> _hidden = new List<GameObject>();
        long _lastId;
        double _initStart;
        bool _resume;
        readonly LocalizerSchedule _schedule = new LocalizerSchedule();
        readonly CoverageTracker _coverage = new CoverageTracker();
        int _epoch;   // changes when the alignment is discarded, so a frame that was being analysed at the time is dropped
        Vector3 _headPos;   // the tracked camera in tracking space, sampled every frame
        bool _hasHead;
        float _headHeading;
        int _rejects, _initFixes;
        Alignment _lastReject;
        bool _everLocalized;
        int _checkNumber;   // the Late check the candidates belong to

        Transform Root => mapRoot ? mapRoot : transform;

        // How many agreeing fixes the next placement or refit needs.
        int FixesNeeded => CurrentPhase == Phase.Late ? ConsistentFixes : startup.enabled ? Math.Max(1, startup.fixesToShow) : 1;

        ScheduleSettings ScheduleNow => new ScheduleSettings
        {
            startupHz = startup.hz,
            earlyEnabled = early.enabled, earlyIntervalSeconds = early.intervalSeconds,
            lateEnabled = late.enabled, lateByTime = late.useInterval, lateByMove = late.useMove, lateByTurn = late.useTurn,
            lateIntervalSeconds = late.intervalSeconds, lateMoveMeters = late.moveMeters, lateTurnDegrees = late.turnDegrees,
            lateBurstSeconds = late.burstSeconds, lateHz = late.hz,
        };

        void Reset() { mapRoot = transform; }

        // There is no room to find in the editor (no passthrough camera), and testing content means seeing the map as
        // authored, so Play mode in the editor leaves the localizer switched off unless asked otherwise.
        bool SkippedInEditor => Application.isEditor && !localizeInEditor;

        void Awake() { if (SkippedInEditor) enabled = false; }

        void Start() { if (autoStart) StartLocalization(); }

        // OnDisable stops the loop; carry on from where it was. A found alignment keeps being tracked rather than
        // searched for again, and a failure stays failed until the user retries.
        void OnEnable()
        {
            if (!_resume) return;
            _resume = false;
            if (State == LocalizerState.Failed) return;
            if (State != LocalizerState.Localized) { StartLocalization(); return; }
            _schedule.Resume();
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
        /// (Re)starts localization from scratch in the Startup phase: forgets the fit, keeps the map where it is until a new one is found.
        /// </summary>
        public void StartLocalization()
        {
            if (SkippedInEditor) return;
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
            _solver = new AlignmentSolver(advanced.alignment, map);
            _current = default; _cands.Clear(); _rejects = 0; _initFixes = 0;
            _initStart = -1; _schedule.Reset(); _epoch++;
            _coverage.Reset();
            _centroid = Vector3.zero;
            foreach (var p in map.LandmarksUnity) _centroid += p;
            if (map.LandmarkCount > 0) _centroid /= map.LandmarkCount;
            if (hideUntilLocalized && !_placed) HideChildren();
            SetState(LocalizerState.Initializing);
            _ = Run(_cts.Token);
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
                    // Checked before any frame is skipped, so stale or missing frames cannot delay giving up.
                    if (State == LocalizerState.Initializing && _initStart >= 0 && !_schedule.TrackingLost && now - _initStart > startup.failAfterSeconds)
                    {
                        Fail(_initFixes == 0
                            ? "Not in this room: no known landmarks were recognised."
                            : "Not in this room: the landmark fixes never agreed.");
                        return;
                    }
                    ScheduleSettings sched = ScheduleNow;
                    if (!_schedule.WantsFrames(now, _headPos, _headHeading, sched)) continue;
                    if (!_schedule.ShouldInfer(now) || _detector.Busy || !cameraSource.IsReady) continue;
                    if (!cameraSource.TryGetFrame(_lastId, out CameraFrame frame)) continue;
                    _lastId = frame.id;
                    if (!frame.hasPose)
                    {
                        if (!_warnedNoPose) { _warnedNoPose = true; Debug.LogWarning("[StageSettAR] Camera source delivers frames without a pose; they are skipped."); }
                        continue;
                    }
                    _schedule.OnInferred(now, sched);
                    if (State == LocalizerState.Initializing && _initStart < 0) _initStart = now;
                    // A source that cannot report its pose between frames is watched on the frames it delivers.
                    if (!cameraSource.TryGetCurrentPose(out _)) SampleHeadFromFrame(frame);

                    int epoch = _epoch;
                    _detector.LayersPerFrame = advanced.layersPerFrame;
                    _detector.InputScale = advanced.inputScale;
                    Detections d = await _detector.DetectAsync(frame, ct);
                    ct.ThrowIfCancellationRequested();
                    if (IsStale(epoch, frame)) continue;
                    await ProcessFrame(work, frame, d, epoch, ct);
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested) { Debug.LogException(e, this); Fail("Localizer error: " + e.Message); }
            }
        }

        // A frame is dropped if the alignment was discarded while it was analysed, if tracking went away meanwhile, or if it
        // was captured before tracking was reset (its pose is in the old tracking space). A frame captured after the reset
        // is good for the new alignment.
        bool IsStale(int epoch, in CameraFrame frame) => epoch != _epoch || _schedule.TrackingLost || _schedule.PredatesJump(frame.timestamp);

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
            if (IsStale(epoch, frame)) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!r.success || r.confidence < gating.minConfidence) return;

            Pose cameraInMap = Conventions.OpenCvCameraToUnity(r.R * turn.Transpose(), r.t);
            Alignment a = AlignmentSolver.FromSingleFix(cameraInMap, frame.cameraPose);
            a.positionSigma = (float)r.positionSigma; a.yawSigmaDeg = (float)r.rotationSigmaDeg;
            LastConfidence = (float)r.confidence;

            if (debug.fusion == Fusion.PerFrame)
            {
                _current = a;
                _initFixes++;
                Place(a, true);
                if (State != LocalizerState.Localized) Found();
                return;
            }

            if (!_current.valid)
            {
                // Startup: collect fixes and look for a consensus. They are not in the solver window yet, because frames
                // from a wrong (non-consensus) cluster would pollute the first Solve.
                _initFixes++;
                AddCandidate(w, frame, d, a, now);
                if (TryConsensus(out Alignment seed))
                {
                    PlaceFromCluster(seed);
                    Found();
                }
            }
            else if (CurrentPhase == Phase.Late) CheckPlacement(w, frame, d, a, now);
            else RefinePlacement(w, frame, d, a, now);
        }

        // Early: every fix inside the gate refines the placement. Fixes outside it are rejected, until three in a row agree
        // with each other: then the old alignment is stale and the fit starts again from them.
        void RefinePlacement(PnpWork w, CameraFrame frame, Detections d, Alignment a, double now)
        {
            if (PlacementCheck.Beyond(_current, a, _centroid, early.gateMeters, early.gateDegrees))
            {
                _rejects = _rejects > 0 && Agree(_lastReject, a) ? _rejects + 1 : 1;
                _lastReject = a;
                if (_rejects >= ConsistentFixes) ReseedFrom(w, frame, d, a, now);
                return;
            }
            _rejects = 0;
            _solver.AddFrame(d, w.solver.InlierMask, frame.cameraPose, now);
            Alignment fit = _solver.Solve(_current);
            if (fit.valid) { _current = fit; Place(fit, false); }
            AddCoverage(frame.cameraPose);
        }

        // Late: the fixes of a check are collected. Once enough of them agree with each other the check is over, and the
        // placement is refitted from them only if they put the map somewhere else than where it is.
        void CheckPlacement(PnpWork w, CameraFrame frame, Detections d, Alignment a, double now)
        {
            if (!_schedule.Checking) return;
            if (_checkNumber != _schedule.Checks) { _checkNumber = _schedule.Checks; _cands.Clear(); }
            AddCandidate(w, frame, d, a, now);
            if (!TryConsensus(out Alignment seed)) return;
            if (PlacementCheck.Deviates(_current, seed, _centroid, late.refitMeters, late.refitDegrees)) PlaceFromCluster(seed);
            _cands.Clear();
            _schedule.EndCheck();
        }

        void Found()
        {
            ShowChildren();
            SetState(LocalizerState.Localized);
            onLocalized.Invoke();
            if (!_everLocalized) { _everLocalized = true; onFirstLocalized.Invoke(); }
            _schedule.Enter(LocalizerSchedule.AfterPlacement(ScheduleNow));
            if (CurrentPhase == Phase.Late) onSettled.Invoke();
            else CheckSettled();
        }

        void AddCoverage(in Pose camera)
        {
            _coverage.Add(camera.position, Heading(camera));
            CheckSettled();
        }

        // Early ends once the accepted fixes cover enough of the room.
        void CheckSettled()
        {
            if (CurrentPhase != Phase.Early || !_coverage.IsComplete(early.minFixes, early.minTurnDeg, early.minMoveMeters)) return;
            _schedule.Enter(Phase.Late);
            onSettled.Invoke();
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

        void AddCandidate(PnpWork w, CameraFrame frame, Detections d, Alignment a, double now)
        {
            _cands.Add(new Candidate { a = a, cam = frame.cameraPose, time = now, det = CopyInliers(d, w.solver.InlierMask) });
            _cands.RemoveAll(c => c.time < now - CandidateSeconds);
            if (_cands.Count > MaxCandidates) _cands.RemoveAt(0);
        }

        /// <summary>Fits the window to the fixes that agree with <paramref name="seed"/> alone, and places the result.</summary>
        void PlaceFromCluster(in Alignment seed)
        {
            _solver.Clear();
            foreach (var c in _cands)
            {
                if (!Agree(seed, c.a)) continue;
                _solver.AddFrame(c.det, null, c.cam, c.time);
                if (CurrentPhase == Phase.Startup) _coverage.Add(c.cam.position, Heading(c.cam));   // the fixes that placed the map count as covered
            }
            Alignment fit = _solver.Solve(seed);
            _current = fit.valid ? fit : seed;
            _cands.Clear();
            Place(_current, !_visible || !_placed);
            _rejects = 0;
        }

        void ReseedFrom(PnpWork w, CameraFrame frame, Detections d, Alignment a, double now)
        {
            Debug.Log("[StageSettAR] Fixes consistently disagree with the current alignment; re-seeding from them.");
            _solver.Clear();
            _solver.AddFrame(d, w.solver.InlierMask, frame.cameraPose, now);
            Alignment fit = _solver.Solve(a);
            _current = fit.valid ? fit : a;
            Place(_current, false);
            _rejects = 0;
            // A restarted fit has to be confirmed again, at the Early rate.
            _coverage.Reset();
            AddCoverage(frame.cameraPose);
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

        // ------------------------------------------------------------------ agreement

        bool Agree(in Alignment a, in Alignment b) =>
            PlacementCheck.Agree(a, b, _centroid, gating.agreementMeters, gating.agreementDegrees);

        /// <summary>Largest group of mutually agreeing fixes (around the best medoid), if it is big enough.</summary>
        bool TryConsensus(out Alignment seed)
        {
            seed = default;
            int best = BestCluster(out int bestCount);
            if (best < 0 || bestCount < FixesNeeded) return false;
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

        // Yaw of where the camera looks. Looking straight up or down has no yaw, so the previous heading is kept.
        float Heading(Pose p)
        {
            Vector3 f = p.rotation * Vector3.forward;
            if (f.x * f.x + f.z * f.z < 1e-6f) return _headHeading;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        // ------------------------------------------------------------------ state

        // Tracking was reset, so the old alignment describes a tracking space that no longer exists. Back to Startup, with
        // the map hidden until it is placed again. After a jump, frames captured before it are told apart by their capture
        // time; otherwise the ones being analysed now are dropped by epoch.
        void RestartStartup(bool jump)
        {
            _current = default; _cands.Clear(); _rejects = 0;
            if (!jump) _epoch++;
            _coverage.Reset();
            _solver.Clear();
            if (hideUntilLocalized) HideChildren();
            _placed = false;   // snap to the new alignment instead of gliding from a stale one
            _initStart = -1;
            _schedule.Enter(Phase.Startup);
            SetState(LocalizerState.Initializing);
        }

        // A jump means the headset's tracking was reset: the same as tracking coming back after a loss.
        void OnJump()
        {
            LoseTracking();
            RestartStartup(true);
        }

        void LoseTracking()
        {
            if (State != LocalizerState.Localized) return;
            SetState(LocalizerState.Lost);
            onLost.Invoke();
        }

        // The device's own tracking is the only thing that loses the room: missing fixes just mean nothing gets refined.
        void UpdateTracking()
        {
            bool worn = cameraSource.IsWorn;
            switch (_schedule.UpdateTracking(worn && cameraSource.IsTracking, worn))
            {
                case LocalizerSchedule.TrackingChange.Lost:
                    LoseTracking();
                    break;
                case LocalizerSchedule.TrackingChange.Resumed:
                    _initStart = -1;
                    if (State == LocalizerState.Lost) { SetState(LocalizerState.Localized); onLocalized.Invoke(); }
                    break;
                case LocalizerSchedule.TrackingChange.RestartNeeded:
                    RestartStartup(false);
                    break;
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
            _hasHead = true;
            return true;
        }

        // The fallback for sources without TryGetCurrentPose. A pose from before a rest is too old to compare with,
        // because walking in between is not a tracking reset.
        void SampleHeadFromFrame(in CameraFrame frame)
        {
            _headPos = frame.cameraPose.position;
            _headHeading = Heading(frame.cameraPose);
            _hasHead = true;
            if (_schedule.TrackJump(_headPos, gating.trackingJumpMeters, frame.timestamp, FrameGapSeconds)) OnJump();
        }

        /// <summary>Every frame, so the map follows XR Origin locomotion (the alignment is in tracking space).</summary>
        void LateUpdate()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (Localizing) UpdateTracking();
            // The jump test looks at every frame, not only the analysed ones, so a long walk never looks like a jump.
            // A pose reported while tracking is missing means nothing, so it is not sampled.
            if (!_schedule.TrackingLost && SampleHead() && _schedule.TrackJump(_headPos, gating.trackingJumpMeters, now) && Localizing)
                OnJump();

            if (!_placed) return;
            Glide();
            Alignment s = _shown;
            Transform ts = cameraSource ? cameraSource.TrackingSpace : null;
            Matrix4x4 m = (ts ? ts.localToWorldMatrix : Matrix4x4.identity) * s.MapToTracking;
            Root.SetPositionAndRotation(m.GetPosition(), m.rotation);
        }

        /// <summary>Moves the shown placement towards the target for this frame; a large move is applied at once.</summary>
        void Glide()
        {
            var settings = new GlideSettings
            {
                smooth = timing.smoothMoves, seconds = timing.glideSeconds,
                snapMeters = timing.snapAboveMeters, snapDegrees = timing.snapAboveDegrees,
            };
            // The size of a move is judged where the user is, or at the landmarks when the head position is unknown.
            Vector3 pivot = _hasHead ? _headPos : _shown.MapToTracking.MultiplyPoint3x4(_centroid);
            _shown = AlignmentGlide.Step(_shown, _target, pivot, Time.unscaledDeltaTime, settings);
        }

        // True while the run loop is going.
        bool Localizing =>
            _solver != null && _cts != null && !_cts.IsCancellationRequested
            && (State == LocalizerState.Initializing || State == LocalizerState.Lost || State == LocalizerState.Localized);

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
            foreach (var p in map.LandmarksUnity) Gizmos.DrawSphere(p, 0.003f);
        }
    }
}
