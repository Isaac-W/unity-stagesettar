using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StageSettAR.Editor
{
    /// <summary>
    /// Builds the "Localizing Screen": a world-space canvas with a background, a message and a progress
    /// bar, wired to a <see cref="LocalizingScreen"/>.
    /// </summary>
    /// <remarks>
    /// Lives here because this assembly only exists when UGUI and TextMeshPro do. The rig builder calls
    /// <see cref="Build"/> by type name, so it needs no reference to either.
    /// </remarks>
    public static class LocalizingScreenBuilder
    {
        const string ScreenName = "Localizing Screen";

        /// <summary>Adds the screen under <paramref name="parent"/>, or returns the one that is already there.</summary>
        public static GameObject Build(Transform parent)
        {
            var existing = parent.GetComponentInChildren<LocalizingScreen>(true);
            if (existing != null) return existing.gameObject;

            if (TMP_Settings.instance == null)
                Debug.LogWarning("[StageSettAR] TextMeshPro has not been set up in this project, so the Localizing Screen's text will not show. " +
                                 "Choose Window > TextMeshPro > Import TMP Essential Resources.");

            // A world-space canvas is sized in pixels; the scale turns 600 x 150 of them into 0.6 x 0.15 m.
            var go = new GameObject(ScreenName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
            go.transform.SetParent(parent, false);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(600f, 150f);
            rect.localScale = Vector3.one * 0.001f;

            var background = Panel("Background", go.transform, new Color(0.05f, 0.07f, 0.12f, 0.7f));
            Anchor(background, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(go.transform, false);
            Anchor(textObject.GetComponent<RectTransform>(), new Vector2(0f, 0.3f), Vector2.one, new Vector2(30f, 0f), new Vector2(-30f, -15f));
            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.fontSize = 32f;
            text.enableAutoSizing = true;
            text.fontSizeMin = 20f;
            text.fontSizeMax = 32f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false;

            var bar = Panel("Bar", go.transform, new Color(1f, 1f, 1f, 0.2f));
            Anchor(bar, new Vector2(0.1f, 0.12f), new Vector2(0.9f, 0.24f), Vector2.zero, Vector2.zero);
            var fill = Panel("Fill", bar.transform, new Color(0.35f, 0.75f, 1f, 1f));
            Anchor(fill, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, Vector2.zero); // anchor max x is the progress

            var screen = go.AddComponent<LocalizingScreen>();
            screen.ApplyDefaultPlacement();
            screen.group = go.GetComponent<CanvasGroup>();
            screen.label = text;
            screen.bar = bar.gameObject;
            screen.barFill = fill;
            return go;
        }

        static RectTransform Panel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return go.GetComponent<RectTransform>();
        }

        static void Anchor(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
