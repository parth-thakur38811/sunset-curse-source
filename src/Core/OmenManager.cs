using System.Collections;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Audio;

namespace SunsetCurse.Core
{
    /// <summary>An omen bends the rules of a single night.</summary>
    public enum Omen { None = 0, BloodMoon = 1, HouseWatched = 2, Downpour = 3, RestlessDead = 4 }

    /// <summary>
    /// Rolls a random OMEN at the start of each night so no two nights play the same. Host-
    /// authoritative + replicated: the host picks the omen on <see cref="GameClock.OnNightStart"/>,
    /// writes it to a NetworkVariable, and every peer reacts (announcement + local effects).
    /// Systems read the omen live via the static <c>Is…</c> helpers.
    ///
    ///   • BLOOD MOON      — both monsters move faster (relative to the player, scaled by difficulty).
    ///   • HOUSE WATCHED   — the SafeHouse gives no protection tonight.
    ///   • DOWNPOUR        — heavy rain (muffles noise both ways) + rain VFX/SFX.
    ///   • RESTLESS DEAD   — the jumpscare character strikes far more often.
    ///
    /// SETUP: one GameObject in the scene with a NetworkObject + this script. For DOWNPOUR, assign
    /// Rain Vfx Prefab (a rain particle system) + Rain Loop (a looping rain ambient clip).
    /// </summary>
    public class OmenManager : NetworkBehaviour
    {
        public static OmenManager Instance { get; private set; }

        [Header("Rolling")]
        [Tooltip("Chance a night HAS an omen at all (else it's an ordinary night).")]
        [Range(0f, 1f)] [SerializeField] private float omenChance = 0.75f;

        [Header("Downpour — rain (assign from the Rainy VFX pack)")]
        [Tooltip("Rain particle-system prefab — spawned following the local camera on a Downpour night.")]
        [SerializeField] private GameObject rainVfxPrefab;
        [Tooltip("Where the rain sits relative to the camera (above + ahead so it fills the view).")]
        [SerializeField] private Vector3 rainLocalOffset = new Vector3(0f, 6f, 4f);
        [Tooltip("Looping rainfall clip (…rain-moderate-a.wav), played on a Downpour night.")]
        [SerializeField] private AudioClip rainLoop;
        [Range(0f, 1f)] [SerializeField] private float rainVolume = 0.7f;
        [Tooltip("How much rain muffles movement noise (both the player's AND the monster's), 0..1.")]
        [Range(0.2f, 1f)] [SerializeField] private float rainNoiseMultiplier = 0.5f;

        [Header("Downpour — clouds")]
        [Tooltip("Cloud particle-system prefab — spawned high above the local camera during a Downpour.")]
        [SerializeField] private GameObject cloudVfxPrefab;
        [Tooltip("Height (and offset) of the clouds relative to the camera.")]
        [SerializeField] private Vector3 cloudLocalOffset = new Vector3(0f, 35f, 0f);

        [Header("Downpour — lightning + thunder")]
        [Tooltip("Lightning-flash prefab — spawned briefly in the sky at random intervals.")]
        [SerializeField] private GameObject lightningVfxPrefab;
        [Tooltip("Thunder clip (…thunder_close_boem.wav) — cracks shortly after each flash.")]
        [SerializeField] private AudioClip thunderSfx;
        [Range(0f, 1f)] [SerializeField] private float thunderVolume = 1f;
        [Tooltip("Random seconds between lightning strikes (x = min, y = max).")]
        [SerializeField] private Vector2 lightningInterval = new Vector2(9f, 26f);
        [Tooltip("Seconds after the flash before thunder cracks (light outruns sound).")]
        [SerializeField] private float thunderDelay = 0.8f;
        [Tooltip("How long a lightning flash lives before it's removed.")]
        [SerializeField] private float lightningLifetime = 1.5f;

        // ── Networked state ──
        private readonly NetworkVariable<int> netOmen = new NetworkVariable<int>(0);

        public static Omen Current => Instance != null ? (Omen)Instance.netOmen.Value : Omen.None;
        public static bool IsBloodMoon    => Current == Omen.BloodMoon;
        public static bool IsHouseWatched => Current == Omen.HouseWatched;
        public static bool IsDownpour     => Current == Omen.Downpour;
        public static bool IsRestlessDead => Current == Omen.RestlessDead;

        // Local per-peer downpour effect handles.
        private GameObject rainVfxInstance;
        private GameObject cloudVfxInstance;
        private AudioSource rainSource;
        private Coroutine lightningCo;
        private bool weatherSetByOmen;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // A second copy of this manager is in the scene. Destroy(this) would rip a
                // NetworkBehaviour off a NetworkObject, which Netcode doesn't support (it shifts
                // the behaviour indices, so RPCs can land on the wrong component). Disable this
                // copy instead and shout, so the duplicate gets deleted from the scene.
                Debug.LogError($"[{GetType().Name}] Duplicate in the scene - only one is allowed. " +
                               "This copy is disabled; delete it.", this);
                enabled = false;
                return;
            }
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            if (Instance != this) return;   // disabled duplicate (see Awake) - stay inert
            netOmen.OnValueChanged += HandleOmenChanged;
            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart += RollForNight;
                GameClock.Instance.OnDayStart   += ClearForDay;
            }
            if (netOmen.Value != 0) HandleOmenChanged(0, netOmen.Value);   // late joiner
        }

        public override void OnNetworkDespawn()
        {
            netOmen.OnValueChanged -= HandleOmenChanged;
            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart -= RollForNight;
                GameClock.Instance.OnDayStart   -= ClearForDay;
            }
        }

        // ── Server: pick the night's omen ──
        private void RollForNight(int day)
        {
            Omen picked = Omen.None;
            if (Random.value < omenChance)
                picked = (Omen)Random.Range(1, 5);   // 1..4 inclusive
            netOmen.Value = (int)picked;
            Debug.Log($"[OmenManager] Night {day}: omen = {picked}.");

            // Announce via a GUARANTEED ClientRpc (reliable delivery to EVERY connected peer,
            // including the host). The NetworkVariable callback below drives the LOCAL effects
            // (rain) + late-join sync, but its OnValueChanged can miss a peer that isn't subscribed
            // at the exact tick — which is why the message reached "only some" players before.
            if (picked != Omen.None)
            {
                if (IsSpawned) AnnounceOmenClientRpc((int)picked);
                else           ShowAnnouncement(picked);   // offline single-player
            }
        }

        private void ClearForDay(int day) => netOmen.Value = (int)Omen.None;

        [ClientRpc]
        private void AnnounceOmenClientRpc(int omen) => ShowAnnouncement((Omen)omen);

        private static void ShowAnnouncement(Omen omen)
        {
            string msg = omen switch
            {
                Omen.BloodMoon    => "BLOOD MOON — she runs faster tonight. Don't get caught.",
                Omen.HouseWatched => "The house is watched — the SafeHouse is NOT safe tonight!",
                Omen.Downpour     => "It's stormy tonight!",
                Omen.RestlessDead => "The spider is unhappy tonight...",
                _ => null
            };
            if (!string.IsNullOrEmpty(msg)) SunsetCurse.UI.ScreenMessage.Show(msg, 5f);
        }

        // ── Every peer: local effects (rain), driven by the replicated state (also handles late join) ──
        private void HandleOmenChanged(int prev, int next)
        {
            ApplyDownpour((Omen)next == Omen.Downpour);
        }

        private void ApplyDownpour(bool on)
        {
            if (on)
            {
                Weather.Set(raining: true, noiseMultiplier: rainNoiseMultiplier);
                weatherSetByOmen = true;

                var cam = Camera.main;
                if (rainVfxPrefab != null && rainVfxInstance == null && cam != null)
                {
                    rainVfxInstance = Instantiate(rainVfxPrefab, cam.transform);
                    rainVfxInstance.transform.localPosition = rainLocalOffset;
                    rainVfxInstance.transform.localRotation = Quaternion.identity;
                }
                if (cloudVfxPrefab != null && cloudVfxInstance == null && cam != null)
                {
                    cloudVfxInstance = Instantiate(cloudVfxPrefab, cam.transform);
                    cloudVfxInstance.transform.localPosition = cloudLocalOffset;
                    cloudVfxInstance.transform.localRotation = Quaternion.identity;
                }
                if (rainLoop != null)
                {
                    if (rainSource == null)
                    {
                        rainSource = gameObject.AddComponent<AudioSource>();
                        rainSource.loop = true;
                        rainSource.spatialBlend = 0f;
                        rainSource.clip = rainLoop;
                        if (AudioManager.Instance != null) rainSource.outputAudioMixerGroup = AudioManager.Instance.SfxGroup;
                    }
                    rainSource.volume = rainVolume;
                    if (!rainSource.isPlaying) rainSource.Play();
                }
                if (lightningVfxPrefab != null && lightningCo == null)
                    lightningCo = StartCoroutine(LightningLoop());
            }
            else
            {
                if (weatherSetByOmen) { Weather.Set(raining: false, noiseMultiplier: 1f); weatherSetByOmen = false; }
                if (rainVfxInstance != null)  { Destroy(rainVfxInstance);  rainVfxInstance = null; }
                if (cloudVfxInstance != null) { Destroy(cloudVfxInstance); cloudVfxInstance = null; }
                if (rainSource != null && rainSource.isPlaying) rainSource.Stop();
                if (lightningCo != null) { StopCoroutine(lightningCo); lightningCo = null; }
            }
        }

        /// <summary>Flashes lightning in the sky near the player at random intervals during a
        /// Downpour, each followed by a thunder crack (light outruns sound → short delay).</summary>
        private IEnumerator LightningLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(Random.Range(lightningInterval.x, lightningInterval.y));
                var cam = Camera.main;
                if (cam == null) continue;

                // High, slightly-random spot near the player so the flash reads as sky lightning.
                Vector2 r = Random.insideUnitCircle * 35f;
                Vector3 pos = cam.transform.position + new Vector3(r.x, 30f, r.y);

                if (lightningVfxPrefab != null)
                {
                    var flash = Instantiate(lightningVfxPrefab, pos, Quaternion.identity);
                    Destroy(flash, lightningLifetime);
                }
                if (thunderSfx != null) StartCoroutine(ThunderAfter(thunderDelay, pos));
            }
        }

        private IEnumerator ThunderAfter(float delay, Vector3 pos)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            if (thunderSfx != null && AudioManager.Instance != null)
                AudioManager.Instance.PlaySfx3D(thunderSfx, pos, thunderVolume, 300f);
        }

        // ── Blood Moon speed maths (both monsters call this) ──
        // Reference speeds match the PlayerCapsule FirstPersonController (walk 8 / run 12). If you
        // change the player's speed on the prefab, update these two constants to keep the tuning.
        private const float PlayerWalk = 8f;
        private const float PlayerRun  = 12f;

        // Per-difficulty factors chosen so the monster lands where the design wants RELATIVE to the
        // player: EASY still slower than you, MEDIUM roughly matched/slightly faster, HARD much faster.
        private static float RunFactor(Difficulty d)  => d == Difficulty.Hard ? 1.42f : d == Difficulty.Medium ? 1.08f : 0.92f;
        private static float WalkFactor(Difficulty d) => d == Difficulty.Hard ? 1.35f : d == Difficulty.Medium ? 1.05f : 0.90f;

        public static float BloodMoonRunSpeed()  => PlayerRun  * RunFactor(DifficultyPreference.Selected);
        public static float BloodMoonWalkSpeed() => PlayerWalk * WalkFactor(DifficultyPreference.Selected);

        /// <summary>On a Blood Moon night, raise a monster's speed to at least the omen target
        /// (walk or run). Off a Blood Moon it returns the base speed unchanged.</summary>
        public static float BloodMoonFloor(float baseSpeed, bool isRun)
            => IsBloodMoon ? Mathf.Max(baseSpeed, isRun ? BloodMoonRunSpeed() : BloodMoonWalkSpeed())
                           : baseSpeed;
    }
}
