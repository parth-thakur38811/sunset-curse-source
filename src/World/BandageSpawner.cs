using System.Collections;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// ONE shared first-aid kit per dawn, at the SAME spot for every player.
    ///
    /// The spot is rolled from the team's shared WorldSeed XOR the day number — the same trick
    /// ResourceSpawner/BerrySpawner use for trees and stones — so every peer computes an
    /// identical position with zero network traffic. Claiming it goes through
    /// Inventory.RequestTakeBandage: first player to press E wins (server-validated), and the
    /// kit vanishes for the whole team. Yesterday's uncollected kit is reclaimed at dawn, so
    /// there's never more than one in the world.
    ///
    /// SETUP (unchanged): empty GameObject + this script; assign a bandage prefab (BandagePickup
    /// + a Collider) and the Map Center.
    /// </summary>
    public class BandageSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject bandagePrefab;

        [Header("Map area")]
        [SerializeField] private Transform mapCenter;
        [Tooltip("The kit can spawn anywhere up to this distance from the centre.")]
        [SerializeField] private float mapRadius = 120f;
        [Tooltip("The kit won't spawn closer to the centre than this (keeps it out of the village).")]
        [SerializeField] private float minRadius = 15f;

        [Header("Misc")]
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private int maxAttemptsPerBandage = 80;

        private GameObject current;   // this dawn's kit prop (local copy — every peer has its own)
        private int currentDay;

        private IEnumerator Start()
        {
            if (bandagePrefab == null) { Debug.LogWarning("[BandageSpawner] No prefab assigned.", this); yield break; }

            // Wait for the shared world seed (same gate as ResourceSpawner) so every peer rolls
            // the identical spot. Times out after a few seconds so odd setups still spawn locally.
            float deadline = Time.time + 12f;
            while (Inventory.WorldSeed == 0 && Time.time < deadline) yield return null;

            Inventory.OnBandageTaken += HandleTaken;
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnDayStart += SpawnForDay;
                SpawnForDay(GameClock.Instance.CurrentDay);   // today's kit (covers game start + late join)
            }
        }

        private void OnDestroy()
        {
            Inventory.OnBandageTaken -= HandleTaken;
            if (GameClock.Instance != null) GameClock.Instance.OnDayStart -= SpawnForDay;
        }

        private void SpawnForDay(int day)
        {
            currentDay = day;
            if (current != null) Destroy(current);        // reclaim yesterday's uncollected kit
            if (Inventory.BandageTakenDay == day) return; // already claimed (late joiner)

            Vector3 center = mapCenter != null ? mapCenter.position : transform.position;
            // Deterministic per-day RNG: identical on every peer, different each dawn.
            var rng = new System.Random(Inventory.WorldSeed ^ (day * 7919) ^ 0x0BADA1D);

            for (int attempt = 0; attempt < maxAttemptsPerBandage; attempt++)
            {
                float ang = (float)(rng.NextDouble() * System.Math.PI * 2.0);
                float dist = Mathf.Sqrt(Mathf.Lerp(minRadius * minRadius, mapRadius * mapRadius,
                                                   (float)rng.NextDouble()));
                Vector3 probe = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * dist;

                if (!Physics.Raycast(probe + Vector3.up * 200f, Vector3.down, out RaycastHit hit, 500f,
                                     groundMask, QueryTriggerInteraction.Ignore))
                    continue;

                current = Instantiate(bandagePrefab, hit.point + Vector3.up * 0.1f, Quaternion.identity, transform);
                var pickup = current.GetComponentInChildren<BandagePickup>();
                if (pickup != null) pickup.Configure(day);
                Debug.Log($"[BandageSpawner] Day {day} first-aid kit at {hit.point} (shared spot).");
                return;
            }
            Debug.LogWarning($"[BandageSpawner] Couldn't find a ground spot for the Day {day} kit.");
        }

        // Someone on the team claimed it → the local prop vanishes on every peer.
        private void HandleTaken(int day)
        {
            if (day == currentDay && current != null) Destroy(current);
        }
    }
}
