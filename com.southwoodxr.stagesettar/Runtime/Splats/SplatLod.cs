using System;
using System.Collections.Generic;
using Gsplat;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Distance-based splat LOD: splats thin out with distance from a centre, so nearby detail
    /// survives and far detail falls away. Replaces the Gradient cutout of the UnitySplats fork.
    ///
    /// Retention is authored as a curve over normalized distance -- 0 at the centre, 1 at
    /// <see cref="Radius"/>, clamped past that -- and scaled by <see cref="Density"/>. Which splats
    /// go is decided by hashing the splat index, never the position or the frame, so every splat
    /// holds one fixed draw for all time: moving the centre only slides the threshold past it and
    /// splats cross it one at a time. Hashing anything view-dependent makes the volume shimmer.
    /// <see cref="FavorLarge"/> and <see cref="FavorOpaque"/> reweight that draw so large and
    /// opaque splats are the last to go; they reweight the exponent (keep = retention^(1/weight))
    /// rather than the value, so a curve that asks to keep everything, or nothing, is obeyed at any
    /// setting.
    ///
    /// How it works with stock UnitySplats: instead of a new cutout type evaluated on the GPU, the
    /// kept splats are worked out on the CPU from the asset's own data and handed to the renderer
    /// through <see cref="GsplatRenderer.SetActiveRanges"/>, which is public API. That needs no
    /// patched shaders, but it costs one pass over the asset (a few tens of milliseconds per
    /// million splats) whenever a setting changes or the centre moves farther than
    /// <see cref="RefreshDistance"/>, and while it is active the renderer's ordinary cutouts are
    /// not applied (UnitySplats treats active ranges as the exact selection). Do not combine it
    /// with UnitySplats' own LOD streaming on the same renderer.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("StageSettAR/Splats/Splat LOD (Gradient)")]
    public class SplatLod : MonoBehaviour
    {
        public enum Preset
        {
            Linear,
            Smooth,
            Exponential,
            Custom
        }

        [Tooltip("Splat to thin. Found automatically on this object, its parents or its children when left empty.")]
        public GsplatRenderer Renderer;

        [Tooltip("Where the falloff is centred. Defaults to this object. Point it at the camera rig to keep detail around the viewer.")]
        public Transform Center;

        [Tooltip("World distance from the centre at which the retention curve ends.")]
        [Min(0.01f)] public float Radius = 20f;

        [Tooltip("Shape of the falloff. Pick Custom to edit the curve by hand.")]
        public Preset Falloff = Preset.Exponential;

        [Tooltip("How sharply the Exponential preset drops. Higher keeps the centre denser.")]
        [Range(0f, 6f)] public float Sharpness = 4f;

        [Tooltip(
            "Share of splats kept over normalized distance: 0 at the centre, 1 at the radius " +
            "(the value past 1 holds). 1 keeps every splat, 0 drops them all.")]
        public AnimationCurve Retention = ExponentialRetention();

        [Tooltip("Scales the whole curve. Thins the entire volume without reshaping the falloff.")]
        [Range(0f, 1f)] public float Density = 1f;

        [Header("Which Splats Go First")]
        [Tooltip(
            "0% drops splats at random. Higher keeps large splats longer, so fine detail disappears " +
            "with distance before coarse structure does.")]
        [Range(0f, 100f)] public float FavorLarge;

        [Tooltip(
            "0% ignores opacity. Higher clears faint, low-alpha haze first, which is usually the " +
            "cheapest quality-per-splat win in an outdoor capture.")]
        [Range(0f, 100f)] public float FavorOpaque;

        [Header("Updating")]
        [Tooltip("How far the centre (or the splat itself) has to move before the selection is recomputed.")]
        [Min(0f)] public float RefreshDistance = 0.5f;

        [Tooltip("Fewest seconds between recomputes while the centre moves.")]
        [Min(0f)] public float MinRefreshSeconds = 0.25f;

        // The two ceilings differ because the quantities do: splat scales are log-distributed and
        // the ratio to the asset median spans roughly 0.12x-20x on a typical capture, so an
        // exponent of 2 already leaves the top quartile unconditionally kept and the bottom
        // quartile all but gone -- past that the selection is deterministic and the cliff
        // artefacts come back. Opacity is bounded to 0..1 and can only push a splat's odds down,
        // so it takes a higher exponent before it stops being a gradient.
        const float k_maxSizeBias = 2f;
        const float k_maxOpacityBias = 4f;
        const int k_lutSize = 128;

        // Must match LN_SCALE_MIN / LN_SCALE_MAX in UnitySplats' GsplatSpark.hlsl, which owns the
        // Spark packing format.
        const float k_lnScaleMin = -12f;
        const float k_lnScaleMax = 9f;

        /// <summary>Splats the last recompute kept.</summary>
        public uint KeptCount { get; private set; }

        /// <summary>Splats in the asset.</summary>
        public uint TotalCount { get; private set; }

        /// <summary>Fired after each recompute has been handed to the renderer.</summary>
        public event Action Refreshed;

        bool m_dirty = true;
        bool m_applied;
        GsplatAsset m_appliedAsset;
        Matrix4x4 m_appliedMatrix;
        Vector3 m_appliedCenter;
        float m_lastRefreshTime = float.NegativeInfinity;
        GsplatAsset m_referenceAsset;
        float m_referenceScale;

        readonly float[] m_lut = new float[k_lutSize];
        readonly List<GsplatActiveRange> m_ranges = new();

        // ---- retention presets ----
        // All of them are two or three keyframes carrying real tangents rather than a dense
        // sampling of an analytic function: a preset is a starting point to drag, and a curve with
        // eight baked points is miserable to edit by hand.

        public static AnimationCurve LinearRetention() => AnimationCurve.Linear(0f, 1f, 1f, 0f);

        public static AnimationCurve SmoothRetention() =>
            new(new Keyframe(0f, 1f, 0f, 0f), new Keyframe(1f, 0f, 0f, 0f));

        /// <summary>Normalized exponential falloff, exp(-k*t) rescaled to reach exactly 0 at the radius.</summary>
        public static AnimationCurve ExponentialRetention(float falloff = 4f)
        {
            // Below this the exponential is indistinguishable from a straight line, and the
            // normalization divides by something close to zero.
            if (falloff < 1e-3f)
                return LinearRetention();

            float tail = Mathf.Exp(-falloff);
            float scale = 1f / (1f - tail);

            float Value(float t) => (Mathf.Exp(-falloff * t) - tail) * scale;
            float Slope(float t) => -falloff * Mathf.Exp(-falloff * t) * scale;

            // Two keyframes cannot hold this shape: a single cubic matching both endpoint slopes
            // undershoots hard through the middle and dips well below zero. A third key at the
            // midpoint tracks the true curve to within 0.017 at the default falloff, and three
            // points stay comfortable to edit. The midpoint stays at 0.5 rather than following the
            // curve's half-value point, which sounds better and measures worse. Past a falloff of
            // about 6 three keys start to loosen; the retention is clamped to 0..1 regardless.
            return new AnimationCurve(
                new Keyframe(0f, Value(0f), Slope(0f), Slope(0f)),
                new Keyframe(0.5f, Value(0.5f), Slope(0.5f), Slope(0.5f)),
                new Keyframe(1f, Value(1f), Slope(1f), Slope(1f)));
        }

        /// <summary>Rebuilds <see cref="Retention"/> from the selected preset. Custom leaves it alone.</summary>
        public void ApplyPreset()
        {
            switch (Falloff)
            {
                case Preset.Linear: Retention = LinearRetention(); break;
                case Preset.Smooth: Retention = SmoothRetention(); break;
                case Preset.Exponential: Retention = ExponentialRetention(Sharpness); break;
            }
            m_dirty = true;
        }

        // ---- lifecycle ----

        void Reset() => Renderer = ResolveRenderer();

        void OnEnable() => m_dirty = true;

        void OnDisable() => Clear();

        void OnValidate()
        {
            if (Falloff != Preset.Custom)
                ApplyPreset();
            m_dirty = true;
        }

        void LateUpdate()
        {
            var renderer = ResolveRenderer();
            if (!renderer || !renderer.GsplatAsset)
                return;

            // A refresh has run on this asset, whether or not it thinned anything. One that kept every splat must
            // still be redone when the centre moves, or the splats never thin once it does.
            bool evaluated = m_appliedAsset != null;
            if (evaluated && (renderer.GsplatAsset != m_appliedAsset || (m_applied && !renderer.HasActiveRanges)))
            {
                // The renderer reloaded its asset or someone else cleared the ranges.
                m_applied = false;
                m_dirty = true;
            }

            var center = Center ? Center : transform;
            bool moved = (center.position - m_appliedCenter).sqrMagnitude > RefreshDistance * RefreshDistance ||
                         MatrixMoved(renderer.transform.localToWorldMatrix);
            if (!m_dirty && !(moved && evaluated))
                return;
            if (Time.realtimeSinceStartup - m_lastRefreshTime < MinRefreshSeconds && evaluated && !m_dirty)
                return;

            Refresh();
        }

        bool MatrixMoved(Matrix4x4 now)
        {
            float d = 0f;
            for (int i = 0; i < 16; i++)
                d += Mathf.Abs(now[i] - m_appliedMatrix[i]);
            return d > 1e-3f + RefreshDistance;
        }

        /// <summary>The renderer this acts on: the assigned one, else this object's, a parent's or a child's.</summary>
        public GsplatRenderer ResolveRenderer()
        {
            if (Renderer)
                return Renderer;
            if (TryGetComponent(out GsplatRenderer here))
                return here;
            var found = GetComponentInParent<GsplatRenderer>();
            return found ? found : GetComponentInChildren<GsplatRenderer>();
        }

        /// <summary>Stops thinning: the renderer goes back to drawing everything its cutouts allow.</summary>
        public void Clear()
        {
            var renderer = ResolveRenderer();
            if (m_applied && renderer)
                renderer.ClearActiveRanges();
            m_applied = false;
            m_appliedAsset = null;
            KeptCount = TotalCount;
        }

        /// <summary>Recomputes the selection now and hands it to the renderer.</summary>
        public void Refresh()
        {
            m_dirty = false;
            m_lastRefreshTime = Time.realtimeSinceStartup;

            var renderer = ResolveRenderer();
            var asset = renderer ? renderer.GsplatAsset : null;
            if (!asset || asset.SplatCount == 0)
                return;

            var spark = asset as GsplatAssetSpark;
            var raw = asset as GsplatAssetUncompressed;
            if ((spark == null || spark.PackedSplats == null) && (raw == null || raw.Positions == null))
            {
                Debug.LogWarning($"[SplatLod] '{asset.name}' keeps no CPU-side splat data; cannot thin it.", this);
                return;
            }

            var toWorld = renderer.transform.localToWorldMatrix;
            var center = (Center ? Center : transform).position;
            m_appliedMatrix = toWorld;
            m_appliedCenter = center;
            m_appliedAsset = asset;
            TotalCount = asset.SplatCount;

            BakeLut();
            float sizeBias = Mathf.Clamp01(FavorLarge * 0.01f) * k_maxSizeBias;
            float opacityBias = Mathf.Clamp01(FavorOpaque * 0.01f) * k_maxOpacityBias;
            float reference = sizeBias > 0f ? ReferenceScale(asset) : 1f;
            float invRadius = 1f / Mathf.Max(Radius, 0.01f);
            float density = Mathf.Clamp01(Density);

            m_ranges.Clear();
            uint runStart = 0, runLength = 0, kept = 0;
            uint count = asset.SplatCount;
            for (uint i = 0; i < count; i++)
            {
                float scale = 0f, opacity = 1f;
                Vector3 position;
                if (spark != null)
                {
                    var word = spark.PackedSplats[i];
                    position = new Vector3(HalfToFloat(word.y & 0xFFFFu), HalfToFloat(word.y >> 16),
                        HalfToFloat(word.z & 0xFFFFu));
                    if (sizeBias > 0f)
                        scale = SparkScale(word.w);
                    opacity = (word.x >> 24) / 255f;
                }
                else
                {
                    position = raw.Positions[i];
                    if (sizeBias > 0f)
                    {
                        var s = raw.Scales[i];
                        scale = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                    }
                    opacity = raw.Colors[i].w;
                }

                float distance = (toWorld.MultiplyPoint3x4(position) - center).magnitude * invRadius;
                bool keep = Keep(distance, i, scale, opacity, density, sizeBias, opacityBias, reference);
                if (keep)
                {
                    if (runLength == 0)
                        runStart = i;
                    runLength++;
                    kept++;
                }
                else if (runLength > 0)
                {
                    m_ranges.Add(new GsplatActiveRange(runStart, runLength));
                    runLength = 0;
                }
            }

            if (runLength > 0)
                m_ranges.Add(new GsplatActiveRange(runStart, runLength));

            KeptCount = kept;
            if (kept == count)
            {
                // Nothing thinned: leave the renderer on its dense path, where cutouts and global
                // sorting work as usual.
                if (m_applied)
                    renderer.ClearActiveRanges();
                m_applied = false;
            }
            else
            {
                renderer.SetActiveRanges(m_ranges);
                m_applied = true;
            }

            Refreshed?.Invoke();
        }

        void BakeLut()
        {
            bool usable = Retention != null && Retention.length > 0;
            for (int i = 0; i < k_lutSize; i++)
                m_lut[i] = usable ? Mathf.Clamp01(Retention.Evaluate(i / (float)(k_lutSize - 1))) : 1f;
        }

        float SampleLut(float t)
        {
            float s = Mathf.Clamp01(t) * (k_lutSize - 1);
            int i0 = (int)s;
            int i1 = Mathf.Min(i0 + 1, k_lutSize - 1);
            return Mathf.Lerp(m_lut[i0], m_lut[i1], s - i0);
        }

        bool Keep(float distance, uint index, float scale, float opacity, float density,
                  float sizeBias, float opacityBias, float referenceScale)
        {
            float retention = Mathf.Clamp01(SampleLut(distance) * density);
            float weight = 1f;
            if (sizeBias > 0f)
                weight *= Mathf.Pow(Mathf.Max(scale, 1e-8f) / Mathf.Max(referenceScale, 1e-8f), sizeBias);
            if (opacityBias > 0f)
                weight *= Mathf.Pow(Mathf.Clamp01(opacity) + 1e-6f, opacityBias);

            float keep = Mathf.Pow(retention, 1f / Mathf.Max(weight, 1e-6f));
            return Hash(unchecked(index * 0x9E3779B9u)) * (1.0 / 4294967296.0) < keep;
        }

        // lowbias32 (Chris Wellons). Good avalanche for sequential inputs, which matters because
        // the input here is the splat index.
        static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
            }
            return x;
        }

        static float SparkScale(uint word3)
        {
            // Word 3 holds the three quantised log scales. exp is monotonic, so taking the max
            // byte first and expanding once is exact.
            uint max = Math.Max(word3 & 0xFFu, Math.Max((word3 >> 8) & 0xFFu, (word3 >> 16) & 0xFFu));
            return max == 0u
                ? 0f
                : Mathf.Exp(k_lnScaleMin + (max - 1u) * ((k_lnScaleMax - k_lnScaleMin) / 254f));
        }

        // IEEE half to float, for the packed Spark positions.
        static float HalfToFloat(uint half)
        {
            uint sign = (half >> 15) & 1u;
            int exponent = (int)((half >> 10) & 0x1Fu);
            uint mantissa = half & 0x3FFu;
            float value;
            if (exponent == 0)
                value = mantissa * (1f / 16777216f); // subnormal: m * 2^-24
            else if (exponent == 31)
                value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
            else
                value = (1f + mantissa / 1024f) * Mathf.Pow(2f, exponent - 15);
            return sign == 1u ? -value : value;
        }

        /// <summary>
        /// Median of the largest per-splat axis, sampled rather than exhaustive. Size bias divides
        /// by this so it means the same thing on a room scan and on a landscape.
        /// </summary>
        float ReferenceScale(GsplatAsset asset)
        {
            if (m_referenceAsset == asset && m_referenceScale > 0f)
                return m_referenceScale;

            var samples = new List<float>();
            if (asset is GsplatAssetSpark spark && spark.PackedSplats != null)
            {
                int stride = Math.Max(1, spark.PackedSplats.Length / 50000);
                for (int i = 0; i < spark.PackedSplats.Length; i += stride)
                {
                    float s = SparkScale(spark.PackedSplats[i].w);
                    if (s > 0f)
                        samples.Add(s);
                }
            }
            else if (asset is GsplatAssetUncompressed raw && raw.Scales != null)
            {
                int stride = Math.Max(1, raw.Scales.Length / 50000);
                for (int i = 0; i < raw.Scales.Length; i += stride)
                {
                    var s = raw.Scales[i];
                    float largest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                    if (largest > 0f)
                        samples.Add(largest);
                }
            }

            samples.Sort();
            m_referenceAsset = asset;
            m_referenceScale = samples.Count > 0 ? samples[samples.Count / 2] : 1e-6f;
            return m_referenceScale;
        }

        void OnDrawGizmosSelected()
        {
            var center = (Center ? Center : transform).position;
            Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
            Gizmos.DrawWireSphere(center, Radius);

            // Rings where retention has fallen to 75/50/25%: a single sphere says nothing about a
            // falloff, and these make the shape of the curve legible without opening the editor.
            if (Retention == null || Retention.length == 0)
                return;
            Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.4f);
            foreach (float level in new[] { 0.75f, 0.5f, 0.25f })
                for (int step = 0; step <= 64; step++)
                {
                    float t = step / 64f;
                    if (Mathf.Clamp01(Retention.Evaluate(t)) * Mathf.Clamp01(Density) <= level)
                    {
                        Gizmos.DrawWireSphere(center, Radius * t);
                        break;
                    }
                }
        }
    }
}
