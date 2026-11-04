using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ArenaRacer
{
    /// <summary>Small helpers for building uGUI from code (reference resolution 1280×720).</summary>
    public static class Ui
    {
        static Font _font;

        public static Font Font => _font != null ? _font : _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public static Canvas Canvas(string name, int sortingOrder)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Positions a rect: anchor and pivot share one normalised point, offset in reference pixels.</summary>
        public static RectTransform Place(this RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        public static RectTransform Stretch(this RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
            return rect;
        }

        public static Image Panel(string name, Transform parent, Color color, bool rounded = true)
        {
            var image = Rect(name, parent).gameObject.AddComponent<Image>();
            image.color = color;
            Sprite sprite = GameAssets.Instance != null ? GameAssets.Instance.Rounded : null;
            if (rounded && sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
                image.pixelsPerUnitMultiplier = 0.5f;
            }
            image.raycastTarget = false;
            return image;
        }

        public static Text Label(Transform parent, string text, int size, Color color,
            TextAnchor alignment = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal, bool shadow = false)
        {
            var label = Rect("Text", parent).gameObject.AddComponent<Text>();
            label.font = Font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = alignment;
            label.supportRichText = true;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            if (shadow)
            {
                var s = label.gameObject.AddComponent<Shadow>();
                s.effectColor = new Color(0f, 0f, 0f, 0.65f);
                s.effectDistance = new Vector2(1.5f, -1.5f);
            }
            return label;
        }

        public static Button Button(Transform parent, string text, int size, Color background, Color foreground, UnityAction onClick)
        {
            Image image = Panel("Button", parent, background);
            image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.onClick.AddListener(() =>
            {
                Sfx.Play(GameAssets.Instance != null ? GameAssets.Instance.Click : null, 0.6f);
                onClick();
            });
            Text label = Label(image.transform, text, size, foreground, TextAnchor.MiddleCenter, FontStyle.Bold);
            label.rectTransform.Stretch();
            return button;
        }

        public static void SetText(this Button button, string text) => button.GetComponentInChildren<Text>().text = text;

        public static string Ordinal(int place) => place switch
        {
            1 => "1st",
            2 => "2nd",
            3 => "3rd",
            _ => place + "th",
        };

        public static string RaceClock(float seconds)
        {
            if (seconds <= 0f) return "--:--.--";
            int minutes = (int)(seconds / 60f);
            return $"{minutes}:{seconds - minutes * 60f:00.00}";
        }
    }
}
