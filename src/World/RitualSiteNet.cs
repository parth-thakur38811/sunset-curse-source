using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.Player;

namespace SunsetCurse.World
{
    /// <summary>
    /// Networked state holder for <see cref="RitualSite"/>. Lives as a SIBLING component on the
    /// altar GameObject (RitualSite stays a plain Interactable so PlayerInteractor can still find
    /// it; this NetworkBehaviour holds the server-authoritative NetworkVariables + RPCs).
    ///
    /// Server-only tick: ticks progress, checks channeler distance, applies PlayerNoise.ExtraNoise.
    /// Every peer reads the NetworkVariables to drive the local progress bar.
    ///
    /// SETUP: same GameObject as RitualSite — add this script + a NetworkObject component.
    /// </summary>
    public class RitualSiteNet : NetworkBehaviour
    {
        [Header("Ritual")]
        [SerializeField] private float channelDuration = 18f;
        [SerializeField] private float channelRadius = 6f;
        [SerializeField] private float ritualNoiseRadius = 70f;

        [Header("River-water offering")]
        [Tooltip("The ritual needs river water: a player carrying the FILLED bucket (see " +
                 "RiverWaterBucket) must stand inside the ritual area (Channel Radius) for the " +
                 "ritual to start AND to progress. If the carrier steps out mid-ritual, progress " +
                 "PAUSES (it does not reset) until they return. Untick to restore the old " +
                 "water-free ritual.")]
        [SerializeField] private bool requireRiverWater = true;

        private readonly NetworkVariable<float> netProgress          = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<bool>  netChanneling        = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<bool>  netCompleted         = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<ulong> netChannelerClientId = new NetworkVariable<ulong>(0);
        // True while the channel is PAUSED because the filled-bucket carrier left the area —
        // replicated so every peer's RitualSite UI can show the "offering must remain" hint.
        private readonly NetworkVariable<bool>  netWaterMissing      = new NetworkVariable<bool>(false);

        public float Progress    => netProgress.Value;
        public bool  Channeling  => netChanneling.Value;
        public bool  Completed   => netCompleted.Value;
        public bool  WaterMissing => netWaterMissing.Value;
        public bool  RequiresRiverWater => requireRiverWater;
        public float ChannelRadius => channelRadius;

        /// <summary>Is any alive, standing player carrying the FILLED bucket within
        /// <paramref name="radius"/> of <paramref name="pos"/>? Readable on EVERY peer (carry
        /// flags + positions replicate), so RitualSite can pre-check before asking the server.
        /// A downed carrier doesn't count — the offering must be held up, not bled on.</summary>
        public static bool FilledBucketCarrierNear(Vector3 pos, float radius)
        {
            var all = PlayerInventory.All;
            for (int i = 0; i < all.Count; i++)
            {
                var inv = all[i];
                if (inv == null || !inv.IsAlive || inv.IsDowned) continue;
                if (!inv.HasFilledBucket) continue;
                if (Vector3.Distance(inv.transform.position, pos) <= radius) return true;
            }
            return false;
        }

        /// <summary>Called by RitualSite.Interact (locally on the player who pressed E).</summary>
        public void RequestStartChannel()
        {
            if (!IsSpawned) { StartLocal(NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0); return; }
            if (IsServer) StartServerSide(NetworkManager.Singleton.LocalClientId);
            else          BeginChannelServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void BeginChannelServerRpc(ServerRpcParams p = default) => StartServerSide(p.Receive.SenderClientId);

        private void StartServerSide(ulong clientId)
        {
            if (netChanneling.Value || netCompleted.Value) return;
            if (EscapeTracker.Instance == null || !EscapeTracker.Instance.HasEnoughToEscape) return;
            // The offering must be at the altar to BEGIN (RitualSite pre-checks locally for the
            // player-facing message; this is the authoritative gate).
            if (requireRiverWater && !FilledBucketCarrierNear(transform.position, channelRadius)) return;
            netChannelerClientId.Value = clientId;
            netProgress.Value = 0f;
            netChanneling.Value = true;
            // Hard override: EVERY monster drops what it's doing and heads for the altar. The
            // ritual can no longer be trivialised by decoying the stalker across the map first.
            SunsetCurse.AI.MonsterAlerts.ReportRitualStarted(transform.position);
        }

        private void StartLocal(ulong clientId)
        {
            // SP path — direct write (NetworkVariable acts as a plain field offline).
            if (netChanneling.Value || netCompleted.Value) return;
            if (EscapeTracker.Instance == null || !EscapeTracker.Instance.HasEnoughToEscape) return;
            if (requireRiverWater && !FilledBucketCarrierNear(transform.position, channelRadius)) return;
            netChannelerClientId.Value = clientId;
            netProgress.Value = 0f;
            netChanneling.Value = true;
            SunsetCurse.AI.MonsterAlerts.ReportRitualStarted(transform.position);
        }

        private void Update()
        {
            // SERVER ONLY past this point. Clients just read NetworkVariables from RitualSite.
            if (IsSpawned && !IsServer) return;
            if (!netChanneling.Value || netCompleted.Value) return;

            Transform channelerT = FindChannelerTransform(out PlayerInventory inv, out PlayerNoise noise);
            if (channelerT == null || (inv != null && !inv.IsAlive))
            {
                CancelServer();
                return;
            }

            bool near = Vector3.Distance(channelerT.position, transform.position) <= channelRadius;
            if (!near)
            {
                // Left the ritual zone → the channel BREAKS: hide the bar (netChanneling → false)
                // and RESET progress to 0, so re-entering and pressing E starts a fresh attempt.
                CancelServer();
                return;
            }

            // THE OFFERING: the ritual needs river water. The CHANNELER leaving still CANCELS
            // (above, unchanged) — but the FILLED-bucket carrier leaving only PAUSES: progress
            // holds where it is and resumes the moment they step back inside the circle.
            if (requireRiverWater && !FilledBucketCarrierNear(transform.position, channelRadius))
            {
                if (!netWaterMissing.Value) netWaterMissing.Value = true;
                // The ritual still blazes while paused — the monsters keep being drawn in.
                if (noise != null) noise.ExtraNoise = ritualNoiseRadius;
                return;
            }
            if (netWaterMissing.Value) netWaterMissing.Value = false;

            netProgress.Value = Mathf.Clamp01(netProgress.Value + Time.deltaTime / channelDuration);
            if (noise != null) noise.ExtraNoise = ritualNoiseRadius;

            if (netProgress.Value >= 1f) CompleteServer();
        }

        // Returns the channeler's transform + their networked PlayerInventory (for the ONLY
        // trustworthy alive check on the host) + PlayerNoise (to drive the ritual's noise draw).
        private Transform FindChannelerTransform(out PlayerInventory inv, out PlayerNoise noise)
        {
            inv = null; noise = null;
            if (NetworkManager.Singleton == null) return null;
            if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(netChannelerClientId.Value, out var nc))
                return null;
            if (nc.PlayerObject == null) return null;
            inv = nc.PlayerObject.GetComponent<PlayerInventory>();
            noise = nc.PlayerObject.GetComponent<PlayerNoise>();
            return nc.PlayerObject.transform;
        }

        private void CompleteServer()
        {
            netChanneling.Value = false;
            netCompleted.Value = true;
            netProgress.Value = 1f;
            netWaterMissing.Value = false;
            _ = FindChannelerTransform(out _, out var noise);
            if (noise != null) noise.ExtraNoise = 0f;
            // With the RADIO TOWER ESCAPE installed, the ritual doesn't win on its own — the win
            // needs BOTH objectives (ritual + SOS transmission), in either order. If the SOS is
            // already out, this ritual completion is the second objective → victory now (the
            // RadioTowerState makes that call). Without a RadioTowerState: classic instant win.
            if (RadioTowerState.Instance == null)
            {
                if (GameClock.Instance != null) GameClock.Instance.TriggerEscape();   // → Victory
            }
            else
            {
                RadioTowerState.Instance.NotifyRitualCompletedServer();
            }
        }

        private void CancelServer()
        {
            _ = FindChannelerTransform(out _, out var noise);
            if (noise != null) noise.ExtraNoise = 0f;
            netChanneling.Value = false;
            netProgress.Value = 0f;
            netWaterMissing.Value = false;
        }
    }
}
