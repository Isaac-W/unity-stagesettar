using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Makes the scene that asks for the camera permission, and puts it first in the build.
    /// </summary>
    /// <remarks>
    /// The camera does not start if it opens before the permission is granted, and asking from the scene that uses
    /// it is too late (see <see cref="CameraPermissionGate"/>). The scene holds only the gate and a plain black camera,
    /// with no XR rig, so nothing touches the camera until the experience scene loads.
    /// </remarks>
    public sealed class PermissionSceneStep : ISetupStep
    {
        public const string ScenePath = RigBuilder.Folder + "/StageSettAR Camera Permission.unity";

        public int Order => 45;
        public string Title => "Camera permission scene";
        public string ActionLabel => "Create";

        public SetupStatus GetStatus(out string detail)
        {
            if (!System.IO.File.Exists(ScenePath))
            {
                detail = "Make a first scene that asks for camera access and then loads the next scene. Without it the camera may never start on the first run.";
                return SetupStatus.Needed;
            }
            if (!IsFirstInBuild())
            {
                detail = "Put " + ScenePath + " first in the build, so the permission is granted before any scene uses the camera.";
                return SetupStatus.Needed;
            }
            detail = "The build starts with " + ScenePath + ", which loads the next build scene once camera access is granted.";
            return SetupStatus.Done;
        }

        public void Run()
        {
            if (!System.IO.File.Exists(ScenePath)) CreateScene();
            MakeFirstInBuild();
        }

        static bool IsFirstInBuild()
        {
            var first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
            return first != null && first.path == ScenePath;
        }

        static void CreateScene()
        {
            RigBuilder.EnsureFolder();
            // Additive, so the scenes the user has open are left alone.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var cameraObject = new GameObject("Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 0;

                var gateObject = new GameObject("Camera Permission Gate");
                SceneManager.MoveGameObjectToScene(gateObject, scene);
                gateObject.AddComponent<CameraPermissionGate>();

                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.Refresh();
        }

        static void MakeFirstInBuild()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[StageSettAR] " + ScenePath + " is now the first scene in the build. The scene after it opens once camera access is granted.");
        }
    }
}
