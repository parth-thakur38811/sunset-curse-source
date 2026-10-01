using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using SunsetCurse.Player;
using SunsetCurse.Core;

namespace SunsetCurse.UI
{
    /// <summary>
    /// Builds and updates the bottom-centre survival HUD entirely in code, so you don't
    /// have to hand-wire a Canvas:
    ///   - a THICK WHITE health bar with a heart icon on its left,
    ///   - a THIN DARK-BLUE hunger bar below it with a berry icon on its left.
    ///
    /// The bars empty from right to left as the values drop.
    ///
    /// SETUP: drop this on the player (or any empty GameObject). It auto-finds PlayerStats.
    /// To use real art, drag a heart/berry Sprite into the icon fields; otherwise simple
    /// coloured placeholders are shown.
    /// </summary>
    public class PlayerHUD : MonoBehaviour
    {
        /// <summary>The LOCAL player's HUD (remote players' HUDs disable themselves).</summary>
        public static PlayerHUD Local { get; private set; }

        [SerializeField] private PlayerStats stats;

        // When set (by SpectatorMode), the bars mirror this player's networked stats instead of ours.
        private PlayerInventory spectateTarget;
        /// <summary>Show a teammate's health/hunger while spectating; null = back to our own stats.</summary>
        public void SetSpectateTarget(PlayerInventory inv) => spectateTarget = inv;

        [Header("Icons (optional — coloured squares used if empty)")]
        [SerializeField] private Sprite heartSprite;
        [SerializeField] private Sprite berrySprite;

        [Header("Layout (pixels, at 1920x1080 reference)")]
        [SerializeField] private float barWidth = 320f;
        [SerializeField] private float healthBarHeight = 24f;   // thick
        [SerializeField] private float hungerBarHeight = 8f;    // thin
        [SerializeField] private float bottomMargin = 40f;
        [SerializeField] private float iconGap = 8f;

        [Header("Colours")]
        [SerializeField] private Color healthColor = Color.white;
        [SerializeField] private Color hungerColor = new Color(0.10f, 0.18f, 0.45f); // dark blue
        [SerializeField] private Color barBackColor = new Color(0f, 0f, 0f, 0.55f);
        [SerializeField] private Color heartColor = new Color(0.85f, 0.12f, 0.16f);
        [SerializeField] private Color berryColor = new Color(0.36f, 0.16f, 0.55f);

        private RectTransform healthFill;
        private RectTransform hungerFill;

        private IEnumerator Start()
        {
            // This HUD lives on the player prefab, so there is one per player. Only the LOCAL
            // player's should render — wait briefly to learn ownership, then disable ours if we can
            // prove this HUD belongs to a REMOTE player. (If uncertain, default to showing it.)
            var ownInv = GetComponentInParent<PlayerInventory>();
            float t = 0f;
            while (ownInv != null && PlayerInventory.Local == null && t < 5f) { t += Time.deltaTime; yield return null; }
            bool definitelyRemote = ownInv != null && PlayerInventory.Local != null && PlayerInventory.Local != ownInv;
            if (definitelyRemote) { enabled = false; yield break; }

            Local = this;

            // Bind to OUR OWN player's stats (not FindFirstObjectByType, which could grab a
            // teammate's in multiplayer).
            if (stats == null)
                stats = ownInv != null ? ownInv.GetComponent<PlayerStats>() : FindAnyObjectByType<PlayerStats>();

            BuildUI();

            if (stats != null)
            {
                stats.OnHealthChanged += SetHealth;
                stats.OnHungerChanged += SetHunger;
                SetHealth(stats.Health01);
                SetHunger(stats.Hunger01);
            }
            else
            {
                Debug.LogWarning("[PlayerHUD] No PlayerStats found for this player.", this);
            }
        }

        private void Update()
        {
            // While spectating a teammate, show THEIR replicated bars instead of our own (dead) 0.
            if (spectateTarget != null)
            {
                SetHealth(spectateTarget.Health01);
                SetHunger(spectateTarget.Hunger01);
            }
        }

        private void OnDestroy()
        {
            if (stats != null)
            {
                stats.OnHealthChanged -= SetHealth;
                stats.OnHungerChanged -= SetHunger;
            }
            if (Local == this) Local = null;
        }

        // Bars empty right-to-left by scaling the fill's X (its pivot is on the left).
        private void SetHealth(float t) { if (healthFill) healthFill.localScale = new Vector3(Mathf.Clamp01(t), 1f, 1f); }
        private void SetHunger(float t) { if (hungerFill) hungerFill.localScale = new Vector3(Mathf.Clamp01(t), 1f, 1f); }

        // ----------------------------------------------------------------- UI building

        private void BuildUI()
        {
            // Canvas (screen overlay, scales with resolution)
            var canvasGO = new GameObject("PlayerHUD_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            float iconCol = healthBarHeight + iconGap;   // width reserved on the left for icons

            // Root: anchored bottom-centre
            var root = NewRect("HUDRoot", canvasGO.transform);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 0f);
            root.sizeDelta = new Vector2(barWidth + iconCol, healthBarHeight + hungerBarHeight + 12f);
            root.anchoredPosition = new Vector2(0f, bottomMargin);

            // Health bar (upper), with heart icon
            BuildBar(root, "HealthBar", iconCol, hungerBarHeight + 8f, barWidth, healthBarHeight,
                     healthColor, out healthFill);
            BuildIcon(root, "HeartIcon", heartSprite, heartColor,
                      iconCol - iconGap, hungerBarHeight + 8f, healthBarHeight);

            // Hunger bar (lower, thinner), with berry icon
            BuildBar(root, "HungerBar", iconCol, 0f, barWidth, hungerBarHeight,
                     hungerColor, out hungerFill);
            BuildIcon(root, "BerryIcon", berrySprite, berryColor,
                      iconCol - iconGap, 0f, hungerBarHeight + 6f);
        }

        // Builds a bar at (x,y) inside root: a dark background + a coloured fill (left pivot).
        private void BuildBar(RectTransform parent, string name, float x, float y,
                              float w, float h, Color fillColor, out RectTransform fill)
        {
            var bg = NewRect(name, parent);
            bg.anchorMin = bg.anchorMax = new Vector2(0f, 0f);
            bg.pivot = new Vector2(0f, 0f);
            bg.sizeDelta = new Vector2(w, h);
            bg.anchoredPosition = new Vector2(x, y);
            AddImage(bg.gameObject, barBackColor);

            fill = NewRect(name + "_Fill", bg);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);   // stretch height to the background
            fill.pivot = new Vector2(0f, 0.5f);     // scale from the left edge
            fill.sizeDelta = new Vector2(w, 0f);
            fill.anchoredPosition = Vector2.zero;
            AddImage(fill.gameObject, fillColor);
        }

        // Builds a square icon whose RIGHT edge sits at x (i.e. just left of the bars).
        private void BuildIcon(RectTransform parent, string name, Sprite sprite, Color fallback,
                               float rightX, float y, float size)
        {
            var rect = NewRect(name, parent);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.sizeDelta = new Vector2(size, size);
            rect.anchoredPosition = new Vector2(rightX, y);
            var img = AddImage(rect.gameObject, sprite != null ? Color.white : fallback);
            if (sprite != null) img.sprite = sprite;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static Image AddImage(GameObject go, Color c)
        {
            var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }
    }
}
