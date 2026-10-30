using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace StageSettAR.Editor
{
    /// <summary>
    /// GameObject &gt; StageSettAR: ready-made objects for students. Each one is created fully set up,
    /// so nothing has to be wired by hand before it works.
    /// </summary>
    /// <remarks>
    /// New objects go under the selected object if there is one, otherwise under the map's Content
    /// object when a STAR Localizer is in the scene (so they live in the map's frame and land in
    /// the right place in the real room), otherwise in the scene root. Either way they appear about
    /// 1.5 m in front of the Scene view camera.
    /// </remarks>
    public static class ToolkitMenu
    {
        const string Root = "GameObject/StageSettAR/";
        const float PlaceDistance = 1.5f;

        // ---- menu items ----

        [MenuItem(Root + "Trigger Zone", false, 10)]
        static void TriggerZone(MenuCommand command)
        {
            var go = Create("Trigger Zone", command);
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(2f, 2f, 2f);
            go.AddComponent<OnTriggerAction>(); // triggerTagName defaults to MainCamera: the user's head.
            Finish(go);
        }

        [MenuItem(Root + "Gaze Target", false, 11)]
        static void GazeTarget(MenuCommand command)
        {
            var go = Create("Gaze Target", command);
            var sphere = go.AddComponent<SphereCollider>();
            sphere.isTrigger = true; // a trigger never blocks other gaze targets behind it
            sphere.radius = 0.3f;
            go.AddComponent<OnGazeAction>();
            Finish(go);
        }

        [MenuItem(Root + "Info Panel", false, 12)]
        static void InfoPanel(MenuCommand command) => BuildInfoPanel("Info Panel", command, follows: false);

        [MenuItem(Root + "Info Panel (Follows You)", false, 13)]
        static void InfoPanelFollowing(MenuCommand command) => BuildInfoPanel("Info Panel (Follows You)", command, follows: true);

        [MenuItem(Root + "Path Follower", false, 30)]
        static void PathFollowerItem(MenuCommand command)
        {
            var go = Create("Path", command);
            var follower = go.AddComponent<PathFollower>();

            // The route object stays put and the follower travels along it. The route's points are
            // read in this object's space, so moving or rotating the path carries the whole route.
            var mover = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            mover.name = "Follower";
            Object.DestroyImmediate(mover.GetComponent<Collider>());
            mover.transform.SetParent(go.transform, false);
            mover.transform.localScale = Vector3.one * 0.15f;
            Undo.RegisterCreatedObjectUndo(mover, "Create Path Follower");
            follower.SetTarget(mover.transform);
            follower.Rebuild();
            Finish(go);
        }

        [MenuItem(Root + "Timed Sequence", false, 31)]
        static void TimedSequence(MenuCommand command)
        {
            var go = Create("Timed Sequence", command);
            var sequence = go.AddComponent<ExperienceSequence>();
            sequence.steps.Add(new ExperienceSequence.Step { label = "First", waitFor = ExperienceSequence.WaitFor.Seconds, seconds = 3f });
            sequence.steps.Add(new ExperienceSequence.Step { label = "Second", waitFor = ExperienceSequence.WaitFor.Seconds, seconds = 5f });
            Finish(go);
        }

        [MenuItem(Root + "Video Panel...", false, 40)]
        static void VideoPanelItem() => AddVideoWindow.Open();

        // ---- info panel ----

        static void BuildInfoPanel(string name, MenuCommand command, bool follows)
        {
            if (TMP_Settings.instance == null)
            {
                EditorUtility.DisplayDialog("StageSettAR",
                    "TextMeshPro has not been set up in this project, so the panel's text will not show. " +
                    "Choose Window > TextMeshPro > Import TMP Essential Resources, then try again.", "OK");
            }

            var go = Create(name, command);

            // A world-space canvas is sized in pixels; the scale turns 600 x 300 of them into
            // 0.6 x 0.3 m. 10 dynamic pixels per unit keeps text crisp at arm's length.
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(600f, 300f);
            rect.localScale = Vector3.one * 0.001f;

            var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
            background.transform.SetParent(go.transform, false);
            Stretch(background.GetComponent<RectTransform>());
            background.GetComponent<Image>().color = new Color(0.05f, 0.07f, 0.12f, 0.75f);

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(go.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            Stretch(textRect);
            textRect.offsetMin = new Vector2(30f, 20f);
            textRect.offsetMax = new Vector2(-30f, -20f);
            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.text = "Hello! Change this text in the Text child.";
            text.fontSize = 36f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;

            // A box over the panel so gaze, and ObjectFade's collider handling, have something to use.
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(600f, 300f, 10f);
            box.isTrigger = true;

            // Canvas text reads from its back (-Z) side, so the panel faces away from the line to the
            // user. Panel Look At's default offset and Tag Along's Flip Forward both do that.
            if (follows)
            {
                var tag = go.AddComponent<TagAlong>();
                tag.flipForward = true;
            }
            else
            {
                go.AddComponent<PanelLookAt>();
            }
            Finish(go);
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // ---- shared ----

        /// <summary>Makes an empty object, placed and parented, with undo registered.</summary>
        internal static GameObject Create(string name, MenuCommand command)
        {
            var go = new GameObject(name);
            Place(go, command != null ? command.context as GameObject : null);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        internal static void Finish(GameObject go)
        {
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        /// <summary>The object new content should go under: the map's content folder, if there is a map in the scene.</summary>
        internal static Transform FindContentRoot()
        {
            var localizer = Object.FindFirstObjectByType<StarLocalizer>();
            if (localizer == null) return null;
            var mapRoot = localizer.mapRoot != null ? localizer.mapRoot : localizer.transform;
            // Never the map root itself: everything there but the content folder is locked.
            return LockedObjects.ToolkitContent(localizer, mapRoot);
        }

        /// <summary>
        /// Parent the object and put it about 1.5 m in front of the Scene view camera, turned to face it.
        /// </summary>
        internal static void Place(GameObject go, GameObject context)
        {
            Transform parent = context != null && context.scene.IsValid() ? context.transform : FindContentRoot();

            Vector3 position = Vector3.zero;
            float yaw = 0f;
            var view = SceneView.lastActiveSceneView;
            if (view != null && view.camera != null)
            {
                var cam = view.camera.transform;
                position = cam.position + cam.forward * PlaceDistance;
                Vector3 flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
                if (flat.sqrMagnitude > 1e-4f) yaw = Quaternion.LookRotation(flat).eulerAngles.y;
            }

            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            GameObjectUtility.EnsureUniqueNameForSibling(go);
        }

        /// <summary>Adds a listener to a persistent UnityEvent; a thin wrapper so the video code reads plainly.</summary>
        internal static void Wire(UnityEngine.Events.UnityEvent evt, UnityEngine.Events.UnityAction action) =>
            UnityEventTools.AddPersistentListener(evt, action);
    }
}
