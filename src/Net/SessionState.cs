using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.Net
{
    /// <summary>
    /// A single PERSISTENT networked object that carries shared session data across the
    /// menu→game scene load: the world seed, the chosen difficulty/mode, and the player roster.
    ///
    /// WHY IT EXISTS: scene-placed NetworkObjects (like the MainMenu's NetworkLobby, or the
    /// SampleScene's Inventory) despawn when their scene unloads. Anything they held is gone the
    /// moment the game scene loads. SessionState is spawned ONCE by the host and calls
    /// DontDestroyOnLoad on every peer, so it (and its data) survive every scene transition and any
    /// future host-migration work.
    ///
    /// The most important payload is the WORLD SEED: because it exists BEFORE the gameplay scene
    /// loads, the resource/berry spawners can read an identical seed on every machine and scatter
    /// the exact same forest — which is what makes "chop a tree, it disappears for everyone" work
    /// (the node IDs are hashed from position, so positions must match).
    ///
    /// SETUP (one-time, in Unity):
    ///   1. Create an empty GameObject "SessionState" → add components: NetworkObject + this script.
    ///   2. Drag it into Assets/_Project/Prefabs/ to make a prefab, then DELETE it from the scene.
    ///   3. NetworkManager Inspector → Network Prefabs list → add the SessionState prefab.
    ///   4. Assign the same prefab to NetworkBootstrap's "Session State Prefab" field (the host
    ///      spawns it automatically when the server starts).
    /// If this isn't set up, everything still works — the spawners fall back to Inventory's own
    /// networked seed (see Inventory.WorldSeed), and the lobby keeps its own roster.
    /// </summary>
    public class SessionState : NetworkBehaviour
    {
        public static SessionState Instance { get; private set; }

        // ── World seed ─────────────────────────────────────────────────────────────
        private readonly NetworkVariable<int> netWorldSeed = new NetworkVariable<int>(0);
        /// <summary>Shared world seed (0 until the host has seeded it).</summary>
        public int WorldSeed => netWorldSeed.Value;

        /// <summary>The seed the world spawners should use: SessionState's if it's up, otherwise the
        /// SampleScene Inventory's own networked seed (so this all still works even if the
        /// SessionState prefab hasn't been set up in the editor yet). 0 = not ready / offline.</summary>
        public static int EffectiveWorldSeed
        {
            get
            {
                if (Instance != null && Instance.WorldSeed != 0) return Instance.WorldSeed;
                return Inventory.WorldSeed;
            }
        }

        // ── Difficulty / mode (mirror of the host's menu choice, so all peers agree) ─
        private readonly NetworkVariable<int> netDifficulty = new NetworkVariable<int>(0);
        private readonly NetworkVariable<int> netGameMode   = new NetworkVariable<int>(0);
        public Difficulty Difficulty => (Difficulty)netDifficulty.Value;
        public GameMode   Mode       => (GameMode)netGameMode.Value;

        // ── Night count (2/4/7). Host-seeded so a joiner plays the HOST's chosen length,
        //    whatever they picked on their own menu. ──
        private readonly NetworkVariable<int> netTotalNights = new NetworkVariable<int>(0);
        public int TotalNights => netTotalNights.Value;

        /// <summary>The night count everyone should use: the host's replicated choice if SessionState
        /// is up, otherwise this peer's own menu pick (offline / not-set-up fallback).</summary>
        public static int EffectiveTotalNights
        {
            get
            {
                if (Instance != null && Instance.TotalNights > 0) return Instance.TotalNights;
                return DifficultyPreference.TotalNights;
            }
        }

        // ── Roster (clientId + username), survives scene loads ──────────────────────
        private readonly NetworkList<PlayerEntry> roster = new NetworkList<PlayerEntry>();
        /// <summary>Fires on every peer whenever the roster changes.</summary>
        public event Action OnRosterChanged;

        public int PlayerCount => roster.Count;

        public string GetUsername(ulong clientId)
        {
            for (int i = 0; i < roster.Count; i++)
                if (roster[i].clientId == clientId) return roster[i].username.ToString();
            return string.Empty;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            // Persist across the menu→game scene load on EVERY peer. This is the documented NGO
            // recipe for a "persistent manager" NetworkObject.
            DontDestroyOnLoad(gameObject);

            roster.OnListChanged += HandleRosterChanged;

            if (IsServer)
            {
                if (netWorldSeed.Value == 0)
                    netWorldSeed.Value = UnityEngine.Random.Range(1, int.MaxValue);
                // Mirror the host's menu selection so clients can read it too (the monster reads it
                // server-side, but this keeps everyone consistent + ready for a future UI).
                netDifficulty.Value = (int)DifficultyPreference.Selected;
                netGameMode.Value   = (int)DifficultyPreference.Mode;
                netTotalNights.Value = Mathf.Max(1, DifficultyPreference.TotalNights);
            }

            OnRosterChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            roster.OnListChanged -= HandleRosterChanged;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();   // let NGO run its NetworkBehaviour teardown
        }

        private void HandleRosterChanged(NetworkListEvent<PlayerEntry> _) => OnRosterChanged?.Invoke();

        /// <summary>SERVER-ONLY. Add or update a player's roster entry. Called from
        /// NetworkLobby's server-side registration so the persistent roster mirrors the lobby.</summary>
        public void ServerRegister(ulong clientId, string username)
        {
            if (!IsServer) return;
            if (string.IsNullOrWhiteSpace(username)) username = "Player";
            if (username.Length > 18) username = username.Substring(0, 18);
            var entry = new PlayerEntry { clientId = clientId, username = new FixedString32Bytes(username) };
            for (int i = 0; i < roster.Count; i++)
            {
                if (roster[i].clientId == clientId) { roster[i] = entry; return; }
            }
            roster.Add(entry);
        }

        /// <summary>SERVER-ONLY. Remove a player (disconnect).</summary>
        public void ServerRemove(ulong clientId)
        {
            if (!IsServer) return;
            for (int i = roster.Count - 1; i >= 0; i--)
                if (roster[i].clientId == clientId) roster.RemoveAt(i);
        }
    }
}
