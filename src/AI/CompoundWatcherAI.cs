using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using SunsetCurse.Core;
using SunsetCurse.Player;

namespace SunsetCurse.AI
{
    /// <summary>
    /// The SECOND monster: an audio-driven watcher that guards the valuables compound. Completely
    /// separate from <see cref="MonsterAI"/> (no scent, no sight, no shared code) so changes to
    /// one can't regress the other.
    ///
    /// HOW SHE HUNTS — ears only:
    ///   • RUNNING or JUMPING near her makes noise (read off each player's replicated
    ///     <see cref="PlayerNoise.NoiseRadius"/> — walking stays under her threshold).
    ///   • Picking up the RITUAL POTION is the loudest event of all (via MonsterAlerts).
    ///   • She walks to the LAST-KNOWN spot of the loudest/most-recent sound — never a live
    ///     position. Sneak away silently after making noise and she inspects an empty spot.
    ///   • The ritual starting forces her toward the ritual site (zone-clamped, like everything).
    ///
    /// HARD ZONE: she NEVER leaves her circle (default: her spawn point + Zone Radius). Any
    /// destination — audio event, ritual site, patrol point — is clamped to the boundary first,
    /// and a per-frame check walks her back in if physics ever shoves her out.
    ///
    /// CATCH: any standing player within Catch Range is downed (routed through PlayerHealthSync,
    /// so the respawn-potion / downed / death flow is identical to the main monster's kills).
    ///
    /// DELIBERATELY IGNORED (by design):
    ///   • Stone Distractor  — she does not subscribe to OnMonsterInvestigate. Stones are useless here.
    ///   • Torch flash       — she does not subscribe to OnMonsterDistracted.
    ///   • Lord's Syrup DOES affect her (30s vanish), same as the main monster.
    ///
    /// SETUP: use Tools ▸ Sunset Curse ▸ 7 to build her in the scene (adds NavMeshAgent,
    /// NetworkObject, NetworkTransform, the Plague Doctor model + zombie animator, and parks her
    /// at the compound). Then size Zone Radius so the gizmo circle covers the valuables area.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class CompoundWatcherAI : NetworkBehaviour
    {
        [Header("Zone (hard boundary — she NEVER leaves this circle)")]
        [Tooltip("Optional zone centre. Leave EMPTY to use her spawn position as the centre.")]
        [SerializeField] private Transform zoneCenterOverride;
        [Tooltip("Radius of her territory, in metres. Size it to the valuables compound.")]
        [SerializeField] private float zoneRadius = 45f;

        [Header("Movement")]
        [SerializeField] private float patrolSpeed = 2.2f;
        [Tooltip("Speed when heading to an audio event.")]
        [SerializeField] private float chaseSpeed = 5.5f;
        [SerializeField] private float reachDistance = 2f;

        [Header("Hearing")]
        [Tooltip("A player's noise radius must be at least this loud to register. Walking (6) " +
                 "stays under it; running (24) and jumps (20) trip it. Rain halves player noise, " +
                 "so a rainy-night sprint (12) only just registers.")]
        [SerializeField] private float loudNoiseThreshold = 12f;
        [Tooltip("How often she listens for player noise, in seconds.")]
        [SerializeField] private float listenInterval = 0.4f;
        [Tooltip("A louder sound always replaces the current one; a QUIETER one only after the " +
                 "current event is this many seconds old.")]
        [SerializeField] private float eventStaleSeconds = 8f;
        [Tooltip("How long she inspects a sound's spot after arriving before resuming patrol.")]
        [SerializeField] private float lingerSeconds = 4f;
        [Tooltip("While inspecting, how far around the heard sound she pokes (idle → short walk → " +
                 "idle …) instead of grinding her walk animation on one spot.")]
        [SerializeField] private float inspectRadius = 6f;

        [Header("Catch")]
        [Tooltip("A standing player within this range is DOWNED (or killed if nobody can revive).")]
        [SerializeField] private float catchRange = 5f;

        [Header("Animation (optional)")]
        [SerializeField] private Animator animator;
        [SerializeField] private string speedParameter = "Speed";

        [Header("Kill scream (the moment she catches someone)")]
        [Tooltip("Played 3D from her position on EVERY peer the instant she downs/kills a player. " +
                 "Import a scream clip and drop it here.")]
        [SerializeField] private AudioClip killScreamClip;
        [Range(0f, 1f)] [SerializeField] private float killScreamVolume = 1f;
        [Tooltip("Animator TRIGGER fired with the scream (wire a scream state with Tools ▸ Sunset " +
                 "Curse ▸ 16 + a selected Humanoid clip). Safely skipped if the trigger doesn't exist.")]
        [SerializeField] private string screamTriggerParam = "Scream";

        [Header("Body size")]
        [Tooltip("Uniform scale applied to her MODEL at startup — visual only (the NavMeshAgent + " +
                 "collider are untouched). 1 = as authored; >1 = a bigger silhouette. Her feet are " +
                 "re-grounded after scaling. Applied on every peer, so all players see her the same size.")]
        [SerializeField] private float bodyScale = 1.2f;

        [Header("Night-only spawn")]
        [SerializeField] private bool hideDuringDay = true;

        [Header("Silhouette glow (so she reads in the dark — cold teal vs the stalker's warm red)")]
        [SerializeField] private Color glowColor = new Color(0.15f, 0.5f, 0.7f);
        [SerializeField] private float glowIntensity = 2.5f;
        [Tooltip("Keep small (~2m) so it doesn't light up the compound around her.")]
        [SerializeField] private float glowRange = 2.2f;
        [SerializeField] private Vector3 glowLocalOffset = new Vector3(0f, 1.2f, 0f);

        private NavMeshAgent agent;
        private bool active;                    // night + server → AI runs

        // Current audio event (server-only state).
        private bool hasEvent;
        private Vector3 eventPos;
        private float eventLoudness;
        private float eventTime;
        private float lingerTimer;

        // Inspect sub-state (UPDATE #4): on arrival at a sound she IDLES, then pokes around the
        // area — short idle pauses alternating with slow walks — instead of grinding her walk
        // animation on one spot.
        private bool inspecting;
        private float inspectTimer;      // total inspect time remaining
        private float inspectStepTimer;  // time until the next idle↔walk toggle
        private bool inspectWalking;     // currently walking to a nearby point (vs idling)
        private float approachBestDistance; // closest she's gotten to the current sound
        private float approachStuckTimer;   // how long she's failed to get any closer
        private const float InspectIdleBeat = 1.3f;    // how long each idle pause lasts
        private const float InspectWalkTimeout = 3f;   // cap on one inspect hop
        private const float NoProgressSeconds = 0.5f;  // safety net: can't get closer this long → arrived

        // Player roster — rebuilt periodically, same pattern as MonsterAI.
        private struct Listened
        {
            public Transform t;
            public PlayerInventory inv;
            public PlayerNoise noise;
        }
        private readonly List<Listened> players = new List<Listened>();
        private float rosterTimer;
        private float listenTimer;

        // Lord's Syrup stun — ticked on EVERY peer; she's invisible while stunned.
        private float stunTimer;
        private bool wasStunned;

        // Night visibility. Default-enabled arrays: SetVisible only restores parts that were ON
        // at startup — never force-enables something deliberately disabled in the Inspector.
        private Vector3 spawnPosition;
        private Renderer[] cachedRenderers;
        private bool[] rendererDefaultOn;
        private Collider[] cachedColliders;
        private bool[] colliderDefaultOn;
        private bool lastVisible;

        // Client-side synthesised animator speed (their NavMeshAgent is disabled).
        private Vector3 prevPos;
        private bool prevPosCaptured;
        private float clientSpeed;

        private Vector3 ZoneCenter => zoneCenterOverride != null ? zoneCenterOverride.position : spawnPosition;

        /// <summary>The one watcher in the scene (null if none placed). MonsterAI reads her zone
        /// to stay OUT of it — the compound is her territory, not the stalker's.</summary>
        public static CompoundWatcherAI Instance { get; private set; }

        /// <summary>Her territory, for other systems (public mirror of the private zone maths).</summary>
        public Vector3 TerritoryCenter => ZoneCenter;
        public float TerritoryRadius => zoneRadius;

        /// <summary>Is a world position inside her territory? (XZ circle, height ignored.)</summary>
        public bool InTerritory(Vector3 pos)
        {
            Vector3 flat = pos - ZoneCenter;
            flat.y = 0f;
            return flat.magnitude <= zoneRadius;
        }

        // ───────────────────────── lifecycle ─────────────────────────

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.stoppingDistance = 0f;
            agent.autoBraking = true;

            spawnPosition = transform.position;
            cachedRenderers = GetComponentsInChildren<Renderer>(true);
            rendererDefaultOn = new bool[cachedRenderers.Length];
            for (int i = 0; i < cachedRenderers.Length; i++)
                rendererDefaultOn[i] = cachedRenderers[i] != null && cachedRenderers[i].enabled;
            cachedColliders = GetComponentsInChildren<Collider>(true);
            colliderDefaultOn = new bool[cachedColliders.Length];
            for (int i = 0; i < cachedColliders.Length; i++)
                colliderDefaultOn[i] = cachedColliders[i] != null && cachedColliders[i].enabled;
            CreateGlow();   // before the first SetVisible so it starts in the right state
            ApplyBodyScale();   // grow her model on every peer (UPDATE #3)
            if (hideDuringDay) SetVisible(false);
        }

        // ───────────────────────── body size (UPDATE #3) ─────────────────────────

        /// <summary>Scale her MODEL child up a touch (visual only) and re-drop her feet to the
        /// ground so she doesn't hover/sink. Runs on EVERY peer (like the glow), so no networking
        /// is needed — each machine scales its own copy identically.</summary>
        private void ApplyBodyScale()
        {
            if (Mathf.Abs(bodyScale - 1f) < 0.001f) return;
            Transform model = ModelRoot();
            if (model == null) return;
            float beforeMinY = ModelBounds(model).min.y;
            model.localScale *= bodyScale;
            float afterMinY = ModelBounds(model).min.y;
            model.position += Vector3.up * (beforeMinY - afterMinY);   // keep feet on the ground
        }

        /// <summary>The direct child of the watcher root that carries the animated model (never the
        /// glow light — that's excluded so it isn't scaled with her).</summary>
        private Transform ModelRoot()
        {
            if (animator != null)
            {
                Transform t = animator.transform;
                while (t != null && t.parent != transform) t = t.parent;
                if (t != null) return t;
            }
            foreach (Transform c in transform)
                if (c.GetComponent<Light>() == null && c.GetComponentInChildren<Renderer>() != null) return c;
            return null;
        }

        private static Bounds ModelBounds(Transform t)
        {
            var rends = t.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return new Bounds(t.position, Vector3.zero);
            Bounds b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            return b;
        }

        private Light glowLight;

        // Same silhouette-glow trick as the main monster: a faint local point light on every peer
        // (no networking needed — her transform is already replicated), toggled with visibility.
        private void CreateGlow()
        {
            if (glowIntensity <= 0f) return;
            var go = new GameObject("WatcherGlow");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = glowLocalOffset;
            glowLight = go.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.color = glowColor;
            glowLight.intensity = glowIntensity;
            glowLight.range = glowRange;
            glowLight.shadows = LightShadows.None;
            glowLight.renderMode = LightRenderMode.ForcePixel;
            glowLight.enabled = false;   // day-hidden; SetVisible flips it
        }

        public override void OnNetworkSpawn()
        {
            // Clients are puppets — NetworkTransform drives them; their local agent must not fight it.
            if (IsSpawned && !IsServer && agent != null) agent.enabled = false;
        }

        private void Start()
        {
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart += HandleNight;
                GameClock.Instance.OnDayStart += HandleDay;
                if (GameClock.Instance.CurrentPhase == GameClock.Phase.Night) HandleNight(GameClock.Instance.CurrentDay);
                else HandleDay(GameClock.Instance.CurrentDay);
            }
        }

        private void OnEnable()
        {
            // Lord's Syrup affects BOTH monsters. Stone Distractor (OnMonsterInvestigate) and
            // Torch (OnMonsterDistracted) are deliberately NOT subscribed — she ignores them.
            Inventory.OnLordsSyrupUsed += HandleLordsSyrup;
            MonsterAlerts.OnValuableTaken += HandlePotionPickedUp;   // potion / battery / transformer / key
            MonsterAlerts.OnRitualStarted += HandleRitualStarted;
            MonsterAlerts.OnLoudNoise += RegisterAudioEvent;   // dropped items clatter — she checks
            MonsterRegistry.Register(transform);   // proximity effects track the nearest monster
            if (Instance == null) Instance = this;
        }

        private void OnDisable()
        {
            Inventory.OnLordsSyrupUsed -= HandleLordsSyrup;
            MonsterAlerts.OnValuableTaken -= HandlePotionPickedUp;
            MonsterAlerts.OnRitualStarted -= HandleRitualStarted;
            MonsterAlerts.OnLoudNoise -= RegisterAudioEvent;
            MonsterRegistry.Unregister(transform);
            if (Instance == this) Instance = null;
        }

        // NetworkBehaviour has its own OnDestroy cleanup — override + call base so NGO still
        // runs it (plain "private void OnDestroy" would silently replace it).
        public override void OnDestroy()
        {
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart -= HandleNight;
                GameClock.Instance.OnDayStart -= HandleDay;
            }
            base.OnDestroy();
        }

        // ───────────────────────── event handlers ─────────────────────────

        private void HandleLordsSyrup() => stunTimer = Mathf.Max(stunTimer, 30f);

        // Potion pickup: the single loudest thing a player can do — beats any running/jumping.
        private void HandlePotionPickedUp(Vector3 pos) => RegisterAudioEvent(pos, 999f);

        // Ritual start: hard override — head straight for the site (zone-clamped like all moves).
        private void HandleRitualStarted(Vector3 pos) => RegisterAudioEvent(pos, 9999f);

        private void HandleNight(int day)
        {
            if (hideDuringDay) SetVisible(true);
            if (IsSpawned && !IsServer) return;
            if (agent != null && NavMesh.SamplePosition(spawnPosition, out var hit, 25f, NavMesh.AllAreas))
                agent.Warp(hit.position);
            active = agent != null && agent.isOnNavMesh;
            hasEvent = false;
            inspecting = false;
            if (active) { agent.isStopped = false; PickPatrolPoint(); }
        }

        private void HandleDay(int day)
        {
            if (hideDuringDay) SetVisible(false);
            if (IsSpawned && !IsServer) return;
            active = false;
            hasEvent = false;
            inspecting = false;
            if (agent != null && agent.isOnNavMesh) { agent.isStopped = true; agent.ResetPath(); }
        }

        // ───────────────────────── per-frame ─────────────────────────

        private void Update()
        {
            DriveAnimator();

            // Stun ticks on EVERY peer — it drives the "vanished" visibility below.
            if (stunTimer > 0f) stunTimer -= Time.deltaTime;

            // Visibility reconcile on every peer: visible only at night and while not syruped.
            if (hideDuringDay && GameClock.Instance != null)
            {
                bool night = GameClock.Instance.CurrentPhase == GameClock.Phase.Night;
                bool shouldBeVisible = night && stunTimer <= 0f;
                if (shouldBeVisible != lastVisible) SetVisible(shouldBeVisible);
            }

            // AI below is host-only.
            if (IsSpawned && !IsServer) return;
            if (!active || agent == null || !agent.isOnNavMesh) return;

            if (stunTimer > 0f)
            {
                wasStunned = true;
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
                return;
            }
            if (wasStunned) { wasStunned = false; agent.isStopped = false; }

            RefreshRosterAndListen();

            // Catch — any standing player in range goes down. Same routing as the main monster,
            // so respawn potions / the downed state / game over all behave identically.
            foreach (var p in players)
            {
                if (p.t == null || p.inv == null || !p.inv.IsAlive || p.inv.IsDowned) continue;
                if (World.SafeHouse.Instance != null && World.SafeHouse.Instance.Contains(p.t.position)) continue;
                if (Vector3.Distance(transform.position, p.t.position) > catchRange) continue;

                // Face the victim and SCREAM — their camera is turning toward her right now.
                Vector3 look = p.t.position - transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(look);
                BroadcastKillScream();

                var sync = p.t.GetComponent<PlayerHealthSync>();
                if (sync != null) sync.ServerKillPlayer();
                hasEvent = false;    // sound dealt with — back to patrol
                inspecting = false;
                break;
            }

            // Zone enforcement: if anything ever pushes her outside, walk straight back in.
            Vector3 flat = transform.position - ZoneCenter; flat.y = 0f;
            if (flat.magnitude > zoneRadius + 2f)
            {
                agent.speed = SunsetCurse.Core.OmenManager.BloodMoonFloor(chaseSpeed, true);
                TrySetDestination(ClampToZone(transform.position));
                return;
            }

            if (hasEvent)
            {
                if (!inspecting)
                {
                    agent.speed = SunsetCurse.Core.OmenManager.BloodMoonFloor(chaseSpeed, true);
                    // Primary arrival: within reachDistance of the (navmesh-snapped) sound spot. Since
                    // eventPos is now a spot she can actually stand on, this fires cleanly with no
                    // walk-in-place. Horizontal distance, so a height offset never blocks it.
                    Vector3 toEvent = transform.position - eventPos; toEvent.y = 0f;
                    bool closeEnough = toEvent.magnitude <= reachDistance;
                    // Safety net only: if something still blocks her from getting closer, give up
                    // after a brief 0.5s (not 1.5s) so she never grinds her walk animation for long.
                    float rd = agent.pathPending ? Mathf.Infinity : agent.remainingDistance;
                    if (rd < approachBestDistance - 0.3f) { approachBestDistance = rd; approachStuckTimer = 0f; }
                    else approachStuckTimer += Time.deltaTime;
                    bool stuck = approachStuckTimer >= NoProgressSeconds;
                    if (closeEnough || stuck) BeginInspect();
                }
                else UpdateInspect();
            }
            else
            {
                agent.speed = SunsetCurse.Core.OmenManager.BloodMoonFloor(patrolSpeed, false);
                if (!agent.pathPending && agent.remainingDistance <= reachDistance) PickPatrolPoint();
            }
        }

        // ───────────────────────── hearing ─────────────────────────

        private void RefreshRosterAndListen()
        {
            rosterTimer -= Time.deltaTime;
            if (players.Count == 0 || rosterTimer <= 0f)
            {
                rosterTimer = 2f;
                players.Clear();
                foreach (var inv in FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None))
                {
                    if (inv == null) continue;
                    players.Add(new Listened
                    {
                        t = inv.transform,
                        inv = inv,
                        noise = inv.GetComponent<PlayerNoise>()
                    });
                }
            }

            listenTimer -= Time.deltaTime;
            if (listenTimer > 0f) return;
            listenTimer = listenInterval;

            foreach (var p in players)
            {
                if (p.t == null || p.inv == null || !p.inv.IsAlive || p.inv.IsDowned || p.noise == null) continue;
                float loud = p.noise.NoiseRadius;
                if (loud < loudNoiseThreshold) continue;
                // She reacts to sounds she could plausibly hear: the noise radius must actually
                // reach her, OR the sound happened inside her territory.
                float dist = Vector3.Distance(transform.position, p.t.position);
                Vector3 inZone = p.t.position - ZoneCenter; inZone.y = 0f;
                if (dist > loud && inZone.magnitude > zoneRadius) continue;
                RegisterAudioEvent(p.t.position, loud);
            }
        }

        /// <summary>Adopt a new sound as the hunt target: always when louder than the current one,
        /// otherwise only once the current event has gone stale. The position is snapshotted NOW —
        /// she inspects where the sound WAS, never a live player position.</summary>
        private void RegisterAudioEvent(Vector3 pos, float loudness)
        {
            if (IsSpawned && !IsServer) return;   // server-authoritative state only
            if (!active) return;                  // day — she's dormant

            bool adopt = !hasEvent ||
                         loudness >= eventLoudness ||
                         Time.time - eventTime > eventStaleSeconds;
            if (!adopt) return;

            hasEvent = true;
            inspecting = false;   // a fresh sound pulls her out of any inspect — go chase it
            approachStuckTimer = 0f;
            approachBestDistance = Mathf.Infinity;

            // Snap the target ONTO the navmesh. The raw sound spot (a player / drop / ritual
            // position) can sit off the navmesh or inside a wall, so without this she can never
            // "arrive" and grinds her walk animation in place — snapping to a spot she can actually
            // stand on means normal arrival fires and she stops cleanly.
            Vector3 target = ClampToZone(pos);
            if (NavMesh.SamplePosition(target, out var navHit, 10f, NavMesh.AllAreas))
                target = navHit.position;
            eventPos = target;

            eventLoudness = loudness;
            eventTime = Time.time;
            lingerTimer = lingerSeconds;
            if (agent != null && agent.isOnNavMesh)
            {
                agent.isStopped = false;   // she may have been idling mid-inspect
                TrySetDestination(eventPos);
            }
        }

        // ───────────────────────── movement helpers ─────────────────────────

        private void PickPatrolPoint()
        {
            Vector2 r = Random.insideUnitCircle * (zoneRadius * 0.9f);
            TrySetDestination(ZoneCenter + new Vector3(r.x, 0f, r.y));
        }

        // ───────────────────────── inspecting a sound (UPDATE #4) ─────────────────────────

        // On arrival she STOPS (idle) rather than grinding her walk animation on the spot, then
        // alternates short idle pauses with slow walks to nearby points — "inspecting the area" —
        // for the linger duration, then resumes patrol.
        private void BeginInspect()
        {
            inspecting = true;
            inspectTimer = lingerSeconds;
            inspectWalking = false;
            inspectStepTimer = InspectIdleBeat;
            approachStuckTimer = 0f;
            approachBestDistance = Mathf.Infinity;
            StopAgent();   // idle first
            Debug.Log($"[CompoundWatcherAI] Reached the sound — stopping to inspect (idle → look around).", this);
        }

        private void UpdateInspect()
        {
            inspectTimer -= Time.deltaTime;
            if (inspectTimer <= 0f) { EndInspect(); return; }

            inspectStepTimer -= Time.deltaTime;
            if (inspectWalking)
            {
                // Walking to a nearby point — reached it (or the hop timed out) → idle again.
                if ((!agent.pathPending && agent.remainingDistance <= reachDistance) || inspectStepTimer <= 0f)
                {
                    inspectWalking = false;
                    inspectStepTimer = InspectIdleBeat;
                    StopAgent();
                }
            }
            else if (inspectStepTimer <= 0f)
            {
                // Done idling — walk to a nearby point around the sound to inspect the region.
                inspectWalking = true;
                inspectStepTimer = InspectWalkTimeout;
                agent.isStopped = false;
                agent.speed = SunsetCurse.Core.OmenManager.BloodMoonFloor(patrolSpeed, false);   // slow, deliberate — not a chase
                Vector2 r = Random.insideUnitCircle * inspectRadius;
                TrySetDestination(ClampToZone(eventPos + new Vector3(r.x, 0f, r.y)));
            }
        }

        private void EndInspect()
        {
            inspecting = false;
            hasEvent = false;
            agent.isStopped = false;
            PickPatrolPoint();
        }

        private void StopAgent()
        {
            if (agent == null) return;
            agent.isStopped = true;
            if (agent.isOnNavMesh) agent.ResetPath();
            agent.velocity = Vector3.zero;
        }

        /// <summary>Pull any point back inside the zone boundary (1m inside the edge).</summary>
        private Vector3 ClampToZone(Vector3 pos)
        {
            Vector3 c = ZoneCenter;
            Vector3 flat = pos - c; flat.y = 0f;
            if (flat.magnitude <= zoneRadius) return pos;
            return c + flat.normalized * (zoneRadius - 1f);
        }

        private void TrySetDestination(Vector3 target)
        {
            if (NavMesh.SamplePosition(target, out var hit, 8f, NavMesh.AllAreas))
                agent.SetDestination(hit.position);
        }

        // ───────────────────────── presentation ─────────────────────────

        private void DriveAnimator()
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;
            float speed;
            if (IsSpawned && !IsServer)
            {
                if (prevPosCaptured)
                {
                    Vector3 d = transform.position - prevPos; d.y = 0f;
                    clientSpeed = d.magnitude / Mathf.Max(0.0001f, Time.deltaTime);
                }
                prevPos = transform.position;
                prevPosCaptured = true;
                speed = clientSpeed;
            }
            else
            {
                speed = agent != null && agent.enabled && agent.isOnNavMesh ? agent.velocity.magnitude : 0f;
            }
            animator.SetFloat(speedParameter, speed);

            // Her animator controller (ZombieMonster) has NO idle clip — its blend tree floors at
            // the WALK motion (threshold 2.2), so at Speed 0 it plays the walk in place. Until a
            // proper Mixamo idle is imported for the Plague Doctor rig, FREEZE the animator whenever
            // she's essentially still, so she holds a static pose (reads as "stopped and listening")
            // instead of moonwalking. Runs on every peer, so remote views freeze too.
            // EXCEPTION: while the kill scream plays she's standing still by design — keep the
            // animator running so the scream animation isn't frozen on its first frame.
            animator.speed = (speed > 0.15f || Time.time < screamAnimatingUntil) ? 1f : 0f;
        }

        // ───────────────────────────── Kill scream ─────────────────────────────

        // While > Time.time, DriveAnimator keeps animator.speed at 1 even though she's standing
        // still — otherwise her idle-freeze workaround would pin the scream on its first frame.
        private float screamAnimatingUntil;

        /// <summary>Server-side: roar at the moment of a catch, heard/seen on every peer.</summary>
        private void BroadcastKillScream()
        {
            if (IsSpawned) KillScreamClientRpc();
            else PlayKillScreamLocal();   // offline single-player
        }

        [ClientRpc]
        private void KillScreamClientRpc() => PlayKillScreamLocal();

        private void PlayKillScreamLocal()
        {
            screamAnimatingUntil = Time.time + 2.5f;
            if (killScreamClip != null && SunsetCurse.Audio.AudioManager.Instance != null)
                SunsetCurse.Audio.AudioManager.Instance.PlaySfx3D(
                    killScreamClip, transform.position + Vector3.up * 1.5f, killScreamVolume, 70f);
            TryFireScreamTrigger();
        }

        /// <summary>SetTrigger, but only if the controller actually HAS the trigger — so the game
        /// runs fine before a scream state is wired into ZombieMonster.controller (Tools ▸ Sunset
        /// Curse ▸ 16 with a Humanoid scream clip selected) and no console warnings spam.</summary>
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

        private void SetVisible(bool on)
        {
            // "on" restores each part to its INTENDED state — never force-enables one that was
            // deliberately disabled in the Inspector.
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

        private void OnDrawGizmosSelected()
        {
            Vector3 c = Application.isPlaying ? ZoneCenter
                : (zoneCenterOverride != null ? zoneCenterOverride.position : transform.position);
            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.8f);
            Gizmos.DrawWireSphere(c, zoneRadius);            // her territory
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, catchRange);
        }
    }
}
