using UnityEngine;
using UnityEngine.UI;
using SunsetCurse.World;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Makes a DOWNED player revivable: a teammate looks at the body, presses E, and stays within
    /// range for a few seconds (a channel, like the ritual) — then the downed player stands back up.
    ///
    /// This is an <see cref="Interactable"/> that lives on every PlayerCapsule (auto-added at
    /// runtime by <see cref="PlayerHealthSync"/> — no prefab wiring needed). The player's
    /// CharacterController doubles as the raycast collider, so PlayerInteractor's "Press E to..."
    /// prompt just works when you aim at a downed teammate.
    ///
    /// HOW THE CHANNEL WORKS: PlayerInteractor only runs on the LOCAL player's machine, so
    /// Interact() — and therefore the channel — runs on the REVIVER's machine only. It ticks a
    /// timer while the reviver stays in range, draws a progress bar on their screen, and on
    /// completion routes the revive through PlayerHealthSync (ServerRpc → targeted ClientRpc on
    /// the victim's machine, where their owner-only PlayerStats actually runs — the same pattern
    /// the monster's kill uses, in reverse).
    ///
    /// Tuning (range / channel seconds / revive health) lives on PlayerStats so it's all in one
    /// Inspector place.
    /// </summary>
    public class ReviveTarget : Interactable
    {
        private Core.PlayerInventory inv;
        private PlayerStats stats;
        private PlayerHealthSync sync;

        // ── Reviver-side channel (only ever set on the machine of whoever pressed E) ──
        private GameObject reviver;
        private float channelTimer;
        private GameObject progressGO;
        private RectTransform progressFillRt;

        private const float BarWidth = 320f;

        public override string Verb => "revive your teammate (stay close)";

        private void Awake()
        {
            inv = GetComponent<Core.PlayerInventory>();
            stats = GetComponent<PlayerStats>();
            sync = GetComponent<PlayerHealthSync>();
        }

        public override bool CanInteract(GameObject interactor)
        {
            // Only a DOWNED (not dead) player can be revived...
            if (inv == null || !inv.IsAlive || !inv.IsDowned) return false;
            // ...and only by a DIFFERENT player who is alive and standing themselves.
            var other = interactor != null ? interactor.GetComponentInParent<Core.PlayerInventory>() : null;
            if (other == null || other == inv) return false;
            return other.IsAlive && !other.IsDowned;
        }

        public override void Interact(GameObject interactor)
        {
            if (!CanInteract(interactor) || reviver != null) return;
            reviver = interactor;
            channelTimer = 0f;
            SunsetCurse.UI.ScreenMessage.Show("Reviving — stay close…", 2f);
        }

        private void Update()
        {
            if (reviver == null) return;   // no channel running on this machine

            float range = stats != null ? stats.ReviveRange : 3f;
            float seconds = stats != null ? stats.ReviveChannelSeconds : 5f;

            // The channel breaks if either side dies/moves away — or the victim already got
            // revived (by someone else, or our own completion landing) — re-check every frame.
            bool victimStillDowned = inv != null && inv.IsAlive && inv.IsDowned;
            bool reviverOk = victimStillDowned && CanInteract(reviver) &&
                             Vector3.Distance(reviver.transform.position, transform.position) <= range;
            if (!reviverOk)
            {
                EndChannel(victimStillDowned ? "Revive interrupted — stay next to them!" : null);
                return;
            }

            channelTimer += Time.deltaTime;
            UpdateProgressBar(channelTimer / seconds);

            if (channelTimer >= seconds)
            {
                if (sync != null) sync.RequestRevive();
                // The bar disappears now; the downed flag flips once the network round-trip lands.
                EndChannel(null);
            }
        }

        private void EndChannel(string interruptMessage)
        {
            reviver = null;
            channelTimer = 0f;
            if (progressGO != null) progressGO.SetActive(false);
            if (interruptMessage != null) SunsetCurse.UI.ScreenMessage.Show(interruptMessage, 2f);
        }

        // ── Progress bar (code-built, bottom-third of the reviver's screen) ──

        private void UpdateProgressBar(float t)
        {
            if (progressGO == null) BuildProgressBar();
            progressGO.SetActive(true);
            progressFillRt.sizeDelta = new Vector2((BarWidth - 4f) * Mathf.Clamp01(t), -4f);
        }

        private void BuildProgressBar()
        {
            progressGO = new GameObject("ReviveProgress_Canvas", typeof(Canvas));
            progressGO.transform.SetParent(transform, false);
            var canvas = progressGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 60;

            var bg = new GameObject("BG", typeof(Image));
            bg.transform.SetParent(progressGO.transform, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = bgRt.anchorMax = new Vector2(0.5f, 0.28f);
            bgRt.pivot = new Vector2(0.5f, 0.5f);
            bgRt.sizeDelta = new Vector2(BarWidth, 14f);
            bg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            var fill = new GameObject("Fill", typeof(Image));
            fill.transform.SetParent(bg.transform, false);
            progressFillRt = (RectTransform)fill.transform;
            progressFillRt.anchorMin = new Vector2(0f, 0f);
            progressFillRt.anchorMax = new Vector2(0f, 1f);
            progressFillRt.pivot = new Vector2(0f, 0.5f);
            progressFillRt.anchoredPosition = new Vector2(2f, 0f);
            progressFillRt.sizeDelta = new Vector2(0f, -4f);
            fill.GetComponent<Image>().color = new Color(0.35f, 0.9f, 0.45f, 0.95f);
        }
    }
}
