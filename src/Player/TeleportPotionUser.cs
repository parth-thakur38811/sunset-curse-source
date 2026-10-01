using UnityEngine;
using UnityEngine.InputSystem;
using SunsetCurse.Core;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Handles the DAWN POTION's use key (G), owner-only. What it does depends on the mode:
    ///
    ///   • SINGLE-PLAYER ("Teleport Potion"): TAP G while alive → consume one potion and warp to a
    ///     random navmesh spot ≥45 m away (PlayerStats.TeleportRandomOnNavmesh).
    ///
    ///   • MULTIPLAYER ("Revival Potion"): HOLD G while DOWNED → consume one potion and self-revive
    ///     (PlayerStats.TriggerRevive runs on this owner's machine, same place a teammate's revive
    ///     lands). A standing MP player pressing G does nothing — the potion is your downed lifeline.
    ///
    /// G was chosen so Torch keeps its T flash key. Auto-added to PlayerCapsule at runtime by
    /// PlayerHealthSync (like ReviveTarget), so there's nothing to wire on the prefab. Owner only.
    /// </summary>
    public class TeleportPotionUser : MonoBehaviour
    {
        [SerializeField] private Key useKey = Key.G;
        [Tooltip("Metres the single-player teleport must move you, minimum.")]
        [SerializeField] private float teleportMinDistance = 45f;
        [Tooltip("Seconds to HOLD T to self-revive while downed (multiplayer).")]
        [SerializeField] private float reviveHoldSeconds = 2.5f;

        private PlayerStats stats;
        private PlayerHealthSync sync;
        private float holdTimer;
        private int lastShownHold = -1;

        private void Awake()
        {
            stats = GetComponent<PlayerStats>();
            sync = GetComponent<PlayerHealthSync>();
        }

        private void Update()
        {
            // Local owner only. In offline SP the inventory isn't NGO-spawned, so PlayerInventory.Local
            // is this player; in MP we must be the owner of THIS body.
            var inv = GetComponent<PlayerInventory>();
            if (inv == null || inv != PlayerInventory.Local) return;
            if (stats == null || Keyboard.current == null) return;
            if (!inv.HasTeleportPotion) { holdTimer = 0f; return; }

            bool multiplayer = DifficultyPreference.Mode == GameMode.Multiplayer;

            if (multiplayer)
            {
                // Revival Potion: hold T while downed to bring yourself back.
                if (stats.IsDowned && Keyboard.current[useKey].isPressed)
                {
                    holdTimer += Time.deltaTime;
                    int secsLeft = Mathf.CeilToInt(reviveHoldSeconds - holdTimer);
                    if (secsLeft != lastShownHold && secsLeft >= 0)
                    {
                        lastShownHold = secsLeft;
                        SunsetCurse.UI.ScreenMessage.Show($"Draining the revival potion… {secsLeft}s", 1f);
                    }
                    if (holdTimer >= reviveHoldSeconds)
                    {
                        if (inv.TryConsumeTeleportPotion())
                        {
                            // TriggerRevive runs on the owner's machine — which is exactly here.
                            stats.TriggerRevive();
                            SunsetCurse.UI.ScreenMessage.Show("The revival potion pulls you back to your feet!", 3f);
                        }
                        holdTimer = 0f;
                    }
                }
                else
                {
                    holdTimer = 0f;
                    lastShownHold = -1;
                }
            }
            else
            {
                // Teleport Potion: tap T while alive to blink far away.
                if (!stats.IsDowned && Keyboard.current[useKey].wasPressedThisFrame)
                {
                    if (stats.TeleportRandomOnNavmesh(teleportMinDistance))
                        inv.TryConsumeTeleportPotion();
                }
            }
        }
    }
}
