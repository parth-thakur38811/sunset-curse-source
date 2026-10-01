using UnityEngine;
using UnityEngine.UI;
using TMPro;
using SunsetCurse.Core;
using SunsetCurse.Audio;
using SunsetCurse.UI;

namespace SunsetCurse.World
{
    /// <summary>
    /// The escape ritual altar. Stays a plain <see cref="Interactable"/> so PlayerInteractor can
    /// raycast-hit it as usual. All networked state (progress, channeling flag, completed flag,
    /// channeler clientId) lives on the SIBLING <see cref="RitualSiteNet"/> component. This script
    /// just renders the SHARED bar from those NetworkVariables — every peer sees the same %.
    ///
    /// SETUP:
    ///   1. Altar GameObject + Collider for the raycast.
    ///   2. Add components: RitualSite + RitualSiteNet + NetworkObject.
    /// </summary>
    [RequireComponent(typeof(RitualSiteNet))]
    public class RitualSite : Interactable
    {
        [Header("Audio (optional, plays locally on each peer at edge transitions)")]
        [SerializeField] private AudioClip startSfx;
        [SerializeField] private AudioClip completeSfx;

        private RitualSiteNet net;
        private CanvasGroup ui;
        private TMP_Text label;
        private RectTransform barFill;
        private float barWidth = 596f;
        private bool prevChanneling, prevCompleted, prevWaterMissing;

        public override string Verb => "break the curse";

        public override bool CanInteract(GameObject interactor)
        {
            EnsureNet();
            return net != null && !net.Channeling && !net.Completed
                && EscapeTracker.Instance != null && EscapeTracker.Instance.HasEnoughToEscape;
        }

        public override void Interact(GameObject interactor)
        {
            if (!CanInteract(interactor)) return;
            // The ritual needs its offering of river water: someone must be standing at the
            // altar holding the FILLED bucket. Pre-checked here (locally readable — carry flags
            // replicate everywhere) so the player gets a message instead of a silent server
            // rejection; RitualSiteNet enforces the same rule authoritatively.
            if (net.RequiresRiverWater &&
                !RitualSiteNet.FilledBucketCarrierNear(transform.position, net.ChannelRadius))
            {
                ScreenMessage.Show(
                    "The ritual demands an offering of river water — bring the FILLED bucket to the altar.", 4f);
                return;
            }
            net.RequestStartChannel();
        }

        private void Awake() => EnsureNet();

        private void Start()
        {
            BuildUI();
            if (ui != null) ui.alpha = 0f;
        }

        private void Update() => UpdateLocalUI();

        private void EnsureNet()
        {
            if (net == null) net = GetComponent<RitualSiteNet>();
        }

        private void UpdateLocalUI()
        {
            if (ui == null || net == null) return;

            bool channelingNow = net.Channeling;
            bool completedNow  = net.Completed;

            if (channelingNow && !prevChanneling)
            {
                ui.alpha = 1f;
                if (startSfx != null && AudioManager.Instance != null) AudioManager.Instance.PlaySfx2D(startSfx);
                ScreenMessage.Show("The curse resists — survive the ritual!", 3f);
            }
            else if (!channelingNow && prevChanneling && !completedNow)
            {
                ui.alpha = 0f;
            }
            if (completedNow && !prevCompleted)
            {
                ui.alpha = 0f;
                if (completeSfx != null && AudioManager.Instance != null) AudioManager.Instance.PlaySfx2D(completeSfx);
            }
            prevChanneling = channelingNow;
            prevCompleted = completedNow;

            if (channelingNow)
            {
                // PAUSED because the filled-bucket carrier left the circle: the bar freezes and
                // the label explains why (plus a one-shot toast on the transition).
                bool waterMissing = net.WaterMissing;
                if (waterMissing && !prevWaterMissing)
                    ScreenMessage.Show("The offering of river water must remain present!", 3f);
                prevWaterMissing = waterMissing;

                if (label != null)
                    label.text = waterMissing
                        ? "The offering of river water must remain present…"
                        : "Breaking the curse…";
                if (barFill != null)
                    barFill.sizeDelta = new Vector2(barWidth * Mathf.Clamp01(net.Progress), barFill.sizeDelta.y);
            }
            else
            {
                prevWaterMissing = false;
            }
        }

        private void BuildUI()
        {
            var canvasGO = new GameObject("Ritual_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            canvasGO.transform.SetParent(transform, false);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.GetComponent<Canvas>().sortingOrder = 90;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            ui = canvasGO.GetComponent<CanvasGroup>();

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(canvasGO.transform, false);
            var lrt = (RectTransform)labelGO.transform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.anchoredPosition = new Vector2(0f, 40f);
            lrt.sizeDelta = new Vector2(900f, 50f);
            label = labelGO.AddComponent<TextMeshProUGUI>();
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = 34;
            label.color = new Color(0.9f, 0.85f, 0.8f);
            label.raycastTarget = false;

            var bg = new GameObject("BarBG", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(canvasGO.transform, false);
            var brt = (RectTransform)bg.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
            brt.pivot = new Vector2(0.5f, 0.5f);
            brt.anchoredPosition = new Vector2(0f, -10f);
            brt.sizeDelta = new Vector2(600f, 26f);
            bg.GetComponent<Image>().color = new Color(0.05f, 0.04f, 0.05f, 0.85f);

            var fill = new GameObject("BarFill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(bg.transform, false);
            barFill = (RectTransform)fill.transform;
            barFill.anchorMin = barFill.anchorMax = new Vector2(0f, 0.5f);
            barFill.pivot = new Vector2(0f, 0.5f);
            barFill.anchoredPosition = new Vector2(2f, 0f);
            barFill.sizeDelta = new Vector2(0f, 18f);
            fill.GetComponent<Image>().color = new Color(0.62f, 0.06f, 0.06f);
            fill.GetComponent<Image>().raycastTarget = false;
        }
    }
}
