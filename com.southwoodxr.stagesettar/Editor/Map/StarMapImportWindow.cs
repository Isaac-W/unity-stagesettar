#pragma warning disable CS0649 // fields are filled by Unity serialization / JsonUtility
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Gsplat;
using Gsplat.Editor;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Imports a star-mobile map zip into the project (model, splat, floor image, a <see cref="StarMap"/>
    /// asset and a ready-to-use prefab), and optionally drops it into the open scene. The splat is
    /// only imported once the Setup steps it depends on are done (see <see cref="SplatSetupReady"/>).
    /// </summary>
    public sealed class StarMapImportWindow : EditorWindow
    {
        // The only zip entries ever read; anything else (including path-traversal names) is ignored.
        static readonly string[] k_entries =
            { "map.json", "landmarks.json", "topdown.json", "topdown.png", "model.onnx", "splat.spz" };

        [Serializable] class InputDto { public int width, height, rotation; public int[] rotations; }
        // "variableSize" and "sizeMultiple" restate the model's input; width and height here are only a suggested size.
        [Serializable] class OnnxDto { public bool variableSize; public int sizeMultiple, width, height; }
        [Serializable] class HoldoutDto { public int frames; public float recall5cm5deg, recall10cm10deg, medianCm, medianDeg; }
        [Serializable] class MapDto
        {
            public string format, id, name, created;
            public int version, landmarks;
            public float focal;
            public InputDto input;
            public OnnxDto onnx;
            public HoldoutDto holdout;
        }
        [Serializable] class TopdownDto { public float minX, minZ, maxX, maxZ, y; }

        string m_zip, m_mapText, m_dest;
        MapDto m_map;
        bool m_hasSplat, m_hasHoldout, m_importSplat = true, m_addToScene = true;
        Gsplat.CompressionMode m_compression = Gsplat.CompressionMode.Spark;
        string m_error;

        [MenuItem("Tools/StageSettAR/Import STAR Map…")]
        static void Open() => GetWindow<StarMapImportWindow>("Import STAR Map");

        // The Setup steps a splat import depends on. Fixing them is the Setup window's job, not the importer's.
        static readonly ISetupStep[] k_splatSteps = { new SplatDependencyStep(), new UrpSplatStep() };

        static bool SplatSetupReady(out string message)
        {
            var lines = new List<string>();
            foreach (var step in k_splatSteps)
                if (step.GetStatus(out var detail) != SetupStatus.Done) lines.Add(step.Title + ": " + detail);
            message = string.Join("\n", lines);
            return lines.Count == 0;
        }

        void OnFocus() => Repaint();

        void OnGUI()
        {
            var setupReady = SplatSetupReady(out var setupMessage);
            if (!setupReady)
            {
                EditorGUILayout.HelpBox("Splats will not be imported or rendered until project setup is finished.\n" + setupMessage, MessageType.Warning);
                if (GUILayout.Button("Open Setup", GUILayout.Height(24))) SetupWindow.Open();
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.SelectableLabel(m_zip ?? "No file selected", GUILayout.Height(18));
            if (GUILayout.Button("Choose .zip…", GUILayout.Width(100)))
            {
                var path = EditorUtility.OpenFilePanel("STAR map zip", "", "zip");
                if (!string.IsNullOrEmpty(path)) Load(path);
            }
            EditorGUILayout.EndHorizontal();

            if (m_error != null) EditorGUILayout.HelpBox(m_error, MessageType.Error);
            if (m_map == null) return;

            var rot = m_map.input.rotations is { Length: > 0 } ? string.Join(",", m_map.input.rotations) : m_map.input.rotation.ToString();
            var (sw, sh) = SuggestedSize(m_map);
            EditorGUILayout.HelpBox(string.Format(CultureInfo.InvariantCulture,
                "{0}\nLandmarks: {1}\nInput: any size, multiples of 16 (suggested {2})   Rotations: {3}   Focal: {4:F1}px\nHoldout: {5}\nSplat: {6}",
                m_map.name, m_map.landmarks, sw > 0 ? sw + "x" + sh : "none", rot, m_map.focal,
                m_hasHoldout ? $"{m_map.holdout.recall5cm5deg:F1}% within 5cm/5deg, {m_map.holdout.recall10cm10deg:F1}% within 10cm/10deg, median {m_map.holdout.medianCm:F1}cm {m_map.holdout.medianDeg:F2}deg" : "none",
                m_hasSplat ? "present" : "absent"), MessageType.Info);

            m_dest = EditorGUILayout.TextField("Destination folder", m_dest);
            var splatOk = m_hasSplat && setupReady;
            using (new EditorGUI.DisabledScope(!splatOk))
            {
                var importSplat = EditorGUILayout.Toggle("Import splat", m_importSplat && splatOk);
                if (splatOk) m_importSplat = importSplat;
                m_compression = (Gsplat.CompressionMode)EditorGUILayout.EnumPopup("Splat compression", m_compression);
            }
            m_addToScene = EditorGUILayout.Toggle("Add to open scene", m_addToScene);

            if (GUILayout.Button("Import", GUILayout.Height(28)))
            {
                try { Import(); }
                catch (Exception e) { m_error = e.Message; Debug.LogException(e); }
                finally { EditorUtility.ClearProgressBar(); }
            }
        }

        void Load(string path)
        {
            m_zip = path; m_map = null; m_error = null;
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var files = ReadEntries(zip, "map.json");
                if (!files.TryGetValue("map.json", out var bytes)) throw new InvalidDataException("map.json missing");
                m_mapText = Encoding.UTF8.GetString(bytes);
                m_map = JsonUtility.FromJson<MapDto>(m_mapText);
                if (m_map == null) throw new InvalidDataException("map.json is empty");
                m_map.input ??= new InputDto();
                m_map.onnx ??= new OnnxDto();
                m_hasHoldout = m_map.holdout != null && m_map.holdout.frames > 0 && m_mapText.Contains("\"holdout\"");
                m_hasSplat = zip.GetEntry("splat.spz") != null;
                m_importSplat = m_hasSplat;
                m_dest = "Assets/StarMaps/" + Sanitize(m_map.name);
            }
            catch (Exception e) { m_map = null; m_error = e.Message; }
        }

        static string Sanitize(string s)
        {
            // Letters and digits of any script, so two maps named in a non-Latin script do not both become "___".
            var t = Regex.Replace(s ?? "", @"[^\p{L}\p{N} _-]", "_").Trim();
            return t.Length == 0 ? "StarMap" : t;
        }

        static Dictionary<string, byte[]> ReadEntries(ZipArchive zip, params string[] only)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (var e in zip.Entries)
            {
                if (Array.IndexOf(only.Length > 0 ? only : k_entries, e.FullName) < 0) continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                result[e.FullName] = ms.ToArray();
            }
            return result;
        }

        void Import()
        {
            m_error = null;
            if (m_dest == null || !m_dest.StartsWith("Assets/", StringComparison.Ordinal) || m_dest.Contains(".."))
                throw new InvalidDataException("Destination must be a folder under Assets/");
            Dictionary<string, byte[]> files;
            using (var zip = ZipFile.OpenRead(m_zip)) files = ReadEntries(zip);

            var map = m_map;
            if (map.format != "starmap") throw new InvalidDataException($"Not a STAR map (format '{map.format}')");
            if (map.version > 1) throw new InvalidDataException($"Unsupported map version {map.version}");
            // map.json "origin", if present, is ignored: the landmarks already sit in the map's own frame.
            if (!m_mapText.Contains("\"rotation\"") && !m_mapText.Contains("\"rotations\""))
                throw new InvalidDataException("map.json has neither input.rotation nor input.rotations");
            // JsonUtility cannot tell a missing bool from false, so check the text for the key.
            if (m_mapText.Contains("\"variableSize\"") && !map.onnx.variableSize)
                throw new InvalidDataException("This map's network has a fixed input size, which is not supported.");
            if (map.onnx.sizeMultiple > 0 && map.onnx.sizeMultiple % 16 != 0)
                throw new InvalidDataException($"Network size multiple {map.onnx.sizeMultiple} is not a multiple of 16");
            var (sw, sh) = SuggestedSize(map);
            if (sw % 16 != 0 || sh % 16 != 0)
                throw new InvalidDataException($"Suggested input size {sw}x{sh} is not a multiple of 16");
            if (!files.TryGetValue("landmarks.json", out var lmBytes) || !files.ContainsKey("model.onnx"))
                throw new InvalidDataException("Zip needs model.onnx and landmarks.json");
            var landmarks = ParseLandmarks(Encoding.UTF8.GetString(lmBytes));
            if (landmarks.Length != map.landmarks)
                throw new InvalidDataException($"landmarks.json has {landmarks.Length} landmarks, map.json says {map.landmarks}");

            var name = Sanitize(map.name);
            // Every import is a new environment: an existing folder is never written into, so maps imported
            // earlier (and anything authored under them) are left exactly as they are.
            var dest = m_dest.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(dest)) dest = AssetDatabase.GenerateUniqueAssetPath(dest);
            EditorUtility.DisplayProgressBar("Importing STAR map", "Writing files", 0.1f);

            var modelPath = Write(dest + "/model.onnx", files["model.onnx"]);
            var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(modelPath);
            if (!model) throw new InvalidDataException("Unity.InferenceEngine did not import model.onnx as a ModelAsset");

            GsplatAsset splat = null;
            var importSplat = m_importSplat;
            if (importSplat && !SplatSetupReady(out _))
            {
                importSplat = false;
                Debug.LogWarning("[StageSettAR] Splat not imported: finish the steps in Tools > StageSettAR > Setup first, then import again.");
            }
            if (importSplat && files.TryGetValue("splat.spz", out var spz))
            {
                EditorUtility.DisplayProgressBar("Importing STAR map", "Importing splat", 0.4f);
                var spzPath = Write(dest + "/splat.spz", spz);
                // The export's world is WebXR (right-handed, y up); SPZ from Spark is RUB in that same world.
                // GsplatImporter converts RUB -> Unity by negating z, which equals the landmark mapping
                // (x, y, -z), so the splat sits at identity under the map root. Auto already means RUB for
                // SPZ; only an explicitly changed importer is reset.
                if (AssetImporter.GetAtPath(spzPath) is GsplatImporter imp &&
                    (imp.Compression != m_compression ||
                     imp.SourceCoordinates is not (SourceCoordinates.Unspecified or SourceCoordinates.RUB)))
                {
                    imp.Compression = m_compression;
                    imp.SourceCoordinates = SourceCoordinates.RUB;
                    imp.SaveAndReimport();
                }
                splat = AssetDatabase.LoadAssetAtPath<GsplatAsset>(spzPath);
            }

            Texture2D topdown = null;
            var td = new TopdownDto();
            if (files.TryGetValue("topdown.png", out var png) && files.TryGetValue("topdown.json", out var tdJson))
            {
                td = JsonUtility.FromJson<TopdownDto>(Encoding.UTF8.GetString(tdJson));
                var pngPath = Write(dest + "/topdown.png", png);
                var ti = (TextureImporter)AssetImporter.GetAtPath(pngPath);
                if (ti.mipmapEnabled || ti.wrapMode != TextureWrapMode.Clamp || ti.npotScale != TextureImporterNPOTScale.None || !ti.alphaIsTransparency)
                {
                    ti.mipmapEnabled = false;
                    ti.wrapMode = TextureWrapMode.Clamp;
                    ti.npotScale = TextureImporterNPOTScale.None;
                    ti.alphaIsTransparency = true;
                    ti.SaveAndReimport();
                }
                topdown = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
            }

            EditorUtility.DisplayProgressBar("Importing STAR map", "Creating assets", 0.8f);
            var starMap = CreateOrUpdateMap(dest + "/" + name + ".asset", map, landmarks, model, topdown, td);
            var prefab = BuildPrefab(dest + "/" + name + ".prefab", name, starMap, splat, topdown, td);

            Debug.Log($"[StageSettAR] Imported '{map.name}' ({landmarks.Length} landmarks{(splat ? ", splat" : "")}) to {dest}", starMap);
            if (m_addToScene)
            {
                AddToScene(prefab);
            }
        }

        /// <summary>Writes in place (keeps the .meta / GUID on re-import) and imports.</summary>
        static string Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path);
            return path;
        }

        /// <summary>The map's suggested input size: the onnx default, else the older input block; 0 x 0 if neither gives one.</summary>
        static (int w, int h) SuggestedSize(MapDto m)
        {
            if (m.onnx != null && m.onnx.width > 0 && m.onnx.height > 0) return (m.onnx.width, m.onnx.height);
            if (m.input != null && m.input.width > 0 && m.input.height > 0) return (m.input.width, m.input.height);
            return (0, 0);
        }

        static Vector3[] ParseLandmarks(string json)
        {
            var start = json.IndexOf("\"landmarks\"", StringComparison.Ordinal);
            if (start < 0) throw new InvalidDataException("landmarks.json has no landmarks array");
            // Only the numbers inside the landmarks array: any numeric field after it would break the triplets.
            var open = json.IndexOf('[', start);
            var close = -1;
            for (int i = open, depth = 0; open >= 0 && i < json.Length && close < 0; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']' && --depth == 0) close = i;
            }
            if (close < 0) throw new InvalidDataException("landmarks.json has no complete landmarks array");
            var nums = Regex.Matches(json.Substring(open, close - open + 1), @"-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?");
            if (nums.Count % 3 != 0) throw new InvalidDataException("landmarks.json is not a list of xyz triplets");
            var pts = new Vector3[nums.Count / 3];
            for (var i = 0; i < pts.Length; i++)
            {
                float F(int k) => float.Parse(nums[i * 3 + k].Value, CultureInfo.InvariantCulture);
                pts[i] = new Vector3(F(0), F(1), F(2)); // verbatim: StarMap.landmarksExport is the export frame
            }
            return pts;
        }

        StarMap CreateOrUpdateMap(string path, MapDto m, Vector3[] landmarks, ModelAsset model, Texture2D topdown, TopdownDto td)
        {
            var sm = AssetDatabase.LoadAssetAtPath<StarMap>(path);
            var isNew = !sm;
            if (isNew) sm = CreateInstance<StarMap>();
            sm.mapId = m.id;
            sm.displayName = m.name;
            sm.created = m.created;
            sm.model = model;
            var (sw, sh) = SuggestedSize(m);
            if (sw > 0) { sm.suggestedWidth = sw; sm.suggestedHeight = sh; }
            sm.cellSize = 8;
            sm.focal = m.focal;
            sm.rotations = m.input.rotations is { Length: > 0 } ? m.input.rotations : new[] { m.input.rotation };
            sm.defaultRotation = m.input.rotation;
            sm.landmarksExport = landmarks;
            sm.topdown = topdown;
            // PNG row 0 is the smallest export z = the largest Unity z, so the Unity z range is [-maxZ, -minZ].
            sm.topdownBounds = topdown ? Rect.MinMaxRect(td.minX, -td.maxZ, td.maxX, -td.minZ) : default;
            sm.topdownY = td.y;
            var h = m_hasHoldout ? m.holdout : new HoldoutDto();
            sm.recall5cm5deg = h.recall5cm5deg;
            sm.recall10cm10deg = h.recall10cm10deg;
            sm.medianCm = h.medianCm;
            sm.medianDeg = h.medianDeg;
            sm.mapJson = m_mapText;
            if (isNew) AssetDatabase.CreateAsset(sm, path);
            else EditorUtility.SetDirty(sm);
            AssetDatabase.SaveAssets();
            return sm;
        }

        /// <summary>Creates the map's prefab. Always a new one: <see cref="Import"/> writes each import to its own folder.</summary>
        /// <remarks>
        /// The splat and floor image are references for placing content and are tagged EditorOnly, so builds leave
        /// them out; a developer who wants the splat on device untags it and adds effects themselves. They and the
        /// floor collider are locked (<see cref="LockedObjects"/>): fixed to the map root, which the localizer moves
        /// into the room, and never picked in the Scene view, so clicks reach the Content placed over them.
        /// </remarks>
        static GameObject BuildPrefab(string path, string name, StarMap map, GsplatAsset splat, Texture2D topdown, TopdownDto td)
        {
            var root = new GameObject("STAR Map " + map.displayName);
            try
            {
                var loc = root.AddComponent<StarLocalizer>();
                loc.map = map;
                loc.mapRoot = root.transform;

                if (splat)
                {
                    var child = Child(root, "Splat");
                    child.tag = "EditorOnly";
                    var r = child.AddComponent<GsplatRenderer>();
                    // Through SerializedObject so GsplatRenderer.OnValidate records the asset GUID (reimport refresh).
                    var so = new SerializedObject(r);
                    so.FindProperty("GsplatAsset").objectReferenceValue = splat;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    LockedObjects.Lock(child);
                }

                Child(root, "Content");

                if (topdown)
                {
                    var b = map.topdownBounds;
                    var floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    floor.name = "Floor";
                    floor.tag = "EditorOnly";
                    UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());
                    floor.transform.SetParent(root.transform, false);
                    // Quad lying flat: local y -> +z, so uv v follows Unity z, matching the PNG orientation.
                    floor.transform.localRotation = Quaternion.Euler(90, 0, 0);
                    floor.transform.localPosition = new Vector3(b.center.x, map.topdownY + 0.005f, b.center.y);
                    floor.transform.localScale = new Vector3(b.width, b.height, 1);
                    floor.GetComponent<MeshRenderer>().sharedMaterial = FloorMaterial(path.Replace(".prefab", "_Floor.mat"), topdown);
                    LockedObjects.Lock(floor);

                    // Invisible ground the size of the room, so dropped and thrown objects land on the real
                    // floor. Kept in builds; delete it if the experience does not want it.
                    const float thickness = 0.1f;
                    var ground = Child(root, "Floor Collider");
                    ground.transform.localPosition = new Vector3(b.center.x, map.topdownY - thickness * 0.5f, b.center.y);
                    ground.AddComponent<BoxCollider>().size = new Vector3(b.width, thickness, b.height);
                    LockedObjects.Lock(ground);
                }
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static GameObject Child(GameObject root, string name)
        {
            var t = root.transform.Find(name);
            if (t) return t.gameObject;
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            return go;
        }

        static Material FloorMaterial(string path, Texture2D tex)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                // Sprites/Default is unlit, alpha-blended, double-sided and works in every pipeline.
                mat = new Material(Shader.Find("Sprites/Default"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.mainTexture = tex;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static GameObject AddToScene(GameObject prefab)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(go, "Add STAR Map");
            var loc = go.GetComponent<StarLocalizer>();

            var mgrType = Type.GetType("UnityEngine.XR.ARFoundation.ARCameraManager, Unity.XR.ARFoundation");
            var srcType = Type.GetType("StageSettAR.ArFoundationCameraSource, StageSettAR.ArFoundation");
            var mgr = mgrType != null ? FindFirstObjectByType(mgrType) as Component : null;
            if (mgr && srcType != null)
            {
                var src = mgr.GetComponent(srcType) ?? Undo.AddComponent(mgr.gameObject, srcType);
                Undo.RecordObject(loc, "Assign camera source");
                loc.cameraSource = (CameraSource)src;
                PrefabUtility.RecordPrefabInstancePropertyModifications(loc);
            }
            else
                Debug.LogWarning("[StageSettAR] No AR Foundation ARCameraManager in the open scene (or AR Foundation missing); assign StarLocalizer.cameraSource manually.", go);

            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            return go;
        }
    }
}
