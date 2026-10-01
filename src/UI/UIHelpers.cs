using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SunsetCurse.UI
{
    /// <summary>
    /// Tiny code-built-UI helpers. Keeps InventoryUI / CraftingTable / etc. from each rebuilding
    /// the same Canvas / Text / Button boilerplate. No Inspector wiring — fully procedural.
    /// </summary>
    public static class UIHelpers
    {
        /// <summary>Build a Canvas with ScreenSpaceOverlay + a CanvasScaler set to 1920×1080.</summary>
        public static Canvas NewCanvas(string name, int sortingOrder, Transform parent = null)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            if (parent != null) go.transform.SetParent(parent, false);
            var c = go.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = sortingOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            return c;
        }

        /// <summary>Build a TMP label anchored at the centre of its parent, at the given offset.</summary>
        public static TMP_Text NewLabel(Transform parent, string text, int fontSize,
                                        Vector2 pos, Vector2 size, Color color,
                                        TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft,
                                        FontStyles style = FontStyles.Normal)
        {
            var go = new GameObject($"Label_{text}", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.raycastTarget = false;
            t.overflowMode = TextOverflowModes.Overflow;
            return t;
        }

        /// <summary>Standard centered button with a dark bar + bone-coloured label. Hover tint blood red.</summary>
        public static Button NewButton(Transform parent, string label, Vector2 pos, Vector2 size,
                                       UnityEngine.Events.UnityAction onClick, out TMP_Text labelText)
        {
            var go = new GameObject($"Btn_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var img = go.GetComponent<Image>();
            img.color = new Color(0.12f, 0.10f, 0.10f, 0.95f);

            // Label, centred.
            labelText = NewLabel(go.transform, label, 28, Vector2.zero, size, new Color(0.92f, 0.88f, 0.82f),
                                 TextAlignmentOptions.Center, FontStyles.Bold);
            labelText.name = "Label";

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor      = Color.white;
            colors.highlightedColor = new Color(1f, 0.55f, 0.5f);
            colors.pressedColor     = new Color(0.6f, 0.10f, 0.10f);
            colors.disabledColor    = new Color(0.35f, 0.35f, 0.35f);
            colors.selectedColor    = Color.white;
            colors.fadeDuration     = 0.10f;
            btn.colors = colors;
            btn.onClick.AddListener(onClick);
            return btn;
        }

        /// <summary>Solid-colour panel image of the given size, anchored to the centre.</summary>
        public static Image NewPanel(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        // ------------------------------------------------------------------ ring sprite

        private static Sprite ringSprite;

        /// <summary>A white ring drawn into a texture at runtime, so radial progress circles need
        /// no imported art. Generated once and shared — used by the hold-to-harvest circle
        /// (PlayerInteractor) and the downed bleed-out ring (PlayerStats). Tint via Image.color;
        /// set Image type to Filled / Radial360 to make it a progress ring.</summary>
        public static Sprite GetRingSprite()
        {
            if (ringSprite != null) return ringSprite;

            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;

            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float outer = size * 0.5f - 2f;        // ring outer radius
            float inner = outer - size * 0.1f;     // ring thickness ≈ 10% of the sprite

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    // Alpha 1 inside the ring band, with a ~1px soft falloff on both rims.
                    float a = Mathf.Clamp01(outer - d) * Mathf.Clamp01(d - inner);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                                       new Vector2(0.5f, 0.5f), 100f);
            return ringSprite;
        }
    }
}
