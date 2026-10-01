using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.Net
{
    /// <summary>
    /// Auto-starts a LOCAL NGO host the moment SampleScene loads — but ONLY in single-player mode.
    /// In multiplayer the host (or client) is already running, kicked off from the MainMenu lobby.
    ///
    /// Why this exists: when we set up multiplayer we deleted the scene-placed PlayerCapsule so
    /// NGO could spawn one player prefab per connected client instead. That broke single-player
    /// (no scene player, NGO not running → no player at all). This script fixes that by booting
    /// NGO into local host mode for SP so the same player-prefab spawn flow runs.
    ///
    /// SETUP: drop this on any GameObject in SampleScene (the GameClock GameObject is a fine home).
    /// No Inspector fields to set.
    /// </summary>
    public class SceneNetworkBootstrap : MonoBehaviour
    {
        private void Start()
        {
            if (NetworkManager.Singleton == null)
            {
                Debug.LogWarning("[SceneNetworkBootstrap] No NetworkManager.Singleton found — " +
                                 "single-player can't spawn a player. Did you forget to put the " +
                                 "NetworkManager in the MainMenu scene?", this);
                return;
            }

            // Already running (we got here via the multiplayer lobby) — leave it alone.
            if (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient) return;

            if (DifficultyPreference.Mode == GameMode.SinglePlayer)
            {
                Debug.Log("[SceneNetworkBootstrap] Single-player: starting local NGO host so the " +
                          "player prefab spawns. (No Relay, no networking — just the host code path.)");
                NetworkManager.Singleton.StartHost();
            }
        }
    }
}
