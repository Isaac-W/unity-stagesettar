using System;
using UnityEngine;

namespace StageSettAR
{
    // Double-precision types for the solvers. Pose math in float loses millimetres at room scale
    // once it goes through RANSAC and least squares, so the solvers stay in double throughout.

    public struct Vector2d
    {
        public double x, y;
        public Vector2d(double x, double y) { this.x = x; this.y = y; }
    }

    public struct Vector3d
    {
        public double x, y, z;
        public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public Vector3d(Vector3 v) { x = v.x; y = v.y; z = v.z; }
        public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);

        public double Length => Math.Sqrt(x * x + y * y + z * z);

        public static double Dot(in Vector3d a, in Vector3d b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3d Cross(in Vector3d a, in Vector3d b) =>
            new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3d operator -(Vector3d a) => new Vector3d(-a.x, -a.y, -a.z);
        public static Vector3d operator *(double s, Vector3d a) => new Vector3d(s * a.x, s * a.y, s * a.z);
    }

    /// <summary>Row-major 3x3 matrix.</summary>
    public struct Matrix3x3d
    {
        public double m00, m01, m02, m10, m11, m12, m20, m21, m22;

        public Matrix3x3d(double m00, double m01, double m02, double m10, double m11, double m12,
                          double m20, double m21, double m22)
        {
            this.m00 = m00; this.m01 = m01; this.m02 = m02;
            this.m10 = m10; this.m11 = m11; this.m12 = m12;
            this.m20 = m20; this.m21 = m21; this.m22 = m22;
        }

        public static Matrix3x3d Identity => new Matrix3x3d(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public Matrix3x3d Transpose() => new Matrix3x3d(m00, m10, m20, m01, m11, m21, m02, m12, m22);

        public double Trace => m00 + m11 + m22;

        public Vector3d Column(int i) => i == 0 ? new Vector3d(m00, m10, m20)
                                       : i == 1 ? new Vector3d(m01, m11, m21)
                                                : new Vector3d(m02, m12, m22);

        public static Matrix3x3d FromColumns(in Vector3d a, in Vector3d b, in Vector3d c) =>
            new Matrix3x3d(a.x, b.x, c.x, a.y, b.y, c.y, a.z, b.z, c.z);

        public static Vector3d operator *(in Matrix3x3d a, Vector3d v) => new Vector3d(
            a.m00 * v.x + a.m01 * v.y + a.m02 * v.z,
            a.m10 * v.x + a.m11 * v.y + a.m12 * v.z,
            a.m20 * v.x + a.m21 * v.y + a.m22 * v.z);

        public static Matrix3x3d operator *(in Matrix3x3d a, in Matrix3x3d b) => new Matrix3x3d(
            a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20, a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21, a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22,
            a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20, a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21, a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22,
            a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20, a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21, a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22);

        /// <summary>Rotation matrix of a unit quaternion (valid in Unity's left-handed frame too).</summary>
        public static Matrix3x3d FromQuaternion(Quaternion q)
        {
            double n = Math.Sqrt((double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z + (double)q.w * q.w);
            double x = q.x / n, y = q.y / n, z = q.z / n, w = q.w / n;
            return new Matrix3x3d(
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y));
        }

        /// <summary>Quaternion of a proper rotation matrix (largest-component branch, stable near 180 degrees).</summary>
        public Quaternion ToQuaternion()
        {
            double x, y, z, w, tr = m00 + m11 + m22;
            if (tr > 0)
            {
                double s = 2 * Math.Sqrt(tr + 1);
                w = 0.25 * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = 2 * Math.Sqrt(1 + m00 - m11 - m22);
                w = (m21 - m12) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                double s = 2 * Math.Sqrt(1 + m11 - m00 - m22);
                w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s;
            }
            else
            {
                double s = 2 * Math.Sqrt(1 + m22 - m00 - m11);
                w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s;
            }
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            return new Quaternion((float)(x / n), (float)(y / n), (float)(z / n), (float)(w / n));
        }

        /// <summary>Exponential map: rotation by |w| radians about w (Rodrigues' formula).</summary>
        public static Matrix3x3d Rodrigues(double wx, double wy, double wz)
        {
            double th2 = wx * wx + wy * wy + wz * wz, th = Math.Sqrt(th2);
            double a, b; // R = I + a [w]x + b [w]x^2
            if (th < 1e-5) { a = 1 - th2 / 6; b = 0.5 - th2 / 24; }
            else { a = Math.Sin(th) / th; b = (1 - Math.Cos(th)) / th2; }
            return new Matrix3x3d(
                1 + b * (-wz * wz - wy * wy), -a * wz + b * wx * wy, a * wy + b * wx * wz,
                a * wz + b * wx * wy, 1 + b * (-wz * wz - wx * wx), -a * wx + b * wy * wz,
                -a * wy + b * wx * wz, a * wx + b * wy * wz, 1 + b * (-wy * wy - wx * wx));
        }

        /// <summary>Rotation about +y by <paramref name="radians"/> in Unity's sense (forward turns towards +x).</summary>
        public static Matrix3x3d RotationY(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Matrix3x3d(c, 0, s, 0, 1, 0, -s, 0, c);
        }
    }

    /// <summary>
    /// The one place that defines how the STAR export, OpenCV and Unity frames relate.
    /// </summary>
    /// <remarks>
    /// The export (and the PnP) works in the WebXR capture world: right-handed, y up, metres.
    /// The Unity map frame is that world mirrored in z. Cameras: the export uses the OpenCV
    /// convention (x right, y down, z forward); Unity cameras look along +z with +y up.
    /// </remarks>
    public static class Conventions
    {
        /// <summary>Export world to Unity map frame: (x, y, -z).</summary>
        public static Vector3 ExportToUnity(Vector3 p) => new Vector3(p.x, p.y, -p.z);

        /// <summary>Unity map frame to export world (the same involution).</summary>
        public static Vector3 UnityToExport(Vector3 p) => new Vector3(p.x, p.y, -p.z);

        /// <summary>
        /// OpenCV camera-to-world pose in the export world (R columns = right, down, forward;
        /// t = camera centre) to the Unity camera pose in the map frame: R_u = M R diag(1,-1,1)
        /// with M = diag(1,1,-1), position (tx, ty, -tz).
        /// </summary>
        public static Pose OpenCvCameraToUnity(in Matrix3x3d R, in Vector3d t)
        {
            var ru = new Matrix3x3d(
                R.m00, -R.m01, R.m02,
                R.m10, -R.m11, R.m12,
                -R.m20, R.m21, -R.m22);
            return new Pose(new Vector3((float)t.x, (float)t.y, (float)-t.z), ru.ToQuaternion());
        }

        /// <summary>Inverse of <see cref="OpenCvCameraToUnity"/>.</summary>
        public static void UnityToOpenCvCamera(Pose p, out Matrix3x3d R, out Vector3d t)
        {
            Matrix3x3d ru = Matrix3x3d.FromQuaternion(p.rotation);
            R = new Matrix3x3d(
                ru.m00, -ru.m01, ru.m02,
                ru.m10, -ru.m11, ru.m12,
                -ru.m20, ru.m21, -ru.m22);
            t = new Vector3d(p.position.x, p.position.y, -p.position.z);
        }

        /// <summary>
        /// The Quest 3 left RGB camera relative to the XR camera (centre eye) transform, in Unity camera-local
        /// axes (x right, y up, z forward), measured on a device: 4.7 cm left, 2.3 cm down, 4.3 cm forward, and
        /// looking 10.9 degrees down (Euler 10.94, 0.20, -0.15).
        /// </summary>
        public static Pose QuestRgbLeftMount => new Pose(
            new Vector3(-0.047f, -0.023f, 0.043f),
            new Quaternion(0.09533f, 0.00187f, -0.00149f, 0.99544f));

        /// <summary>The camera pose for a camera mounted at <paramref name="mount"/> (in head-local axes) on a head at <paramref name="head"/>.</summary>
        public static Pose MountedPose(Pose head, Pose mount) =>
            new Pose(head.position + head.rotation * mount.position, head.rotation * mount.rotation);

        /// <summary>True when two mounts are within <paramref name="maxMeters"/> in position and <paramref name="maxDegrees"/> in rotation.</summary>
        public static bool MountsAgree(Pose a, Pose b, float maxMeters, float maxDegrees) =>
            (a.position - b.position).magnitude <= maxMeters && Quaternion.Angle(a.rotation, b.rotation) <= maxDegrees;

        /// <summary>
        /// M^q with M = [[0,1,0],[-1,0,0],[0,0,1]], the camera-axis change of one clockwise
        /// quarter turn of the image. If the network saw the image turned q times, the
        /// network camera's pose is R_net = R_cam · M^q, so R_cam = R_net · (M^q)^T, and a
        /// vector in network-camera axes maps to physical-camera axes by M^q. This is
        /// star-mobile's rotateCamera.
        /// </summary>
        public static Matrix3x3d QuarterTurnCameraRotation(int q)
        {
            q = ((q % 4) + 4) % 4;
            var m = new Matrix3x3d(0, 1, 0, -1, 0, 0, 0, 0, 1);
            Matrix3x3d r = Matrix3x3d.Identity;
            for (int i = 0; i < q; i++) r = r * m;
            return r;
        }
    }
}
