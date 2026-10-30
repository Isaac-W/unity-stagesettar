using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Runs a map's network and PnP on the held-out frames of a recorded scan and compares the poses with the scan's
    /// ground truth, at several image scales. It mirrors star-web-demo's holdout evaluation, so the numbers are
    /// comparable with the ones stored in the map. Detection and PnP are called directly, without the localizer's fusion.
    /// </summary>
    public sealed class StarScanEvaluatorWindow : EditorWindow
    {
        sealed class ScaleResult
        {
            public float scale;
            public HoldoutScore score;
            public double meanDetections, meanInliers, msPerFrame;
            public int failures;
        }

        const int ColumnWidth = 74;
        static readonly string[] k_columns =
            { "Scale", "Frames", "5cm/5°  %", "10cm/10°  %", "Median cm", "Median °", "Detections", "Inliers", "Failures", "ms/frame" };

        [SerializeField] StarMap m_map;
        [SerializeField] string m_scanPath = "";
        [SerializeField] BackendType m_backend = BackendType.GPUCompute;
        [SerializeField] string m_scales = "0.25, 0.5, 0.75, 1";
        [SerializeField] CropAspect m_crop = CropAspect.None;
        [SerializeField] Vector2 m_customCrop = new Vector2(4f, 3f);

        PnpSettings m_pnp = PnpSettings.Default;
        bool m_showPnp;
        List<ScaleResult> m_results;
        Vector2Int m_frameSize;   // size of the frames of the last run, after cropping
        string m_error;
        Vector2 m_scroll;

        [MenuItem("Tools/StageSettAR/Test Map Against Scan…", false, 21)]
        static void Open() => GetWindow<StarScanEvaluatorWindow>("Test Map Against Scan");

        void OnGUI()
        {
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            m_map = (StarMap)EditorGUILayout.ObjectField("STAR map", m_map, typeof(StarMap), false);

            EditorGUILayout.BeginHorizontal();
            m_scanPath = EditorGUILayout.TextField("Scan (.starscan or folder)", m_scanPath);
            if (GUILayout.Button("File…", GUILayout.Width(50)))
            {
                var path = EditorUtility.OpenFilePanel("STAR scan", "", "starscan");
                if (!string.IsNullOrEmpty(path)) m_scanPath = path;
            }
            if (GUILayout.Button("Folder…", GUILayout.Width(60)))
            {
                var path = EditorUtility.OpenFolderPanel("Unpacked STAR scan", "", "");
                if (!string.IsNullOrEmpty(path)) m_scanPath = path;
            }
            EditorGUILayout.EndHorizontal();

            m_backend = (BackendType)EditorGUILayout.EnumPopup("Backend", m_backend);
            m_scales = EditorGUILayout.TextField(new GUIContent("Image scales",
                "Comma separated. 1 feeds the image at the map's trained focal length; smaller values shrink it, which is faster and finds fewer landmarks."), m_scales);

            m_crop = (CropAspect)EditorGUILayout.EnumPopup(new GUIContent("Crop aspect",
                "Centre-crops each upright frame to this shape, to imitate a camera with another aspect ratio. None keeps the frame as stored."), m_crop);
            if (m_crop == CropAspect.Custom) m_customCrop = EditorGUILayout.Vector2Field("Width : Height", m_customCrop);

            m_showPnp = EditorGUILayout.Foldout(m_showPnp, "PnP settings", true);
            if (m_showPnp)
            {
                EditorGUI.indentLevel++;
                m_pnp.ransacThresholdPx = EditorGUILayout.DoubleField("RANSAC threshold (px)", m_pnp.ransacThresholdPx);
                m_pnp.confidence = EditorGUILayout.DoubleField("Confidence", m_pnp.confidence);
                m_pnp.maxIterations = EditorGUILayout.IntField("Max iterations", m_pnp.maxIterations);
                m_pnp.huberPx = EditorGUILayout.DoubleField("Huber (px)", m_pnp.huberPx);
                m_pnp.reselectPx = EditorGUILayout.DoubleField("Re-select gate (px)", m_pnp.reselectPx);
                m_pnp.minInliers = EditorGUILayout.IntField("Min inliers", m_pnp.minInliers);
                EditorGUILayout.HelpBox("The seed is set to the frame index for every frame, so runs are reproducible.", MessageType.None);
                if (GUILayout.Button("Reset to defaults", GUILayout.Width(130))) m_pnp = PnpSettings.Default;
                EditorGUI.indentLevel--;
            }

            float[] scales = ParseScales(m_scales);
            using (new EditorGUI.DisabledScope(m_map == null || string.IsNullOrEmpty(m_scanPath) || scales.Length == 0))
            {
                if (GUILayout.Button("Run", GUILayout.Height(28)))
                {
                    m_error = null;
                    try
                    {
                        var run = Evaluate(m_map, m_scanPath, m_backend, m_pnp, scales,
                            StarScan.AspectRatio(m_crop, m_customCrop.x, m_customCrop.y), out Vector2Int size);
                        if (run != null) { m_results = run; m_frameSize = size; }
                    }
                    catch (Exception e) { m_error = e.Message; Debug.LogException(e); }
                    finally { EditorUtility.ClearProgressBar(); }
                }
            }

            if (m_error != null) EditorGUILayout.HelpBox(m_error, MessageType.Error);
            if (m_results != null) DrawResults();
            EditorGUILayout.EndScrollView();
        }

        void DrawResults()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Frame size after crop", $"{m_frameSize.x} x {m_frameSize.y}");
            EditorGUILayout.BeginHorizontal();
            foreach (var c in k_columns) GUILayout.Label(c, EditorStyles.boldLabel, GUILayout.Width(ColumnWidth));
            EditorGUILayout.EndHorizontal();
            foreach (var r in m_results)
            {
                EditorGUILayout.BeginHorizontal();
                Cell(r.scale.ToString("0.##", CultureInfo.InvariantCulture));
                Cell(r.score.frames.ToString());
                Cell(Fmt(r.score.recall5cm5deg, "F1"));
                Cell(Fmt(r.score.recall10cm10deg, "F1"));
                Cell(Fmt(r.score.medianCm, "F1"));
                Cell(Fmt(r.score.medianDeg, "F2"));
                Cell(Fmt(r.meanDetections, "F1"));
                Cell(Fmt(r.meanInliers, "F1"));
                Cell(r.failures.ToString());
                Cell(Fmt(r.msPerFrame, "F0"));
                EditorGUILayout.EndHorizontal();
            }

            if (m_map != null && (m_map.recall5cm5deg > 0f || m_map.recall10cm10deg > 0f))
                EditorGUILayout.HelpBox(string.Format(CultureInfo.InvariantCulture,
                    "Stored with the map (holdout of its training run, scale 1): {0:F1}% within 5cm/5deg, {1:F1}% within 10cm/10deg, median {2:F1}cm {3:F2}deg.",
                    m_map.recall5cm5deg, m_map.recall10cm10deg, m_map.medianCm, m_map.medianDeg), MessageType.Info);
            else
                EditorGUILayout.HelpBox("The map has no stored holdout figures.", MessageType.None);

            if (GUILayout.Button("Copy as CSV", GUILayout.Width(110))) EditorGUIUtility.systemCopyBuffer = ToCsv(m_results, m_frameSize);
        }

        static void Cell(string text) => GUILayout.Label(text, GUILayout.Width(ColumnWidth));

        static string Fmt(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

        static float[] ParseScales(string text)
        {
            var list = new List<float>();
            foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0f) list.Add(s);
            return list.ToArray();
        }

        static string ToCsv(List<ScaleResult> results, Vector2Int size)
        {
            var sb = new StringBuilder("scale,frames,recall5cm5deg,recall10cm10deg,medianCm,medianDeg,meanDetections,meanInliers,failures,msPerFrame,frameWidth,frameHeight\n");
            foreach (var r in results)
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2:F2},{3:F2},{4:F2},{5:F3},{6:F2},{7:F2},{8},{9:F1},{10},{11}\n",
                    r.scale, r.score.frames, r.score.recall5cm5deg, r.score.recall10cm10deg, r.score.medianCm, r.score.medianDeg,
                    r.meanDetections, r.meanInliers, r.failures, r.msPerFrame, size.x, size.y);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- evaluation

        /// <returns>One result per scale, or null when the user cancelled.</returns>
        static List<ScaleResult> Evaluate(StarMap map, string scanPath, BackendType backend, PnpSettings pnp, float[] scales,
                                          float cropAspect, out Vector2Int frameSize)
        {
            frameSize = default;
            using var scan = StarScan.Open(scanPath);
            List<int> test = scan.TestFrames();
            if (test.Count == 0) throw new InvalidOperationException("The scan has no test frames with ground truth.");

            int n = map.LandmarkCount;
            var landmarks = new Vector3d[n];
            for (int i = 0; i < n; i++) landmarks[i] = new Vector3d(map.landmarksExport[i]);
            var p3 = new Vector3d[n];
            var p2 = new Vector2d[n];
            var score = new double[n];

            var results = new List<ScaleResult>();
            var detector = new StarDetector(map, backend);
            try
            {
                for (int s = 0; s < scales.Length; s++)
                {
                    detector.InputScale = scales[s];
                    var meters = new List<double>();
                    var degrees = new List<double>();
                    double detections = 0, inliers = 0, timedMs = 0;
                    int failures = 0, timedFrames = 0;
                    var watch = new Stopwatch();

                    for (int k = 0; k < test.Count; k++)
                    {
                        if (EditorUtility.DisplayCancelableProgressBar("Testing map against scan",
                                $"Scale {scales[s]:0.##} ({s + 1}/{scales.Length}), frame {k + 1}/{test.Count}",
                                (s + (float)k / test.Count) / scales.Length)) return null;

                        UprightFrame up = scan.LoadUpright(test[k], cropAspect);
                        try
                        {
                            frameSize = new Vector2Int(up.texture.width, up.texture.height);
                            var frame = new CameraFrame
                            {
                                texture = up.texture,
                                flipY = true,
                                intrinsics = up.intrinsics,
                                id = k + 1,
                                // The ground-truth pose picks the network's quarter turn from gravity, as the localizer does.
                                cameraPose = Conventions.OpenCvCameraToUnity(up.R, up.t),
                                hasPose = true,
                            };

                            // The seed is fixed per frame (plus one, since 0 means random), so a rerun gives the same numbers.
                            // The solver copies the settings when it is built, so it is built after the seed and outside the timing.
                            pnp.seed = scan.Frames[test[k]].index + 1;
                            var solver = new StarPnp(n, pnp);

                            watch.Restart();
                            Detections d = detector.DetectBlocking(frame);
                            StarLocalizer.FillCorrespondences(d, landmarks, p3, p2, score);
                            CameraIntrinsics ki = d.netIntrinsics;
                            PnpResult r = solver.Solve(d.count, p3, p2, score, ki.fx, ki.fy, ki.cx, ki.cy, ki.width, ki.height);
                            watch.Stop();
                            // The first frame at a new scale also builds the network, so it is left out of the timing.
                            if (k > 0 || test.Count == 1) { timedMs += watch.Elapsed.TotalMilliseconds; timedFrames++; }

                            detections += d.count;
                            inliers += r.inliers;
                            if (r.success)
                            {
                                // The solve is for the turned image the network saw; undo the turn to compare with the upright camera.
                                Matrix3x3d rUpright = r.R * Conventions.QuarterTurnCameraRotation(d.quarterTurns).Transpose();
                                PoseMetrics.Distance(rUpright, r.t, up.R, up.t, out double m, out double deg);
                                meters.Add(m);
                                degrees.Add(deg);
                            }
                            else
                            {
                                failures++;
                                meters.Add(double.PositiveInfinity);
                                degrees.Add(double.PositiveInfinity);
                            }
                        }
                        finally { UnityObjects.Discard(up.texture, immediate: true); }
                    }

                    results.Add(new ScaleResult
                    {
                        scale = scales[s],
                        score = PoseMetrics.Score(meters, degrees),
                        meanDetections = detections / test.Count,
                        meanInliers = inliers / test.Count,
                        failures = failures,
                        msPerFrame = timedMs / Math.Max(1, timedFrames),
                    });
                }
            }
            finally { detector.Dispose(); }
            return results;
        }
    }
}
