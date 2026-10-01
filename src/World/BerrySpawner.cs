using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Net;

namespace SunsetCurse.World
{
    /// <summary>
    /// Spawns berry clusters in a FORAGE ZONE around a centre point (your village/spawn) at the
    /// start of each game — random spots every play, but kept near the safe hub so foraging is a
    /// manageable daytime activity instead of a map-wide search.
    ///
    /// For each spot it picks a random point in a ring between Min/Max radius, drops it onto the
    /// ground, optionally plants a bush prefab there, and grows an edible berry cluster on it.
    ///
    /// SETUP:
    ///   1. Empty GameObject "BerrySpawner" + this script.
    ///   2. Set "Forage Center" to your village/spawn (or leave empty to use this object's spot).
    ///   3. (Optional) drag a bush prefab into "Bush Prefab" so berries sit on a real bush.
    ///   4. Press Play. The yellow ring in the Scene view shows the forage zone.
    /// </summary>
    public class BerrySpawner : MonoBehaviour
    {
        [Header("Forage zone (around the village)")]
        [Tooltip("Centre of the forage area. Defaults to this object's position if empty.")]
        [SerializeField] private Transform forageCenter;
        [Tooltip("No berries closer than this to the centre (keeps them out of the houses).")]
        [SerializeField] private float minRadius = 8f;
        [Tooltip("No berries farther than this from the centre (keeps foraging close to home).")]
        [SerializeField] private float maxRadius = 55f;

        [Header("How many")]
        [Tooltip("Berry clusters to spawn each game.")]
        [SerializeField] private int berryClusterCount = 18;
        [Tooltip("Random seed. 0 = different every game; any other number = reproducible.")]
        [SerializeField] private int seed = 0;

        [Header("Ground detection")]
        [Tooltip("Which layers count as 'ground' for dropping berries onto. Everything is fine.")]
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("Optional bush under the berries")]
        [Tooltip("If set, this bush prefab is planted at each spot and berries grow on it.")]
        [SerializeField] private GameObject bushPrefab;

        [Header("Berry look & placement")]
        [SerializeField] private GameObject berryPrefab;     // optional; else coloured spheres
        [SerializeField] private Vector2Int berriesPerCluster = new Vector2Int(4, 7);
        [Tooltip("Height of the berries above the ground (keep reachable).")]
        [SerializeField] private float placementHeight = 1.0f;
        [SerializeField] private float clusterRadius = 0.25f;
        [SerializeField] private float berrySize = 0.13f;
        [SerializeField] private Color berryColor = new Color(0.75f, 0.05f, 0.12f);
        [Tooltip("Makes the berries glow a little so they're easier to spot.")]
        [SerializeField] private bool glow = true;

        [Header("Gameplay")]
        [SerializeField] private float regrowSeconds = 25f;

        private Material berryMaterial;

        private IEnumerator Start()
        {
            // Wait for the shared world seed so every peer grows berries in the SAME spots. Offline
            // SP (no NetworkManager) falls straight through to the time-based fallback below.
            if (NetworkManager.Singleton != null)
            {
                float t = 0f;
                while (SessionState.EffectiveWorldSeed == 0 && t < 5f) { t += Time.deltaTime; yield return null; }
            }

            Vector3 center = forageCenter != null ? forageCenter.position : transform.position;
            // Shared world seed for MP determinism — every peer grows berries in the same spots.
            int effectiveSeed;
            if (seed != 0)
            {
                effectiveSeed = seed;
            }
            else
            {
                int worldSeed = SessionState.EffectiveWorldSeed;   // SessionState → else Inventory
                if (worldSeed == 0) worldSeed = (int)(System.DateTime.UtcNow.Ticks & 0x7FFFFFFF);
                effectiveSeed = worldSeed ^ 0x42BE7B2D;  // distinct namespace from ResourceSpawner
            }
            var rng = new System.Random(effectiveSeed);

            int spawned = 0, attempts = 0, maxAttempts = berryClusterCount * 10;
            while (spawned < berryClusterCount && attempts < maxAttempts)
            {
                attempts++;

                // Random point in the ring [minRadius, maxRadius] around the centre.
                float ang = (float)(rng.NextDouble() * System.Math.PI * 2.0);
                float dist = Mathf.Sqrt(Mathf.Lerp(minRadius * minRadius, maxRadius * maxRadius,
                                                   (float)rng.NextDouble()));
                Vector3 probe = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * dist;

                // Drop it onto the ground.
                Vector3 rayStart = probe + Vector3.up * 100f;
                if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 500f, groundMask,
                                     QueryTriggerInteraction.Ignore))
                    continue;

                GrowBerriesAt(hit.point, rng);
                spawned++;
            }

            Debug.Log($"[BerrySpawner] Grew {spawned} berry clusters in the forage zone " +
                      $"({minRadius}-{maxRadius}m around the village).");
        }

        private void GrowBerriesAt(Vector3 groundPos, System.Random rng)
        {
            // Optional bush at the spot.
            if (bushPrefab != null)
            {
                float yaw = (float)(rng.NextDouble() * 360.0);
                Instantiate(bushPrefab, groundPos, Quaternion.Euler(0f, yaw, 0f), transform);
            }

            // Cluster parent (holds berries + collider + BerryBush).
            var cluster = new GameObject("BerryCluster");
            cluster.transform.SetParent(transform, true);
            cluster.transform.position = groundPos + Vector3.up * placementHeight;

            var visuals = new List<GameObject>();
            if (berryPrefab != null)
            {
                visuals.Add(Instantiate(berryPrefab, cluster.transform.position, Quaternion.identity, cluster.transform));
            }
            else
            {
                int n = rng.Next(berriesPerCluster.x, berriesPerCluster.y + 1);
                for (int i = 0; i < n; i++)
                    visuals.Add(CreateBerrySphere(cluster.transform, rng));
            }

            var col = cluster.AddComponent<SphereCollider>();
            col.radius = clusterRadius + berrySize;

            var bush = cluster.AddComponent<BerryBush>();
            bush.Configure(visuals.ToArray(), regrowSeconds);
        }

        private GameObject CreateBerrySphere(Transform parent, System.Random rng)
        {
            var berry = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            berry.name = "Berry";
            berry.transform.SetParent(parent, false);

            Vector3 offset = new Vector3(
                (float)(rng.NextDouble() * 2 - 1),
                (float)(rng.NextDouble() * 2 - 1) * 0.5f,
                (float)(rng.NextDouble() * 2 - 1)) * clusterRadius;
            berry.transform.localPosition = offset;
            berry.transform.localScale = Vector3.one * berrySize;

            var c = berry.GetComponent<Collider>();
            if (c != null) Destroy(c);

            var r = berry.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = GetBerryMaterial();
            return berry;
        }

        private Material GetBerryMaterial()
        {
            if (berryMaterial != null) return berryMaterial;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            berryMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            if (berryMaterial.HasProperty("_BaseColor")) berryMaterial.SetColor("_BaseColor", berryColor);
            else berryMaterial.color = berryColor;

            if (glow && berryMaterial.HasProperty("_EmissionColor"))
            {
                berryMaterial.EnableKeyword("_EMISSION");
                berryMaterial.SetColor("_EmissionColor", berryColor * 0.6f);
            }
            return berryMaterial;
        }

        // Show the forage ring in the Scene view.
        private void OnDrawGizmosSelected()
        {
            Vector3 center = forageCenter != null ? forageCenter.position : transform.position;
            Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.9f);
            DrawRing(center, maxRadius);
            Gizmos.color = new Color(1f, 0.5f, 0.2f, 0.7f);
            DrawRing(center, minRadius);
        }

        private static void DrawRing(Vector3 c, float r)
        {
            const int seg = 48;
            Vector3 prev = c + new Vector3(r, 0f, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 next = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }
    }
}
