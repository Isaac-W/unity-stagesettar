using System;
using System.Collections.Generic;
using UnityEngine;

namespace StageSettAR
{
    /// <summary>
    /// Pinhole intrinsics in the OpenCV pixel frame (origin at the top-left of the image,
    /// x right, y down) for one specific resolution.
    /// </summary>
    [System.Serializable]
    public struct CameraIntrinsics
    {
        public float fx, fy, cx, cy;
        public int width, height;

        public CameraIntrinsics(float fx, float fy, float cx, float cy, int width, int height)
        {
            this.fx = fx; this.fy = fy; this.cx = cx; this.cy = cy;
            this.width = width; this.height = height;
        }

        public bool IsValid => fx > 0f && fy > 0f && width > 0 && height > 0;

        /// <summary>The same camera at another resolution (the whole sensor area, rescaled).</summary>
        public CameraIntrinsics Scaled(int w, int h)
        {
            float sx = (float)w / width, sy = (float)h / height;
            return new CameraIntrinsics(fx * sx, fy * sy, cx * sx, cy * sy, w, h);
        }

        public override string ToString() => $"f=({fx:F1},{fy:F1}) c=({cx:F1},{cy:F1}) {width}x{height}";
    }

    /// <summary>Hand-entered intrinsics, with the resolution they were measured at.</summary>
    /// <remarks>
    /// Intrinsics are resolution-dependent, so recording the calibration resolution lets a
    /// source rescale them when the camera opens at another size.
    /// </remarks>
    [System.Serializable]
    public struct ManualIntrinsics
    {
        [Tooltip("Use these values instead of whatever the camera reports.")]
        public bool use;

        [Tooltip("Focal length in pixels, at the resolution below.")]
        public float fx;
        public float fy;

        [Tooltip("Principal point in pixels. Zero means the image centre.")]
        public float cx;
        public float cy;

        [Tooltip("Resolution these were calibrated at. Zero means the camera's actual resolution.")]
        public Vector2Int measuredAt;

        public bool TryResolve(int width, int height, out CameraIntrinsics k)
        {
            k = default;
            if (!use || fx <= 0f || fy <= 0f) return false;

            int rw = measuredAt.x > 0 ? measuredAt.x : width;
            int rh = measuredAt.y > 0 ? measuredAt.y : height;
            float pcx = cx > 0f ? cx : rw * 0.5f;
            float pcy = cy > 0f ? cy : rh * 0.5f;

            k = new CameraIntrinsics(fx, fy, pcx, pcy, rw, rh);
            if (rw != width || rh != height) k = k.Scaled(width, height);
            return k.IsValid;
        }
    }

    /// <summary>How the pixels of a <see cref="CameraFrame"/> are stored.</summary>
    public enum CameraImageFormat
    {
        /// <summary>Colour in <see cref="CameraFrame.texture"/>, nothing else.</summary>
        Rgb = 0,
        /// <summary>
        /// YUV 4:2:0 as camera hardware delivers it: luma in <see cref="CameraFrame.texture"/> (R8), Cb and Cr
        /// interleaved in <see cref="CameraFrame.chroma"/> (RG8, half resolution; r = Cb, g = Cr).
        /// BT.601 full range, converted to RGB by the preprocess shader.
        /// </summary>
        YuvSemiPlanar,
        /// <summary>
        /// YUV 4:2:0 with separate chroma planes: luma in <see cref="CameraFrame.texture"/>, Cb in
        /// <see cref="CameraFrame.chroma"/>, Cr in <see cref="CameraFrame.chromaV"/> (all R8). The chroma
        /// textures hold raw rows: the sample for chroma column i is texel i * <see cref="CameraFrame.chromaPixelStride"/>.
        /// </summary>
        YuvPlanar,
    }

    /// <summary>One camera image and everything needed to localize it.</summary>
    public struct CameraFrame
    {
        /// <summary>The image. Owned by the source and valid until the next <see cref="CameraSource.TryGetFrame"/>.</summary>
        public Texture texture;

        /// <summary>How <see cref="texture"/> (and the chroma textures) are to be read. Default <see cref="CameraImageFormat.Rgb"/>.</summary>
        public CameraImageFormat format;

        /// <summary>YUV only: chroma texture (Cb, or Cb and Cr interleaved); see <see cref="CameraImageFormat"/>.</summary>
        public Texture chroma;

        /// <summary>YUV planar only: the Cr texture.</summary>
        public Texture chromaV;

        /// <summary>YUV planar only: texels between neighbouring chroma samples in a row (1 or 2).</summary>
        public int chromaPixelStride;

        /// <summary>
        /// Image size in pixels. Zero means the size of <see cref="texture"/>; YUV textures may be wider than the
        /// image because they keep the row padding.
        /// </summary>
        public int width, height;

        public int ImageWidth => width > 0 ? width : texture.width;
        public int ImageHeight => height > 0 ? height : texture.height;

        /// <summary>True when row 0 of <see cref="texture"/> is the bottom row of the image.</summary>
        public bool flipY;

        /// <summary>Intrinsics at the texture's resolution, in the OpenCV pixel frame of the upright image.</summary>
        public CameraIntrinsics intrinsics;

        /// <summary>Strictly increasing per new frame.</summary>
        public long id;

        /// <summary>Capture time in seconds on the source's clock. Informational.</summary>
        public double timestamp;

        /// <summary>
        /// Pose of the camera sensor (not the head) at the moment of capture, relative to
        /// <see cref="CameraSource.TrackingSpace"/>. Unity convention: +z is the optical axis,
        /// +y is image up.
        /// </summary>
        public Pose cameraPose;

        /// <summary>False when the source cannot say where the camera was.</summary>
        public bool hasPose;
    }

    /// <summary>Supplies camera frames to <see cref="StarLocalizer"/>.</summary>
    /// <remarks>
    /// Implementations are responsible for three things that are easy to get wrong and that
    /// each turn into pose error: intrinsics that match the delivered texture's resolution,
    /// the image orientation (<see cref="CameraFrame.flipY"/>), and the camera pose at
    /// capture time rather than at the time the frame is read.
    /// </remarks>
    public abstract class CameraSource : MonoBehaviour
    {
        /// <summary>True once the camera is open and delivering frames.</summary>
        public abstract bool IsReady { get; }

        /// <summary>
        /// The transform that <see cref="CameraFrame.cameraPose"/> is expressed in, usually the
        /// XR Origin. Null means world space. Must have +y up.
        /// </summary>
        public virtual Transform TrackingSpace => null;

        /// <summary>
        /// The focal length (in pixels) the landmark network was trained at, set by <see cref="StarLocalizer"/>.
        /// A source may use it to deliver no more pixels than the network needs. Zero means unknown.
        /// </summary>
        public float TargetFocal { get; set; }

        /// <summary>
        /// Width that preserves what the network can use: the image scaled so its focal length becomes
        /// <paramref name="targetFocal"/>, rounded up to a multiple of 8 and never larger than the image.
        /// Without a usable focal length or target the image width is returned.
        /// </summary>
        public static int AutoWidth(int imageWidth, float fx, float targetFocal)
        {
            if (!(fx > 0f) || !(targetFocal > 0f)) return imageWidth;
            int w = Mathf.CeilToInt(imageWidth * targetFocal / fx);
            w = (w + 7) / 8 * 8;
            return Mathf.Clamp(w, 1, imageWidth);
        }

        /// <summary>
        /// Index of the entry of <paramref name="sizes"/> to use for a requested resolution, or -1 when the list is empty.
        /// A request with a zero side means the largest. Otherwise sizes with the requested aspect ratio (within 2%)
        /// are preferred, and among the candidates the one whose pixel count is closest to the request wins.
        /// </summary>
        public static int ClosestResolution(IReadOnlyList<Vector2Int> sizes, Vector2Int requested)
        {
            int best = -1;
            bool any = requested.x > 0 && requested.y > 0;
            long target = (long)requested.x * requested.y;
            float aspect = any ? (float)requested.x / requested.y : 0f;

            // Two passes: first only sizes with the requested aspect, then (if there were none) all of them.
            for (int pass = 0; pass < 2 && best < 0; pass++)
            {
                for (int i = 0; i < sizes.Count; i++)
                {
                    long area = (long)sizes[i].x * sizes[i].y;
                    if (area <= 0) continue;
                    if (any && pass == 0 && Mathf.Abs((float)sizes[i].x / sizes[i].y / aspect - 1f) > 0.02f) continue;
                    if (best < 0) { best = i; continue; }
                    long bestArea = (long)sizes[best].x * sizes[best].y;
                    long d = any ? Math.Abs(area - target) : -area;
                    long bestD = any ? Math.Abs(bestArea - target) : -bestArea;
                    if (d < bestD || (d == bestD && area > bestArea)) best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// The camera's current tracked pose, relative to <see cref="TrackingSpace"/> and in the same convention as
        /// <see cref="CameraFrame.cameraPose"/>. Cheap and available every frame, without acquiring an image, so the
        /// localizer can watch for tracking resets and movement while it is resting. False (the default) when unknown.
        /// </summary>
        public virtual bool TryGetCurrentPose(out Pose pose)
        {
            pose = Pose.identity;
            return false;
        }

        /// <summary>Fetch the newest frame with an id greater than <paramref name="afterId"/>.</summary>
        /// <remarks>
        /// A source may acquire frames only when asked, so a call that returns false can be what makes the next frame
        /// arrive: keep calling while a frame is wanted.
        /// </remarks>
        /// <returns>False when there is no such frame.</returns>
        public abstract bool TryGetFrame(long afterId, out CameraFrame frame);
    }
}
