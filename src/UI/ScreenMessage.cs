using UnityEngine;
using UnityEngine.UI;

namespace SunsetCurse.UI
{
    /// <summary>
    /// A simple transient on-screen message ("toast"). Call <c>ScreenMessage.Show("...")</c>
    /// from anywhere — it lazily creates the UI the first time, shows the text for a few
    /// seconds, then fades it out. Used for "Cannot enter during day time", and reusable for
    /// any other brief notice.
    /// </summary>
    public class ScreenMessage : MonoBehaviour
    {
        private static ScreenMessage instance;

        private Text text;
        private CanvasGroup group;
        private float hideAt;

        /// <summary>Show a message centred on screen for <paramref name="seconds"/>.</summary>
        public static void Show(string message, float seconds = 2.5f)
        {
            EnsureExists();
            instance.Display(message, seconds);
        }

        private static void EnsureExists()
        {
            if (instance != null) return;
            var go = new GameObject("ScreenMessage");
            instance = go.AddComponent<ScreenMessage>();
            instance.Build();
        }

        private void Display(string message, float seconds)
        {
            text.text = message;
            hideAt = Time.time + seconds;
            if (group != null) group.alpha = 1f;
        }

        private void Update()
        {
            if (group == null || group.alpha <= 0f) return;
            if (Time.time >= hideAt)
                group.alpha = Mathf.MoveTowards(group.alpha, 0f, Time.deltaTime * 2f);
        }

        private void Build()
        {
            var canvasGO = new GameObject("Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.GetComponent<Canvas>().sortingOrder = 100; // above the other HUDs
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(canvasGO.transform, false);
            var rt = (RectTransform)textGO.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 130f);   // a bit above centre
            rt.sizeDelta = new Vector2(1000f, 80f);

            group = textGO.AddComponent<CanvasGroup>();
            group.alpha = 0f;

            text = textGO.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                        ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 34;
            text.color = Color.white;
            text.raycastTarget = false;

            var outline = textGO.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(2f, -2f);
        }
    }
}
