using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using SunsetCurse.Core;
using SunsetCurse.World;
using SunsetCurse.Audio;
using SunsetCurse.UI;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Fills the river bucket. Runs entirely on the LOCAL player's machine (a scene component,
    /// not a player component — it always operates on <see cref="PlayerInventory.Local"/>):
    ///
    ///   • Carrying the EMPTY bucket + standing at least 2m in from the river bank, actually in
    ///     the water → a "Hold X" hint shows.
    ///   • HOLD X for 5 seconds → a sky-blue radial circle fills 0–100% at screen centre.
    ///   • Releasing X, wading back toward the bank, going down, or losing the bucket
    ///     INTERRUPTS and resets the progress to zero.
    ///   • On completion the bucket flips to FILLED — an owner-write NetworkVariable on
    ///     PlayerInventory, so every peer (and the host's ritual validation) sees it.
    ///
    /// SETUP: lives on the "RiverBucket" GameObject (Tools ▸ Sunset Curse ▸ 24). Drop the two
    /// SFX clips in whenever you have them — everything is null-guarded.
    /// </summary>
    public class RiverBucketFiller : MonoBehaviour
    {
        [Header("Filling")]
        [Tooltip("Key to HOLD to fill the bucket. X is unused by every other system.")]
        [SerializeField] private Key fillKey = Key.X;
        [Tooltip("How long X must be held, in seconds, without interruption.")]
        [SerializeField] private float holdSeconds = 5f;
        [Tooltip("The player must be at least this many metres (horizontally) in from the " +
                 "nearest river bank — dipping a toe at the edge doesn't count.")]
        [SerializeField] private float minBankDistance = 2f;

        [Header("River SFX — drop clips here")]
        [Tooltip("One-shot splash when the bucket finishes filling. Leave empty until you have " +
                 "a clip — nothing errors while unassigned.")]
        [SerializeField] private AudioClip bucketFillSfx;
        [Tooltip("Loop that plays WHILE the bucket is being filled (water sloshing). Stops the " +
                 "moment the channel is interrupted or completes.")]
        [SerializeField] private AudioClip bucketFillingSfx;

        // Channel state.
        private float holdTimer;
        private bool channeling;

        // Cached per-player lookups.
        private PlayerInventory cachedInv;
        private PlayerStats cachedStats;

        // Code-built UI: sky-blue radial ring + percentage label at screen centre.
        private GameObject uiRoot;
        private Image ringFill;
        private TMP_Text percentLabel;

        // Runtime source for the filling loop (2D, SFX bus) — created on first use.
        private AudioSource fillingSource;

        private void Update()
        {
            var inv = PlayerInventory.Local;
            var river = RiverFlowController.Instance;

            if (inv == null || river == null) { Cancel(); return; }
            if (cachedInv != inv) { cachedInv = inv; cachedStats = inv.GetComponent<PlayerStats>(); }

            // Only while actually playing: alive, standing, and not in a menu (cursor locked).
            bool controllable = inv.IsAlive
                             && (cachedStats == null || !cachedStats.IsDowned)
                             && Cursor.lockState == CursorLockMode.Locked;

            Vector3 pos = inv.transform.position;
            bool deepEnough = controllable
                           && inv.Bucket == BucketState.Empty
                           && river.IsInWater(pos, minBankDistance);

            if (!deepEnough)
            {
                Cancel();
                // Gentle nudge when they're in the water with the empty bucket but too close to
                // the bank (re-shown per frame while it applies — the PlayerClimb prompt pattern).
                if (controllable && inv.Bucket == BucketState.Empty && river.IsInWater(pos))
                    ScreenMessage.Show("Wade deeper into the river to fill the bucket...", 0.5f);
                return;
            }

            bool held = Keyboard.current != null && Keyboard.current[fillKey].isPressed;

            if (!channeling)
            {
                ScreenMessage.Show($"Hold {fillKey} to fill the bucket from the river", 0.5f);
                if (held) Begin();
                return;
            }

            if (!held) { Cancel(); return; }

            holdTimer += Time.deltaTime;
            float t = Mathf.Clamp01(holdTimer / Mathf.Max(0.01f, holdSeconds));
            if (ringFill != null) ringFill.fillAmount = t;
            if (percentLabel != null) percentLabel.text = $"Filling the bucket…  {Mathf.RoundToInt(t * 100f)}%";

            if (holdTimer >= holdSeconds) Complete(inv);
        }

        private void Begin()
        {
            channeling = true;
            holdTimer = 0f;
            EnsureUI();
            uiRoot.SetActive(true);
            if (ringFill != null) ringFill.fillAmount = 0f;
            if (percentLabel != null) percentLabel.text = "Filling the bucket…  0%";

            if (bucketFillingSfx != null)
            {
                EnsureFillingSource();
                fillingSource.clip = bucketFillingSfx;
                fillingSource.Play();
            }
        }

        private void Cancel()
        {
            if (!channeling) return;
            channeling = false;
            holdTimer = 0f;   // interrupted → progress RESETS, next attempt starts from zero
            if (uiRoot != null) uiRoot.SetActive(false);
            if (fillingSource != null && fillingSource.isPlaying) fillingSource.Stop();
        }

        private void Complete(PlayerInventory inv)
        {
            Cancel();
            inv.SetBucket(BucketState.Filled);   // owner-write → replicates to every peer

            if (bucketFillSfx != null)
            {
                if (AudioManager.Instance != null) AudioManager.Instance.PlaySfx2D(bucketFillSfx);
                else { EnsureFillingSource(); fillingSource.PlayOneShot(bucketFillSfx); }
            }
            ScreenMessage.Show("The bucket brims with river water — bring it to the ritual.", 4f);
        }

        private void EnsureFillingSource()
        {
            if (fillingSource != null) return;
            fillingSource = gameObject.AddComponent<AudioSource>();
            fillingSource.playOnAwake = false;
            fillingSource.loop = true;
            fillingSource.spatialBlend = 0f;   // your own hands — always audible, local only
            if (AudioManager.Instance != null && AudioManager.Instance.SfxGroup != null)
                fillingSource.outputAudioMixerGroup = AudioManager.Instance.SfxGroup;
        }

        // ── Code-built radial UI (same pattern as PlayerInteractor's harvest circle) ─────────
        private void EnsureUI()
        {
            if (uiRoot != null) return;

            var canvas = UIHelpers.NewCanvas("BucketFill_Canvas", 95, transform);

            uiRoot = new GameObject("FillProgress", typeof(RectTransform));
            uiRoot.transform.SetParent(canvas.transform, false);
            var root = (RectTransform)uiRoot.transform;
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(120f, 120f);

            // Faint full ring so the player sees how much circle remains.
            var backGO = new GameObject("RingBack", typeof(RectTransform), typeof(Image));
            backGO.transform.SetParent(uiRoot.transform, false);
            StretchToParent((RectTransform)backGO.transform);
            var back = backGO.GetComponent<Image>();
            back.sprite = UIHelpers.GetRingSprite();
            back.color = new Color(1f, 1f, 1f, 0.18f);
            back.raycastTarget = false;

            // The filling ring — SKY BLUE, radial 0→100%.
            var fillGO = new GameObject("RingFill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(uiRoot.transform, false);
            StretchToParent((RectTransform)fillGO.transform);
            ringFill = fillGO.GetComponent<Image>();
            ringFill.sprite = UIHelpers.GetRingSprite();
            ringFill.color = new Color(0.45f, 0.80f, 1f, 0.95f);
            ringFill.raycastTarget = false;
            ringFill.type = Image.Type.Filled;
            ringFill.fillMethod = Image.FillMethod.Radial360;
            ringFill.fillOrigin = (int)Image.Origin360.Top;
            ringFill.fillClockwise = true;
            ringFill.fillAmount = 0f;

            percentLabel = UIHelpers.NewLabel(uiRoot.transform, "", 24,
                new Vector2(0f, -84f), new Vector2(600f, 34f),
                new Color(0.85f, 0.94f, 1f), TextAlignmentOptions.Center, FontStyles.Bold);

            uiRoot.SetActive(false);
        }

        private static void StretchToParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
