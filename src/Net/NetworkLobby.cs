using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.Net
{
    /// <summary>One entry in the lobby roster. INetworkSerializable + IEquatable so it can live
    /// inside a <see cref="NetworkList{T}"/>.</summary>
    public struct PlayerEntry : INetworkSerializable, IEquatable<PlayerEntry>
    {
        public ulong clientId;
        public FixedString32Bytes username;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref clientId);
            s.SerializeValue(ref username);
        }

        public bool Equals(PlayerEntry other) => clientId == other.clientId && username.Equals(other.username);
        public override bool Equals(object obj) => obj is PlayerEntry pe && Equals(pe);
        public override int GetHashCode() => clientId.GetHashCode() ^ username.GetHashCode();
    }

    /// <summary>
    /// Multiplayer lobby brain — now backed by a <see cref="NetworkList{PlayerEntry}"/> so late
    /// joiners auto-receive the full current roster (previously a Dictionary + broadcast, which
    /// meant the late client only saw players who joined AFTER them — that's the lobby bug).
    /// Also removes entries on disconnect via NetworkManager.OnClientDisconnectCallback.
    ///
    /// SETUP: drop on NetworkManager GameObject + add a NetworkObject component.
    /// </summary>
    public class NetworkLobby : NetworkBehaviour
    {
        public static NetworkLobby Instance { get; private set; }

        [SerializeField] private string gameSceneName = "SampleScene";

        private readonly NetworkVariable<bool> gameStarted = new NetworkVariable<bool>(false);
        private readonly NetworkList<PlayerEntry> players = new NetworkList<PlayerEntry>();

        /// <summary>Fires on every peer whenever the player list mutates (add / remove / change).</summary>
        public event Action OnPlayerListChanged;
        /// <summary>Fires on every peer when a NEW player joins (for the "X has joined" toast).</summary>
        public event Action<string> OnPlayerJoined;
        /// <summary>Fires on every peer when host's START takes effect.</summary>
        public event Action OnGameStarted;

        public bool GameStarted => gameStarted.Value;
        public int PlayerCount => players.Count;

        public IEnumerable<PlayerEntry> Players
        {
            get
            {
                for (int i = 0; i < players.Count; i++) yield return players[i];
            }
        }

        /// <summary>Lookup the username for a connected client (returns empty if unknown).</summary>
        public string GetUsername(ulong clientId)
        {
            for (int i = 0; i < players.Count; i++)
                if (players[i].clientId == clientId) return players[i].username.ToString();
            return string.Empty;
        }

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

        public override void OnNetworkSpawn()
        {
            if (Instance != this) return;   // disabled duplicate (see Awake) - stay inert
            gameStarted.OnValueChanged += HandleGameStartedChanged;
            players.OnListChanged += HandlePlayerListChanged;

            if (IsServer)
            {
                NetworkManager.OnClientDisconnectCallback += HandleClientDisconnect;
                if (DifficultyPreference.Mode == GameMode.Multiplayer && GameClock.Instance != null)
                {
                    GameClock.Instance.SetRunning(false);
                    Debug.Log("[NetworkLobby] Host: GameClock paused until START.", this);
                }
            }

            // Late-joiner / initial: fire once so subscribed UIs draw the current roster immediately.
            OnPlayerListChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            gameStarted.OnValueChanged -= HandleGameStartedChanged;
            players.OnListChanged -= HandlePlayerListChanged;
            if (IsServer && NetworkManager.Singleton != null)
                NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
        }

        private void HandlePlayerListChanged(NetworkListEvent<PlayerEntry> e)
        {
            OnPlayerListChanged?.Invoke();
            if (e.Type == NetworkListEvent<PlayerEntry>.EventType.Add)
                OnPlayerJoined?.Invoke(e.Value.username.ToString());
        }

        private void HandleClientDisconnect(ulong clientId)
        {
            if (!IsServer) return;
            for (int i = players.Count - 1; i >= 0; i--)
                if (players[i].clientId == clientId) players.RemoveAt(i);
            if (SessionState.Instance != null) SessionState.Instance.ServerRemove(clientId);
        }

        // ───────────────────────────── Username registration ─────────────────────────────

        /// <summary>Called by HostJoinUI / MainMenuController once we're connected — tells the
        /// host who we are. Server adds to (or updates) the NetworkList, which auto-syncs.</summary>
        public void RegisterMyUsername(string username)
        {
            username = ClampUsername(username);
            if (username.Length == 0) username = "Player";
            // Stash it in a scene-independent static so the local player's NameTag can publish it
            // onto the player object (which survives the menu→game scene load, unlike this lobby).
            NetworkBootstrap.LocalUsername = username;
            RegisterUsernameServerRpc(username);
        }

        /// <summary>Max username length in CHARACTERS (matches the menu input field's limit).</summary>
        public const int MaxUsernameChars = 18;

        // FixedString32Bytes stores at most 29 UTF-8 BYTES (32 minus length + terminator).
        private const int MaxUsernameBytes = 29;

        /// <summary>
        /// Makes a username safe to store in a <see cref="FixedString32Bytes"/>. That type holds 29
        /// BYTES, not 29 characters — Hindi letters take 3 bytes each and emoji take 4, so an
        /// 18-character name can be 54+ bytes. Unclamped, the editor throws an ArgumentException
        /// (the player's name never registers) and builds silently cut it short or blank. This
        /// trims to 18 characters AND 29 bytes, and never splits an emoji's surrogate pair.
        /// Use it everywhere a username becomes a FixedString.
        /// </summary>
        public static string ClampUsername(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            name = name.Trim();
            if (name.Length > MaxUsernameChars) name = name.Substring(0, MaxUsernameChars);
            // The character cap may have cut an emoji in half — drop a dangling high surrogate.
            if (name.Length > 0 && char.IsHighSurrogate(name[name.Length - 1]))
                name = name.Substring(0, name.Length - 1);
            while (name.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(name) > MaxUsernameBytes)
            {
                int cut = name.Length - 1;
                if (cut > 0 && char.IsLowSurrogate(name[cut])) cut--;   // remove the WHOLE emoji pair
                name = name.Substring(0, cut);
            }
            return name;
        }

        [ServerRpc(RequireOwnership = false)]
        private void RegisterUsernameServerRpc(string username, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            // Re-clamp on the SERVER too: the string arrives from the client, so never trust its
            // length — an oversized name would otherwise throw right here, on the host.
            username = ClampUsername(username);
            if (username.Length == 0) username = "Player";
            var entry = new PlayerEntry { clientId = clientId, username = new FixedString32Bytes(username) };

            // Mirror into the PERSISTENT roster so usernames survive into the gameplay scene
            // (this lobby object despawns when MainMenu unloads).
            if (SessionState.Instance != null) SessionState.Instance.ServerRegister(clientId, username);

            // Replace if already present (rename case).
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].clientId == clientId)
                {
                    players[i] = entry;
                    return;
                }
            }
            players.Add(entry);
        }

        // ───────────────────────────── Start-game gate ─────────────────────────────

        public void StartGame()
        {
            if (!IsSpawned) return;
            if (IsServer) StartGameInternal();
            else          StartGameServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void StartGameServerRpc(ServerRpcParams p = default)
        {
            // Only the HOST starts the match. The host takes the IsServer branch in StartGame()
            // and never comes through here, and the START button is host-only — so a request
            // arriving here from a client can only come from a modded build. Ignore it.
            if (p.Receive.SenderClientId != Unity.Netcode.NetworkManager.ServerClientId)
            {
                Debug.LogWarning($"[NetworkLobby] Ignored start request from client " +
                                 $"{p.Receive.SenderClientId} — only the host can start the game.");
                return;
            }
            StartGameInternal();
        }

        private void StartGameInternal()
        {
            if (gameStarted.Value) return;
            gameStarted.Value = true;
            if (GameClock.Instance != null) GameClock.Instance.SetRunning(true);
            Debug.Log("[NetworkLobby] Host pressed START — loading game scene for all peers.", this);

            if (NetworkManager != null && NetworkManager.SceneManager != null && !string.IsNullOrEmpty(gameSceneName))
                NetworkManager.SceneManager.LoadScene(gameSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
        }

        private void HandleGameStartedChanged(bool previous, bool next)
        {
            if (next) OnGameStarted?.Invoke();
        }
    }
}
