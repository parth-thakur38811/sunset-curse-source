using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.Net;

namespace SunsetCurse.World
{
    /// <summary>
    /// Scatters resource-node prefabs across the forest ring around a centre (the village). Same
    /// ring-sample pattern as <see cref="BerrySpawner"/>: pick a random angle + radius, raycast
    /// down, place the prefab there.
    ///
    /// Re-scatters at every DAWN (GameClock.OnDayStart, days 2-7) so the daily forage matters —
    /// yesterday's uncollected nodes despawn and a new randomised set takes their place. Inventory
    /// is NEVER touched by this; only the world-scattered prefabs are recycled.
    ///
    /// SETUP:
    ///   1. Empty GameObject "ResourceSpawner" + this script.
    ///   2. Set Forest Center to your village transform (or leave empty to use this object's spot).
    ///   3. For each entry, drop a prefab that has a ResourceNode component pre-configured
    ///      (e.g. a tree with ResourceNode.type = Wood), and set Count.
    /// </summary>
    public class ResourceSpawner : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Prefab to instantiate. Must have a ResourceNode component with the correct Type.")]
            public GameObject prefab;
            [Tooltip("How many of this prefab to place.")]
            public int count = 8;
            [Tooltip("ON = re-scatter this entry every dawn (Days 2–7). OFF = ONE-SHOT at Day 1 — " +
                     "once the players harvest them all, they're gone for the rest of the run. " +
                     "Off is the right default for RARE items like Ritual Herbs.")]
            public bool respawnDaily = false;
        }

        [Header("Forest zone (around the village)")]
        [SerializeField] private Transform forestCenter;
        [Tooltip("No nodes closer than this to the centre (keeps them out of the village).")]
        [SerializeField] private float minRadius = 25f;
        [Tooltip("No nodes farther than this from the centre.")]
        [SerializeField] private float maxRadius = 110f;

        [Header("Spawn list")]
        [SerializeField] private Entry[] entries;

        [Header("Misc")]
        [Tooltip("Which layers count as 'ground' for placing nodes. Everything is fine.")]
        [SerializeField] private LayerMask groundMask = ~0;
        [Tooltip("Random seed. 0 = different every game; any other = reproducible.")]
        [SerializeField] private int seed = 0;
        [Tooltip("Max attempts to find a spot for each node before giving up.")]
        [SerializeField] private int maxAttemptsPerNode = 15;

        private IEnumerator Start()
        {
            // Wait for the shared world seed so EVERY peer scatters the identical forest — this is
            // what makes "chop a tree, it despawns for everyone" reliable (node IDs are hashed from
            // position, so positions must match across machines). Offline SP falls straight through.
            yield return WaitForWorldSeed();

            // Day-1 scatter happens as soon as the seed is known so the world is populated when the
            // player spawns. From Day 2 onward we re-scatter ONLY entries marked respawnDaily.
            Scatter(initialDay: true);
            if (GameClock.Instance != null)
                GameClock.Instance.OnDayStart += HandleDayStart;
        }

        /// <summary>Block until a shared world seed is available (or a short timeout). If there's no
        /// NetworkManager we're offline single-player — no peers to match, so don't wait at all and
        /// let Scatter use its time-based fallback.</summary>
        private static IEnumerator WaitForWorldSeed()
        {
            if (NetworkManager.Singleton == null) yield break;
            float t = 0f;
            while (SessionState.EffectiveWorldSeed == 0 && t < 5f)
            {
                t += Time.deltaTime;
                yield return null;
            }
        }

        private void OnDestroy()
        {
            if (GameClock.Instance != null)
                GameClock.Instance.OnDayStart -= HandleDayStart;
        }

        private void HandleDayStart(int day)
        {
            // Day 1 was the initial Start() scatter; only respawn from Day 2 onwards.
            if (day <= 1) return;
            DespawnRespawningOnly();
            Scatter(initialDay: false);
            Debug.Log($"[ResourceSpawner] DAWN day {day} — re-scattered (respawning entries only).");
        }

        /// <summary>Destroy only the children we tagged as respawning. One-shot entries (e.g. the
        /// rare herb) survive the dawn so the player can still find them later in the week.</summary>
        private void DespawnRespawningOnly()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                var marker = child.GetComponent<SpawnedMarker>();
                if (marker != null && marker.respawnDaily) Destroy(child.gameObject);
            }
        }

        private void Scatter(bool initialDay)
        {
            if (entries == null || entries.Length == 0) return;

            Vector3 center = forestCenter != null ? forestCenter.position : transform.position;
            // Seed = explicit Inspector value if set, otherwise the SHARED world seed broadcast by
            // Inventory (so every peer in MP scatters the SAME forest). XOR with the current day
            // so respawning entries still differ from Day 1 to Day 2, etc.
            int effectiveSeed;
            if (seed != 0)
            {
                effectiveSeed = seed;
            }
            else
            {
                int worldSeed = SessionState.EffectiveWorldSeed;   // SessionState → else Inventory
                if (worldSeed == 0) worldSeed = (int)(System.DateTime.UtcNow.Ticks & 0x7FFFFFFF); // offline SP fallback
                int day = GameClock.Instance != null ? GameClock.Instance.CurrentDay : (initialDay ? 1 : 2);
                effectiveSeed = worldSeed ^ (day * 73856093);
            }
            var rng = new System.Random(effectiveSeed);

            int totalSpawned = 0;
            foreach (var e in entries)
            {
                if (e == null || e.prefab == null) continue;
                // Day 2+ only re-scatters respawning entries; one-shot stuff was placed at Day 1.
                if (!initialDay && !e.respawnDaily) continue;

                for (int i = 0; i < e.count; i++)
                {
                    if (TrySpawnOne(e.prefab, center, rng, e.respawnDaily)) totalSpawned++;
                }
            }
            Debug.Log($"[ResourceSpawner] {(initialDay ? "Day 1 initial" : "Dawn refresh")} — " +
                      $"placed {totalSpawned} nodes ({minRadius:0}–{maxRadius:0}m).");
        }

        private bool TrySpawnOne(GameObject prefab, Vector3 center, System.Random rng, bool respawning)
        {
            for (int attempt = 0; attempt < maxAttemptsPerNode; attempt++)
            {
                float ang = (float)(rng.NextDouble() * System.Math.PI * 2.0);
                float dist = Mathf.Sqrt(Mathf.Lerp(minRadius * minRadius, maxRadius * maxRadius,
                                                   (float)rng.NextDouble()));
                Vector3 probe = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * dist;

                if (!TryFindGroundUnder(probe, out RaycastHit hit)) continue;

                float yaw = (float)(rng.NextDouble() * 360.0);
                var spawned = Instantiate(prefab, hit.point, Quaternion.Euler(0f, yaw, 0f), transform);
                spawned.AddComponent<SpawnedMarker>().respawnDaily = respawning;   // remembered for dawn cleanup
                AddNavBlocker(spawned);   // spawned AFTER the navmesh bake → carve so monsters path around it

                // Assign the networked node ID so any peer chopping this tree despawns it for
                // every peer. Day is mixed in so day-2 respawns at colliding positions still get
                // unique IDs (not pre-consumed by day-1 grabs).
                var node = spawned.GetComponent<ResourceNode>();
                if (node != null)
                {
                    int day = GameClock.Instance != null ? GameClock.Instance.CurrentDay : 1;
                    int id = Inventory.ComputeNodeId(hit.point, node.Type, day);
                    node.SetNodeId(id);
                }
                return true;
            }
            return false;
        }

        /// <summary>Marker added to each spawned child so DespawnRespawningOnly can pick the
        /// right ones to recycle. Not networked — each peer manages their own scatter locally.</summary>
        private class SpawnedMarker : MonoBehaviour { public bool respawnDaily; }

        /// <summary>Runtime-spawned nodes appear after NavMeshBootstrap already baked the navmesh,
        /// so the bake can't know about them — give each a box carve-obstacle matching its collider.
        /// Players already collide with the collider; this keeps the monsters blocked alike.</summary>
        private static void AddNavBlocker(GameObject go)
        {
            var col = go.GetComponentInChildren<Collider>();
            if (col == null || col.isTrigger) return;
            if (go.GetComponentInChildren<UnityEngine.AI.NavMeshObstacle>() != null) return;

            Bounds b = col.bounds;
            float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (maxDim > 12f) return;   // sanity cap — a node prefab should never be building-sized

            var obs = col.gameObject.AddComponent<UnityEngine.AI.NavMeshObstacle>();
            obs.shape = UnityEngine.AI.NavMeshObstacleShape.Box;
            obs.carving = true;
            obs.carveOnlyStationary = true;   // static prop — carve once, cheap
            if (col is BoxCollider bc)
            {
                obs.center = bc.center;
                obs.size = bc.size;
            }
            else
            {
                obs.center = col.transform.InverseTransformPoint(b.center);
                Vector3 s = col.transform.InverseTransformVector(b.size);
                obs.size = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            }
        }

        /// <summary>
        /// Find the ACTUAL ground beneath <paramref name="probe"/>, skipping tree canopies, rocks,
        /// and any other ResourceNode the raycast might otherwise hit on the way down. Without this
        /// the spawner happily drops herbs on top of 10m-tall pines — visible to the player but
        /// completely unreachable.
        ///
        /// Uses RaycastAll → filters out tree/rock/log hits → picks the LOWEST remaining hit (the
        /// real terrain). Falls back to "no ground found" if everything in the column is rejected.
        /// </summary>
        private bool TryFindGroundUnder(Vector3 probe, out RaycastHit ground)
        {
            ground = default;
            Vector3 rayStart = probe + Vector3.up * 200f;
            var hits = Physics.RaycastAll(rayStart, Vector3.down, 500f, groundMask,
                                          QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return false;

            float lowestY = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < hits.Length; i++)
            {
                if (IsRejectableSurface(hits[i].collider)) continue;
                if (hits[i].point.y < lowestY)
                {
                    lowestY = hits[i].point.y;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0) return false;
            ground = hits[bestIdx];
            return true;
        }

        /// <summary>Surfaces we never want to place a resource node ON TOP of: other resources
        /// (trees / rocks the tagger already marked), name-matched trees / rocks / stumps / logs,
        /// the crafting table, bushes, etc. — anything that isn't actual terrain.</summary>
        private static bool IsRejectableSurface(Collider col)
        {
            if (col == null) return true;
            if (col.GetComponentInParent<ResourceNode>() != null) return true;
            if (col.GetComponentInParent<Interactable>() != null) return true;
            string n = col.name.ToLowerInvariant();
            Transform p = col.transform.parent;
            if (p != null) n += " " + p.name.ToLowerInvariant();
            if (n.Contains("tree") || n.Contains("trunk") || n.Contains("pine") || n.Contains("oak") ||
                n.Contains("birch") || n.Contains("stump") || n.Contains("log") ||
                n.Contains("rock") || n.Contains("stone") || n.Contains("boulder") ||
                n.Contains("fence") || n.Contains("wall") || n.Contains("roof"))
                return true;
            return false;
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 c = forestCenter != null ? forestCenter.position : transform.position;
            Gizmos.color = new Color(0.4f, 0.8f, 0.4f, 0.5f);
            Gizmos.DrawWireSphere(c, maxRadius);
            Gizmos.color = new Color(0.8f, 0.4f, 0.4f, 0.5f);
            Gizmos.DrawWireSphere(c, minRadius);
        }
    }
}
