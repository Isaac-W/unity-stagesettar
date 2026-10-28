using System;
using UnityEngine;

namespace StageSettAR
{
    [Serializable]
    public struct AlignmentSettings
    {
        [Tooltip("Frames kept in the fitting window.")]
        public int maxFrames;
        [Tooltip("Frames older than this (seconds, on the clock passed to AddFrame) are dropped.")]
        public float maxFrameAgeSeconds;
        [Tooltip("Huber scale of the fit, in network pixels.")]
        public double huberPx;
        [Tooltip("Correspondences farther than this from the fit are ignored, in network pixels.")]
        public double inlierPx;

        public static AlignmentSettings Default => new AlignmentSettings
        {
            maxFrames = 30, maxFrameAgeSeconds = 60f, huberPx = 2, inlierPx = 8,
        };
    }

    /// <summary>
    /// Map-to-tracking transform p_trk = scale · Ry(yaw) · p_map + translation. Only yaw is free
    /// because the tracking space and the map both have gravity along -y. The scale is not fitted
    /// per alignment: <see cref="SessionScaleEstimator"/> owns it.
    /// </summary>
    public struct Alignment
    {
        public bool valid;
        public float yawDeg;
        public Vector3 translation;
        public float scale;
        /// <summary>Standard deviation (m) of where the observed landmarks' centroid lands in tracking space.</summary>
        public float positionSigma;
        public float yawSigmaDeg;
        public int frames;
        public int correspondences;
        public double rmsPx;

        public static Alignment Identity => new Alignment { valid = true, scale = 1f };

        public Matrix4x4 MapToTracking => Matrix4x4.TRS(translation, Quaternion.Euler(0f, yawDeg, 0f), Vector3.one * scale);
    }

    /// <summary>
    /// Fits the map-to-tracking transform from the PnP inlier correspondences of several frames.
    /// </summary>
    /// <remarks>
    /// The headset's own tracking is smooth but drifts; a STAR fix is absolute but noisy. Fitting
    /// one (yaw, translation) at a given scale (<see cref="SetScale"/>) to the reprojection error of
    /// all recent frames, each seen from where tracking says the camera was, averages the noise down
    /// without trusting tracking over long times. Working in image space weights each landmark by what
    /// the network actually measured. Not thread-safe.
    /// </remarks>
    public sealed class AlignmentSolver
    {
        sealed class Frame
        {
            public double time;
            public Matrix3x3d toNet;       // tracking-space offset -> network-camera vector
            public Vector3d cam;           // camera centre in tracking space
            public double fx, fy, cx, cy;
            public int count;
            public Vector3d[] map = Array.Empty<Vector3d>();
            public double[] u = Array.Empty<double>(), v = Array.Empty<double>();
        }

        const int NP = 4;   // free parameters: yaw, tx, ty, tz

        readonly AlignmentSettings s;
        readonly Vector3[] landmarks;
        readonly Frame[] ring;
        int head, count;

        readonly double[] H = new double[NP * NP], Hd = new double[NP * NP], g = new double[NP], rhs = new double[NP], step = new double[NP];
        readonly double[] cov = new double[NP * NP], ja = new double[NP], jb = new double[NP];
        readonly double[] cur = new double[NP], cand = new double[NP];
        double scale = 1;
        int usedCount; double usedSsq; Vector3d usedSum;

        public AlignmentSolver(AlignmentSettings settings, StarMap map) : this(settings, map.LandmarksUnity) { }

        /// <param name="landmarks">Landmark positions in the Unity map frame (<see cref="StarMap.LandmarksUnity"/>).</param>
        public AlignmentSolver(AlignmentSettings settings, Vector3[] landmarks)
        {
            s = settings;
            this.landmarks = landmarks ?? throw new ArgumentNullException(nameof(landmarks));
            ring = new Frame[Math.Max(1, s.maxFrames)];
        }

        public int FrameCount => count;

        public void Clear() { head = 0; count = 0; }

        /// <summary>The map scale (tracking metres per map unit) that <see cref="Solve"/> holds fixed. Default 1.</summary>
        public void SetScale(double value)
        {
            if (!(value > 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "The scale must be positive and finite.");
            scale = value;
        }

        /// <summary>
        /// Stores one accepted localization: the correspondences flagged in <paramref name="inlierMask"/>
        /// (null = all) and where the camera sensor was in tracking space at capture. Copies the data,
        /// since detections are reused buffers, and drops frames beyond the window size or age.
        /// </summary>
        public void AddFrame(Detections d, bool[] inlierMask, Pose cameraInTracking, double time)
        {
            while (count > 0 && (count == ring.Length || ring[head].time < time - s.maxFrameAgeSeconds)) Drop();

            int slot = (head + count) % ring.Length;
            Frame f = ring[slot] ??= new Frame();
            if (f.map.Length < d.count)
            {
                f.map = new Vector3d[d.count];
                f.u = new double[d.count];
                f.v = new double[d.count];
            }
            int k = 0;
            for (int i = 0; i < d.count; i++)
            {
                int l = d.landmark[i];
                if ((inlierMask != null && !inlierMask[i]) || (uint)l >= (uint)landmarks.Length) continue;
                f.map[k] = new Vector3d(landmarks[l]);
                f.u[k] = d.pixel[i].x;
                f.v[k] = d.pixel[i].y;
                k++;
            }
            if (k == 0) return;

            // p_net = Q^T · diag(1,-1,1) · Rcam^T · (p_trk - cam): Unity camera axes -> OpenCV -> turned network camera.
            Matrix3x3d toCam = Matrix3x3d.FromQuaternion(cameraInTracking.rotation).Transpose();
            var flip = new Matrix3x3d(1, 0, 0, 0, -1, 0, 0, 0, 1);
            f.toNet = Conventions.QuarterTurnCameraRotation(d.quarterTurns).Transpose() * flip * toCam;
            f.cam = new Vector3d(cameraInTracking.position);
            f.fx = d.netIntrinsics.fx; f.fy = d.netIntrinsics.fy; f.cx = d.netIntrinsics.cx; f.cy = d.netIntrinsics.cy;
            f.time = time;
            f.count = k;
            count++;
        }

        void Drop() { head = (head + 1) % ring.Length; count--; }

        /// <summary>
        /// Robust fit over the stored frames, started from <paramref name="initial"/> (which must be
        /// valid, e.g. from <see cref="FromSingleFix"/>; a bad start can land in a wrong yaw).
        /// Yaw and translation are fitted with the scale held at <see cref="SetScale"/> (the scale in
        /// <paramref name="initial"/> is ignored). Huber LM, then once more on the correspondences
        /// within the inlier gate.
        /// </summary>
        public Alignment Solve(Alignment initial)
        {
            var result = new Alignment { frames = count };
            if (!initial.valid || count == 0) return result;

            cur[0] = initial.yawDeg * Math.PI / 180;
            cur[1] = initial.translation.x; cur[2] = initial.translation.y; cur[3] = initial.translation.z;

            Optimize(double.PositiveInfinity);
            Optimize(s.inlierPx);

            // Statistics over the inliers with plain least squares: cov = s2 (J^T J)^-1.
            Evaluate(cur, s.inlierPx, double.PositiveInfinity, true);
            if (usedCount < 3) return result;

            double s2 = Math.Max(1, usedSsq / Math.Max(1, 2 * usedCount - NP));
            double yawSigma = double.PositiveInfinity, posSigma = double.PositiveInfinity;
            if (Invert())
            {
                // Centroid of the observed landmarks in tracking space: its Jacobian wrt (yaw, t).
                double c = Math.Cos(cur[0]), sn = Math.Sin(cur[0]), sc = scale;
                Vector3d mc = (1.0 / usedCount) * usedSum;
                double trace = 0;
                for (int a = 0; a < NP; a++)
                    for (int b = 0; b < NP; b++)
                        trace += cov[a * NP + b] * Vector3d.Dot(Col(a, mc, c, sn, sc), Col(b, mc, c, sn, sc));
                yawSigma = Math.Sqrt(s2 * Math.Max(0, cov[0])) * 180 / Math.PI;
                posSigma = Math.Sqrt(s2 * Math.Max(0, trace));
            }

            double yaw = cur[0] * 180 / Math.PI;
            result.valid = true;
            result.yawDeg = (float)(yaw - 360 * Math.Floor((yaw + 180) / 360));
            result.translation = new Vector3((float)cur[1], (float)cur[2], (float)cur[3]);
            result.scale = (float)scale;
            result.positionSigma = (float)posSigma;
            result.yawSigmaDeg = (float)yawSigma;
            result.correspondences = usedCount;
            result.rmsPx = Math.Sqrt(usedSsq / (2.0 * usedCount));
            return result;
        }

        /// <summary>
        /// The alignment implied by one PnP pose, T = cameraInTracking · cameraInMap^-1 reduced to
        /// yaw only. The rotation is the closest rotation about +y; the translation is then
        /// recomputed so the camera centre maps exactly onto its tracked position (keeping the
        /// full fit's translation would move it by tilt × lever arm). The map is taken to be
        /// <paramref name="scale"/> tracking metres per map unit.
        /// </summary>
        public static Alignment FromSingleFix(Pose cameraInMap, Pose cameraInTracking, float scale = 1f)
        {
            Matrix3x3d r = Matrix3x3d.FromQuaternion(cameraInTracking.rotation) * Matrix3x3d.FromQuaternion(cameraInMap.rotation).Transpose();
            double yaw = Math.Atan2(r.m02 - r.m20, r.m00 + r.m22);
            Vector3d t = new Vector3d(cameraInTracking.position) - scale * (Matrix3x3d.RotationY(yaw) * new Vector3d(cameraInMap.position));
            return new Alignment { valid = true, yawDeg = (float)(yaw * 180 / Math.PI), translation = t.ToVector3(), scale = scale, frames = 1 };
        }

        // ---- the fit ----

        // Levenberg-Marquardt on cur (yaw rad, tx, ty, tz) at the fixed scale, residuals gated at `gate` px.
        void Optimize(double gate)
        {
            double cost = Evaluate(cur, gate, s.huberPx, true), lam = 1e-3;
            for (int outer = 0; outer < 40; outer++)
            {
                bool improved = false;
                for (int tries = 0; tries < 12; tries++)
                {
                    Array.Copy(H, Hd, NP * NP);
                    for (int i = 0; i < NP; i++) Hd[i * NP + i] += lam * Math.Max(H[i * NP + i], 1e-9);
                    for (int i = 0; i < NP; i++) rhs[i] = -g[i];
                    if (Solve(NP, Hd, rhs, step))
                    {
                        for (int i = 0; i < NP; i++) cand[i] = cur[i] + step[i];
                        double c2 = Evaluate(cand, gate, s.huberPx, false);
                        if (c2 < cost)
                        {
                            double rel = (cost - c2) / Math.Max(cost, 1e-300);
                            Array.Copy(cand, cur, NP);
                            cost = Evaluate(cur, gate, s.huberPx, true);   // refreshes H and g
                            lam = Math.Max(lam / 3, 1e-12);
                            improved = rel > 1e-10;
                            break;
                        }
                    }
                    lam *= 5;
                }
                if (!improved) break;
            }
        }

        // Cost (truncated Huber) at p; with jac, also fills H, g and the used-count statistics.
        double Evaluate(double[] p, double gate, double huber, bool jac)
        {
            double c = Math.Cos(p[0]), sn = Math.Sin(p[0]), sc = scale;
            double cap = Math.Min(gate, 1e4), capCost = 2 * Rho(cap, huber);
            double cost = 0;
            usedCount = 0; usedSsq = 0; usedSum = default;
            if (jac) { Array.Clear(H, 0, NP * NP); Array.Clear(g, 0, NP); }

            for (int fi = 0; fi < count; fi++)
            {
                Frame f = ring[(head + fi) % ring.Length];
                for (int k = 0; k < f.count; k++)
                {
                    Vector3d m = f.map[k];
                    var rm = new Vector3d(c * m.x + sn * m.z, m.y, -sn * m.x + c * m.z);
                    Vector3d pn = f.toNet * (sc * rm + new Vector3d(p[1], p[2], p[3]) - f.cam);
                    if (!(pn.z > 1e-6)) { cost += capCost; continue; }
                    double rx = f.fx * pn.x / pn.z + f.cx - f.u[k], ry = f.fy * pn.y / pn.z + f.cy - f.v[k];
                    double e2 = rx * rx + ry * ry;
                    if (e2 > gate * gate) { cost += capCost; continue; }
                    cost += Rho(rx, huber) + Rho(ry, huber);
                    if (!jac) continue;

                    usedCount++; usedSsq += e2; usedSum = usedSum + m;
                    // d p_trk / d(yaw, t), then through toNet and the projection.
                    for (int a = 0; a < NP; a++)
                    {
                        Vector3d dn = f.toNet * Col(a, m, c, sn, sc);
                        ja[a] = f.fx * (dn.x - pn.x / pn.z * dn.z) / pn.z;
                        jb[a] = f.fy * (dn.y - pn.y / pn.z * dn.z) / pn.z;
                    }
                    AddRow(ja, rx, huber);
                    AddRow(jb, ry, huber);
                }
            }
            return double.IsNaN(cost) ? double.PositiveInfinity : cost;
        }

        void AddRow(double[] j, double r, double huber)
        {
            double a = Math.Abs(r), om = a <= huber ? 1 : huber / a;
            for (int i = 0; i < NP; i++)
            {
                g[i] += om * j[i] * r;
                for (int k = 0; k < NP; k++) H[i * NP + k] += om * j[i] * j[k];
            }
        }

        static double Rho(double r, double f)
        {
            double a = Math.Abs(r);
            return a <= f ? a * a : 2 * f * a - f * f;
        }

        // Column a of the d(p_trk)/d(yaw, t) Jacobian for a map point m.
        static Vector3d Col(int a, Vector3d m, double c, double sn, double sc)
        {
            switch (a)
            {
                case 0: return new Vector3d(sc * (-sn * m.x + c * m.z), 0, sc * (-c * m.x - sn * m.z));
                case 1: return new Vector3d(1, 0, 0);
                case 2: return new Vector3d(0, 1, 0);
                default: return new Vector3d(0, 0, 1);
            }
        }

        // cov = H^-1 (NP x NP).
        bool Invert()
        {
            for (int a = 0; a < NP; a++)
            {
                Array.Copy(H, Hd, NP * NP);
                for (int i = 0; i < NP; i++) rhs[i] = i == a ? 1 : 0;
                if (!Solve(NP, Hd, rhs, step)) return false;
                for (int i = 0; i < NP; i++) cov[i * NP + a] = step[i];
            }
            return true;
        }

        // Gaussian elimination with partial pivoting on a row-major n x n system; A and b are consumed.
        static bool Solve(int n, double[] A, double[] b, double[] x)
        {
            for (int c = 0; c < n; c++)
            {
                int piv = c; double big = Math.Abs(A[c * n + c]);
                for (int r = c + 1; r < n; r++)
                    if (Math.Abs(A[r * n + c]) > big) { big = Math.Abs(A[r * n + c]); piv = r; }
                if (!(big > 1e-300)) return false;
                if (piv != c)
                {
                    for (int k = 0; k < n; k++) { double t = A[c * n + k]; A[c * n + k] = A[piv * n + k]; A[piv * n + k] = t; }
                    double tb = b[c]; b[c] = b[piv]; b[piv] = tb;
                }
                for (int r = c + 1; r < n; r++)
                {
                    double f = A[r * n + c] / A[c * n + c];
                    for (int k = c; k < n; k++) A[r * n + k] -= f * A[c * n + k];
                    b[r] -= f * b[c];
                }
            }
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = b[r];
                for (int k = r + 1; k < n; k++) sum -= A[r * n + k] * x[k];
                x[r] = sum / A[r * n + r];
                if (double.IsNaN(x[r]) || double.IsInfinity(x[r])) return false;
            }
            return true;
        }
    }
}
