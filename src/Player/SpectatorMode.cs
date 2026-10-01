using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using Unity.Cinemachine;
using SunsetCurse.Core;
using SunsetCurse.UI;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Co-op spectator mode. When THIS PLAYER (the local owner) dies but other teammates are still
    /// alive, instead of immediately showing GAME OVER we hand the camera to one of the survivors
    /// and pin a clickable player list in the bottom-left so the dead player can switch view.
    ///
    /// Only the LAST living player gets the GAME OVER screen — when their <see cref="PlayerStats"/>
    /// reports death and the alive-count drops to zero, <see cref="GameOverController.HandleDeath"/>
    /// runs as usual.
    ///
    /// In single-player there's only one player, so death goes straight to GAME OVER (this script
    /// stays dormant since FindAliveOthers() returns nothing).
    ///
    /// SETUP: drop on PlayerCapsule (owner-only behaviour — same list as FirstPersonController etc.,
    /// so it only runs on YOUR player on YOUR machine).
    /// </summary>
    public class SpectatorMode : MonoBehaviour
    {
        public static SpectatorMode Local { get; private set; }

        private PlayerStats myStats;
        private bool spectating;
        private PlayerInventory currentTarget;
        private CinemachineVirtualCameraBase vcam;

        // UI
        private Canvas uiCanvas;
        private GameObject uiPanel;
        private TMP_Text headerLabel;
        private RectTransform listContent;
        private readonly Dictionary<PlayerInventory, Button> playerButtons = new Dictionary<PlayerInventory, Button>();
        private float refreshTimer;

        private void Awake() => myStats = GetComponent<PlayerStats>();

        private void OnEnable()
        {
            Local = this;
            if (myStats != null) myStats.OnPlayerDied += EnterSpectatorIfPossible;
            PlayerInventory.OnAnyAliveChanged += HandleAliveChanged;
        }

        private void OnDisable()
        {
            if (myStats != null) myStats.OnPlayerDied -= EnterSpectatorIfPossible;
            PlayerInventory.OnAnyAliveChanged -= HandleAliveChanged;
            if (Local == this) Local = null;
        }

        private void Update()
        {
            if (!spectating) return;
            // Periodic refresh: if the player we're spectating dies, jump to another survivor.
            refreshTimer -= Time.unscaledDeltaTime;
            if (refreshTimer > 0f) return;
            refreshTimer = 0.75f;

            if (currentTarget == null || !currentTarget.IsAlive)
                SwitchToAnyAliveOther();
            RefreshPlayerList();
        }

        // ───────────── Entry / camera switching ─────────────

        private void EnterSpectatorIfPossible()
        {
            if (spectating) return;

            // SP and last-player-alive paths: nobody else to spectate → fall through to game over.
            var others = FindAliveOthers();
            if (others.Count == 0)
            {
                Debug.Log("[SpectatorMode] No alive teammates — letting GameOverController take over.");
                return;
            }

            spectating = true;
            DisableLocalPlayerControl();

            // Pick a random teammate to start.
            var initial = others[Random.Range(0, others.Count)];
            BuildUI();
            SwitchView(initial);
            Debug.Log($"[SpectatorMode] Entered spectator → following {initial.gameObject.name}.", this);
        }

        private void SwitchToAnyAliveOther()
        {
            var others = FindAliveOthers();
            if (others.Count == 0)
            {
                Debug.Log("[SpectatorMode] All teammates have fallen too.");
                spectating = false;
                if (uiCanvas != null) uiCanvas.gameObject.SetActive(false);
                // GameOverController's HandleDeath should already have fired for whoever died last;
                // if not, fall back to the global game-over.
                if (GameOverController.Instance != null) GameOverController.Instance.ShowGameOver();
                return;
            }
            SwitchView(others[0]);
        }

        public void SwitchView(PlayerInventory target)
        {
            if (target == null) return;
            currentTarget = target;

            // Point our HUD at the player we're now watching, so the health/hunger bars show THEIR
            // stats (replicated) instead of our own dead 0.
            if (SunsetCurse.UI.PlayerHUD.Local != null) SunsetCurse.UI.PlayerHUD.Local.SetSpectateTarget(target);

            if (vcam == null) vcam = Object.FindFirstObjectByType<CinemachineVirtualCameraBase>();
            if (vcam == null)
            {
                Debug.LogWarning("[SpectatorMode] No CinemachineVirtualCamera found — can't switch view.", this);
                return;
            }

            // Find the camera root on the target. Convention: a child named "PlayerCameraRoot"
            // (tag "CinemachineTarget"). Falls back to target's transform.
            Transform follow = FindCameraRoot(target.transform) ?? target.transform;
            vcam.Follow = follow;
            vcam.LookAt = follow;

            if (headerLabel != null) headerLabel.text = $"SPECTATING — {NameOf(target)}";
        }

        private Transform FindCameraRoot(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.CompareTag("CinemachineTarget") || t.name == "PlayerCameraRoot") return t;
            return null;
        }

        private List<PlayerInventory> FindAliveOthers()
        {
            var result = new List<PlayerInventory>();
            foreach (var inv in Object.FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None))
            {
                if (inv == null) continue;
                if (inv == PlayerInventory.Local) continue;
                if (!inv.IsAlive) continue;
                // DOWNED players don't count as spectatable: with nobody standing they can't be
                // revived and are about to die themselves (PlayerStats skips the bleed-out when no
                // rescuer exists) — spectating them instead of showing GAME OVER was a bug.
                if (inv.IsDowned) continue;
                result.Add(inv);
            }
            return result;
        }

        private void DisableLocalPlayerControl()
        {
            // Stop our own first-person controller / input. Camera moves with spectated player now.
            DisableByName("FirstPersonController");
            DisableByName("StarterAssetsInputs");
            DisableByName("PlayerInput");
            DisableByName("PlayerInteractor");
        }

        private void DisableByName(string typeName)
        {
            foreach (var mb in GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name == typeName) mb.enabled = false;
        }

        private void HandleAliveChanged()
        {
            if (!spectating) return;
            // Someone else's alive state changed — refresh our list and re-evaluate current target.
            if (currentTarget == null || !currentTarget.IsAlive) SwitchToAnyAliveOther();
            RefreshPlayerList();
        }

        // ───────────── UI ─────────────

        private void BuildUI()
        {
            uiCanvas = UIHelpers.NewCanvas("Spectator_Canvas", 250, transform);
            // Cursor needs to be visible to click buttons.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            uiPanel = new GameObject("SpectatorPanel", typeof(RectTransform), typeof(Image));
            uiPanel.transform.SetParent(uiCanvas.transform, false);
            var rt = (RectTransform)uiPanel.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);   // bottom-left
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(24f, 24f);
            rt.sizeDelta = new Vector2(320f, 280f);
            uiPanel.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.04f, 0.85f);

            headerLabel = UIHelpers.NewLabel(uiPanel.transform, "SPECTATING", 26,
                new Vector2(0f, 110f), new Vector2(300f, 30f),
                new Color(1f, 0.55f, 0.55f), TextAlignmentOptions.Center, FontStyles.Bold);

            UIHelpers.NewLabel(uiPanel.transform, "You died. Click a teammate to watch them.", 16,
                new Vector2(0f, 80f), new Vector2(300f, 20f),
                new Color(0.78f, 0.78f, 0.78f), TextAlignmentOptions.Center);

            // Scrollable list area (simple — vertical layout)
            var listGO = new GameObject("PlayerList", typeof(RectTransform), typeof(VerticalLayoutGroup));
            listGO.transform.SetParent(uiPanel.transform, false);
            listContent = (RectTransform)listGO.transform;
            listContent.anchorMin = listContent.anchorMax = new Vector2(0.5f, 0.5f);
            listContent.pivot = new Vector2(0.5f, 0.5f);
            listContent.anchoredPosition = new Vector2(0f, -10f);
            listContent.sizeDelta = new Vector2(280f, 180f);
            var layout = listGO.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            RefreshPlayerList();
        }

        private void RefreshPlayerList()
        {
            if (listContent == null) return;

            // Remove stale buttons (for players that despawned).
            var stale = new List<PlayerInventory>();
            foreach (var kv in playerButtons)
                if (kv.Key == null) stale.Add(kv.Key);
            foreach (var s in stale) playerButtons.Remove(s);

            // Add buttons for all currently alive others (and refresh labels).
            foreach (var inv in Object.FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None))
            {
                if (inv == null || inv == PlayerInventory.Local) continue;
                if (!playerButtons.TryGetValue(inv, out var btn))
                {
                    btn = BuildListButton(inv);
                    playerButtons[inv] = btn;
                }
                bool alive = inv.IsAlive;
                btn.interactable = alive;
                var label = btn.GetComponentInChildren<TMP_Text>();
                if (label != null)
                {
                    string marker = inv == currentTarget ? "▶ " : "   ";
                    label.text = marker + NameOf(inv) + (alive ? "" : "  (dead)");
                }
            }
        }

        private Button BuildListButton(PlayerInventory inv)
        {
            var go = new GameObject($"Btn_{inv.gameObject.name}", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(listContent, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(260f, 36f);
            var img = go.GetComponent<Image>();
            img.color = new Color(0.10f, 0.08f, 0.10f, 0.95f);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)labelGO.transform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(10f, 0f); lrt.offsetMax = new Vector2(-10f, 0f);
            var t = labelGO.AddComponent<TextMeshProUGUI>();
            t.text = NameOf(inv);
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.fontSize = 18;
            t.color = new Color(0.9f, 0.9f, 0.9f);
            t.raycastTarget = false;

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor      = Color.white;
            colors.highlightedColor = new Color(1f, 0.55f, 0.5f);
            colors.pressedColor     = new Color(0.6f, 0.10f, 0.10f);
            btn.colors = colors;
            btn.onClick.AddListener(() => SwitchView(inv));
            return btn;
        }

        private static string NameOf(PlayerInventory inv)
        {
            // "Player [LOCAL — Id 1]" or "Player [remote — Id 2]" → trim to a readable name.
            string raw = inv.gameObject.name;
            int idx = raw.IndexOf("Id ", System.StringComparison.Ordinal);
            return idx >= 0 ? "Player " + raw.Substring(idx + 3).TrimEnd(']') : raw;
        }
    }
}
