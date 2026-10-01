using System.Collections;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// The ritual's water bucket — ONE exists in the world. It spawns at a fixed spot inside
    /// TheLoothouse_Shed; a player grabs it (E), wades ≥2m into the river and holds X to fill it
    /// (see <see cref="SunsetCurse.Player.RiverBucketFiller"/>), then carries the FILLED bucket
    /// to the ritual altar — the ritual only progresses while it's present
    /// (see <see cref="RitualSiteNet"/>).
    ///
    /// Same host-picks / everyone-mirrors pattern as <see cref="DawnPotionSpawner"/>:
    /// netPresent + netGeneration replicate, every peer builds its own local visual, pickup is
    /// server-validated (first grab wins) and granted to the picker via targeted ClientRpc.
    /// If the carrier dies or disconnects, the server respawns the bucket at the shed (empty)
    /// so the objective can never soft-lock.
    ///
    /// SETUP: Tools ▸ Sunset Curse ▸ 24 creates the "RiverBucket" GameObject with a
    /// NetworkObject + this script (+ RiverBucketFiller + RiverAmbience) — then just drop your
    /// bucket model into Bucket Prefab and save the scene.
    /// </summary>
    public class RiverWaterBucket : NetworkBehaviour
    {
        public static RiverWaterBucket Instance { get; private set; }

        [Header("Bucket spawn (inside TheLoothouse_Shed)")]
        [Tooltip("Fixed WORLD position the bucket appears at. Default is the shed interior.")]
        [SerializeField] private Vector3 spawnPosition = new Vector3(-67f, -2f, 33f);
        [Tooltip("Optional: drag an empty here to mark the spot instead of typing coordinates. " +
                 "Overrides Spawn Position when assigned.")]
        [SerializeField] private Transform spawnPointOverride;
        [Tooltip("Raycast down from just above the spawn spot so the bucket sits ON the shed " +
                 "floor even if the coordinates are slightly inside/below it.")]
        [SerializeField] private bool snapToGround = true;

        [Header("Bucket look — drop your prefab here")]
        [Tooltip("Your bucket model. Needs nothing on it — a trigger collider and the pickup " +
                 "component are added in code. Falls back to a procedural tin bucket if empty.")]
        [SerializeField] private GameObject bucketPrefab;
        [Tooltip("The model is auto-scaled so its largest side is this many metres (matches the " +
                 "other pickups' size normalisation). 0 = keep the prefab's own size.")]
        [SerializeField] private float bucketTargetSize = 0.45f;

        [Tooltip("How long (seconds) the server waits after losing track of the carrier before " +
                 "respawning the bucket at the shed — covers the pickup-grant replication window.")]
        [SerializeField] private float respawnGraceSeconds = 4f;

        // Networked: bump the generation on any change so every peer rebuilds its local visual.
        // present==true means the bucket is sitting at the shed right now.
        private readonly NetworkVariable<bool> netPresent    = new NetworkVariable<bool>(true);
        private readonly NetworkVariable<int>  netGeneration = new NetworkVariable<int>(0);

        private GameObject currentBucket;
        private Material bucketMaterial;
        private float missingSince = -1f;   // when the server first saw "not present + no carrier"

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

        private IEnumerator Start()
        {
            // Offline fallback: if NGO never spawns us (no NetworkManager at all), the
            // NetworkVariables act as plain fields and no OnValueChanged fires — build the
            // visual by hand. In normal play (SP hosts offline NGO) OnNetworkSpawn covers it.
            yield return new WaitForSeconds(2f);
            if (!IsSpawned && currentBucket == null && netPresent.Value)
                currentBucket = CreateBucket(ResolveSpawnPos());
        }

        public override void OnNetworkSpawn()
        {
            if (Instance != this) return;   // disabled duplicate (see Awake) - stay inert
            netGeneration.OnValueChanged += HandleGenerationChanged;
            if (netPresent.Value) HandleGenerationChanged(0, netGeneration.Value);   // initial + late join
        }

        public override void OnNetworkDespawn()
        {
            netGeneration.OnValueChanged -= HandleGenerationChanged;
        }

        // ── Server: respawn watchdog ─────────────────────────────────────────────────────────
        private void Update()
        {
            // SERVER ONLY past this point (a never-spawned offline game counts as its own server).
            if (IsSpawned && !IsServer) return;
            if (netPresent.Value) { missingSince = -1f; return; }

            // Someone is carrying it (alive OR downed — downed players keep their items and can
            // be revived). Only a real death / disconnect makes the carrier vanish.
            var all = PlayerInventory.All;
            for (int i = 0; i < all.Count; i++)
            {
                var inv = all[i];
                if (inv != null && inv.HasBucket && inv.IsAlive) { missingSince = -1f; return; }
            }

            // No carrier in sight. Wait out the grace window (a fresh pickup's owner-write flag
            // takes a moment to replicate back to the host) before respawning at the shed.
            if (missingSince < 0f) { missingSince = Time.time; return; }
            if (Time.time - missingSince < respawnGraceSeconds) return;

            missingSince = -1f;
            netPresent.Value = true;
            netGeneration.Value += 1;
            if (!IsSpawned) HandleGenerationChanged(0, netGeneration.Value);   // offline: no callback
            Debug.Log("[RiverWaterBucket] Carrier lost — bucket respawned at the shed.");
        }

        // ── Every peer: mirror the replicated state into a local visual ─────────────────────
        private void HandleGenerationChanged(int prev, int next)
        {
            if (currentBucket != null) { Destroy(currentBucket); currentBucket = null; }
            if (!netPresent.Value) return;
            currentBucket = CreateBucket(ResolveSpawnPos());
        }

        private Vector3 ResolveSpawnPos()
        {
            Vector3 pos = spawnPointOverride != null ? spawnPointOverride.position : spawnPosition;
            if (snapToGround &&
                Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 30f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                pos = hit.point + Vector3.up * 0.02f;
            return pos;
        }

        private GameObject CreateBucket(Vector3 pos)
        {
            GameObject go;
            if (bucketPrefab != null)
            {
                go = Instantiate(bucketPrefab, pos, Quaternion.identity, transform);
                ModelArtifacts.Strip(go);   // kill any embedded camera/light that would hijack the view
                NormalizeAndSeat(go, pos);
            }
            else
            {
                // Procedural tin bucket so the feature works before any art is imported. Its
                // primitive collider is kept — EnsureTriggerCollider flips it to a trigger.
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "RiverBucket";
                go.transform.SetParent(transform, true);
                go.transform.localScale = new Vector3(0.32f, 0.18f, 0.32f);
                go.transform.position = pos + Vector3.up * 0.18f;
                var r = go.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = GetBucketMaterial();
            }

            EnsureTriggerCollider(go);
            (go.GetComponent<RiverBucketPickup>() ?? go.AddComponent<RiverBucketPickup>())
                .Configure(netGeneration.Value);
            return go;
        }

        /// <summary>Fit the model to bucketTargetSize and plant its lowest point on the floor —
        /// same measure-by-renderer-bounds approach the other item spawners use.</summary>
        private void NormalizeAndSeat(GameObject go, Vector3 pos)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return;

            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

            if (bucketTargetSize > 0f)
            {
                float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);
                if (maxDim > 0.001f)
                {
                    go.transform.localScale *= bucketTargetSize / maxDim;
                    b = rends[0].bounds;   // re-measure at the new scale
                    for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                }
            }

            go.transform.position += new Vector3(pos.x - b.center.x, pos.y - b.min.y, pos.z - b.center.z);
        }

        /// <summary>Pickups must NEVER physically trap the player (session-11 lesson) — every
        /// collider becomes a trigger; if the model shipped without one, a bounds-fit trigger box
        /// is added so PlayerInteractor's ray (QueryTriggerInteraction.Collide) can find it.</summary>
        private static void EnsureTriggerCollider(GameObject go)
        {
            var cols = go.GetComponentsInChildren<Collider>();
            foreach (var c in cols) c.isTrigger = true;
            if (cols.Length > 0) return;

            var rends = go.GetComponentsInChildren<Renderer>();
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            if (rends.Length > 0)
            {
                Bounds b = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                box.center = go.transform.InverseTransformPoint(b.center);
                Vector3 ls = go.transform.lossyScale;
                box.size = new Vector3(Mathf.Max(0.3f, b.size.x / Mathf.Max(0.0001f, Mathf.Abs(ls.x))),
                                       Mathf.Max(0.3f, b.size.y / Mathf.Max(0.0001f, Mathf.Abs(ls.y))),
                                       Mathf.Max(0.3f, b.size.z / Mathf.Max(0.0001f, Mathf.Abs(ls.z))));
            }
        }

        // ── Pickup routing (server-validated, first grab wins) ──────────────────────────────
        public void RequestPickup(int generation)
        {
            if (!IsSpawned)  { PickupServerSide(generation, ulong.MaxValue); return; }
            if (IsServer)    PickupServerSide(generation, NetworkManager.LocalClientId);
            else             PickupServerRpc(generation);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PickupServerRpc(int generation, ServerRpcParams p = default)
            => PickupServerSide(generation, p.Receive.SenderClientId);

        private void PickupServerSide(int generation, ulong picker)
        {
            if (!netPresent.Value || generation != netGeneration.Value) return;   // already taken
            netPresent.Value = false;
            netGeneration.Value += 1;                                             // everyone despawns
            if (!IsSpawned) HandleGenerationChanged(0, netGeneration.Value);      // offline: no callback

            if (picker == ulong.MaxValue || (IsServer && IsSpawned && picker == NetworkManager.LocalClientId))
            {
                GrantLocal();
            }
            else
            {
                GrantClientRpc(new ClientRpcParams
                { Send = new ClientRpcSendParams { TargetClientIds = new[] { picker } } });
            }
        }

        [ClientRpc]
        private void GrantClientRpc(ClientRpcParams _) => GrantLocal();

        private static void GrantLocal()
        {
            if (PlayerInventory.Local == null) return;
            PlayerInventory.Local.SetBucket(BucketState.Empty);
            SunsetCurse.UI.ScreenMessage.Show(
                "Picked up the bucket — wade deep into the river and hold X to fill it.", 4f);
        }

        private Material GetBucketMaterial()
        {
            if (bucketMaterial != null) return bucketMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            bucketMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            var tin = new Color(0.62f, 0.66f, 0.72f);
            if (bucketMaterial.HasProperty("_BaseColor")) bucketMaterial.SetColor("_BaseColor", tin);
            else bucketMaterial.color = tin;
            if (bucketMaterial.HasProperty("_Smoothness")) bucketMaterial.SetFloat("_Smoothness", 0.6f);
            return bucketMaterial;
        }
    }
}
