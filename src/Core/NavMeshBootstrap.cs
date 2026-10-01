using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace SunsetCurse.Core
{
    /// <summary>
    /// Builds the runtime navmesh the monsters walk on — and makes it match PLAYER collision.
    ///
    /// THE BUG THIS FIXES: the surface used to collect RENDER MESHES. Hand-placed BoxColliders
    /// are invisible (no render mesh), so the bake ignored them — players collided with houses
    /// while both monsters walked straight through. Worse, in BUILDS render meshes without
    /// Read/Write enabled are silently skipped, so even visible buildings dropped out of the
    /// bake. Collecting PHYSICS COLLIDERS instead means the monsters are blocked by exactly
    /// what blocks the player: your manual box colliders + every built-in collider.
    ///
    /// DOORS: a closed door's collider must NOT be baked as a permanent wall (the monster could
    /// never use the doorway once it opens). Every Door gets a NavMeshModifier (excluded from
    /// the bake) + a carving NavMeshObstacle that follows the door — closed blocks the path,
    /// open swings the hole aside. Players are excluded too (their CharacterController is a
    /// collider — it would bake a hole at their feet).
    ///
    /// BUILD BACKSTOP: MeshColliders whose mesh isn't Read/Write enabled can't feed the bake in
    /// a player build (works in the editor, silently missing in the exe). Small props (trees,
    /// rocks) get a box carve-obstacle instead; big buildings should carry manual BoxColliders.
    ///
    /// SETUP: drop this on one manager object (already in the scene). Check the Console logs.
    /// </summary>
    [DefaultExecutionOrder(-100)]   // run before the monsters wake up
    public class NavMeshBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            var surfaces = FindObjectsByType<NavMeshSurface>(FindObjectsInactive.Include);

            if (surfaces.Length == 0)
            {
                Debug.LogError("[NavMeshBootstrap] Found 0 NavMeshSurface components in the scene! " +
                               "Add a NavMeshSurface to your ground and Bake.");
                return;
            }

            PrepareDoorsAndPlayersForBake();

            // Force each surface to (re)build its navmesh AT RUNTIME from PHYSICS COLLIDERS,
            // guaranteeing the walkable area matches what the player's body collides with.
            foreach (var s in surfaces)
            {
                s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                s.BuildNavMesh();
                Debug.Log($"[NavMeshBootstrap] Built navmesh at runtime for '{s.name}' from " +
                          $"physics colliders (data: {(s.navMeshData != null ? "OK" : "NULL")}).", s);
            }

            AddObstacleBackstops();

            // Sanity log: where did the navmesh land? (normal log so it never pauses the editor)
            var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
            if (tri.vertices != null && tri.vertices.Length > 0)
            {
                var b = new Bounds(tri.vertices[0], Vector3.zero);
                foreach (var v in tri.vertices) b.Encapsulate(v);
                Debug.Log($"[NavMeshBootstrap] Navmesh centred at {b.center}, size {b.size}.");
            }
            else
            {
                Debug.LogWarning("[NavMeshBootstrap] Navmesh has ZERO triangles — nothing walkable was baked.");
            }
        }

        /// <summary>Doors: keep their doorways OPEN on the baked navmesh (a carving obstacle
        /// blocks/unblocks them dynamically as they swing). Players: keep their body colliders
        /// out of the bake entirely.</summary>
        private static void PrepareDoorsAndPlayersForBake()
        {
            foreach (var door in FindObjectsByType<SunsetCurse.World.Door>(FindObjectsInactive.Include))
            {
                if (door.GetComponent<NavMeshModifier>() == null)
                {
                    var mod = door.gameObject.AddComponent<NavMeshModifier>();
                    mod.ignoreFromBuild = true;   // affects the door's whole hierarchy
                }

                // The carving obstacle sits on the door mesh's own collider object, so it swings
                // WITH the door: closed = path blocked, open = doorway clear.
                var col = door.GetComponentInChildren<Collider>(true);
                if (col != null && col.GetComponent<NavMeshObstacle>() == null)
                {
                    var obs = col.gameObject.AddComponent<NavMeshObstacle>();
                    obs.shape = NavMeshObstacleShape.Box;
                    obs.carving = true;
                    obs.carveOnlyStationary = false;   // it rotates — the hole must follow it
                    FitBoxToCollider(obs, col);
                }
            }

            foreach (var pi in FindObjectsByType<PlayerInventory>(FindObjectsInactive.Include))
            {
                if (pi.GetComponent<NavMeshModifier>() == null)
                    pi.gameObject.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
            }
        }

        /// <summary>In a BUILD, a MeshCollider whose mesh has no Read/Write access can't feed the
        /// navmesh bake (editor reads everything, so this gap only shows in the exe). Small props
        /// — trees, rocks, herbs — get a box carve-obstacle matching their bounds instead. Big
        /// objects are skipped: a single box would seal off a building's interior; those need the
        /// manual BoxColliders (which the physics-collider bake picks up directly).</summary>
        private static void AddObstacleBackstops()
        {
            int added = 0;
            foreach (var mc in FindObjectsByType<MeshCollider>())
            {
                if (mc == null || !mc.enabled || mc.isTrigger) continue;
                if (mc.sharedMesh == null || mc.sharedMesh.isReadable) continue;   // baked fine
                if (mc.GetComponent<NavMeshObstacle>() != null) continue;
                if (mc.GetComponentInParent<SunsetCurse.World.Door>() != null) continue;   // doors handled above
                if (mc.GetComponentInParent<NavMeshAgent>() != null) continue;             // never the monsters

                Bounds b = mc.bounds;
                float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (maxDim < 0.6f || maxDim > 12f) continue;   // too small to matter / building-sized

                var obs = mc.gameObject.AddComponent<NavMeshObstacle>();
                obs.shape = NavMeshObstacleShape.Box;
                obs.carving = true;
                obs.carveOnlyStationary = true;   // static props — carve once, cheap
                FitBoxToCollider(obs, mc);
                added++;
            }
            if (added > 0)
                Debug.Log($"[NavMeshBootstrap] Added {added} carve-obstacle backstop(s) for props " +
                          "whose collision meshes aren't readable in a build.");
        }

        /// <summary>Size a box obstacle to a collider: exact for BoxColliders, world-bounds
        /// approximation for everything else.</summary>
        private static void FitBoxToCollider(NavMeshObstacle obs, Collider col)
        {
            if (col is BoxCollider bc)
            {
                obs.center = bc.center;
                obs.size = bc.size;
                return;
            }
            Bounds b = col.bounds;
            obs.center = col.transform.InverseTransformPoint(b.center);
            Vector3 s = col.transform.InverseTransformVector(b.size);
            obs.size = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }
    }
}
