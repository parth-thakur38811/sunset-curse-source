using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SunsetCurse.World
{
    /// <summary>
    /// Builds the invisible anti-jump walls for EVERY fence AUTOMATICALLY when a scene loads — in
    /// the editor Play mode AND in a build — so nobody ever has to re-run an editor menu, save the
    /// scene, or rebuild just to get working fences. This is the authoritative source of the
    /// barriers now; the editor menu (Tools ▸ Sunset Curse ▸ 13) is no longer required.
    ///
    /// WHY RUNTIME: the previous editor-baked approach kept coming out "same to same" because it
    /// depends on a chain of manual steps (recompile → re-run the menu → save → rebuild), each a
    /// failure point. Running on scene load removes all of that.
    ///
    /// WHAT IT DOES each load:
    ///   1. Deletes any leftover baked barriers — old child "AntiJumpBarrier" ledges (the wrongly
    ///      oriented ones that let you climb) and any baked "FenceJumpBarriers" container — so
    ///      nothing stale fights the fresh walls.
    ///   2. Sweeps the scene for fence meshes (keyword "fence" anywhere in the name chain) and drops
    ///      a WORLD-axis-aligned invisible BoxCollider on each: 5 m tall, ≥0.6 m thick, sunk 0.5 m
    ///      below the base. World-aligned = genuinely vertical no matter how the fence mesh is
    ///      rotated (the bug that made the baked ledges climbable). Gate FRAMES are walled only
    ///      above head height so the doorway stays walkable; swinging Doors are skipped.
    ///
    /// Runs on every peer (each client needs its own local colliders for its own CharacterController)
    /// and needs no networking — the walls are static local physics only.
    /// </summary>
    public static class FenceBarrierRuntime
    {
        private const string RuntimeContainer = "FenceJumpBarriers (runtime)";
        private const string BakedContainer   = "FenceJumpBarriers";   // from editor menu 13
        private const string OldChildBarrier  = "AntiJumpBarrier";     // from the first (broken) version

        private const float ExtraHeight   = 5f;    // world metres of wall above the fence top
        private const float BelowGround   = 0.5f;  // extend below the base — no ground gap
        private const float MinThickness  = 0.6f;  // min horizontal thickness (anti-tunnel)
        private const float Padding       = 0.35f; // stick out past the fence so the player's grounded
                                                    // sphere (r=0.5) can't reach the fence's own Default-
                                                    // layer collider and false-ground on it either
        private const float GateClearance = 2.8f;  // gate pieces: keep BELOW this clear (walkable doorway)

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            // Belt AND braces: the sceneLoaded event covers normal loads, and a tiny persistent
            // poller covers scene changes that don't raise it (e.g. NGO-driven transitions). The
            // per-scene idempotency guard means they never double-build.
            SceneManager.sceneLoaded += (scene, mode) => BuildForScene(scene);

            var go = new GameObject("~FenceBarrierRuntime");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Runner>();

            BuildForScene(SceneManager.GetActiveScene());   // the scene we started in
        }

        /// <summary>Persistent poller — builds barriers whenever the ACTIVE scene changes, no matter
        /// how it was loaded (Unity SceneManager, NGO, additive, whatever). One int compare a frame.</summary>
        private class Runner : MonoBehaviour
        {
            private int lastSceneHandle;
            private void Update()
            {
                var s = SceneManager.GetActiveScene();
                if (s.handle == lastSceneHandle) return;
                lastSceneHandle = s.handle;
                BuildForScene(s);
            }
        }

        private static void BuildForScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            // Idempotent: if we already built for this scene instance, don't double up.
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == RuntimeContainer) return;

            // 1) Remove any baked / old barriers so they can't interfere.
            RemoveBaked(scene);

            // 2) Build fresh world-aligned walls. Only make the container if there's a fence to wall.
            GameObject container = null;
            int count = 0;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.gameObject.scene != scene) continue;                 // this scene only
                var rend = mf.GetComponent<MeshRenderer>();
                if (rend == null) continue;
                if (!NameChainContains(mf.transform, "fence")) continue;
                if (mf.GetComponentInParent<Door>() != null) continue;              // swinging doors stay free
                if (mf.GetComponentInParent<CharacterController>() != null) continue;
                if (mf.GetComponentInParent<UnityEngine.AI.NavMeshAgent>() != null) continue;

                Bounds wb = rend.bounds;                                     // WORLD AABB — orientation-proof
                if (wb.size == Vector3.zero) continue;
                if (Mathf.Max(wb.size.x, wb.size.z) > 60f) continue;        // skip a whole-map single mesh

                if (container == null)
                {
                    container = new GameObject(RuntimeContainer);
                    SceneManager.MoveGameObjectToScene(container, scene);
                }
                BuildOne(container.transform, wb, NameChainContains(mf.transform, "gate"));
                count++;
            }

            // One clear line in the Console so you can CONFIRM this new runtime system actually ran
            // (if you don't see it on Play, the scripts didn't recompile).
            if (count > 0)
                Debug.Log($"[FenceBarrierRuntime] Built {count} anti-jump fence wall(s) in scene '{scene.name}'.");
        }

        private static void RemoveBaked(Scene scene)
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || t.gameObject.scene != scene) continue;
                if (t.name == OldChildBarrier || t.name == BakedContainer)
                    Object.Destroy(t.gameObject);
            }
        }

        private static void BuildOne(Transform parent, Bounds wb, bool gate)
        {
            var go = new GameObject(gate ? "GateTopBarrier" : "FenceBarrier");
            go.transform.SetParent(parent, worldPositionStays: true);
            go.transform.rotation = Quaternion.identity;   // world-aligned — the whole point
            // Layer 2 = "Ignore Raycast". CRITICAL for anti-climb: the player CharacterController
            // still physically COLLIDES with this layer (so the wall blocks movement), but the
            // StarterAssets "grounded" check is a sphere limited to GroundLayers (= Default only),
            // so it will NOT treat the wall as a foothold. On Default the wall doubled as fake
            // ground → the player could jump-into-wall, get falsely grounded, and re-jump up it
            // ("jump+W then W then jump over"). Off the ground layer, that exploit is dead. Bonus:
            // interaction / monster-sight rays pass through (the visible fence mesh still blocks sight).
            go.layer = 2;
            var box = go.AddComponent<BoxCollider>();

            // Horizontal footprint = fence's world XZ + a pad; bump the THINNER axis (thickness) to a
            // minimum so a fast jump can't tunnel through.
            float sizeX = wb.size.x + Padding * 2f;
            float sizeZ = wb.size.z + Padding * 2f;
            if (sizeX <= sizeZ) sizeX = Mathf.Max(sizeX, MinThickness);
            else                sizeZ = Mathf.Max(sizeZ, MinThickness);

            float bottom, top;
            if (gate)
            {
                bottom = wb.min.y + GateClearance;         // keep the doorway walkable
                top    = wb.max.y + ExtraHeight;
                if (top < bottom + ExtraHeight) top = bottom + ExtraHeight;
            }
            else
            {
                bottom = wb.min.y - BelowGround;
                top    = wb.max.y + ExtraHeight;
            }

            go.transform.position = new Vector3(wb.center.x, (bottom + top) * 0.5f, wb.center.z);
            box.center = Vector3.zero;
            box.size = new Vector3(sizeX, top - bottom, sizeZ);
        }

        /// <summary>"fence"/"gate" match against the object's own name AND every ancestor's — pack
        /// meshes are often anonymous children under a named prefab root.</summary>
        private static bool NameChainContains(Transform t, string keyword)
        {
            for (Transform cur = t; cur != null; cur = cur.parent)
                if (cur.name.ToLowerInvariant().Contains(keyword)) return true;
            return false;
        }
    }
}
