using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using SunsetCurse.Core;
using SunsetCurse.Player;
using SunsetCurse.UI;

namespace SunsetCurse.AI
{
    /// <summary>
    /// The night stalker. Designed for CONSTANT dread, not occasional encounters.
    ///
    /// SENSES (in order):
    ///   • CLOSE-RANGE SCENT — within <see cref="closeRangeScentRadius"/> she ALWAYS knows where
    ///     you are. No walls, no foliage, no view cone needed. Hiding 3m away behind a tree
    ///     while she's looking the other way will NOT save you.
    ///   • SIGHT — within <see cref="detectRadius"/>, inside her view cone, with a clear LOS.
    ///   • HEARING — within the player's noise radius (running = loud).
    ///   • SCENT BURSTS — <see cref="scentChasesPerNight"/> times per night she catches the scent
    ///     and SPRINTS to the player's current location. Even sneaking perfectly, she comes for you.
    ///
    /// BEHAVIOR:
    ///   • WANDER — free-roams the map; each new point is biased toward the player by
    ///     <see cref="wanderBiasTowardPlayer"/>. So she's always slowly drifting your way.
    ///   • HUNT — sprints at you while sensed; sprints to last-known if scent-driven.
    ///   • SEARCH — visits your last-known spot AND a PREDICTED forward position (anticipates
    ///     where you ran to), making it much harder to shake her.
    ///
    /// PER-DAY ESCALATION (the dread dial):
    ///   Day 1 = base stats. By the final day she's faster, sees further, scents more often,
    ///   and searches longer. Same monster, but the night gets meaner as the week ends.
    ///
    /// Touch = death. Day = inactive (she shelters). Anti-stuck recovery keeps her from
    /// snagging on trees / rocks / fences. Requires a baked NavMesh.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class MonsterAI : NetworkBehaviour
    {
        private enum State { Inactive, Wander, Hunt, Search }

        [Header("Movement speeds (base — see Per-Day Escalation)")]
        [SerializeField] private float stalkSpeed = 2.6f;
        [SerializeField] private float runSpeed = 7f;
        [Tooltip("The chase-cycle timer. When the SAME player has been her pursuit target for this " +
                 "long, she first tries to SWITCH to the 2nd-nearest player (if they're within " +
                 "Target Switch Distance Threshold of the nearest — resets this timer); if there's " +
                 "no one to switch to, she escalates to a RUN on her current target instead. 0 = off.")]
        [SerializeField] private float relentlessAfterSeconds = 20f;
        [Tooltip("On chase-timer expiry she only switches to the 2nd-nearest player when " +
                 "(their distance − nearest's distance) ≤ this many metres. Bigger = switches more " +
                 "eagerly; smaller = mostly sticks to her current victim.")]
        [SerializeField] private float targetSwitchDistanceThreshold = 8f;

        [Header("Sight (base — see Per-Day Escalation)")]
        [Tooltip("How far she can SEE you (needs line of sight + within her view cone).")]
        [SerializeField] private float detectRadius = 50f;   // scene instance overrides this too — keep both in sync
        [Range(20f, 360f)] [SerializeField] private float sightFOV = 110f;
        [SerializeField] private float eyeHeight = 1.6f;
        [Tooltip("Solid things that block her view (trees, rocks, buildings). Triggers are ignored.")]
        [SerializeField] private LayerMask sightBlockers = ~0;

        [Header("Close-range scent (no LOS required)")]
        [Tooltip("Within this radius she ALWAYS senses you — no line of sight, no view cone, no " +
                 "hiding behind nearby cover. Trees and walls do NOT help at point-blank range.")]
        [SerializeField] private float closeRangeScentRadius = 8f;

        [Header("Search / wander")]
        [SerializeField] private float searchDuration = 15f;
        [Tooltip("OPTIONAL anchor for wandering. LEAVE EMPTY for free-roam — each next point is " +
                 "picked near her current position. Only set this to PEN her to a specific area.")]
        [SerializeField] private Transform wanderCenter;
        [Tooltip("How far each new wander point is from her current spot (or wanderCenter).")]
        [SerializeField] private float wanderRadius = 60f;
        [Range(0f, 1f)]
        [Tooltip("How strongly random wander points are pulled toward the player (0 = pure random; " +
                 "1 = walks straight at you while \"wandering\"). 0.20–0.35 feels relentless without " +
                 "being a guaranteed shadow.")]
        [SerializeField] private float wanderBiasTowardPlayer = 0.40f;
        [SerializeField] private float reachDistance = 1.6f;

        [Header("Scent bursts (twice per night by default)")]
        [Tooltip("How many scent bursts per night. Each pins your CURRENT location as the hunt target.")]
        [SerializeField] private int scentChasesPerNight = 4;
        [Range(0f, 1f)] [SerializeField] private float scentEarliestFraction = 0.0f;
        [Range(0f, 1f)] [SerializeField] private float scentLatestFraction = 1.0f;

        [Header("Per-day escalation (1.0 = no change; >1.0 = harder by the final day)")]
        [Tooltip("Run-speed multiplier on the FINAL day. 1.3 = 30% faster on the final night than " +
                 "night 1 (final-night run ≈ 9.1 Easy / 11.4 Med / 13.65 Hard vs player sprint 12).")]
        [SerializeField] private float maxRunSpeedMultiplier = 1.3f;
        [Tooltip("Sight-range multiplier on the FINAL day. 1.8 = 80% wider sight on day 7.")]
        [SerializeField] private float maxDetectRadiusMultiplier = 1.8f;
        [Tooltip("Search-duration multiplier on the FINAL day. 3.0 = she searches 3x longer.")]
        [SerializeField] private float maxSearchDurationMultiplier = 3.0f;
        [Tooltip("EXTRA scent bursts granted by the final day (added on top of the base count).")]
        [SerializeField] private int extraScentsByFinalDay = 8;

        [Header("Anti-stuck (un-jam on trees / rocks / fences)")]
        [SerializeField] private float stuckSpeedThreshold = 0.2f;
        [SerializeField] private float stuckTimeoutSeconds = 1.5f;

        [Header("Hunt persistence")]
        [Tooltip("After losing sight she keeps SPRINTING to your last-known spot for this many " +
                 "seconds before slowing to a walk. Higher = harder to shake by ducking behind cover.")]
        [SerializeField] private float lostSightGraceSeconds = 2f;

        [Header("Attack")]
        [Tooltip("Touching the player within this distance kills them. Keep generous — NavMeshAgent " +
                 "may stop short of overlap due to colliders, so a tight value means \"close call\" " +
                 "instead of kill.")]
        [SerializeField] private float killRange = 2.5f;

        [Header("Animation (optional)")]
        [SerializeField] private Animator animator;
        [SerializeField] private string speedParameter = "Speed";

        [Header("Kill scream (the moment she catches someone)")]
        [Tooltip("Played 3D from her position on EVERY peer the instant she downs/kills a player — " +
                 "the victim's camera is turning to face her at the same moment. Import your scream " +
                 "clip and drop it here.")]
        [SerializeField] private AudioClip killScreamClip;
        [Range(0f, 1f)] [SerializeField] private float killScreamVolume = 1f;
        [Tooltip("Animator TRIGGER fired with the scream (wired by Tools ▸ Sunset Curse ▸ 16 — the " +
                 "Creep pack's own Roar_Action). Safely skipped if the controller has no such trigger.")]
        [SerializeField] private string screamTriggerParam = "Scream";
        [Tooltip("Seconds she stands over her kill roaring before resuming the hunt. 0 = walk off " +
                 "immediately (the old behaviour).")]
        [SerializeField] private float killScreamPauseSeconds = 2f;

        [Header("Silhouette glow (subtle warm light parented to chest so she reads in the dark)")]
        [Tooltip("Color of the faint glow attached to the monster's chest. Picks her out against " +
                 "dark forest at night without breaking the horror atmosphere.")]
        [SerializeField] private Color glowColor = new Color(0.7f, 0.18f, 0.15f);
        [SerializeField] private float glowIntensity = 2.5f;
        [Tooltip("Range of the glow — keep small (~2m) so it doesn't light up the world around her.")]
        [SerializeField] private float glowRange = 2.2f;
        [SerializeField] private Vector3 glowLocalOffset = new Vector3(0f, 1.2f, 0f);

        [Header("Night-only spawn")]
        [Tooltip("Hide every Renderer + Collider on the monster during the DAY, and warp her back " +
                 "to her starting position when night begins. The Inspector daytime position acts " +
                 "as her spawn point.")]
        [SerializeField] private bool hideDuringDay = true;

        private NavMeshAgent agent;

        // One row per player in the game — cached so the per-frame sensing scan doesn't re-run
        // GetComponent. Rebuilt periodically by RefreshPlayers (handles late joins / leaves).
        private struct PlayerTarget
        {
            public Transform t;
            public PlayerInventory inv;   // networked IsAlive — the ONLY trustworthy alive flag on the host
            public PlayerNoise noise;     // networked NoiseRadius — footstep loudness (see PlayerNoise)
            public PlayerStats stats;     // owner-local; used only for the death routing on the victim
            public bool Valid => t != null && inv != null;
            public bool Alive => Valid && inv.IsAlive;
        }
        private readonly List<PlayerTarget> livePlayers = new List<PlayerTarget>();

        // The player she's currently locked onto (for the Hunt/Search state machine + velocity
        // anticipation). Chosen each frame from livePlayers by SENSES first, then nearest.
        //
        // Alive is ALWAYS read via PlayerInventory (owner-write, replicated). We CANNOT trust
        // PlayerStats.IsAlive on the host: PlayerStats is owner-only, so a remote player's copy is
        // disabled and its local isDead never flips — it would report every remote player as alive.
        private Transform player;
        private PlayerInventory playerInventory;
        private State state = State.Inactive;
        private Vector3 lastKnown;
        private Vector3 lastKnownVelocity;
        private Vector3 lastPlayerPosThisFrame;
        private bool playerPosCaptured;
        private float searchTimer;
        private float stuckTimer;
        private int searchStage;
        private bool huntFromScent;
        private Coroutine scentCoroutine;
        private float lostSightGraceTimer;
        private float playerRefreshTimer;
        private float wanderRepath;   // throttles re-steering toward the nearest player during pursuit
        private PlayerInventory pursuitTarget;   // who the pursuit is tracking, for the chase timer
        private float pursuitTimer;              // seconds pursuitTarget has stayed our pursuit
        private float nextSwitchCheckAt;         // pursuitTimer value at which we next try a target switch
        private PlayerInventory lockedPursuitInv; // non-null after a timeout switch: pursue THIS player
                                                  // instead of the raw nearest (else "nearest wins"
                                                  // would undo the switch on the very next repath)
        private const float PlayerRefreshInterval = 2f;

        // Night-only visibility state. The default-enabled arrays remember which renderers /
        // colliders were ON at startup, so SetVisible only ever restores THOSE — it must never
        // force-enable something deliberately disabled (e.g. the prefab's old placeholder
        // capsule MeshRenderer, which is switched off in the Inspector).
        private Vector3 spawnPosition;
        private Renderer[] cachedRenderers;
        private bool[] rendererDefaultOn;
        private Collider[] cachedColliders;
        private bool[] colliderDefaultOn;
        private bool lastVisible;   // current SetVisible state, reconciled each frame on every peer

        // Stun (Lord's Syrup) — agent stops + she vanishes for a duration.
        private float stunTimer;
        private bool wasStunned;   // true while stunned; drives the "resume movement" on stun-end
        public bool IsStunned => stunTimer > 0f;

        // Distraction (Torch flash) — she FREEZES (stationary) for torchFreezeSeconds, then can only
        // WALK (no sprint) and ignores all senses for the rest of the duration.
        private float distractFreezeTimer;   // initial stationary freeze
        private float distractTimer;         // walk-only phase after the freeze
        public bool IsDistracted => distractTimer > 0f || distractFreezeTimer > 0f;
        [Tooltip("Seconds the torch flash FREEZES her stationary before she recovers to a walk.")]
        [SerializeField] private float torchFreezeSeconds = 2f;

        // Investigate (Stone Distractor) — she walks to the thrown spot at stalk speed and
        // ignores senses until the timer expires. Then resumes normal wander.
        private float investigateTimer;
        private Vector3 investigatePos;
        public bool IsInvestigating => investigateTimer > 0f;

        // Tracks transform-delta-based speed for the animator on NON-SERVER peers (the server
        // drives the NavMeshAgent and reads agent.velocity directly; clients see the replicated
        // transform changes from NetworkTransform but their local agent is disabled, so we
        // synthesise the "speed" parameter from frame-to-frame position deltas instead).
        private Vector3 prevTransformPos;
        private bool prevTransformPosCaptured;
        private float clientSyntheticSpeed;

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            agent.autoBraking = true;
            // Stop right ON the player, not short of them — otherwise she stalls outside killRange
            // and the touch-kill never fires even when she's right next to you.
            agent.stoppingDistance = 0f;

            // Snapshot for night-only visibility / respawn — capture the INTENDED enabled state
            // of every renderer/collider BEFORE the first SetVisible touches them.
            spawnPosition = transform.position;
            cachedRenderers = GetComponentsInChildren<Renderer>(true);
            rendererDefaultOn = new bool[cachedRenderers.Length];
            for (int i = 0; i < cachedRenderers.Length; i++)
                rendererDefaultOn[i] = cachedRenderers[i] != null && cachedRenderers[i].enabled;
            cachedColliders = GetComponentsInChildren<Collider>(true);
            colliderDefaultOn = new bool[cachedColliders.Length];
            for (int i = 0; i < cachedColliders.Length; i++)
                colliderDefaultOn[i] = cachedColliders[i] != null && cachedColliders[i].enabled;
            if (hideDuringDay) SetVisible(false); // hidden by default until first night

            // Silhouette glow — runs on EVERY peer (it's local, no networking needed because the
            // monster's transform is already synced). Toggled with the renderers via SetVisible.
            CreateGlow();
        }

        private Light glowLight;

        private void CreateGlow()
        {
            if (glowIntensity <= 0f) return;
            var go = new GameObject("MonsterGlow");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = glowLocalOffset;
            glowLight = go.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.color = glowColor;
            glowLight.intensity = glowIntensity;
            glowLight.range = glowRange;
            glowLight.shadows = LightShadows.None;
            glowLight.renderMode = LightRenderMode.ForcePixel;
            glowLight.enabled = false;   // off during the day; SetVisible flips it
        }

        public override void OnNetworkSpawn()
        {
            // CLIENTS don't run the AI — the host is authoritative. Disable the NavMeshAgent so
            // the local agent doesn't fight the replicated transform from NetworkTransform.
            if (IsSpawned && !IsServer && agent != null)
            {
                agent.enabled = false;
            }
        }

        private void Start()
        {
            // GameClock events fire on EVERY peer (it's networked + re-fires events locally), so
            // visibility/animator state stays in sync without us doing anything special. The AI
            // logic itself is gated server-side inside the handlers below.
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart += HandleNight;
                GameClock.Instance.OnDayStart += HandleDay;
                if (GameClock.Instance.CurrentPhase == GameClock.Phase.Night) HandleNight(GameClock.Instance.CurrentDay);
                else HandleDay(GameClock.Instance.CurrentDay);
            }

            // Server-side AI prep — clients skip everything below (their monster is a puppet
            // driven by NetworkTransform).
            if (IsSpawned && !IsServer) return;

            RefreshPlayers();
            ApplyDifficulty(DifficultyPreference.Selected);
            if (GameClock.Instance == null || GameClock.Instance.CurrentPhase != GameClock.Phase.Night)
                Deactivate();
        }

        public override void OnDestroy()
        {
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart -= HandleNight;
                GameClock.Instance.OnDayStart -= HandleDay;
            }
            Inventory.OnLordsSyrupUsed    -= HandleLordsSyrup;
            Inventory.OnMonsterDistracted -= HandleDistraction;
            Inventory.OnMonsterInvestigate -= HandleInvestigate;
            base.OnDestroy();   // NGO's own NetworkBehaviour cleanup must still run
        }

        private void OnEnable()
        {
            Inventory.OnLordsSyrupUsed    += HandleLordsSyrup;
            Inventory.OnMonsterDistracted += HandleDistraction;
            Inventory.OnMonsterInvestigate += HandleInvestigate;
            MonsterAlerts.OnValuableTaken        += HandleValuableTaken;
            MonsterAlerts.OnRitualStarted        += ForceHuntPosition;
            MonsterRegistry.Register(transform);   // proximity effects track the nearest monster
        }

        private void OnDisable()
        {
            Inventory.OnLordsSyrupUsed    -= HandleLordsSyrup;
            Inventory.OnMonsterDistracted -= HandleDistraction;
            Inventory.OnMonsterInvestigate -= HandleInvestigate;
            MonsterAlerts.OnValuableTaken        -= HandleValuableTaken;
            MonsterAlerts.OnRitualStarted        -= ForceHuntPosition;
            MonsterRegistry.Unregister(transform);
        }

        /// <summary>UPDATE #5: a valuable was taken from the compound. The watcher's territory rule
        /// switches OFF for the stalker (she may now enter and hunt inside), and she gets the
        /// picker's position as a forced scent target so she charges the disturbance. The breach
        /// lasts until dawn (HandleDay clears it), so each night the compound starts as the
        /// watcher's exclusive ground again.</summary>
        private void HandleValuableTaken(Vector3 pos)
        {
            if (IsSpawned && !IsServer) return;
            compoundBreached = true;
            ForceHuntPosition(pos);   // ClampOutsideWatcherTerritory is now a no-op → she goes IN
        }

        /// <summary>Forced target acquisition (ritual potion pickup / ritual start): overrides any
        /// pursuit lock and current hunt outright — no scent-score comparison — and SPRINTS to the
        /// position. Senses stay live en route, so on arrival she naturally engages whoever she
        /// finds. Player-tool effects (syrup stun / torch daze / stone investigate) still take
        /// precedence while their timers run — a paid tool is never cancelled by the ritual.</summary>
        public void ForceHuntPosition(Vector3 pos)
        {
            if (IsSpawned && !IsServer) return;           // host-authoritative
            if (state == State.Inactive) return;          // daytime — she's sheltering
            if (agent == null || !agent.isOnNavMesh) return;

            // Territory rule: an UNBREACHED-compound flare (e.g. a ritual starting there) draws her
            // only to the FENCE. But a valuable pickup breaches the compound first (HandleValuableTaken
            // sets compoundBreached before calling here), so that flare passes through and she goes IN.
            pos = ClampOutsideWatcherTerritory(pos);

            lockedPursuitInv = null;
            pursuitTimer = 0f;
            nextSwitchCheckAt = relentlessAfterSeconds;
            lastKnown = pos;
            lastKnownVelocity = Vector3.zero;
            EnterHunt(fromScent: true);
            agent.SetDestination(pos);
            Debug.Log($"[MonsterAI] {name}: RITUAL SCENT — forced hunt to {pos}.", this);
        }

        private void HandleLordsSyrup() => Stun(30f);
        private void HandleDistraction(float duration) => Distract(duration);
        private void HandleInvestigate(Vector3 pos, float duration) => Investigate(pos, duration);

        /// <summary>Freeze the monster in place for <paramref name="duration"/> seconds. Called on
        /// every peer by Inventory's OnLordsSyrupUsed event, so the local stalker stops everywhere.</summary>
        public void Stun(float duration)
        {
            stunTimer = Mathf.Max(stunTimer, duration);
            pursuitTimer = 0f;   // a disruption breaks her focus — she must re-earn the run escalation
            lockedPursuitInv = null; nextSwitchCheckAt = relentlessAfterSeconds;
            if (agent != null && agent.isOnNavMesh) { agent.isStopped = true; agent.ResetPath(); }
            Debug.Log($"[MonsterAI] {name}: STUNNED for {duration:0}s (Lord's Syrup).", this);
        }

        /// <summary>Distract the monster for <paramref name="duration"/> seconds — she keeps moving
        /// but slowly, and ignores all senses (won't acquire / chase the player). Called on every
        /// peer by Inventory's OnMonsterDistracted broadcast (Torch flash).</summary>
        public void Distract(float duration)
        {
            distractFreezeTimer = Mathf.Max(distractFreezeTimer, torchFreezeSeconds);   // stop dead first
            distractTimer = Mathf.Max(distractTimer, duration);                          // then walk-only
            pursuitTimer = 0f;   // breaks her focus — must re-earn the run escalation afterwards
            lockedPursuitInv = null; nextSwitchCheckAt = relentlessAfterSeconds;
            // Force her out of any active hunt so she actually disengages.
            if (state == State.Hunt || state == State.Search) EnterWander();
            Debug.Log($"[MonsterAI] {name}: TORCH — frozen {torchFreezeSeconds:0}s, then walk-only for {duration:0}s.", this);
        }

        /// <summary>Send the monster to investigate <paramref name="pos"/> for <paramref name="duration"/>
        /// seconds — she walks there at stalk speed and ignores senses on the way. Stone-distractor effect.</summary>
        public void Investigate(Vector3 pos, float duration)
        {
            // A stone thrown INTO the compound lures her to its fence, never inside (territory rule).
            pos = ClampOutsideWatcherTerritory(pos);
            investigatePos   = pos;
            investigateTimer = Mathf.Max(investigateTimer, duration);
            pursuitTimer = 0f;   // the stone breaks her chase focus — run escalation must re-earn
            lockedPursuitInv = null; nextSwitchCheckAt = relentlessAfterSeconds;
            // Quit any active chase and commit to the noise. (The Update investigate block then
            // walks her to the stone and holds her there, senses off, until the timer expires.)
            if (state == State.Hunt || state == State.Search) EnterWander();
            if (agent != null && agent.isOnNavMesh) TrySetDestination(pos, 8f);
            Debug.Log($"[MonsterAI] {name}: INVESTIGATING {pos} for {duration:0}s (Stone Distractor).", this);
        }

        private void HandleNight(int day)
        {
            // Visibility on EVERY peer (so clients see her appear at nightfall too).
            if (hideDuringDay) SetVisible(true);
            // Warn every local player the hunt has begun (runs on every peer).
            ScreenMessage.Show("It's turning dark, she must be out on a hunt now!", 4f);
            // Movement + AI scheduling are server-only.
            if (IsSpawned && !IsServer) return;
            if (hideDuringDay) WarpToSpawn();
            Activate();
            if (scentCoroutine != null) StopCoroutine(scentCoroutine);
            scentCoroutine = StartCoroutine(ScheduleScentChases());
        }

        private void HandleDay(int day)
        {
            // Hide on every peer.
            if (hideDuringDay) SetVisible(false);
            if (IsSpawned && !IsServer) return;
            Deactivate();
            compoundBreached = false;   // new day → the compound is the watcher's ground again (UPDATE #5)
            if (scentCoroutine != null) { StopCoroutine(scentCoroutine); scentCoroutine = null; }
        }

        private void WarpToSpawn()
        {
            if (agent == null) return;
            // Try the saved spawn position first; if it's not on the navmesh, sample nearby.
            if (NavMesh.SamplePosition(spawnPosition, out var hit, 25f, NavMesh.AllAreas))
                agent.Warp(hit.position);
            else
                agent.Warp(spawnPosition);
        }

        private void SetVisible(bool on)
        {
            // "on" restores each part to its INTENDED state — never force-enables one that was
            // deliberately disabled in the Inspector (like the placeholder capsule renderer).
            if (cachedRenderers != null)
                for (int i = 0; i < cachedRenderers.Length; i++)
                    if (cachedRenderers[i] != null)
                        cachedRenderers[i].enabled = on && rendererDefaultOn[i];
            if (cachedColliders != null)
                for (int i = 0; i < cachedColliders.Length; i++)
                    if (cachedColliders[i] != null)
                        cachedColliders[i].enabled = on && colliderDefaultOn[i];
            if (glowLight != null) glowLight.enabled = on;
            lastVisible = on;
        }

        private void Activate()
        {
            if (!EnsureOnNavMesh())
            {
                Debug.LogWarning($"[MonsterAI] {name}: no navmesh found anywhere in range — is one baked?", this);
                return;
            }
            EnterWander();
            Debug.Log($"[MonsterAI] {name}: NIGHT — speed×{Mult(maxRunSpeedMultiplier):0.00}, " +
                      $"sight×{Mult(maxDetectRadiusMultiplier):0.00}, " +
                      $"scents={EffectiveScentCount()}, search={EffectiveSearchDuration():0.0}s.", this);
        }

        private bool EnsureOnNavMesh(float searchRadius = 250f)
        {
            if (agent == null) return false;
            if (agent.isOnNavMesh) return true;
            if (NavMesh.SamplePosition(transform.position, out var hit, searchRadius, NavMesh.AllAreas))
                agent.Warp(hit.position);
            if (!agent.isOnNavMesh && player != null &&
                NavMesh.SamplePosition(player.position, out var phit, searchRadius, NavMesh.AllAreas))
                agent.Warp(phit.position);
            return agent.isOnNavMesh;
        }

        private void Deactivate()
        {
            state = State.Inactive;
            if (agent != null && agent.isOnNavMesh) { agent.isStopped = true; agent.ResetPath(); }
        }

        private void Update()
        {
            // Animator on EVERY peer — server reads agent.velocity, clients synthesise from
            // transform deltas (because their NavMeshAgent is disabled, but they DO receive the
            // replicated transform changes from NetworkTransform).
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                float speed;
                if (IsSpawned && !IsServer)
                {
                    if (prevTransformPosCaptured)
                    {
                        Vector3 delta = transform.position - prevTransformPos;
                        delta.y = 0f;
                        float raw = delta.magnitude / Mathf.Max(0.0001f, Time.deltaTime);
                        // Low-pass: NetworkTransform interpolation makes per-frame deltas noisy;
                        // fed raw, the walk blend flickered between Idle and Walk ("glides, then
                        // walks, then glides"). Smooth over ~0.25s, snap tiny values to a clean 0.
                        clientSyntheticSpeed = Mathf.Lerp(clientSyntheticSpeed, raw,
                                                          Mathf.Clamp01(Time.deltaTime / 0.25f));
                    }
                    prevTransformPos = transform.position;
                    prevTransformPosCaptured = true;
                    speed = clientSyntheticSpeed < 0.35f ? 0f : clientSyntheticSpeed;
                }
                else
                {
                    // Feed the COMMANDED speed while she's genuinely en route — instantaneous
                    // velocity dips on every turn / 0.3s repath / avoidance nudge, which dragged
                    // the blend toward Idle mid-stride (the walk↔glide cycling). Standing,
                    // stopped or arrived still reads 0 → clean idle.
                    bool moving = agent != null && agent.enabled && agent.isOnNavMesh &&
                                  agent.hasPath && !agent.isStopped &&
                                  agent.velocity.sqrMagnitude > 0.2f;
                    speed = moving ? agent.speed : 0f;
                }
                // Damped, so blend changes EASE between idle/walk/run instead of snapping.
                animator.SetFloat(speedParameter, speed, 0.12f, Time.deltaTime);
            }

            // Tick the STUN timer on EVERY peer (the server-only block below can't run on clients,
            // and every peer needs the timer to drive the "she vanishes while stunned" visibility).
            if (stunTimer > 0f) stunTimer -= Time.deltaTime;

            // Reconcile visibility EVERY frame on EVERY peer: she is visible only at NIGHT and while
            // NOT stunned — Lord's Syrup makes her DISAPPEAR entirely for its 30s. Also guards a
            // client that missed OnNightStart. Cheap: SetVisible only runs when the state flips.
            if (hideDuringDay && GameClock.Instance != null)
            {
                bool night = GameClock.Instance.CurrentPhase == GameClock.Phase.Night;
                bool shouldBeVisible = night && stunTimer <= 0f;
                if (shouldBeVisible != lastVisible) SetVisible(shouldBeVisible);
            }

            // Everything below this point is AI/decision logic — server-only.
            if (IsSpawned && !IsServer) return;

            TrackPlayerVelocity();

            // Keep the roster of players fresh (handles late NGO spawns + join/leave). Cheap —
            // there are at most 4 players. The per-frame SENSING below scans this whole list, so
            // she reacts to ANY player's footsteps / scent / silhouette, weighted by proximity —
            // not just one pre-chosen target.
            playerRefreshTimer -= Time.deltaTime;
            if (livePlayers.Count == 0 || playerRefreshTimer <= 0f)
            {
                playerRefreshTimer = PlayerRefreshInterval;
                RefreshPlayers();
            }

            if (state == State.Inactive || agent == null || !agent.isOnNavMesh) return;

            // Lord's Syrup: agent stops + ignores all senses (and she's INVISIBLE — see the
            // visibility reconcile above) for the stun duration. The timer is ticked pre-gate.
            if (stunTimer > 0f)
            {
                wasStunned = true;
                if (agent.isOnNavMesh) { agent.isStopped = true; agent.velocity = Vector3.zero; }
                return;
            }
            if (wasStunned)   // stun just ended → resume movement
            {
                wasStunned = false;
                if (agent.isOnNavMesh) agent.isStopped = false;
            }

            // Torch flash — PHASE 1: FREEZE stationary for torchFreezeSeconds.
            if (distractFreezeTimer > 0f)
            {
                distractFreezeTimer -= Time.deltaTime;
                if (agent.isOnNavMesh) { agent.isStopped = true; agent.velocity = Vector3.zero; }
                return;
            }
            // Torch flash — PHASE 2: she can only WALK (no sprint) and ignores all senses for the
            // rest of the duration, so a player can back away without her running them down.
            if (distractTimer > 0f)
            {
                distractTimer -= Time.deltaTime;
                if (agent.isOnNavMesh)
                {
                    agent.isStopped = false;
                    agent.speed = stalkSpeed;   // walk only — never runs while dazed
                    if (Arrived()) PickWanderPoint();
                }
                return;
            }

            // Stone Distractor: navigate to the thrown spot and stay there, senses off, until
            // the timer expires. This is what makes the distractor actually useful — she COMMITS
            // to investigating the noise rather than spotting the player en route.
            if (investigateTimer > 0f)
            {
                investigateTimer -= Time.deltaTime;
                if (agent.isOnNavMesh)
                {
                    agent.speed = stalkSpeed;
                    // Once she reaches the spot, STAND STILL and inspect — don't oscillate in place
                    // with a walk animation (stoppingDistance is 0, so the agent micro-jitters at the
                    // exact point unless we explicitly stop it).
                    if (Vector3.Distance(transform.position, investigatePos) <= reachDistance)
                    {
                        agent.isStopped = true;
                        agent.velocity = Vector3.zero;
                        if (agent.hasPath) agent.ResetPath();
                    }
                    else
                    {
                        agent.isStopped = false;
                        if (!agent.hasPath || Vector3.Distance(agent.destination, investigatePos) > 1.5f)
                            TrySetDestination(investigatePos, 4f);
                    }
                }
                return;
            }

            // TERRITORY: while the rule is still enforced (no valuable taken yet tonight), a path
            // that cuts through the watcher's compound turns straight back out — the stalker won't
            // operate inside the rival's ground. Once breached, this no-ops and she roams freely.
            if (InActiveWatcherTerritory(transform.position))
            {
                var w = CompoundWatcherAI.Instance;
                Vector3 outDir = transform.position - w.TerritoryCenter;
                outDir.y = 0f;
                if (outDir.sqrMagnitude < 1f) outDir = Vector3.forward;
                agent.speed = stalkSpeed;
                TrySetDestination(w.TerritoryCenter + outDir.normalized * (w.TerritoryRadius + 8f), 12f);
                CheckStuck();
                return;
            }

            // Touch kills — check EVERY alive player, not just the current target.
            var inRange = FirstAliveInKillRange();
            if (inRange.HasValue) { KillPlayer(inRange.Value); return; }

            // SENSING across ALL alive players: the closest one she can currently see / hear /
            // smell becomes the hunt target this frame. A distant player who sprints (loud) can
            // pull her off a nearby silent one — everyone's footsteps + scent matter, by proximity.
            var sensed = SelectSensedTarget();
            bool senses = sensed.HasValue;
            if (senses)
            {
                SetCurrentTarget(sensed.Value);
                lastKnown = player.position;
                lastKnownVelocity = playerPosCaptured ? lastKnownVelocity : Vector3.zero;
            }
            else if (playerInventory == null || !playerInventory.IsAlive ||
                     (player != null && InSafeHouse(player.position)))
            {
                // Current target is gone / dead / ducked into the safe house and nobody is sensed —
                // drift toward the nearest EXPOSED player (never camps a corpse or a safe teammate).
                var near = NearestHuntable();
                if (near.HasValue) SetCurrentTarget(near.Value);
                else ClearCurrentTarget();
            }

            // Chase timer: track how long the SAME player has been our pursuit target (the locked
            // switch target if one is set, else the raw nearest). On expiry she first tries to hand
            // the chase to the 2ND-NEAREST player (cycle restarts); when there's nobody comparable
            // to switch to, the old relentless escalation kicks in: she RUNS her current target
            // down even without sensing them — so single-player behaves exactly as before.
            var pursuitNow = CurrentPursuit();
            var pursuitInv = pursuitNow.HasValue ? pursuitNow.Value.inv : null;
            if (pursuitInv != null && pursuitInv == pursuitTarget) pursuitTimer += Time.deltaTime;
            else { pursuitTarget = pursuitInv; pursuitTimer = 0f; nextSwitchCheckAt = relentlessAfterSeconds; }

            if (relentlessAfterSeconds > 0f && pursuitTimer >= nextSwitchCheckAt)
            {
                // Schedule the NEXT re-check one full period out; a successful switch resets both.
                nextSwitchCheckAt = pursuitTimer + relentlessAfterSeconds;
                TrySwitchPursuitTarget();
            }
            bool relentless = relentlessAfterSeconds > 0f && pursuitTimer >= relentlessAfterSeconds;

            float effRun = EffectiveRunSpeed();

            switch (state)
            {
                case State.Wander:
                    // CONSTANT PURSUIT (scent-driven): even with no sight/sound, she always advances
                    // on the nearest LIVING player, re-steering as they move. She never wanders off or
                    // camps an empty spot. Normally a stalk — but once a player has been her nearest
                    // for `relentlessAfterSeconds` she switches to a full RUN toward them.
                    agent.speed = relentless ? effRun : stalkSpeed;
                    if (senses) { EnterHunt(fromScent: false); break; }
                    wanderRepath -= Time.deltaTime;
                    if (wanderRepath <= 0f || Arrived()) { wanderRepath = 0.3f; PickWanderPoint(); }
                    break;

                case State.Hunt:
                    // Hunt = ALWAYS sprint toward the sensed player. When she loses them she sprints
                    // to their last-known spot for the grace window, then RESUMES the walking scent
                    // pursuit (never searches an empty spot).
                    agent.speed = effRun;
                    if (senses)
                    {
                        huntFromScent = false;
                        agent.SetDestination(player.position);
                        lostSightGraceTimer = lostSightGraceSeconds;
                    }
                    else
                    {
                        agent.SetDestination(lastKnown);
                        lostSightGraceTimer -= Time.deltaTime;
                        if (Arrived() || lostSightGraceTimer <= 0f) EnterWander();
                    }
                    break;

                case State.Search:
                    agent.speed = stalkSpeed;
                    if (senses) { EnterHunt(fromScent: false); break; }
                    searchTimer -= Time.deltaTime;
                    if (Arrived()) NextSearchPoint();
                    if (searchTimer <= 0f) EnterWander();
                    break;
            }

            // BLOOD MOON omen: floor her speed to the omen target (faster tonight). Run target when
            // she's hunting/relentless, walk target otherwise. No-op on non-Blood-Moon nights.
            if (agent.isOnNavMesh)
                agent.speed = OmenManager.BloodMoonFloor(agent.speed, state == State.Hunt || relentless);

            CheckStuck();
        }

        // Smoothed player velocity, used by Search to ANTICIPATE where you ran to.
        private void TrackPlayerVelocity()
        {
            if (player == null) { playerPosCaptured = false; return; }

            if (playerPosCaptured)
            {
                Vector3 instant = (player.position - lastPlayerPosThisFrame) / Mathf.Max(0.0001f, Time.deltaTime);
                lastKnownVelocity = Vector3.Lerp(lastKnownVelocity, instant, 0.3f);
            }
            lastPlayerPosThisFrame = player.position;
            playerPosCaptured = true;
        }

        /// <summary>Can she sense THIS specific player right now (scent / hearing / sight)? Called
        /// once per alive player per frame so footsteps + scent of everyone are considered.</summary>
        private bool CanSense(PlayerTarget p)
        {
            Vector3 eye = transform.position + Vector3.up * eyeHeight;
            Vector3 to = (p.t.position + Vector3.up) - eye;
            float dist = to.magnitude;

            // CLOSE-RANGE SCENT — point-blank, walls don't help.
            if (dist <= closeRangeScentRadius) return true;

            // HEARING — within THIS player's current noise radius (networked footstep loudness).
            float noise = p.noise != null ? p.noise.NoiseRadius : 0f;
            if (dist <= noise) return true;

            // SIGHT — in range, in cone, with a clear line of sight (per-day-scaled range).
            float effDetect = EffectiveDetectRadius();
            if (dist <= effDetect)
            {
                float angle = Vector3.Angle(transform.forward, to);
                if (angle <= sightFOV * 0.5f &&
                    !Physics.Raycast(eye, to.normalized, dist - 0.4f, sightBlockers, QueryTriggerInteraction.Ignore))
                    return true;
            }
            return false;
        }

        // "Huntable" = alive AND on their feet AND NOT sheltering in the safe house AND NOT inside
        // the compound watcher's territory. Players inside the safe house are completely
        // off-limits — she can't sense, target, or kill them. DOWNED players (bleeding out,
        // waiting for a revive) are also ignored: she considers them dealt with and moves on,
        // which is exactly what gives teammates their rescue window.
        //
        // TERRITORY RULE: the valuables compound belongs to the WATCHER — UNTIL a valuable is
        // taken. Two monsters hunting the same player in that small space made night raids
        // unwinnable, so until the compound is disturbed the stalker treats it like rival
        // territory (won't target players inside it, PROWLS THE FENCE instead — see
        // PickWanderPoint). The moment ANY valuable is picked up (compoundBreached), the rule
        // switches OFF for the rest of the night and she may enter and hunt inside like anywhere
        // else (UPDATE #5). Resets at dawn.
        private bool compoundBreached;
        private bool WatcherTerritoryActive => CompoundWatcherAI.Instance != null && !compoundBreached;
        private static bool InSafeHouse(Vector3 pos)
            => World.SafeHouse.Instance != null && World.SafeHouse.Instance.Contains(pos);
        private static bool InWatcherTerritory(Vector3 pos)
            => CompoundWatcherAI.Instance != null && CompoundWatcherAI.Instance.InTerritory(pos);
        // Territory that's still ENFORCED (exists AND not yet breached this night).
        private bool InActiveWatcherTerritory(Vector3 pos)
            => WatcherTerritoryActive && CompoundWatcherAI.Instance.InTerritory(pos);
        private bool Huntable(PlayerTarget p)
            => p.Alive && !p.inv.IsDowned
               && !(p.inv == recentlyDownedInv && Time.time < recentlyDownedUntil)   // just-downed grace
               && !InSafeHouse(p.t.position) && !InActiveWatcherTerritory(p.t.position);

        /// <summary>Is there ANY living player at all (even ones safe inside the house)? Used to
        /// decide "everyone's holed up → retreat" vs "no one left → idle".</summary>
        private bool AnyAlivePlayer()
        {
            foreach (var p in livePlayers) if (p.Alive) return true;
            return false;
        }

        /// <summary>Any living player currently inside the compound watcher's territory? Drives
        /// the "prowl the fence while they raid" behavior.</summary>
        private bool AnyAliveInWatcherTerritory()
        {
            if (!WatcherTerritoryActive) return false;   // breached → she hunts them, doesn't prowl
            foreach (var p in livePlayers)
                if (p.Alive && InWatcherTerritory(p.t.position)) return true;
            return false;
        }

        // The closest HUNTABLE player she can currently sense (or null). This is what makes her react
        // to whichever exposed teammate is loudest / most visible nearby.
        private PlayerTarget? SelectSensedTarget()
        {
            PlayerTarget? best = null;
            float bestDist = float.MaxValue;
            foreach (var p in livePlayers)
            {
                if (!Huntable(p)) continue;
                if (!CanSense(p)) continue;
                float d = Vector3.SqrMagnitude(transform.position - p.t.position);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        // The closest HUNTABLE player regardless of senses — used for scent pursuit + scent bursts.
        private PlayerTarget? NearestHuntable()
        {
            PlayerTarget? best = null;
            float bestDist = float.MaxValue;
            foreach (var p in livePlayers)
            {
                if (!Huntable(p)) continue;
                float d = Vector3.SqrMagnitude(transform.position - p.t.position);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        // ─────────── Chase-timer target switching (anti tunnel-vision) ───────────

        /// <summary>Who the constant pursuit should track right now: the locked switch target if
        /// one is set and still huntable, otherwise the raw nearest player. The lock self-clears
        /// the moment its player dies / goes down / reaches the safe house.</summary>
        private PlayerTarget? CurrentPursuit()
        {
            if (lockedPursuitInv != null)
            {
                foreach (var p in livePlayers)
                {
                    if (p.inv != lockedPursuitInv) continue;
                    if (Huntable(p)) return p;
                    break;   // found but no longer huntable → drop the lock
                }
                lockedPursuitInv = null;
            }
            return NearestHuntable();
        }

        /// <summary>Chase timer expired: hand the chase to the 2nd-nearest player — but only when
        /// they're within <see cref="targetSwitchDistanceThreshold"/> metres of the nearest's
        /// distance (both measured NOW, not cached — positions moved during the chase). With only
        /// one player around, or a too-distant runner-up, nothing changes and the relentless run
        /// continues; we re-check on the next expiry.</summary>
        private void TrySwitchPursuitTarget()
        {
            // Nearest + 2nd-nearest in one pass, at this exact moment.
            PlayerTarget? first = null, second = null;
            float firstDist = float.MaxValue, secondDist = float.MaxValue;
            foreach (var p in livePlayers)
            {
                if (!Huntable(p)) continue;
                float d = Vector3.Distance(transform.position, p.t.position);
                if (d < firstDist)
                {
                    second = first; secondDist = firstDist;
                    first = p; firstDist = d;
                }
                else if (d < secondDist)
                {
                    second = p; secondDist = d;
                }
            }

            if (!second.HasValue) return;   // solo / one exposed player → old behavior untouched

            float distanceDelta = secondDist - firstDist;
            if (distanceDelta > targetSwitchDistanceThreshold)
            {
                Debug.Log($"[MonsterAI] {name}: chase timer expired — runner-up is {distanceDelta:0.0}m " +
                          $"further (> {targetSwitchDistanceThreshold:0}m), keeping current target.", this);
                return;
            }

            // Lock onto the runner-up and restart the cycle.
            lockedPursuitInv = second.Value.inv;
            pursuitTarget = second.Value.inv;
            pursuitTimer = 0f;
            nextSwitchCheckAt = relentlessAfterSeconds;
            SetCurrentTarget(second.Value);
            Debug.Log($"[MonsterAI] {name}: chase timer expired — SWITCHING to the 2nd-nearest player " +
                      $"(Δ {distanceDelta:0.0}m ≤ {targetSwitchDistanceThreshold:0}m).", this);
        }

        // Any HUNTABLE player within lethal touch range (checked every frame).
        private PlayerTarget? FirstAliveInKillRange()
        {
            foreach (var p in livePlayers)
            {
                if (!Huntable(p)) continue;
                if (Vector3.Distance(transform.position, p.t.position) <= killRange) return p;
            }
            return null;
        }

        private void SetCurrentTarget(PlayerTarget p)
        {
            player = p.t;
            playerInventory = p.inv;
        }

        private void ClearCurrentTarget()
        {
            player = null;
            playerInventory = null;
        }

        private bool Arrived() => !agent.pathPending && agent.remainingDistance <= reachDistance;

        // If she's barely moving while heading somewhere, she's snagged on geometry — pick a
        // fresh target so she frees herself.
        private void CheckStuck()
        {
            if (Arrived()) { stuckTimer = 0f; return; }

            if (agent.velocity.magnitude < stuckSpeedThreshold) stuckTimer += Time.deltaTime;
            else stuckTimer = 0f;

            if (stuckTimer < stuckTimeoutSeconds) return;
            stuckTimer = 0f;

            switch (state)
            {
                case State.Wander: PickWanderPoint(); break;
                case State.Search: NextSearchPoint(); break;
                case State.Hunt:
                    Vector2 r = Random.insideUnitCircle * 5f;
                    TrySetDestination(lastKnown + new Vector3(r.x, 0f, r.y), 8f);
                    break;
            }
        }

        private void EnterWander()
        {
            state = State.Wander;
            agent.isStopped = false;
            agent.speed = stalkSpeed;
            huntFromScent = false;
            PickWanderPoint();
        }

        private void EnterHunt(bool fromScent)
        {
            state = State.Hunt;
            agent.isStopped = false;
            huntFromScent = fromScent;
            lostSightGraceTimer = lostSightGraceSeconds;
            // Real senses trump the timeout switch — seeing/hearing someone re-grounds the chase,
            // so the pursuit lock is dropped and "nearest" logic resumes after this hunt ends.
            lockedPursuitInv = null;
        }

        private void EnterSearch()
        {
            state = State.Search;
            searchTimer = EffectiveSearchDuration();
            huntFromScent = false;
            searchStage = 0;
            NextSearchPoint();
        }

        // Multi-stage search: visit last-known, then anticipate where the player ran to, then
        // random pokes. Much harder to shake than a single-spot search.
        private void NextSearchPoint()
        {
            searchStage++;
            Vector3 target;
            switch (searchStage)
            {
                case 1:
                    target = lastKnown;
                    break;
                case 2:
                    // ANTICIPATE — push out 8m along the player's recent velocity direction.
                    Vector3 fwd = lastKnownVelocity.sqrMagnitude > 0.01f
                        ? lastKnownVelocity.normalized
                        : transform.forward;
                    target = lastKnown + fwd * 8f;
                    break;
                default:
                    Vector2 r = Random.insideUnitCircle * 6f;
                    target = lastKnown + new Vector3(r.x, 0f, r.y);
                    break;
            }
            TrySetDestination(target, 8f);
        }

        // Scent pursuit: head straight for the pursuit target — the locked switch target if the
        // chase timer handed her off, else the NEAREST exposed player. If everyone is raiding the
        // watcher's compound, PROWL ITS FENCE (waiting for them to come out). If everyone is
        // sheltering in the safe house, RETREAT far from it and roam. Nobody alive → random drift.
        private void PickWanderPoint()
        {
            var near = CurrentPursuit();
            if (near.HasValue)
            {
                TrySetDestination(near.Value.t.position, wanderRadius);
                return;
            }

            // No huntable player right now (everyone is dead, sheltered, or inside the watcher's
            // compound). Patrol points are DESTINATIONS, not steering: keep walking to the current
            // one and only roll a new one on arrival. The wander state repaths every 0.3s — the
            // old code rolled a fresh point (or a fence-orbit point ~1.5m ahead) on every repath,
            // so she micro-shuffled in place and looked like she was just standing there.
            if (agent.pathPending || (agent.hasPath && !Arrived())) return;

            // Everyone alive is inside the watcher's compound (rival ground — she won't enter
            // until it's breached): patrol HER OWN region randomly, so she's somewhere
            // unpredictable when they come back out.
            if (AnyAliveInWatcherTerritory())
            {
                RollPatrolPoint();
                return;
            }
            // Everyone's holed up in the safe house? Keep well away from it.
            if (World.SafeHouse.Instance != null && AnyAlivePlayer())
            {
                TrySetDestination(RetreatPointAwayFromSafeHouse(), wanderRadius);
                return;
            }
            RollPatrolPoint();
        }

        /// <summary>A random patrol destination in her home region (wander centre + radius).</summary>
        private void RollPatrolPoint()
        {
            Vector3 anchor = wanderCenter != null ? wanderCenter.position : transform.position;
            Vector2 r = Random.insideUnitCircle * wanderRadius;
            TrySetDestination(anchor + new Vector3(r.x, 0f, r.y), wanderRadius);
        }

        // A roam point at least SafeHouse.RetreatDistance out from the safe house, in the direction
        // she's already heading away from it — so while everyone shelters she stays ≥60m off.
        private Vector3 RetreatPointAwayFromSafeHouse()
        {
            var sh = World.SafeHouse.Instance;
            Vector3 c = sh.Center;
            Vector3 away = transform.position - c; away.y = 0f;
            if (away.sqrMagnitude < 1f)
            {
                Vector2 r = Random.insideUnitCircle.normalized;
                away = new Vector3(r.x, 0f, r.y);
            }
            away.Normalize();
            float dist = sh.RetreatDistance + Random.Range(5f, 25f);
            return c + away * dist;
        }

        private void TrySetDestination(Vector3 target, float sampleRange)
        {
            if (NavMesh.SamplePosition(target, out var hit, sampleRange, NavMesh.AllAreas))
                agent.SetDestination(hit.position);
        }

        /// <summary>A point inside the watcher's still-enforced compound gets pushed to just
        /// OUTSIDE its fence (in the point's own direction from the centre). Points already outside
        /// — and ALL points once the compound is breached (UPDATE #5) — pass through unchanged.</summary>
        private Vector3 ClampOutsideWatcherTerritory(Vector3 pos)
        {
            if (!WatcherTerritoryActive) return pos;   // breached → she's allowed inside now
            var w = CompoundWatcherAI.Instance;
            if (!w.InTerritory(pos)) return pos;
            Vector3 dir = pos - w.TerritoryCenter;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1f) dir = Vector3.forward;
            return w.TerritoryCenter + dir.normalized * (w.TerritoryRadius + 6f);
        }

        private void KillPlayer(PlayerTarget victim)
        {
            // Server-only path (we're gated above). Route the damage through a targeted ClientRpc
            // on the VICTIM's PlayerHealthSync so the death + respawn-potion check runs on the
            // owner's machine (where their PlayerInventory.Local is correctly bound). Calling
            // PlayerStats.TakeDamage directly only works for the host, because a remote player's
            // PlayerStats is owner-only-disabled on the host's machine.
            // Face the victim and SCREAM — the victim's death/downed camera is turning toward her
            // right now, so on their screen she's roaring straight at them. Broadcast so every
            // peer hears/sees it (server-authoritative rotation replicates via NetworkTransform).
            if (victim.t != null)
            {
                Vector3 look = victim.t.position - transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(look);
            }
            BroadcastKillScream();

            var sync = victim.t != null ? victim.t.GetComponent<SunsetCurse.Player.PlayerHealthSync>() : null;
            if (sync != null)
            {
                sync.ServerKillPlayer();
            }
            else if (victim.stats != null)
            {
                // Fallback for pre-rework scenes: still hit the local PlayerStats so SP keeps working.
                victim.stats.TakeDamage(99999f);
            }

            // MOVE ON from the body: the player she just downed must NOT keep pulling her. Two
            // things stick her otherwise — (1) the old "linger/investigate the kill spot" and (2)
            // a ~100ms gap before the victim's downed flag replicates to the host, during which
            // they still read as huntable. So we (a) blacklist this victim for a short window
            // (bridges the replication gap), (b) clear any pursuit lock/target, and (c) EnterWander
            // — which re-targets the NEAREST STANDING player immediately. She walks off toward the
            // next victim while the scream still rings out.
            recentlyDownedInv = victim.inv;
            recentlyDownedUntil = Time.time + recentlyDownedIgnoreSeconds;
            lockedPursuitInv = null;
            pursuitTarget = null;
            pursuitTimer = 0f;
            ClearCurrentTarget();
            EnterWander();
        }

        // A player the monster JUST downed — ignored as a target for a short window so she peels
        // off to the next standing player instead of hovering over the body (also covers the delay
        // before the victim's replicated IsDowned reaches the host).
        private PlayerInventory recentlyDownedInv;
        private float recentlyDownedUntil;
        private const float recentlyDownedIgnoreSeconds = 1.5f;

        // ───────────────────────────── Kill scream ─────────────────────────────

        /// <summary>Server-side: roar at the moment of a catch, on every peer.</summary>
        private void BroadcastKillScream()
        {
            if (IsSpawned) KillScreamClientRpc();
            else PlayKillScreamLocal();   // offline single-player
        }

        [ClientRpc]
        private void KillScreamClientRpc() => PlayKillScreamLocal();

        private void PlayKillScreamLocal()
        {
            if (killScreamClip != null && SunsetCurse.Audio.AudioManager.Instance != null)
                SunsetCurse.Audio.AudioManager.Instance.PlaySfx3D(
                    killScreamClip, transform.position + Vector3.up * 1.5f, killScreamVolume, 70f);
            TryFireScreamTrigger();
        }

        /// <summary>SetTrigger, but only if the controller actually HAS the trigger — so the game
        /// runs fine before the scream state is wired (Tools ▸ Sunset Curse ▸ 16) and no console
        /// warnings spam when it isn't.</summary>
        private void TryFireScreamTrigger()
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;
            foreach (var p in animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Trigger && p.name == screamTriggerParam)
                {
                    animator.SetTrigger(screamTriggerParam);
                    return;
                }
            }
        }

        private IEnumerator ScheduleScentChases()
        {
            var clock = GameClock.Instance;
            if (clock == null || clock.CurrentPhase != GameClock.Phase.Night) yield break;

            float nightDuration = clock.PhaseTimeRemaining;
            if (nightDuration <= 0f) yield break;

            float earliest = nightDuration * scentEarliestFraction;
            float latest = nightDuration * scentLatestFraction;

            int count = Mathf.Max(0, EffectiveScentCount());
            float[] times = new float[count];
            for (int i = 0; i < count; i++) times[i] = Random.Range(earliest, latest);
            System.Array.Sort(times);

            float waited = 0f;
            for (int i = 0; i < count; i++)
            {
                float delta = times[i] - waited;
                if (delta > 0f) yield return new WaitForSeconds(delta);
                waited = times[i];

                if (state == State.Inactive) yield break;
                TriggerScent();
            }
        }

        private void TriggerScent()
        {
            // A scent burst pins the NEAREST exposed player's CURRENT location and sprints there.
            // (Players safe inside the house are never scented.)
            var near = NearestHuntable();
            if (!near.HasValue) return;
            SetCurrentTarget(near.Value);
            lastKnown = near.Value.t.position;
            EnterHunt(fromScent: true);
            Debug.Log($"[MonsterAI] {name}: SCENT — sprinting to nearest player's location.", this);
        }

        // Scale base Inspector values by the difficulty the player picked on the menu.
        // Easy = unchanged (×1.0); Medium / Hard ramp up speed, sight, scent and aggression.
        private void ApplyDifficulty(Difficulty d)
        {
            runSpeed *= DifficultyPreference.RunSpeedMult(d);
            stalkSpeed *= DifficultyPreference.StalkSpeedMult(d);
            detectRadius *= DifficultyPreference.DetectRadiusMult(d);
            closeRangeScentRadius *= DifficultyPreference.CloseScentMult(d);
            killRange *= DifficultyPreference.KillRangeMult(d);
            wanderBiasTowardPlayer = Mathf.Clamp01(wanderBiasTowardPlayer * DifficultyPreference.WanderBiasMult(d));
            searchDuration *= DifficultyPreference.SearchDurationMult(d);
            scentChasesPerNight += DifficultyPreference.ScentCountBonus(d);
            Debug.Log($"[MonsterAI] Applied difficulty {DifficultyPreference.Label(d)} — " +
                      $"run={runSpeed:0.0} sight={detectRadius:0.0} scent={scentChasesPerNight} " +
                      $"closeScent={closeRangeScentRadius:0.0} bias={wanderBiasTowardPlayer:0.00}.", this);
        }

        // Rebuild the roster of players in the scene. Handles late spawning (NGO instantiates the
        // player AFTER our Start runs) and co-op join/leave. We key off PlayerInventory because its
        // IsAlive is the networked, host-trustworthy alive flag; PlayerStats/PlayerNoise are pulled
        // from the same GameObject for the death routing + footstep loudness.
        private void RefreshPlayers()
        {
            livePlayers.Clear();
            foreach (var inv in FindObjectsByType<PlayerInventory>())
            {
                if (inv == null) continue;
                livePlayers.Add(new PlayerTarget
                {
                    t     = inv.transform,
                    inv   = inv,
                    noise = inv.GetComponent<PlayerNoise>(),
                    stats = inv.GetComponent<PlayerStats>()
                });
            }

            // If our current target vanished (disconnect) or died, drop it — the per-frame logic
            // will re-acquire from the fresh list.
            if (playerInventory == null || !playerInventory.IsAlive) ClearCurrentTarget();
        }

        // -------- Per-day escalation helpers --------

        // 0.0 on day 1, 1.0 on the final day, linear in between.
        private float DayProgress01()
        {
            var clock = GameClock.Instance;
            if (clock == null || clock.TotalDays <= 1) return 0f;
            return Mathf.Clamp01((clock.CurrentDay - 1) / (float)(clock.TotalDays - 1));
        }

        private float Mult(float maxMult) => Mathf.Lerp(1f, maxMult, DayProgress01());

        private float EffectiveRunSpeed() => runSpeed * Mult(maxRunSpeedMultiplier);
        private float EffectiveDetectRadius() => detectRadius * Mult(maxDetectRadiusMultiplier);
        private float EffectiveSearchDuration() => searchDuration * Mult(maxSearchDurationMultiplier);
        private int EffectiveScentCount()
            => scentChasesPerNight + Mathf.RoundToInt(extraScentsByFinalDay * DayProgress01());

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, detectRadius);   // base sight range
            Gizmos.color = new Color(1f, 0f, 1f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, closeRangeScentRadius); // close-scent
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, killRange);
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.5f);
            Vector3 c = wanderCenter != null ? wanderCenter.position : transform.position;
            Gizmos.DrawWireSphere(c, wanderRadius);
        }
    }
}
