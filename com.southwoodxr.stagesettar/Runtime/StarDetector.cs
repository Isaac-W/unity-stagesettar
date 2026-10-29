using System;
using System.Threading;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace StageSettAR
{
    /// <summary>Landmarks the network found in one frame. Buffers are reused until the next detection.</summary>
    public sealed class Detections
    {
        /// <summary>Number of detected landmarks; only the first <c>count</c> entries of the arrays are valid.</summary>
        public int count;
        /// <summary>Landmark index (network class) per detection, ascending.</summary>
        public int[] landmark;
        /// <summary>Network-input pixel (top-left origin, y down) per detection.</summary>
        public Vector2d[] pixel;
        /// <summary>Plane probability at the peak times the best class probability, 0..1.</summary>
        public double[] score;
        /// <summary>Synthetic camera of the network input, the intrinsics PnP must use.</summary>
        public CameraIntrinsics netIntrinsics;
        /// <summary>Clockwise quarter turns applied to the camera image before the network.</summary>
        public int quarterTurns;
        /// <summary>The frame this was computed from (pose at capture etc.).</summary>
        public CameraFrame frame;

        internal bool[] seen;

        public Detections(int capacity = 0) { Ensure(capacity); }

        internal void Ensure(int n)
        {
            if (landmark != null && landmark.Length >= n) return;
            landmark = new int[n]; pixel = new Vector2d[n]; score = new double[n]; seen = new bool[n + 1];
        }
    }

    /// <summary>
    /// Runs a map's landmark network on camera frames. The camera image is rotated, scaled and cropped
    /// on the GPU to the network's trained camera (port of star-mobile resample.ts), inference runs in
    /// Sentis (optionally a few layers per frame), and the readout (star-mobile readout.ts) is part of the same Sentis graph so that only
    /// about 5.5k floats are read back, asynchronously, instead of the 7 MB of logits.
    /// </summary>
    /// <remarks>
    /// Network input is <c>image01 * 255</c>: the shader writes the sRGB-encoded 8-bit colour as 0..1
    /// (linear-colour-space projects are handled by re-encoding each fetch) and the graph scales it.
    /// YUV frames (<see cref="CameraFrame.format"/>) are converted to that same colour in the same pass.
    /// The network input is as large as the frame allows (multiples of 16, see <see cref="Geometry"/>); the worker,
    /// input tensor and render texture are rebuilt only when that size changes.
    /// Not re-entrant: one detection at a time.
    /// </remarks>
    public sealed class StarDetector : IDisposable
    {
        const string ShaderResource = "StageSettARPreprocess";
        static readonly int SizesId = Shader.PropertyToID("_Sizes"), CropId = Shader.PropertyToID("_Crop"),
            OutId = Shader.PropertyToID("_Out"), FlagsId = Shader.PropertyToID("_Flags"),
            YuvId = Shader.PropertyToID("_Yuv"), ChromaSizeId = Shader.PropertyToID("_ChromaSize"),
            ChromaUId = Shader.PropertyToID("_ChromaU"), ChromaVId = Shader.PropertyToID("_ChromaV");

        const int SizeMultiple = 16;
        const string LogitsOutput = "logits";

        readonly StarMap _map;
        readonly BackendType _backend;
        readonly int _n;
        readonly Material _material;
        readonly Detections _det;
        Worker _worker;
        Tensor<float> _input;
        RenderTexture _rt;
        float[] _packed;
        int _w, _h;              // the size the worker, input and render texture were built for
        int _prevQuarter = -1;
        bool _busy, _disposed, _released, _stale;

        /// <summary>
        /// Layers of the network run per rendered frame; 0 or less runs the whole network at once. Spreading the work
        /// keeps one frame from paying for the entire dispatch. Takes effect from the next detection.
        /// </summary>
        public int LayersPerFrame { get; set; }

        /// <summary>Most pixels the network input may have; 0 or less for no limit. Takes effect from the next detection.</summary>
        public int MaxInputPixels { get; set; }

        /// <summary>Prepares a detector for <paramref name="map"/>. GPUCompute is right for Quest and desktop GPUs.</summary>
        /// <remarks>The network itself is built on first use (warm-up or the first frame), once the input size is known.</remarks>
        public StarDetector(StarMap map, BackendType backend = BackendType.GPUCompute)
        {
            if (map == null || map.model == null) throw new ArgumentException("Map has no model.");
            _map = map;
            _backend = backend;
            _n = map.LandmarkCount;
            var shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null) throw new InvalidOperationException("StageSettAR preprocess shader missing from Resources.");
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _det = new Detections(_n);
        }

        /// <summary>True while a detection (or warm-up) is in flight.</summary>
        public bool Busy => _busy;

        /// <summary>
        /// Runs the network once on black so shader compilation happens before the first real frame
        /// instead of stalling it. Optional; the first <see cref="DetectAsync"/> otherwise pays for it.
        /// Uses the map's suggested input size; real frames may rebuild the network at another size.
        /// </summary>
        public async Awaitable WarmupAsync(CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StarDetector));
            if (_busy) throw new InvalidOperationException("StarDetector is busy.");
            _busy = true;
            try
            {
                EnsureSize(Mathf.Max(SizeMultiple, _map.suggestedWidth / SizeMultiple * SizeMultiple),
                           Mathf.Max(SizeMultiple, _map.suggestedHeight / SizeMultiple * SizeMultiple));
                await RunNetwork(ct);
                (await ReadOutput()).Dispose();
            }
            finally { Finish(); }
        }

        /// <summary>
        /// Preprocesses <paramref name="frame"/> on the GPU right now (the texture may be released once
        /// this returns), schedules the network and awaits the small readout asynchronously.
        /// </summary>
        /// <returns>Detections owned by the detector, valid until the next call.</returns>
        /// <exception cref="InvalidOperationException">Another detection is in flight.</exception>
        public Awaitable<Detections> DetectAsync(CameraFrame frame, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StarDetector));
            if (_busy) throw new InvalidOperationException("StarDetector is busy.");
            ct.ThrowIfCancellationRequested();
            if (frame.texture == null || !frame.intrinsics.IsValid) throw new ArgumentException("Frame has no texture or intrinsics.");
            if (frame.format != CameraImageFormat.Rgb && (frame.chroma == null || (frame.format == CameraImageFormat.YuvPlanar && frame.chromaV == null)))
                throw new ArgumentException("YUV frame has no chroma texture.");
            _busy = true;
            try
            {
                Preprocess(frame);
                TextureConverter.ToTensor(_rt, _input, default);
            }
            catch { Finish(); throw; }
            return InferAsync(frame, ct);
        }

        async Awaitable<Detections> InferAsync(CameraFrame frame, CancellationToken ct)
        {
            try
            {
                await RunNetwork(ct);
                var t = await ReadOutput();
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (_disposed) throw new OperationCanceledException();
                    t.AsReadOnlySpan().CopyTo(_packed);
                }
                finally { t.Dispose(); }
                DecodeReadout(_packed, _n + 1, _h / _map.cellSize, _w / _map.cellSize, _det);
                _det.frame = frame;
                return _det;
            }
            finally { Finish(); }
        }

        /// <summary>
        /// Schedules the network. With <see cref="LayersPerFrame"/> set, steps that many layers and waits a frame between
        /// steps; the preprocessed input must stay untouched until this ends, which <see cref="Busy"/> guarantees.
        /// </summary>
        async Awaitable RunNetwork(CancellationToken ct)
        {
            int perFrame = LayersPerFrame;
            if (perFrame <= 0) { _worker.Schedule(_input); return; }
            bool finished = false;
            try
            {
                var steps = _worker.ScheduleIterable(_input);
                for (int i = 1; steps.MoveNext(); i++)
                {
                    if (i % perFrame != 0) continue;
                    await Awaitable.NextFrameAsync(ct);
                    if (_disposed) throw new OperationCanceledException();
                }
                finished = true;
            }
            finally
            {
                // A half-run schedule leaves the worker in an unknown state, so the next detection builds a fresh one.
                if (!finished) _stale = true;
            }
        }

        /// <summary>Builds (or rebuilds, if the size changed or a run was cut short) the network objects for a w x h input.</summary>
        void EnsureSize(int w, int h)
        {
            if (_worker != null && !_stale && w == _w && h == _h) return;
            FreeNetwork();
            var worker = new Worker(BuildModel(_map, w, h), _backend);
            _worker = worker;
            _input = new Tensor<float>(new TensorShape(1, 3, h, w));
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            _rt.Create();
            _packed = new float[(w / _map.cellSize) * (h / _map.cellSize) + 3 * _n];
            _w = w; _h = h; _stale = false;
        }

        void FreeNetwork()
        {
            _worker?.Dispose();
            _input?.Dispose();
            if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); }
            _worker = null; _input = null; _rt = null;
        }

        Awaitable<Tensor<float>> ReadOutput() => ((Tensor<float>)_worker.PeekOutput()).ReadbackAndCloneAsync();

        void Finish()
        {
            _busy = false;
            if (_disposed) Release();
        }

        /// <summary>Safe while a detection is in flight: the native objects are freed when it ends.</summary>
        public void Dispose()
        {
            _disposed = true;
            if (!_busy) Release();
        }

        void Release()
        {
            if (_released) return;
            _released = true;
            FreeNetwork();
            UnityEngine.Object.Destroy(_material);
        }

        // ---------------------------------------------------------------- preprocessing

        /// <summary>
        /// Clockwise quarter turns of the upright camera image that the network wants, from gravity
        /// (star-mobile landscapeQuarter + queryQuarter).
        /// </summary>
        /// <param name="map">Supplies the orientations the network was trained on.</param>
        /// <param name="cameraPose">Camera pose in a y-up tracking space (Unity: +z optical axis, +y image up).</param>
        /// <param name="hasPose">False falls back to the image shape alone.</param>
        /// <param name="previous">Last result, for hysteresis near a 45 degree tilt; -1 for none.</param>
        /// <param name="portraitImage">True when the image is taller than wide.</param>
        /// <remarks>
        /// Gravity in the image: world down expressed in the camera frame, x right, y down (OpenCV), i.e.
        /// <c>(local.x, -local.y)</c> with <c>local = inverse(rotation) * down</c>. "Natural" is the turn that
        /// makes the image landscape with gravity pointing down; a Quest headset held level gives 0.
        /// </remarks>
        public static int ChooseQuarterTurns(StarMap map, Pose cameraPose, bool hasPose, int previous = -1, bool portraitImage = false)
        {
            int natural = portraitImage ? 1 : 0;
            if (hasPose)
            {
                Vector3 g = Quaternion.Inverse(cameraPose.rotation) * Vector3.down;
                natural = LandscapeQuarter(g.x, -g.y, portraitImage, previous);
            }
            var seen = map.rotations;
            if (seen == null || seen.Length == 0) return Mod4(map.defaultRotation);
            return Array.IndexOf(seen, natural) >= 0 ? natural : Mod4(map.defaultRotation);
        }

        static int Mod4(int q) => ((q % 4) + 4) % 4;

        static int LandscapeQuarter(float dx, float dy, bool portrait, int prev)
        {
            int baseQ = portrait ? 1 : 0;
            bool ok = prev >= 0 && prev % 2 == baseQ;
            float n = Mathf.Sqrt(dx * dx + dy * dy);
            if (!(n >= 0.3f)) return ok ? prev : baseQ;
            float gx = dx, gy = dy;
            if (baseQ == 1) { float t = gx; gx = -gy; gy = t; }   // one clockwise turn: (x, y) -> (-y, x)
            if (ok && Mathf.Abs(Mathf.Abs(gy) - Mathf.Abs(gx)) < 0.15f * n) return prev;
            bool flip = Mathf.Abs(gy) >= Mathf.Abs(gx) ? gy < 0 : gx > 0;
            return flip ? (baseQ + 2) % 4 : baseQ;
        }

        /// <summary>The rotate, scale and crop that take a camera image to the network input.</summary>
        public struct PreprocessGeometry
        {
            /// <summary>Size of the image after the quarter turns.</summary>
            public float rotatedW, rotatedH;
            /// <summary>Isotropic scale from the rotated image to the network input.</summary>
            public float scale;
            /// <summary>Network input size in pixels, multiples of 16.</summary>
            public int outW, outH;
            /// <summary>Crop window in the rotated image. Inside it unless the frame is smaller than 16 scaled pixels (the shader pads with black).</summary>
            public float x0, y0, cropW, cropH;
            /// <summary>Box-filter taps per axis.</summary>
            public int taps;
            /// <summary>Synthetic camera of the network input.</summary>
            public CameraIntrinsics net;
        }

        /// <summary>
        /// Network input size and intrinsics through turn, scale and crop. <paramref name="k"/> must already be at srcW x srcH.
        /// Rotating the camera q clockwise turns gives K' = (fy, fx, h - cy, cx) and swaps the size
        /// (conventions.ts rotateCamera); then one scale s = focal / fx'. The network input is the largest multiple-of-16
        /// size inside the scaled frame (and inside <paramref name="maxPixels"/>, keeping the aspect), cropped about the
        /// image centre (not the principal point): fx'' = fx' s, cx'' = (cx' - x0) s.
        /// </summary>
        /// <param name="maxPixels">Most pixels the network input may have; 0 or less for no limit.</param>
        public static PreprocessGeometry Geometry(CameraIntrinsics k, int srcW, int srcH, int q, float mapFocal, int maxPixels = 0)
        {
            float fx = k.fx, fy = k.fy, cx = k.cx, cy = k.cy, rw = srcW, rh = srcH;
            for (int i = 0; i < q; i++)
            {
                (fx, fy, cx, cy) = (fy, fx, rh - cy, cx);
                (rw, rh) = (rh, rw);
            }

            float s = mapFocal / fx;
            float sw = rw * s, sh = rh * s;
            int outW = FloorToMultiple(sw), outH = FloorToMultiple(sh);
            if (maxPixels > 0 && (long)outW * outH > maxPixels)
            {
                float t = Mathf.Sqrt(maxPixels / (sw * sh));
                outW = FloorToMultiple(sw * t);
                outH = FloorToMultiple(sh * t);
            }
            float cw = outW / s, ch = outH / s, x0 = (rw - cw) * 0.5f, y0 = (rh - ch) * 0.5f;
            return new PreprocessGeometry
            {
                rotatedW = rw, rotatedH = rh, scale = s, outW = outW, outH = outH,
                x0 = x0, y0 = y0, cropW = cw, cropH = ch,
                taps = Mathf.Clamp(Mathf.CeilToInt(cw / outW), 1, 4),
                net = new CameraIntrinsics(fx * s, fy * s, (cx - x0) * s, (cy - y0) * s, outW, outH),
            };
        }

        static int FloorToMultiple(float v) => Mathf.Max(SizeMultiple, Mathf.FloorToInt(v / SizeMultiple) * SizeMultiple);

        /// <summary>
        /// CPU reference of the shader's YUV conversion: BT.601 full range, all values 0..1 (Cb and Cr centred on 0.5),
        /// result clamped to 0..1 and sRGB-encoded like the RGB path.
        /// </summary>
        public static Vector3 YuvToRgb(float y, float cb, float cr)
        {
            cb -= 0.5f; cr -= 0.5f;
            return new Vector3(Mathf.Clamp01(y + 1.402f * cr),
                               Mathf.Clamp01(y - 0.344136f * cb - 0.714136f * cr),
                               Mathf.Clamp01(y + 1.772f * cb));
        }

        void Preprocess(CameraFrame frame)
        {
            Texture tex = frame.texture;
            int srcW = frame.ImageWidth, srcH = frame.ImageHeight;
            var k = frame.intrinsics;
            if (k.width != srcW || k.height != srcH) k = k.Scaled(srcW, srcH);

            int q = ChooseQuarterTurns(_map, frame.cameraPose, frame.hasPose, _prevQuarter, srcW < srcH);
            _prevQuarter = q;

            var g = Geometry(k, srcW, srcH, q, _map.focal, MaxInputPixels);
            int outW = g.outW, outH = g.outH;
            EnsureSize(outW, outH);

            bool yuv = frame.format != CameraImageFormat.Rgb;
            bool linearFetch = !yuv && GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat) && QualitySettings.activeColorSpace == ColorSpace.Linear;
            _material.SetVector(SizesId, new Vector4(srcW, srcH, g.rotatedW, g.rotatedH));
            _material.SetVector(CropId, new Vector4(g.x0, g.y0, g.cropW, g.cropH));
            _material.SetVector(OutId, new Vector4(outW, outH, q, g.taps));
            _material.SetVector(FlagsId, new Vector4(frame.flipY ? 1 : 0, linearFetch ? 1 : 0, 0, 0));
            // YUV: x = layout (0 RGB, 1 interleaved chroma, 2 separate chroma planes), y = chroma texel step, z/w = luma texture size
            _material.SetVector(YuvId, new Vector4((float)frame.format, Mathf.Max(1, frame.chromaPixelStride), tex.width, tex.height));
            if (yuv)
            {
                _material.SetTexture(ChromaUId, frame.chroma);
                _material.SetTexture(ChromaVId, frame.chromaV != null ? frame.chromaV : frame.chroma);
                _material.SetVector(ChromaSizeId, new Vector4(frame.chroma.width, frame.chroma.height, 0, 0));
            }
            Graphics.Blit(tex, _rt, _material);

            _det.quarterTurns = q;
            _det.netIntrinsics = g.net;
        }

        // ---------------------------------------------------------------- readout graph

        static Model BuildModel(StarMap map, int w, int h)
        {
            var g = new FunctionalGraph();
            var image = g.AddInput(DataType.Float, new TensorShape(1, 3, h, w), "image01");
            var onnx = ModelLoader.Load(map.model);
            var outputs = Functional.Forward(onnx, image * 255f);
            // By name, so extra outputs in the model cannot shift it; a lone unnamed output is taken as the logits.
            int at = onnx.outputs.FindIndex(o => o.name == LogitsOutput);
            if (at < 0 && onnx.outputs.Count == 1) at = 0;
            if (at < 0) throw new InvalidOperationException($"Model has no '{LogitsOutput}' output.");
            return g.Compile(BuildReadout(outputs[at], map.LandmarkCount + 1, h / map.cellSize, w / map.cellSize, map.cellSize));
        }

        /// <summary>A graph that only does the readout, input "logits" [1, classes, gh, gw]. For tests.</summary>
        public static Model BuildReadoutModel(int classes, int gh, int gw, int cell)
        {
            var g = new FunctionalGraph();
            var logits = g.AddInput(DataType.Float, new TensorShape(1, classes, gh, gw), "logits");
            return g.Compile(BuildReadout(logits, classes, gh, gw, cell));
        }

        /// <summary>
        /// The readout reductions as one flat float output, decoded by <see cref="DecodeReadout"/>:
        /// [0, gh*gw) argmax class per cell (over all classes incl. reject); then N scores, N pixel x, N pixel y
        /// per landmark (score = row softmax at the peak x max over cells of the class softmax; pixel = 3x3
        /// softmax-weighted cell centres around the row's peak, row/col clamped separately).
        /// </summary>
        static FunctionalTensor BuildReadout(FunctionalTensor logits, int classes, int gh, int gw, int cell)
        {
            int n = classes - 1, hw = gh * gw;
            var z = logits.Reshape(new[] { classes, hw });
            var top = Functional.ArgMax(z, 0).Float();                                   // [hw]
            var rows = z.Narrow(0, 0, n);                                                // [N, hw]
            var peak = Functional.ArgMax(rows, 1);                                       // [N] int
            var plane = Functional.Exp(Functional.ReduceMax(rows, 1) - Functional.ReduceLogSumExp(rows, 1));
            var classP = Functional.ReduceMax(Functional.Softmax(z, 0).Narrow(0, 0, n), 1);

            var r0 = Functional.FloorDivide(peak, Functional.Constant(gw)).Unsqueeze(1); // [N,1]
            var c0 = (peak % gw).Unsqueeze(1);
            var off = new int[9];
            var dr = new int[9];
            for (int i = 0; i < 9; i++) { dr[i] = i / 3 - 1; off[i] = i % 3 - 1; }
            var rr = Functional.Clamp(r0 + Functional.Constant(new TensorShape(1, 9), dr), 0, gh - 1);
            var cc = Functional.Clamp(c0 + Functional.Constant(new TensorShape(1, 9), off), 0, gw - 1);
            var w = Functional.Softmax(rows.Gather(1, rr * gw + cc), 1);                 // [N,9]
            var u = Functional.ReduceSum(w * ((cc.Float() + 0.5f) * (float)cell), 1);
            var v = Functional.ReduceSum(w * ((rr.Float() + 0.5f) * (float)cell), 1);
            return Functional.Concat(new[] { top, plane * classP, u, v }, 0);
        }

        /// <summary>Turns the flat readout tensor into detections (landmarks that are the top class of some cell).</summary>
        public static void DecodeReadout(float[] packed, int classes, int gh, int gw, Detections into)
        {
            int n = classes - 1, hw = gh * gw;
            into.Ensure(n);
            Array.Clear(into.seen, 0, into.seen.Length);
            for (int c = 0; c < hw; c++)
            {
                float v = packed[c];
                if (v >= 0f && v < classes) into.seen[(int)v] = true;   // NaN fails both tests
            }
            int count = 0;
            for (int l = 0; l < n; l++)
            {
                if (!into.seen[l]) continue;
                into.landmark[count] = l;
                into.score[count] = packed[hw + l];
                into.pixel[count] = new Vector2d(packed[hw + n + l], packed[hw + 2 * n + l]);
                count++;
            }
            into.count = count;
        }

        /// <summary>
        /// CPU reference of the readout from raw logits laid out [classes, gh, gw] (reject bias already applied,
        /// last class = reject), the specification the in-graph readout must match.
        /// </summary>
        public static void ReadoutReference(float[] logits, int classes, int gh, int gw, int cell, Detections into)
        {
            int n = classes - 1, hw = gh * gw;
            into.Ensure(n);
            Array.Clear(into.seen, 0, into.seen.Length);
            var cls = new double[classes];
            var best = new double[n];
            for (int c = 0; c < hw; c++)
            {
                int arg = 0; double max = logits[c];
                for (int k = 1; k < classes; k++) if (logits[k * hw + c] > max) { max = logits[k * hw + c]; arg = k; }
                double sum = 0;
                for (int k = 0; k < classes; k++) { cls[k] = Math.Exp(logits[k * hw + c] - max); sum += cls[k]; }
                into.seen[arg] = true;
                for (int l = 0; l < n; l++) best[l] = c == 0 ? cls[l] / sum : Math.Max(best[l], cls[l] / sum);
            }
            int count = 0;
            for (int l = 0; l < n; l++)
            {
                if (!into.seen[l]) continue;
                int o = l * hw, peak = 0;
                for (int c = 1; c < hw; c++) if (logits[o + c] > logits[o + peak]) peak = c;
                double lse = 0, zp = logits[o + peak];
                for (int c = 0; c < hw; c++) lse += Math.Exp(logits[o + c] - zp);
                double plane = 1.0 / lse;                                    // exp(zp - logsumexp)
                int r0 = peak / gw, c0 = peak % gw;
                double wsum = 0, pu = 0, pv = 0;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int r = Math.Min(gh - 1, Math.Max(0, r0 + dr)), cl = Math.Min(gw - 1, Math.Max(0, c0 + dc));
                        double e = Math.Exp(logits[o + r * gw + cl] - zp);   // zp is the window max (it contains the peak)
                        wsum += e; pu += e * (cl + 0.5) * cell; pv += e * (r + 0.5) * cell;
                    }
                into.landmark[count] = l;
                into.score[count] = plane * best[l];
                into.pixel[count] = new Vector2d(pu / wsum, pv / wsum);
                count++;
            }
            into.count = count;
        }
    }
}
