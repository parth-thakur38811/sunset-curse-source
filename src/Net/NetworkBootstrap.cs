using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace SunsetCurse.Net
{
    /// <summary>
    /// Handles the "press a button → become host" / "type a code → join as client" flow
    /// using Unity Gaming Services (UGS) + Relay. Relay punches through home routers
    /// for us so we don't have to set up port forwarding — perfect for a small co-op
    /// game on the free tier.
    ///
    /// FLOW:
    ///   HOST   → InitServices → SignInAnonymously → RelayService.CreateAllocation
    ///          → GetJoinCode (the string you share) → UnityTransport.SetRelayServerData
    ///          → NetworkManager.StartHost
    ///   CLIENT → InitServices → SignInAnonymously → RelayService.JoinAllocation(code)
    ///          → UnityTransport.SetRelayServerData → NetworkManager.StartClient
    ///
    /// ONE-TIME UNITY DASHBOARD SETUP (per project, not per machine):
    ///   1. Edit ▸ Project Settings ▸ Services → link this project to a Unity Cloud project.
    ///   2. Open the Unity Dashboard for that project → enable **Relay** (free tier is fine).
    ///   3. (Authentication is enabled automatically by signing in anonymously below.)
    ///
    /// SETUP IN SCENE: drop this component on the same GameObject as NetworkManager.
    /// </summary>
    public class NetworkBootstrap : MonoBehaviour
    {
        public static NetworkBootstrap Instance { get; private set; }

        [Tooltip("Max players we'll allow in a single co-op session (1 host + 3 clients = 4).")]
        [SerializeField] private int maxConnections = 4;

        [Tooltip("Relay connection type. 'dtls' = encrypted (recommended); 'udp' = unencrypted.")]
        [SerializeField] private string relayConnectionType = "dtls";

        [Tooltip("The persistent SessionState prefab (NetworkObject + SessionState). The HOST spawns " +
                 "one automatically when the server starts; it survives the menu→game scene load and " +
                 "carries the shared world seed / difficulty / roster. Leave empty to skip it (systems " +
                 "fall back to Inventory's own seed + the lobby roster). Must also be registered in " +
                 "NetworkManager's Network Prefabs list.")]
        [SerializeField] private GameObject sessionStatePrefab;

        /// <summary>The local player's chosen username. Set once from the menu (via
        /// <see cref="NetworkLobby.RegisterMyUsername"/>). Static so it survives the menu→game
        /// scene transition — the per-player NameTag reads it on spawn and publishes it as an
        /// owner-write NetworkVariable that travels with the player into the gameplay scene.</summary>
        public static string LocalUsername { get; set; }

        /// <summary>Last Relay allocation's join code — the short string the host shares.</summary>
        public string JoinCode { get; private set; }

        /// <summary>Status string the UI shows ("Initialising...", "Host ready: ABC123", ...).</summary>
        public string Status { get; private set; } = "Idle";

        /// <summary>True while a host/join async operation is in flight (UI disables buttons).</summary>
        public bool IsBusy { get; private set; }

        public event Action OnStatusChanged;

        private bool servicesReady;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // Spawn the persistent SessionState the moment the server starts — covers BOTH the
            // multiplayer host (StartHostWithRelay) and single-player (SceneNetworkBootstrap.StartHost),
            // since both raise OnServerStarted. Runs once; the object DontDestroyOnLoads itself.
            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.OnServerStarted += SpawnSessionState;
        }

        private void OnDisable()
        {
            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.OnServerStarted -= SpawnSessionState;
        }

        private void SpawnSessionState()
        {
            if (sessionStatePrefab == null) return;                 // not set up — fall back paths handle it
            if (SessionState.Instance != null) return;              // already spawned
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

            var go = Instantiate(sessionStatePrefab);
            var netObj = go.GetComponent<NetworkObject>();
            if (netObj == null)
            {
                Debug.LogError("[NetworkBootstrap] SessionState prefab has no NetworkObject — cannot spawn.", this);
                Destroy(go);
                return;
            }
            netObj.Spawn();   // SessionState.OnNetworkSpawn calls DontDestroyOnLoad on every peer
            Debug.Log("[NetworkBootstrap] Spawned persistent SessionState.", this);
        }

        // --- UGS init (anonymous sign-in is free and requires no player account). ---

        private async Task EnsureServicesReadyAsync()
        {
            if (servicesReady) return;
            SetStatus("Initialising Unity Services...");
            await UnityServices.InitializeAsync();
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            servicesReady = true;
            SetStatus($"Signed in (PlayerId {AuthenticationService.Instance.PlayerId})");
        }

        // --- Host: create a Relay allocation and start the host. ---

        public async void StartHostWithRelay()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await EnsureServicesReadyAsync();

                SetStatus("Creating Relay allocation...");
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
                JoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                ApplyRelayServerData(new RelayServerData(allocation, relayConnectionType));

                if (!NetworkManager.Singleton.StartHost())
                {
                    SetStatus("StartHost failed (NetworkManager rejected). Check console.");
                    return;
                }

                SetStatus($"HOST ready — share this join code: {JoinCode}");
            }
            catch (Exception ex)
            {
                SetStatus($"Host failed: {ex.Message}");
                Debug.LogException(ex, this);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // --- Client: take a join code and connect to the host. ---

        public async void StartClientWithRelay(string joinCode)
        {
            if (IsBusy) return;
            if (string.IsNullOrWhiteSpace(joinCode))
            {
                SetStatus("Join code is empty.");
                return;
            }
            IsBusy = true;
            try
            {
                await EnsureServicesReadyAsync();

                string code = joinCode.Trim().ToUpperInvariant();
                SetStatus($"Joining Relay with code {code}...");
                JoinAllocation join = await RelayService.Instance.JoinAllocationAsync(code);

                ApplyRelayServerData(new RelayServerData(join, relayConnectionType));

                if (!NetworkManager.Singleton.StartClient())
                {
                    SetStatus("StartClient failed (NetworkManager rejected). Check console.");
                    return;
                }

                SetStatus($"Connecting to host via Relay ({code})...");
            }
            catch (Exception ex)
            {
                SetStatus($"Join failed: {ex.Message}");
                Debug.LogException(ex, this);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static void ApplyRelayServerData(RelayServerData data)
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport == null)
            {
                throw new InvalidOperationException(
                    "NetworkManager is missing a UnityTransport component. " +
                    "Add one in the Inspector and assign it as the Network Transport.");
            }
            transport.SetRelayServerData(data);
        }

        private void SetStatus(string status)
        {
            Status = status;
            Debug.Log($"[NetworkBootstrap] {status}");
            OnStatusChanged?.Invoke();
        }
    }
}
