using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using SunsetCurse.World;

namespace SunsetCurse.EditorTools
{
    /// <summary>
    /// One-click scene setup for the Session-8/9 building shipment. Everything here is EDITOR-ONLY
    /// (lives in an Editor/ folder, never ships in a build) — it just automates the fiddly Unity
    /// wiring so nobody has to follow a 40-step Inspector checklist.
    ///
    /// HOW TO USE (in Unity, with SampleScene open):
    ///   Tools ▸ Sunset Curse ▸ 1. Place Buildings + Towers   — spawns the safe house, 2 loot
    ///        houses and 2 scout towers at sensible spots, adds colliders + the SafeHouse zone.
    ///        Drag/rotate them afterwards like any object — nothing is position-locked.
    ///   Tools ▸ Sunset Curse ▸ 2. Create DoorSync Object     — the ONE networked door-state object.
    ///   Tools ▸ Sunset Curse ▸ 3. Add Door To Selected       — select a building (or nothing),
    ///        get a ready hinge+door assembly to slide into its doorway.
    ///   Tools ▸ Sunset Curse ▸ 4. Swap Monster Model To Creep — replaces the Plague Doctor with
    ///        the Creep (new AnimatorController from its own clips) on Monster.prefab.
    ///
    /// Every action is undoable (Ctrl+Z) and safe to re-run — it skips things that already exist.
    /// </summary>
    public static class SunsetSceneBuilder
    {
        // ───────────────────────────── asset paths (all verified to exist) ─────────────────────────────
        private const string SafeHousePrefabPath = "Assets/NikolayFedorov/OldVillage/SurvivalOldHouse01/SurvivalOldHouse01_prefab.prefab";
        private const string ShedPrefabPath      = "Assets/Blackant Master Studio/The Shed/The Shed Optimized/Prefab/The Shed.prefab";
        private const string RoomPrefabPath      = "Assets/Asset pack for horror game/Prefabs/Room.prefab";
        private const string TowerPrefabPath     = "Assets/Flooded_Grounds/Prefabs/Buildings/Structures1/Struct_RadioTower_A.prefab";
        private const string DoorPrefabPath      = "Assets/Asset pack for horror game/Prefabs/Door.prefab";
        private const string MonsterPrefabPath   = "Assets/_Project/Prefabs/Monster.prefab";
        private const string CreepModelPath      = "Assets/Creep Horror Creature/Prefabs/Creep1.prefab";
        private const string CreepClipsFbxPath   = "Assets/Creep Horror Creature/Meshes/Creep_mesh.fbx";
        private const string CreepControllerPath = "Assets/_Project/Art/Monsters/Creep/CreepMonster.controller";

        private const string BuildingsParentName = "NewBuildings";

        // ───────────────────────────── 1. buildings + towers ─────────────────────────────

        [MenuItem("Tools/Sunset Curse/1. Place Buildings + Towers")]
        public static void PlaceBuildings()
        {
            // Everything under one parent so it's easy to find/move/delete as a group.
            var parent = GameObject.Find(BuildingsParentName);
            if (parent == null)
            {
                parent = new GameObject(BuildingsParentName);
                Undo.RegisterCreatedObjectUndo(parent, "Create NewBuildings parent");
            }

            // Positions chosen off the real map: village centre ≈ (-32, -75), compound ≈ (85, 99),
            // ground is the flat 250m plane at y≈0. All of these are starting points — drag to taste.
            var safeHouse = PlaceOne(SafeHousePrefabPath, "SafeHouse_OldHouse", new Vector3(-8f, 0f, -55f), 180f, parent.transform);
            PlaceOne(ShedPrefabPath,  "LootHouse_Shed",  new Vector3(-70f, 0f, 35f), 135f, parent.transform);
            PlaceOne(RoomPrefabPath,  "LootHouse_Room",  new Vector3(55f, 0f, -75f), -45f, parent.transform);
            PlaceOne(TowerPrefabPath, "ScoutTower_East", new Vector3(30f, 0f, 20f),  0f,  parent.transform);
            PlaceOne(TowerPrefabPath, "ScoutTower_West", new Vector3(-60f, 0f, -20f), 0f, parent.transform);

            // The safe-house zone: SafeHouse component + a centre marker at the building's real
            // middle (the model's pivot may be at a corner, so we anchor to renderer bounds instead).
            if (safeHouse != null && safeHouse.GetComponent<SafeHouse>() == null)
            {
                var zone = Undo.AddComponent<SafeHouse>(safeHouse);
                Bounds b = RendererBounds(safeHouse);

                var centerMarker = new GameObject("SafeZoneCenter");
                Undo.RegisterCreatedObjectUndo(centerMarker, "Create SafeZoneCenter");
                centerMarker.transform.SetParent(safeHouse.transform, worldPositionStays: true);
                centerMarker.transform.position = new Vector3(b.center.x, safeHouse.transform.position.y, b.center.z);

                // radius / centerOverride are [SerializeField] private — set them the official
                // editor way (SerializedObject) so undo + prefab overrides behave.
                var so = new SerializedObject(zone);
                so.FindProperty("radius").floatValue = Mathf.Max(b.extents.x, b.extents.z) + 1f;
                so.FindProperty("centerOverride").objectReferenceValue = centerMarker.transform;
                so.ApplyModifiedProperties();
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[SunsetSceneBuilder] Buildings placed under 'NewBuildings'. Drag/rotate them " +
                      "to taste, then run '3. Add Door To Selected' with the safe house selected. " +
                      "The SafeHouse zone radius was auto-sized to the building — check the blue " +
                      "gizmo sphere with the safe house selected.");
        }

        /// <summary>Instantiate one prefab as a proper prefab instance, snapped to the ground,
        /// with colliders guaranteed (asset-pack houses often ship without any).</summary>
        private static GameObject PlaceOne(string prefabPath, string name, Vector3 pos, float yaw, Transform parent)
        {
            var existing = GameObject.Find(name);
            if (existing != null)
            {
                Debug.Log($"[SunsetSceneBuilder] '{name}' already exists — skipped (delete it first to re-place).");
                return existing;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[SunsetSceneBuilder] Prefab not found at '{prefabPath}' — was the asset pack moved?");
                return null;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            Undo.RegisterCreatedObjectUndo(go, $"Place {name}");
            go.name = name;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.position = SnapToGround(pos);

            // Without colliders the player walks through walls, the monster SEES through them,
            // and the runtime navmesh bake ignores the building entirely.
            EnsureColliders(go);

            // Rest the model on the ground: after collider setup, shift so the lowest renderer
            // point sits at the ground height (many packs have their pivot mid-wall or at a corner).
            Bounds b = RendererBounds(go);
            if (b.size != Vector3.zero)
                go.transform.position += Vector3.up * (go.transform.position.y - b.min.y);

            return go;
        }

        private static Vector3 SnapToGround(Vector3 pos)
        {
            // Cast from high above so we land on the terrain/plane even if slightly uneven.
            if (Physics.Raycast(pos + Vector3.up * 100f, Vector3.down, out var hit, 300f))
                return hit.point;
            return new Vector3(pos.x, 0f, pos.z);   // flat 250m plane fallback
        }

        private static void EnsureColliders(GameObject root)
        {
            if (root.GetComponentsInChildren<Collider>(true).Length > 0) return;
            int added = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<Collider>() != null) continue;
                var mc = Undo.AddComponent<MeshCollider>(mf.gameObject);
                mc.sharedMesh = mf.sharedMesh;
                added++;
            }
            if (added > 0)
                Debug.Log($"[SunsetSceneBuilder] '{root.name}' had no colliders — added {added} MeshColliders.");
        }

        private static Bounds RendererBounds(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(root.transform.position, Vector3.zero);
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        /// <summary>Renderer bounds expressed in <paramref name="space"/>'s LOCAL coordinates.
        /// Needed inside LoadPrefabContents: the loaded root keeps its serialized world position,
        /// so world-space bounds are offset — feet placement needs root-relative numbers.</summary>
        private static Bounds LocalRendererBounds(GameObject go, Transform space)
        {
            Bounds world = RendererBounds(go);
            Vector3 min = space.InverseTransformPoint(world.min);
            Vector3 max = space.InverseTransformPoint(world.max);
            var b = new Bounds((min + max) * 0.5f, Vector3.zero);
            b.Encapsulate(min);
            b.Encapsulate(max);
            return b;
        }

        // ───────────────────────────── 2. DoorSync ─────────────────────────────

        [MenuItem("Tools/Sunset Curse/2. Create DoorSync Object")]
        public static void CreateDoorSync()
        {
            if (Object.FindAnyObjectByType<DoorSync>() != null)
            {
                Debug.Log("[SunsetSceneBuilder] A DoorSync already exists in the scene — nothing to do.");
                return;
            }
            var go = new GameObject("DoorSync");
            Undo.RegisterCreatedObjectUndo(go, "Create DoorSync");
            go.AddComponent<Unity.Netcode.NetworkObject>();
            go.AddComponent<DoorSync>();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[SunsetSceneBuilder] DoorSync created. Doors are now co-op synced. " +
                      "(Unity assigns its network id when you SAVE the scene — so save now.)");
        }

        // ───────────────────────────── 3. door assembly ─────────────────────────────

        [MenuItem("Tools/Sunset Curse/3. Add Door To Selected Building")]
        public static void AddDoorToSelected()
        {
            var doorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DoorPrefabPath);
            if (doorPrefab == null)
            {
                Debug.LogError($"[SunsetSceneBuilder] Door prefab missing at '{DoorPrefabPath}'.");
                return;
            }

            // Spawn near the selected building's front, or at the scene-view focus point.
            GameObject target = Selection.activeGameObject;
            Vector3 spawnPos;
            if (target != null)
            {
                Bounds b = RendererBounds(target);
                spawnPos = new Vector3(b.center.x, target.transform.position.y, b.center.z)
                           + target.transform.forward * (b.extents.z + 0.5f);
            }
            else if (SceneView.lastActiveSceneView != null)
            {
                spawnPos = SnapToGround(SceneView.lastActiveSceneView.pivot);
            }
            else
            {
                spawnPos = Vector3.zero;
            }

            // The hinge is an EMPTY at the door's rotation edge; the mesh hangs off it sideways
            // so it swings like a real door (see Door.cs header for the full explanation).
            var hinge = new GameObject("DoorHinge");
            Undo.RegisterCreatedObjectUndo(hinge, "Create Door");
            hinge.transform.position = spawnPos;
            if (target != null) hinge.transform.rotation = target.transform.rotation;

            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(doorPrefab, hinge.transform);
            mesh.name = "DoorMesh";
            // Zeroes the prefab's stray saved offset, then offsets half a width so the hinge sits
            // on the door's EDGE with its base at the hinge's ground level.
            SeatDoorMeshOnHinge(hinge, mesh);

            hinge.AddComponent<Door>();
            Selection.activeGameObject = hinge;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[SunsetSceneBuilder] Door created (selected in Hierarchy). Move the HINGE " +
                      "to one vertical edge of the doorway — the mesh swings around it. Set Open " +
                      "Angle to -90 on the Door component if it swings the wrong way. Run tool 2 " +
                      "once per scene so doors sync in co-op.");
        }

        // ───────────────────────────── 4. creep monster swap ─────────────────────────────

        [MenuItem("Tools/Sunset Curse/4. Swap Monster Model To Creep")]
        public static void SwapMonsterToCreep()
        {
            var creepPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreepModelPath);
            if (creepPrefab == null)
            {
                Debug.LogError($"[SunsetSceneBuilder] Creep prefab missing at '{CreepModelPath}'.");
                return;
            }

            // 1) Make the creep's clips LOOP. The FBX imports its takes with looping OFF, which
            //    would freeze the walk cycle after one playthrough inside our blend tree.
            MakeCreepClipsLoop();

            // 2) Build (or reuse) an AnimatorController: one float "Speed" drives a 1D blend —
            //    Idle at 0, walk at stalk speed, faster walk at run speed. MonsterAI feeds Speed
            //    with the agent's real velocity, so the animation follows the actual movement.
            var controller = BuildCreepController();

            // 3) Rebuild the Monster prefab: out with the Plague Doctor child, in with the Creep.
            var root = PrefabUtility.LoadPrefabContents(MonsterPrefabPath);
            try
            {
                // Remove any previous model child (Plague Doctor, or an older Creep from a re-run).
                foreach (Transform child in root.transform.Cast<Transform>().ToArray())
                {
                    if (child.name.Contains("Plague") || child.name.Contains("Creep"))
                        Object.DestroyImmediate(child.gameObject);
                }

                var creep = (GameObject)PrefabUtility.InstantiatePrefab(creepPrefab, root.transform);
                creep.name = "Creep_Model";
                creep.transform.localPosition = Vector3.zero;
                creep.transform.localRotation = Quaternion.identity;

                // Feet-on-ground: the NavMeshAgent floats the root 1m above the navmesh (Base
                // Offset), and the SCENE instance scales the whole monster ×3 — so in prefab-local
                // units the ground plane sits at y = -1/3. Shift the model so its lowest point
                // lands there.
                const float sceneRootScale = 3f;     // Monster instance in SampleScene is scaled ×3
                const float agentBaseOffset = 1f;    // NavMeshAgent "Base Offset" on the prefab
                Bounds cb = LocalRendererBounds(creep, root.transform);
                float targetLocalY = -agentBaseOffset / sceneRootScale;
                creep.transform.localPosition += Vector3.up * (targetLocalY - cb.min.y);

                // Wire the animator: controller on the model's Animator, and point MonsterAI's
                // Inspector field at it (the old reference was a dead link into Slow Jog.fbx —
                // Speed was silently never applied).
                var animator = creep.GetComponentInChildren<Animator>();
                if (animator == null) animator = creep.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;    // the NavMeshAgent moves her, not the clips

                var monsterAI = root.GetComponent<SunsetCurse.AI.MonsterAI>();
                var so = new SerializedObject(monsterAI);
                so.FindProperty("animator").objectReferenceValue = animator;
                so.ApplyModifiedProperties();

                PrefabUtility.SaveAsPrefabAsset(root, MonsterPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            Debug.Log("[SunsetSceneBuilder] Monster now wears the Creep model with a Speed-driven " +
                      "Idle/Walk blend. The scene Monster instance updates automatically (its tuned " +
                      "AI values are untouched). Press Play at night to check her feet sit on the " +
                      "ground — if she floats or sinks, nudge the Creep_Model child's Y inside " +
                      "Assets/_Project/Prefabs/Monster.prefab.");
        }

        // ───────────────────────────── 7. compound watcher monster ─────────────────────────────

        private const string PlagueDoctorModelPath = "Assets/Polytope Studio/Lowpoly_Characters/Sources/Meshes/PT_Plague_Doctor.fbx";
        private const string ZombieControllerPath  = "Assets/_Project/Art/Monsters/Zombie/ZombieMonster.controller";

        /// <summary>
        /// Builds the SECOND monster — the audio-driven compound watcher — in the open scene:
        /// NavMeshAgent + NetworkObject + NetworkTransform + CompoundWatcherAI + the Plague Doctor
        /// model wearing the old zombie AnimatorController. Parked at the valuables compound
        /// (the ItemSpawner position). Re-run safe: skips if a watcher already exists.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/7. Create Compound Watcher Monster")]
        public static void CreateCompoundWatcher()
        {
            var existing = Object.FindAnyObjectByType<SunsetCurse.AI.CompoundWatcherAI>(FindObjectsInactive.Include);
            if (existing != null)
            {
                // Upgrade path: re-running the tool retrofits newer pieces onto an already-built
                // watcher instead of duplicating her.
                if (existing.GetComponent<SunsetCurse.AI.MonsterFootsteps>() == null)
                {
                    Undo.AddComponent<SunsetCurse.AI.MonsterFootsteps>(existing.gameObject);
                    EditorSceneManager.MarkSceneDirty(existing.gameObject.scene);
                    Debug.Log("[SunsetSceneBuilder] Added MonsterFootsteps to the existing Watcher — " +
                              "assign your walk/run clips on it in the Inspector, then save.", existing);
                }
                else
                {
                    Debug.Log("[SunsetSceneBuilder] A Compound Watcher already exists in the scene — nothing to do.");
                }
                return;
            }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(PlagueDoctorModelPath);
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ZombieControllerPath);
            if (model == null) { Debug.LogError($"[SunsetSceneBuilder] Plague Doctor model missing at '{PlagueDoctorModelPath}'."); return; }
            if (controller == null) { Debug.LogError($"[SunsetSceneBuilder] Zombie controller missing at '{ZombieControllerPath}'."); return; }

            // Park her at the valuables compound — the ItemSpawner marks its centre.
            var compound = GameObject.Find("ItemSpawner");
            Vector3 pos = SnapToGround(compound != null ? compound.transform.position : new Vector3(85f, 0f, 99f));

            var go = new GameObject("Watcher_Monster");
            Undo.RegisterCreatedObjectUndo(go, "Create Compound Watcher");
            go.transform.position = pos;

            var agent = go.AddComponent<UnityEngine.AI.NavMeshAgent>();
            agent.radius = 0.35f;
            agent.height = 2f;
            agent.baseOffset = 0f;
            agent.angularSpeed = 360f;
            agent.acceleration = 20f;

            // Physical presence so players can't walk through her (hidden by day via the AI).
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.height = 2f;
            capsule.radius = 0.4f;
            capsule.center = new Vector3(0f, 1f, 0f);

            go.AddComponent<Unity.Netcode.NetworkObject>();
            go.AddComponent<Unity.Netcode.Components.NetworkTransform>();   // server-auth by default
            var ai = go.AddComponent<SunsetCurse.AI.CompoundWatcherAI>();
            go.AddComponent<SunsetCurse.AI.MonsterFootsteps>();   // assign walk/run clips in Inspector

            // The model child, feet resting on the ground, wearing the zombie animations.
            var m = (GameObject)PrefabUtility.InstantiatePrefab(model, go.transform);
            m.name = "PlagueDoctor_Model";
            m.transform.localPosition = Vector3.zero;
            Bounds b = RendererBounds(m);
            if (b.size != Vector3.zero)
                m.transform.localPosition += Vector3.up * (go.transform.position.y - b.min.y);

            var anim = m.GetComponentInChildren<Animator>();
            if (anim == null) anim = m.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;

            var so = new SerializedObject(ai);
            so.FindProperty("animator").objectReferenceValue = anim;
            so.ApplyModifiedProperties();

            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log("[SunsetSceneBuilder] Watcher created at the compound (selected). With it selected, " +
                      "size 'Zone Radius' so the blue gizmo circle covers the whole valuables area, then " +
                      "SAVE the scene (assigns its network id). She's invisible until night — that's normal.");
        }

        // ───────────────────────────── 10. tower fence + golden gate ─────────────────────────────

        /// <summary>
        /// Rings the escape tower with a fence and a GOLDEN-KEY-LOCKED gate:
        ///   • "TowerFence" — a FenceBorder configured like your MapFence (same prefab/length),
        ///     shrunk to a square around the tower, generated immediately.
        ///   • "TowerGate" — a hinged door assembly with the GoldenGate script: locked for
        ///     everyone until the golden key (compound, night 4) is turned in it; synced.
        /// AFTER RUNNING: delete the 1–2 fence segments where the gate should stand (inside
        /// TowerFence ▸ FenceBorderContainer), slide the gate into the gap, then run item 6 to
        /// snap its hinge.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/10. Fence The Escape Tower (+ locked gate)")]
        public static void FenceEscapeTower()
        {
            GameObject tower = ResolveEscapeTower();
            if (tower == null)
            {
                Debug.LogError("[SunsetSceneBuilder] No scout tower found — select your tower and re-run.");
                return;
            }

            // GROUND REFERENCE: the BOTTOM of the tower's own bounds. (A sky-raycast over the
            // tower hits the tower itself — that's how v1 stranded the fence at tower-top height.)
            Bounds towerBounds = RendererBounds(tower);
            Vector3 basePos = new Vector3(towerBounds.center.x, towerBounds.min.y, towerBounds.center.z);

            // ── Fence ring (create OR repair — re-running fixes a floating ring) ──
            var fenceGO = GameObject.Find("TowerFence");
            if (fenceGO == null)
            {
                fenceGO = new GameObject("TowerFence");
                Undo.RegisterCreatedObjectUndo(fenceGO, "Create TowerFence");
            }
            fenceGO.transform.position = basePos;
            var fence = fenceGO.GetComponent<SunsetCurse.World.FenceBorder>();
            if (fence == null) fence = fenceGO.AddComponent<SunsetCurse.World.FenceBorder>();

            // Copy the working config from the map's existing fence (prefab, segment length, yaw).
            SunsetCurse.World.FenceBorder source = null;
            foreach (var f in Object.FindObjectsByType<SunsetCurse.World.FenceBorder>(FindObjectsInactive.Include))
                if (f != fence) { source = f; break; }

            if (source != null)
            {
                var src = new SerializedObject(source);
                var dst = new SerializedObject(fence);
                foreach (string prop in new[] { "fencePrefab", "segmentLength", "segmentYawOffset",
                                                "snapToGround", "groundMask", "yOffset",
                                                "addCornerPosts", "cornerPostPrefab" })
                {
                    var s = src.FindProperty(prop);
                    var d = dst.FindProperty(prop);
                    if (s != null && d != null) dst.CopyFromSerializedProperty(s);
                }
                dst.FindProperty("borderSize").vector2Value = new Vector2(16f, 16f);
                dst.ApplyModifiedProperties();

                fence.Clear();      // wipe any floating segments from a previous run
                fence.Generate();   // regenerate at the corrected ground height
                Debug.Log($"[SunsetSceneBuilder] TowerFence generated at y={basePos.y:0.00} " +
                          $"(16×16m, config from '{source.gameObject.name}').", fenceGO);
            }
            else
            {
                Debug.LogWarning("[SunsetSceneBuilder] No existing FenceBorder found to copy from — " +
                                 "assign a Fence Prefab + Segment Length on TowerFence, set Border " +
                                 "Size 16×16, then right-click it ▸ Generate Fence Border.", fenceGO);
            }

            // ── The locked gate (create, reposition, or re-skin with the fence-pack door) ──
            Vector3 gatePos = basePos + new Vector3(0f, 0f, -8f);   // middle of the south fence edge
            var gateDoorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GateDoorPrefabPath);
            if (gateDoorPrefab == null)
            {
                // Fall back to the horror-pack door rather than failing outright.
                gateDoorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DoorPrefabPath);
                Debug.LogWarning($"[SunsetSceneBuilder] '{GateDoorPrefabPath}' not found — using the " +
                                 "horror-pack door instead.");
                if (gateDoorPrefab == null) { Debug.LogError("[SunsetSceneBuilder] No door prefab available."); return; }
            }

            var existingGate = Object.FindAnyObjectByType<SunsetCurse.World.GoldenGate>(FindObjectsInactive.Include);
            if (existingGate != null)
            {
                // If it's already wearing the requested prefab, re-seat BOTH the hinge AND the
                // mesh (the mesh child can carry the prefab's stray saved offset — see
                // SeatDoorMeshOnHinge); otherwise rebuild with the right mesh.
                GameObject meshChild = null;
                foreach (Transform child in existingGate.transform)
                    if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child.gameObject) == GateDoorPrefabPath)
                    { meshChild = child.gameObject; break; }

                if (meshChild != null)
                {
                    Undo.RecordObject(existingGate.transform, "Reposition TowerGate");
                    Undo.RecordObject(meshChild.transform, "Reposition TowerGate");
                    existingGate.transform.position = gatePos;
                    SeatDoorMeshOnHinge(existingGate.gameObject, meshChild);
                    Selection.activeGameObject = existingGate.gameObject;
                    Debug.Log("[SunsetSceneBuilder] Existing TowerGate re-seated (hinge AND mesh).", existingGate);
                    EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                    return;
                }
                Undo.DestroyObjectImmediate(existingGate.gameObject);   // old horror-door gate — replace
            }

            // The gate FRAME (posts + arch) the door hangs in — same pack, sits in the fence gap.
            if (GameObject.Find("TowerGateFrame") == null)
            {
                var framePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GateFramePrefabPath);
                if (framePrefab != null)
                {
                    var frame = (GameObject)PrefabUtility.InstantiatePrefab(framePrefab);
                    Undo.RegisterCreatedObjectUndo(frame, "Create TowerGateFrame");
                    frame.name = "TowerGateFrame";
                    frame.transform.position = gatePos;
                    Bounds fb = RendererBounds(frame);
                    if (fb.size != Vector3.zero)
                        frame.transform.position += Vector3.up * (gatePos.y - fb.min.y);
                    EnsureColliders(frame);
                }
            }

            var hinge = new GameObject("TowerGate");
            Undo.RegisterCreatedObjectUndo(hinge, "Create TowerGate");
            hinge.transform.position = gatePos;

            var mesh = (GameObject)PrefabUtility.InstantiatePrefab(gateDoorPrefab, hinge.transform);
            mesh.name = "GateMesh";
            SeatDoorMeshOnHinge(hinge, mesh);
            // The 2015 binary fence-pack prefab may ship without a collider — the E-prompt raycast
            // needs one, so box it from the renderer bounds if nothing is there.
            if (mesh.GetComponentInChildren<Collider>() == null)
            {
                var box = mesh.AddComponent<BoxCollider>();
                Bounds b = RendererBounds(mesh);
                if (b.size != Vector3.zero)
                {
                    box.center = mesh.transform.InverseTransformPoint(b.center);
                    Vector3 size = mesh.transform.InverseTransformVector(b.size);
                    box.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                }
            }
            hinge.AddComponent<SunsetCurse.World.GoldenGate>();
            Selection.activeGameObject = hinge;

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[SunsetSceneBuilder] Tower fence + locked gate ready. NOW: (1) delete the fence " +
                      "segment(s) where the gate belongs (TowerFence ▸ FenceBorderContainer — the gate " +
                      "is at the SOUTH edge), (2) slide the TowerGate into the gap, (3) run item 6 with " +
                      "it selected to snap the hinge, (4) SAVE. It unlocks with the golden key (night 4).");
        }

        private const string GateDoorPrefabPath =
            "Assets/Flooded_Grounds/Prefabs/Buildings/Structures1/Struct_Fence1_Gate_A_Door.prefab";
        private const string GateFramePrefabPath =
            "Assets/Flooded_Grounds/Prefabs/Buildings/Structures1/Struct_Fence1_Gate_A.prefab";

        /// <summary>Puts a door/gate mesh in its proper place relative to its hinge: flush at the
        /// hinge's height, slid half a width so the hinge sits on the door's EDGE.
        ///
        /// CRITICAL FIRST STEP: PrefabUtility.InstantiatePrefab KEEPS the prefab's own saved root
        /// position as the child's local offset — the 2015 fence-pack door was saved ~640m from
        /// the origin, which teleported the visible gate into the wilderness while its hinge sat
        /// innocently on the fence line. Zero the local pose BEFORE measuring anything.</summary>
        private static void SeatDoorMeshOnHinge(GameObject hinge, GameObject mesh)
        {
            mesh.transform.localPosition = Vector3.zero;
            mesh.transform.localRotation = Quaternion.identity;
            Bounds mb = RendererBounds(mesh);
            if (mb.size == Vector3.zero) return;
            Vector3 slide = mb.size.x >= mb.size.z
                ? hinge.transform.right * mb.extents.x
                : hinge.transform.forward * mb.extents.z;
            Vector3 drop = Vector3.up * (hinge.transform.position.y - mb.min.y);
            mesh.transform.position += slide + drop;
        }

        private static GameObject ResolveEscapeTower()
        {
            // 1) The authoritative answer: whatever tower item 8 wired into RadioTowerEscape.
            //    (Selection-based guessing burned us: "TowerGate"/"TowerFence" also contain
            //    "Tower", so re-running item 10 right after a run — which auto-selects the gate —
            //    anchored the whole build to the gate's own position.)
            var st = Object.FindAnyObjectByType<SunsetCurse.World.RadioTowerState>(FindObjectsInactive.Include);
            if (st != null && st.Tower != null) return st.Tower.gameObject;

            // 2) An explicitly selected scout tower (exact-name family only, never Gate/Fence).
            var sel = Selection.activeGameObject;
            if (sel != null && sel.name.StartsWith("ScoutTower") &&
                sel.GetComponentInChildren<Renderer>() != null)
                return sel;

            // 3) Fallback by name.
            return GameObject.Find("ScoutTower_East") ?? GameObject.Find("ScoutTower_West");
        }

        // ───────────────────────────── 11. snap spawn spots ─────────────────────────────

        /// <summary>
        /// Drops every ItemSpawner hiding spot onto the surface beneath it (ground OR house floor),
        /// so potions/parts never hover in mid-air. Casts from just above each spot — from inside
        /// the room, not from the sky — so spots inside buildings land on the FLOOR, not the roof.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/11. Snap ItemSpawner Spots To Surfaces")]
        public static void SnapSpawnSpots()
        {
            var spawner = Object.FindAnyObjectByType<SunsetCurse.World.NightValuableSpawner>(FindObjectsInactive.Include);
            if (spawner == null)
            {
                Debug.LogError("[SunsetSceneBuilder] No NightValuableSpawner (ItemSpawner) in the scene.");
                return;
            }

            var so = new SerializedObject(spawner);
            var points = so.FindProperty("spawnPoints");
            int snapped = 0, unchanged = 0, failed = 0;
            for (int i = 0; i < points.arraySize; i++)
            {
                var t = points.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                if (t == null) continue;

                // Primary: cast from 1.5m above the spot (stays under ceilings → hits floors).
                // Fallback: from 25m up, for spots buried under the terrain.
                Vector3 hitPoint;
                if (Physics.Raycast(t.position + Vector3.up * 1.5f, Vector3.down, out var hit, 60f,
                                    ~0, QueryTriggerInteraction.Ignore))
                    hitPoint = hit.point;
                else if (Physics.Raycast(t.position + Vector3.up * 25f, Vector3.down, out hit, 100f,
                                         ~0, QueryTriggerInteraction.Ignore))
                    hitPoint = hit.point;
                else
                {
                    Debug.LogWarning($"[SunsetSceneBuilder] '{t.name}': no surface found above or below — " +
                                     "move it near the ground by hand.", t);
                    failed++;
                    continue;
                }

                Vector3 target = hitPoint + Vector3.up * 0.12f;
                if (Vector3.Distance(t.position, target) < 0.02f) { unchanged++; continue; }
                Undo.RecordObject(t, "Snap spawn spot");
                t.position = target;
                snapped++;
            }

            EditorSceneManager.MarkSceneDirty(spawner.gameObject.scene);
            Debug.Log($"[SunsetSceneBuilder] Spawn spots: {snapped} snapped to a surface, {unchanged} " +
                      $"already correct, {failed} need manual fixing. (RadioItemSpawner shares these " +
                      "same Transforms, so both spawners are fixed together.)");
        }

        // ───────────────────────────── 12. building colliders ─────────────────────────────

        /// <summary>
        /// Scene-wide sweep: every building-sized mesh without a collider gets a MeshCollider, so
        /// players AND monsters collide with walls (the runtime navmesh bake also carves around
        /// them — no more Watcher strolling through a house). Skips foliage (FoliagePassThrough
        /// territory), small props, and anything alive (agents / players).
        /// </summary>
        [MenuItem("Tools/Sunset Curse/12. Ensure Building Colliders (scene-wide)")]
        public static void EnsureBuildingColliders()
        {
            string[] softKeywords = { "grass", "foliage", "leaf", "branch", "bush", "fern", "flower", "ivy", "berry" };
            int added = 0;
            foreach (var r in Object.FindObjectsByType<MeshRenderer>())
            {
                if (r.GetComponent<Collider>() != null) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                // Building-sized only: biggest dimension ≥ 2.5m (walls, roofs, whole houses).
                Vector3 size = r.bounds.size;
                if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 2.5f) continue;

                // Never on foliage or on anything that moves (players, monsters).
                string lower = r.name.ToLowerInvariant();
                bool soft = false;
                foreach (var k in softKeywords) if (lower.Contains(k)) { soft = true; break; }
                if (soft) continue;
                if (r.GetComponentInParent<UnityEngine.AI.NavMeshAgent>() != null) continue;
                if (r.GetComponentInParent<CharacterController>() != null) continue;

                var mc = Undo.AddComponent<MeshCollider>(r.gameObject);
                mc.sharedMesh = mf.sharedMesh;
                added++;
                Debug.Log($"[SunsetSceneBuilder] MeshCollider added: '{Path(r.transform)}'.", r);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[SunsetSceneBuilder] Building-collider sweep done — {added} MeshCollider(s) added. " +
                      "Monsters can no longer walk through these (the runtime navmesh bake respects " +
                      "colliders). For ENTERABLE houses, add a door with items 3 + 6.");
        }

        // ───────────────────────────── 13. anti-jump fence barriers ─────────────────────────────

        private const string BarrierName = "AntiJumpBarrier";            // OLD (child-based) barriers — cleaned up on re-run
        private const string BarrierContainerName = "FenceJumpBarriers"; // NEW: one container of world-aligned walls
        private const float BarrierExtraHeight  = 5f;    // world metres of invisible wall ABOVE the fence top
        private const float BarrierBelowGround  = 0.5f;  // extend below the base so there's no ground gap
        private const float BarrierMinThickness = 0.6f;  // min horizontal thickness (stops a fast jump tunnelling through)
        private const float BarrierPadding      = 0.15f; // small horizontal pad to close seams between segments
        private const float GateDoorwayClearance = 2.8f; // gate pieces: keep BELOW this clear so you can walk through

        /// <summary>
        /// Rebuilds the invisible anti-jump walls for EVERY fence in the scene (keyword "fence"
        /// anywhere in the hierarchy name chain — perimeter MapFence, TowerFence, compound/pavilion
        /// fences, VillagePack fences).
        ///
        /// WHY IT WAS BROKEN (players still jumped over): the previous version sized each barrier
        /// from the fence's MESH-LOCAL bounds and extended it along the mesh's LOCAL up axis. Many
        /// fence-pack meshes are modelled lying flat and then rotated upright by the prefab, so
        /// "local up" points sideways in the world — the barrier became a horizontal invisible
        /// LEDGE near the fence top. You'd jump, land on the ledge ("scrolled upwards"), and stroll
        /// over. This version builds each wall from the fence's WORLD bounds, world-axis-aligned,
        /// under a plain container (no parent rotation/scale to corrupt it): a genuinely vertical
        /// wall, {BarrierExtraHeight}m tall, ≥{BarrierMinThickness}m thick, sunk {BarrierBelowGround}m
        /// below the base. You get blocked and pushed back — no climbing.
        ///
        /// Gate rules: swinging Door/GoldenGate pieces are SKIPPED (must keep opening); gate FRAMES
        /// (name also contains "gate") are walled only ABOVE head height, so the doorway stays
        /// walkable but you can't hop over the top.
        ///
        /// Re-run-safe: it deletes the container (and any leftover old child barriers) first, then
        /// rebuilds from scratch. Also re-fits the tower gate door's interact collider.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/13. Add Anti-Jump Barriers To All Fences")]
        public static void AddFenceJumpBarriers()
        {
            // 1) Remove the OLD child-based barriers (the wrongly-oriented ledges) scene-wide.
            var oldBarriers = new List<GameObject>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
                if (t != null && t.name == BarrierName) oldBarriers.Add(t.gameObject);
            foreach (var go in oldBarriers) Undo.DestroyObjectImmediate(go);

            // 2) Fresh container for the new world-aligned walls.
            var oldContainer = GameObject.Find(BarrierContainerName);
            if (oldContainer != null) Undo.DestroyObjectImmediate(oldContainer);
            var container = new GameObject(BarrierContainerName);
            Undo.RegisterCreatedObjectUndo(container, "Create fence barriers");

            int walls = 0, gates = 0;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var rend = mf.GetComponent<MeshRenderer>();
                if (rend == null) continue;
                if (!NameChainContains(mf.transform, "fence")) continue;

                if (mf.GetComponentInParent<Door>() != null) continue;                    // swinging doors stay free
                if (mf.GetComponentInParent<CharacterController>() != null) continue;      // never on players
                if (mf.GetComponentInParent<UnityEngine.AI.NavMeshAgent>() != null) continue; // never on monsters

                Bounds wb = rend.bounds;                        // WORLD axis-aligned box — orientation-proof
                if (wb.size == Vector3.zero) continue;
                if (Mathf.Max(wb.size.x, wb.size.z) > 60f) continue;   // guard against a whole-map single mesh

                bool isGate = NameChainContains(mf.transform, "gate");
                BuildWorldBarrier(container.transform, wb, isGate);
                if (isGate) gates++; else walls++;
            }

            // Belt-and-braces for the "prompt only at the top edge" bug: snap the golden gate's
            // interact BoxCollider back onto the door mesh it's supposed to cover.
            var goldenGate = Object.FindAnyObjectByType<GoldenGate>(FindObjectsInactive.Include);
            if (goldenGate != null && RefitDoorCollider(goldenGate))
                Debug.Log("[SunsetSceneBuilder] TowerGate door collider re-fitted to the visible door.",
                          goldenGate);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[SunsetSceneBuilder] Fence barriers rebuilt under '{BarrierContainerName}': " +
                      $"{walls} solid walls + {gates} gate-top walls, each {BarrierExtraHeight}m tall and " +
                      $"≥{BarrierMinThickness}m thick (invisible, WORLD-aligned — no more climbable ledges). " +
                      $"Removed {oldBarriers.Count} old barrier(s). SAVE the scene; re-run after adding/moving fences.");
        }

        /// <summary>Build ONE world-axis-aligned invisible wall from a fence piece's WORLD bounds,
        /// under the barrier container (identity transform, so the box is truly vertical no matter
        /// how the fence mesh is rotated). Tall + thick + sunk below ground = the player is blocked
        /// and pushed back, never able to slide up and over.</summary>
        private static void BuildWorldBarrier(Transform parent, Bounds wb, bool gate)
        {
            var go = new GameObject(gate ? "GateTopBarrier" : "FenceBarrier");
            Undo.RegisterCreatedObjectUndo(go, "Fence barrier");
            go.transform.SetParent(parent, worldPositionStays: true);
            go.transform.rotation = Quaternion.identity;   // world-aligned — the whole point
            go.layer = 0;                                  // Default layer: the player CC collides with it

            var box = go.AddComponent<BoxCollider>();

            // Horizontal footprint = fence's world XZ + a little pad; the THINNER axis (the fence's
            // thickness) is bumped to a minimum so a fast jump can't tunnel through.
            float sizeX = wb.size.x + BarrierPadding * 2f;
            float sizeZ = wb.size.z + BarrierPadding * 2f;
            if (sizeX <= sizeZ) sizeX = Mathf.Max(sizeX, BarrierMinThickness);
            else                sizeZ = Mathf.Max(sizeZ, BarrierMinThickness);

            float bottom, top;
            if (gate)
            {
                // Keep the doorway (below head height) clear; wall everything above it.
                bottom = wb.min.y + GateDoorwayClearance;
                top    = wb.max.y + BarrierExtraHeight;
                if (top < bottom + BarrierExtraHeight) top = bottom + BarrierExtraHeight;  // tiny gate → still a real wall
            }
            else
            {
                bottom = wb.min.y - BarrierBelowGround;
                top    = wb.max.y + BarrierExtraHeight;
            }

            go.transform.position = new Vector3(wb.center.x, (bottom + top) * 0.5f, wb.center.z);
            box.center = Vector3.zero;
            box.size = new Vector3(sizeX, top - bottom, sizeZ);
        }

        /// <summary>"fence"/"gate" matching against the object's own name AND every ancestor's —
        /// pack meshes are often anonymous children ("polySurface12") under a named prefab root.</summary>
        private static bool NameChainContains(Transform t, string keyword)
        {
            for (Transform cur = t; cur != null; cur = cur.parent)
                if (cur.name.ToLowerInvariant().Contains(keyword)) return true;
            return false;
        }

        /// <summary>Re-fit a door's interact BoxCollider to its CURRENT renderer bounds. The
        /// TowerGate door was boxed while the 2015 prefab's stray root offset was still in play,
        /// which left the collider hanging off the visible door — the E-prompt then only fired
        /// when the crosshair grazed the overlap at the top edge.</summary>
        private static bool RefitDoorCollider(Door door)
        {
            var box = door.GetComponentInChildren<BoxCollider>(true);
            var rends = door.GetComponentsInChildren<Renderer>(true);
            if (box == null || rends.Length == 0) return false;
            Bounds b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            Undo.RecordObject(box, "Refit gate door collider");
            box.center = box.transform.InverseTransformPoint(b.center);
            Vector3 size = box.transform.InverseTransformVector(b.size);
            box.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            return true;
        }

        // ───────────────────────────── 14. intro cutscene ─────────────────────────────

        /// <summary>Drops an IntroCutscene object into the gameplay scene. It plays the opening
        /// slideshow on Start and works immediately (captions + colour washes + the red-flash
        /// scare); assign your imported illustrations + audio to its beats in the Inspector.</summary>
        [MenuItem("Tools/Sunset Curse/14. Setup Intro Cutscene")]
        public static void SetupIntroCutscene()
        {
            var existing = Object.FindAnyObjectByType<SunsetCurse.UI.IntroCutscene>(FindObjectsInactive.Include);
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[SunsetSceneBuilder] IntroCutscene already exists — selected it. Assign each " +
                          "beat's Image + Sound in the Inspector.");
                return;
            }
            var go = new GameObject("IntroCutscene");
            Undo.RegisterCreatedObjectUndo(go, "Create IntroCutscene");
            go.AddComponent<SunsetCurse.UI.IntroCutscene>();
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[SunsetSceneBuilder] IntroCutscene added (plays on Start). It works right now with " +
                      "captions + the red-flash scare; drop your illustrations + audio onto its beats to " +
                      "bring it to life, then SAVE the scene.");
        }

        // ───────────────────────────── 15. per-player appearance ─────────────────────────────

        /// <summary>Adds the PlayerAppearance component to the PlayerCapsule prefab so each player
        /// can get a different character model. After running, select the prefab and drop your 4
        /// character prefabs into its "Character Models" list (0 = Player 1 … 3 = Player 4).</summary>
        [MenuItem("Tools/Sunset Curse/15. Add Player Appearance To PlayerCapsule")]
        public static void SetupPlayerAppearance()
        {
            const string prefabPath = "Assets/_Project/Prefabs/PlayerCapsule.prefab";
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                Debug.LogError($"[SunsetSceneBuilder] PlayerCapsule prefab not found at '{prefabPath}'.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                if (root.GetComponent<SunsetCurse.Player.PlayerAppearance>() == null)
                {
                    root.AddComponent<SunsetCurse.Player.PlayerAppearance>();
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    Debug.Log("[SunsetSceneBuilder] Added PlayerAppearance to PlayerCapsule. NOW select the " +
                              "prefab in Assets/_Project/Prefabs/, assign your 4 character prefabs to " +
                              "'Character Models' (element 0 = Player 1 … 3 = Player 4), and set Target Height (~2.5).");
                }
                else
                {
                    Debug.Log("[SunsetSceneBuilder] PlayerCapsule already has PlayerAppearance — just assign " +
                              "the 4 models in its Inspector.");
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // ───────────────────────────── 9. climb animations ─────────────────────────────

        private const string PlayerControllerPath = "Assets/_Project/Animations/PlayerAnimator.controller";

        /// <summary>
        /// Wires your two imported climb clips into the player's Animator so teammates SEE you climb
        /// the tower (you're first-person, so this is purely for the remote view). Adds a "Climbing"
        /// bool + "ClimbDir" float, a 1D blend state (down at -1, up at +1), and the Any-State→Climb
        /// / Climb→Exit transitions. PlayerClimb + PlayerAnimatorDriver already drive those params.
        ///
        /// USE IT: import the two climb FBXs (Rig ▸ Humanoid, Loop Time ON), select BOTH in the
        /// Project window — or the up one then the down one — and run this. It sorts up/down by
        /// filename ("up"/"down"); if it can't tell, it assumes the first selected = up.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/9. Wire Climb Animations (select the 2 clips)")]
        public static void WireClimbAnimations()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(PlayerControllerPath);
            if (controller == null)
            {
                Debug.LogError($"[SunsetSceneBuilder] Player controller not found at '{PlayerControllerPath}'.");
                return;
            }

            // Gather AnimationClips from the selection (accept FBX models or the clips directly).
            var clips = new List<AnimationClip>();
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                if (obj is AnimationClip c) { clips.Add(c); continue; }
                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (sub is AnimationClip ac && !ac.name.StartsWith("__preview")) clips.Add(ac);
            }
            if (clips.Count < 2)
            {
                Debug.LogError("[SunsetSceneBuilder] Select your TWO climb clips (or their FBX files) " +
                               "in the Project window first — found " + clips.Count + ".");
                return;
            }

            // Decide which is up vs down (filename keyword; else selection order).
            AnimationClip up = clips.Find(c => c.name.ToLower().Contains("up"));
            AnimationClip down = clips.Find(c => c.name.ToLower().Contains("down"));
            if (up == null || down == null)
            {
                up = clips[0];
                down = clips[1];
                Debug.LogWarning($"[SunsetSceneBuilder] Couldn't tell up/down from names — assuming " +
                                 $"'{up.name}' = UP and '{down.name}' = DOWN. Re-run with swapped " +
                                 "selection order if that's backwards.");
            }

            AddParamIfMissing(controller, "Climbing", AnimatorControllerParameterType.Bool);
            AddParamIfMissing(controller, "ClimbDir", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;

            // Remove any prior Climb state so re-runs don't stack duplicates.
            foreach (var cs in sm.states)
                if (cs.state.name == "Climb") { sm.RemoveState(cs.state); break; }

            var blend = new BlendTree
            {
                name = "ClimbBlend",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "ClimbDir",
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(blend, controller);
            blend.AddChild(down, -1f);
            blend.AddChild(up, 1f);

            var climbState = sm.AddState("Climb");
            climbState.motion = blend;
            climbState.writeDefaultValues = true;

            // Any State → Climb when Climbing is true; Climb → Exit when it's false.
            var toClimb = sm.AddAnyStateTransition(climbState);
            toClimb.AddCondition(AnimatorConditionMode.If, 0f, "Climbing");
            toClimb.duration = 0.12f;
            toClimb.hasExitTime = false;
            toClimb.canTransitionToSelf = false;

            var fromClimb = climbState.AddExitTransition();
            fromClimb.AddCondition(AnimatorConditionMode.IfNot, 0f, "Climbing");
            fromClimb.duration = 0.15f;
            fromClimb.hasExitTime = false;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[SunsetSceneBuilder] Climb wired — UP='{up.name}', DOWN='{down.name}'. " +
                      "Press Play with a second instance (ParrelSync) and watch a teammate climb the " +
                      "tower. If up/down look swapped, re-run with the other clip selected first.");
        }

        private static void AddParamIfMissing(AnimatorController c, string name, AnimatorControllerParameterType type)
        {
            foreach (var p in c.parameters) if (p.name == name) return;
            c.AddParameter(name, type);
        }

        // ───────────────────────────── 16. monster scream animations ─────────────────────────────
        // (CreepControllerPath + ZombieControllerPath are already defined by menu items 4 and 7.)

        private const string CreepMeshPath = "Assets/Creep Horror Creature/Meshes/Creep_mesh.fbx";

        /// <summary>
        /// Wires the KILL-SCREAM animation states that MonsterAI / CompoundWatcherAI fire via the
        /// "Scream" animator trigger the moment they catch a player:
        ///   • MAIN MONSTER (Creep): fully automatic — the pack already ships 'Creep|Roar_Action'
        ///     on the same Generic rig, so it's pulled out of Creep_mesh.fbx and wired with ZERO
        ///     imports.
        ///   • WATCHER (ZombieMonster.controller): optional — her Plague Doctor rig is HUMANOID,
        ///     so import any Mixamo scream/roar (Rig ▸ Humanoid), SELECT the clip (or its FBX) in
        ///     the Project window, and run this again. With nothing selected she's skipped — she
        ///     still screams in AUDIO; the animation is a bonus.
        /// Re-run safe: replaces any prior Scream state instead of stacking duplicates.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/16. Wire Monster Scream Animations")]
        public static void WireMonsterScreams()
        {
            // A) Creep — find the pack's own roar inside the mesh FBX.
            var creepController = AssetDatabase.LoadAssetAtPath<AnimatorController>(CreepControllerPath);
            AnimationClip roar = null;
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(CreepMeshPath))
                if (sub is AnimationClip ac && ac.name == "Creep|Roar_Action") { roar = ac; break; }

            if (creepController == null)
                Debug.LogError($"[SunsetSceneBuilder] Creep controller not found at '{CreepControllerPath}'.");
            else if (roar == null)
                Debug.LogError($"[SunsetSceneBuilder] 'Creep|Roar_Action' not found inside '{CreepMeshPath}'.");
            else
            {
                WireScreamState(creepController, roar);
                Debug.Log("[SunsetSceneBuilder] MAIN MONSTER scream wired (Creep|Roar_Action → 'Scream' trigger).");
            }

            // B) Watcher — needs a HUMANOID scream clip; uses the selection if the user provided one.
            var zombieController = AssetDatabase.LoadAssetAtPath<AnimatorController>(ZombieControllerPath);
            AnimationClip watcherClip = null;
            foreach (var obj in Selection.objects)
            {
                if (obj is AnimationClip direct && !direct.name.StartsWith("__preview"))
                {
                    watcherClip = direct;
                    break;
                }
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (sub is AnimationClip ac && !ac.name.StartsWith("__preview")) { watcherClip = ac; break; }
                if (watcherClip != null) break;
            }

            if (zombieController == null)
                Debug.LogWarning($"[SunsetSceneBuilder] Watcher controller not found at '{ZombieControllerPath}' — skipped.");
            else if (watcherClip == null)
                Debug.Log("[SunsetSceneBuilder] WATCHER skipped (no clip selected). To wire her too: import a " +
                          "Mixamo scream (Rig ▸ Humanoid), select the clip in the Project window, re-run this " +
                          "menu item. She already screams in AUDIO either way.");
            else
            {
                WireScreamState(zombieController, watcherClip);
                Debug.Log($"[SunsetSceneBuilder] WATCHER scream wired ('{watcherClip.name}' → 'Scream' trigger).");
            }
        }

        /// <summary>Adds a 'Scream' trigger + state to the controller: Any State → Scream on the
        /// trigger, back to the default state when the clip finishes. Replaces a prior Scream
        /// state so the menu item is safely re-runnable.</summary>
        private static void WireScreamState(AnimatorController controller, AnimationClip clip)
        {
            AddParamIfMissing(controller, "Scream", AnimatorControllerParameterType.Trigger);

            var sm = controller.layers[0].stateMachine;
            foreach (var cs in sm.states)
                if (cs.state.name == "Scream") { sm.RemoveState(cs.state); break; }

            var scream = sm.AddState("Scream");
            scream.motion = clip;
            scream.writeDefaultValues = true;

            var toScream = sm.AddAnyStateTransition(scream);
            toScream.AddCondition(AnimatorConditionMode.If, 0f, "Scream");
            toScream.duration = 0.08f;
            toScream.hasExitTime = false;
            toScream.canTransitionToSelf = false;

            var back = scream.AddTransition(sm.defaultState);
            back.hasExitTime = true;
            back.exitTime = 0.95f;    // when the roar finishes...
            back.duration = 0.2f;     // ...blend back into locomotion

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        // ─────────────── 19. Living House animator ───────────────

        private const string LivingHouseFbxPath = "Assets/_Project/Prefabs/new house/living-househouse-head-rig/source/Living House.fbx";
        private const string LivingHouseControllerPath = "Assets/_Project/Prefabs/new house/living-househouse-head-rig/LivingHouse.controller";

        /// <summary>
        /// The Living House FBX carries 10 Generic-rig animations (idle / walk / run / reveal /
        /// ground / attack1-4) but the scene object has NO Animator, so none play. This builds a
        /// controller from those clips (idle = looping default; every other clip is a state reached
        /// by a trigger of the SAME name — 'walk', 'attack1', 'reveal', …) and adds an Animator
        /// wired to it on the SELECTED Living House object.
        ///
        /// USE IT: select your Living House in the Hierarchy (or Project), run this. Then drive it
        /// from code/events with animator.SetTrigger("attack1") etc.; idle plays on its own.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/19. Build Living House Animator (select the house)")]
        public static void BuildLivingHouseAnimator()
        {
            // Gather the FBX's clips (skip Unity's __preview + the 'ref' reference pose).
            var clips = new List<AnimationClip>();
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(LivingHouseFbxPath))
                if (sub is AnimationClip c && !c.name.StartsWith("__preview"))
                {
                    string bare = ClipShortName(c.name);
                    if (bare == "ref") continue;
                    clips.Add(c);
                }
            if (clips.Count == 0)
            {
                Debug.LogError($"[SunsetSceneBuilder] No animation clips found in '{LivingHouseFbxPath}'. " +
                               "Focus Unity so the FBX finishes importing, then re-run.");
                return;
            }

            // Loop the ambient locomotion clips so idle/walk/run don't freeze on their last frame.
            SetClipsLooping(LivingHouseFbxPath, new[] { "idle", "walk", "run", "ground" });
            // reload clips after the reimport
            clips.Clear();
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(LivingHouseFbxPath))
                if (sub is AnimationClip c && !c.name.StartsWith("__preview") && ClipShortName(c.name) != "ref")
                    clips.Add(c);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(LivingHouseControllerPath);
            var sm = controller.layers[0].stateMachine;

            // idle = default (fallback: first clip).
            AnimationClip idle = clips.Find(c => ClipShortName(c.name) == "idle") ?? clips[0];
            var idleState = sm.AddState("idle");
            idleState.motion = idle;
            sm.defaultState = idleState;

            foreach (var clip in clips)
            {
                string name = ClipShortName(clip.name);
                if (name == "idle") continue;
                AddParamIfMissing(controller, name, AnimatorControllerParameterType.Trigger);
                var st = sm.AddState(name);
                st.motion = clip;

                var toState = sm.AddAnyStateTransition(st);
                toState.AddCondition(AnimatorConditionMode.If, 0f, name);
                toState.duration = 0.15f;
                toState.hasExitTime = false;
                toState.canTransitionToSelf = false;

                var back = st.AddTransition(idleState);   // return to idle when the one-shot ends
                back.hasExitTime = true;
                back.exitTime = 0.9f;
                back.duration = 0.2f;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            // Attach an Animator to the selected house (or warn to select one).
            var target = Selection.activeGameObject;
            if (target != null)
            {
                var anim = target.GetComponent<Animator>() ?? Undo.AddComponent<Animator>(target);
                anim.runtimeAnimatorController = controller;
                anim.applyRootMotion = false;
                EditorUtility.SetDirty(target);
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log($"[SunsetSceneBuilder] Living House animator built ({clips.Count} clips) and " +
                          $"attached to '{target.name}'. idle loops by default; trigger the rest by name " +
                          "(e.g. animator.SetTrigger(\"reveal\")).", target);
            }
            else
            {
                Debug.LogWarning("[SunsetSceneBuilder] Controller built at " + LivingHouseControllerPath +
                                 ", but NO GameObject was selected — select your Living House and re-run " +
                                 "to attach the Animator (or add an Animator by hand and assign the controller).");
            }
        }

        /// <summary>"house_head_...|idle" → "idle"; "mixamo.com" → "mixamo.com".</summary>
        private static string ClipShortName(string full)
        {
            int bar = full.LastIndexOf('|');
            return (bar >= 0 ? full.Substring(bar + 1) : full).ToLowerInvariant();
        }

        private static void SetClipsLooping(string fbxPath, string[] shortNames)
        {
            var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (imp == null) return;
            var anims = imp.clipAnimations;
            if (anims == null || anims.Length == 0) anims = imp.defaultClipAnimations;
            bool changed = false;
            for (int i = 0; i < anims.Length; i++)
            {
                string bare = ClipShortName(anims[i].name);
                foreach (var n in shortNames)
                    if (bare == n && !anims[i].loopTime) { anims[i].loopTime = true; changed = true; }
            }
            if (changed) { imp.clipAnimations = anims; imp.SaveAndReimport(); }
        }

        // ─────────────── 20. Jumpscare character prefab ───────────────

        private const string ZombieScreamFbxPath = "Assets/_Project/Prefabs/new house/funny-fear-p2-rig/Zombie Scream (1).fbx";
        private const string JumpscarePrefabPath  = "Assets/_Project/Prefabs/new house/JumpscareCharacter.prefab";
        private const string JumpscareControllerPath = "Assets/_Project/Prefabs/new house/JumpscareScream.controller";

        // The SKINNED MESH lives in the model FBX; the scream is an animation-only FBX (0 mesh —
        // a Mixamo "without skin" export). We build the character from the MODEL and play the
        // scream clip on it (same rig, so the Generic clip retargets by matching bone paths).
        private const string JumpscareModelFbxPath = "Assets/_Project/Prefabs/new house/funny-fear-p2-rig/source/AWW HES SO FANNNYYYYY.fbx";

        /// <summary>
        /// Builds the jumpscare character PREFAB, two ways:
        ///  • An FBX SELECTED in the Project window (a Mixamo "WITH SKIN" download: mesh + skeleton
        ///    + animation in ONE file) → build entirely from it. Rig and clip come from the same
        ///    export, so the clip can never relocate the mesh — the failure that forced statue
        ///    mode on the original two-file build (see session 19g in CLAUDE.md).
        ///  • Nothing selected → legacy build: AWW model mesh + the Zombie Scream (1) clip
        ///    (two mismatched exports — keep JumpscareDirector ▸ Statue Mode ON for this one).
        /// Overwrites the same prefab asset, so the scene's Scary Prefab slot keeps working.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/20. Build Jumpscare Character Prefab")]
        public static void BuildJumpscareCharacter()
        {
            string modelPath = JumpscareModelFbxPath;
            string clipPath  = ZombieScreamFbxPath;

            // Selection mode: a with-skin FBX (mesh AND clip in one file) replaces both sources.
            string selPath = Selection.activeObject != null
                ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            if (!string.IsNullOrEmpty(selPath) && selPath.ToLowerInvariant().EndsWith(".fbx"))
            {
                bool hasMesh = false, hasClip = false;
                foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(selPath))
                {
                    if (sub is Mesh) hasMesh = true;
                    if (sub is AnimationClip ac && !ac.name.StartsWith("__preview")) hasClip = true;
                }
                if (hasMesh && hasClip)
                {
                    modelPath = selPath;
                    clipPath  = selPath;
                    Debug.Log($"[SunsetSceneBuilder] Jumpscare: building from SELECTED '{selPath}' " +
                              "(mesh + clip in one file — matched rig, safe to untick Statue Mode).");
                }
                else
                {
                    Debug.LogWarning($"[SunsetSceneBuilder] Selected FBX '{selPath}' has no " +
                                     $"{(hasMesh ? "animation clip" : "mesh")} — for Mixamo, download " +
                                     "WITH SKIN. Falling back to the default model+clip build.");
                }
            }

            var modelFbx = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (modelFbx == null)
            {
                Debug.LogError($"[SunsetSceneBuilder] Character MODEL FBX not found at " +
                               $"'{modelPath}'. Focus Unity so it imports, then re-run.");
                return;
            }

            // Make every clip in the animation source loop (the scream keeps going while watched).
            if (AssetImporter.GetAtPath(clipPath) is ModelImporter clipImp)
            {
                var clipAnims = clipImp.clipAnimations is { Length: > 0 }
                    ? clipImp.clipAnimations : clipImp.defaultClipAnimations;
                bool loopChanged = false;
                foreach (var ca in clipAnims)
                    if (!ca.loopTime) { ca.loopTime = true; loopChanged = true; }
                if (loopChanged) { clipImp.clipAnimations = clipAnims; clipImp.SaveAndReimport(); }
            }

            AnimationClip scream = null;
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(clipPath))
                if (sub is AnimationClip c && !c.name.StartsWith("__preview")) { scream = c; break; }
            if (scream == null)
                Debug.LogWarning($"[SunsetSceneBuilder] No scream clip inside '{clipPath}' — " +
                                 "the character will be visible but won't animate. Check that FBX imported.");

            // Controller: single default state = the scream (auto-plays on Instantiate).
            var controller = AnimatorController.CreateAnimatorControllerAtPath(JumpscareControllerPath);
            if (scream != null)
            {
                var sm = controller.layers[0].stateMachine;
                var st = sm.AddState("Scream");
                st.motion = scream;
                sm.defaultState = st;
                EditorUtility.SetDirty(controller);
            }

            // Build the instance from the MODEL, wire the Animator, save as a plain prefab.
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(modelFbx);
            inst.name = "JumpscareCharacter";
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            // Force the whole thing upright at origin: root + its immediate model/armature children
            // reset to identity (scale kept). Mixamo rigs stand upright at (0,0,0); any offset/tilt
            // here is the import artifact we're clearing. Deeper bones are untouched.
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            foreach (Transform child in inst.transform)
            {
                child.localPosition = Vector3.zero;
                child.localRotation = Quaternion.identity;
            }

            var anim = inst.GetComponentInChildren<Animator>() ?? inst.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;

            // Re-assign the model's EXTERNAL URP materials BY NAME — the unpack above captured the
            // FBX's EMBEDDED (pink, non-URP) material slots, but the real textured .mat files (the
            // black skin + red mask) already sit in the model folder. Match each renderer slot's
            // material name to the extracted .mat of the same name.
            string matFolder = System.IO.Path.GetDirectoryName(modelPath).Replace('\\', '/');
            if (matFolder.EndsWith("/source"))   // the AWW model sits in a source/ subfolder; mats live one up
                matFolder = System.IO.Path.GetDirectoryName(matFolder).Replace('\\', '/');
            var byName = new Dictionary<string, Material>();
            foreach (var g in AssetDatabase.FindAssets("t:Material", new[] { matFolder }))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g));
                if (mat != null) byName[mat.name] = mat;
            }
            int reassigned = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (byName.TryGetValue(mats[i].name, out var repl) && repl != mats[i])
                    { mats[i] = repl; changed = true; reassigned++; }
                }
                if (changed) r.sharedMaterials = mats;
            }

            // Fallback for slots the name-match missed: a Mixamo re-export RENAMES materials but
            // preserves SUBMESH ORDER, so any slot still pointing at an FBX-embedded material is
            // mapped to the original material of the same position (the AWW import's order).
            string[] originalOrder = { "body", "head_ears", "teeth", "head", "mouth", "eyes" };
            const string rigMatFolder = "Assets/_Project/Prefabs/new house/funny-fear-p2-rig";
            var ordered = new List<Material>();
            foreach (var n in originalOrder)
                ordered.Add(byName.TryGetValue(n, out var om)
                    ? om : AssetDatabase.LoadAssetAtPath<Material>($"{rigMatFolder}/{n}.mat"));
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length && i < ordered.Count; i++)
                {
                    if (ordered[i] == null || mats[i] == ordered[i]) continue;
                    string mp = mats[i] != null ? AssetDatabase.GetAssetPath(mats[i]) : null;
                    bool embedded = !string.IsNullOrEmpty(mp) && mp.ToLowerInvariant().EndsWith(".fbx");
                    if (mats[i] == null || embedded) { mats[i] = ordered[i]; changed = true; reassigned++; }
                }
                if (changed) r.sharedMaterials = mats;
            }
            Debug.Log($"[SunsetSceneBuilder] Jumpscare: re-assigned {reassigned} URP material(s) " +
                      $"(name-match from '{matFolder}', then order-match for Mixamo-renamed slots).");

            var prefab = PrefabUtility.SaveAsPrefabAsset(inst, JumpscarePrefabPath);
            Object.DestroyImmediate(inst);
            AssetDatabase.SaveAssets();

            Selection.activeObject = prefab;
            bool matched = modelPath == clipPath;   // single-file (Mixamo with-skin) build
            Debug.Log($"[SunsetSceneBuilder] Jumpscare character prefab built at '{JumpscarePrefabPath}' " +
                      $"from '{modelPath}'. NOW: (1) verify JumpscareDirector ▸ Scary Prefab still points " +
                      "at this prefab, (2) assign a Scream Sfx (e.g. zombie_jumpscare in _Project/Audio/SFX), " +
                      (matched
                        ? "(3) UNTICK JumpscareDirector ▸ Statue Mode — this build's rig and clip come from " +
                          "one file, so the animation is safe to play. Then Play + F9."
                        : "(3) keep Statue Mode ON — this legacy build's clip is the mismatched one. " +
                          "Then Play + F9."), prefab);
        }

        // ─────────────── 21 + 22. River corner (replaces the Living House) ───────────────
        //
        // Design: the Living House stood near the NW corner of the 250×250 map. A small river now
        // cuts that corner off diagonally (flowing SW↔NE), a bridge crosses it at the old house
        // spot, and the corner pocket beyond holds a haunted house whose doorstep is where the
        // dawn Teleport/Revival potion spawns.

        private const string RiverRootName     = "River_Root";
        private const string HauntedAnchorName = "HauntedHouse_Anchor";
        private const string HauntedRootName   = "HauntedHouse_Corner";
        private const string BridgeFbxPath     = "Assets/_Project/Prefabs/water/old-bridge/source/Bridge.fbx";
        private const string BridgeTexFolder   = "Assets/_Project/Prefabs/water/old-bridge/textures";
        private const string RiverMatPath      = "Assets/_Project/Prefabs/water/River_Water.mat";
        private const string WaterNormalPath   = "Assets/Polytope Studio/Lowpoly_Environments/Sources/Textures/PT_Water_NM_01.png";
        private const string WaterSfxPath      = "Assets/Flooded_Grounds/Content/Sounds/Water.mp3";
        private const string RiverRockPath     = "Assets/Polytope Studio/Lowpoly_Environments/Prefabs/Rocks/PT_River_Rock_Pile_02.prefab";

        private static readonly Vector3 RiverSpot    = new Vector3(-106.3f, 0f, 101.6f); // old Living House spot
        private static readonly Vector3 RiverFlowDir = new Vector3(1f, 0f, 1f).normalized;   // downstream (SW→NE)
        private static readonly Vector3 RiverCross   = new Vector3(-1f, 0f, 1f).normalized;  // toward the corner
        private static readonly Vector3 HauntedSpot  = new Vector3(-111f, 0f, 111f);         // corner pocket
        // Asymmetric width: the near bank (bridge/map side) stays put; the far side reaches ALL
        // the way past the map corner (the corner sits ~30m beyond the centreline), so the water
        // flows off the world edge — where the ground ends, the shader shows deep water.
        private const float RiverWidthNear = 4f;    // metres from centreline toward the map
        private const float RiverWidthFar  = 44f;   // metres from centreline — past the corner
        private const float RiverLength    = 95f;   // unchanged per design (width grows, not length)
        private const float WaterDepth     = 0.45f; // surface sits this high above the bed — wading depth
        private const float BridgeSpan     = 17f;   // bridge covers the CROSSING, not the whole lagoon
        private const string PwsShaderName = "FX/ProceduralWater";
        private const string RiverPwsMatPath = "Assets/_Project/Prefabs/water/River_Water_PWS.mat";

        [MenuItem("Tools/Sunset Curse/21. Build River + Bridge (removes Living House)")]
        public static void BuildRiverAndBridge()
        {
            // 1. The Living House makes way for the river.
            Vector3 spot = RiverSpot;
            var living = GameObject.Find("Living House");
            if (living != null)
            {
                spot = living.transform.position; spot.y = 0f;
                Undo.DestroyObjectImmediate(living);
                Debug.Log("[SunsetSceneBuilder] Living House removed — the river takes its place.");
            }
            float groundY = GroundHeightAt(spot);

            // 1b. The map fence runs straight through the widened water — remove the segments in
            // the flooded corner (user-approved). Where players wade past the world edge, the
            // ground ends and they fall — PlayerStats' fall recovery teleports them back.
            var mapFence = GameObject.Find("MapFence");
            if (mapFence != null)
            {
                var doomed = new List<GameObject>();
                foreach (Transform seg in mapFence.transform)
                {
                    Vector3 d = seg.position - spot; d.y = 0f;
                    float crossDist = Vector3.Dot(d, RiverCross);
                    float alongDist = Mathf.Abs(Vector3.Dot(d, RiverFlowDir));
                    if (crossDist > -(RiverWidthNear + 2f) && crossDist < RiverWidthFar + 2f &&
                        alongDist < RiverLength * 0.5f + 3f)
                        doomed.Add(seg.gameObject);
                }
                foreach (var g in doomed) Undo.DestroyObjectImmediate(g);
                if (doomed.Count > 0)
                    Debug.Log($"[SunsetSceneBuilder] Removed {doomed.Count} MapFence segment(s)/post(s) crossing the water.");
            }

            // Re-run safe: rebuild the whole river group from scratch. Delete EVERY leftover
            // river root and stray water plane — an interrupted earlier run can leave more than
            // one behind (seen in the wild as two stacked water planes, one white, one cyan).
            for (var oldRoot = GameObject.Find(RiverRootName); oldRoot != null;
                 oldRoot = GameObject.Find(RiverRootName))
                Undo.DestroyObjectImmediate(oldRoot);
            for (var strayWater = GameObject.Find("RiverWater"); strayWater != null;
                 strayWater = GameObject.Find("RiverWater"))
                Undo.DestroyObjectImmediate(strayWater);

            var root = new GameObject(RiverRootName);
            Undo.RegisterCreatedObjectUndo(root, "Build River");
            root.transform.position = new Vector3(spot.x, groundY, spot.z);

            // 2. Water: a strip along the diagonal, widened BACKWARD toward the corner (centreline
            // shifted so the near bank stays put), surface raised to wading depth. The visible
            // mesh is rebuilt denser at runtime by RiverFlowController (with a bank skirt), so
            // the primitive here is just a placeholder + transform.
            float totalWidth = RiverWidthNear + RiverWidthFar;
            Vector3 centerShift = RiverCross * ((RiverWidthFar - RiverWidthNear) * 0.5f);
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "RiverWater";
            Object.DestroyImmediate(water.GetComponent<Collider>());   // RiverFlowController adds its own TRIGGER
            water.transform.SetParent(root.transform, false);
            water.transform.localPosition = centerShift + Vector3.up * WaterDepth;
            water.transform.rotation = Quaternion.LookRotation(RiverFlowDir);   // plane local +Z = downstream
            water.transform.localScale = new Vector3(totalWidth / 10f, 1f, RiverLength / 10f); // Plane = 10×10m

            // Prefer the Procedural Water Shader pack (GPU Gerstner waves + shore/edge foam +
            // refraction). Its URP variant lives in the pack's URP.unitypackage — installed
            // session 21c. Falls back to the plain URP/Lit water if the shader isn't found.
            Material mat = null;
            var pws = Shader.Find(PwsShaderName);
            if (pws != null)
            {
                mat = AssetDatabase.LoadAssetAtPath<Material>(RiverPwsMatPath);
                if (mat == null)
                {
                    mat = new Material(pws);
                    AssetDatabase.CreateAsset(mat, RiverPwsMatPath);
                }
                mat.shader = pws;
                // River tuning: shallow murky forest water, small waves aligned downstream (world
                // XZ ≈ (0.7, 0.7)), foam kicking in on crests and wherever geometry intersects
                // (rocks, banks, the bridge posts) via scene depth.
                // OPACITY + LOOK (session 25 — "too transparent and unappealing"): the shader is now
                // LIT (sun/moon, shadows, every flashlight), so the colours below are ALBEDO, not
                // final pixels. Dark murky "cursed" water: alpha 1 mid-river, refraction almost fully
                // replaced by the murk (the grass bed no longer shows through), a thin translucent
                // rim only at the banks. Session 23's values (teal 0.32 albedo, alpha 0.92,
                // refraction 0.9) still let the bed through and glowed at night (unlit shader).
                mat.SetColor("_BaseColor", new Color(0.045f, 0.085f, 0.075f, 1f));
                mat.SetColor("_DeepColor", new Color(0.015f, 0.035f, 0.035f, 1f));
                mat.SetFloat("_DepthMax", 1.4f);
                mat.SetFloat("_Opacity", 1f);
                mat.SetFloat("_MinAlpha", 0.7f);
                mat.SetFloat("_DepthAlphaBoost", 0.6f);
                mat.SetFloat("_RefractionStrength", 0.97f);
                mat.SetFloat("_Absorption", 1.6f);
                mat.SetFloat("_EdgeFade", 0.25f);
                // Reflection is now the REAL sky (environment cubemap, auto-dimmed at night by the
                // shader) instead of a constant white-blue tint — the constant tint is what turned
                // the whole river into a "plain white plane" at grazing angles, which is why the old
                // tuning had to choke it to 0.15. Near-physical water Fresnel, moderate strength.
                mat.SetColor("_ReflectionColor", new Color(0.75f, 0.82f, 0.88f, 1f));
                mat.SetFloat("_SkyReflection", 0.6f);
                mat.SetFloat("_ReflectionRoughness", 0.12f);
                mat.SetFloat("_FresnelPower", 5f);
                mat.SetFloat("_FresnelScale", 0.7f);
                mat.SetFloat("_FresnelBias", 0.02f);
                // Glints from the real lights: sun by day, the moon at night, flashlights always.
                mat.SetColor("_SpecularColor", new Color(1f, 1f, 1f, 0.6f));
                mat.SetFloat("_Shininess", 220f);
                mat.SetFloat("_SunSpecular", 2.5f);
                mat.SetFloat("_AddLightSpecular", 2f);
                mat.SetFloat("_BodyLighting", 0.5f);
                mat.SetFloat("_RippleScale", 5f);
                mat.SetFloat("_RippleSpeed", 1.1f);
                // CREST FOAM FLOOD (the "always white no matter what we tune" bug): the shader's
                // crest foam is driven by normal tilt — crest = (1 − n.y − threshold)/(1 − threshold)
                // — and ADDS WHITE over everything. Steep Gerstner + strong ripple normals tilted
                // n.y enough to fire it across the WHOLE surface. Keep normals gentle and the
                // crest term quiet; shore/edge foam (depth-based, at rocks/banks) stays.
                mat.SetFloat("_RippleStrength", 0.35f);
                mat.SetFloat("_FoamIntensity", 0.6f);
                mat.SetFloat("_EdgeFoamIntensity", 0.8f);
                mat.SetColor("_FoamColor", new Color(0.75f, 0.78f, 0.74f, 1f));   // dirty grey, lit by the scene
                mat.SetFloat("_FoamCrestThreshold", 0.55f);
                mat.SetFloat("_FoamCrestIntensity", 0.25f);
                // MUST be well under WaterDepth (0.45): shore foam appears where the water is
                // shallower than this, and the whole bed sits 0.45 below the surface — at 0.5
                // the ENTIRE river rendered as solid white foam (the "white plane" symptom).
                mat.SetFloat("_ShoreDepth", 0.12f);
                // Amplitudes must be CLEARLY visible from shore at eye level — the first pass
                // (7cm) read as a dead-flat plane. ~18cm rollers + smaller chop on top. Steepness
                // stays LOW: steep crests tilt normals → crest-foam flood (see above).
                mat.SetVector("_GAmplitude",   new Vector4(0.18f, 0.12f, 0.10f, 0.08f));
                mat.SetVector("_GFrequency",   new Vector4(1.6f, 2.2f, 1.3f, 2.8f));
                mat.SetVector("_GSteepness",   new Vector4(0.45f, 0.40f, 0.35f, 0.30f));
                mat.SetVector("_GSpeed",       new Vector4(1.1f, 1.4f, 0.9f, 1.7f));
                mat.SetVector("_GDirectionAB", new Vector4(0.7f, 0.7f, 0.55f, 0.83f));
                mat.SetVector("_GDirectionCD", new Vector4(0.83f, 0.55f, 0.6f, 0.8f));
                EditorUtility.SetDirty(mat);
            }
            if (mat == null)
            {
                mat = AssetDatabase.LoadAssetAtPath<Material>(RiverMatPath);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, RiverMatPath);
                }
            }
            bool usingPws = pws != null && mat.shader == pws;
            if (!usingPws)
            {
                // FALLBACK ONLY (pack shader missing): plain URP/Lit translucent water with the
                // Polytope ripple normal map.
                mat.SetFloat("_Surface", 1f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetFloat("_ZWrite", 0f);
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                mat.SetColor("_BaseColor", new Color(0.10f, 0.18f, 0.20f, 0.88f));
                mat.SetFloat("_Smoothness", 0.9f);
                if (AssetImporter.GetAtPath(WaterNormalPath) is TextureImporter waterNormImp &&
                    waterNormImp.textureType != TextureImporterType.NormalMap)
                {
                    waterNormImp.textureType = TextureImporterType.NormalMap;
                    waterNormImp.SaveAndReimport();
                }
                var waterNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(WaterNormalPath);
                if (waterNormal != null)
                {
                    mat.SetTexture("_BumpMap", waterNormal);
                    mat.EnableKeyword("_NORMALMAP");
                }
                mat.mainTextureScale = new Vector2(2f, RiverLength / 6f);
                EditorUtility.SetDirty(mat);
                Debug.LogWarning($"[SunsetSceneBuilder] '{PwsShaderName}' shader not found — using the " +
                                 "plain URP/Lit water. Import the pack's URP.unitypackage and re-run for foam/waves.");
            }

            var waterRend = water.GetComponent<MeshRenderer>();
            waterRend.sharedMaterial = mat;
            waterRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Runtime brain: dense mesh + Perlin ripple + trigger current + buoyancy hand-off.
            water.AddComponent<RiverFlowController>();

            // 3. Water SFX: gentle 3D loop at the crossing (clip already ships with Flooded Grounds).
            var sfx = water.AddComponent<AudioSource>();
            sfx.clip = AssetDatabase.LoadAssetAtPath<AudioClip>(WaterSfxPath);
            sfx.loop = true; sfx.playOnAwake = true; sfx.spatialBlend = 1f;
            sfx.minDistance = 4f; sfx.maxDistance = 38f;
            sfx.rolloffMode = AudioRolloffMode.Linear;
            sfx.volume = 0.55f;
            if (sfx.clip == null)
                Debug.LogWarning($"[SunsetSceneBuilder] Water loop not found at '{WaterSfxPath}' — assign any river clip on RiverWater's AudioSource.");

            // 4. Bridge across the flow (the user-imported old-bridge model).
            var bridgeFbx = AssetDatabase.LoadAssetAtPath<GameObject>(BridgeFbxPath);
            if (bridgeFbx != null)
            {
                var bridge = (GameObject)PrefabUtility.InstantiatePrefab(bridgeFbx, root.transform);
                bridge.name = "RiverBridge";
                bridge.transform.localPosition = Vector3.zero;               // 10e lesson: reset before measuring
                bridge.transform.localRotation = Quaternion.identity;
                Bounds bb = RendererBoundsOf(bridge);
                Vector3 longAxis = bb.size.x >= bb.size.z ? Vector3.right : Vector3.forward;
                bridge.transform.rotation = Quaternion.FromToRotation(longAxis, RiverCross);
                // The bridge covers the CROSSING near the old house spot — from the near bank
                // toward the corner-side shore — NOT the whole widened lagoon.
                ScaleToMaxDimension(bridge, BridgeSpan);
                Vector3 bridgeSpot = spot + RiverCross * (BridgeSpan * 0.5f - RiverWidthNear);
                SeatByBounds(bridge, new Vector3(bridgeSpot.x, groundY, bridgeSpot.z), out _);
                int bCols = AddCollidersUnder(bridge.transform, 0.35f);
                int bMats = BuildMaterialsFor(bridge, BridgeTexFolder, "bridge");
                Debug.Log($"[SunsetSceneBuilder] Bridge placed across the river ({bCols} collider(s), {bMats} URP material(s)).");
            }
            else Debug.LogWarning($"[SunsetSceneBuilder] Bridge FBX not found at '{BridgeFbxPath}' — river built without it.");

            // 5. River rocks IN the shallows — the shader's depth-based edge foam draws white
            // rings around them automatically. Only placed where real ground exists under the
            // water (the far side hangs past the world edge, where a rock would float in void).
            var rock = AssetDatabase.LoadAssetAtPath<GameObject>(RiverRockPath);
            if (rock != null)
            {
                float[] along = { -30f, -14f, 2f, 16f, 30f };
                float[] cross = { 6f, -1.5f, 12f, 3f, 9f };
                for (int i = 0; i < along.Length; i++)
                {
                    Vector3 p = spot + RiverFlowDir * along[i] + RiverCross * cross[i];
                    if (!Physics.Raycast(p + Vector3.up * 60f, Vector3.down, out RaycastHit rockHit,
                                         200f, ~0, QueryTriggerInteraction.Ignore))
                        continue;   // no ground here (past the world edge) — skip
                    var r = (GameObject)PrefabUtility.InstantiatePrefab(rock, root.transform);
                    r.transform.position = rockHit.point;
                    r.transform.rotation = Quaternion.Euler(0f, i * 73f, 0f);
                }
            }

            // 6. PINK-PROOF the whole group: the Polytope ENVIRONMENTS pack (river rocks) was
            // never RP-converted (only the Props pack was), so its materials are Built-in Standard
            // → magenta in URP. Convert every non-URP .mat used under the river root in place —
            // this fixes those shared materials everywhere they're used. FBX-embedded materials
            // are read-only and skipped (the bridge already got real URP mats from its textures).
            var urpLit = Shader.Find("Universal Render Pipeline/Lit");
            int fixedMats = 0;
            var sweptMats = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m == mat || !sweptMats.Add(m)) continue;   // never touch the water itself
                    string mp = AssetDatabase.GetAssetPath(m);
                    if (!string.IsNullOrEmpty(mp) && mp.ToLowerInvariant().EndsWith(".fbx")) continue;
                    if (ConvertToUrpLit(m, urpLit)) fixedMats++;
                }
            if (fixedMats > 0)
                Debug.Log($"[SunsetSceneBuilder] Converted {fixedMats} non-URP material(s) under the river to URP/Lit (un-pinks the rocks).");

            // 7. Anchor for menu 22's haunted house, in the cut-off corner pocket.
            if (GameObject.Find(HauntedAnchorName) == null)
            {
                var anchor = new GameObject(HauntedAnchorName);
                Undo.RegisterCreatedObjectUndo(anchor, "Haunted anchor");
                anchor.transform.position = new Vector3(HauntedSpot.x, GroundHeightAt(HauntedSpot), HauntedSpot.z);
            }

            AssetDatabase.SaveAssets();
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            // The version tag PROVES which build of this tool ran — if the console shows an older
            // tag (or none), the editor hadn't recompiled when the menu was clicked.
            Debug.Log("[SunsetSceneBuilder] RIVER BUILD v5 — THE white-water bug is dead: the pink-proof " +
                      "sweep was converting the water material itself to plain URP/Lit (white) on every " +
                      "run because 'FX/ProceduralWater' didn't match its URP name check. The water shader " +
                      $"is now restored + retuned (shader on mat: '{mat.shader.name}'). Judge in PLAY mode. " +
                      "NEXT: nudge + SAVE.");
        }

        [MenuItem("Tools/Sunset Curse/22. Place Haunted House At River Corner (select prefab)")]
        public static void PlaceHauntedHouseAtCorner()
        {
            var sel = Selection.activeObject as GameObject;
            if (sel == null)
            {
                Debug.LogError("[SunsetSceneBuilder] Select the imported haunted-house prefab/FBX in the " +
                               "Project window first, then run this again.");
                return;
            }

            var anchor = GameObject.Find(HauntedAnchorName);
            Vector3 spot = anchor != null ? anchor.transform.position : HauntedSpot;
            float groundY = GroundHeightAt(spot);

            var oldRoot = GameObject.Find(HauntedRootName);
            if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);   // re-run safe

            var root = new GameObject(HauntedRootName);
            Undo.RegisterCreatedObjectUndo(root, "Place haunted house");

            var house = PrefabUtility.InstantiatePrefab(sel, root.transform) as GameObject;
            if (house == null) house = Object.Instantiate(sel, root.transform);
            house.name = "HauntedHouse";
            house.transform.localPosition = Vector3.zero;                 // 10e lesson: reset before measuring
            house.transform.localRotation = Quaternion.identity;
            if (PrefabUtility.IsPartOfPrefabInstance(house))
                PrefabUtility.UnpackPrefabInstance(house, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            // Face back across the river toward the bridge/map (rotate later if the door differs).
            house.transform.rotation = Quaternion.LookRotation(new Vector3(1f, 0f, -1f).normalized);

            // Adaptive size (17b lesson): cm-export dollhouses get ×100; oversized packs get clamped.
            Bounds pre = RendererBoundsOf(house);
            float preMax = Mathf.Max(pre.size.x, Mathf.Max(pre.size.y, pre.size.z));
            if (preMax > 0.0001f && preMax < 5f) house.transform.localScale *= 100f;
            Bounds mid = RendererBoundsOf(house);
            float midMax = Mathf.Max(mid.size.x, Mathf.Max(mid.size.y, mid.size.z));
            if (midMax > 26f) ScaleToMaxDimension(house, 18f);            // keep it inside the corner pocket

            SeatByBounds(house, new Vector3(spot.x, groundY, spot.z), out Bounds hb);
            int cols = AddCollidersUnder(house.transform, 0.35f);

            // URP materials from a sibling textures folder, when the pack ships one.
            int mats = 0;
            string selPath = AssetDatabase.GetAssetPath(sel);
            if (!string.IsNullOrEmpty(selPath))
            {
                string dir = System.IO.Path.GetDirectoryName(selPath).Replace('\\', '/');
                string up  = System.IO.Path.GetDirectoryName(dir)?.Replace('\\', '/');
                foreach (var cand in new[] { dir + "/textures", up + "/textures", dir })
                    if (!string.IsNullOrEmpty(cand) && AssetDatabase.IsValidFolder(cand))
                    { mats = BuildMaterialsFor(house, cand, "hauntedhouse"); break; }
            }

            // Doorstep marker on the bridge-facing side — and the dawn potion moves there.
            var doorstep = new GameObject("PotionDoorstep");
            doorstep.transform.SetParent(root.transform, true);
            Vector3 front = hb.center + new Vector3(1f, 0f, -1f).normalized
                          * (Mathf.Max(hb.extents.x, hb.extents.z) + 1.6f);
            doorstep.transform.position = new Vector3(front.x, GroundHeightAt(front) + 0.1f, front.z);

            var spawner = Object.FindAnyObjectByType<DawnPotionSpawner>();
            if (spawner != null)
            {
                var so = new SerializedObject(spawner);
                var sp = so.FindProperty("spawnPoint");
                var existingPoint = sp != null ? sp.objectReferenceValue as Transform : null;
                if (existingPoint != null)
                {
                    Undo.RecordObject(existingPoint, "Move potion spawn");
                    existingPoint.position = doorstep.transform.position;   // keep wiring, move the marker
                }
                else if (sp != null)
                {
                    sp.objectReferenceValue = doorstep.transform;
                    so.ApplyModifiedProperties();
                }
                Debug.Log("[SunsetSceneBuilder] Dawn Teleport/Revival potion now spawns at the haunted house doorstep.");
            }
            else Debug.LogWarning("[SunsetSceneBuilder] No DawnPotionSpawner in the scene — potion spawn NOT moved.");

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[SunsetSceneBuilder] Haunted house placed: {hb.size.x:0.0}×{hb.size.y:0.0}×{hb.size.z:0.0}m " +
                      $"at the river corner ({cols} collider(s), {mats} URP material(s)). Nudge the house / " +
                      "PotionDoorstep to sit exactly at the door, rotate if the entrance faces the wrong way, " +
                      "then SAVE the scene. If it renders pink, run Tools ▸ 5 on it.");
        }

        /// <summary>
        /// Points every renderer slot on the SELECTED scene object at the matching standalone
        /// .mat asset of the same (normalized) name — e.g. a slot wearing our blind fallback
        /// 'object1264mtl' gets the pack's own extracted 'Object1264_mtl.mat', which the user may
        /// have already textured by hand. Fixes "the whole model is flat brown/grey" after a
        /// pack's texture names defeat the automatic matcher. In-place: placement untouched.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/23. Relink Pack Materials On Selected (by name)")]
        public static void RelinkPackMaterials()
        {
            var target = Selection.activeGameObject;
            if (target == null || !target.scene.IsValid())
            {
                Debug.LogError("[SunsetSceneBuilder] Select the placed object in the HIERARCHY first " +
                               "(e.g. HauntedHouse_Corner), then run this again.");
                return;
            }

            var urp = Shader.Find("Universal Render Pipeline/Lit");

            // Index every standalone .mat in the project by normalized name; textured ones win.
            bool Textured(Material m) =>
                (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null) ||
                (m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null);
            var byKey = new Dictionary<string, Material>();
            foreach (var g in AssetDatabase.FindAssets("t:Material"))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (p.StartsWith(HouseMatFolder)) continue;                 // our generated fallbacks
                if (p.ToLowerInvariant().EndsWith(".fbx")) continue;        // embedded — read-only
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m == null) continue;
                string k = NormalizeName(m.name);
                if (string.IsNullOrEmpty(k)) continue;
                if (!byKey.TryGetValue(k, out var existing)) byKey[k] = m;
                else if (Textured(m) && !Textured(existing)) byKey[k] = m;
            }

            int relinked = 0, converted = 0;
            var swept = new HashSet<Material>();
            foreach (var r in target.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string k = NormalizeName(mats[i].name.Replace(" (Instance)", ""));
                    if (byKey.TryGetValue(k, out var repl) && repl != mats[i])
                    {
                        Undo.RecordObject(r, "Relink pack materials");
                        mats[i] = repl;
                        changed = true;
                        relinked++;
                        if (swept.Add(repl) && ConvertToUrpLit(repl, urp)) converted++;
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[SunsetSceneBuilder] Relinked {relinked} material slot(s) on '{target.name}' to " +
                      $"the packs' own .mat assets ({converted} converted to URP on the way). If pieces " +
                      "stay flat-coloured, that pack has no ready-made .mat for them — assign by hand or " +
                      "run menu 5.");
        }

        // ─────────────── 24. river bucket + ambience ───────────────
        //
        // The ritual's river-water offering (session 23): ONE bucket spawns inside
        // TheLoothouse_Shed; a player grabs it, wades ≥2m into the river, holds X for 5s to fill
        // it, and the ritual only starts/progresses while the FILLED bucket stands at the altar.
        // This menu wires the whole thing: a "RiverBucket" GameObject with NetworkObject +
        // RiverWaterBucket (spawn/pickup/respawn) + RiverBucketFiller (X-hold channel + SFX
        // slots) + RiverAmbience (night river audio slots). Re-run-safe.

        [MenuItem("Tools/Sunset Curse/24. Setup River Bucket + River Ambience")]
        public static void SetupRiverBucket()
        {
            var go = GameObject.Find("RiverBucket");
            if (go == null)
            {
                go = new GameObject("RiverBucket");
                Undo.RegisterCreatedObjectUndo(go, "Setup River Bucket");
                go.transform.position = new Vector3(-67f, -2f, 33f);   // the shed spot (gizmo aid only)
            }

            if (go.GetComponent<Unity.Netcode.NetworkObject>() == null)
                Undo.AddComponent<Unity.Netcode.NetworkObject>(go);
            if (go.GetComponent<RiverWaterBucket>() == null)
                Undo.AddComponent<RiverWaterBucket>(go);
            if (go.GetComponent<SunsetCurse.Player.RiverBucketFiller>() == null)
                Undo.AddComponent<SunsetCurse.Player.RiverBucketFiller>(go);
            if (go.GetComponent<RiverAmbience>() == null)
                Undo.AddComponent<RiverAmbience>(go);

            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log("[SunsetSceneBuilder] RiverBucket ready (NetworkObject + RiverWaterBucket + " +
                      "RiverBucketFiller + RiverAmbience). Optional Inspector drops: bucket model → " +
                      "RiverWaterBucket ▸ Bucket Prefab; SFX clips → RiverBucketFiller / RiverAmbience; " +
                      "bucket inventory image → InventoryUI ▸ Bucket Icon. Then SAVE the scene.");
        }

        // ─────────────── 17. haunted parish environment ───────────────
        //
        // Replaces the RETIRED two-model haunted house (creepy-house exterior + underground
        // diplomat interior + teleport doors). The parish is one PS1-style village environment
        // (church, statues, huts) placed directly on the map: enterable on foot, monster-
        // compatible (it lives on the surface navmesh), no teleports needed. Running the menu
        // also DELETES the old HauntedHouse_Root from the scene if it's still there.

        private const string LegacyHouseRootName = "HauntedHouse_Root";
        private const string ParishRootName  = "HauntedParish_Root";
        private const string ParishFbxPath   = "Assets/_Project/Prefabs/house/haunted-parish-ps1ps2-environment-mcpato/source/horrorVillageV1.fbx";
        private const string ParishTexFolder = "Assets/_Project/Prefabs/house/haunted-parish-ps1ps2-environment-mcpato/textures";
        private const string HouseMatFolder  = "Assets/_Project/Prefabs/house/Materials";
        private static readonly Vector3 HouseSpot = new Vector3(-55f, 0f, 105f);   // user-requested X/Z; Y ground-snapped

        [MenuItem("Tools/Sunset Curse/17. Place Haunted Parish Environment")]
        public static void PlaceHauntedParish()
        {
            var parishAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ParishFbxPath);
            if (parishAsset == null)
            {
                Debug.LogError($"[SunsetSceneBuilder] Parish FBX not found at '{ParishFbxPath}' — " +
                               "focus Unity so the import finishes, then re-run.");
                return;
            }

            // Clean re-run — and REMOVE THE RETIRED HAUNTED-HOUSE UPDATE if it's still in the
            // scene (creepy-house exterior + underground interior + teleport doors, old root).
            var legacy = GameObject.Find(LegacyHouseRootName);
            if (legacy != null) Undo.DestroyObjectImmediate(legacy);
            var old = GameObject.Find(ParishRootName);
            if (old != null) Undo.DestroyObjectImmediate(old);

            // Normal-map import types FIRST — a SaveAndReimport later, while we're iterating live
            // scene renderers, can rebuild the FBX instances under our feet.
            NormalizeNormalMapTypes(ParishTexFolder);   // (this pack has none, but future-proof)

            // Ground height BEFORE any new geometry exists that the ray could hit.
            float groundY = GroundHeightAt(HouseSpot);

            var root = new GameObject(ParishRootName);
            Undo.RegisterCreatedObjectUndo(root, "Place Haunted Parish");

            var parish = (GameObject)PrefabUtility.InstantiatePrefab(parishAsset, root.transform);
            parish.name = "HauntedParish";
            parish.transform.localPosition = Vector3.zero;         // session-10e lesson: reset BEFORE measuring
            parish.transform.localRotation = Quaternion.identity;
            parish.transform.localScale = Vector3.one;

            // The pack ships its own SKY DOME (material 'sky', texture 'skybake') — a baked sky
            // sphere makes no sense inside our world and would corrupt the size measurement.
            // Unpack first (children of a prefab instance can't be deleted), then drop sky pieces.
            PrefabUtility.UnpackPrefabInstance(parish, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);
            int skyRemoved = 0;
            foreach (var r in parish.GetComponentsInChildren<MeshRenderer>(true))
            {
                bool sky = r.name.ToLowerInvariant().Contains("sky");
                if (!sky)
                    foreach (var m in r.sharedMaterials)
                        if (m != null && m.name.ToLowerInvariant().Contains("sky")) { sky = true; break; }
                if (sky) { Undo.DestroyObjectImmediate(r.gameObject); skyRemoved++; }
            }

            // cm-export fix (UnitScaleFactor=1): if it imported as a miniature, restore design size.
            Bounds pre = RendererBoundsOf(parish);
            float preMax = Mathf.Max(pre.size.x, Mathf.Max(pre.size.y, pre.size.z));
            if (preMax > 0.0001f && preMax < 5f) parish.transform.localScale *= 100f;

            SeatByBounds(parish, new Vector3(HouseSpot.x, groundY, HouseSpot.z), out Bounds b);

            int cols = AddCollidersUnder(parish.transform, 0.35f);
            int mats = BuildMaterialsFor(parish, ParishTexFolder, "parish");

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[SunsetSceneBuilder] Haunted parish placed: {b.size.x:0.0}×{b.size.y:0.0}×" +
                      $"{b.size.z:0.0}m at ({HouseSpot.x}, {groundY:0.0}, {HouseSpot.z}). Removed " +
                      $"{skyRemoved} sky piece(s), added {cols} collider(s), built {mats} material(s). " +
                      "It sits on the SURFACE like any other building — players walk in on foot and " +
                      "the monsters path around/inside it via the normal navmesh (no teleports). " +
                      "Drag HauntedParish_Root to reposition, then SAVE.", root);
        }

        private static float GroundHeightAt(Vector3 spot)
        {
            if (Physics.Raycast(new Vector3(spot.x, 120f, spot.z), Vector3.down, out var hit, 400f,
                                ~0, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return 0f;
        }

        /// <summary>Uniformly scale a model so its LARGEST bounds dimension equals the target in
        /// metres. Robust against FBXs that import at cm scale, m scale, or anything between —
        /// measured from the actual instance, never assumed from the file's unit metadata.</summary>
        private static void ScaleToMaxDimension(GameObject go, float targetMeters)
        {
            Bounds b = RendererBoundsOf(go);
            float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (maxDim < 0.0001f) return;
            go.transform.localScale *= targetMeters / maxDim;
        }

        /// <summary>Move a model so its combined renderer bounds sit bottom-centred on the target.</summary>
        private static void SeatByBounds(GameObject go, Vector3 targetBottomCenter, out Bounds bounds)
        {
            bounds = RendererBoundsOf(go);
            Vector3 delta = targetBottomCenter - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            go.transform.position += delta;
            bounds = RendererBoundsOf(go);
        }

        private static Bounds RendererBoundsOf(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        private static int AddCollidersUnder(Transform rootT, float minSize)
        {
            int added = 0;
            foreach (var r in rootT.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.GetComponent<Collider>() != null) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Vector3 s = r.bounds.size;
                if (Mathf.Max(s.x, Mathf.Max(s.y, s.z)) < minSize) continue;
                var mc = Undo.AddComponent<MeshCollider>(r.gameObject);
                mc.sharedMesh = mf.sharedMesh;
                added++;
            }
            return added;
        }

        /// <summary>Create URP/Lit materials from a model's texture folder and assign them to the
        /// instance's renderers, matching by MATERIAL NAME against texture file names. Handles both
        /// naming schemes present: '&lt;mat&gt;_d/_n' (creepy-house) and
        /// '&lt;mat&gt;_BaseColor/_Normal' + '&lt;mat&gt;_diff/_norm' (diplomat interior).</summary>
        private static int BuildMaterialsFor(GameObject modelInstance, string texFolder, string prefix)
        {
            if (!AssetDatabase.IsValidFolder(texFolder)) return 0;
            if (!AssetDatabase.IsValidFolder(HouseMatFolder))
                AssetDatabase.CreateFolder("Assets/_Project/Prefabs/house", "Materials");

            var texs = new List<Texture2D>();
            var texPaths = new List<string>();
            foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { texFolder }))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (t != null) { texs.Add(t); texPaths.Add(p); }
            }
            if (texs.Count == 0) return 0;

            var urpLit = Shader.Find("Universal Render Pipeline/Lit");
            var cache = new Dictionary<string, Material>();
            int built = 0;

            // READY-MADE pack materials beat anything we could generate: if the pack folder ships
            // (or the user extracted) .mat files with textures already assigned, seed the cache
            // with them so their slots RELINK instead of being rebuilt as blind fallbacks.
            // (The shipwreck pack is why: its texture files are timestamp-named, so name-matching
            // textures fails — but its extracted .mats are perfect.)
            foreach (var g in AssetDatabase.FindAssets("t:Material", new[] { texFolder }))
            {
                var pm = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g));
                if (pm == null) continue;
                bool textured = (pm.HasProperty("_BaseMap") && pm.GetTexture("_BaseMap") != null) ||
                                (pm.HasProperty("_MainTex") && pm.GetTexture("_MainTex") != null);
                if (!textured) continue;
                ConvertToUrpLit(pm, urpLit);   // no-op when already URP
                cache[NormalizeName(pm.name)] = pm;
            }

            foreach (var r in modelInstance.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    // A NULL slot renders magenta ("missing material") — the interior FBX shipped
                    // with dead externalObjects remaps that imported as null. Never leave one.
                    if (mats[i] == null)
                    {
                        mats[i] = GetOrBuildFallback(cache, urpLit, prefix, ref built);
                        changed = true;
                        continue;
                    }
                    string key = NormalizeName(mats[i].name);
                    if (string.IsNullOrEmpty(key)) key = "unnamed";
                    if (!cache.TryGetValue(key, out var replacement))
                    {
                        int baseIdx = FindTextureFor(texs, key, wantNormal: false);
                        int normIdx = FindTextureFor(texs, key, wantNormal: true);

                        // ALWAYS build a replacement — the embedded FBX material renders PINK in
                        // URP and the RP Converter can't touch it (read-only, inside the FBX).
                        // Textured when a texture matched, plain tinted URP/Lit otherwise.
                        string ap = $"{HouseMatFolder}/{prefix}_{key}.mat";
                        var m = AssetDatabase.LoadAssetAtPath<Material>(ap);   // re-run-safe: reuse
                        if (m == null)
                        {
                            m = new Material(urpLit) { name = key };
                            AssetDatabase.CreateAsset(m, ap);
                        }
                        else
                        {
                            m.shader = urpLit;
                        }

                        if (baseIdx >= 0) m.SetTexture("_BaseMap", texs[baseIdx]);
                        if (normIdx >= 0)
                        {
                            // (import type already fixed by NormalizeNormalMapTypes — reimporting
                            // HERE would rebuild the FBX instances we're iterating)
                            m.SetTexture("_BumpMap", texs[normIdx]);
                            m.EnableKeyword("_NORMALMAP");
                        }
                        m.SetFloat("_Smoothness", 0.15f);   // old wood/stone, not plastic

                        if (baseIdx < 0)
                        {
                            // Untextured fallback tints so nothing reads as raw grey plastic.
                            Color c = new Color(0.42f, 0.40f, 0.38f);            // dusty neutral
                            if (key.Contains("glass"))       c = new Color(0.55f, 0.62f, 0.68f);
                            else if (key.Contains("candle")) c = new Color(0.9f, 0.82f, 0.6f);
                            else if (key.Contains("moon"))   c = new Color(0.85f, 0.88f, 1f);
                            else if (key.Contains("bag"))    c = new Color(0.45f, 0.38f, 0.3f);
                            m.SetColor("_BaseColor", c);
                            // Candles/moons should faintly GLOW — free atmosphere for the interior.
                            if (key.Contains("candle") || key.Contains("moon"))
                            {
                                m.EnableKeyword("_EMISSION");
                                m.SetColor("_EmissionColor", c * (key.Contains("candle") ? 1.6f : 0.9f));
                            }
                            Debug.Log($"[SunsetSceneBuilder] No texture for material '{mats[i].name}' — " +
                                      "assigned a plain URP fallback (was pink).");
                        }

                        ApplySpecialLook(m, key);
                        EditorUtility.SetDirty(m);
                        built++;
                        cache[key] = m;
                        replacement = m;
                    }
                    if (replacement != null && mats[i] != replacement) { mats[i] = replacement; changed = true; }
                }
                if (changed)
                {
                    Undo.RecordObject(r, "Assign house materials");
                    r.sharedMaterials = mats;
                    EditorUtility.SetDirty(r);
                }
            }
            AssetDatabase.SaveAssets();
            return built;
        }

        private static string NormalizeName(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Per-material tweaks keyed off the name: foliage cards get alpha-cutout +
        /// double-sided (PS1-style plants are flat quads with transparent PNGs — opaque they'd be
        /// black rectangles), and the parish's 'emit' material (window panes) gets a warm glow.</summary>
        private static void ApplySpecialLook(Material m, string key)
        {
            if (key.Contains("atrans") || key.Contains("plant"))
            {
                m.SetFloat("_AlphaClip", 1f);
                m.SetFloat("_Cutoff", 0.5f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.SetFloat("_Cull", 0f);   // double-sided foliage cards
            }
            if (key.Contains("emit"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_BaseColor", new Color(1f, 0.8f, 0.5f));
                m.SetColor("_EmissionColor", new Color(1f, 0.72f, 0.38f) * 2f);   // candlelit windows
            }
        }

        // The diplomat-building pack's material names don't literally match its texture names:
        // 'TILE FLOOR' vs FLOOR_TILE_* (word order flipped), 'WALLS1' vs WALL1_* (stray s),
        // 'wood plabks' vs wood_planks_* (typo in the asset itself). Normalized aliases:
        private static readonly Dictionary<string, string> MatTextureAliases =
            new Dictionary<string, string>
            {
                { "tilefloor",  "floortile" },
                { "walls1",     "wall1" },
                { "woodplabks", "woodplanks" },
                // Parish (horrorVillageV1): stone-family + statue textures named differently.
                { "rock1",      "stone" },
                { "rock1trim",  "stonetrim" },
                { "rock1trim2", "stonetrim2" },
                { "cat2",       "catstatue" },
            };

        private static int FindTextureFor(List<Texture2D> texs, string matKey, bool wantNormal)
        {
            // Pass 0: the raw key. Pass 1: its alias (flipped/typo'd pack names). Pass 2: the key
            // with trailing digits stripped ("stonewalls2" → "stonewalls"), since packs often
            // number materials but not their shared textures.
            string alias = MatTextureAliases.TryGetValue(matKey, out var a) ? a : null;
            string keyNoDigits = matKey.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            for (int pass = 0; pass < 3; pass++)
            {
                string key = pass == 0 ? matKey : (pass == 1 ? alias : keyNoDigits);
                if (string.IsNullOrEmpty(key)) continue;
                for (int i = 0; i < texs.Count; i++)
                {
                    string tn = NormalizeName(texs[i].name);
                    bool matches = tn.Contains(key);
                    if (!matches) continue;
                    bool isNormal = tn.EndsWith(key + "n") || tn == key + "n" ||
                                    tn.Contains("normal") || tn.Contains("norm");
                    bool isBase = tn == key || tn == key + "d" || tn.EndsWith(key + "d") ||
                                  tn.Contains("basecolor") || tn.Contains("diff") || tn.Contains("albedo");
                    // never confuse occlusion/spec/roughness maps for a base map
                    bool isOther = tn == key + "o" || tn == key + "s" ||
                                   tn.Contains("rough") || tn.Contains("occlusion") || tn.Contains("metal");
                    if (wantNormal && isNormal) return i;
                    if (!wantNormal && isBase && !isNormal && !isOther) return i;
                }
            }

            // Lenient last resort for BASE maps: any texture containing the key that isn't
            // clearly a normal/roughness/occlusion map. Covers suffix-less pack names like
            // 'plants.png' for material 'plant1' or 'cat_statue' for 'cat2' (via its alias).
            if (!wantNormal)
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    string key = pass == 0 ? matKey : (pass == 1 ? alias : keyNoDigits);
                    if (string.IsNullOrEmpty(key)) continue;
                    for (int i = 0; i < texs.Count; i++)
                    {
                        string tn = NormalizeName(texs[i].name);
                        if (!tn.Contains(key)) continue;
                        if (tn.Contains("norm") || tn.Contains("rough") || tn.Contains("occlusion") ||
                            tn.Contains("metal") || tn.EndsWith(key + "n") || tn.EndsWith(key + "o") ||
                            tn.EndsWith(key + "s")) continue;
                        return i;
                    }
                }
            }
            return -1;
        }

        private static void EnsureNormalMapImport(string assetPath)
        {
            var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (ti != null && ti.textureType != TextureImporterType.NormalMap)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.SaveAndReimport();
            }
        }

        /// <summary>Set the Normal Map import type on every normal-looking texture in the folder
        /// (*_n, *_norm*, *Normal*). Runs BEFORE any instantiation: SaveAndReimport can trigger a
        /// pipeline refresh that rebuilds FBX scene instances mid-iteration.</summary>
        private static void NormalizeNormalMapTypes(string texFolder)
        {
            if (!AssetDatabase.IsValidFolder(texFolder)) return;
            foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { texFolder }))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                string raw = System.IO.Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                if (raw.EndsWith("_n") || raw.Contains("_norm") || raw.Contains("normal"))
                    EnsureNormalMapImport(p);
            }
        }

        /// <summary>The shared grey URP material used for NULL slots (missing-material magenta)
        /// and other unmatchable pieces. One asset per model prefix, created on demand.</summary>
        private static Material GetOrBuildFallback(Dictionary<string, Material> cache,
                                                   Shader urpLit, string prefix, ref int built)
        {
            if (cache.TryGetValue("__missing", out var missing) && missing != null) return missing;
            string mp = $"{HouseMatFolder}/{prefix}_missing.mat";
            missing = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (missing == null)
            {
                missing = new Material(urpLit) { name = $"{prefix}_missing" };
                missing.SetColor("_BaseColor", new Color(0.42f, 0.40f, 0.38f));
                missing.SetFloat("_Smoothness", 0.15f);
                AssetDatabase.CreateAsset(missing, mp);
                built++;
            }
            cache["__missing"] = missing;
            return missing;
        }

        // ───────────────────────────── 8. radio tower escape ─────────────────────────────

        private const string RadioFolder = "Assets/_Project/Prefabs/RadioTower";

        /// <summary>
        /// One click wires the whole radio-tower escape into the open scene:
        ///   • "RadioTowerEscape" object (NetworkObject + RadioTowerState) with the imported item
        ///     models auto-assigned (Battery / ElectricalTransformer / krotkofal FBX).
        ///   • RadioItemSpawner added beside NightValuableSpawner on the ItemSpawner object, with
        ///     the same 22 hiding spots copied over.
        ///   • On the escape tower (selected tower, else ScoutTower_East): a "Ladder" climb volume
        ///     + a "RepairPoint" volume on the top platform. Slide the Ladder box over the actual
        ///     ladder mesh afterwards.
        /// Re-run safe — every piece is skipped if it already exists.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/8. Setup Radio Tower Escape")]
        public static void SetupRadioTowerEscape()
        {
            // ── The shared state object ──
            var state = Object.FindAnyObjectByType<SunsetCurse.World.RadioTowerState>(FindObjectsInactive.Include);
            if (state == null)
            {
                var go = new GameObject("RadioTowerEscape");
                Undo.RegisterCreatedObjectUndo(go, "Create RadioTowerEscape");
                go.AddComponent<Unity.Netcode.NetworkObject>();
                state = go.AddComponent<SunsetCurse.World.RadioTowerState>();
            }

            // Auto-assign the imported item models + the escape tower reference.
            var so = new SerializedObject(state);
            AssignIfEmpty(so, "batteryPrefab",     RadioFolder + "/Battery.fbx");
            AssignIfEmpty(so, "transformerPrefab", RadioFolder + "/ElectricalTransformer.fbx");
            AssignIfEmpty(so, "krotkofalPrefab",   RadioFolder + "/krotkofal.fbx");

            // The selected object counts as "the tower" only if it's an actual building: name
            // contains Tower, has renderers, and isn't the RadioTowerEscape state object (whose
            // name also contains "Tower" — selecting it must not hijack the ladder/radio setup).
            GameObject tower = null;
            var sel = Selection.activeGameObject;
            if (sel != null && sel.name.Contains("Tower") &&
                sel.GetComponent<SunsetCurse.World.RadioTowerState>() == null &&
                sel.GetComponentInChildren<Renderer>() != null)
                tower = sel;
            if (tower == null) tower = GameObject.Find("ScoutTower_East");
            if (tower == null) tower = GameObject.Find("ScoutTower_West");
            if (tower != null && so.FindProperty("towerTransform").objectReferenceValue == null)
                so.FindProperty("towerTransform").objectReferenceValue = tower.transform;
            so.ApplyModifiedProperties();

            // ── The nightly item spawner, sharing the potion's hiding spots ──
            var potionSpawner = Object.FindAnyObjectByType<SunsetCurse.World.NightValuableSpawner>(FindObjectsInactive.Include);
            if (potionSpawner == null)
            {
                Debug.LogError("[SunsetSceneBuilder] No NightValuableSpawner (ItemSpawner) in the scene — " +
                               "open SampleScene first.");
                return;
            }
            var radioSpawner = potionSpawner.GetComponent<SunsetCurse.World.RadioItemSpawner>();
            if (radioSpawner == null)
            {
                radioSpawner = Undo.AddComponent<SunsetCurse.World.RadioItemSpawner>(potionSpawner.gameObject);
                var src = new SerializedObject(potionSpawner).FindProperty("spawnPoints");
                var dst = new SerializedObject(radioSpawner);
                var dstPoints = dst.FindProperty("spawnPoints");
                dstPoints.arraySize = src.arraySize;
                for (int i = 0; i < src.arraySize; i++)
                    dstPoints.GetArrayElementAtIndex(i).objectReferenceValue =
                        src.GetArrayElementAtIndex(i).objectReferenceValue;
                dst.ApplyModifiedProperties();
                Debug.Log($"[SunsetSceneBuilder] RadioItemSpawner added with {src.arraySize} hiding spots.");
            }

            // ── Ladder + repair point on the escape tower ──
            if (tower != null)
            {
                Bounds tb = RendererBounds(tower);
                if (tower.GetComponentInChildren<SunsetCurse.World.TowerLadder>() == null)
                {
                    var ladderGO = new GameObject("Ladder");
                    Undo.RegisterCreatedObjectUndo(ladderGO, "Create Tower Ladder");
                    ladderGO.transform.SetParent(tower.transform, false);
                    ladderGO.transform.rotation = tower.transform.rotation;
                    // Front face of the tower, full height. The user slides it onto the real ladder.
                    ladderGO.transform.position = new Vector3(tb.center.x, tb.min.y, tb.center.z)
                                                  + tower.transform.forward * (tb.extents.z + 0.3f);
                    var box = ladderGO.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    float h = Mathf.Max(4f, tb.size.y * 0.92f);
                    box.size = new Vector3(1.2f, h, 1.2f);
                    box.center = new Vector3(0f, h * 0.5f, 0f);
                    ladderGO.AddComponent<SunsetCurse.World.TowerLadder>();
                }
                if (tower.GetComponentInChildren<SunsetCurse.World.TowerRepairPoint>() == null)
                {
                    var repairGO = new GameObject("RepairPoint");
                    Undo.RegisterCreatedObjectUndo(repairGO, "Create Tower RepairPoint");
                    repairGO.transform.SetParent(tower.transform, false);
                    repairGO.transform.position = new Vector3(tb.center.x, tb.max.y - 2f, tb.center.z);
                    var box = repairGO.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = new Vector3(4f, 4f, 4f);
                    repairGO.AddComponent<SunsetCurse.World.TowerRepairPoint>();
                }

                // The physical RADIO UNIT (_Low.fbx) on the top platform — the thing players look
                // at and press the Attach key on. Re-run safe: skipped once one exists.
                if (tower.GetComponentInChildren<SunsetCurse.World.TowerRadioProp>() == null)
                {
                    var radioModel = AssetDatabase.LoadAssetAtPath<GameObject>(RadioFolder + "/_Low.fbx");
                    if (radioModel != null)
                    {
                        var radioGO = (GameObject)PrefabUtility.InstantiatePrefab(radioModel, tower.transform);
                        Undo.RegisterCreatedObjectUndo(radioGO, "Create Tower Radio Unit");
                        radioGO.name = "TowerRadioUnit";
                        // Park it on the top platform, slightly off-centre so it doesn't block the
                        // ladder exit. The user nudges it into its final spot.
                        radioGO.transform.position = new Vector3(tb.center.x, tb.max.y - 2f, tb.center.z)
                                                     + tower.transform.right * 0.8f;
                        if (radioGO.GetComponentInChildren<Collider>() == null)
                        {
                            foreach (var mf in radioGO.GetComponentsInChildren<MeshFilter>())
                                if (mf.GetComponent<Collider>() == null && mf.sharedMesh != null)
                                    radioGO.AddComponent<BoxCollider>();   // one box on the root is enough to aim at
                            if (radioGO.GetComponentInChildren<Collider>() == null)
                                radioGO.AddComponent<BoxCollider>();
                        }
                        radioGO.AddComponent<SunsetCurse.World.TowerRadioProp>();
                        Debug.Log("[SunsetSceneBuilder] Radio unit (_Low) placed on the tower platform — " +
                                  "nudge it into position, and run tool 5 on the RadioTower folder if it's black.", radioGO);
                    }
                    else
                    {
                        Debug.LogWarning($"[SunsetSceneBuilder] '_Low.fbx' not found in {RadioFolder} — " +
                                         "place your radio unit manually and add the TowerRadioProp component.");
                    }
                }
            }
            else
            {
                Debug.LogWarning("[SunsetSceneBuilder] No scout tower found — select your tower and re-run " +
                                 "this to add the Ladder + RepairPoint.");
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[SunsetSceneBuilder] Radio Tower Escape wired. NOW: (1) select the tower's 'Ladder' " +
                      "child and slide the green box over the actual ladder mesh — its blue arrow must " +
                      "point toward the platform you step onto at the top; (2) check 'RepairPoint' covers " +
                      "the top platform; (3) drop your helicopter clip into RadioTowerEscape ▸ Helicopter " +
                      "Clip when imported; (4) SAVE the scene.");
        }

        private static void AssignIfEmpty(SerializedObject so, string prop, string assetPath)
        {
            var p = so.FindProperty(prop);
            if (p.objectReferenceValue != null) return;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset != null) p.objectReferenceValue = asset;
            else Debug.LogWarning($"[SunsetSceneBuilder] Couldn't find '{assetPath}' for {prop} — assign it manually.");
        }

        // ───────────────────────────── 6. hinge snapper ─────────────────────────────

        /// <summary>
        /// Moves a door's HINGE onto the door mesh's nearest vertical edge — without moving the
        /// mesh — so the door pivots on its edge like a real door.
        ///
        /// WHY: Door.cs rotates the DoorHinge object. If you position the door by dragging the
        /// DoorMesh child (the natural thing to do — the hinge is invisible), the hinge stays
        /// behind, and opening sweeps the door in a huge arc through the wall. This puts the
        /// pivot back where it belongs, wherever the mesh ended up.
        /// </summary>
        [MenuItem("Tools/Sunset Curse/6. Snap Hinge To Door Edge (Select Door)")]
        public static void SnapHingeToDoorEdge()
        {
            // Accept the hinge, the mesh, or anything under either — find the Door component.
            var door = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<Door>()
                : null;
            if (door == null)
            {
                Debug.LogError("[SunsetSceneBuilder] Select your door (the DoorHinge or DoorMesh) first.");
                return;
            }

            Transform hinge = door.transform;
            var renderers = hinge.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogError("[SunsetSceneBuilder] This door has no mesh under its hinge.");
                return;
            }

            // Door bounds in the hinge's local frame (handles a rotated door correctly).
            Bounds b = LocalRendererBounds(renderers[0].transform.gameObject, hinge);
            foreach (var r in renderers) b.Encapsulate(LocalRendererBounds(r.gameObject, hinge));

            // The door's WIDTH axis is whichever horizontal direction the mesh spans more (packs
            // orient their meshes differently — assuming X put the hinge on the door's FACE for
            // the horror-pack door, so opening swept it through the wall). The hinge belongs on
            // the vertical edge at the end of the WIDTH, on the side nearest to where the user
            // left the hinge (preserves which side the door is hung on). Y at the mesh base.
            float sizeX = b.max.x - b.min.x;
            float sizeZ = b.max.z - b.min.z;
            Vector3 localEdge;
            if (sizeX >= sizeZ)
            {
                float edgeX = Mathf.Abs(b.min.x) <= Mathf.Abs(b.max.x) ? b.min.x : b.max.x;
                localEdge = new Vector3(edgeX, b.min.y, (b.min.z + b.max.z) * 0.5f);
            }
            else
            {
                float edgeZ = Mathf.Abs(b.min.z) <= Mathf.Abs(b.max.z) ? b.min.z : b.max.z;
                localEdge = new Vector3((b.min.x + b.max.x) * 0.5f, b.min.y, edgeZ);
            }
            Vector3 worldEdge = hinge.TransformPoint(localEdge);

            // Move the hinge to the edge, then shift the children back so nothing moves visually.
            Vector3 delta = worldEdge - hinge.position;
            Undo.RecordObject(hinge, "Snap Hinge To Door Edge");
            var children = hinge.Cast<Transform>().ToArray();
            foreach (var c in children) Undo.RecordObject(c, "Snap Hinge To Door Edge");

            hinge.position += delta;
            foreach (var c in children) c.position -= delta;

            EditorSceneManager.MarkSceneDirty(hinge.gameObject.scene);
            Debug.Log("[SunsetSceneBuilder] Hinge snapped onto the door's edge — it now pivots in " +
                      "place. Press Play and open it: if it swings INTO the house, flip 'Open Angle' " +
                      "on the Door component (90 ↔ -90).", door);
        }

        // ───────────────────────────── 5. pink material fixer ─────────────────────────────

        /// <summary>
        /// Converts every still-built-in material under the selected object(s) to URP/Lit.
        ///
        /// WHY: the Render Pipeline Converter only upgrades materials it can parse AND whose shader
        /// it recognises. The Flooded_Grounds towers reference BLD_Bridge1(+_LOD).mat — 2015-era
        /// binary assets on legacy shaders — so the converter skips them and they render magenta.
        /// Going through the Unity API here sidesteps the file format entirely.
        /// </summary>
        private const string BridgeMatPath    = "Assets/Flooded_Grounds/Content/Materials/BLD_Bridge1.mat";
        private const string BridgeLodMatPath = "Assets/Flooded_Grounds/Content/Materials/BLD_Bridge1_LOD.mat";

        [MenuItem("Tools/Sunset Curse/5. Fix Pink or Black Materials (Selected or NewBuildings)")]
        public static void FixPinkMaterials()
        {
            var urp = Shader.Find("Universal Render Pipeline/Lit");
            if (urp == null) { Debug.LogError("[SunsetSceneBuilder] URP/Lit shader not found — is URP installed?"); return; }

            // ── Path A: MATERIALS (or folders of materials) selected in the Project window. ──
            // This is how you fix a whole imported pack that reads BLACK: its materials are on the
            // Built-in Standard shader (no URP lighting → black). Select the pack's Materials folder
            // (or the .mat files) and run this. Black here is the same root cause as pink — a
            // non-URP shader — just a different symptom depending on the shader's passes.
            var selectedMats = Selection.GetFiltered<Material>(SelectionMode.DeepAssets);
            if (selectedMats != null && selectedMats.Length > 0)
            {
                int done = 0;
                foreach (var mat in selectedMats)
                    if (ConvertToUrpLit(mat, urp)) done++;
                AssetDatabase.SaveAssets();
                Debug.Log($"[SunsetSceneBuilder] Converted {done} selected material(s) to URP/Lit " +
                          $"({selectedMats.Length - done} were already URP). Albedo, normal, " +
                          "metallic-smoothness and emission maps were carried over.");
                return;
            }

            // ── Path B: GameObjects selected in the scene (or the NewBuildings group). ──
            // The tower materials, converted by direct path — even when the renderer slots lost
            // the reference (a MISSING material renders magenta too, exactly like a broken shader).
            var bridgeMat = AssetDatabase.LoadAssetAtPath<Material>(BridgeMatPath);
            var bridgeLod = AssetDatabase.LoadAssetAtPath<Material>(BridgeLodMatPath);
            int converted = 0;
            var seen = new System.Collections.Generic.HashSet<Material>();
            if (bridgeMat != null && seen.Add(bridgeMat) && ConvertToUrpLit(bridgeMat, urp)) converted++;
            if (bridgeLod != null && seen.Add(bridgeLod) && ConvertToUrpLit(bridgeLod, urp)) converted++;

            // The pack's custom shaders may use non-standard property names, in which case the
            // generic conversion can't carry the texture over — wire the known ones by hand.
            var bridgeAlbedo = AssetDatabase.LoadAssetAtPath<Texture>("Assets/Flooded_Grounds/Content/Textures/BLD_Bridge1_A.tif");
            var bridgeNormal = AssetDatabase.LoadAssetAtPath<Texture>("Assets/Flooded_Grounds/Content/Textures/BLD_Bridge1_N.tif");
            foreach (var m in new[] { bridgeMat, bridgeLod })
            {
                if (m == null || m.GetTexture("_BaseMap") != null || bridgeAlbedo == null) continue;
                m.SetTexture("_BaseMap", bridgeAlbedo);
                if (bridgeNormal != null) { m.SetTexture("_BumpMap", bridgeNormal); m.EnableKeyword("_NORMALMAP"); }
                EditorUtility.SetDirty(m);
                Debug.Log($"[SunsetSceneBuilder] '{m.name}': texture was lost in conversion — re-assigned BLD_Bridge1_A.", m);
            }

            // Work on the selection, or fall back to the NewBuildings group.
            GameObject[] roots = Selection.gameObjects;
            if (roots == null || roots.Length == 0)
            {
                var parent = GameObject.Find(BuildingsParentName);
                if (parent == null)
                {
                    Debug.LogError("[SunsetSceneBuilder] Select the pink object(s) in the Hierarchy first " +
                                   "(or run tool 1 so 'NewBuildings' exists).");
                    return;
                }
                roots = new[] { parent };
            }

            int repaired = 0;
            foreach (var root in roots)
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null)
                    {
                        // Empty slot. For the radio towers we KNOW the intended material (their
                        // 2015 binary prefab references the pack's bridge metal) — reattach it.
                        bool isTower = r.name.Contains("RadioTower");
                        Debug.LogWarning($"[SunsetSceneBuilder] '{Path(r.transform)}' slot {i}: material " +
                                         $"reference is MISSING{(isTower ? " → assigning bridge metal" : " (don't know what belongs here — tell Claude)")}.", r);
                        if (isTower && bridgeMat != null)
                        {
                            mats[i] = (r.name.Contains("_LOD1") || r.name.Contains("_LOD2")) && bridgeLod != null
                                ? bridgeLod : bridgeMat;
                            changed = true;
                            repaired++;
                        }
                        continue;
                    }

                    string shaderName = mat.shader != null ? mat.shader.name : "(null shader)";
                    Debug.Log($"[SunsetSceneBuilder] '{Path(r.transform)}' slot {i}: '{mat.name}' on '{shaderName}'.", r);
                    if (seen.Add(mat) && ConvertToUrpLit(mat, urp)) converted++;
                }
                if (changed)
                {
                    Undo.RecordObject(r, "Repair missing materials");
                    r.sharedMaterials = mats;
                    EditorUtility.SetDirty(r);
                }
            }

            AssetDatabase.SaveAssets();
            if (roots.Length > 0) EditorSceneManager.MarkSceneDirty(roots[0].scene);
            Debug.Log($"[SunsetSceneBuilder] Done — {converted} material(s) converted, {repaired} missing " +
                      "slot(s) repaired. Every renderer it touched is listed above — if something is " +
                      "still pink, copy those lines to Claude.");
        }

        /// <summary>Convert one material to URP/Lit, keeping its texture/tint/normal map.
        /// Returns true if a conversion actually happened (false = it was already fine).</summary>
        private static bool ConvertToUrpLit(Material mat, Shader urp)
        {
            // Anything already on a URP-family shader renders fine — leave it alone. The FX/
            // ProceduralWater check matters: it IS a URP shader despite the name, and this
            // converter once silently turned the river's water material into white URP/Lit on
            // every menu-21 run (the "water is a plain white plane" saga — session 22).
            string shaderName = mat.shader != null ? mat.shader.name : "";
            if (shaderName.StartsWith("Universal Render Pipeline") ||
                shaderName.StartsWith("Shader Graphs") ||
                shaderName.StartsWith("Skybox") ||
                shaderName == PwsShaderName)
                return false;

            // Save everything the old (Built-in Standard / legacy) material knew BEFORE the shader
            // swap wipes the mapping. Standard and URP/Lit happen to share several property names
            // (_MetallicGlossMap, _BumpMap, _EmissionMap, _EmissionColor), but _MainTex/_Color are
            // Standard-only and must be re-homed to _BaseMap/_BaseColor.
            Texture albedo   = mat.HasProperty("_MainTex")         ? mat.GetTexture("_MainTex")         : null;
            Texture normal   = mat.HasProperty("_BumpMap")         ? mat.GetTexture("_BumpMap")         : null;
            Texture metalMap = mat.HasProperty("_MetallicGlossMap")? mat.GetTexture("_MetallicGlossMap"): null;
            Texture emitMap  = mat.HasProperty("_EmissionMap")     ? mat.GetTexture("_EmissionMap")     : null;
            Color tint       = mat.HasProperty("_Color")           ? mat.GetColor("_Color")             : Color.white;
            Color emitColor  = mat.HasProperty("_EmissionColor")   ? mat.GetColor("_EmissionColor")     : Color.black;
            float metallic   = mat.HasProperty("_Metallic")        ? mat.GetFloat("_Metallic")          : 0f;
            // Standard stores smoothness as _Glossiness (albedo-metallic workflow); keep it.
            float smooth     = mat.HasProperty("_Glossiness")      ? mat.GetFloat("_Glossiness")        : 0.35f;
            bool emitOn      = mat.IsKeywordEnabled("_EMISSION") ||
                               (emitMap != null) || emitColor.maxColorComponent > 0.01f;
            bool cutout = shaderName.Contains("Cutout") ||
                          (mat.HasProperty("_Cutoff") && mat.GetFloat("_Cutoff") > 0.01f);

            mat.shader = urp;

            if (albedo != null) { mat.SetTexture("_BaseMap", albedo); mat.SetTexture("_MainTex", albedo); }
            mat.SetColor("_BaseColor", tint);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (metalMap != null)
            {
                // With a metallic-smoothness MAP, URP reads smoothness from the map's alpha —
                // drive it with a full multiplier so the texture's own values come through.
                mat.SetTexture("_MetallicGlossMap", metalMap);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetFloat("_Smoothness", 1f);
            }
            else
            {
                mat.SetFloat("_Smoothness", smooth);
            }
            mat.SetFloat("_Metallic", metallic);
            if (emitOn)
            {
                mat.EnableKeyword("_EMISSION");
                if (emitMap != null) mat.SetTexture("_EmissionMap", emitMap);
                mat.SetColor("_EmissionColor", emitColor.maxColorComponent > 0.01f ? emitColor : Color.white);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            if (cutout)   // lattice/railing textures need alpha clipping or they render as solid quads
            {
                mat.SetFloat("_AlphaClip", 1f);
                mat.EnableKeyword("_ALPHATEST_ON");
            }

            EditorUtility.SetDirty(mat);
            Debug.Log($"[SunsetSceneBuilder] Converted '{mat.name}' (was '{shaderName}') → URP/Lit.", mat);
            return true;
        }

        private static string Path(Transform t) =>
            t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

        private static void MakeCreepClipsLoop()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(CreepClipsFbxPath);
            if (importer == null) return;

            // Copying defaultClipAnimations materialises one editable clip per FBX take.
            var clips = importer.clipAnimations is { Length: > 0 }
                ? importer.clipAnimations
                : importer.defaultClipAnimations;
            bool changed = false;
            foreach (var c in clips)
            {
                bool shouldLoop = c.name.Contains("Idle") || c.name.Contains("Walk") || c.name.Contains("Crouch");
                if (shouldLoop && !c.loopTime) { c.loopTime = true; changed = true; }
            }
            if (changed || importer.clipAnimations == null || importer.clipAnimations.Length == 0)
            {
                importer.clipAnimations = clips;
                importer.SaveAndReimport();
            }
        }

        private static AnimatorController BuildCreepController()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(CreepControllerPath);
            if (existing != null) return existing;

            string dir = System.IO.Path.GetDirectoryName(CreepControllerPath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(dir))
            {
                // AssetDatabase can only create one folder level at a time.
                string[] parts = dir.Split('/');
                string cur = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = cur + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                    cur = next;
                }
            }

            AnimationClip Find(string suffix) =>
                AssetDatabase.LoadAllAssetsAtPath(CreepClipsFbxPath)
                    .OfType<AnimationClip>()
                    .FirstOrDefault(c => c.name.EndsWith(suffix));

            var idle = Find("Idle1_Action");
            var walk = Find("Walk1_Action");
            var run  = Find("Walk2_Action");
            if (idle == null || walk == null)
            {
                Debug.LogError("[SunsetSceneBuilder] Couldn't find Idle1/Walk1 clips inside Creep_mesh.fbx.");
                return null;
            }
            if (run == null) run = walk;

            var controller = AnimatorController.CreateAnimatorControllerAtPath(CreepControllerPath);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

            // 1D blend tree: thresholds match the REAL in-scene speeds (stalk 5 / run 7 as tuned
            // on the scene instance) so the walk cycle kicks in exactly when she starts moving.
            var tree = new BlendTree
            {
                name = "Locomotion",
                blendParameter = "Speed",
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(idle, 0f);
            tree.AddChild(walk, 5f);
            tree.AddChild(run, 8f);

            // Play the top-speed clip a bit faster so her legs keep up during the sprint.
            var children = tree.children;
            children[2].timeScale = 1.3f;
            tree.children = children;

            var state = controller.layers[0].stateMachine.AddState("Locomotion");
            state.motion = tree;
            AssetDatabase.SaveAssets();
            return controller;
        }
    }
}
