using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;
using SunsetCurse.Audio;

namespace SunsetCurse.UI
{
    /// <summary>
    /// The opening "studio" splash. A full-screen overlay that fades the studio name in, holds,
    /// then fades out — and as it fades out it tells the <see cref="MainMenuController"/> to reveal
    /// itself, so the splash crossfades straight into the menu. Press any key to skip.
    ///
    /// SETUP: put this on the SAME "MainMenu" GameObject as MainMenuController (it auto-finds it).
    /// Optionally assign a logo sprite and a short audio "sting".
    /// </summary>
    public class SplashScreen : MonoBehaviour
    {
        [Header("Studio splash")]
        [SerializeField] private string studioName = "Couch Crash Studio";
        [SerializeField] private string tagline = "presents";
        [Tooltip("Optional studio logo shown above the name.")]
        [SerializeField] private Sprite logo;
        [Tooltip("Optional: the same horror TMP font you use on the menu, for a consistent look.")]
        [SerializeField] private TMP_FontAsset font;
        [SerializeField] private Color backgroundColor = Color.black;

        [Header("Timing (seconds)")]
        [SerializeField] private float fadeIn = 1.2f;
        [SerializeField] private float hold = 1.6f;
        [SerializeField] private float fadeOut = 1.2f;

        [Header("Audio")]
        [Tooltip("Optional one-shot played when the splash appears (a low sting/whoosh).")]
        [SerializeField] private AudioClip sting;

        [Header("Hookup")]
        [Tooltip("Menu to reveal when the splash ends. Auto-found if left empty.")]
        [SerializeField] private MainMenuController menu;

        private CanvasGroup group;
        private bool finished;

        private void Start()
        {
            Build();

            if (menu == null) menu = GetComponent<MainMenuController>();
            if (menu == null) menu = FindFirstObjectByType<MainMenuController>();

            if (sting != null && AudioManager.Instance != null)
                AudioManager.Instance.PlaySfx2D(sting);

            StartCoroutine(Run());
        }

        private void Update()
        {
            if (finished) return;
            bool skip = (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame)
                        || (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame);
            if (skip) Skip();
        }

        private IEnumerator Run()
        {
            yield return Fade(0f, 1f, fadeIn);
            yield return new WaitForSecondsRealtime(hold);
            if (menu != null) menu.RevealMenu();      // begin showing the menu underneath
            yield return Fade(1f, 0f, fadeOut);        // ...and fade the splash away to reveal it
            Finish();
        }

        private void Skip()
        {
            StopAllCoroutines();
            Finish();
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            if (menu != null) menu.RevealMenu();   // idempotent — safe if Run already called it
            group.alpha = 0f;
            group.blocksRaycasts = false;
            if (splashCanvas != null) splashCanvas.SetActive(false);   // hide the overlay
        }

        private IEnumerator Fade(float from, float to, float dur)
        {
            float t = 0f;
            group.alpha = from;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(from, to, dur <= 0f ? 1f : t / dur);
                yield return null;
            }
            group.alpha = to;
        }

        private GameObject splashCanvas;

        private void Build()
        {
            var canvasGO = new GameObject("Splash_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            canvasGO.transform.SetParent(transform, false);
            splashCanvas = canvasGO;
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300;             // above the menu (which sits at 50/80)
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            group = canvasGO.GetComponent<CanvasGroup>();
            group.alpha = 0f;

            // Solid background.
            var bg = new GameObject("BG", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(canvasGO.transform, false);
            var brt = (RectTransform)bg.transform;
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = backgroundColor;

            // Optional logo above the name.
            if (logo != null)
            {
                var logoGO = new GameObject("Logo", typeof(RectTransform), typeof(Image));
                logoGO.transform.SetParent(canvasGO.transform, false);
                var lrt = (RectTransform)logoGO.transform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
                lrt.pivot = new Vector2(0.5f, 0.5f);
                lrt.anchoredPosition = new Vector2(0f, 120f);
                lrt.sizeDelta = new Vector2(360f, 360f);
                var img = logoGO.GetComponent<Image>();
                img.sprite = logo;
                img.preserveAspect = true;
            }

            MakeText(canvasGO.transform, studioName, 80, new Vector2(0f, logo != null ? -120f : 20f),
                     new Vector2(1400f, 160f), Color.white, FontStyles.Bold);
            MakeText(canvasGO.transform, tagline, 36, new Vector2(0f, logo != null ? -210f : -70f),
                     new Vector2(800f, 80f), new Color(0.75f, 0.75f, 0.78f), FontStyles.Italic);
        }

        private void MakeText(Transform parent, string content, int size, Vector2 pos, Vector2 dim,
                              Color color, FontStyles style)
        {
            var go = new GameObject("Text_" + content, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = dim;

            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.text = content;
            text.fontSize = size;
            text.color = color;
            text.fontStyle = style;
            text.alignment = TextAlignmentOptions.Center;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
        }
    }
}
