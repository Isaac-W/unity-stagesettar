using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Lists every <see cref="ISetupStep"/> in the project and lets the user run the ones that are still needed.
    /// Steps are rediscovered on each domain reload, so ones in newly compiled assemblies show up by themselves.
    /// </summary>
    public sealed class SetupWindow : EditorWindow
    {
        const string k_shownKey = "StageSettAR.SetupShown";

        List<ISetupStep> m_steps = new();
        readonly Dictionary<ISetupStep, string> m_errors = new();
        static readonly HashSet<Type> s_logged = new();
        Vector2 m_scroll;

        [MenuItem("Tools/StageSettAR/Setup…")]
        public static void Open() => GetWindow<SetupWindow>("StageSettAR Setup");

        [InitializeOnLoadMethod]
        static void AutoOpen()
        {
            if (SessionState.GetBool(k_shownKey, false)) return;
            EditorApplication.delayCall += () =>
            {
                SessionState.SetBool(k_shownKey, true);
                foreach (var step in Discover())
                    if (Query(step, out _) == SetupStatus.Needed) { Open(); return; }
            };
        }

        static List<ISetupStep> Discover()
        {
            var list = new List<ISetupStep>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<ISetupStep>())
            {
                if (t.IsAbstract || t.IsInterface) continue;
                try { list.Add((ISetupStep)Activator.CreateInstance(t)); }
                catch (Exception e) { Debug.LogException(e); }
            }
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return list;
        }

        static SetupStatus Query(ISetupStep step, out string detail)
        {
            try { return step.GetStatus(out detail); }
            catch (Exception e)
            {
                if (s_logged.Add(step.GetType())) Debug.LogException(e);
                detail = "Error: " + e.Message;
                return SetupStatus.Blocked;
            }
        }

        void OnEnable()
        {
            m_steps = Discover();
            Events.registeredPackages += OnPackages;
        }

        void OnDisable() => Events.registeredPackages -= OnPackages;

        void OnPackages(PackageRegistrationEventArgs _) => Repaint();
        void OnFocus() => Repaint();

        // A step may be running a download; keep the rows fresh without needing mouse input.
        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            EditorGUILayout.HelpBox("Click each button that is enabled, top to bottom. Unity may recompile in between; that is normal.", MessageType.None);
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            foreach (var step in m_steps) DrawStep(step);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            var ignore = EditorGUILayout.ToggleLeft("Keep downloaded UnitySplats out of version control", StageSettARSettings.instance.IgnoreSplatsInVcs);
            if (ignore != StageSettARSettings.instance.IgnoreSplatsInVcs) StageSettARSettings.instance.IgnoreSplatsInVcs = ignore;
            if (GUILayout.Button("Use a local UnitySplats folder instead of downloading…", GUILayout.Height(22)))
                SplatDependencyStep.UseLocalFolder();
        }

        void DrawStep(ISetupStep step)
        {
            var status = Query(step, out var detail);
            if (m_errors.TryGetValue(step, out var err)) detail = err;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            var icon = status switch
            {
                SetupStatus.Done => "TestPassed",
                SetupStatus.Needed => "console.warnicon",
                _ => "TestIgnored",
            };
            GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(24), GUILayout.Height(24));
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(step.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedMiniLabel);
            if (step is ISetupStepOptions options) options.DrawOptions();
            EditorGUILayout.EndVertical();
            using (new EditorGUI.DisabledScope(status != SetupStatus.Needed))
                if (GUILayout.Button(step.ActionLabel, GUILayout.Width(90), GUILayout.Height(24)))
                    RunStep(step);
            EditorGUILayout.EndHorizontal();
        }

        void RunStep(ISetupStep step)
        {
            m_errors.Remove(step);
            try { step.Run(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                m_errors[step] = "Error: " + e.Message;
            }
            GUIUtility.ExitGUI();
        }
    }
}
