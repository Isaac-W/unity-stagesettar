using System;
using UnityEngine;

namespace StageSettAR
{
    [Serializable]
    public sealed class SessionScaleSettings
    {
        [Tooltip("Correct the map's size (a few per cent) from how far you walk. On by default; turn off to keep the map at exactly 1 unit per metre. Everything below is optional tuning.")]
        public bool enabled = true;
        [Tooltip("The scale is only learned after the headset has moved this far: the RMS spread of its positions, in metres. Default 0.5: a few steps around the room; standing still or turning your head never gets there.")]
        public float minSpreadMeters = 0.5f;
        [Tooltip("...and after at least this many accepted fixes. Default 10: a few seconds of looking, so one fix cannot decide.")]
        public int minFixes = 10;
        [Tooltip("A fix this many sigma away from the current scale fit is left out of the scale (not out of the alignment). Default 3: the usual cut-off for a measurement that is almost certainly wrong.")]
        public float outlierSigmas = 3f;
        [Tooltip("Fastest the scale may change, per second (0.003 = 0.3 %). Default 0.003: a 4 % correction takes about 13 seconds, slowly enough never to be noticed. 0 turns the limit off.")]
        public float maxRatePerSecond = 0.003f;
        [Tooltip("The scale never goes below this... Default 0.9: maps are exported at 1 unit = 1 m, so the real error is a few per cent.")]
        public float minScale = 0.9f;
        [Tooltip("...or above this. Default 1.1. An estimate that sits at a limit means a badly scaled map, not a small tweak: a warning is logged once.")]
        public float maxScale = 1.1f;
        [Tooltip("The scale used until it can be told. Default 1: the map is exported at 1 unit = 1 m.")]
        public float priorScale = 1f;

        public static SessionScaleSettings Default => new SessionScaleSettings();
    }

    /// <summary>
    /// Estimates the map's scale error once for the whole session, from the history of fixes,
    /// taking the headset's tracking distances as the true metres.
    /// </summary>
    /// <remarks>
    /// Each fix is a camera position c in the map and t in tracking space, related by
    /// t = s · Ry(yaw) · c + T. Fixes are grouped in segments (one alignment, one tracking frame);
    /// yaw and T belong to a segment, the scale s is shared. Tracking is nearly noise-free and the
    /// PnP positions are not, so map is regressed onto tracking, which avoids the dilution bias of
    /// regressing the other way. With values centred per segment (c~, t~) and weights w = 1/sigma^2:
    /// s = sum w |t~|^2 / sum w (R c~) . t~, with (R c~) . t~ = trace(R · sum w c~ t~^T). Only per-segment
    /// sums are kept, so an update costs a few dozen multiplications and allocates nothing.
    /// A segment's yaw is the 2D Procrustes angle of its own positions in XZ when it spans enough ground
    /// for that to be well determined, else the weighted circular mean of the alignment yaws passed in
    /// (the fit's yaw is image-based and accurate; the error of a yaw only enters the cross term as cos).
    /// Standing still or turning on the spot gives |t~| ~ 0, so nothing is learned and
    /// <see cref="IsObservable"/> stays false. Not thread-safe; no Unity API is used in the math.
    /// </remarks>
    public sealed class SessionScaleEstimator
    {
        const double MinSigma = 0.005;           // metres; keeps one overconfident fix from owning the sums
        const double ProcrustesMinSpread = 0.5;  // metres, horizontal RMS of a segment
        const double MaxAdvanceSeconds = 1.0;    // a pause must not allow a jump

        readonly SessionScaleSettings s;
        readonly double min, max, prior;

        // Closed segments, reduced to their share of the two sums of the ratio.
        double closedNum, closedDen, closedW;
        int closedN;

        // The open segment, in coordinates shifted by its first fix (centring is shift-invariant;
        // shifting keeps the sums small).
        int n;
        double w;
        readonly double[] c0 = new double[3], t0 = new double[3];
        readonly double[] sumC = new double[3], sumT = new double[3], sumCT = new double[9];
        double sumTT, sumTTxz, sumYawCos, sumYawSin;
        readonly double[] pc = new double[3], pt = new double[3];

        double applied, lastTime = double.NaN;
        bool warned;

        public SessionScaleEstimator(SessionScaleSettings settings)
        {
            s = settings ?? new SessionScaleSettings();
            double lo = Math.Max(0.01, Math.Min(s.minScale, s.maxScale));
            double hi = Math.Max(lo, Math.Max(s.minScale, s.maxScale));
            min = lo; max = hi;
            prior = Math.Min(hi, Math.Max(lo, s.priorScale > 0 ? s.priorScale : 1.0));
            applied = prior;
        }

        /// <summary>Accepted fixes so far, over all segments. Fixes left out as outliers are not counted.</summary>
        public int FixCount => closedN + n;

        /// <summary>RMS spread of the tracked camera positions around each segment's centre, metres.</summary>
        public double SpreadMeters
        {
            get
            {
                Totals(out double num, out _, out double wt, out _);
                return wt > 0 ? Math.Sqrt(num / wt) : 0;
            }
        }

        /// <summary>True once the camera has moved enough, over enough fixes, for the scale to be told.</summary>
        public bool IsObservable
        {
            get
            {
                Totals(out double num, out _, out double wt, out int count);
                return count >= s.minFixes && wt > 0 && Math.Sqrt(num / wt) >= s.minSpreadMeters;
            }
        }

        /// <summary>The raw estimate (tracking metres per map unit): the prior until observable. Not clamped.</summary>
        public double Scale
        {
            get
            {
                Totals(out double num, out double den, out double wt, out int count);
                bool observable = count >= s.minFixes && wt > 0 && Math.Sqrt(num / wt) >= s.minSpreadMeters;
                return observable && den > 1e-12 && num > 0 ? num / den : prior;
            }
        }

        /// <summary>
        /// Approximate standard deviation of <see cref="Scale"/>: sigma_pos / RMS spread / sqrt(N_eff),
        /// computed as s^2 / sqrt(sum w |t~|^2). Counts only the fixes' noise (a little conservative: only the noise
        /// along the direction of travel matters), not map error, tracking drift or the correlation of consecutive fixes.
        /// Infinity before anything is known.
        /// </summary>
        public double ScaleSigma
        {
            get
            {
                Totals(out double num, out _, out _, out _);
                if (!(num > 0)) return double.PositiveInfinity;
                double sc = Scale;
                return sc * sc / Math.Sqrt(num);
            }
        }

        /// <summary>The scale to use: follows <see cref="Scale"/> at a limited rate, clamped, only once observable. Moves in <see cref="Advance"/>.</summary>
        public double AppliedScale => applied;

        /// <summary>Forgets everything and returns to the prior.</summary>
        public void Reset()
        {
            closedNum = closedDen = closedW = 0; closedN = 0;
            ClearOpen();
            applied = prior; lastTime = double.NaN; warned = false;
        }

        /// <summary>
        /// Starts a new segment: call whenever tracking or the alignment is re-seeded (tracking jump, Lose,
        /// consensus re-seed, Relocalize). The scale stays; yaw and translation are fitted per segment.
        /// </summary>
        public void BeginSegment()
        {
            if (n == 0) return;
            OpenTerms(out double num, out double den, out _);
            closedNum += num; closedDen += den; closedW += w; closedN += n;
            ClearOpen();
        }

        /// <summary>
        /// Adds one accepted fix: the camera position in the map and in tracking space (same instant),
        /// the alignment's current yaw in degrees, and the position error of the fix (m, RMS over the three axes,
        /// as <see cref="PnpResult.positionSigma"/>), which sets the weight 1/sigma^2. Returns false if the fix was left out: the estimator is disabled, not finite, or, once observable, a
        /// residual under the current estimate beyond <see cref="SessionScaleSettings.outlierSigmas"/> sigma.
        /// </summary>
        public bool Add(Vector3d cameraInMap, Vector3d cameraInTracking, double yawDeg, double sigmaPos)
        {
            if (!s.enabled || !Finite(cameraInMap) || !Finite(cameraInTracking) || double.IsNaN(yawDeg) || double.IsInfinity(yawDeg)) return false;
            if (!(sigmaPos > 0) || double.IsInfinity(sigmaPos)) sigmaPos = 0.05;
            double sigma = Math.Max(MinSigma, sigmaPos);

            if (n == 0)
            {
                c0[0] = cameraInMap.x; c0[1] = cameraInMap.y; c0[2] = cameraInMap.z;
                t0[0] = cameraInTracking.x; t0[1] = cameraInTracking.y; t0[2] = cameraInTracking.z;
            }
            pc[0] = cameraInMap.x - c0[0]; pc[1] = cameraInMap.y - c0[1]; pc[2] = cameraInMap.z - c0[2];
            pt[0] = cameraInTracking.x - t0[0]; pt[1] = cameraInTracking.y - t0[1]; pt[2] = cameraInTracking.z - t0[2];

            if (n >= 2 && s.outlierSigmas > 0 && IsObservable && IsOutlier(sigma)) return false;

            double wi = 1.0 / (sigma * sigma);
            n++; w += wi;
            for (int i = 0; i < 3; i++)
            {
                sumC[i] += wi * pc[i];
                sumT[i] += wi * pt[i];
                for (int j = 0; j < 3; j++) sumCT[i * 3 + j] += wi * pc[i] * pt[j];
            }
            sumTT += wi * (pt[0] * pt[0] + pt[1] * pt[1] + pt[2] * pt[2]);
            sumTTxz += wi * (pt[0] * pt[0] + pt[2] * pt[2]);
            double yaw = yawDeg * Math.PI / 180;
            sumYawCos += wi * Math.Cos(yaw); sumYawSin += wi * Math.Sin(yaw);
            return true;
        }

        /// <summary>
        /// Moves <see cref="AppliedScale"/> towards the estimate by at most maxRatePerSecond times the time since
        /// the last call (capped at one second). <paramref name="now"/> is any monotonic clock in seconds.
        /// Returns true if the applied scale changed.
        /// </summary>
        public bool Advance(double now)
        {
            double target = prior;   // also what a disabled estimator heads for
            if (s.enabled && IsObservable)
            {
                double raw = Scale;
                target = Math.Min(max, Math.Max(min, raw));
                if (!warned && (raw < min || raw > max))
                {
                    warned = true;
                    Debug.LogWarning($"[StageSettAR] The map scale estimate ({raw:F3}) is outside {min:F2}..{max:F2}; using {target:F2}. " +
                                     "A scale error that large suggests a badly scaled map or splat, not a tweak.");
                }
            }

            double dt = double.IsNaN(lastTime) ? 0 : Math.Min(MaxAdvanceSeconds, Math.Max(0, now - lastTime));
            lastTime = now;

            double delta = target - applied;
            if (s.maxRatePerSecond > 0)
            {
                double step = s.maxRatePerSecond * dt;
                delta = Math.Max(-step, Math.Min(step, delta));
            }
            if (delta == 0) return false;
            applied += delta;
            return true;
        }

        // ---- sums ----

        void ClearOpen()
        {
            n = 0; w = 0;
            Array.Clear(sumC, 0, 3); Array.Clear(sumT, 0, 3); Array.Clear(sumCT, 0, 9);
            sumTT = sumTTxz = sumYawCos = sumYawSin = 0;
        }

        // Closed plus open segments: sum w|t~|^2, sum w (R c~).t~, sum w, fix count.
        void Totals(out double num, out double den, out double wt, out int count)
        {
            OpenTerms(out double on, out double od, out _);
            num = closedNum + on; den = closedDen + od; wt = closedW + w; count = closedN + n;
        }

        // The open segment's sum w|t~|^2 and sum w (R c~).t~, and the yaw (radians) used for R.
        void OpenTerms(out double num, out double den, out double yaw)
        {
            num = den = yaw = 0;
            if (n == 0) return;

            num = Math.Max(0, sumTT - (sumT[0] * sumT[0] + sumT[1] * sumT[1] + sumT[2] * sumT[2]) / w);

            // m[i, j] = sum w c~_i t~_j
            double m00 = Cross(0, 0), m02 = Cross(0, 2), m11 = Cross(1, 1), m20 = Cross(2, 0), m22 = Cross(2, 2);

            double spreadXz = Math.Sqrt(Math.Max(0, sumTTxz - (sumT[0] * sumT[0] + sumT[2] * sumT[2]) / w) / w);
            if (spreadXz >= ProcrustesMinSpread)
                yaw = Math.Atan2(m20 - m02, m00 + m22);   // maximises (c m00 + s m20 - s m02 + c m22): 2D Procrustes in XZ
            else
                yaw = Math.Atan2(sumYawSin, sumYawCos);

            double cs = Math.Cos(yaw), sn = Math.Sin(yaw);
            den = cs * (m00 + m22) + sn * (m20 - m02) + m11;   // trace(Ry · M) for Ry = [[c,0,s],[0,1,0],[-s,0,c]]
        }

        double Cross(int i, int j) => sumCT[i * 3 + j] - sumC[i] * sumT[j] / w;

        // Residual of the fix in pc/pt under the current estimate, in tracking metres, against its own segment.
        bool IsOutlier(double sigma)
        {
            OpenTerms(out _, out _, out double yaw);
            double sc = Scale, cs = Math.Cos(yaw), sn = Math.Sin(yaw);
            double dcx = pc[0] - sumC[0] / w, dcy = pc[1] - sumC[1] / w, dcz = pc[2] - sumC[2] / w;
            double ex = pt[0] - sumT[0] / w - sc * (cs * dcx + sn * dcz);
            double ey = pt[1] - sumT[1] / w - sc * dcy;
            double ez = pt[2] - sumT[2] / w - sc * (-sn * dcx + cs * dcz);
            double limit = s.outlierSigmas * sc * Math.Sqrt(sigma * sigma + 1.0 / w);   // 1/w: variance of the segment centre
            return ex * ex + ey * ey + ez * ez > limit * limit;
        }

        static bool Finite(in Vector3d v) =>
            !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z) || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
    }
}
