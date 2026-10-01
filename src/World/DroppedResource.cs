using System.Collections.Generic;
using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.UI;

namespace SunsetCurse.World
{
    /// <summary>
    /// A world pickup created when a player drags a resource OUT of their inventory. Visible to
    /// every peer (Inventory broadcasts the spawn via ClientRpc) and pickable by ANY player
    /// (server is authoritative — first interactor wins, the loser sees nothing happen).
    ///
    /// Built procedurally: small floating glowing sphere tinted by resource type. No prefab to
    /// import. The Inventory singleton owns the lifecycle:
    ///   • spawn — when Inventory.OnDropSpawned fires (every peer)
    ///   • despawn — when Inventory.OnDropDespawned fires (every peer)
    /// </summary>
    public class DroppedResource : Interactable
    {
        // Per-peer registry so Inventory can look up the local GameObject by drop id.
        private static readonly Dictionary<int, DroppedResource> registry = new Dictionary<int, DroppedResource>();

        public int Id { get; private set; }
        public ResourceType Type { get; private set; }
        public int Amount { get; private set; }

        /// <summary>Optional per-type drop models (set from InventoryUI's Inspector slots on Start).
        /// If a type's prefab is assigned, dropped stacks use that model; otherwise they fall back
        /// to the procedural glowing sphere.</summary>
        public static GameObject WoodPrefab;
        public static GameObject StonePrefab;
        public static GameObject HerbPrefab;

        /// <summary>Assigned drop models are auto-scaled so their largest dimension is this many
        /// metres — keeps oversized imported models a sensible pickup size. Set from InventoryUI.
        /// 0 = don't rescale (use the prefab's own size).</summary>
        public static float ModelTargetSize = 0.5f;

        private float bobTime;
        private Vector3 baseLocalPos;

        // Colours match the inventory UI palette.
        private static readonly Color WoodColor  = new Color(0.55f, 0.34f, 0.18f);
        private static readonly Color StoneColor = new Color(0.55f, 0.55f, 0.58f);
        private static readonly Color HerbColor  = new Color(0.30f, 0.78f, 0.35f);

        public override string Verb => $"pick up {Amount} {ShortName(Type)}";

        public override void Interact(GameObject interactor)
        {
            // Don't decide locally whether we succeed — the server does. Just request it.
            // If two players grab simultaneously, only one's request will find this id alive.
            Inventory.RequestPickupDropped(Id);
        }

        private void Update()
        {
            // Subtle bob + spin so it's easy to spot on the ground.
            bobTime += Time.deltaTime;
            transform.localPosition = baseLocalPos + Vector3.up * Mathf.Sin(bobTime * 2f) * 0.08f;
            transform.Rotate(0f, 50f * Time.deltaTime, 0f, Space.World);
        }

        // ───────────────────────────── Lifecycle hooks ─────────────────────────────

        private static GameObject hostRoot;   // parent for all dropped objects (Hierarchy tidiness)

        private static void EnsureRoot()
        {
            if (hostRoot != null) return;
            hostRoot = new GameObject("DroppedResources");
        }

        /// <summary>Subscribe Inventory's broadcast events once per scene. Called from
        /// <see cref="InventoryUI"/>'s Start (only place we know exists once per scene). Safe to
        /// call multiple times — idempotent.</summary>
        public static void EnsureSubscribed()
        {
            Inventory.OnDropSpawned -= HandleSpawned;
            Inventory.OnDropSpawned += HandleSpawned;
            Inventory.OnDropDespawned -= HandleDespawned;
            Inventory.OnDropDespawned += HandleDespawned;
            // Late-join replay: spawn any drops that already exist (either now, or as soon as the
            // Inventory NetworkObject comes up — whichever order things initialise in).
            Inventory.OnReady -= ReplayExisting;
            Inventory.OnReady += ReplayExisting;
            ReplayExisting();
        }

        private static void ReplayExisting() => Inventory.ReplayDrops(HandleSpawned);

        private static void HandleSpawned(int id, ResourceType type, int amount, Vector3 pos)
        {
            // Should be impossible but be defensive — if a stale GO is still around, kill it.
            if (registry.TryGetValue(id, out var stale) && stale != null) Object.Destroy(stale.gameObject);
            registry[id] = SpawnLocal(id, type, amount, pos);
        }

        private static void HandleDespawned(int id)
        {
            if (registry.TryGetValue(id, out var go) && go != null) Object.Destroy(go.gameObject);
            registry.Remove(id);
        }

        // ───────────────────────────── Procedural visual ─────────────────────────────

        private static DroppedResource SpawnLocal(int id, ResourceType type, int amount, Vector3 pos)
        {
            EnsureRoot();

            GameObject prefab = PrefabFor(type);
            GameObject go;
            if (prefab != null)
            {
                // Assigned model: instantiate it, right-size it, ensure a trigger collider.
                go = Object.Instantiate(prefab, pos + Vector3.up * 0.2f, Quaternion.identity, hostRoot.transform);
                go.name = $"Dropped_{type}_x{amount}_id{id}";
                ModelArtifacts.Strip(go);   // kill any embedded camera that would hijack the view
                NormalizeSize(go);          // shrink oversized imported models to pickup size
                EnsureTriggerCollider(go);
            }
            else
            {
                // Procedural fallback: a small glowing sphere tinted by type.
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Dropped_{type}_x{amount}_id{id}";
                go.transform.SetParent(hostRoot.transform, false);
                go.transform.position = pos + Vector3.up * 0.45f;
                go.transform.localScale = Vector3.one * 0.55f;

                var col = go.GetComponent<SphereCollider>();
                col.isTrigger = true;
                col.radius = 0.8f;

                var mr = go.GetComponent<MeshRenderer>();
                Color tint = TintFor(type);
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    mat.SetColor("_BaseColor", tint);
                    mat.SetColor("_Color", tint);                       // legacy fallback
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", tint * 1.8f);
                    mr.material = mat;
                }
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            var dropped = go.GetComponent<DroppedResource>() ?? go.AddComponent<DroppedResource>();
            dropped.Id = id;
            dropped.Type = type;
            dropped.Amount = amount;
            dropped.baseLocalPos = go.transform.localPosition;
            return dropped;
        }

        private static GameObject PrefabFor(ResourceType t) => t switch
        {
            ResourceType.Wood       => WoodPrefab,
            ResourceType.Stone      => StonePrefab,
            ResourceType.RitualHerb => HerbPrefab,
            _ => null
        };

        /// <summary>Uniformly scale an assigned drop model so its largest renderer-bounds dimension
        /// equals <see cref="ModelTargetSize"/> metres — fixes imported models that come in huge.</summary>
        private static void NormalizeSize(GameObject go)
        {
            if (ModelTargetSize <= 0f) return;
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return;
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (maxDim > 0.001f) go.transform.localScale *= ModelTargetSize / maxDim;
        }

        /// <summary>Guarantee a trigger collider on an assigned model so PlayerInteractor's look-ray
        /// (QueryTriggerInteraction.Collide) finds it and the player walks through it.</summary>
        private static void EnsureTriggerCollider(GameObject go)
        {
            var col = go.GetComponentInChildren<Collider>();
            if (col == null)
            {
                var box = go.AddComponent<BoxCollider>();
                var r = go.GetComponentInChildren<Renderer>();
                if (r != null)
                {
                    box.center = go.transform.InverseTransformPoint(r.bounds.center);
                    box.size = r.bounds.size;
                }
                col = box;
            }
            col.isTrigger = true;
        }

        private static Color TintFor(ResourceType t) => t switch
        {
            ResourceType.Wood       => WoodColor,
            ResourceType.Stone      => StoneColor,
            ResourceType.RitualHerb => HerbColor,
            _ => Color.white
        };

        private static string ShortName(ResourceType t) => t switch
        {
            ResourceType.Wood       => "Wood",
            ResourceType.Stone      => "Stone",
            ResourceType.RitualHerb => "Ritual Herb",
            _ => "Resource"
        };
    }
}
