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
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
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
            if (string.IsNullOrWhiteSpace(username)) username = "Player";
            username = username.Trim();
            if (username.Length > 18) username = username.Substring(0, 18);
            // Stash it in a scene-independent static so the local player's NameTag can publish it
            // onto the player object (which survives the menu→game scene load, unlike this lobby).
            NetworkBootstrap.LocalUsername = username;
            RegisterUsernameServerRpc(username);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RegisterUsernameServerRpc(string username, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
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
        private void StartGameServerRpc() => StartGameInternal();

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
