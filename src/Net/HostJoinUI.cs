using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.Net
{
    /// <summary>
    /// The on-screen lobby for Sunset Curse co-op. Built as a single OnGUI panel for the vertical
    /// slice — pragmatic, zero scene setup, easy for a beginner to reason about. (We'll polish into
    /// a Canvas/TMP main-menu integration later.)
    ///
    /// Flow:
    ///   PRE-LOBBY  → Username text field + HOST / JOIN buttons. Visible ONLY in multiplayer mode.
    ///   IN-LOBBY   → Role (HOST/CLIENT) + join code + list of connected usernames. The HOST also
    ///                sees a START button + transient "XYZ has joined" toasts.
    ///   IN-GAME    → Panel hides itself (game has begun). Disconnect button still available.
    ///
    /// Single-player mode: this panel never draws. Press Play in the menu with Single Player
    /// selected and you go straight into the game (the GameClock isn't paused).
    /// </summary>
    public class HostJoinUI : MonoBehaviour
    {
        [SerializeField] private Vector2 panelPosition = new Vector2(20, 20);
        [SerializeField] private float panelWidth = 360f;

        private string usernameInput = "";
        private string joinCodeInput = "";

        // Lobby state cached for UI display.
        private readonly Dictionary<ulong, string> knownUsernames = new Dictionary<ulong, string>();
        private string lastJoinMessage;
        private float lastJoinTime;
        private const float JoinMessageDuration = 4f;

        // Subscription bookkeeping.
        private bool lobbySubscribed;
        private bool usernameRegistered;

        private void Update()
        {
            // Lazily subscribe to NetworkLobby once it spawns.
            if (!lobbySubscribed && NetworkLobby.Instance != null && NetworkLobby.Instance.IsSpawned)
            {
                NetworkLobby.Instance.OnPlayerListChanged += RebuildKnownUsernames;
                NetworkLobby.Instance.OnPlayerJoined += HandlePlayerToast;
                lobbySubscribed = true;
                RebuildKnownUsernames();   // populate immediately from the current roster
            }

            // Auto-register OUR username once we're connected and the lobby exists.
            if (!usernameRegistered
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.IsConnectedClient
                && NetworkLobby.Instance != null
                && NetworkLobby.Instance.IsSpawned)
            {
                NetworkLobby.Instance.RegisterMyUsername(string.IsNullOrWhiteSpace(usernameInput) ? "Player" : usernameInput);
                usernameRegistered = true;
            }

            // Reset on full disconnect so we can rejoin cleanly.
            if (NetworkManager.Singleton == null || (!NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsClient))
            {
                usernameRegistered = false;
            }
        }

        private void RebuildKnownUsernames()
        {
            knownUsernames.Clear();
            if (NetworkLobby.Instance == null) return;
            foreach (var entry in NetworkLobby.Instance.Players)
                knownUsernames[entry.clientId] = entry.username.ToString();
        }

        private void HandlePlayerToast(string username)
        {
            lastJoinMessage = $"{username} has joined.";
            lastJoinTime = Time.unscaledTime;
        }

        // ─────────────────────────────── OnGUI ───────────────────────────────

        private void OnGUI()
        {
            // Single-player mode: never show the lobby. The menu's Play button goes straight in.
            if (DifficultyPreference.Mode != GameMode.Multiplayer) return;

            var nm = NetworkManager.Singleton;
            bool connected = nm != null && (nm.IsServer || nm.IsClient);

            if (!connected) { DrawPreLobby(); return; }

            // Connected but game not started → in-lobby. Connected AND started → in-game (compact bar).
            // If NetworkLobby is missing entirely (e.g. the MainMenu-scene NetworkLobby has despawned
            // after the host-driven scene transition), assume the game is started — we're past the lobby.
            bool started = NetworkLobby.Instance == null || NetworkLobby.Instance.GameStarted;
            if (!started) DrawInLobby(nm);
            else          DrawInGame(nm);
        }

        // -------- PRE-LOBBY --------
        private void DrawPreLobby()
        {
            GUILayout.BeginArea(new Rect(panelPosition.x, panelPosition.y, panelWidth, 240), GUI.skin.box);

            GUILayout.Label("Sunset Curse — Co-op Lobby");

            bool busy = NetworkBootstrap.Instance != null && NetworkBootstrap.Instance.IsBusy;
            GUI.enabled = !busy;

            GUILayout.Label("Username (this session only):");
            usernameInput = GUILayout.TextField(usernameInput ?? "", 18);

            GUILayout.Space(6);

            if (GUILayout.Button("HOST (create Relay session)"))
            {
                NetworkBootstrap.Instance?.StartHostWithRelay();
            }

            GUILayout.Space(6);
            GUILayout.Label("Join code:");
            joinCodeInput = GUILayout.TextField(joinCodeInput ?? "", 6);
            if (GUILayout.Button("JOIN"))
            {
                NetworkBootstrap.Instance?.StartClientWithRelay(joinCodeInput);
            }

            GUI.enabled = true;

            GUILayout.Space(8);
            string status = NetworkBootstrap.Instance != null ? NetworkBootstrap.Instance.Status : "(no NetworkBootstrap)";
            GUILayout.Label($"Status: {status}");
            GUILayout.EndArea();
        }

        // -------- IN-LOBBY (connected, waiting for START) --------
        private void DrawInLobby(NetworkManager nm)
        {
            float h = 230f;
            GUILayout.BeginArea(new Rect(panelPosition.x, panelPosition.y, panelWidth, h), GUI.skin.box);

            string role = nm.IsHost ? "HOST" : (nm.IsServer ? "SERVER" : "CLIENT");
            GUILayout.Label($"Role: {role}");

            string joinCode = NetworkBootstrap.Instance?.JoinCode;
            if (!string.IsNullOrEmpty(joinCode))
                GUILayout.Label($"Join code: {joinCode}    (share with friends)");

            GUILayout.Label($"Players ({knownUsernames.Count}/4):");
            foreach (var kv in knownUsernames)
                GUILayout.Label($"  • {kv.Value}");

            GUILayout.Space(6);

            // HOST-only START button.
            if (nm.IsHost)
            {
                if (GUILayout.Button("START GAME"))
                {
                    NetworkLobby.Instance?.StartGame();
                }
            }
            else
            {
                GUILayout.Label("Waiting for host to press START…");
            }

            if (GUILayout.Button("Disconnect")) nm.Shutdown();

            GUILayout.EndArea();

            DrawJoinToast();
        }

        // -------- IN-GAME (game running) --------
        private void DrawInGame(NetworkManager nm)
        {
            GUILayout.BeginArea(new Rect(panelPosition.x, panelPosition.y, panelWidth, 60), GUI.skin.box);
            string role = nm.IsHost ? "HOST" : "CLIENT";
            GUILayout.Label($"{role} — connected players: {knownUsernames.Count}");
            if (GUILayout.Button("Disconnect")) nm.Shutdown();
            GUILayout.EndArea();

            DrawJoinToast();
        }

        // Transient "XYZ has joined." toast, top-centre. Visible everywhere once subscribed.
        private void DrawJoinToast()
        {
            if (string.IsNullOrEmpty(lastJoinMessage)) return;
            float age = Time.unscaledTime - lastJoinTime;
            if (age > JoinMessageDuration) return;

            float fade = 1f - Mathf.Clamp01(age / JoinMessageDuration);
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, fade);

            float w = 360f, h = 50f;
            var rect = new Rect((Screen.width - w) * 0.5f, 30f, w, h);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label(lastJoinMessage);
            GUILayout.EndArea();

            GUI.color = prev;
        }
    }
}
