using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using SunsetCurse.Core;
using SunsetCurse.AI;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Tactical anti-monster flash. Per-player item, ONE use per NIGHT (resets at dawn).
    ///
    /// CONTROLS:
    ///   • Press T (or click USE in the inventory) to fire a flash.
    ///   • If the monster is in your view cone, within range, with a clear line of sight, she's
    ///     DISTRACTED for 10 seconds (slowed + senses ignored) on every peer.
    ///   • If no monster is in view, the flash still fires + counts as your night use. Aim carefully.
    ///   • You can flash again the next night.
    ///
    /// SETUP: drop on PlayerCapsule (any root component is fine). Add to NetworkPlayer's
    /// "Owner Only Behaviours" list so only the local owner runs the T-key handler.
    /// </summary>
    public class Torch : MonoBehaviour
    {
        [Header("Flash effect")]
        [Tooltip("How long the monster stays distracted after a successful flash, in seconds.")]
        [SerializeField] private float distractDuration = 10f;

        [Header("Aim cone")]
        [Tooltip("Max distance from the player camera at which the flash can reach the monster.")]
        [SerializeField] private float flashRange = 40f;
        [Tooltip("Half-angle of the flash cone in degrees (30 = 60° total cone — generous aim).")]
        [Range(5f, 90f)] [SerializeField] private float flashConeHalfAngle = 30f;

        [Header("Input")]
        [SerializeField] private Key flashKey = Key.T;

        [Header("Audio (optional)")]
        [SerializeField] private AudioClip flashSfx;

        private bool usedThisNight;
        public bool UsedThisNight => usedThisNight;
        public bool IsOwned => PlayerInventory.Local != null && PlayerInventory.Local.HasTorch;

        // Screen-flash overlay (built lazily on first use).
        private Canvas flashCanvas;
        private Image flashImage;

        private void OnEnable()
        {
            if (GameClock.Instance != null) GameClock.Instance.OnDayStart += HandleDayStart;
        }

        private void OnDisable()
        {
            if (GameClock.Instance != null) GameClock.Instance.OnDayStart -= HandleDayStart;
        }

        private void Update()
        {
            // Only the LOCAL player's torch responds to input. Without this, a remote player's Torch
            // component (which isn't owner-gated) also runs on this machine and would fire on OUR
            // keypress against OUR inventory.
            if (PlayerInventory.Local == null || GetComponentInParent<PlayerInventory>() != PlayerInventory.Local)
                return;
            if (Keyboard.current != null && Keyboard.current[flashKey].wasPressedThisFrame)
                TryFlash();
        }

        /// <summary>Public entry point — same effect as pressing T. Used by the inventory USE button.</summary>
        public void TryFlash()
        {
            if (!IsOwned)
            {
                SunsetCurse.UI.ScreenMessage.Show("You don't have a torch.", 1.5f);
                return;
            }
            if (usedThisNight)
            {
                SunsetCurse.UI.ScreenMessage.Show("Torch already used tonight. Resets at dawn.", 2f);
                return;
            }

            // Consume the night use REGARDLESS of hit — positioning matters. Also burn the torch
            // itself — torches are now re-crafted each dawn, so HasTorch flips false here AND will
            // also be cleared by PlayerInventory's dawn handler if you somehow didn't use it.
            usedThisNight = true;
            if (PlayerInventory.Local != null) PlayerInventory.Local.SetTorchOwned(false);

            // Local feedback: bright camera flash on screen, plus optional sfx.
            StartCoroutine(ScreenFlashRoutine());
            if (flashSfx != null && SunsetCurse.Audio.AudioManager.Instance != null)
                SunsetCurse.Audio.AudioManager.Instance.PlaySfx2D(flashSfx);

            // Broadcast the WORLD flash to every peer so teammates SEE the yellow burst from far
            // away (TorchFlashFX listens and spawns a brief point light at this position).
            var cam = Camera.main;
            if (cam != null) Inventory.TriggerTorchFlashVisual(cam.transform.position + cam.transform.forward * 0.5f);

            // Did we hit the monster? If so, broadcast the distraction to every peer.
            if (TryFindMonsterInView(out var monster))
            {
                Inventory.TriggerMonsterDistraction(distractDuration);
                SunsetCurse.UI.ScreenMessage.Show($"Flash struck the monster! Distracted for {distractDuration:0}s.", 2.5f);
                Debug.Log($"[Torch] Flash hit {monster.name} — distracting {distractDuration}s.", this);
            }
            else
            {
                SunsetCurse.UI.ScreenMessage.Show("Torch fired — but no target in sight.", 2f);
            }

            // Refresh the inventory card so it reads "USED TONIGHT".
            if (SunsetCurse.UI.InventoryUI.Instance != null)
                SunsetCurse.UI.InventoryUI.Instance.RefreshExternal();
        }

        private bool TryFindMonsterInView(out MonsterAI hit)
        {
            hit = null;
            var cam = Camera.main;
            if (cam == null) return false;

            foreach (var m in FindObjectsByType<MonsterAI>())
            {
                if (m == null) continue;
                // Use a chest-height anchor — easier to "see" than the feet, less head-popping.
                Vector3 target = m.transform.position + Vector3.up * 1.4f;
                Vector3 to = target - cam.transform.position;
                float dist = to.magnitude;
                if (dist > flashRange) continue;
                if (Vector3.Angle(cam.transform.forward, to) > flashConeHalfAngle) continue;

                // Line of sight: raycast must reach the monster without anything else in the way.
                if (Physics.Raycast(cam.transform.position, to.normalized, out var rh,
                                    dist + 0.5f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (rh.collider.GetComponentInParent<MonsterAI>() == m)
                    {
                        hit = m;
                        return true;
                    }
                }
            }
            return false;
        }

        private void HandleDayStart(int _)
        {
            // Dawn — torch is fresh again for the next night.
            usedThisNight = false;
            if (SunsetCurse.UI.InventoryUI.Instance != null)
                SunsetCurse.UI.InventoryUI.Instance.RefreshExternal();
        }

        // ───────────── Screen flash visual ─────────────

        private void EnsureFlashOverlay()
        {
            if (flashCanvas != null) return;
            var go = new GameObject("Torch_FlashOverlay",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            flashCanvas = go.GetComponent<Canvas>();
            flashCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            flashCanvas.sortingOrder = 200;

            var imgGO = new GameObject("Flash", typeof(RectTransform), typeof(Image));
            imgGO.transform.SetParent(go.transform, false);
            var rt = (RectTransform)imgGO.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            flashImage = imgGO.GetComponent<Image>();
            flashImage.color = new Color(1f, 1f, 1f, 0f);
            flashImage.raycastTarget = false;
        }

        private IEnumerator ScreenFlashRoutine()
        {
            EnsureFlashOverlay();
            // Snappy snap-up + slow fade — reads as a real camera flash.
            float t = 0f;
            const float rise = 0.05f, fall = 0.55f;
            while (t < rise)
            {
                t += Time.unscaledDeltaTime;
                flashImage.color = new Color(1f, 1f, 0.95f, Mathf.Lerp(0f, 0.9f, t / rise));
                yield return null;
            }
            t = 0f;
            while (t < fall)
            {
                t += Time.unscaledDeltaTime;
                flashImage.color = new Color(1f, 1f, 0.95f, Mathf.Lerp(0.9f, 0f, t / fall));
                yield return null;
            }
            flashImage.color = new Color(1f, 1f, 1f, 0f);
        }
    }
}
