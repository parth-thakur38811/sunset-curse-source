using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
using SunsetCurse.Core;

namespace SunsetCurse.Player
{
    /// <summary>
    /// The player's survival stats: HEALTH and HUNGER.
    ///
    /// RULES (current design):
    ///   - DAYTIME: the hunger bar shrinks (it refills full each dawn); HEALTH never drops by day.
    ///   - EATING berries (any time, day or night) LOCKS the hunger bar — it stops draining and
    ///     stays put, including through the night. (Eating also tops the bar up.)
    ///   - If you DON'T eat, hunger keeps shrinking — through the night too — until it hits zero.
    ///   - HEALTH only drains once hunger reaches ZERO at night: a fixed % every few seconds
    ///     (default 3% every 2s). Eat to lock the bar above zero and it stops.
    ///   - Movement speed scales DOWN as health drops (see SpeedMultiplierForHealth).
    ///
    /// Listens to the <see cref="GameClock"/> for dawn (refill hunger) and dusk (empty hunger →
    /// start the night health drain).
    ///
    /// SETUP: put this on the player (the PlayerCapsule that has the FirstPersonController).
    /// </summary>
    public class PlayerStats : MonoBehaviour
    {
        [Header("Health")]
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float startingHealth = 100f;

        [Header("Respawn & fall recovery")]
        [Tooltip("Respawn Potion drops you at least this far from the monster (metres).")]
        [SerializeField] private float respawnSafeDistance = 60f;
        [Tooltip("Seconds of invulnerability right after a Respawn Potion fires — stops the monster " +
                 "re-killing you in the split second before the teleport replicates.")]
        [SerializeField] private float respawnInvulnSeconds = 2f;
        [Tooltip("If the player falls this many metres BELOW the last solid ground they stood on " +
                 "(fell through / off the map edge), teleport them back. RELATIVE on purpose — an " +
                 "absolute Y cutoff would instantly eject players from underground interiors like " +
                 "the haunted-house interior buried at y=-40.")]
        [SerializeField] private float fallRecoveryDrop = 30f;

        [Header("Downed & revive (co-op)")]
        [Tooltip("When health hits 0 and a STANDING teammate is still alive, you go DOWNED instead " +
                 "of dying — bleeding out for this many seconds. A teammate reviving you in time " +
                 "puts you back up; otherwise it's a real death. Solo (or last one standing) = " +
                 "instant death, exactly like before.")]
        [SerializeField] private float downedBleedOutSeconds = 60f;
        [Tooltip("Health (% of max) you stand back up with after being revived.")]
        [SerializeField] private float reviveHealthPercent = 35f;
        [Tooltip("Seconds a teammate must stay next to your body to revive you (their E-channel).")]
        [SerializeField] private float reviveChannelSeconds = 5f;
        [Tooltip("How close the reviving teammate must stay, in metres.")]
        [SerializeField] private float reviveRange = 3f;
        [Tooltip("How far the first-person camera drops while downed (eye height while collapsed).")]
        [SerializeField] private float downedCameraHeight = 0.7f;
        [Tooltip("Seconds the view takes to TURN and face the monster that downed you, before the " +
                 "collapse — same beat as the death cam. 0 = skip the turn.")]
        [SerializeField] private float downedTurnSeconds = 0.6f;
        [Tooltip("Seconds the collapse takes (the eye sinking to Downed Camera Height) after the turn.")]
        [SerializeField] private float downedCollapseSeconds = 1.1f;
        [Tooltip("How far the view rolls onto its side while collapsed, in degrees.")]
        [SerializeField] private float downedRollDegrees = 22f;
        [Tooltip("Mouse-look sensitivity while downed — WASD is dead, but you can look around for " +
                 "your rescuer.")]
        [SerializeField] private float downedLookSensitivity = 1f;

        [Header("Nighttime health drain (starts once hunger hits zero at sunset)")]
        [Tooltip("Health lost each tick, as a % of max health.")]
        [SerializeField] private float nightHealthLossPercent = 3f;
        [Tooltip("How often that loss is applied, in seconds.")]
        [SerializeField] private float nightHealthLossInterval = 2f;

        [Header("Hunger (the daytime satiation bar)")]
        [SerializeField] private float maxHunger = 100f;
        [Tooltip("How fast the hunger bar empties, in points per second. Tune so an UN-eaten bar " +
                 "lasts through the day and drains to zero partway into the night.")]
        [SerializeField] private float hungerDrainPerSecond = 100f / 450f;
        [Tooltip("Hunger restored by eating one fruit/berry.")]
        [SerializeField] private float foodValue = 35f;

        [Header("Movement link (auto-found if left empty)")]
        [Tooltip("The StarterAssets FirstPersonController. Found automatically by looking for a " +
                 "component that has MoveSpeed/SprintSpeed fields.")]
        [SerializeField] private MonoBehaviour movementController;

        // --- Events other systems / the HUD listen to (values are normalized 0..1) ---
        public event Action<float> OnHealthChanged;
        public event Action<float> OnHungerChanged;
        public event Action OnStarvingChanged;   // toggles when the night health-drain starts/stops
        public event Action OnPlayerDied;

        // --- Public read-only state ---
        public float Health01 => Mathf.Clamp01(health / maxHealth);
        public float Hunger01 => Mathf.Clamp01(hunger / maxHunger);
        public bool IsAlive => !isDead;
        public bool IsStarving => isStarving;   // true while health is bleeding at night
        public bool IsDowned => isDowned;       // bleeding out, waiting for a teammate
        public float ReviveChannelSeconds => reviveChannelSeconds;   // read by ReviveTarget
        public float ReviveRange => reviveRange;

        private float health;
        private float hunger;
        private bool hungerLocked;   // true after eating: hunger stops draining until next dawn
        private bool isNight;
        private bool isStarving;
        private bool isDead;
        private bool subscribed;
        private float nightDrainTimer;

        // Downed state (owner machine only — replicated to peers via PlayerInventory.SetDowned).
        private bool isDowned;
        private float downedTimer;
        private float rescuerRecheckTimer;   // throttles the "is anyone left to save me?" scan
        private Transform cameraRoot;         // PlayerCameraRoot child — collapsed while downed
        private Vector3 cameraRootHome;
        private Quaternion cameraRootHomeRot;
        private bool cameraLowered;
        private Coroutine downedIntroCo;      // the turn-to-monster + collapse cinematic
        private bool downedIntroPlaying;
        private float downedPitch;            // camera pitch while downed (mouse look)

        // Downed UI (radial bleed-out ring + countdown + revive hint), built in code on first down.
        private GameObject downedUI;
        private Image downedFill;
        private TMP_Text downedCountdown;

        // Fall-recovery + post-respawn grace.
        private CharacterController characterController;
        private Vector3 lastSafePos;
        private float invulnerableUntil;

        // Networked mirror of our stats so spectators can read them off this player.
        private Core.PlayerInventory playerInventory;

        // Reflection cache so we don't hard-depend on the StarterAssets assembly.
        private FieldInfo moveSpeedField, sprintSpeedField;
        private float baseMoveSpeed, baseSprintSpeed;

        private void Awake()
        {
            health = Mathf.Min(startingHealth, maxHealth);
            hunger = maxHunger;
            characterController = GetComponent<CharacterController>();
            playerInventory = GetComponent<Core.PlayerInventory>();
            lastSafePos = transform.position;
            CacheMovementSpeeds();
        }

        private void Start()
        {
            // Subscribe in Start (not OnEnable) so GameClock.Instance is guaranteed to exist.
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnDayStart += HandleDayStart;
                GameClock.Instance.OnNightStart += HandleNightStart;
                subscribed = true;

                // If we somehow start at night, reflect that immediately.
                isNight = GameClock.Instance.CurrentPhase == GameClock.Phase.Night;
            }
            else
            {
                Debug.LogWarning("[PlayerStats] No GameClock found — day/night stat timing disabled.", this);
            }

            ApplySpeedForHealth();
            OnHealthChanged?.Invoke(Health01);
            OnHungerChanged?.Invoke(Hunger01);
        }

        private void OnDestroy()
        {
            if (subscribed && GameClock.Instance != null)
            {
                GameClock.Instance.OnDayStart -= HandleDayStart;
                GameClock.Instance.OnNightStart -= HandleNightStart;
            }
        }

        private void Update()
        {
            // Publish our stats to the networked inventory EVERY frame (incl. while dead → 0) so a
            // spectator watching this player sees the real bars, not their own 0-health HUD.
            if (playerInventory != null) playerInventory.PublishStats(Health01, Hunger01);

            if (isDead) return;

            // DOWNED: no hunger drain, no starvation — just the bleed-out clock. A teammate's
            // revive (ReviveTarget → PlayerHealthSync → TriggerRevive) is the only way back up.
            if (isDowned)
            {
                // "Can anyone still save me?" — checked EVEN DURING the intro cinematic, so a
                // hopeless downed state (last teammate died a beat later, or we went down on a
                // stale snapshot of a teammate's flags) converts to a real death within half a
                // second instead of a pointless 60s bleed-out. Timer-throttled (scans all players).
                rescuerRecheckTimer -= Time.deltaTime;
                if (rescuerRecheckTimer <= 0f)
                {
                    rescuerRecheckTimer = 0.5f;
                    if (!HasStandingTeammate())
                    {
                        SunsetCurse.UI.ScreenMessage.Show("No one is left to help you...", 3f);
                        FinalDeath();
                        return;
                    }
                }

                // The turn-to-monster + collapse cinematic plays first (DownedIntro coroutine);
                // the bleed-out clock, the revive UI and mouse-look only start once it's done.
                if (downedIntroPlaying) return;

                DownedLook();   // WASD is dead (controller disabled) but you can look around

                downedTimer -= Time.deltaTime;
                UpdateDownedUI();   // radial ring depletes + the countdown number ticks
                if (downedTimer <= 0f) FinalDeath();
                return;
            }

            // Remember the last solid ground we stood on, and yank us back if we fall off the world.
            if (characterController != null && characterController.enabled && characterController.isGrounded)
                lastSafePos = transform.position;
            if (transform.position.y < lastSafePos.y - fallRecoveryDrop) { RecoverFromFall(); return; }

            // Hunger empties over time — UNLESS eating has locked the bar.
            if (!hungerLocked && hunger > 0f)
            {
                hunger = Mathf.Max(0f, hunger - hungerDrainPerSecond * Time.deltaTime);
                OnHungerChanged?.Invoke(Hunger01);
            }

            // Health ONLY bleeds at night, and only once hunger is empty.
            bool bleeding = isNight && hunger <= 0f;
            if (bleeding != isStarving)
            {
                isStarving = bleeding;
                OnStarvingChanged?.Invoke();
            }

            if (bleeding)
            {
                nightDrainTimer += Time.deltaTime;
                if (nightDrainTimer >= nightHealthLossInterval)
                {
                    nightDrainTimer -= nightHealthLossInterval;
                    ChangeHealth(-(nightHealthLossPercent / 100f) * maxHealth);   // e.g. -3% of max
                }
            }
            else
            {
                nightDrainTimer = 0f;   // fresh full interval needed next time hunger empties
            }
        }

        // ---------------------------------------------------------------- public actions

        /// <summary>Eat a custom amount of food (a bush/fruit calls this).</summary>
        public void Eat(float amount)
        {
            if (isDead || isDowned) return;
            hunger = Mathf.Min(maxHunger, hunger + amount);
            hungerLocked = true;        // eating LOCKS the bar — it stops draining (incl. at night)
            OnHungerChanged?.Invoke(Hunger01);
        }

        /// <summary>Eat one standard fruit/berry.</summary>
        public void EatFood() => Eat(foodValue);

        /// <summary>Take damage (a monster calls this).</summary>
        public void TakeDamage(float amount)
        {
            if (isDead || isDowned || amount <= 0f) return;   // downed players can't be hit again
            if (Time.time < invulnerableUntil) return;   // brief post-respawn grace window
            ChangeHealth(-amount);
        }

        public void Heal(float amount)
        {
            if (isDead || isDowned || amount <= 0f) return;   // bandages can't fix downed — revive only
            ChangeHealth(amount);
        }

        // ---------------------------------------------------------------- internals

        private void ChangeHealth(float delta)
        {
            health = Mathf.Clamp(health + delta, 0f, maxHealth);
            OnHealthChanged?.Invoke(Health01);
            ApplySpeedForHealth();

            if (health <= 0f && !isDead && !isDowned)
            {
                // 1) Respawn Potion intercept — if the local player has one, consume it and revive
                //    them at a safe distance from the monster INSTEAD of triggering the death flow.
                var inv = Core.PlayerInventory.Local;
                Debug.Log($"[PlayerStats] Fatal hit. PlayerInventory.Local={(inv != null ? "OK" : "NULL")}, " +
                          $"HasRespawnPotion={(inv != null && inv.HasRespawnPotion)}, " +
                          $"Potions={(inv != null ? inv.RespawnPotions : -1)}.");
                if (inv != null && inv.HasRespawnPotion && inv.TryConsumeRespawnPotion())
                {
                    Debug.Log("[PlayerStats] Respawn Potion CONSUMED — triggering respawn.");
                    TriggerRespawn();
                    return;
                }

                // 2) DOWNED instead of dead — but only if someone can still come save us. Solo,
                //    or when every teammate is already dead/downed, it's a real death (no rescuer).
                if (HasStandingTeammate())
                {
                    EnterDowned();
                    return;
                }

                // 3) Real death (the pre-downed flow, unchanged).
                FinalDeath();
            }
        }

        // ---------------------------------------------------------------- downed & revive

        /// <summary>Is any OTHER player alive AND on their feet (not downed)? Read off the
        /// networked PlayerInventory flags, so it's correct for remote players too.</summary>
        private bool HasStandingTeammate()
        {
            foreach (var inv in FindObjectsByType<Core.PlayerInventory>())
            {
                if (inv == null || inv == playerInventory) continue;
                if (inv.IsAlive && !inv.IsDowned) return true;
            }
            return false;
        }

        private void EnterDowned()
        {
            isDowned = true;
            isStarving = false;
            downedTimer = downedBleedOutSeconds;
            rescuerRecheckTimer = 0f;   // first "can anyone save me?" check fires immediately

            // No walking (recoverably disabled — unlike death). The camera plays a short
            // cinematic — turn to face the monster that got you, then collapse to the ground —
            // and ONLY THEN the bleed-out clock + revive UI + mouse-look start (see the
            // downedIntroPlaying gate in Update).
            if (movementController != null) movementController.enabled = false;
            if (downedIntroCo != null) StopCoroutine(downedIntroCo);
            downedIntroCo = StartCoroutine(DownedIntro());

            if (playerInventory != null) playerInventory.SetDowned(true);   // replicate to peers
            Debug.Log("[PlayerStats] DOWNED — bleeding out.", this);
        }

        /// <summary>A teammate finished their revive channel (routed to this machine by
        /// PlayerHealthSync). Stand back up with partial health.</summary>
        public void TriggerRevive()
        {
            if (isDead || !isDowned) return;

            isDowned = false;
            health = Mathf.Clamp(maxHealth * (reviveHealthPercent / 100f), 1f, maxHealth);
            invulnerableUntil = Time.time + respawnInvulnSeconds;   // brief grace, same as respawn

            if (downedIntroCo != null) { StopCoroutine(downedIntroCo); downedIntroCo = null; }
            downedIntroPlaying = false;
            HideDownedUI();

            if (movementController != null) movementController.enabled = true;
            RestoreCameraFromDowned();

            if (playerInventory != null) playerInventory.SetDowned(false);
            OnHealthChanged?.Invoke(Health01);
            ApplySpeedForHealth();
            SunsetCurse.UI.ScreenMessage.Show("You're back on your feet — stay with the group!", 3f);
            Debug.Log("[PlayerStats] REVIVED by a teammate.", this);
        }

        /// <summary>The actual, definitive death — from a hit with nobody left standing, or from
        /// bleeding out while downed. Runs the original death flow (spectator / game over).</summary>
        private void FinalDeath()
        {
            isDowned = false;
            isDead = true;
            isStarving = false;
            if (downedIntroCo != null) { StopCoroutine(downedIntroCo); downedIntroCo = null; }
            downedIntroPlaying = false;
            HideDownedUI();
            if (movementController != null) movementController.enabled = false;
            // (Camera stays where it is — GameOverController's death-cam takes over from here.)

            Debug.Log("[PlayerStats] The player has died.");

            // Radio-escape parts don't die with their carrier — they drop where the body fell,
            // for any teammate to recover (networked via RadioTowerState's replicated drop list).
            if (World.RadioTowerState.Instance != null)
                World.RadioTowerState.Instance.DropAllFromLocalInventory(transform.position);

            // Publish dead state to all peers via networked inventory flag (used by SpectatorMode +
            // by the all-dead game-over check). Only the local owner can write it (owner-auth).
            // ORDER MATTERS: alive=false must land BEFORE downed=false. The old order opened a
            // phantom window where remote peers briefly read "alive AND not downed" (= STANDING!)
            // on a player who was actually dying — a teammate killed inside that window went
            // DOWNED (waiting for a rescuer who didn't exist) instead of straight to game over.
            if (playerInventory != null)
            {
                playerInventory.SetAlive(false);
                playerInventory.SetDowned(false);
            }
            OnPlayerDied?.Invoke();
        }

        private bool EnsureCameraRoot()
        {
            if (cameraRoot != null) return true;
            var t = transform.Find("PlayerCameraRoot");
            if (t == null) return false;   // no camera root (remote copy) — nothing to drive
            cameraRoot = t;
            return true;
        }

        private void RestoreCameraFromDowned()
        {
            if (!cameraLowered || cameraRoot == null) return;
            cameraRoot.localPosition = cameraRootHome;
            cameraRoot.localRotation = cameraRootHomeRot;
            cameraLowered = false;
        }

        /// <summary>The downed cinematic: TURN to face the monster that got you, then COLLAPSE to
        /// the ground — the same beat as the death cam, but recoverable. Drives the body's yaw +
        /// PlayerCameraRoot (Cinemachine's follow target), so the normal camera pipeline stays
        /// intact for the revive. The bleed-out clock and revive UI start only when this ends.</summary>
        private IEnumerator DownedIntro()
        {
            downedIntroPlaying = true;

            if (EnsureCameraRoot() && !cameraLowered)
            {
                cameraRootHome = cameraRoot.localPosition;      // snapshot for the revive restore
                cameraRootHomeRot = cameraRoot.localRotation;
                cameraLowered = true;
            }

            // Whoever downed us is the nearest monster (main stalker OR the compound watcher) —
            // face her. If neither is around (e.g. starvation), skip the turn and just collapse.
            bool haveMonster = SunsetCurse.AI.MonsterRegistry.TryGetNearest(
                transform.position, out Vector3 monsterPos, out _);

            float startPitch = 0f;
            if (cameraRoot != null)
            {
                startPitch = cameraRoot.localEulerAngles.x;
                if (startPitch > 180f) startPitch -= 360f;      // Unity serves 350° for -10°
            }
            downedPitch = startPitch;

            // ── PHASE 1: TURN to face her (body yaw + camera pitch), staying upright. ──
            if (haveMonster && downedTurnSeconds > 0f && cameraRoot != null)
            {
                Vector3 to = (monsterPos + Vector3.up * 1.4f) - cameraRoot.position;   // her chest
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                float targetYaw = flat.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(flat).eulerAngles.y
                    : transform.eulerAngles.y;
                float targetPitch = -Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg;

                float startYaw = transform.eulerAngles.y;
                float t = 0f;
                while (t < downedTurnSeconds && isDowned)
                {
                    t += Time.deltaTime;
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / downedTurnSeconds));
                    transform.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(startYaw, targetYaw, k), 0f);
                    downedPitch = Mathf.Lerp(startPitch, targetPitch, k);
                    cameraRoot.localRotation = Quaternion.Euler(downedPitch, 0f, 0f);
                    yield return null;
                }
            }

            // ── PHASE 2: COLLAPSE — the eye sinks to the ground and rolls onto its side. ──
            if (cameraRoot != null)
            {
                Vector3 fromPos = cameraRoot.localPosition;
                Vector3 toPos = new Vector3(cameraRootHome.x, downedCameraHeight, cameraRootHome.z);
                float t = 0f;
                while (t < downedCollapseSeconds && isDowned)
                {
                    t += Time.deltaTime;
                    float k = Mathf.Clamp01(t / downedCollapseSeconds);
                    float eased = k * k;                        // accelerate, like falling
                    cameraRoot.localPosition = Vector3.Lerp(fromPos, toPos, eased);
                    cameraRoot.localRotation = Quaternion.Euler(
                        downedPitch, 0f, Mathf.Lerp(0f, downedRollDegrees, eased));
                    yield return null;
                }
                if (isDowned) cameraRoot.localPosition = toPos;
            }

            downedIntroPlaying = false;
            downedIntroCo = null;
            if (isDowned) ShowDownedUI();   // the bleed-out clock + revive hint start now
        }

        /// <summary>Free look while downed — movement is dead (the FirstPersonController is off),
        /// but the player can turn to watch for their rescuer. Drives body yaw + camera pitch the
        /// same way the controller would; the roll stays (you're lying on your side).</summary>
        private void DownedLook()
        {
            if (cameraRoot == null || Mouse.current == null) return;
            if (Cursor.lockState != CursorLockMode.Locked) return;   // paused / a menu is open

            Vector2 delta = Mouse.current.delta.ReadValue();
            if (delta.sqrMagnitude < 0.0001f) return;

            transform.Rotate(0f, delta.x * downedLookSensitivity * 0.12f, 0f);
            downedPitch = Mathf.Clamp(downedPitch - delta.y * downedLookSensitivity * 0.12f, -75f, 75f);
            cameraRoot.localRotation = Quaternion.Euler(downedPitch, 0f, downedRollDegrees);
        }

        // ------------------- Downed UI (radial bleed-out ring + countdown + hint) -------------------

        private void ShowDownedUI()
        {
            if (downedUI == null) BuildDownedUI();
            if (downedUI != null)
            {
                downedUI.SetActive(true);
                UpdateDownedUI();
            }
        }

        private void HideDownedUI()
        {
            if (downedUI != null) downedUI.SetActive(false);
        }

        private void UpdateDownedUI()
        {
            if (downedFill != null)
                downedFill.fillAmount = Mathf.Clamp01(downedTimer / Mathf.Max(1f, downedBleedOutSeconds));
            if (downedCountdown != null)
                downedCountdown.text = Mathf.Max(0, Mathf.CeilToInt(downedTimer)).ToString();
        }

        /// <summary>Built in code on the first down (owner-only — PlayerStats only runs on the
        /// local player): a red radial ring that DEPLETES over the bleed-out, the seconds left
        /// right below it, and the italic revive hint at the bottom-centre of the screen.</summary>
        private void BuildDownedUI()
        {
            var canvas = SunsetCurse.UI.UIHelpers.NewCanvas("DownedUI_Canvas", 180, transform);
            downedUI = canvas.gameObject;

            Color red = new Color(0.85f, 0.15f, 0.12f, 0.95f);

            // Faint full ring behind, bright depleting ring on top.
            MakeRing(canvas.transform, new Vector2(0f, 70f), 130f, new Color(1f, 1f, 1f, 0.13f));
            downedFill = MakeRing(canvas.transform, new Vector2(0f, 70f), 130f, red);
            downedFill.type = Image.Type.Filled;
            downedFill.fillMethod = Image.FillMethod.Radial360;
            downedFill.fillOrigin = (int)Image.Origin360.Top;
            downedFill.fillClockwise = true;
            downedFill.fillAmount = 1f;

            // The numerical countdown, right below the ring.
            downedCountdown = SunsetCurse.UI.UIHelpers.NewLabel(
                canvas.transform, "60", 46, new Vector2(0f, -20f), new Vector2(220f, 60f),
                red, TextAlignmentOptions.Center, FontStyles.Bold);

            // The italic revive hint, bottom-centre.
            var hint = SunsetCurse.UI.UIHelpers.NewLabel(
                canvas.transform, "You are downed by the monster, Ask teammate for revival!",
                30, Vector2.zero, new Vector2(1400f, 44f),
                new Color(0.95f, 0.9f, 0.85f, 0.95f),
                TextAlignmentOptions.Center, FontStyles.Italic);
            var hintRT = (RectTransform)hint.transform;
            hintRT.anchorMin = hintRT.anchorMax = new Vector2(0.5f, 0f);   // bottom-centre anchor
            hintRT.pivot = new Vector2(0.5f, 0f);
            hintRT.anchoredPosition = new Vector2(0f, 60f);
        }

        private static Image MakeRing(Transform parent, Vector2 pos, float size, Color color)
        {
            var go = new GameObject("Ring", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(size, size);
            var img = go.GetComponent<Image>();
            img.sprite = SunsetCurse.UI.UIHelpers.GetRingSprite();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Revive the player at a safe spot ≥60 m from the monster. Consumed Respawn
        /// Potion already deducted by caller. Restores full health.</summary>
        private void TriggerRespawn()
        {
            health = maxHealth;
            isDead = false;                    // explicit — just in case anything flipped it
            isStarving = false;
            hunger = maxHunger;                // give them a fresh hunger bar too
            invulnerableUntil = Time.time + respawnInvulnSeconds;   // can't be re-killed for a moment
            OnHealthChanged?.Invoke(Health01);
            OnHungerChanged?.Invoke(Hunger01);
            ApplySpeedForHealth();

            Vector3 safe = FindSafeRespawnPos();
            TeleportTo(safe);
            lastSafePos = safe;

            // Republish alive-state to all peers — flips spectator UI off for any teammate who
            // may have already opened a list with this player marked dead. SetAlive is owner-write.
            if (Core.PlayerInventory.Local != null) Core.PlayerInventory.Local.SetAlive(true);

            SunsetCurse.UI.ScreenMessage.Show("Respawn Potion activated. The curse pulls you back.", 3f);
            Debug.Log($"[PlayerStats] Respawned via Potion at {safe}.", this);
        }

        /// <summary>Dawn Teleport Potion (single-player): warp to a random point on the navmesh at
        /// least <paramref name="minDistance"/> metres away. Called by TeleportPotionUser on the
        /// owner's machine. Returns true if a valid spot was found and the teleport happened.</summary>
        public bool TeleportRandomOnNavmesh(float minDistance)
        {
            if (isDead || isDowned) return false;

            Vector3 origin = transform.position;
            for (int i = 0; i < 40; i++)
            {
                // A ring between minDistance and minDistance+60 so it's "far", never absurdly far.
                Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
                float dist = minDistance + UnityEngine.Random.Range(0f, 60f);
                Vector3 candidate = origin + new Vector3(dir.x, 0f, dir.y) * dist;

                // Snap the candidate onto the navmesh (the surface players + monsters share).
                if (!NavMesh.SamplePosition(candidate, out var hit, 12f, NavMesh.AllAreas)) continue;
                if (Vector3.Distance(hit.position, origin) < minDistance) continue;   // sample pulled it too close

                TeleportTo(hit.position + Vector3.up * 0.5f);
                lastSafePos = hit.position + Vector3.up * 0.5f;
                invulnerableUntil = Time.time + respawnInvulnSeconds;
                SunsetCurse.UI.ScreenMessage.Show("The potion tears you across the forest.", 2.5f);
                return true;
            }
            SunsetCurse.UI.ScreenMessage.Show("The teleport fizzles — nowhere far enough to land.", 2.5f);
            return false;
        }

        /// <summary>Warp the player reliably: disable the CharacterController (it fights transform
        /// writes) and use NetworkTransform.Teleport so the owner-authoritative move replicates as a
        /// SNAP, not a slide across the map. Inventory + stats are untouched. Shared by the Respawn
        /// Potion and the fall-recovery path.</summary>
        private void TeleportTo(Vector3 pos)
        {
            var cc = characterController != null ? characterController : GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            var netTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
            if (netTransform != null && netTransform.IsSpawned)
                netTransform.Teleport(pos, transform.rotation, transform.localScale);
            else
                transform.position = pos;

            if (cc != null) cc.enabled = true;
            Physics.SyncTransforms();          // keep the CC's internal state in sync with the new pos
        }

        /// <summary>Player fell off / through the map — put them back on the last solid ground they
        /// stood on. They keep everything (inventory is networked + separate from this component).</summary>
        private void RecoverFromFall()
        {
            Vector3 target = lastSafePos + Vector3.up * 0.5f;
            TeleportTo(target);
            SunsetCurse.UI.ScreenMessage.Show("You slipped off the path — pulled back to solid ground.", 2.5f);
            Debug.Log($"[PlayerStats] Fall recovery → {target}.", this);
        }

        private Vector3 FindSafeRespawnPos()
        {
            // There can be MORE THAN ONE monster now (scent stalker + compound watcher) — the
            // respawn spot must be a safe distance from ALL of them, whichever one made the kill.
            var monsterPositions = new System.Collections.Generic.List<Vector3>();
            foreach (var m in FindObjectsByType<SunsetCurse.AI.MonsterAI>())
                if (m != null) monsterPositions.Add(m.transform.position);
            foreach (var m in FindObjectsByType<SunsetCurse.AI.CompoundWatcherAI>())
                if (m != null) monsterPositions.Add(m.transform.position);

            Vector3 origin = transform.position;

            // Try up to 30 random candidates within a 120m radius of where we died; accept the
            // first that's on the navmesh-ish (raycast-down hit) AND far enough from EVERY monster.
            for (int i = 0; i < 30; i++)
            {
                Vector2 r = UnityEngine.Random.insideUnitCircle * 120f;
                Vector3 candidate = origin + new Vector3(r.x, 0f, r.y);
                if (!Physics.Raycast(candidate + Vector3.up * 200f, Vector3.down, out var hit, 500f,
                                     ~0, QueryTriggerInteraction.Ignore))
                    continue;
                Vector3 ground = hit.point + Vector3.up * 0.5f;
                bool tooClose = false;
                foreach (var mp in monsterPositions)
                    if (Vector3.Distance(ground, mp) < respawnSafeDistance) { tooClose = true; break; }
                if (tooClose) continue;
                return ground;
            }

            // Fallback: push directly away from the NEAREST monster (the one that got us).
            Vector3 nearest = Vector3.zero;
            float best = float.MaxValue;
            foreach (var mp in monsterPositions)
            {
                float d = Vector3.Distance(origin, mp);
                if (d < best) { best = d; nearest = mp; }
            }
            Vector3 away = origin - nearest;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            return nearest + away.normalized * (respawnSafeDistance + 5f) + Vector3.up * 0.5f;
        }

        private void HandleDayStart(int day)
        {
            // Dawn: safe again. Hunger refills, UNLOCKS, and shrinks through the day until you eat.
            isNight = false;
            hungerLocked = false;
            nightDrainTimer = 0f;
            hunger = maxHunger;
            OnHungerChanged?.Invoke(Hunger01);
        }

        private void HandleNightStart(int day)
        {
            // Sunset: night begins. Hunger is NOT reset — if you ate today it's locked and stays
            // put; otherwise it keeps draining, and once it hits zero health starts to bleed.
            isNight = true;
            nightDrainTimer = 0f;
            Debug.Log("[PlayerStats] Night fell. " + (hungerLocked
                ? "Hunger is locked — you're safe."
                : "Hunger unlocked — it'll bleed health once it empties."));
        }

        // ---- Speed scaling (uses reflection so this script still compiles even if the
        //      StarterAssets controller is renamed/removed) ----

        private void CacheMovementSpeeds()
        {
            if (movementController == null)
            {
                foreach (var mb in GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var t = mb.GetType();
                    if (t.GetField("MoveSpeed") != null && t.GetField("SprintSpeed") != null)
                    {
                        movementController = mb;
                        break;
                    }
                }
            }

            if (movementController == null)
            {
                Debug.LogWarning("[PlayerStats] No movement controller with MoveSpeed/SprintSpeed " +
                                 "found — health-based speed scaling is disabled.", this);
                return;
            }

            var type = movementController.GetType();
            moveSpeedField = type.GetField("MoveSpeed");
            sprintSpeedField = type.GetField("SprintSpeed");
            if (moveSpeedField != null) baseMoveSpeed = (float)moveSpeedField.GetValue(movementController);
            if (sprintSpeedField != null) baseSprintSpeed = (float)sprintSpeedField.GetValue(movementController);
        }

        private void ApplySpeedForHealth()
        {
            if (movementController == null) return;
            float mult = SpeedMultiplierForHealth(health);
            if (moveSpeedField != null) moveSpeedField.SetValue(movementController, baseMoveSpeed * mult);
            if (sprintSpeedField != null) sprintSpeedField.SetValue(movementController, baseSprintSpeed * mult);
        }

        /// <summary>
        /// Maps health (0..100) to a movement-speed multiplier. Hits the design's exact
        /// points and interpolates smoothly between them:
        ///   100% -> 1.00x,  75% -> 0.90x,  60% -> 0.75x,  50% (and below) -> 0.60x.
        /// </summary>
        private float SpeedMultiplierForHealth(float h)
        {
            if (h >= 100f) return 1.00f;
            if (h >= 75f)  return Mathf.Lerp(0.90f, 1.00f, (h - 75f) / 25f);
            if (h >= 60f)  return Mathf.Lerp(0.75f, 0.90f, (h - 60f) / 15f);
            if (h >= 50f)  return Mathf.Lerp(0.60f, 0.75f, (h - 50f) / 10f);
            return 0.60f;
        }

        // ---- Debug helpers: right-click the component header in Play mode to test ----
        [ContextMenu("DEBUG / Eat one food (locks hunger)")] private void DebugEat() => EatFood();
        [ContextMenu("DEBUG / Take 20 damage")] private void DebugHurt() => TakeDamage(20f);
        [ContextMenu("DEBUG / Force night")] private void DebugNight() => HandleNightStart(0);
        [ContextMenu("DEBUG / Empty hunger now")] private void DebugEmptyHunger()
        { hungerLocked = false; hunger = 0f; OnHungerChanged?.Invoke(Hunger01); }
    }
}
