using System;
using Unity.Netcode;
using UnityEngine;

namespace SunsetCurse.Core
{
    /// <summary>
    /// The master clock for Sunset Curse.
    ///
    /// Drives the whole game loop:
    ///   - Counts off a fixed number of DAYS (default 7).
    ///   - Each day has a DAY phase (safe-ish, foraging/scouting) followed by a
    ///     NIGHT phase (monsters roam, players raid the compound).
    ///   - Daylight SHRINKS a little every day to build dread.
    ///   - After the final night, if the players have not escaped, night becomes
    ///     permanent and the game is over (a loss).
    ///
    /// Other systems should NOT keep their own timers. Instead they subscribe to the
    /// events below (OnDayStart, OnNightStart, etc.) so the whole game agrees on time.
    ///
    /// ---------------------------------------------------------------------------
    /// MULTIPLAYER (Phase 3 — Netcode for GameObjects)
    /// ---------------------------------------------------------------------------
    /// This is now a NetworkBehaviour. The HOST/SERVER is the only authority on
    /// time — it ticks the phase timer using NetworkManager.ServerTime (a clock
    /// every connected client agrees on) and pushes the current day/phase/etc.
    /// out via NetworkVariables. Clients never advance the clock themselves;
    /// they receive the new state and re-fire the same local C# events
    /// (OnDayStart, OnNightStart, OnGameOver, OnEscape) so every existing
    /// consumer script (DayNightLighting, PlayerStats, MonsterAI, ...) keeps
    /// working without changes.
    ///
    /// If you press Play WITHOUT starting a host (i.e. no NetworkManager is
    /// listening yet), GameClock falls back to its original single-player
    /// behaviour — the timer just ticks locally. This keeps the normal solo
    /// iteration flow intact while you build out the multiplayer vertical
    /// slice.
    ///
    /// SETUP IN UNITY:
    ///   1. Add a `NetworkManager` GameObject to your scene (Components: NetworkManager
    ///      + UnityTransport). This is required for any NGO play.
    ///   2. On the existing "GameClock" GameObject, add a `NetworkObject` component.
    ///      It must sit ABOVE this GameClock script in the Inspector (the
    ///      NetworkObject is what NGO looks for when spawning scene objects).
    /// </summary>
    public class GameClock : NetworkBehaviour
    {
        /// <summary>The two parts of every day.</summary>
        public enum Phase { Day, Night }

        // We use a small byte enum for the "how did the run end" NetworkVariable
        // (NetworkVariable<byte> is one of the simplest types to sync).
        private const byte EndStateNone = 0;
        private const byte EndStateGameOver = 1;
        private const byte EndStateEscaped = 2;

        /// <summary>
        /// Global access point so other scripts can do GameClock.Instance.CurrentDay, etc.
        /// (Simple singleton — still fine in co-op because there's only one clock,
        /// owned by the host.)
        /// </summary>
        public static GameClock Instance { get; private set; }

        // ----------------------------------------------------------------------------
        //  Inspector settings — tweak these to balance the game. All times in seconds.
        // ----------------------------------------------------------------------------

        [Header("Schedule")]
        [Tooltip("How many days the players have before night becomes permanent.")]
        [SerializeField] private int totalDays = 7;

        [Header("Daytime length (shrinks each day)")]
        [Tooltip("Seconds of daylight on Day 1.")]
        [SerializeField] private float firstDayLength = 360f;     // 6 minutes
        [Tooltip("Daylight lost each day after the first (the 'dread' dial).")]
        [SerializeField] private float daylightLostPerDay = 30f;  // Day 2 = 5:30, Day 3 = 5:00, ...
        [Tooltip("Daylight will never drop below this. Day 7 = 3:00 with current settings.")]
        [SerializeField] private float minDayLength = 60f;

        [Header("Nighttime length (constant)")]
        [Tooltip("Seconds of night each day — same every night.")]
        [SerializeField] private float nightLength = 360f;        // 6 minutes

        [Header("Debug — read-only while playing")]
        [SerializeField] private int currentDay = 1;
        [SerializeField] private Phase currentPhase = Phase.Day;
        [SerializeField] private float phaseTimer;                // counts UP from 0
        [SerializeField] private float currentPhaseLength;        // length of the active phase
        [SerializeField] private bool isRunning;

        // ----------------------------------------------------------------------------
        //  Server-authoritative network state.
        //  Only the host/server writes these; every client receives the updates and
        //  mirrors them into the local fields above via OnValueChanged below.
        // ----------------------------------------------------------------------------

        private readonly NetworkVariable<int> netCurrentDay = new NetworkVariable<int>(0);
        private readonly NetworkVariable<byte> netCurrentPhase = new NetworkVariable<byte>(0);
        private readonly NetworkVariable<float> netCurrentPhaseLength = new NetworkVariable<float>(0f);
        // Server's clock-time when the current phase began. Clients compute "how far
        // into the phase are we" as (NetworkManager.ServerTime.Time - this).
        private readonly NetworkVariable<double> netPhaseStartServerTime = new NetworkVariable<double>(0.0);
        private readonly NetworkVariable<bool> netIsRunning = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<byte> netEndState = new NetworkVariable<byte>(EndStateNone);

        // ----------------------------------------------------------------------------
        //  Events — other systems subscribe to these instead of polling.
        //  Example:  GameClock.Instance.OnNightStart += HandleNightStart;
        //
        //  In multiplayer these fire on EVERY peer (host + each client) at the moment
        //  the phase change is received, so subscribers don't need to know whether
        //  they're on the server or a client.
        // ----------------------------------------------------------------------------

        /// <summary>Fired when a new DAY phase begins. Passes the day number (1..totalDays).</summary>
        public event Action<int> OnDayStart;

        /// <summary>Fired when a NIGHT phase begins. Passes the day number.</summary>
        public event Action<int> OnNightStart;

        /// <summary>Fired on ANY phase change. Passes the phase that just started.</summary>
        public event Action<Phase> OnPhaseChanged;

        /// <summary>Fired when the final night ends with no escape → permanent night (a LOSS).</summary>
        public event Action OnGameOver;

        /// <summary>Fired when players successfully escape (a WIN). Call TriggerEscape() to fire it.</summary>
        public event Action OnEscape;

        // ----------------------------------------------------------------------------
        //  Public read-only state — for UI, lighting, AI, etc.
        //  These read from the local mirrored fields, which are kept in sync by the
        //  NetworkVariable callbacks below — so consumers don't need to care about
        //  networking at all.
        // ----------------------------------------------------------------------------

        public int CurrentDay => currentDay;
        public Phase CurrentPhase => currentPhase;
        public bool IsRunning => isRunning;
        public int TotalDays => totalDays;

        /// <summary>Seconds left in the current phase (day or night).</summary>
        public float PhaseTimeRemaining
        {
            get
            {
                if (IsSpawned)
                {
                    // Networked: use the host's authoritative clock so every peer agrees.
                    double elapsed = NetworkManager.ServerTime.Time - netPhaseStartServerTime.Value;
                    return Mathf.Max(0f, netCurrentPhaseLength.Value - (float)elapsed);
                }
                // Offline: original local timer.
                return Mathf.Max(0f, currentPhaseLength - phaseTimer);
            }
        }

        /// <summary>
        /// How far through the current phase we are, 0 (just started) → 1 (about to end).
        /// Handy for rotating the sun / blending the skybox.
        /// </summary>
        public float PhaseProgress01
        {
            get
            {
                float length, elapsed;
                if (IsSpawned)
                {
                    length = netCurrentPhaseLength.Value;
                    elapsed = (float)(NetworkManager.ServerTime.Time - netPhaseStartServerTime.Value);
                }
                else
                {
                    length = currentPhaseLength;
                    elapsed = phaseTimer;
                }
                return length <= 0f ? 1f : Mathf.Clamp01(elapsed / length);
            }
        }

        // ----------------------------------------------------------------------------
        //  Unity lifecycle
        // ----------------------------------------------------------------------------

        private void Awake()
        {
            // Enforce a single instance.
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[GameClock] A second GameClock was found and destroyed.", this);
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // Apply the chosen night-count (2 / 4 / 7). Sourced from SessionState so a JOINER uses
            // the HOST's choice (replicated) rather than whatever they picked on their own menu; it
            // falls back to this peer's local pick offline / if SessionState isn't set up.
            totalDays = Mathf.Max(1, SunsetCurse.Net.SessionState.EffectiveTotalNights);

            // Begin the game on Day 1 immediately, regardless of network state.
            // - Offline: this is the normal single-player tick.
            // - Networked: if the host has already started, OnNetworkSpawn ran first
            //   and StartDay was effectively driven from there; if the host starts
            //   LATER, OnNetworkSpawn will then mirror our current local state into
            //   the NetworkVariables so clients catch up.
            StartDay(1);
        }

        public override void OnNetworkSpawn()
        {
            // Subscribe to network state changes on every peer (host + clients).
            // The handlers themselves bail out on the server to avoid double-firing
            // (the server already invoked the local C# events directly).
            netCurrentPhase.OnValueChanged += OnNetCurrentPhaseChanged;
            netEndState.OnValueChanged += OnNetEndStateChanged;

            if (IsServer)
            {
                // Push whatever the local clock is currently doing into the network
                // so any already-connected clients sync up to the right phase.
                PublishCurrentStateToNetwork();
            }
            else
            {
                // Late-joining client: pull the current state and fire an initial
                // phase event so consumers (lighting, HUD, spawners, monsters) snap
                // immediately to the right phase.
                AdoptNetworkState(fireInitialEvents: true);
            }
        }

        public override void OnNetworkDespawn()
        {
            netCurrentPhase.OnValueChanged -= OnNetCurrentPhaseChanged;
            netEndState.OnValueChanged -= OnNetEndStateChanged;
        }

        private void Update()
        {
            if (!isRunning) return;

            if (IsSpawned)
            {
                // Networked mode — only the host advances the clock; clients are
                // driven by NetworkVariable updates received from the host.
                if (!IsServer) return;

                double elapsed = NetworkManager.ServerTime.Time - netPhaseStartServerTime.Value;
                phaseTimer = (float)elapsed; // keep the inspector "Debug" field useful

                if (elapsed >= netCurrentPhaseLength.Value)
                {
                    AdvancePhase();
                }
            }
            else
            {
                // Offline — original single-player tick.
                phaseTimer += Time.deltaTime;
                if (phaseTimer >= currentPhaseLength)
                {
                    AdvancePhase();
                }
            }
        }

        // ----------------------------------------------------------------------------
        //  Public controls
        // ----------------------------------------------------------------------------

        /// <summary>
        /// Call this when the players have completed the escape ritual.
        /// Safe to call from any peer:
        ///   - Offline: runs directly.
        ///   - Networked host: runs directly.
        ///   - Networked client: routes through a ServerRpc so the host stops the clock
        ///     and broadcasts OnEscape to everyone.
        /// </summary>
        public void TriggerEscape()
        {
            if (!IsSpawned)
            {
                TriggerEscapeLocal();
                return;
            }
            if (IsServer) TriggerEscapeLocal();
            else          TriggerEscapeServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void TriggerEscapeServerRpc() => TriggerEscapeLocal();

        private void TriggerEscapeLocal()
        {
            if (!isRunning) return;
            isRunning = false;

            if (IsSpawned && IsServer)
            {
                netIsRunning.Value = false;
                netEndState.Value = EndStateEscaped; // → OnEscape fires on each client
            }

            Debug.Log("[GameClock] The players escaped the curse! (WIN)");
            OnEscape?.Invoke();
        }

        /// <summary>
        /// Pause/resume the clock (e.g. for a menu).
        /// In networked play this is host-only — clients calling it are silently ignored
        /// so a single client can't pause the shared world.
        /// </summary>
        public void SetRunning(bool running)
        {
            if (IsSpawned && !IsServer) return; // host-only when networked
            isRunning = running;
            if (IsSpawned && IsServer) netIsRunning.Value = running;
        }

        // ----------------------------------------------------------------------------
        //  Internal phase machine — only the server (or offline play) ever calls these.
        // ----------------------------------------------------------------------------

        private void StartDay(int day)
        {
            currentDay = day;
            currentPhase = Phase.Day;
            currentPhaseLength = DayLengthForDay(day);
            phaseTimer = 0f;
            isRunning = true;

            if (IsSpawned && IsServer) PublishCurrentStateToNetwork();

            Debug.Log($"[GameClock] Day {day} — DAYTIME begins ({currentPhaseLength:0}s of light).");
            OnDayStart?.Invoke(day);
            OnPhaseChanged?.Invoke(Phase.Day);
        }

        private void StartNight(int day)
        {
            currentPhase = Phase.Night;
            currentPhaseLength = nightLength;
            phaseTimer = 0f;

            if (IsSpawned && IsServer) PublishCurrentStateToNetwork();

            Debug.Log($"[GameClock] Day {day} — NIGHTTIME begins. The monsters wake.");
            OnNightStart?.Invoke(day);
            OnPhaseChanged?.Invoke(Phase.Night);
        }

        private void AdvancePhase()
        {
            if (currentPhase == Phase.Day)
            {
                // Day → Night of the same day.
                StartNight(currentDay);
            }
            else
            {
                // Night just ended. Move to the next day — unless we're out of days.
                int nextDay = currentDay + 1;
                if (nextDay > totalDays) GameOver();
                else                     StartDay(nextDay);
            }
        }

        private void GameOver()
        {
            isRunning = false;

            if (IsSpawned && IsServer)
            {
                netIsRunning.Value = false;
                netEndState.Value = EndStateGameOver; // → OnGameOver fires on each client
            }

            Debug.Log("[GameClock] The sun never rose. Night is permanent. (GAME OVER)");
            OnGameOver?.Invoke();
        }

        /// <summary>
        /// Daylight length for a given day: starts at firstDayLength and loses
        /// daylightLostPerDay each day, but never below minDayLength.
        /// </summary>
        private float DayLengthForDay(int day)
        {
            float length = firstDayLength - daylightLostPerDay * (day - 1);
            return Mathf.Max(minDayLength, length);
        }

        // ----------------------------------------------------------------------------
        //  Network sync helpers
        // ----------------------------------------------------------------------------

        // SERVER → network: copy current local state into the NetworkVariables so
        // every connected client sees what's happening right now.
        private void PublishCurrentStateToNetwork()
        {
            netCurrentDay.Value = currentDay;
            netCurrentPhase.Value = (byte)currentPhase;
            netCurrentPhaseLength.Value = currentPhaseLength;
            // Back-date the phase start so PhaseProgress01 lines up with our phaseTimer.
            netPhaseStartServerTime.Value = NetworkManager.ServerTime.Time - phaseTimer;
            netIsRunning.Value = isRunning;
        }

        // NETWORK → client: copy the NetworkVariables into local fields so the
        // public read-only API keeps returning the right values, and optionally fire
        // a one-shot phase event so subsystems (lighting/HUD/spawners) initialise.
        private void AdoptNetworkState(bool fireInitialEvents)
        {
            currentDay = netCurrentDay.Value;
            currentPhase = (Phase)netCurrentPhase.Value;
            currentPhaseLength = netCurrentPhaseLength.Value;
            isRunning = netIsRunning.Value;

            if (!fireInitialEvents) return;

            if (netEndState.Value == EndStateGameOver) { OnGameOver?.Invoke(); return; }
            if (netEndState.Value == EndStateEscaped)  { OnEscape?.Invoke();  return; }

            if (currentPhase == Phase.Day) OnDayStart?.Invoke(currentDay);
            else                            OnNightStart?.Invoke(currentDay);
            OnPhaseChanged?.Invoke(currentPhase);
        }

        // Fires when the host changes netCurrentPhase. Clients mirror the new state
        // and re-fire the local C# events. The host skips this — it already fired
        // them directly inside StartDay/StartNight.
        private void OnNetCurrentPhaseChanged(byte previous, byte next)
        {
            if (IsServer) return;
            AdoptNetworkState(fireInitialEvents: true);
        }

        // Same idea for the end-of-game state (Game Over / Escape).
        private void OnNetEndStateChanged(byte previous, byte next)
        {
            if (IsServer) return;
            isRunning = false;
            if (next == EndStateGameOver) OnGameOver?.Invoke();
            else if (next == EndStateEscaped) OnEscape?.Invoke();
        }

        // Keep inspector values sane while editing.
        private void OnValidate()
        {
            totalDays = Mathf.Max(1, totalDays);
            firstDayLength = Mathf.Max(1f, firstDayLength);
            daylightLostPerDay = Mathf.Max(0f, daylightLostPerDay);
            minDayLength = Mathf.Clamp(minDayLength, 1f, firstDayLength);
            nightLength = Mathf.Max(1f, nightLength);
        }
    }
}
