using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Spawns ONE shared ritual potion inside the compound at the start of each night, at the
    /// SAME spot for every peer. The host picks the spawn point + bumps a "generation" counter
    /// every night; clients react to the generation change and locally instantiate the visual.
    ///
    /// Pickup is server-validated: when ANY player presses E on the potion, their local Valuable
    /// fires a ServerRpc up to the spawner. The host checks the request's generation matches the
    /// current one (so two-near-simultaneous grabs don't double-count), bumps the generation
    /// again (clients despawn their local copy), and increments the shared EscapeTracker. Reach
    /// 7 / 7 and every peer fires OnEscapeReady at the same moment.
    ///
    /// SETUP:
    ///   1. Place empty GameObjects as hiding spots inside the compound.
    ///   2. Put this script on "ItemSpawner" + drag the empties into Spawn Points.
    ///   3. Add a **NetworkObject** component to ItemSpawner (auto-spawns on scene load).
    ///   4. Make sure EscapeTracker + GameClock are also in the scene, each with their own
    ///      NetworkObject.
    /// </summary>
    public class NightValuableSpawner : NetworkBehaviour
    {
        public static NightValuableSpawner Instance { get; private set; }

        /// <summary>Tonight's spot index (-1 = none). RadioItemSpawner reads this to avoid
        /// hiding its part at the same spot as the potion.</summary>
        public int CurrentSpawnIndex => netSpawnIndex.Value;

        [Header("Hiding spots inside the compound")]
        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();
        [Tooltip("Never hide the item at the same spot two nights running.")]
        [SerializeField] private bool avoidRepeat = true;

        [Header("The item")]
        [Tooltip("Your potion prefab. If empty, a glowing gold cube is used.")]
        [SerializeField] private GameObject valuablePrefab;
        [SerializeField] private int worth = 1;
        [SerializeField] private Color valuableColor = new Color(1f, 0.82f, 0.2f);

        [Header("Timing")]
        [Tooltip("Remove the potion at dawn if it wasn't collected.")]
        [SerializeField] private bool despawnAtDawn = true;

        // ───────────── Networked state ─────────────
        // Position MUST be replicated FIRST (declaration order = NGO dispatch order), so the
        // generation callback reads the correct index when it fires.
        private readonly NetworkVariable<int> netSpawnIndex = new NetworkVariable<int>(-1);
        // Generation bumps on EVERY change (spawn or despawn) so OnValueChanged always fires,
        // even if the same index is rolled twice. -1 in spawnIndex = "no item active".
        private readonly NetworkVariable<int> netGeneration = new NetworkVariable<int>(0);

        // Local-only state
        private GameObject currentItem;
        private int localGeneration;
        private int lastIndex = -1;     // server-only memory for avoidRepeat
        private Material gemMaterial;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // A second copy of this manager is in the scene. Destroy(this) would rip a
                // NetworkBehaviour off a NetworkObject, which Netcode doesn't support (it shifts
                // the behaviour indices, so RPCs can land on the wrong component). Disable this
                // copy instead and shout, so the duplicate gets deleted from the scene.
                Debug.LogError($"[{GetType().Name}] Duplicate in the scene - only one is allowed. " +
                               "This copy is disabled; delete it.", this);
                enabled = false;
                return;
            }
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();   // NGO's own NetworkBehaviour cleanup must still run
        }

        public override void OnNetworkSpawn()
        {
            if (Instance != this) return;   // disabled duplicate (see Awake) - stay inert
            netGeneration.OnValueChanged += HandleGenerationChanged;

            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart += HandleNightServer;
                GameClock.Instance.OnDayStart   += HandleDayServer;
            }

            // Late-joiner sync — if there's already a potion live, spawn it locally now.
            if (netGeneration.Value != 0 && netSpawnIndex.Value >= 0)
                HandleGenerationChanged(0, netGeneration.Value);
        }

        public override void OnNetworkDespawn()
        {
            netGeneration.OnValueChanged -= HandleGenerationChanged;
            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart -= HandleNightServer;
                GameClock.Instance.OnDayStart   -= HandleDayServer;
            }
        }

        // ───────────── Server: night/day handlers ─────────────

        private void HandleNightServer(int day)
        {
            if (spawnPoints == null || spawnPoints.Count == 0)
            {
                Debug.LogWarning("[NightValuableSpawner] No spawn points configured.", this);
                return;
            }
            int index = UnityEngine.Random.Range(0, spawnPoints.Count);
            if (avoidRepeat && spawnPoints.Count > 1)
            {
                int safety = 0;
                while (index == lastIndex && safety++ < 16)
                    index = UnityEngine.Random.Range(0, spawnPoints.Count);
            }
            lastIndex = index;

            netSpawnIndex.Value = index;       // write FIRST so the kick callback reads the right pos
            netGeneration.Value += 1;          // bump → every peer fires HandleGenerationChanged
            Debug.Log($"[NightValuableSpawner] Night {day}: shared potion at index {index} (gen {netGeneration.Value}).");
        }

        private void HandleDayServer(int day)
        {
            if (!despawnAtDawn) return;
            if (netSpawnIndex.Value < 0) return;   // already consumed during the night
            netSpawnIndex.Value = -1;
            netGeneration.Value += 1;              // bump → every peer despawns local copy
        }

        // ───────────── Every peer: spawn / despawn local copy ─────────────

        private void HandleGenerationChanged(int prev, int next)
        {
            // Clear whatever's currently shown.
            if (currentItem != null) { Destroy(currentItem); currentItem = null; }
            localGeneration = next;

            int index = netSpawnIndex.Value;
            if (index < 0 || spawnPoints == null || index >= spawnPoints.Count) return;
            Transform spot = spawnPoints[index];
            if (spot == null) return;

            currentItem = CreateValuable(spot.position, next);
        }

        private GameObject CreateValuable(Vector3 position, int generation)
        {
            GameObject go;
            if (valuablePrefab != null)
            {
                go = Instantiate(valuablePrefab, position, Quaternion.identity, transform);
                ModelArtifacts.Strip(go);   // kill any embedded camera that would hijack the view
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Valuable";
                go.transform.SetParent(transform, true);
                go.transform.position = position;
                go.transform.localScale = Vector3.one * 0.4f;
                go.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
                var r = go.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = GetGemMaterial();
            }

            if (go.GetComponent<Collider>() == null) go.AddComponent<BoxCollider>();
            var v = go.GetComponent<Valuable>() ?? go.AddComponent<Valuable>();
            v.Configure(worth, generation);
            return go;
        }

        // ───────────── Pickup routing ─────────────

        /// <summary>Called by Valuable.Interact (locally on the picker). Server validates
        /// (generation must match) and atomically claims the pickup.</summary>
        public void RequestPickup(int generation)
        {
            if (!IsSpawned)      { PickupServerSide(generation, ulong.MaxValue); return; }
            if (IsServer)        PickupServerSide(generation, NetworkManager.LocalClientId);
            else                 PickupServerRpc(generation);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PickupServerRpc(int generation, ServerRpcParams p = default)
            => PickupServerSide(generation, p.Receive.SenderClientId);

        private void PickupServerSide(int generation, ulong pickerClientId)
        {
            if (generation != netGeneration.Value) return;          // someone already grabbed it
            netSpawnIndex.Value = -1;
            netGeneration.Value += 1;                               // tells all clients to despawn
            if (EscapeTracker.Instance != null) EscapeTracker.Instance.Collect(worth);

            // Valuable scent flare: the curse notices its prize being carried off. Every monster
            // gets the picker's position as a forced target — decoying the stalker across the map
            // no longer makes the potion grab free — AND it opens the compound to the stalker
            // (UPDATE #5): once a valuable is taken she may enter the watcher's territory and chase.
            Vector3? pickerPos = ResolvePlayerPosition(pickerClientId);
            if (pickerPos.HasValue) SunsetCurse.AI.MonsterAlerts.ReportValuableTaken(pickerPos.Value);
        }

        /// <summary>Position of a connected client's player object; falls back to the local
        /// player for the offline/SP path (pickerClientId == ulong.MaxValue).</summary>
        private Vector3? ResolvePlayerPosition(ulong clientId)
        {
            if (clientId == ulong.MaxValue || NetworkManager.Singleton == null)
            {
                var local = Core.PlayerInventory.Local;
                return local != null ? local.transform.position : (Vector3?)null;
            }
            if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var nc) &&
                nc.PlayerObject != null)
                return nc.PlayerObject.transform.position;
            return null;
        }

        // ───────────── Gem fallback material ─────────────

        private Material GetGemMaterial()
        {
            if (gemMaterial != null) return gemMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            gemMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            if (gemMaterial.HasProperty("_BaseColor")) gemMaterial.SetColor("_BaseColor", valuableColor);
            else gemMaterial.color = valuableColor;
            if (gemMaterial.HasProperty("_EmissionColor"))
            {
                gemMaterial.EnableKeyword("_EMISSION");
                gemMaterial.SetColor("_EmissionColor", valuableColor * 0.6f);
            }
            return gemMaterial;
        }
    }
}
