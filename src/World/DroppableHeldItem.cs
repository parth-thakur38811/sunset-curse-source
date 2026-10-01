using System.Collections.Generic;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// World instance of a dropped held item (Lord's Syrup or Respawn Potion). Spawned on every
    /// peer when Inventory broadcasts <see cref="Inventory.OnHeldDropSpawned"/>. Visual model
    /// comes from the user-imported prefabs assigned in <see cref="UI.InventoryUI"/>'s Inspector.
    ///
    /// On interact (E): calls Inventory.RequestPickupHeldDrop(id). Server validates uniqueness
    /// (first picker wins), broadcasts despawn to every peer, and hands the item to the picker.
    /// </summary>
    public class DroppableHeldItem : Interactable
    {
        private static readonly Dictionary<int, DroppableHeldItem> registry = new Dictionary<int, DroppableHeldItem>();
        private static GameObject hostRoot;

        // Prefabs are set by InventoryUI on Start from its Inspector fields, so we can spawn the
        // correct visual without needing a per-item subclass / scene singleton.
        public static GameObject LordsSyrupPrefab;
        public static GameObject RespawnPotionPrefab;

        public int Id { get; private set; }
        public HeldItemType Type { get; private set; }

        public override string Verb => Type == HeldItemType.LordsSyrup
            ? "pick up Lord's Syrup"
            : "pick up Respawn Potion";

        public override void Interact(GameObject interactor)
        {
            Inventory.RequestPickupHeldDrop(Id);
        }

        // ───────────── Subscribe / spawn / despawn ─────────────

        public static void EnsureSubscribed()
        {
            Inventory.OnHeldDropSpawned   -= HandleSpawned;
            Inventory.OnHeldDropSpawned   += HandleSpawned;
            Inventory.OnHeldDropDespawned -= HandleDespawned;
            Inventory.OnHeldDropDespawned += HandleDespawned;
            // Late-join replay for held items already dropped in the world.
            Inventory.OnReady -= ReplayExisting;
            Inventory.OnReady += ReplayExisting;
            ReplayExisting();
        }

        private static void ReplayExisting() => Inventory.ReplayHeldDrops(HandleSpawned);

        private static void HandleSpawned(int id, HeldItemType type, Vector3 pos)
        {
            if (registry.TryGetValue(id, out var stale) && stale != null) Object.Destroy(stale.gameObject);
            registry[id] = SpawnLocal(id, type, pos);
        }

        private static void HandleDespawned(int id)
        {
            if (registry.TryGetValue(id, out var go) && go != null) Object.Destroy(go.gameObject);
            registry.Remove(id);
        }

        private static DroppableHeldItem SpawnLocal(int id, HeldItemType type, Vector3 pos)
        {
            if (hostRoot == null) hostRoot = new GameObject("DroppedHeldItems");

            GameObject prefab = type == HeldItemType.LordsSyrup ? LordsSyrupPrefab : RespawnPotionPrefab;
            GameObject go;
            if (prefab != null)
            {
                go = Object.Instantiate(prefab, pos + Vector3.up * 0.15f, Quaternion.identity, hostRoot.transform);
                ModelArtifacts.Strip(go);   // kill any embedded camera that would hijack the view
            }
            else
            {
                // Fallback if the prefab wasn't assigned — a coloured sphere so the drop is still pick-able.
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(hostRoot.transform, false);
                go.transform.position = pos + Vector3.up * 0.3f;
                go.transform.localScale = Vector3.one * 0.4f;
                Color tint = type == HeldItemType.LordsSyrup
                    ? new Color(0.65f, 0.30f, 0.85f)
                    : new Color(0.40f, 0.85f, 0.55f);
                var mr = go.GetComponent<MeshRenderer>();
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    mat.SetColor("_BaseColor", tint);
                    mat.SetColor("_Color", tint);
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", tint * 1.5f);
                    mr.material = mat;
                }
            }
            go.name = $"Dropped_{type}_id{id}";

            // Make sure there's a Collider sized to the visible mesh, set as a trigger so the
            // player walks through but the look-raycast still hits.
            var col = go.GetComponent<Collider>();
            if (col == null) col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;

            // Disable shadows for tiny pickups (less GPU cost).
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var dh = go.AddComponent<DroppableHeldItem>();
            dh.Id = id;
            dh.Type = type;
            return dh;
        }
    }
}
