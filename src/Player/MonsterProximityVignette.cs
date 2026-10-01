using UnityEngine;
using UnityEngine.UI;
using SunsetCurse.AI;
using SunsetCurse.Core;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Full-screen red/dark tint that fades in as the monster gets closer to the player. Visual
    /// companion to the heartbeat audio: even if your eyes are pointed somewhere else, the screen
    /// telling you "she's near" creates dread.
    ///
    /// SETUP: put this on the player. By default it AUTO-CREATES a screen-space Canvas + a full-
    /// screen UI Image (raycastTarget off, so it never blocks input). If you want a fancier look,
    /// build a vignette PNG yourself, drop it in a Canvas Image, and drag that Image into the
    /// "Tint Image" slot — the script will then drive its alpha.
    /// </summary>
    public class MonsterProximityVignette : MonoBehaviour
    {
        [Header("Overlay")]
        [Tooltip("Optional: a UI Image (full-screen) whose alpha will be driven by proximity. " +
                 "Leave EMPTY to auto-create a flat red tint overlay at runtime.")]
        [SerializeField] private Image tintImage;
        [Tooltip("Colour of the tint. Alpha here is the MAX intensity at point-blank range.")]
        [SerializeField] private Color tintColor = new Color(0.55f, 0.02f, 0.02f, 0.6f);
        [Tooltip("Canvas sort order. 250 keeps it above the HUD but below the end-game screen (300).")]
        [SerializeField] private int sortingOrder = 250;

        [Header("Proximity range")]
        [Tooltip("Tint starts fading in at this distance.")]
        [SerializeField] private float maxRange = 15f;
        [Tooltip("At this distance and closer the tint is at full alpha (the tintColor's alpha).")]
        [SerializeField] private float minRange = 2f;

        [Header("When")]
        [Tooltip("Only show the tint at night.")]
        [SerializeField] private bool nightOnly = true;

        private GameClock clock;

        private void Start()
        {
            clock = GameClock.Instance;
            EnsureOverlay();
        }

        private void EnsureOverlay()
        {
            if (tintImage != null) return;

            var canvasGO = new GameObject("ProximityVignette_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var imgGO = new GameObject("Tint", typeof(RectTransform), typeof(Image));
            imgGO.transform.SetParent(canvasGO.transform, false);
            var rt = (RectTransform)imgGO.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            tintImage = imgGO.GetComponent<Image>();
            tintImage.raycastTarget = false;
            tintImage.color = new Color(tintColor.r, tintColor.g, tintColor.b, 0f);
        }

        private void Update()
        {
            if (tintImage == null) return;

            bool nightActive = !nightOnly || (clock != null && clock.CurrentPhase == GameClock.Phase.Night);
            // NEAREST monster each frame — either of them closing in tints the screen.
            if (!nightActive ||
                !MonsterRegistry.TryGetNearest(transform.position, out _, out float dist))
            {
                SetAlpha(0f);
                return;
            }

            // 0 at maxRange (far), 1 at minRange or closer (point-blank).
            float t = 1f - Mathf.Clamp01(Mathf.InverseLerp(minRange, maxRange, dist));
            SetAlpha(tintColor.a * t);
        }

        private void SetAlpha(float a)
        {
            var c = tintImage.color;
            c.r = tintColor.r; c.g = tintColor.g; c.b = tintColor.b;
            c.a = Mathf.Clamp01(a);
            tintImage.color = c;
        }
    }
}
