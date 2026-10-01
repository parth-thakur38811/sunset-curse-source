using System.Collections.Generic;
using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Auto-places a fence around the whole map perimeter — evenly spaced, all at the same
    /// rotation, with NO gaps. Replaces placing segments one-by-one by hand (which drifts out of
    /// alignment and leaves gaps).
    ///
    /// HOW IT WORKS: it walks the four edges of a rectangle centred on this GameObject and drops a
    /// fence segment every <see cref="segmentLength"/> metres, each rotated to run along that edge.
    /// To guarantee no gaps, each edge divides into a whole number of slots, so segments sit slightly
    /// tighter than their length (a tiny, invisible overlap) instead of leaving holes. Optional
    /// corner posts hide the 90° junctions. All fences are parented under one container so you can
    /// clear + regenerate in a click.
    ///
    /// SETUP (in Unity — no Play mode needed, it saves with the scene):
    ///   1. Empty GameObject "MapFence" at the CENTRE of your ground (e.g. Position 0,0,0).
    ///   2. Add this script.
    ///   3. Drag your fence-segment prefab into "Fence Prefab".
    ///   4. Right-click the component header (⋮) → "Measure Segment Length From Prefab" — read the
    ///      logged X/Z size and type the longer one into "Segment Length".
    ///   5. Set "Border Size" to your ground size (the green gizmo rectangle shows the border).
    ///   6. Right-click (⋮) → "Generate Fence Border". Re-run any time; "Clear Fence Border" removes it.
    ///
    /// If the fences come out facing the wrong way (running across the border instead of along it),
    /// set "Segment Yaw Offset" to 90.
    ///
    /// IMPORTANT: your ground needs a Collider (it already does) so the downward ground-snap ray hits.
    /// </summary>
    public class FenceBorder : MonoBehaviour
    {
        [Header("What to place")]
        [Tooltip("Your fence-segment prefab (one straight piece of fence).")]
        [SerializeField] private GameObject fencePrefab;

        [Tooltip("Length of ONE fence segment in metres, measured ALONG the fence. Use the " +
                 "'Measure Segment Length From Prefab' context-menu to find it.")]
        [SerializeField] private float segmentLength = 4f;

        [Tooltip("Extra yaw (degrees) added to every segment. If your fences run ACROSS the border " +
                 "instead of along it, set this to 90.")]
        [SerializeField] private float segmentYawOffset = 0f;

        [Header("Border rectangle (centred on this object)")]
        [Tooltip("Size of the border, in metres (X = width, Z = depth). Match your ground.")]
        [SerializeField] private Vector2 borderSize = new Vector2(250f, 250f);

        [Header("Ground snapping")]
        [Tooltip("Raycast down so each segment sits on the terrain (handles uneven ground).")]
        [SerializeField] private bool snapToGround = true;
        [Tooltip("Which layers count as 'ground'. Leave as Everything if unsure.")]
        [SerializeField] private LayerMask groundMask = ~0;
        [Tooltip("Raise (+) or sink (-) every segment by this much after snapping, in metres.")]
        [SerializeField] private float yOffset = 0f;

        [Header("Corner posts (optional — hide the 90° junctions)")]
        [SerializeField] private bool addCornerPosts = true;
        [Tooltip("Prefab for the four corner posts. Leave EMPTY to reuse the fence prefab.")]
        [SerializeField] private GameObject cornerPostPrefab;

        private const string ContainerName = "FenceBorderContainer";

        // One planned placement (computed first, instantiated second, so fences never block each
        // other's ground raycasts).
        private struct Placement { public Vector3 pos; public Quaternion rot; public GameObject prefab; }

        [ContextMenu("Generate Fence Border")]
        public void Generate()
        {
            if (fencePrefab == null)
            {
                Debug.LogWarning("[FenceBorder] Assign a Fence Prefab first.", this);
                return;
            }
            if (segmentLength <= 0.01f)
            {
                Debug.LogWarning("[FenceBorder] Segment Length must be > 0. Use 'Measure Segment Length From Prefab'.", this);
                return;
            }

            Clear();

            Vector3 c = transform.position;
            float hx = borderSize.x * 0.5f;
            float hz = borderSize.y * 0.5f;

            // Four corners, clockwise. Edges connect consecutive corners; the 4th edge wraps back.
            Vector3[] corners =
            {
                c + new Vector3(-hx, 0f, -hz),
                c + new Vector3(-hx, 0f,  hz),
                c + new Vector3( hx, 0f,  hz),
                c + new Vector3( hx, 0f, -hz),
            };

            var plan = new List<Placement>();
            for (int e = 0; e < 4; e++)
                PlanEdge(corners[e], corners[(e + 1) % 4], plan);

            if (addCornerPosts)
            {
                GameObject post = cornerPostPrefab != null ? cornerPostPrefab : fencePrefab;
                foreach (var corner in corners)
                    plan.Add(new Placement { pos = Ground(corner), rot = Quaternion.Euler(0f, segmentYawOffset, 0f), prefab = post });
            }

            // Instantiate everything AFTER planning, under a single container.
            Transform container = new GameObject(ContainerName).transform;
            container.SetParent(transform, worldPositionStays: false);
            foreach (var p in plan)
            {
                var go = Instantiate(p.prefab, p.pos, p.rot, container);
                go.transform.localScale = p.prefab.transform.localScale;
            }

            Debug.Log($"[FenceBorder] Placed {plan.Count} fence pieces around a {borderSize.x}×{borderSize.y} m border.", this);
        }

        // Lay segments along one edge: divide it into a whole number of slots so segments tile with
        // no gaps (a tiny overlap instead), each rotated to run ALONG the edge.
        private void PlanEdge(Vector3 start, Vector3 end, List<Placement> plan)
        {
            Vector3 edge = end - start;
            float length = edge.magnitude;
            if (length < 0.001f) return;

            Vector3 dir = edge / length;
            int count = Mathf.Max(1, Mathf.CeilToInt(length / segmentLength));
            float step = length / count;

            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(0f, segmentYawOffset, 0f);

            for (int i = 0; i < count; i++)
            {
                Vector3 flat = start + dir * ((i + 0.5f) * step);
                plan.Add(new Placement { pos = Ground(flat), rot = rot, prefab = fencePrefab });
            }
        }

        // Drop a point onto the ground (if snapping is on) and apply the height offset.
        private Vector3 Ground(Vector3 flat)
        {
            if (snapToGround &&
                Physics.Raycast(new Vector3(flat.x, transform.position.y + 500f, flat.z), Vector3.down,
                                out RaycastHit hit, 2000f, groundMask, QueryTriggerInteraction.Ignore))
            {
                return hit.point + Vector3.up * yOffset;
            }
            return new Vector3(flat.x, transform.position.y + yOffset, flat.z);
        }

        [ContextMenu("Clear Fence Border")]
        public void Clear()
        {
            Transform existing = transform.Find(ContainerName);
            while (existing != null)
            {
                if (Application.isPlaying) Destroy(existing.gameObject);
                else DestroyImmediate(existing.gameObject);   // required in edit mode
                existing = transform.Find(ContainerName);
            }
        }

        /// <summary>Logs the fence prefab's world-space size so you can read off the correct
        /// Segment Length (use the LONGER of X/Z — that's the direction the fence runs).</summary>
        [ContextMenu("Measure Segment Length From Prefab")]
        public void MeasureSegment()
        {
            if (fencePrefab == null) { Debug.LogWarning("[FenceBorder] Assign a Fence Prefab first.", this); return; }

            var renderers = fencePrefab.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) { Debug.LogWarning("[FenceBorder] Prefab has no Renderers to measure.", this); return; }

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

            Debug.Log($"[FenceBorder] '{fencePrefab.name}' size ≈ X:{b.size.x:0.00}m  Z:{b.size.z:0.00}m  " +
                      $"(Y:{b.size.y:0.00}m). Set Segment Length to the LONGER of X/Z. If the fences end " +
                      $"up crossing the border, set Segment Yaw Offset to 90.", this);
        }

        // Show the border rectangle in the Scene view so you can match it to the ground.
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.85f, 0.5f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(transform.position, new Vector3(borderSize.x, 0.2f, borderSize.y));
        }
    }
}
