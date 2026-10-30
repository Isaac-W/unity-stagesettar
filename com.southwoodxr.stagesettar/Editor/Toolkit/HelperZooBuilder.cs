using System;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using StageSettAR.Helpers;
using Object = UnityEngine.Object;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Maintainer tool: builds the Helper Zoo scene that ships as the package's "Helper Zoo" sample, one station
    /// per script in StageSettAR.Helpers, each already wired so it runs by itself when Play is pressed.
    /// </summary>
    /// <remarks>
    /// Run it once in a project, then move the resulting Assets/Helper Zoo folder (with its .meta files) to the
    /// package's Samples~/Helper Zoo. Everything is in that one folder and the scene has a plain Main Camera, not
    /// the rig (the rig prefab is generated per project, so a shipped scene cannot reference it); students run
    /// Tools &gt; StageSettAR &gt; Set Up Current Scene to swap the camera for the rig. Stations use only helpers and
    /// toolkit components, wired with persistent listeners so the Inspector shows how each is hooked up.
    /// </remarks>
    public static class HelperZooBuilder
    {
        const string SceneFolder = "Assets/Helper Zoo";
        const string AssetFolder = SceneFolder;
        const string ScenePath = SceneFolder + "/Helper Zoo.unity";
        const string SceneName = "Helper Zoo";

        const float EyeHeight = 1.68f;   // matches the rig's editor eye height
        const float ArcRadius = 2f;
        const float StationHeight = 1.2f;
        const float ArcStart = -96f;     // degrees from straight ahead
        const float ArcStep = 24f;
        const float LabelRise = 0.45f;
        const int StationCount = 9;

        // No menu item: it is a one-off for maintainers. To rebuild the sample, put back
        // [MenuItem("Tools/StageSettAR/Maintainer/Build Helper Zoo Sample", false, 100)] for a moment.
        static void Create()
        {
            if (TMP_Settings.instance == null)
            {
                EditorUtility.DisplayDialog("StageSettAR",
                    "TextMeshPro has not been set up in this project, so the labels will not show. " +
                    "Choose Window > TextMeshPro > Import TMP Essential Resources, then try again.", "OK");
                return;
            }

            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Helper Zoo",
                    ScenePath + " already exists. Replace it?", "Replace", "Cancel"))
            {
                return;
            }

            // Opening the new scene in Single mode would throw away the user's unsaved work.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EnsureFolders();
            var tones = MakeTones();
            var ball = MakeBallPrefab();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddUser(scene);
            EnsureLight(scene);

            var zoo = new GameObject("Helper Zoo").transform;
            BuildStations(zoo, tones, ball);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("[StageSettAR] Could not save " + ScenePath);
                return;
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[StageSettAR] Built " + ScenePath + ". Press Play to see each helper at work. " +
                      "To try the Scene Changer station, add the scene in File > Build Profiles > Scene List.");
        }

        // ---- the user ----

        // A bare camera at head height, named and tagged so Set Up Current Scene replaces it with the rig.
        static void AddUser(Scene scene)
        {
            var camera = new GameObject("Main Camera");
            camera.tag = "MainCamera"; // FacePlayer and the trigger zone both look for this
            camera.AddComponent<Camera>();
            camera.AddComponent<AudioListener>();
            camera.transform.position = new Vector3(0f, EyeHeight, 0f);
        }

        static void EnsureLight(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                    if (light.type == LightType.Directional) return;

            var go = new GameObject("Directional Light");
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var directional = go.AddComponent<Light>();
            directional.type = LightType.Directional;
            directional.shadows = LightShadows.Soft;
        }

        // ---- stations ----

        static void BuildStations(Transform zoo, AudioClip[] tones, GameObject ball)
        {
            int i = 0;
            BuildRotator(Station(zoo, i++, "Rotator", "Spins at a set speed, here around a tilted axis."));
            BuildFloater(Station(zoo, i++, "Floater", "Bobs up and down. Handy for pickups."));
            BuildFacePlayer(Station(zoo, i++, "Face Player", "The arrow always points at you. These labels use it too."));
            BuildFollowTarget(Station(zoo, i++, "Follow Target", "Each ball follows the one before it. The first follows a spinning marker."));
            BuildMoveToTarget(Station(zoo, i++, "Move To Target", "Goes to a marker. Two Timed Actions send it back and forth."));
            BuildRandomSound(Station(zoo, i++, "Random Sound", "Plays one of three tones with a little pitch change."), tones);
            BuildSimpleSpawner(Station(zoo, i++, "Simple Spawner", "Drops a copy of a prefab every half second, up to Max Count."), ball);
            BuildDebugMessage(Station(zoo, i++, "Debug Message", "Prints to the Console every 3 seconds."));
            BuildSceneChanger(Station(zoo, i++, "Scene Changer", "Walk into the pillar to reload this scene. It must be in Build Profiles."));
            if (i != StationCount) Debug.LogWarning("[StageSettAR] Helper Zoo: station count is out of step with the arc.");
        }

        /// <summary>An empty root on the arc, turned toward the user, with its label above it.</summary>
        static Transform Station(Transform zoo, int index, string title, string explanation)
        {
            float angle = (ArcStart + ArcStep * index) * Mathf.Deg2Rad;
            var root = new GameObject("Station: " + title).transform;
            root.SetParent(zoo, false);
            Vector3 position = new Vector3(Mathf.Sin(angle) * ArcRadius, StationHeight, Mathf.Cos(angle) * ArcRadius);
            root.position = position;
            root.rotation = Quaternion.LookRotation(new Vector3(-position.x, 0f, -position.z));

            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(root, false);
            label.transform.localPosition = Vector3.up * LabelRise;
            // TMP text reads from behind, so it faces away from the user; FacePlayer (flip) keeps it that way in Play mode.
            label.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var rect = label.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0.8f, 0.4f);

            var text = label.AddComponent<TextMeshPro>();
            text.text = "<b>" + title + "</b>\n" + explanation;
            text.fontSize = 0.5f;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.color = Color.white;
            label.AddComponent<FacePlayer>();
            return root;
        }

        static void BuildRotator(Transform station)
        {
            var cube = Prim(PrimitiveType.Cube, "Cube", station, Vector3.zero, Vector3.one * 0.3f, Mat("Orange", new Color(1f, 0.55f, 0.1f)));
            var rotator = cube.AddComponent<Rotator>();
            rotator.axis = new Vector3(0.3f, 1f, 0f);
            rotator.degreesPerSecond = 90f;
        }

        static void BuildFloater(Transform station)
        {
            var sphere = Prim(PrimitiveType.Sphere, "Sphere", station, Vector3.zero, Vector3.one * 0.25f, Mat("Cyan", new Color(0.1f, 0.8f, 0.9f)));
            var floater = sphere.AddComponent<Floater>();
            floater.height = 0.12f;
            floater.bobsPerSecond = 0.5f;
        }

        static void BuildFacePlayer(Transform station)
        {
            // A sign made of two boxes: the shaft and a nose at +Z, which is the side that turns to the user.
            var arrow = new GameObject("Arrow");
            arrow.transform.SetParent(station, false);
            var face = arrow.AddComponent<FacePlayer>();
            face.uprightOnly = true;
            face.flip = false; // not text, so +Z should point at the user
            Prim(PrimitiveType.Cube, "Shaft", arrow.transform, Vector3.zero, new Vector3(0.08f, 0.08f, 0.4f), Mat("Green", new Color(0.3f, 0.85f, 0.3f)));
            Prim(PrimitiveType.Cube, "Tip", arrow.transform, new Vector3(0f, 0f, 0.25f), new Vector3(0.2f, 0.08f, 0.1f), Mat("Red", new Color(0.9f, 0.2f, 0.2f)));
        }

        static void BuildFollowTarget(Transform station)
        {
            // The leader orbits on a spinning pivot, so it moves without any script of its own.
            var pivot = new GameObject("Orbit").transform;
            pivot.SetParent(station, false);
            var spin = pivot.gameObject.AddComponent<Rotator>();
            spin.axis = Vector3.up;
            spin.degreesPerSecond = 70f;

            var leader = Prim(PrimitiveType.Sphere, "Leader", pivot, new Vector3(0.3f, 0f, 0f), Vector3.one * 0.12f, Mat("Red", new Color(0.9f, 0.2f, 0.2f)));
            Transform previous = leader.transform;
            var colors = new[] { new Color(1f, 0.55f, 0.1f), new Color(1f, 0.9f, 0.2f), new Color(0.3f, 0.85f, 0.3f), new Color(0.2f, 0.5f, 1f) };
            string[] names = { "Orange", "Yellow", "Green", "Blue" };
            for (int i = 0; i < colors.Length; i++)
            {
                // Followers hang off the station, not the pivot, or they would spin with it instead of following.
                float size = 0.1f - 0.01f * i;
                var link = Prim(PrimitiveType.Sphere, "Follower " + (i + 1), station, new Vector3(0.3f, 0f, -0.14f * (i + 1)), Vector3.one * size, Mat(names[i], colors[i]));
                var follow = link.AddComponent<FollowTarget>();
                follow.target = previous;
                follow.followDistance = 0.12f;
                follow.speed = 1.2f;
                previous = link.transform;
            }
        }

        static void BuildMoveToTarget(Transform station)
        {
            var a = new GameObject("Marker A").transform;
            a.SetParent(station, false);
            a.localPosition = new Vector3(-0.3f, 0f, 0f);
            var b = new GameObject("Marker B").transform;
            b.SetParent(station, false);
            b.localPosition = new Vector3(0.3f, 0f, 0f);

            var mover = Prim(PrimitiveType.Capsule, "Mover", station, a.localPosition, new Vector3(0.12f, 0.12f, 0.12f), Mat("Purple", new Color(0.65f, 0.3f, 0.9f)));
            var move = mover.AddComponent<MoveToTarget>();
            move.destination = b;
            move.speed = 0.3f;
            move.moveOnStart = true;

            // Moving stops on arrival, so two one-shot timers hand the mover to the other marker in turn:
            // each fires after 3 s, sends it to its marker, then starts the other timer.
            var timers = new GameObject("Ping Pong");
            timers.transform.SetParent(station, false);
            var toA = timers.AddComponent<TimedAction>();
            var toB = timers.AddComponent<TimedAction>();
            toA.duration = 3f;
            toA.startOnAwake = true;
            toB.duration = 3f;

            UnityEventTools.AddObjectPersistentListener<Transform>(toA.onTimerComplete, move.SetDestination, a);
            UnityEventTools.AddPersistentListener(toA.onTimerComplete, move.StartMoving);
            UnityEventTools.AddPersistentListener(toA.onTimerComplete, toB.Play);

            UnityEventTools.AddObjectPersistentListener<Transform>(toB.onTimerComplete, move.SetDestination, b);
            UnityEventTools.AddPersistentListener(toB.onTimerComplete, move.StartMoving);
            UnityEventTools.AddPersistentListener(toB.onTimerComplete, toA.Play);
        }

        static void BuildRandomSound(Transform station, AudioClip[] tones)
        {
            var speaker = Prim(PrimitiveType.Sphere, "Speaker", station, Vector3.zero, Vector3.one * 0.2f, Mat("Pink", new Color(1f, 0.4f, 0.7f)));
            var sound = speaker.AddComponent<RandomSound>(); // adds its AudioSource
            var source = speaker.GetComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            sound.clips = tones;

            var timer = speaker.AddComponent<TimedAction>();
            timer.duration = 2.5f;
            timer.repeat = true;
            timer.startOnAwake = true;
            UnityEventTools.AddPersistentListener(timer.onTimerComplete, sound.PlayRandom);
        }

        static void BuildSimpleSpawner(Transform station, GameObject ball)
        {
            Prim(PrimitiveType.Cube, "Spawner", station, Vector3.zero, new Vector3(0.1f, 0.1f, 0.1f), Mat("Grey", new Color(0.6f, 0.6f, 0.65f)));

            // The spawn point rides a spinning pivot so the copies land in a ring instead of one pile.
            var pivot = new GameObject("Spawn Arm").transform;
            pivot.SetParent(station, false);
            var spin = pivot.gameObject.AddComponent<Rotator>();
            spin.axis = Vector3.up;
            spin.degreesPerSecond = 60f;
            var point = new GameObject("Spawn Point").transform;
            point.SetParent(pivot, false);
            point.localPosition = new Vector3(0.3f, 0f, 0f);

            var spawner = station.gameObject.AddComponent<SimpleSpawner>();
            spawner.prefab = ball;
            spawner.spawnPoint = point;
            spawner.maxCount = 12;

            var timer = station.gameObject.AddComponent<TimedAction>();
            timer.duration = 0.5f;
            timer.repeat = true;
            timer.startOnAwake = true;
            UnityEventTools.AddPersistentListener(timer.onTimerComplete, spawner.Spawn);
        }

        static void BuildDebugMessage(Transform station)
        {
            var box = Prim(PrimitiveType.Cube, "Debug Message", station, Vector3.zero, new Vector3(0.25f, 0.15f, 0.05f), Mat("Yellow", new Color(1f, 0.9f, 0.2f)));
            var debug = box.AddComponent<DebugMessage>();
            var timer = box.AddComponent<TimedAction>();
            timer.duration = 3f;
            timer.repeat = true;
            timer.startOnAwake = true;
            UnityEventTools.AddStringPersistentListener(timer.onTimerComplete, debug.Log, "Hello from the Helper Zoo!");
        }

        static void BuildSceneChanger(Transform station)
        {
            Prim(PrimitiveType.Cube, "Door", station, Vector3.zero, new Vector3(0.4f, 0.6f, 0.05f), Mat("Blue", new Color(0.2f, 0.5f, 1f)));

            var changer = station.gameObject.AddComponent<SceneChanger>();
            changer.sceneName = SceneName; // reloads this scene, so there is always somewhere to go

            // A tall zone around the station's spot on the floor, so the user's head (the MainCamera tag) enters it
            // when they walk over, whatever their height. Only fires once the user deliberately steps in.
            var zone = new GameObject("Zone");
            zone.transform.SetParent(station, false);
            zone.transform.position = new Vector3(station.position.x, 1.25f, station.position.z);
            var box = zone.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(0.6f, 2.5f, 0.6f);
            var trigger = zone.AddComponent<OnTriggerAction>();
            trigger.enterOnce = true;
            UnityEventTools.AddPersistentListener(trigger.onEnter, changer.LoadScene);
        }

        // ---- assets ----

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPosition, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>()); // nothing here needs one
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        static void EnsureFolders()
        {
            // One folder holds the scene and its assets, so it can be moved into Samples~ whole.
            if (!AssetDatabase.IsValidFolder(SceneFolder)) AssetDatabase.CreateFolder("Assets", "Helper Zoo");
        }

        /// <summary>A URP Lit material saved once and reused by name, so rebuilding the zoo does not pile up copies.</summary>
        static Material Mat(string name, Color color)
        {
            string path = AssetFolder + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { color = color };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static GameObject MakeBallPrefab()
        {
            string path = AssetFolder + "/Spawned Ball.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var ball = Prim(PrimitiveType.Sphere, "Spawned Ball", null, Vector3.zero, Vector3.one * 0.08f, Mat("Red", new Color(0.9f, 0.2f, 0.2f)));
            var prefab = PrefabUtility.SaveAsPrefabAsset(ball, path);
            Object.DestroyImmediate(ball);
            return prefab;
        }

        /// <summary>Three short beeps, written as WAV files because the package ships no audio.</summary>
        static AudioClip[] MakeTones()
        {
            float[] pitches = { 523.25f, 659.25f, 783.99f }; // C, E, G
            var clips = new AudioClip[pitches.Length];
            for (int i = 0; i < pitches.Length; i++)
            {
                string path = AssetFolder + "/Tone " + (i + 1) + ".wav";
                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, ToneWav(pitches[i]));
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }
                clips[i] = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            return clips;
        }

        static byte[] ToneWav(float frequency)
        {
            const int rate = 22050;
            const float seconds = 0.3f;
            int count = (int)(rate * seconds);
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + count * 2);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);              // fmt chunk size
            w.Write((short)1);        // PCM
            w.Write((short)1);        // mono
            w.Write(rate);
            w.Write(rate * 2);        // bytes per second
            w.Write((short)2);        // bytes per frame
            w.Write((short)16);       // bits
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(count * 2);
            for (int n = 0; n < count; n++)
            {
                float t = n / (float)rate;
                // A 10 ms fade-in avoids a click; the squared decay makes it a soft "plink".
                float envelope = Mathf.Min(1f, t / 0.01f) * Mathf.Pow(1f - t / seconds, 2f);
                w.Write((short)(Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.5f * short.MaxValue));
            }
            w.Flush();
            return stream.ToArray();
        }
    }
}
