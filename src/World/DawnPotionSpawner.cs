using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Spawns ONE dawn potion at a fixed spot every dawn — the "Teleport Potion" (single-player) /
    /// "Revival Potion" (multiplayer). Same host-picks / everyone-mirrors pattern as
    /// <see cref="NightValuableSpawner"/>, but on <c>OnDayStart</c> and at a single spawn point.
    ///
    /// Pickup is server-validated (first grab wins); the picker's client gets one potion added to
    /// their <see cref="PlayerInventory"/>. Uncollected potions clear at the next nightfall.
    ///
    /// SETUP:
    ///   1. Mark ONE empty GameObject in the scene as the spawn spot (the "potion altar").
    ///   2. Put this script on a GameObject that has a NetworkObject (a fresh "DawnPotionSpawner"
    ///      object, or reuse ItemSpawner). Drag the empty into Spawn Point.
    ///   3. Assign your potion model to Potion Prefab (a glowing sphere is used if left empty).
    /// </summary>
    public class DawnPotionSpawner : NetworkBehaviour
    {
        public static DawnPotionSpawner Instance { get; private set; }

        [Header("Where the potion appears each dawn")]
        [SerializeField] private Transform spawnPoint;

        [Header("The potion")]
        [Tooltip("Your potion model prefab. If empty, a glowing violet sphere is used.")]
        [SerializeField] private GameObject potionPrefab;
        [SerializeField] private Color potionColor = new Color(0.5f, 0.35f, 1f);
        [Tooltip("Clear an uncollected potion when night falls.")]
        [SerializeField] private bool despawnAtNight = true;

        // Networked: bump the generation on any change so every peer rebuilds its local visual.
        // present==true means a potion is live at the spawn point right now.
        private readonly NetworkVariable<bool> netPresent   = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<int>  netGeneration = new NetworkVariable<int>(0);

        private GameObject currentItem;
        private Material gemMaterial;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            netGeneration.OnValueChanged += HandleGenerationChanged;
            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnDayStart   += HandleDayServer;
                GameClock.Instance.OnNightStart += HandleNightServer;
                // If we spawn INTO a day already in progress, place today's potion now.
                if (GameClock.Instance.CurrentPhase == GameClock.Phase.Day) HandleDayServer(GameClock.Instance.CurrentDay);
            }
            if (netPresent.Value) HandleGenerationChanged(0, netGeneration.Value);   // late-joiner sync
        }

        public override void OnNetworkDespawn()
        {
            netGeneration.OnValueChanged -= HandleGenerationChanged;
            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnDayStart   -= HandleDayServer;
                GameClock.Instance.OnNightStart -= HandleNightServer;
            }
        }

        // ── Server ──
        private void HandleDayServer(int day)
        {
            if (spawnPoint == null) { Debug.LogWarning("[DawnPotionSpawner] No spawn point set.", this); return; }
            netPresent.Value = true;
            netGeneration.Value += 1;
            Debug.Log($"[DawnPotionSpawner] Dawn of day {day}: potion placed (gen {netGeneration.Value}).");
        }

        private void HandleNightServer(int day)
        {
            if (!despawnAtNight || !netPresent.Value) return;
            netPresent.Value = false;
            netGeneration.Value += 1;
        }

        // ── Every peer ──
        private void HandleGenerationChanged(int prev, int next)
        {
            if (currentItem != null) { Destroy(currentItem); currentItem = null; }
            if (!netPresent.Value || spawnPoint == null) return;
            currentItem = CreatePotion(spawnPoint.position);
        }

        private GameObject CreatePotion(Vector3 pos)
        {
            GameObject go;
            if (potionPrefab != null)
            {
                go = Instantiate(potionPrefab, pos, Quaternion.identity, transform);
                ModelArtifacts.Strip(go);   // kill any embedded camera that would hijack the view
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "DawnPotion";
                go.transform.SetParent(transform, true);
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * 0.4f;
                var r = go.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = GetGemMaterial();
            }
            if (go.GetComponent<Collider>() == null) go.AddComponent<SphereCollider>();
            (go.GetComponent<TeleportPotionPickup>() ?? go.AddComponent<TeleportPotionPickup>())
                .Configure(netGeneration.Value);
            return go;
        }

        // ── Pickup routing (server-validated, first grab wins) ──
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

        /// <summary>Add the potion to the local player + confirm with an on-screen message so the
        /// pickup is never silent. Mode decides the wording (teleport vs revival).</summary>
        private static void GrantLocal()
        {
            if (PlayerInventory.Local == null) return;
            PlayerInventory.Local.AddTeleportPotion(1);
            bool mp = SunsetCurse.Core.DifficultyPreference.Mode == SunsetCurse.Core.GameMode.Multiplayer;
            SunsetCurse.UI.ScreenMessage.Show(
                mp ? "Picked up a Revival Potion — hold G while downed to revive yourself."
                   : "Picked up a Teleport Potion — press G to blink far away.", 3.5f);
        }

        private Material GetGemMaterial()
        {
            if (gemMaterial != null) return gemMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            gemMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            if (gemMaterial.HasProperty("_BaseColor")) gemMaterial.SetColor("_BaseColor", potionColor);
            else gemMaterial.color = potionColor;
            if (gemMaterial.HasProperty("_EmissionColor"))
            {
                gemMaterial.EnableKeyword("_EMISSION");
                gemMaterial.SetColor("_EmissionColor", potionColor * 0.7f);
            }
            return gemMaterial;
        }
    }
}
