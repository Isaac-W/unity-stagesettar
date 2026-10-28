using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace StageSettAR.Editor
{
    /// <summary>When a video panel plays.</summary>
    public enum VideoPlayWhen
    {
        /// <summary>While the user looks at the panel; pauses when they look away.</summary>
        WhenGazedAt,

        /// <summary>While the user is within a few metres of it; pauses when they walk away.</summary>
        WhenNearby,

        /// <summary>As soon as the scene starts.</summary>
        OnStart,

        /// <summary>Never by itself: start it from an event (Video Player &gt; Play).</summary>
        Manual,
    }

    /// <summary>
    /// Tools &gt; StageSettAR &gt; Add Video: brings a video file into the project with settings that
    /// play well on Quest, and builds a ready-to-use video panel for it.
    /// </summary>
    public sealed class AddVideoWindow : EditorWindow
    {
        string _sourceFile = "";
        VideoClip _clip;
        VideoPlayWhen _playWhen = VideoPlayWhen.WhenGazedAt;
        float _widthMeters = 1.6f;
        float _nearbyMeters = 2.5f;
        bool _loop = true;

        [MenuItem("Tools/StageSettAR/Add Video...")]
        public static void Open()
        {
            var window = GetWindow<AddVideoWindow>(true, "Add Video", true);
            window.minSize = new Vector2(380f, 330f);
            // A video already selected in the Project window is the one meant.
            if (Selection.activeObject is VideoClip clip) window._clip = clip;
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Pick a video file (mp4, mov, m4v, webm) or drop one here. It is copied into " +
                "Assets/StageSettAR/Videos and set up to play on Quest, then a video panel is added to the scene.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Video file", GUILayout.Width(80f));
                EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(_sourceFile) ? "(none chosen)" : _sourceFile,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Choose...", GUILayout.Width(80f)))
                {
                    string picked = EditorUtility.OpenFilePanel("Choose a video", "", "mp4,mov,m4v,webm,avi");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        _sourceFile = picked;
                        _clip = null;
                    }
                }
            }

            EditorGUI.BeginChangeCheck();
            var clip = (VideoClip)EditorGUILayout.ObjectField("...or a video in the project", _clip, typeof(VideoClip), false);
            if (EditorGUI.EndChangeCheck())
            {
                _clip = clip;
                if (clip != null) _sourceFile = "";
            }

            HandleDrop(GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true)));

            EditorGUILayout.Space();
            _playWhen = (VideoPlayWhen)EditorGUILayout.EnumPopup("Play", _playWhen);
            if (_playWhen == VideoPlayWhen.WhenNearby)
                _nearbyMeters = Mathf.Max(0.5f, EditorGUILayout.FloatField("Within (metres)", _nearbyMeters));
            _widthMeters = Mathf.Max(0.1f, EditorGUILayout.FloatField("Screen width (metres)", _widthMeters));
            _loop = EditorGUILayout.Toggle("Loop", _loop);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_clip == null && string.IsNullOrEmpty(_sourceFile)))
            {
                if (GUILayout.Button("Add Video Panel", GUILayout.Height(30f))) Create();
            }
        }

        void HandleDrop(Rect area)
        {
            GUI.Box(area, "Drop a video here", EditorStyles.helpBox);
            var e = Event.current;
            if (!area.Contains(e.mousePosition) || (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)) return;

            VideoClip dropped = null;
            string path = null;
            foreach (var o in DragAndDrop.objectReferences)
                if (o is VideoClip c) dropped = c;
            if (dropped == null)
                foreach (var p in DragAndDrop.paths)
                    if (VideoPanelBuilder.IsVideoFile(p)) path = p;

            if (dropped == null && path == null)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                if (dropped != null) { _clip = dropped; _sourceFile = ""; }
                else { _sourceFile = Path.GetFullPath(path); _clip = null; }
            }
            e.Use();
        }

        void Create()
        {
            var clip = _clip != null ? _clip : VideoPanelBuilder.Import(_sourceFile);
            if (clip == null) return;
            var go = VideoPanelBuilder.Build(clip, _playWhen, _widthMeters, _nearbyMeters, _loop, null);
            ToolkitMenu.Finish(go);
            Close();
        }
    }

    /// <summary>
    /// Imports a video for Quest and builds the panel that shows it.
    /// </summary>
    public static class VideoPanelBuilder
    {
        public const string Folder = "Assets/StageSettAR/Videos";

        // Quest 3 decodes much more than this, but a 4K panel costs memory and battery for a picture
        // seen at arm's length. Wider sources are scaled down on Android.
        const int MaxAndroidWidth = 2560;

        public static bool IsVideoFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".mp4" || ext == ".mov" || ext == ".m4v" || ext == ".webm" || ext == ".avi";
        }

        /// <summary>Right-click a video clip in the Project window &gt; StageSettAR &gt; Create Video Panel.</summary>
        [MenuItem("Assets/StageSettAR/Create Video Panel", false, 2000)]
        static void FromSelection()
        {
            var clip = Selection.activeObject as VideoClip;
            if (clip == null) return;
            ConfigureForQuest(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as VideoClipImporter, clip);
            var go = Build(clip, VideoPlayWhen.WhenGazedAt, 1.6f, 2.5f, true, null);
            ToolkitMenu.Finish(go);
        }

        [MenuItem("Assets/StageSettAR/Create Video Panel", true)]
        static bool FromSelectionValid() => Selection.activeObject is VideoClip;

        /// <summary>Copy the file into the project, apply the Quest import settings, and return the clip.</summary>
        public static VideoClip Import(string sourceFile)
        {
            if (!File.Exists(sourceFile))
            {
                EditorUtility.DisplayDialog("Add Video", "That file could not be found:\n" + sourceFile, "OK");
                return null;
            }

            EnsureFolder();
            string name = Path.GetFileName(sourceFile);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + name);
            File.Copy(sourceFile, assetPath, false);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(assetPath);
            if (clip == null)
            {
                EditorUtility.DisplayDialog("Add Video", "Unity could not read that file as a video. Try converting it to an H.264 mp4.", "OK");
                return null;
            }

            ConfigureForQuest(AssetImporter.GetAtPath(assetPath) as VideoClipImporter, clip);
            return AssetDatabase.LoadAssetAtPath<VideoClip>(assetPath);
        }

        /// <summary>
        /// Android import settings that play on Quest: transcode to H.264 (the one codec every Quest
        /// decodes in hardware, and the one that avoids HEVC and ProRes files failing), capped in size.
        /// </summary>
        static void ConfigureForQuest(VideoClipImporter importer, VideoClip clip)
        {
            if (importer == null) return;

            var android = importer.GetTargetSettings("Android");
            if (android == null) android = importer.defaultTargetSettings;
            android.enableTranscoding = true;
            android.codec = VideoCodec.H264;
            android.bitrateMode = VideoBitrateMode.Medium;
            android.spatialQuality = VideoSpatialQuality.HighSpatialQuality;
            if (clip != null && clip.width > MaxAndroidWidth)
            {
                float scale = (float)MaxAndroidWidth / clip.width;
                android.resizeMode = VideoResizeMode.CustomSize;
                android.aspectRatio = VideoEncodeAspectRatio.NoScaling;
                android.customWidth = MaxAndroidWidth;
                android.customHeight = Mathf.Max(2, Mathf.RoundToInt(clip.height * scale / 2f) * 2);
            }
            else
            {
                android.resizeMode = VideoResizeMode.OriginalSize;
            }
            importer.SetTargetSettings("Android", android);
            importer.SaveAndReimport();
        }

        /// <summary>
        /// A root with a "Screen" quad (video player + Video To Material) shaped to the clip, plus
        /// the gaze or proximity wiring the play mode asks for.
        /// </summary>
        public static GameObject Build(VideoClip clip, VideoPlayWhen playWhen, float widthMeters,
                                       float nearbyMeters, bool loop, GameObject context)
        {
            var root = ToolkitMenu.Create("Video " + Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip)),
                                          new MenuCommand(context));

            // Aspect straight from the clip so nothing is stretched. Falls back to 16:9 for a clip
            // whose size is not known yet.
            float aspect = clip.width > 0 && clip.height > 0 ? (float)clip.width / clip.height : 16f / 9f;

            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screen.name = "Screen";
            screen.transform.SetParent(root.transform, false);
            screen.transform.localScale = new Vector3(widthMeters, widthMeters / aspect, 1f);

            // A thin box instead of the quad's mesh collider: cheaper, and the gaze code can read
            // its face direction from it. Sized in local units so it follows the scale.
            Object.DestroyImmediate(screen.GetComponent<Collider>());
            var box = screen.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 1f, 0.02f);
            box.isTrigger = true;

            var renderer = screen.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = EnsureMaterial(clip);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Sound comes from the screen, fading with distance, like the picture.
            var audio = screen.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;

            var player = screen.AddComponent<VideoPlayer>();
            player.clip = clip;
            player.isLooping = loop;
            player.playOnAwake = playWhen == VideoPlayWhen.OnStart;
            player.renderMode = VideoRenderMode.APIOnly; // Video To Material uses the decoder's texture
            player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            player.controlledAudioTrackCount = 1;
            player.EnableAudioTrack(0, true);
            player.SetTargetAudioSource(0, audio);

            screen.AddComponent<VideoToMaterial>(); // finds the player and renderer on this object

            switch (playWhen)
            {
                case VideoPlayWhen.WhenGazedAt:
                    var gaze = screen.AddComponent<OnGazeAction>();
                    gaze.gazeCollider = box;
                    gaze.gazeRadius = 0.2f;
                    gaze.dwellSeconds = 0.3f;
                    ToolkitMenu.Wire(gaze.onEnter, player.Play);
                    ToolkitMenu.Wire(gaze.onExit, player.Pause);
                    break;

                case VideoPlayWhen.WhenNearby:
                    // A zone on the root, which is unscaled, so its radius really is in metres.
                    var zone = new GameObject("Play Zone");
                    zone.transform.SetParent(root.transform, false);
                    var sphere = zone.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    sphere.radius = nearbyMeters;
                    var trigger = zone.AddComponent<OnTriggerAction>();
                    ToolkitMenu.Wire(trigger.onEnter, player.Play);
                    ToolkitMenu.Wire(trigger.onExit, player.Pause);
                    Undo.RegisterCreatedObjectUndo(zone, "Create Video Panel");
                    break;
            }

            Undo.RegisterCreatedObjectUndo(screen, "Create Video Panel");
            return root;
        }

        static Material EnsureMaterial(VideoClip clip)
        {
            EnsureFolder();
            string path = AssetDatabase.GenerateUniqueAssetPath(
                Folder + "/" + Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip)) + " Screen.mat");
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/StageSettAR")) AssetDatabase.CreateFolder("Assets", "StageSettAR");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/StageSettAR", "Videos");
        }
    }
}
