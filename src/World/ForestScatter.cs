using System.Collections.Generic;
using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Scatters a forest of tree prefabs across the ground automatically.
    ///
    /// HOW IT WORKS: it picks random spots inside a rectangular area, fires a ray
    /// straight DOWN to find the ground, and drops a randomly-rotated, randomly-scaled
    /// tree there. All spawned trees are parented under a child container so they're
    /// easy to clear and re-generate.
    ///
    /// SETUP (in Unity):
    ///   1. Create an empty GameObject, name it "Forest", put it at the centre of your
    ///      ground (Position 0,0,0 is fine).
    ///   2. Add this script to it.
    ///   3. Drag your tree prefabs (Pine Forest_LowPoly/Prefabs/Trees/*) into the
    ///      "Tree Prefabs" list in the Inspector.
    ///   4. Set "Area Size" to cover your ground, then RIGHT-CLICK the component header
    ///      (the three dots ⋮) → "Generate Forest". (Right-click again → "Clear Forest".)
    ///
    /// You do NOT need to press Play — generate it in edit mode and it saves with the scene.
    /// IMPORTANT: your ground must have a Collider (Unity's Plane already has one) or the
    /// downward ray hits nothing and no trees appear.
    /// </summary>
    public class ForestScatter : MonoBehaviour
    {
        [Header("What to plant")]
        [Tooltip("One or more tree prefabs. Each spawn picks one at random.")]
        [SerializeField] private List<GameObject> treePrefabs = new List<GameObject>();

        [Tooltip("How many trees to attempt to place.")]
        [SerializeField] private int count = 200;

        [Header("Where to plant (centred on this object)")]
        [Tooltip("Size of the rectangle to scatter across, in metres (X = width, Z = depth).")]
        [SerializeField] private Vector2 areaSize = new Vector2(50f, 50f);

        [Tooltip("Only treat these layers as 'ground'. Leave as Everything if unsure.")]
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("Keep a clearing (optional)")]
        [Tooltip("No trees will spawn within this radius of the clearing centre " +
                 "(e.g. the settlement / player spawn). 0 = off.")]
        [SerializeField] private float clearingRadius = 0f;
        [Tooltip("Centre of the clearing. If empty, uses this object's position.")]
        [SerializeField] private Transform clearingCenter;

        [Header("Variation (so it doesn't look copy-pasted)")]
        [SerializeField] private bool randomYRotation = true;
        [SerializeField] private float minScale = 0.8f;
        [SerializeField] private float maxScale = 1.4f;
        [Tooltip("Sink trees this far into the ground so trunks aren't floating.")]
        [SerializeField] private float sinkIntoGround = 0.1f;

        private const string ContainerName = "ForestContainer";

        [ContextMenu("Generate Forest")]
        public void Generate()
        {
            if (treePrefabs == null || treePrefabs.Count == 0)
            {
                Debug.LogWarning("[ForestScatter] Assign at least one tree prefab first.", this);
                return;
            }

            Clear(); // start fresh each time

            Transform container = new GameObject(ContainerName).transform;
            container.SetParent(transform, worldPositionStays: false);

            Vector3 origin = transform.position;
            Vector3 clearPos = clearingCenter != null ? clearingCenter.position : origin;
            int placed = 0;

            for (int i = 0; i < count; i++)
            {
                // Random point inside the area (centred on this object).
                float x = origin.x + Random.Range(-areaSize.x * 0.5f, areaSize.x * 0.5f);
                float z = origin.z + Random.Range(-areaSize.y * 0.5f, areaSize.y * 0.5f);

                // Skip points inside the protected clearing.
                if (clearingRadius > 0f)
                {
                    float dx = x - clearPos.x;
                    float dz = z - clearPos.z;
                    if (dx * dx + dz * dz < clearingRadius * clearingRadius)
                        continue;
                }

                // Fire a ray down from high up to find the ground height.
                Vector3 rayStart = new Vector3(x, origin.y + 200f, z);
                if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 1000f, groundMask))
                    continue; // nothing to stand on here

                GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Count)];
                GameObject tree = Instantiate(prefab, container);

                tree.transform.position = hit.point + Vector3.down * sinkIntoGround;
                float yRot = randomYRotation ? Random.Range(0f, 360f) : 0f;
                tree.transform.rotation = Quaternion.Euler(0f, yRot, 0f);
                tree.transform.localScale = prefab.transform.localScale * Random.Range(minScale, maxScale);

                placed++;
            }

            Debug.Log($"[ForestScatter] Planted {placed} trees (asked for {count}).", this);
        }

        [ContextMenu("Clear Forest")]
        public void Clear()
        {
            // Remove the existing container (if any).
            Transform existing = transform.Find(ContainerName);
            while (existing != null)
            {
                // DestroyImmediate is required when called from the editor (not Play mode).
                if (Application.isPlaying) Destroy(existing.gameObject);
                else DestroyImmediate(existing.gameObject);
                existing = transform.Find(ContainerName);
            }
        }

        // Draw the scatter area + clearing in the Scene view so you can see the bounds.
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(transform.position, new Vector3(areaSize.x, 0.1f, areaSize.y));

            if (clearingRadius > 0f)
            {
                Gizmos.color = Color.yellow;
                Vector3 c = clearingCenter != null ? clearingCenter.position : transform.position;
                Gizmos.DrawWireSphere(c, clearingRadius);
            }
        }
    }
}
