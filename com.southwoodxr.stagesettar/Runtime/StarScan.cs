#pragma warning disable CS0649 // fields are filled by JsonUtility
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>Aspect ratio to crop the upright frames to, to imitate a camera other than the scan's.</summary>
    public enum CropAspect { None, Square, FourThree, Custom }

    /// <summary>One frame of a <see cref="StarScan"/>: pose and intrinsics as stored, i.e. for the stored (turned) image.</summary>
    public struct ScanFrame
    {
        /// <summary>The frame's name in the scan (frames/NNNNNN.jpg); not necessarily its position in <see cref="StarScan.Frames"/>.</summary>
        public int index;
        public int timeMs;
        /// <summary>OpenCV camera-to-world rotation of the stored image, in the export frame.</summary>
        public Matrix3x3d R;
        /// <summary>Camera centre in the export frame.</summary>
        public Vector3d t;
        /// <summary>In stored pixels.</summary>
        public CameraIntrinsics K;
        /// <summary>Clockwise quarter turns from the upright camera image to the stored image.</summary>
        public int rotation;
        /// <summary>The pose is a guess or a placeholder, or lies in a world that drifted: not usable as ground truth.</summary>
        public bool unplaced, untracked;
        public int segment;

        public bool HasGroundTruth => !unplaced && !untracked && segment == 0;
    }

    /// <summary>A stored frame turned into the upright camera frame the pipeline expects.</summary>
    public struct UprightFrame
    {
        /// <summary>Owned by the caller (release with <see cref="UnityObjects.Discard"/>). Unity orientation: row 0 is the bottom, so <c>flipY</c> is true.</summary>
        public Texture2D texture;
        public CameraIntrinsics intrinsics;
        /// <summary>Ground-truth OpenCV camera-to-world pose of the upright camera, export frame.</summary>
        public Matrix3x3d R;
        public Vector3d t;
    }

    /// <summary>
    /// A recorded capture (.starscan, star-web-demo's scan file): camera images with ground-truth poses in the map's
    /// world. Read from the zip itself or from an unpacked folder; JPEGs are only decoded when asked for.
    /// </summary>
    /// <remarks>
    /// Each stored JPEG is the upright camera image turned <see cref="ScanFrame.rotation"/> clockwise quarter turns, and
    /// its pose and intrinsics are for that turned image. <see cref="LoadUpright"/> undoes the turn, which is what
    /// <see cref="CameraFrame"/> wants. Every 10th frame (position 9, 19, ...) is held out for testing unless the scan
    /// lists its own test frames.
    /// </remarks>
    public sealed class StarScan : IDisposable
    {
        [Serializable] class SizeDto { public int width, height; }
        [Serializable] class IntrinsicsDto { public float fx, fy, cx, cy; }
        [Serializable] class MetaDto
        {
            public string format, id, name, device;
            public int version, rotation;
            public SizeDto image;
            public IntrinsicsDto intrinsics;
        }

        const string Format = "starscan";
        const int MaxVersion = 2;
        const int HoldoutEvery = 10;

        readonly ZipArchive _zip;
        readonly string _folder;
        readonly HashSet<int> _test = new HashSet<int>();
        readonly List<ScanFrame> _frames = new List<ScanFrame>();

        public string Name { get; private set; }
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }

        /// <summary>The frames in the order of poses.txt.</summary>
        public IReadOnlyList<ScanFrame> Frames => _frames;

        /// <summary>Opens a .starscan file or an unpacked scan folder.</summary>
        public static StarScan Open(string path)
        {
            if (Directory.Exists(path)) return new StarScan(path, null);
            if (!File.Exists(path)) throw new FileNotFoundException("No such scan: " + path);
            var zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
            try { return new StarScan(null, zip); }
            catch { zip.Dispose(); throw; }
        }

        StarScan(string folder, ZipArchive zip)
        {
            _folder = folder;
            _zip = zip;
            ParseMeta(out CameraIntrinsics k0, out int rotation0);
            ParseFrames(k0, rotation0);
        }

        public void Dispose() => _zip?.Dispose();

        // ---------------------------------------------------------------- parsing

        void ParseMeta(out CameraIntrinsics k0, out int rotation0)
        {
            byte[] bytes = Read("meta.json") ?? throw new InvalidDataException("Not a .starscan (no meta.json).");
            var meta = JsonUtility.FromJson<MetaDto>(Encoding.UTF8.GetString(bytes));
            if (meta == null || meta.format != Format) throw new InvalidDataException("Not a .starscan file.");
            if (meta.version > MaxVersion) throw new InvalidDataException($".starscan version {meta.version} is newer than this reader.");
            if (meta.image == null || meta.image.width <= 0 || meta.image.height <= 0) throw new InvalidDataException("meta.json has no image size.");
            if (meta.intrinsics == null) throw new InvalidDataException("meta.json has no intrinsics.");
            Name = string.IsNullOrEmpty(meta.name) ? meta.id : meta.name;
            ImageWidth = meta.image.width;
            ImageHeight = meta.image.height;
            var i = meta.intrinsics;
            k0 = new CameraIntrinsics(i.fx, i.fy, i.cx, i.cy, ImageWidth, ImageHeight);
            rotation0 = Mod4(meta.rotation);
        }

        void ParseFrames(CameraIntrinsics k0, int rotation0)
        {
            var intrinsics = new Dictionary<int, double[]>();
            foreach (var r in Rows("intrinsics.txt", 5)) intrinsics[(int)r[0]] = r;
            var rotations = new Dictionary<int, int>();
            foreach (var r in Rows("rotations.txt", 2)) rotations[(int)r[0]] = Mod4((int)r[1]);
            var unplaced = IndexSet("unplaced.txt");
            var untracked = IndexSet("untracked.txt");
            var segments = new Dictionary<int, int>();
            foreach (var r in Rows("segments.txt", 2)) segments[(int)r[0]] = (int)r[1];
            foreach (int i in IndexSet("test.txt")) _test.Add(i);

            foreach (var r in Rows("poses.txt", 9))
            {
                int index = (int)r[0];
                var f = new ScanFrame
                {
                    index = index,
                    timeMs = (int)r[1],
                    t = new Vector3d(r[2], r[3], r[4]),
                    R = Matrix3x3d.FromQuaternion(new Quaternion((float)r[5], (float)r[6], (float)r[7], (float)r[8])),   // Hamilton x y z w, normalized there
                    K = k0,
                    rotation = rotations.TryGetValue(index, out int q) ? q : rotation0,
                    unplaced = unplaced.Contains(index),
                    untracked = untracked.Contains(index),
                    segment = segments.TryGetValue(index, out int seg) ? seg : 0,
                };
                if (intrinsics.TryGetValue(index, out var k)) f.K = new CameraIntrinsics((float)k[1], (float)k[2], (float)k[3], (float)k[4], ImageWidth, ImageHeight);
                _frames.Add(f);
            }
        }

        /// <summary>Numeric rows of a text file: blank lines and '#' lines skipped, split on whitespace. A missing file has no rows.</summary>
        List<double[]> Rows(string name, int minColumns)
        {
            var rows = new List<double[]>();
            byte[] bytes = Read(name);
            if (bytes == null) return rows;
            foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < minColumns) throw new InvalidDataException($"{name}: expected {minColumns} columns, got '{line}'.");
                var row = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out row[i]))
                        throw new InvalidDataException($"{name}: not a number: '{parts[i]}'.");
                rows.Add(row);
            }
            return rows;
        }

        HashSet<int> IndexSet(string name)
        {
            var set = new HashSet<int>();
            foreach (var r in Rows(name, 1)) set.Add((int)r[0]);
            return set;
        }

        /// <summary>A file of the scan, or null when it is not there.</summary>
        byte[] Read(string name)
        {
            if (_zip == null)
            {
                string path = Path.Combine(_folder, name);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            var entry = _zip.GetEntry(name);
            if (entry == null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        // ---------------------------------------------------------------- test split

        /// <summary>
        /// Positions in <see cref="Frames"/> of the frames to test against: those in test.txt if it lists any, otherwise
        /// every 10th. Frames without usable ground truth are left out.
        /// </summary>
        public List<int> TestFrames()
        {
            var result = new List<int>();
            for (int i = 0; i < _frames.Count; i++)
            {
                if (!_frames[i].HasGroundTruth) continue;
                bool isTest = _test.Count > 0 ? _test.Contains(_frames[i].index) : i % HoldoutEvery == HoldoutEvery - 1;
                if (isTest) result.Add(i);
            }
            return result;
        }

        /// <summary>Positions in <see cref="Frames"/> of all frames with usable ground truth.</summary>
        public List<int> PlacedFrames()
        {
            var result = new List<int>();
            for (int i = 0; i < _frames.Count; i++) if (_frames[i].HasGroundTruth) result.Add(i);
            return result;
        }

        // ---------------------------------------------------------------- images

        /// <summary>Decodes the stored JPEG of the frame at <paramref name="position"/> in <see cref="Frames"/>. The caller owns the texture.</summary>
        public Texture2D LoadStored(int position)
        {
            int index = _frames[position].index;
            byte[] bytes = Read($"frames/{index:D6}.jpg") ?? throw new FileNotFoundException($"Scan has no image for frame {index}.");
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(bytes)) { UnityObjects.Discard(tex); throw new InvalidDataException($"Frame {index} is not a readable image."); }
            return tex;
        }

        /// <summary>
        /// The upright camera frame of the frame at <paramref name="position"/>, with its ground-truth pose. A positive
        /// <paramref name="cropAspect"/> (width / height) centre-crops the largest such rectangle, to imitate a camera with another aspect ratio.
        /// </summary>
        public UprightFrame LoadUpright(int position, float cropAspect = 0f)
        {
            ScanFrame f = _frames[position];
            Texture2D stored = LoadStored(position);
            CameraIntrinsics k = f.K;
            if (k.width != stored.width || k.height != stored.height) k = k.Scaled(stored.width, stored.height);

            StoredToUpright(k, f.R, f.rotation, out CameraIntrinsics ku, out Matrix3x3d ru);
            Texture2D tex = stored;
            if (f.rotation != 0)
            {
                Color32[] px = RotateCcw(stored.GetPixels32(), stored.width, stored.height, f.rotation, out int w, out int h);
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.SetPixels32(px);
                tex.Apply(false);
                UnityObjects.Discard(stored);
            }

            if (cropAspect > 0f)
            {
                CropRect(tex.width, tex.height, cropAspect, out int x0, out int y0, out int cw, out int ch);
                if (cw != tex.width || ch != tex.height)
                {
                    Texture2D cropped = new Texture2D(cw, ch, TextureFormat.RGB24, false);
                    cropped.SetPixels32(CropPixels(tex.GetPixels32(), tex.width, tex.height, x0, y0, cw, ch));
                    cropped.Apply(false);
                    UnityObjects.Discard(tex);
                    tex = cropped;
                    ku = CropIntrinsics(ku, x0, y0, cw, ch);
                }
            }
            return new UprightFrame { texture = tex, intrinsics = ku, R = ru, t = f.t };
        }

        /// <summary>Width / height of a crop preset; 0 (no crop) for <see cref="CropAspect.None"/> or a custom ratio that is not positive.</summary>
        public static float AspectRatio(CropAspect aspect, float customW, float customH) => aspect switch
        {
            CropAspect.Square => 1f,
            CropAspect.FourThree => 4f / 3f,
            CropAspect.Custom => customW > 0f && customH > 0f ? customW / customH : 0f,
            _ => 0f,
        };

        /// <summary>The largest centred rectangle of ratio <paramref name="aspect"/> (width / height) inside w x h, in whole pixels.</summary>
        public static void CropRect(int w, int h, float aspect, out int x0, out int y0, out int cw, out int ch)
        {
            cw = w; ch = h;
            if (aspect > 0f)
            {
                if ((float)w / h > aspect) cw = Mathf.Clamp(Mathf.RoundToInt(h * aspect), 1, w);
                else ch = Mathf.Clamp(Mathf.RoundToInt(w / aspect), 1, h);
            }
            x0 = (w - cw) / 2;
            y0 = (h - ch) / 2;
        }

        /// <summary>The same camera seen through a window: the principal point moves, the focal lengths do not.</summary>
        public static CameraIntrinsics CropIntrinsics(CameraIntrinsics k, int x0, int y0, int cw, int ch) =>
            new CameraIntrinsics(k.fx, k.fy, k.cx - x0, k.cy - y0, cw, ch);

        /// <summary>Cuts a cw x ch window with top-left corner (x0, y0) out of a w x h image. Pixels are in Unity's order (row 0 is the bottom).</summary>
        public static Color32[] CropPixels(Color32[] px, int w, int h, int x0, int y0, int cw, int ch)
        {
            var result = new Color32[cw * ch];
            int bottom = h - y0 - ch;   // bottom row of the window
            for (int y = 0; y < ch; y++) Array.Copy(px, (bottom + y) * w + x0, result, y * cw, cw);
            return result;
        }

        // ---------------------------------------------------------------- stored to upright

        /// <summary>
        /// The upright camera's intrinsics and rotation from the stored image's: undoes <paramref name="q"/> clockwise
        /// quarter turns, <c>R_upright = R_stored · (M^q)^T</c> (the camera centre does not change).
        /// </summary>
        public static void StoredToUpright(CameraIntrinsics stored, Matrix3x3d rStored, int q, out CameraIntrinsics upright, out Matrix3x3d rUpright)
        {
            upright = stored;
            for (int i = 0; i < Mod4(q); i++)
                upright = new CameraIntrinsics(upright.fy, upright.fx, upright.cy, upright.width - upright.cx, upright.height, upright.width);
            rUpright = rStored * Conventions.QuarterTurnCameraRotation(q).Transpose();
        }

        /// <summary>Where a point at <paramref name="p"/> (pixels, top-left origin) of a w x h image lands after <paramref name="q"/> counter-clockwise quarter turns.</summary>
        public static Vector2 TurnPixelCcw(Vector2 p, int w, int h, int q)
        {
            for (int i = 0; i < Mod4(q); i++)
            {
                p = new Vector2(p.y, w - p.x);
                (w, h) = (h, w);
            }
            return p;
        }

        /// <summary>
        /// Rotates a w x h image <paramref name="q"/> quarter turns counter-clockwise. Pixels are in Unity's order (row 0 is the
        /// bottom row), as <c>GetPixels32</c> returns them.
        /// </summary>
        public static Color32[] RotateCcw(Color32[] px, int w, int h, int q, out int outW, out int outH)
        {
            outW = w; outH = h;
            for (int i = 0; i < Mod4(q); i++)
            {
                // Output (x', y') in top-left terms comes from input (w - 1 - y', x'); the output is h wide and w tall.
                var rotated = new Color32[px.Length];
                for (int y = 0; y < w; y++)          // output row, top-left terms
                    for (int x = 0; x < h; x++)      // output column
                        rotated[(w - 1 - y) * h + x] = px[(h - 1 - x) * w + (w - 1 - y)];
                px = rotated;
                (w, h) = (h, w);
                outW = w; outH = h;
            }
            return px;
        }

        static int Mod4(int q) => ((q % 4) + 4) % 4;
    }

    /// <summary>The numbers of a holdout evaluation, as in star-web-demo's pipeline.</summary>
    public struct HoldoutScore
    {
        public int frames;
        public double recall5cm5deg, recall10cm10deg;   // percent
        public double medianCm, medianDeg;
    }

    /// <summary>Pose error and its summary over a set of frames.</summary>
    public static class PoseMetrics
    {
        /// <summary>Camera-centre distance in metres and rotation angle in degrees between two camera-to-world poses.</summary>
        public static void Distance(in Matrix3x3d ra, in Vector3d ta, in Matrix3x3d rb, in Vector3d tb, out double meters, out double degrees)
        {
            Matrix3x3d d = ra.Transpose() * rb;
            degrees = Math.Acos(Math.Max(-1.0, Math.Min(1.0, (d.Trace - 1) / 2))) * 180.0 / Math.PI;
            meters = (ta - tb).Length;
        }

        /// <summary>Median of the values (infinities allowed); the mean of the two middle ones for an even count. NaN when empty.</summary>
        public static double Median(IReadOnlyList<double> values)
        {
            int n = values.Count;
            if (n == 0) return double.NaN;
            var sorted = new double[n];
            for (int i = 0; i < n; i++) sorted[i] = values[i];
            Array.Sort(sorted);
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        }

        /// <summary>
        /// Recall within 5 cm / 5 degrees and 10 cm / 10 degrees (inclusive) and the medians. A frame whose pose could not be
        /// solved has both errors infinite, so it counts as a miss and pushes the medians up.
        /// </summary>
        public static HoldoutScore Score(IReadOnlyList<double> meters, IReadOnlyList<double> degrees)
        {
            int n = meters.Count, ok5 = 0, ok10 = 0;
            for (int i = 0; i < n; i++)
            {
                if (meters[i] <= 0.05 && degrees[i] <= 5) ok5++;
                if (meters[i] <= 0.10 && degrees[i] <= 10) ok10++;
            }
            return new HoldoutScore
            {
                frames = n,
                recall5cm5deg = n > 0 ? 100.0 * ok5 / n : double.NaN,
                recall10cm10deg = n > 0 ? 100.0 * ok10 / n : double.NaN,
                medianCm = 100 * Median(meters),
                medianDeg = Median(degrees),
            };
        }
    }
}
