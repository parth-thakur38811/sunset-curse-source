using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.Audio;
using SunsetCurse.Player;

namespace SunsetCurse.World
{
    /// <summary>
    /// "Unreliable senses": at night (and optionally daytime), occasionally plays a monster-like
    /// sound from a random direction near a player. Mixed with the real monster's directional
    /// sounds, ears stop being a reliable tracker.
    ///
    /// NETWORKING (2026-06-30): converted to NetworkBehaviour. Server schedules + picks a random
    /// alive player to anchor the cue around, then broadcasts (clipIndex, worldPos, volume) via
    /// ClientRpc so EVERY peer hears the SAME red-herring at the SAME spot — previously each peer
    /// rolled its own cue and teammates heard different sounds at different positions, undermining
    /// the "we're all hearing the same thing" horror beat.
    ///
    /// SETUP: drop on a scene GameObject + add a NetworkObject so it auto-spawns. Assign cue clips.
    /// </summary>
    public class FalseCues : NetworkBehaviour
    {
        [Tooltip("Fake monster sounds — growls, footsteps, branch snaps, whispers.")]
        [SerializeField] private AudioClip[] cues;

        [Header("Night cadence (loud + frequent)")]
        [SerializeField] private float minInterval = 9f;
        [SerializeField] private float maxInterval = 22f;
        [Range(0f, 1f)] [SerializeField] private float volume = 0.8f;

        [Header("Daytime cadence (subtle — extends the dread into the safe hours)")]
        [SerializeField] private bool daytimeAlsoEnabled = true;
        [SerializeField] private float daytimeMinInterval = 30f;
        [SerializeField] private float daytimeMaxInterval = 75f;
        [Range(0f, 1f)] [SerializeField] private float daytimeVolume = 0.35f;

        [Header("Position around the player")]
        [SerializeField] private float minDistance = 8f;
        [SerializeField] private float maxDistance = 24f;

        [Header("Legacy")]
        [Tooltip("Kept for back-compat. Uncheck both this AND 'Daytime Also Enabled' to disable.")]
        [SerializeField] private bool nightOnly = true;

        private float nextTime;

        public override void OnNetworkSpawn()
        {
            if (IsSpawned && !IsServer) return;   // only server schedules
            ScheduleNext(true);
        }

        private void Update()
        {
            if (IsSpawned && !IsServer) return;
            if (cues == null || cues.Length == 0) return;

            bool isNight = GameClock.Instance == null ||
                           GameClock.Instance.CurrentPhase == GameClock.Phase.Night;

            if (isNight)
            {
                if (!nightOnly && !daytimeAlsoEnabled) return;
            }
            else
            {
                if (!daytimeAlsoEnabled) return;
            }

            if (Time.time < nextTime) return;

            // Pick a random alive player to anchor the cue around. Skips if none alive (during
            // game-over transition etc).
            Transform anchor = PickRandomAlivePlayer();
            if (anchor == null) { ScheduleNext(isNight); return; }

            // Roll position + clip on the server, broadcast results so every peer plays the
            // SAME sound at the SAME spot.
            Vector2 dir = Random.insideUnitCircle.normalized;
            float d = Random.Range(minDistance, maxDistance);
            Vector3 pos = anchor.position + new Vector3(dir.x, 0f, dir.y) * d;
            int idx = Random.Range(0, cues.Length);
            float vol = isNight ? volume : daytimeVolume;

            if (IsSpawned) BroadcastCueClientRpc(idx, pos, vol);
            else           PlayCueLocal(idx, pos, vol);  // SP fallback (no NGO)

            ScheduleNext(isNight);
        }

        private Transform PickRandomAlivePlayer()
        {
            // Use PlayerInventory's networked IsAlive to find alive players. PlayerObject is on
            // the same GameObject.
            var alivePlayers = new List<Transform>();
            foreach (var inv in FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None))
            {
                if (inv != null && inv.IsAlive) alivePlayers.Add(inv.transform);
            }
            if (alivePlayers.Count == 0) return null;
            return alivePlayers[Random.Range(0, alivePlayers.Count)];
        }

        private void ScheduleNext(bool isNight)
        {
            float min = isNight ? minInterval : daytimeMinInterval;
            float max = isNight ? maxInterval : daytimeMaxInterval;
            nextTime = Time.time + Random.Range(min, max);
        }

        [ClientRpc]
        private void BroadcastCueClientRpc(int clipIdx, Vector3 pos, float vol) => PlayCueLocal(clipIdx, pos, vol);

        private void PlayCueLocal(int clipIdx, Vector3 pos, float vol)
        {
            if (cues == null || clipIdx < 0 || clipIdx >= cues.Length) return;
            if (AudioManager.Instance == null) return;
            AudioManager.Instance.PlaySfx3D(cues[clipIdx], pos, vol, maxDistance + 20f);
        }
    }
}
