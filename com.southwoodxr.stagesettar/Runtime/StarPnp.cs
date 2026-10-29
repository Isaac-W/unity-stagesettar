using System;

namespace StageSettAR
{
    /// <summary>Thresholds of the PnP pipeline. The defaults are star-mobile's final settings.</summary>
    public struct PnpSettings
    {
        /// <summary>MSAC inlier gate in pixels.</summary>
        public double ransacThresholdPx;
        /// <summary>Probability of having drawn one all-inlier sample before RANSAC stops early.</summary>
        public double confidence;
        public int maxIterations;
        /// <summary>Huber scale of the least-squares refits, in pixels.</summary>
        public double huberPx;
        /// <summary>Gate of the single re-selection round after the first refit, in pixels.</summary>
        public double reselectPx;
        /// <summary>Fewest final inliers for <see cref="PnpResult.success"/>.</summary>
        public int minInliers;
        /// <summary>0 draws a different RANSAC sequence on every Solve; any other value restarts the same sequence on each Solve, so results are reproducible.</summary>
        public int seed;

        public static PnpSettings Default => new PnpSettings
        {
            ransacThresholdPx = 8, confidence = 0.99, maxIterations = 5000,
            huberPx = 2, reselectPx = 8, minInliers = 6, seed = 0,
        };
    }

    public struct PnpResult
    {
        public bool success;
        /// <summary>OpenCV camera-to-world rotation in the export frame.</summary>
        public Matrix3x3d R;
        /// <summary>Camera centre in the export frame.</summary>
        public Vector3d t;
        public int inliers;
        public int correspondences;
        /// <summary>0..1, star-mobile's confidence.ts: chance of coincidence times precision.</summary>
        public double confidence;
        /// <summary>RMS camera-centre standard deviation in metres, from (J^T J)^-1.</summary>
        public double positionSigma;
        /// <summary>RMS rotation standard deviation in degrees.</summary>
        public double rotationSigmaDeg;
        public double rmsPx;
    }

    /// <summary>
    /// Robust PnP for STAR detections, a port of star-mobile's pnp.ts and confidence.ts:
    /// P3P (Grunert) inside MSAC RANSAC, then a Huber refit weighted by score^2, one re-selection
    /// round and an unweighted Huber refit. The Huber knee is in pixels and the weight multiplies
    /// the loss, so it does not move the knee.
    /// </summary>
    /// <remarks>
    /// Differences from star-mobile: hypotheses are drawn PROSAC-style (best-scored detections
    /// first, as in its Rust solver), and an optional prior pose is scored as the first
    /// hypothesis so a good tracking-predicted pose wins without needing a lucky sample.
    /// All working memory is allocated in the constructor; <see cref="Solve"/> never allocates
    /// and uses no Unity API, so it may run on a background thread. One instance is not
    /// re-entrant.
    /// </remarks>
    public sealed class StarPnp
    {
        const int MinPoints = 4;       // P3P needs 3, plus one to disambiguate
        const int ProsacIterations = 1500;
        const double UniformFromInlierFraction = 0.3;   // PROSAC sampling ends at the first hypothesis this good
        const int MaxHypotheses = 5000 * 4;

        struct Model { public Matrix3x3d R; public Vector3d t; }   // world-to-camera: p = R X + t

        readonly PnpSettings s;
        readonly int capacity;
        readonly Vector3d[] bearing;
        readonly double[] err, sortKey;
        readonly int[] idx, order;
        readonly bool[] mask;
        readonly double[] H = new double[36], Hd = new double[36], g = new double[6], step = new double[6], rhs = new double[6];
        readonly double[] j0 = new double[6], j1 = new double[6];
        readonly Matrix3x3d[] solR = new Matrix3x3d[4];
        readonly Vector3d[] solT = new Vector3d[4];
        readonly double[] roots = new double[4], crit = new double[3], pts = new double[5];
        ulong rng, seedCounter = 1;

        // Inputs of the current Solve.
        Vector3d[] X; Vector2d[] uv; double[] score;
        double fx, fy, cx, cy;
        int n;

        // RANSAC state.
        Model best; double bestScore; int iterCap, growth, done, uniformFrom, stopAfter;
        bool haveBest;

        public StarPnp(int maxCorrespondences, PnpSettings settings)
        {
            if (maxCorrespondences < MinPoints) throw new ArgumentOutOfRangeException(nameof(maxCorrespondences));
            s = settings;
            capacity = maxCorrespondences;
            bearing = new Vector3d[capacity];
            err = new double[capacity];
            sortKey = new double[capacity];
            idx = new int[capacity];
            order = new int[capacity];
            mask = new bool[capacity];
        }

        /// <summary>Final inlier flags of the last <see cref="Solve"/>, indexed like its input (length &gt;= n).</summary>
        public bool[] InlierMask => mask;

        /// <summary>
        /// Solves for the camera pose. Points are in the export frame; pixels are network-input
        /// pixels with the synthetic intrinsics. <paramref name="scores"/> are the detection
        /// scores (RANSAC ordering and refit weights). <paramref name="priorR"/>/<paramref name="priorT"/>
        /// give an optional predicted camera-to-world pose, evaluated as the first hypothesis.
        /// </summary>
        public PnpResult Solve(int n, Vector3d[] pts3, Vector2d[] pts2, double[] scores,
                               double fx, double fy, double cx, double cy, int imageWidth, int imageHeight,
                               Matrix3x3d? priorR = null, Vector3d? priorT = null)
        {
            if (n > capacity) throw new ArgumentException("More correspondences than the solver was built for.", nameof(n));
            var result = new PnpResult { correspondences = n, R = Matrix3x3d.Identity };
            Array.Clear(mask, 0, mask.Length);
            if (n < MinPoints) return result;

            X = pts3; uv = pts2; score = scores;
            this.fx = fx; this.fy = fy; this.cx = cx; this.cy = cy; this.n = n;
            for (int i = 0; i < n; i++) bearing[i] = Bearing(i);

            if (!Ransac(priorR, priorT)) return result;

            // Refit 1: Huber over the RANSAC inliers, weighted by score^2.
            int cnt = CollectInliers(best, s.ransacThresholdPx);
            Model m = Refine(best, cnt, true, s.huberPx, 60);

            // One re-selection round against all correspondences, then an unweighted refit.
            cnt = CollectInliers(m, s.reselectPx);
            if (cnt >= MinPoints) m = Refine(m, cnt, false, s.huberPx, 60);

            // The reported inliers are those of the final pose (points behind the camera never pass).
            cnt = CollectInliers(m, s.reselectPx);
            double ssq = 0;
            for (int k = 0; k < cnt; k++)
            {
                int i = idx[k];
                mask[i] = true;
                ssq += err[i] * err[i];
            }

            Matrix3x3d rc = m.R.Transpose();
            result.R = rc;
            result.t = -(rc * m.t);
            result.inliers = cnt;
            result.rmsPx = cnt > 0 ? Math.Sqrt(ssq / cnt) : double.PositiveInfinity;
            Uncertainty(m, cnt, out double posM, out double rotDeg);
            result.positionSigma = posM;
            result.rotationSigmaDeg = rotDeg;
            double chance = Log10Chance(n, cnt, imageWidth, imageHeight, s.reselectPx);
            result.confidence = ConfidenceScore(chance, posM * 100, rotDeg);
            result.success = cnt >= s.minInliers;
            return result;
        }

        Vector3d Bearing(int i)
        {
            var b = new Vector3d((uv[i].x - cx) / fx, (uv[i].y - cy) / fy, 1);
            return (1.0 / b.Length) * b;
        }

        /// <summary>
        /// The camera-to-world poses P3P gives for the first three correspondences (up to four, into
        /// <paramref name="R"/>/<paramref name="t"/>). Returns the count. Exposed for tests.
        /// </summary>
        public int SolveP3p(Vector3d[] pts3, Vector2d[] pts2, double fx, double fy, double cx, double cy,
                            Matrix3x3d[] R, Vector3d[] t)
        {
            X = pts3; uv = pts2;
            this.fx = fx; this.fy = fy; this.cx = cx; this.cy = cy;
            for (int i = 0; i < 3; i++) bearing[i] = Bearing(i);
            int count = P3p(0, 1, 2);
            for (int k = 0; k < count; k++)
            {
                R[k] = solR[k].Transpose();
                t[k] = -(R[k] * solT[k]);
            }
            return count;
        }

        // ---- RANSAC ----

        bool Ransac(Matrix3x3d? priorR, Vector3d? priorT)
        {
            // With only a few points there are few distinct samples, so a few passes over all triples is enough.
            long triples = (long)n * (n - 1) * (n - 2) / 6;
            iterCap = (int)Math.Min(s.maxIterations, 3 * triples);
            growth = Math.Min(ProsacIterations, iterCap);
            // The adaptive bound assumes uniform sampling, so it only counts iterations after sampling turns uniform.
            uniformFrom = -1; done = 0; stopAfter = iterCap;
            haveBest = false; bestScore = double.PositiveInfinity;
            rng = 0x9E3779B97F4A7C15UL * (s.seed != 0 ? (ulong)(uint)s.seed : seedCounter++);

            for (int i = 0; i < n; i++) { order[i] = i; sortKey[i] = -score[i]; }
            Array.Sort(sortKey, order, 0, n);   // best score first

            if (priorR.HasValue && priorT.HasValue)
            {
                Matrix3x3d rw = priorR.Value.Transpose();
                Consider(new Model { R = rw, t = -(rw * priorT.Value) });
            }

            for (int it = 0; it < iterCap; it++)
            {
                if (uniformFrom >= 0 && it - uniformFrom >= stopAfter) break;
                done = it + 1;
                int a, b, c;
                // PROSAC growth: the sample pool starts at the 3 best detections and grows to all of them,
                // until a good hypothesis is found; from then on every point is drawn uniformly.
                int m = uniformFrom < 0 && it < growth ? Math.Min(n, 3 + (int)((long)(n - 3) * (it + 1) / growth)) : n;
                if (m < n)
                {
                    c = m - 1;
                    a = Next(m - 1);
                    b = Next(m - 2); if (b >= a) b++;
                }
                else
                {
                    a = Next(n);
                    b = Next(n - 1); if (b >= a) b++;
                    c = Next(n - 2);
                    int lo = Math.Min(a, b), hi = Math.Max(a, b);
                    if (c >= lo) c++;
                    if (c >= hi) c++;
                }
                int count = P3p(order[a], order[b], order[c]);
                for (int k = 0; k < count; k++) Consider(new Model { R = solR[k], t = solT[k] });
            }

            return haveBest && CollectInliers(best, s.ransacThresholdPx) >= MinPoints;
        }

        int Next(int k) => (int)((Rand() >> 11) * (1.0 / 9007199254740992.0) * k);

        ulong Rand()
        {
            rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
            return rng * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>Scores a hypothesis (MSAC), tries a local optimisation, keeps it if best, updates the stopping bound.</summary>
        void Consider(Model m)
        {
            if (!Score(m, bestScore, out double cost, out int count)) return;
            if (count >= 6)
            {
                int k = CollectInliers(m, s.ransacThresholdPx);
                Model m2 = Refine(m, k, false, double.PositiveInfinity, 10);
                if (Score(m2, cost, out double c2, out int n2)) { m = m2; cost = c2; count = n2; }
            }
            best = m; bestScore = cost; haveBest = true;
            double w = (double)count / n;
            if (uniformFrom < 0 && (w >= UniformFromInlierFraction || done >= growth)) uniformFrom = done;
            double all = w * w * w;
            double need = all < 1e-12 ? iterCap : Math.Log(1 - s.confidence) / Math.Log(1 - Math.Min(all, 1 - 1e-12));
            stopAfter = Math.Max(1, (int)Math.Min(iterCap, Math.Ceiling(need)));
        }

        /// <summary>
        /// MSAC cost of a hypothesis. Returns false as soon as the running cost reaches <paramref name="limit"/>,
        /// since such a hypothesis cannot beat the best one.
        /// </summary>
        bool Score(in Model m, double limit, out double cost, out int count)
        {
            double th = s.ransacThresholdPx, th2 = th * th;
            cost = 0; count = 0;
            for (int i = 0; i < n; i++)
            {
                double e2 = ErrorSq(m, i);
                if (e2 <= th2) { cost += e2; count++; }
                else cost += th2;
                if (cost >= limit) return false;
            }
            return true;
        }

        int CollectInliers(in Model m, double th)
        {
            int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                err[i] = Error(m, i);
                if (err[i] <= th) idx[cnt++] = i;
            }
            return cnt;
        }

        double Error(in Model m, int i) => Math.Sqrt(ErrorSq(m, i));

        /// <summary>Squared reprojection error in pixels, infinity when the point is behind the camera.</summary>
        double ErrorSq(in Model m, int i)
        {
            Vector3d p = X[i];
            double pz = m.R.m20 * p.x + m.R.m21 * p.y + m.R.m22 * p.z + m.t.z;
            if (!(pz > 0)) return double.PositiveInfinity;
            double du = fx * (m.R.m00 * p.x + m.R.m01 * p.y + m.R.m02 * p.z + m.t.x) / pz + cx - uv[i].x;
            double dv = fy * (m.R.m10 * p.x + m.R.m11 * p.y + m.R.m12 * p.z + m.t.y) / pz + cy - uv[i].y;
            return du * du + dv * dv;
        }

        // ---- P3P (Grunert / Haralick et al. 1994) ----

        /// <summary>Up to four world-to-camera poses for three correspondences, in solR/solT. Returns the count.</summary>
        int P3p(int i0, int i1, int i2)
        {
            Vector3d P0 = X[i0], P1 = X[i1], P2 = X[i2];
            Vector3d f0 = bearing[i0], f1 = bearing[i1], f2 = bearing[i2];
            double a = (P1 - P2).Length, b = (P0 - P2).Length, c = (P0 - P1).Length;
            if (a < 1e-9 || b < 1e-9 || c < 1e-9) return 0;
            double ca = Vector3d.Dot(f1, f2), cb = Vector3d.Dot(f0, f2), cg = Vector3d.Dot(f0, f1);
            double a2 = a * a, b2 = b * b, c2 = c * c;
            double p = (a2 - c2) / b2, q = (a2 + c2) / b2;
            double A4 = (p - 1) * (p - 1) - 4 * c2 / b2 * ca * ca;
            double A3 = 4 * (p * (1 - p) * cb - (1 - q) * ca * cg + 2 * (c2 / b2) * ca * ca * cb);
            double A2 = 2 * (p * p - 1 + 2 * p * p * cb * cb + 2 * ((b2 - c2) / b2) * ca * ca - 4 * q * ca * cb * cg + 2 * ((b2 - a2) / b2) * cg * cg);
            double A1 = 4 * (-p * (1 + p) * cb + 2 * (a2 / b2) * cg * cg * cb - (1 - q) * ca * cg);
            double A0 = (1 + p) * (1 + p) - 4 * a2 / b2 * cg * cg;
            if (!Frame3(P0, P1, P2, out Matrix3x3d MP)) return 0;

            int nr = RealRoots(A4, A3, A2, A1, A0, roots), count = 0;
            for (int r = 0; r < nr; r++)
            {
                double v = roots[r];
                if (!(v > 0)) continue;
                double den = 2 * (cg - v * ca);
                if (Math.Abs(den) < 1e-12) continue;
                double u = ((-1 + p) * v * v - 2 * p * cb * v + 1 + p) / den;
                if (!(u > 0)) continue;
                double s1sq = b2 / (1 + v * v - 2 * v * cb);
                if (!(s1sq > 0)) continue;
                double s1 = Math.Sqrt(s1sq);
                Vector3d Q0 = s1 * f0, Q1 = (u * s1) * f1, Q2 = (v * s1) * f2;
                if (!Frame3(Q0, Q1, Q2, out Matrix3x3d MQ)) continue;
                Matrix3x3d R = MQ * MP.Transpose();
                solR[count] = R;
                solT[count] = Q0 - R * P0;
                count++;
            }
            return count;
        }

        static bool Frame3(in Vector3d a, in Vector3d b, in Vector3d c, out Matrix3x3d M)
        {
            Vector3d e1 = b - a, nrm = Vector3d.Cross(b - a, c - a);
            double l1 = e1.Length, ln = nrm.Length;
            M = default;
            if (ln < 1e-12 || l1 < 1e-12) return false;
            e1 = (1 / l1) * e1;
            Vector3d e3 = (1 / ln) * nrm;
            M = Matrix3x3d.FromColumns(e1, Vector3d.Cross(e3, e1), e3);
            return true;
        }

        // ---- polynomial roots: closed-form cubic for the critical points, bracketed bisection for the quartic ----

        /// <summary>Real roots of c4 x^4 + ... + c0 into <paramref name="r"/> (length &gt;= 4); returns the count.</summary>
        int RealRoots(double c4, double c3, double c2, double c1, double c0, double[] r)
        {
            double mx = Math.Max(Math.Max(Math.Abs(c4), Math.Abs(c3)), Math.Max(Math.Max(Math.Abs(c2), Math.Abs(c1)), Math.Abs(c0)));
            if (!(mx > 0) || double.IsInfinity(mx)) return 0;
            if (Math.Abs(c4) < 1e-14 * mx) return CubicRoots(c3, c2, c1, c0, r);

            int nc = CubicRoots(4 * c4, 3 * c3, 2 * c2, c1, crit);
            Array.Sort(crit, 0, nc);
            double bound = 1 + Math.Max(Math.Max(Math.Abs(c3 / c4), Math.Abs(c2 / c4)), Math.Max(Math.Abs(c1 / c4), Math.Abs(c0 / c4)));
            int np = 0;
            pts[np++] = -bound;
            for (int i = 0; i < nc; i++) if (crit[i] > -bound && crit[i] < bound) pts[np++] = crit[i];
            pts[np++] = bound;

            int count = 0;
            for (int k = 0; k + 1 < np; k++)
            {
                double lo = pts[k], hi = pts[k + 1];
                double flo = Horner(c4, c3, c2, c1, c0, lo), fhi = Horner(c4, c3, c2, c1, c0, hi);
                if (flo == 0) { r[count++] = lo; continue; }
                if (flo * fhi > 0)
                {
                    // A tangent (double) root is a critical point where f touches zero without changing sign.
                    if (k > 0 && Math.Abs(flo) <= 1e-9 * mx) r[count++] = lo;
                    continue;
                }
                for (int it = 0; it < 100 && hi - lo > 1e-15 * Math.Max(1, Math.Abs(lo)); it++)
                {
                    double mid = 0.5 * (lo + hi), fm = Horner(c4, c3, c2, c1, c0, mid);
                    if (fm == 0) { lo = hi = mid; break; }
                    if (flo * fm < 0) hi = mid; else { lo = mid; flo = fm; }
                }
                r[count++] = 0.5 * (lo + hi);
            }
            return count;
        }

        static double Horner(double c4, double c3, double c2, double c1, double c0, double x) =>
            (((c4 * x + c3) * x + c2) * x + c1) * x + c0;

        /// <summary>Real roots of a x^3 + b x^2 + c x + d (trigonometric / Cardano), Newton-polished.</summary>
        static int CubicRoots(double a, double b, double c, double d, double[] r)
        {
            double mx = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), Math.Max(Math.Abs(c), Math.Abs(d)));
            if (!(mx > 0)) return 0;
            if (Math.Abs(a) < 1e-14 * mx)
            {
                if (Math.Abs(b) < 1e-14 * mx)
                {
                    if (Math.Abs(c) < 1e-14 * mx) return 0;
                    r[0] = -d / c; return 1;
                }
                double disc2 = c * c - 4 * b * d;
                if (disc2 < 0) return 0;
                double sq = Math.Sqrt(disc2), qq = -0.5 * (c + (c >= 0 ? sq : -sq));
                r[0] = qq / b;
                if (qq == 0) return 1;
                r[1] = d / qq;
                return 2;
            }
            double B = b / a, C = c / a, D = d / a;
            double shift = B / 3;
            double p = C - B * B / 3, q = 2 * B * B * B / 27 - B * C / 3 + D;
            double disc = q * q / 4 + p * p * p / 27;
            int n;
            if (disc > 0)
            {
                double sq = Math.Sqrt(disc);
                r[0] = Cbrt(-q / 2 + sq) + Cbrt(-q / 2 - sq) - shift;
                n = 1;
            }
            else
            {
                double rad = 2 * Math.Sqrt(-p / 3);
                double arg = p == 0 ? 0 : Math.Max(-1, Math.Min(1, 3 * q / (p * rad)));
                double phi = Math.Acos(arg) / 3;
                for (int k = 0; k < 3; k++) r[k] = rad * Math.Cos(phi - 2 * Math.PI * k / 3) - shift;
                n = 3;
            }
            for (int k = 0; k < n; k++)
                for (int it = 0; it < 2; it++)
                {
                    double x = r[k], f = ((x + B) * x + C) * x + D, df = (3 * x + 2 * B) * x + C;
                    if (df != 0 && !double.IsNaN(f / df)) r[k] = x - f / df;
                }
            return n;
        }

        static double Cbrt(double x) => x < 0 ? -Math.Pow(-x, 1.0 / 3) : Math.Pow(x, 1.0 / 3);

        // ---- Levenberg-Marquardt with Huber weights ----

        /// <summary>
        /// Refines a world-to-camera pose over idx[0..cnt) with left-multiplied rotation updates.
        /// The loss is w * Huber(proj - uv) on each pixel coordinate, with w = score^2 when weighted and 1 otherwise.
        /// </summary>
        Model Refine(Model m, int cnt, bool weighted, double huber, int maxEval)
        {
            double cost = Cost(m, cnt, weighted, huber), lam = 1e-3;
            if (double.IsInfinity(cost)) return m;
            int evals = 1;
            while (evals < maxEval)
            {
                Accumulate(m, cnt, weighted, huber);
                bool improved = false;
                while (evals < maxEval)
                {
                    Array.Copy(H, Hd, 36);
                    for (int i = 0; i < 6; i++) Hd[i * 7] += lam * Math.Max(H[i * 7], 1e-12);
                    for (int i = 0; i < 6; i++) rhs[i] = -g[i];
                    if (!SolveSystem(Hd, rhs, step)) break;
                    Matrix3x3d dR = Matrix3x3d.Rodrigues(step[0], step[1], step[2]);
                    var cand = new Model { R = dR * m.R, t = new Vector3d(m.t.x + step[3], m.t.y + step[4], m.t.z + step[5]) };
                    double c2 = Cost(cand, cnt, weighted, huber);
                    evals++;
                    if (c2 < cost)
                    {
                        double rel = (cost - c2) / Math.Max(cost, 1e-300);
                        m = cand; cost = c2;
                        lam = Math.Max(lam / 3, 1e-12);
                        improved = true;
                        if (rel < 1e-12) return m;
                        break;
                    }
                    lam *= 5;
                    if (lam > 1e12) break;
                }
                if (!improved) break;
            }
            return m;
        }

        static double Rho(double r, double f)
        {
            double a = Math.Abs(r);
            return a <= f ? a * a : 2 * f * a - f * f;
        }

        double Cost(in Model m, int cnt, bool weighted, double f)
        {
            double c = 0;
            for (int k = 0; k < cnt; k++)
            {
                int i = idx[k];
                Vector3d p = X[i];
                double px = m.R.m00 * p.x + m.R.m01 * p.y + m.R.m02 * p.z + m.t.x;
                double py = m.R.m10 * p.x + m.R.m11 * p.y + m.R.m12 * p.z + m.t.y;
                double pz = m.R.m20 * p.x + m.R.m21 * p.y + m.R.m22 * p.z + m.t.z;
                if (!(pz > 0)) return double.PositiveInfinity;
                double w = weighted ? score[i] * score[i] : 1;
                c += w * (Rho(fx * px / pz + cx - uv[i].x, f) + Rho(fy * py / pz + cy - uv[i].y, f));
            }
            return double.IsNaN(c) ? double.PositiveInfinity : c;
        }

        /// <summary>Gauss-Newton normal equations H = sum om J^T J, g = sum om J^T r (IRLS Huber weights om).</summary>
        void Accumulate(in Model m, int cnt, bool weighted, double f)
        {
            Array.Clear(H, 0, 36); Array.Clear(g, 0, 6);
            for (int k = 0; k < cnt; k++)
            {
                int i = idx[k];
                Vector3d p = X[i];
                double qx = m.R.m00 * p.x + m.R.m01 * p.y + m.R.m02 * p.z;
                double qy = m.R.m10 * p.x + m.R.m11 * p.y + m.R.m12 * p.z;
                double qz = m.R.m20 * p.x + m.R.m21 * p.y + m.R.m22 * p.z;
                double px = qx + m.t.x, py = qy + m.t.y, pz = qz + m.t.z;
                if (!(pz > 0)) continue;
                double w = weighted ? score[i] * score[i] : 1;
                double r0 = fx * px / pz + cx - uv[i].x, r1 = fy * py / pz + cy - uv[i].y;
                // d p / d omega = -[q]x, d p / d tau = I
                double a0 = fx / pz, a2 = -fx * px / (pz * pz);
                double b1 = fy / pz, b2 = -fy * py / (pz * pz);
                j0[0] = a2 * qy; j0[1] = a0 * qz - a2 * qx; j0[2] = -a0 * qy; j0[3] = a0; j0[4] = 0; j0[5] = a2;
                j1[0] = -b1 * qz + b2 * qy; j1[1] = -b2 * qx; j1[2] = b1 * qx; j1[3] = 0; j1[4] = b1; j1[5] = b2;
                AddRow(j0, r0, w, f);
                AddRow(j1, r1, w, f);
            }
            for (int i = 0; i < 6; i++) for (int j = 0; j < i; j++) H[i * 6 + j] = H[j * 6 + i];
        }

        void AddRow(double[] j, double r, double w, double f)
        {
            double a = Math.Abs(r), om = w * (a <= f ? 1 : f / a);
            for (int i = 0; i < 6; i++)
            {
                g[i] += om * j[i] * r;
                for (int k = i; k < 6; k++) H[i * 6 + k] += om * j[i] * j[k];
            }
        }

        // 6x6 Gaussian elimination with partial pivoting. A and b are consumed.
        static bool SolveSystem(double[] A, double[] b, double[] x)
        {
            for (int c = 0; c < 6; c++)
            {
                int piv = c; double big = Math.Abs(A[c * 6 + c]);
                for (int r = c + 1; r < 6; r++)
                    if (Math.Abs(A[r * 6 + c]) > big) { big = Math.Abs(A[r * 6 + c]); piv = r; }
                if (!(big > 1e-300)) return false;
                if (piv != c)
                {
                    for (int k = 0; k < 6; k++) { double t = A[c * 6 + k]; A[c * 6 + k] = A[piv * 6 + k]; A[piv * 6 + k] = t; }
                    double tb = b[c]; b[c] = b[piv]; b[piv] = tb;
                }
                for (int r = c + 1; r < 6; r++)
                {
                    double f = A[r * 6 + c] / A[c * 6 + c];
                    if (f == 0) continue;
                    for (int k = c; k < 6; k++) A[r * 6 + k] -= f * A[c * 6 + k];
                    b[r] -= f * b[c];
                }
            }
            for (int r = 5; r >= 0; r--)
            {
                double sum = b[r];
                for (int k = r + 1; k < 6; k++) sum -= A[r * 6 + k] * x[k];
                x[r] = sum / A[r * 6 + r];
            }
            for (int i = 0; i < 6; i++) if (double.IsNaN(x[i]) || double.IsInfinity(x[i])) return false;
            return true;
        }

        // ---- uncertainty and confidence ----

        /// <summary>
        /// Standard deviation of the camera centre (metres) and rotation (degrees) from s2 (J^T J)^-1
        /// over the final inliers, with a world-frame rotation perturbation and centre shift as in
        /// star-mobile (analytic Jacobian instead of central differences). s2 is at least 1 px^2
        /// because detections are cell-based.
        /// </summary>
        void Uncertainty(in Model m, int cnt, out double posM, out double rotDeg)
        {
            posM = rotDeg = double.PositiveInfinity;
            if (cnt < MinPoints) return;
            Array.Clear(H, 0, 36);
            Vector3d C = -(m.R.Transpose() * m.t);
            double ssq = 0;
            for (int k = 0; k < cnt; k++)
            {
                int i = idx[k];
                Vector3d v = X[i] - C;
                Vector3d pc = m.R * v;
                if (!(pc.z > 0)) return;
                double iz = 1 / pc.z;
                ssq += Sq(fx * pc.x * iz + cx - uv[i].x) + Sq(fy * pc.y * iz + cy - uv[i].y);
                for (int row = 0; row < 2; row++)
                {
                    // Jp (1x3) times R_w2c gives b (1x3); then dp/d(delta) = b [v]x, dp/d(centre) = -b.
                    double f = row == 0 ? fx : fy, pcc = row == 0 ? pc.x : pc.y;
                    double e0 = row == 0 ? f * iz : 0, e1 = row == 0 ? 0 : f * iz, e2 = -f * pcc * iz * iz;
                    double b0 = e0 * m.R.m00 + e1 * m.R.m10 + e2 * m.R.m20;
                    double b1 = e0 * m.R.m01 + e1 * m.R.m11 + e2 * m.R.m21;
                    double b2 = e0 * m.R.m02 + e1 * m.R.m12 + e2 * m.R.m22;
                    j0[0] = b1 * v.z - b2 * v.y; j0[1] = -b0 * v.z + b2 * v.x; j0[2] = b0 * v.y - b1 * v.x;
                    j0[3] = -b0; j0[4] = -b1; j0[5] = -b2;
                    for (int a = 0; a < 6; a++) for (int c = 0; c < 6; c++) H[a * 6 + c] += j0[a] * j0[c];
                }
            }
            double s2 = Math.Max(1, ssq / Math.Max(1, 2 * cnt - 6));
            double pos2 = 0, rot2 = 0;
            for (int a = 0; a < 6; a++)
            {
                Array.Copy(H, Hd, 36);
                for (int i = 0; i < 6; i++) rhs[i] = i == a ? 1 : 0;
                if (!SolveSystem(Hd, rhs, step)) return;
                if (a < 3) rot2 += s2 * step[a]; else pos2 += s2 * step[a];
            }
            posM = Math.Sqrt(Math.Max(0, pos2));
            rotDeg = Math.Sqrt(Math.Max(0, rot2)) * 180 / Math.PI;
        }

        static double Sq(double x) => x * x;

        static double LGamma(double x)
        {
            if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LGamma(1 - x);
            x -= 1;
            double a = 0.99999999999980993, t = x + 7.5;
            a += 676.5203681218851 / (x + 1);
            a += -1259.1392167224028 / (x + 2);
            a += 771.32342877765313 / (x + 3);
            a += -176.61502916214059 / (x + 4);
            a += 12.507343278686905 / (x + 5);
            a += -0.13857109526572012 / (x + 6);
            a += 9.9843695780195716e-6 / (x + 7);
            a += 1.5056327351493116e-7 / (x + 8);
            return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
        }

        static double LChoose(double n, double k) => LGamma(n + 1) - LGamma(k + 1) - LGamma(n - k + 1);

        static double BinomialLogTerm(int n, int j, double lp, double lq) => LChoose(n, j) + j * lp + (n - j) * lq;

        /// <summary>log10 P(X &gt;= k) for X ~ Binomial(n, p).</summary>
        static double Log10BinomialTail(int n, int k, double p)
        {
            if (k <= 0) return 0;
            if (k > n) return double.NegativeInfinity;
            double lp = Math.Log(p), lq = Math.Log(1 - p), top = double.NegativeInfinity;
            for (int j = k; j <= n; j++) top = Math.Max(top, BinomialLogTerm(n, j, lp, lq));
            double sum = 0;
            for (int j = k; j <= n; j++) sum += Math.Exp(BinomialLogTerm(n, j, lp, lq) - top);
            return Math.Min(0, (top + Math.Log(sum)) / Math.Log(10));
        }

        /// <summary>
        /// log10 of the chance that <paramref name="inliers"/> of <paramref name="detected"/> detections
        /// agree with some wrong pose by coincidence in a W x H image (confidence.ts).
        /// </summary>
        public static double Log10Chance(int detected, int inliers, int width, int height, double gatePx)
        {
            if (detected < 4 || inliers < 4) return 0;
            double p = Math.Min(1 - 1e-12, Math.PI * gatePx * gatePx / ((double)width * height));
            double lnTries = Math.Min(Math.Log(MaxHypotheses), Math.Log(4) + LChoose(detected, 3));
            return Math.Min(0, lnTries / Math.Log(10) + Log10BinomialTail(detected - 3, inliers - 3, p));
        }

        /// <summary>One 0..1 score from the chance of a coincidence and the pose uncertainty (half at 10 cm or 5 degrees).</summary>
        public static double ConfidenceScore(double log10Chance, double posCm, double rotDeg)
        {
            double chance = Math.Min(1, Math.Max(0, -log10Chance / 12));
            double precise = 1 / (1 + (posCm / 10) * (posCm / 10) + (rotDeg / 5) * (rotDeg / 5));
            return double.IsNaN(precise) ? 0 : chance * precise;
        }
    }
}
