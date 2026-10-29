using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Serialization;

namespace StageSettAR
{
    /// <summary>
    /// A STAR map exported by star-mobile: the landmark network and the landmarks it detects.
    /// Created by the import dialog (Tools ▸ StageSettAR ▸ Import STAR Map).
    /// </summary>
    /// <remarks>
    /// Landmarks are stored verbatim in the export/training frame (the WebXR capture world:
    /// right-handed, y up, metres), which is what PnP works in. Unity map-frame positions,
    /// (x, y, -z) of those, come from <see cref="LandmarkUnity"/> / <see cref="LandmarksUnity"/>;
    /// see <see cref="Conventions"/>. <see cref="topdownBounds"/> is already a Unity-frame rectangle.
    /// </remarks>
    public sealed class StarMap : ScriptableObject
    {
        [Header("Identity")]
        public string mapId;
        public string displayName;
        public string created;

        [Header("Network")]
        public ModelAsset model;

        [Tooltip("Suggested network input size in pixels, used only for warm-up. The network takes any size that is a multiple of 16; the detector picks the size per camera frame.")]
        [FormerlySerializedAs("inputWidth")] public int suggestedWidth = 640;
        [FormerlySerializedAs("inputHeight")] public int suggestedHeight = 352;

        [Tooltip("Output cell size in input pixels.")]
        public int cellSize = 8;

        [Tooltip("Training focal length in input pixels.")]
        public float focal;

        [Tooltip("Clockwise quarter turns from the upright camera image that the map was trained on.")]
        public int[] rotations = { 0 };

        [Tooltip("Quarter turns used when the gravity-chosen turn is not in Rotations.")]
        public int defaultRotation;

        [Header("Landmarks")]
        [Tooltip("Landmark positions in the export/training frame (WebXR capture world, right-handed, y up, metres), as in landmarks.json. Index = network class.")]
        public Vector3[] landmarksExport;

        [Header("Floor map (optional)")]
        public Texture2D topdown;

        [Tooltip("Map-frame (Unity) XZ rectangle covered by Topdown.")]
        public Rect topdownBounds;
        public float topdownY;

        [Header("Holdout quality (offline, informational)")]
        public float recall5cm5deg;
        public float recall10cm10deg;
        public float medianCm;
        public float medianDeg;

        [Header("Source")]
        [TextArea(3, 12)] public string mapJson;

        [System.NonSerialized] Vector3[] _landmarksUnity;
        [System.NonSerialized] Vector3[] _landmarksUnitySource;

        void OnValidate() => _landmarksUnity = null;

        public int LandmarkCount => landmarksExport?.Length ?? 0;

        /// <summary>Landmark positions in the Unity map frame, built lazily from <see cref="landmarksExport"/>. Do not modify.</summary>
        public Vector3[] LandmarksUnity
        {
            get
            {
                if (_landmarksUnity == null || !ReferenceEquals(_landmarksUnitySource, landmarksExport) || _landmarksUnity.Length != LandmarkCount)
                {
                    _landmarksUnity = new Vector3[LandmarkCount];
                    _landmarksUnitySource = landmarksExport;
                    for (int i = 0; i < _landmarksUnity.Length; i++) _landmarksUnity[i] = Conventions.ExportToUnity(landmarksExport[i]);
                }
                return _landmarksUnity;
            }
        }

        /// <summary>Landmark <paramref name="i"/> in the Unity map frame.</summary>
        public Vector3 LandmarkUnity(int i) => LandmarksUnity[i];
    }
}
