using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SunsetCurse.Core
{
    /// <summary>The three forest resources players gather during daytime.</summary>
    public enum ResourceType { Wood, Stone, RitualHerb }

    /// <summary>Crafted held items that can be dropped to the world and picked up by anyone.</summary>
    public enum HeldItemType { LordsSyrup, RespawnPotion }

    // ── NetworkList element structs for world drops ──────────────────────────────────────────
    // Backed by NetworkLists (not one-shot ClientRpcs) so a LATE JOINER receives every existing
    // drop automatically and can spawn it locally. Enums are stored as int for NetworkList safety.

    /// <summary>A dropped resource pile in the world.</summary>
    public struct ResourceDrop : INetworkSerializable, System.IEquatable<ResourceDrop>
    {
        public int id; public int typeInt; public int amount; public Vector3 pos;
        public ResourceType Type => (ResourceType)typeInt;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref id); s.SerializeValue(ref typeInt); s.SerializeValue(ref amount); s.SerializeValue(ref pos); }
        public bool Equals(ResourceDrop o) => id == o.id;
        public override bool Equals(object o) => o is ResourceDrop d && Equals(d);
        public override int GetHashCode() => id;
    }

    /// <summary>A dropped held item (Lord's Syrup / Respawn Potion) in the world.</summary>
    public struct HeldDrop : INetworkSerializable, System.IEquatable<HeldDrop>
    {
        public int id; public int typeInt; public Vector3 pos;
        public HeldItemType Type => (HeldItemType)typeInt;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref id); s.SerializeValue(ref typeInt); s.SerializeValue(ref pos); }
        public bool Equals(HeldDrop o) => id == o.id;
        public override bool Equals(object o) => o is HeldDrop d && Equals(d);
        public override int GetHashCode() => id;
    }

    /// <summary>A thrown stone distractor in the world.</summary>
    public struct DistractorDrop : INetworkSerializable, System.IEquatable<DistractorDrop>
    {
        public int id; public Vector3 pos;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        { s.SerializeValue(ref id); s.SerializeValue(ref pos); }
        public bool Equals(DistractorDrop o) => id == o.id;
        public override bool Equals(object o) => o is DistractorDrop d && Equals(d);
        public override int GetHashCode() => id;
    }

    /// <summary>
    /// SHARED world state for crafting — everything that has to be the same on every peer:
    ///   • Whether the crafting table has been BUILT in the world (and where).
    ///   • A "Lord's Syrup was used" broadcast so every peer's MonsterAI stuns in sync.
    ///
    /// Per-player resources (wood/stone/herb counts) and per-player items (torch, syrup) live on
    /// <see cref="PlayerInventory"/> — one instance per PlayerCapsule, owner-authoritative.
    ///
    /// SETUP:
    ///   1. In SampleScene, empty GameObject "Inventory".
    ///   2. Add NetworkObject + this Inventory script.
    /// </summary>
    public class Inventory : NetworkBehaviour
    {
        public static Inventory Instance { get; private set; }

        // Position must come BEFORE the bool flag — NGO dispatches OnValueChanged in field-
        // declaration order. By the time the "built" callback fires on a client, the position
        // is already current.
        private readonly NetworkVariable<Vector3> netCraftingTablePos = new NetworkVariable<Vector3>(Vector3.zero);
        private readonly NetworkVariable<bool>    netHasCraftingTable = new NetworkVariable<bool>(false);

        // Bumps every time someone uses syrup → OnValueChanged drives the global monster stun.
        // A bool wouldn't re-fire on repeat uses; an int counter always does.
        private readonly NetworkVariable<int>     netLordsSyrupKick   = new NetworkVariable<int>(0);

        // SHARED WORLD SEED — host writes a stable seed at game start that every spawner uses
        // when seeding their local RNG. Without this each peer rolls a different scatter and you
        // see different trees / rocks / berries on every machine. WorldSeed is read by:
        //   • ResourceSpawner (trees, stones, herbs)
        //   • BerrySpawner    (berry clusters)
        // Each spawner XORs this with the day number so daily respawns still differ from Day 1.
        private readonly NetworkVariable<int>     netWorldSeed        = new NetworkVariable<int>(0);
        public static int WorldSeed => Instance != null ? Instance.netWorldSeed.Value : 0;

        // CONSUMED RESOURCE NODES — every tree/rock/herb has a deterministic ID (hash of its
        // position + type + day). When anyone picks one up, the server appends the ID here;
        // NetworkList auto-syncs to every peer (including late joiners) so each peer can despawn
        // the matching local node. Append-only — never removed within a session.
        private readonly NetworkList<int>         netConsumedNodes    = new NetworkList<int>();

        /// <summary>Has this node ID already been consumed by SOMEONE on the team? Used by newly-
        /// spawned ResourceNodes to self-despawn if they were chopped before the late joiner
        /// arrived (the despawn broadcast already fired before they existed).</summary>
        public static bool IsNodeConsumed(int nodeId)
        {
            if (Instance == null) return false;
            var list = Instance.netConsumedNodes;
            for (int i = 0; i < list.Count; i++)
                if (list[i] == nodeId) return true;
            return false;
        }

        /// <summary>Deterministic ID for a world resource node. Same input on every peer = same ID,
        /// because we round to 0.1m precision then mix with type + day. Day = 0 for scene-tagged
        /// nodes (permanent); day = current day for respawning spawner nodes (so day-2 spawns at
        /// the same position as a chopped day-1 tree get a different ID and aren't pre-consumed).</summary>
        public static int ComputeNodeId(Vector3 pos, ResourceType type, int day)
        {
            // All primes fit inside int (max 2,147,483,647) so the multiplications stay int and
            // `unchecked` lets them wrap around on overflow without the compiler complaining.
            unchecked
            {
                int x = Mathf.RoundToInt(pos.x * 10f);
                int y = Mathf.RoundToInt(pos.y * 10f);
                int z = Mathf.RoundToInt(pos.z * 10f);
                int h = (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
                h ^= (int)type * 1376312589;   // prime that fits in int
                h ^= day * 1234567;
                return h;
            }
        }

        // Monster investigate broadcast (Stone Distractor). Position written FIRST so the kick
        // callback reads the up-to-date target.
        private readonly NetworkVariable<Vector3> netInvestigatePos    = new NetworkVariable<Vector3>(Vector3.zero);
        private readonly NetworkVariable<float>   netInvestigateSeconds = new NetworkVariable<float>(15f);
        private readonly NetworkVariable<int>     netInvestigateKick   = new NetworkVariable<int>(0);

        // Same pattern for Torch flashes — counter so repeat flashes all fire OnValueChanged.
        // Paired with a Vector3 we (ab)use to carry the distraction DURATION in seconds on .x
        // (avoids needing a second NetworkList<float>).
        private readonly NetworkVariable<int>     netDistractKick      = new NetworkVariable<int>(0);
        private readonly NetworkVariable<float>   netDistractDuration  = new NetworkVariable<float>(10f);

        // Torch FLASH visual broadcast — fires every time anyone presses T, regardless of whether
        // they hit the monster. Each peer locally spawns a brief yellow point light at flashPos
        // so teammates SEE the flash burst even from across the map.
        // flashPos must be set BEFORE netFlashKick increments (NGO replicates in field-declaration
        // order, so the position is up-to-date when the kick callback fires).
        private readonly NetworkVariable<Vector3> netLastFlashPos       = new NetworkVariable<Vector3>(Vector3.zero);
        private readonly NetworkVariable<int>     netFlashKick          = new NetworkVariable<int>(0);

        // NETWORKED drop registries — NetworkLists so LATE JOINERS auto-receive every existing drop
        // (the old one-shot ClientRpc broadcasts never reached anyone who joined after the drop).
        // Adds/removes fire OnListChanged on every peer → the static OnXSpawned/OnXDespawned events
        // → the world objects spawn/despawn locally. Server-only id counters keep ids unique.
        private readonly NetworkList<ResourceDrop>   netDrops       = new NetworkList<ResourceDrop>();
        private readonly NetworkList<DistractorDrop> netDistractors = new NetworkList<DistractorDrop>();
        private readonly NetworkList<HeldDrop>       netHeldDrops   = new NetworkList<HeldDrop>();
        private int nextDropId = 1, nextDistractorId = 1, nextHeldDropId = 1;

        // Offline (no-NGO) fallbacks — only used when !IsSpawned (pure Play-in-SampleScene with no
        // host). Normal play always runs a host, so these stay empty.
        private readonly Dictionary<int, DropData> serverDrops = new Dictionary<int, DropData>();
        private struct DropData { public ResourceType type; public int amount; public Vector3 pos; }
        private readonly Dictionary<int, Vector3> serverDistractors = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, HeldDropData> serverHeldDrops = new Dictionary<int, HeldDropData>();
        private struct HeldDropData { public HeldItemType type; public Vector3 pos; }

        // ───────────── Public read API ─────────────

        public static bool    HasCraftingTable => Instance != null && Instance.netHasCraftingTable.Value;
        public static Vector3 CraftingTablePos => Instance != null ? Instance.netCraftingTablePos.Value : Vector3.zero;

        // ───────────── Events ─────────────

        /// <summary>Fires on every peer when the crafting table is built (spawn the local prefab).</summary>
        public static event Action OnCraftingTableBuilt;
        /// <summary>Fires on every peer when anyone uses Lord's Syrup (each MonsterAI stuns locally).</summary>
        public static event Action OnLordsSyrupUsed;
        /// <summary>Fires on every peer when anyone successfully flashes their torch — every
        /// MonsterAI distracts locally for the broadcast duration (seconds).</summary>
        public static event Action<float> OnMonsterDistracted;
        /// <summary>Fires on every peer the moment ANY player presses their torch (regardless of
        /// whether they hit the monster). Carries the WORLD position of the flash so peers can
        /// spawn a brief yellow light there. <see cref="World.TorchFlashFX"/> handles the visual.</summary>
        public static event Action<Vector3> OnTorchFlashed;
        /// <summary>Fires on every peer when the crafting table is removed (dawn cleanup).</summary>
        public static event Action OnCraftingTableDespawned;
        /// <summary>Fires on every peer when a stone distractor lands. (id, world pos)</summary>
        public static event Action<int, Vector3> OnDistractorSpawned;
        /// <summary>Fires on every peer when a stone distractor is picked up. (id)</summary>
        public static event Action<int> OnDistractorDespawned;
        /// <summary>Fires on every peer when the monster should investigate a position.</summary>
        public static event Action<Vector3, float> OnMonsterInvestigate;
        /// <summary>Fires on every peer when a held item drops to the world. (id, type, pos)</summary>
        public static event Action<int, HeldItemType, Vector3> OnHeldDropSpawned;
        /// <summary>Fires on every peer when a held drop is picked up. (id)</summary>
        public static event Action<int> OnHeldDropDespawned;
        /// <summary>Fires on every peer when a world drop appears. (id, type, amount, position).</summary>
        public static event Action<int, ResourceType, int, Vector3> OnDropSpawned;
        /// <summary>Fires on every peer when a world drop is consumed (anyone picked it up).</summary>
        public static event Action<int> OnDropDespawned;
        /// <summary>Fires (locally, per peer) once this Inventory has spawned and its synced drop
        /// lists are populated. Drop-object subscribers use it to replay existing drops for a LATE
        /// JOINER, regardless of whether they subscribed before or after Inventory came up.</summary>
        public static event Action OnReady;

        // ───────────── Public write API ─────────────

        /// <summary>Mark the crafting table as built at <paramref name="worldPos"/>. The caller is
        /// expected to have already deducted the resources from their own PlayerInventory — this
        /// is just the shared-world part. Idempotent: if a second caller races in, it no-ops.</summary>
        public static void RequestBuildCraftingTable(Vector3 worldPos)
        {
            if (Instance == null) return;
            Instance.BuildTableRoute(worldPos);
        }

        /// <summary>Tell every peer to stun their local monster (Lord's Syrup effect).</summary>
        public static void TriggerLordsSyrupStun()
        {
            if (Instance == null) return;
            Instance.SyrupStunRoute();
        }

        /// <summary>Tell every peer to distract their local monster for <paramref name="duration"/>
        /// seconds (Torch flash effect).</summary>
        public static void TriggerMonsterDistraction(float duration)
        {
            if (Instance == null) return;
            Instance.DistractRoute(duration);
        }

        /// <summary>Tell every peer to spawn a brief yellow flash light at <paramref name="worldPos"/>
        /// (so teammates SEE the flash). Fires regardless of whether the monster was hit.</summary>
        public static void TriggerTorchFlashVisual(Vector3 worldPos)
        {
            if (Instance == null) return;
            Instance.FlashRoute(worldPos);
        }

        /// <summary>Player ejected resources from their inventory — drop a world pickup all
        /// peers can see + grab. The caller (PlayerInventory) is expected to have already
        /// deducted the amount from their own inventory. </summary>
        public static void RequestDropResource(ResourceType type, int amount, Vector3 worldPos)
        {
            if (Instance == null || amount <= 0) return;
            Instance.DropRoute(type, amount, worldPos);
        }

        /// <summary>Player wants to pick up a world drop. Server validates (id still alive),
        /// removes it, broadcasts despawn to every peer, and grants the picker the resource.</summary>
        public static void RequestPickupDropped(int dropId)
        {
            if (Instance == null) return;
            Instance.PickupRoute(dropId);
        }

        // ───────────── Stone Distractor world tracking ─────────────

        /// <summary>Player threw a stone distractor — spawn a world object on every peer + tell
        /// the monster to investigate that spot for 15s.</summary>
        public static void RequestSpawnDistractor(Vector3 worldPos)
        {
            if (Instance == null) return;
            Instance.SpawnDistractorRoute(worldPos);
        }

        /// <summary>Someone picked up a stone distractor — server removes it, broadcasts despawn,
        /// and hands the distractor to the picker's inventory.</summary>
        public static void RequestPickupDistractor(int distractorId)
        {
            if (Instance == null) return;
            Instance.PickupDistractorRoute(distractorId);
        }

        /// <summary>Tell every peer's local monster to investigate <paramref name="worldPos"/>
        /// for <paramref name="duration"/> seconds.</summary>
        public static void TriggerMonsterInvestigate(Vector3 worldPos, float duration)
        {
            if (Instance == null) return;
            Instance.InvestigateRoute(worldPos, duration);
        }

        // ───────────── Held-item world drops (syrup, potion) ─────────────

        /// <summary>Player ejected a held craft (syrup/potion) — spawn it visibly to all peers.
        /// Caller (PlayerInventory) already removed it from their own inventory.</summary>
        public static void RequestSpawnHeldDrop(HeldItemType type, Vector3 worldPos)
        {
            if (Instance == null) return;
            Instance.SpawnHeldDropRoute(type, worldPos);
        }

        /// <summary>Someone picked up a held world drop — server validates, broadcasts despawn,
        /// gives the right item to the picker.</summary>
        public static void RequestPickupHeldDrop(int id)
        {
            if (Instance == null) return;
            Instance.PickupHeldDropRoute(id);
        }

        // ───────────── Lifecycle ─────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();   // NGO's own NetworkBehaviour cleanup must still run
        }

        public override void OnNetworkSpawn()
        {
            netHasCraftingTable.OnValueChanged += OnTableBuiltChanged;
            netLordsSyrupKick.OnValueChanged   += OnSyrupKick;
            netDistractKick.OnValueChanged     += OnDistractKick;
            netFlashKick.OnValueChanged        += OnFlashKick;
            netInvestigateKick.OnValueChanged  += OnInvestigateKick;
            netConsumedNodes.OnListChanged     += OnConsumedNodesChanged;
            netDrops.OnListChanged             += OnDropsChanged;
            netDistractors.OnListChanged       += OnDistractorsChanged;
            netHeldDrops.OnListChanged         += OnHeldDropsChanged;
            netBandageTakenDay.OnValueChanged  += OnBandageTakenChanged;
            // Late-join sync: walk the current consumed list NOW and despawn any matching local
            // nodes that may have already spawned for us before NGO replicated the list.
            for (int i = 0; i < netConsumedNodes.Count; i++)
                SunsetCurse.World.ResourceNode.DespawnLocalById(netConsumedNodes[i]);

            // Server handles the daily reset of the SHARED crafting-table state. Owner-level
            // resets (per-player torch) happen in PlayerInventory.
            if (IsServer && GameClock.Instance != null)
                GameClock.Instance.OnDayStart += OnDawnResetTable;

            // Server seeds the shared world RNG once, on first spawn. Use Random.Range here
            // (host's local Random) — it just needs to be different per game session.
            if (IsServer && netWorldSeed.Value == 0)
                netWorldSeed.Value = UnityEngine.Random.Range(1, int.MaxValue);

            // Our synced lists are populated by now — let drop subscribers replay existing drops
            // (covers a late joiner whose InventoryUI subscribed before this Inventory spawned).
            OnReady?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            netHasCraftingTable.OnValueChanged -= OnTableBuiltChanged;
            netLordsSyrupKick.OnValueChanged   -= OnSyrupKick;
            netDistractKick.OnValueChanged     -= OnDistractKick;
            netFlashKick.OnValueChanged        -= OnFlashKick;
            netInvestigateKick.OnValueChanged  -= OnInvestigateKick;
            netConsumedNodes.OnListChanged     -= OnConsumedNodesChanged;
            netDrops.OnListChanged             -= OnDropsChanged;
            netDistractors.OnListChanged       -= OnDistractorsChanged;
            netHeldDrops.OnListChanged         -= OnHeldDropsChanged;
            netBandageTakenDay.OnValueChanged  -= OnBandageTakenChanged;
            if (IsServer && GameClock.Instance != null)
                GameClock.Instance.OnDayStart -= OnDawnResetTable;
        }

        // ── NetworkList → static event bridges (fire on EVERY peer, incl. late joiners) ──
        private void OnDropsChanged(NetworkListEvent<ResourceDrop> e)
        {
            if (e.Type == NetworkListEvent<ResourceDrop>.EventType.Add ||
                e.Type == NetworkListEvent<ResourceDrop>.EventType.Insert)
                OnDropSpawned?.Invoke(e.Value.id, e.Value.Type, e.Value.amount, e.Value.pos);
            else if (e.Type == NetworkListEvent<ResourceDrop>.EventType.RemoveAt ||
                     e.Type == NetworkListEvent<ResourceDrop>.EventType.Remove)
                OnDropDespawned?.Invoke(e.Value.id);
        }

        private void OnDistractorsChanged(NetworkListEvent<DistractorDrop> e)
        {
            if (e.Type == NetworkListEvent<DistractorDrop>.EventType.Add ||
                e.Type == NetworkListEvent<DistractorDrop>.EventType.Insert)
                OnDistractorSpawned?.Invoke(e.Value.id, e.Value.pos);
            else if (e.Type == NetworkListEvent<DistractorDrop>.EventType.RemoveAt ||
                     e.Type == NetworkListEvent<DistractorDrop>.EventType.Remove)
                OnDistractorDespawned?.Invoke(e.Value.id);
        }

        private void OnHeldDropsChanged(NetworkListEvent<HeldDrop> e)
        {
            if (e.Type == NetworkListEvent<HeldDrop>.EventType.Add ||
                e.Type == NetworkListEvent<HeldDrop>.EventType.Insert)
                OnHeldDropSpawned?.Invoke(e.Value.id, e.Value.Type, e.Value.pos);
            else if (e.Type == NetworkListEvent<HeldDrop>.EventType.RemoveAt ||
                     e.Type == NetworkListEvent<HeldDrop>.EventType.Remove)
                OnHeldDropDespawned?.Invoke(e.Value.id);
        }

        // ── Late-join replay: spawn every EXISTING drop locally when a world-object subscriber
        // first hooks up (called from each drop class's EnsureSubscribed). Fires the LOCAL handler
        // only (not the global event), so it doesn't disturb peers that already have the objects.
        public static void ReplayDrops(Action<int, ResourceType, int, Vector3> localSpawn)
        {
            if (Instance == null || localSpawn == null) return;
            var list = Instance.netDrops;
            for (int i = 0; i < list.Count; i++)
                localSpawn(list[i].id, list[i].Type, list[i].amount, list[i].pos);
        }

        public static void ReplayDistractors(Action<int, Vector3> localSpawn)
        {
            if (Instance == null || localSpawn == null) return;
            var list = Instance.netDistractors;
            for (int i = 0; i < list.Count; i++)
                localSpawn(list[i].id, list[i].pos);
        }

        public static void ReplayHeldDrops(Action<int, HeldItemType, Vector3> localSpawn)
        {
            if (Instance == null || localSpawn == null) return;
            var list = Instance.netHeldDrops;
            for (int i = 0; i < list.Count; i++)
                localSpawn(list[i].id, list[i].Type, list[i].pos);
        }

        private void OnConsumedNodesChanged(NetworkListEvent<int> e)
        {
            if (e.Type == NetworkListEvent<int>.EventType.Add ||
                e.Type == NetworkListEvent<int>.EventType.Insert ||
                e.Type == NetworkListEvent<int>.EventType.Value)
            {
                SunsetCurse.World.ResourceNode.DespawnLocalById(e.Value);
            }
        }

        /// <summary>Called by ResourceNode.Interact (locally on the picker). Server checks if
        /// this node-id was already consumed (race / late RPC) — if not, appends to the synced
        /// list (every peer despawns their local copy via OnListChanged) and grants the picker
        /// their +amount via targeted ClientRpc.</summary>
        public static void RequestConsumeNode(int nodeId, ResourceType type, int amount)
        {
            if (Instance == null || amount <= 0) return;
            Instance.ConsumeNodeRoute(nodeId, type, amount);
        }

        private void ConsumeNodeRoute(int nodeId, ResourceType type, int amount)
        {
            if (!IsSpawned)
            {
                // SP / pre-NGO fallback: just add it locally + credit local inventory.
                ConsumeNodeLocal(nodeId);
                if (PlayerInventory.Local != null) PlayerInventory.Local.Add(type, amount);
                return;
            }
            if (IsServer) ConsumeNodeServer(nodeId, type, amount, NetworkManager.Singleton.LocalClientId);
            else          ConsumeNodeServerRpc(nodeId, type, amount);
        }

        [ServerRpc(RequireOwnership = false)]
        private void ConsumeNodeServerRpc(int nodeId, ResourceType type, int amount, ServerRpcParams p = default)
            => ConsumeNodeServer(nodeId, type, amount, p.Receive.SenderClientId);

        private void ConsumeNodeServer(int nodeId, ResourceType type, int amount, ulong pickerClientId)
        {
            // Already consumed by someone else this session? Reject (prevents double-grant).
            for (int i = 0; i < netConsumedNodes.Count; i++)
                if (netConsumedNodes[i] == nodeId) return;
            netConsumedNodes.Add(nodeId);              // OnListChanged → every peer despawns

            // Hand the resource to the picker only.
            var target = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { pickerClientId } } };
            GivePickedNodeClientRpc(type, amount, target);
        }

        private void ConsumeNodeLocal(int nodeId)
        {
            netConsumedNodes.Add(nodeId);   // offline: still goes into the list (and fires OnListChanged)
        }

        [ClientRpc]
        private void GivePickedNodeClientRpc(ResourceType type, int amount, ClientRpcParams _)
        {
            if (PlayerInventory.Local != null) PlayerInventory.Local.Add(type, amount);
        }

        // ── Daily bandage (ONE shared first-aid kit per dawn — see BandageSpawner) ───────────
        // Which day's kit has been claimed (0 = none yet). Replicated so the kit despawns for
        // the whole team the moment anyone grabs it, and so late joiners don't see a taken kit.
        private readonly NetworkVariable<int> netBandageTakenDay = new NetworkVariable<int>(0);
        public static int BandageTakenDay => Instance != null ? Instance.netBandageTakenDay.Value : 0;

        /// <summary>Fired on every peer when the day's kit is claimed (arg = day number).</summary>
        public static event Action<int> OnBandageTaken;

        private void OnBandageTakenChanged(int _, int day) => OnBandageTaken?.Invoke(day);

        /// <summary>Called by BandagePickup.Interact on the picker's machine. First to claim the
        /// day's kit wins: the server flips the shared day marker (every peer's BandageSpawner
        /// despawns the prop via OnBandageTaken) and only the winner receives the heal.</summary>
        public static void RequestTakeBandage(int day, float healAmount)
        {
            if (Instance == null) return;
            if (!Instance.IsSpawned)
            {
                // SP / pre-NGO fallback: claim + heal locally.
                SunsetCurse.World.BandagePickup.GrantLocal(healAmount);
                OnBandageTaken?.Invoke(day);
                return;
            }
            if (Instance.IsServer) Instance.TakeBandageServer(day, healAmount, NetworkManager.Singleton.LocalClientId);
            else                   Instance.TakeBandageServerRpc(day, healAmount);
        }

        [ServerRpc(RequireOwnership = false)]
        private void TakeBandageServerRpc(int day, float healAmount, ServerRpcParams p = default)
            => TakeBandageServer(day, healAmount, p.Receive.SenderClientId);

        private void TakeBandageServer(int day, float healAmount, ulong pickerClientId)
        {
            if (netBandageTakenDay.Value == day) return;   // someone grabbed it first this dawn
            netBandageTakenDay.Value = day;                // OnValueChanged → every peer despawns
            var target = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { pickerClientId } } };
            GiveBandageClientRpc(healAmount, target);
        }

        [ClientRpc]
        private void GiveBandageClientRpc(float healAmount, ClientRpcParams _)
            => SunsetCurse.World.BandagePickup.GrantLocal(healAmount);

        private void OnTableBuiltChanged(bool prev, bool next)
        {
            if (next && !prev) OnCraftingTableBuilt?.Invoke();
            else if (prev && !next) OnCraftingTableDespawned?.Invoke();
        }
        private void OnSyrupKick(int _, int __)        => OnLordsSyrupUsed?.Invoke();
        private void OnDistractKick(int _, int __)     => OnMonsterDistracted?.Invoke(netDistractDuration.Value);
        private void OnFlashKick(int _, int __)        => OnTorchFlashed?.Invoke(netLastFlashPos.Value);
        private void OnInvestigateKick(int _, int __)  => OnMonsterInvestigate?.Invoke(netInvestigatePos.Value, netInvestigateSeconds.Value);

        private void OnDawnResetTable(int day)
        {
            if (day <= 1) return;                       // Day 1 = initial
            if (!netHasCraftingTable.Value) return;     // already gone
            netHasCraftingTable.Value = false;          // OnValueChanged → OnCraftingTableDespawned on every peer
        }

        // ───────────── Routing ─────────────

        private void BuildTableRoute(Vector3 worldPos)
        {
            if (!IsSpawned || IsServer) BuildTableServer(worldPos);
            else                        BuildTableServerRpc(worldPos);
        }

        [ServerRpc(RequireOwnership = false)]
        private void BuildTableServerRpc(Vector3 worldPos) => BuildTableServer(worldPos);

        private void BuildTableServer(Vector3 worldPos)
        {
            if (netHasCraftingTable.Value) return;  // already built (idempotent)
            netCraftingTablePos.Value = worldPos;
            netHasCraftingTable.Value = true;
            if (!IsSpawned) OnCraftingTableBuilt?.Invoke();
        }

        private void SyrupStunRoute()
        {
            if (!IsSpawned || IsServer) SyrupStunServer();
            else                        SyrupStunServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void SyrupStunServerRpc() => SyrupStunServer();

        private void SyrupStunServer()
        {
            netLordsSyrupKick.Value += 1;   // OnValueChanged → every peer fires OnLordsSyrupUsed
            if (!IsSpawned) OnLordsSyrupUsed?.Invoke();
        }

        // ───────────── Monster distraction (Torch flash) ─────────────

        private void DistractRoute(float duration)
        {
            if (!IsSpawned || IsServer) DistractServer(duration);
            else                        DistractServerRpc(duration);
        }

        [ServerRpc(RequireOwnership = false)]
        private void DistractServerRpc(float duration) => DistractServer(duration);

        private void DistractServer(float duration)
        {
            netDistractDuration.Value = duration;     // updated first so kick callback reads the right value
            netDistractKick.Value    += 1;
            if (!IsSpawned) OnMonsterDistracted?.Invoke(duration);
        }

        // ───────────── Torch flash visual broadcast ─────────────

        private void FlashRoute(Vector3 worldPos)
        {
            if (!IsSpawned || IsServer) FlashServer(worldPos);
            else                        FlashServerRpc(worldPos);
        }

        [ServerRpc(RequireOwnership = false)]
        private void FlashServerRpc(Vector3 worldPos) => FlashServer(worldPos);

        private void FlashServer(Vector3 worldPos)
        {
            netLastFlashPos.Value = worldPos;          // update FIRST so the kick fires with the right pos
            netFlashKick.Value   += 1;
            if (!IsSpawned) OnTorchFlashed?.Invoke(worldPos);
        }

        // ───────────── World drops (dragged-out resources) ─────────────

        private void DropRoute(ResourceType type, int amount, Vector3 worldPos)
        {
            if (!IsSpawned) { DropLocally(type, amount, worldPos); return; }   // SP fallback
            if (IsServer)   DropServer(type, amount, worldPos);
            else            DropServerRpc(type, amount, worldPos);
        }

        [ServerRpc(RequireOwnership = false)]
        private void DropServerRpc(ResourceType type, int amount, Vector3 worldPos)
            => DropServer(type, amount, worldPos);

        private void DropServer(ResourceType type, int amount, Vector3 worldPos)
        {
            int id = nextDropId++;
            // Add to the synced list → OnListChanged fires OnDropSpawned on every peer, and any
            // LATE JOINER replays it on connect. (No one-shot ClientRpc that late joiners miss.)
            netDrops.Add(new ResourceDrop { id = id, typeInt = (int)type, amount = amount, pos = worldPos });
            // The thud is audible — the compound watcher investigates dropped items.
            SunsetCurse.AI.MonsterAlerts.ReportLoudNoise(worldPos, 16f);
        }

        // SP-only path (no NetworkManager started). Fires the event directly + records locally.
        private void DropLocally(ResourceType type, int amount, Vector3 worldPos)
        {
            int id = nextDropId++;
            serverDrops[id] = new DropData { type = type, amount = amount, pos = worldPos };
            OnDropSpawned?.Invoke(id, type, amount, worldPos);
        }

        // --- Pickup ---

        private void PickupRoute(int dropId)
        {
            if (!IsSpawned) { PickupLocally(dropId); return; }   // SP fallback
            if (IsServer) PickupServer(dropId, NetworkManager.Singleton.LocalClientId);
            else          PickupServerRpc(dropId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PickupServerRpc(int dropId, ServerRpcParams p = default)
            => PickupServer(dropId, p.Receive.SenderClientId);

        private void PickupServer(int dropId, ulong pickerClientId)
        {
            int idx = -1;
            for (int i = 0; i < netDrops.Count; i++) if (netDrops[i].id == dropId) { idx = i; break; }
            if (idx < 0) return;                         // already gone (race — first grabber won)
            var data = netDrops[idx];
            netDrops.RemoveAt(idx);                      // OnListChanged → OnDropDespawned everywhere

            // Hand the resource to the picker only.
            var target = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { pickerClientId } } };
            GiveDroppedClientRpc(data.Type, data.amount, target);
        }

        private void PickupLocally(int dropId)
        {
            if (!serverDrops.TryGetValue(dropId, out var data)) return;
            serverDrops.Remove(dropId);
            OnDropDespawned?.Invoke(dropId);
            if (PlayerInventory.Local != null) PlayerInventory.Local.Add(data.type, data.amount);
        }

        [ClientRpc]
        private void GiveDroppedClientRpc(ResourceType type, int amount, ClientRpcParams _)
        {
            if (PlayerInventory.Local != null) PlayerInventory.Local.Add(type, amount);
        }

        // ───────────── Stone Distractor world spawn/despawn ─────────────

        private void SpawnDistractorRoute(Vector3 worldPos)
        {
            if (!IsSpawned) { SpawnDistractorLocally(worldPos); return; }
            if (IsServer) SpawnDistractorServer(worldPos);
            else          SpawnDistractorServerRpc(worldPos);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SpawnDistractorServerRpc(Vector3 worldPos) => SpawnDistractorServer(worldPos);

        private void SpawnDistractorServer(Vector3 worldPos)
        {
            int id = nextDistractorId++;
            netDistractors.Add(new DistractorDrop { id = id, pos = worldPos });   // synced → late joiners see it
        }

        private void SpawnDistractorLocally(Vector3 worldPos)
        {
            int id = nextDistractorId++;
            serverDistractors[id] = worldPos;
            OnDistractorSpawned?.Invoke(id, worldPos);
        }

        // --- pickup ---

        private void PickupDistractorRoute(int id)
        {
            if (!IsSpawned) { PickupDistractorLocally(id); return; }
            if (IsServer) PickupDistractorServer(id, NetworkManager.Singleton.LocalClientId);
            else          PickupDistractorServerRpc(id);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PickupDistractorServerRpc(int id, ServerRpcParams p = default)
            => PickupDistractorServer(id, p.Receive.SenderClientId);

        private void PickupDistractorServer(int id, ulong pickerClientId)
        {
            int idx = -1;
            for (int i = 0; i < netDistractors.Count; i++) if (netDistractors[i].id == id) { idx = i; break; }
            if (idx < 0) return;
            netDistractors.RemoveAt(idx);                // OnListChanged → OnDistractorDespawned everywhere
            var target = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { pickerClientId } } };
            GiveDistractorClientRpc(target);
        }

        private void PickupDistractorLocally(int id)
        {
            if (!serverDistractors.ContainsKey(id)) return;
            serverDistractors.Remove(id);
            OnDistractorDespawned?.Invoke(id);
            if (PlayerInventory.Local != null) PlayerInventory.Local.AddStoneDistractor(1);
        }

        [ClientRpc]
        private void GiveDistractorClientRpc(ClientRpcParams _)
        {
            if (PlayerInventory.Local != null) PlayerInventory.Local.AddStoneDistractor(1);
        }

        // ───────────── Monster investigate broadcast ─────────────

        private void InvestigateRoute(Vector3 worldPos, float duration)
        {
            if (!IsSpawned || IsServer) InvestigateServer(worldPos, duration);
            else                        InvestigateServerRpc(worldPos, duration);
        }

        [ServerRpc(RequireOwnership = false)]
        private void InvestigateServerRpc(Vector3 worldPos, float duration) => InvestigateServer(worldPos, duration);

        private void InvestigateServer(Vector3 worldPos, float duration)
        {
            netInvestigatePos.Value     = worldPos;
            netInvestigateSeconds.Value = duration;
            netInvestigateKick.Value   += 1;
            if (!IsSpawned) OnMonsterInvestigate?.Invoke(worldPos, duration);
        }

        // ───────────── Held-item drop spawn / despawn ─────────────

        private void SpawnHeldDropRoute(HeldItemType type, Vector3 worldPos)
        {
            if (!IsSpawned) { SpawnHeldDropLocally(type, worldPos); return; }
            if (IsServer) SpawnHeldDropServer(type, worldPos);
            else          SpawnHeldDropServerRpc(type, worldPos);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SpawnHeldDropServerRpc(HeldItemType type, Vector3 worldPos) => SpawnHeldDropServer(type, worldPos);

        private void SpawnHeldDropServer(HeldItemType type, Vector3 worldPos)
        {
            int id = nextHeldDropId++;
            netHeldDrops.Add(new HeldDrop { id = id, typeInt = (int)type, pos = worldPos });   // synced → late joiners see it
            SunsetCurse.AI.MonsterAlerts.ReportLoudNoise(worldPos, 16f);   // audible thud
        }

        private void SpawnHeldDropLocally(HeldItemType type, Vector3 worldPos)
        {
            int id = nextHeldDropId++;
            serverHeldDrops[id] = new HeldDropData { type = type, pos = worldPos };
            OnHeldDropSpawned?.Invoke(id, type, worldPos);
        }

        // --- pickup ---

        private void PickupHeldDropRoute(int id)
        {
            if (!IsSpawned) { PickupHeldDropLocally(id); return; }
            if (IsServer) PickupHeldDropServer(id, NetworkManager.Singleton.LocalClientId);
            else          PickupHeldDropServerRpc(id);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PickupHeldDropServerRpc(int id, ServerRpcParams p = default)
            => PickupHeldDropServer(id, p.Receive.SenderClientId);

        private void PickupHeldDropServer(int id, ulong pickerClientId)
        {
            int idx = -1;
            for (int i = 0; i < netHeldDrops.Count; i++) if (netHeldDrops[i].id == id) { idx = i; break; }
            if (idx < 0) return;
            var data = netHeldDrops[idx];
            netHeldDrops.RemoveAt(idx);                  // OnListChanged → OnHeldDropDespawned everywhere
            var target = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { pickerClientId } } };
            GiveHeldDropClientRpc(data.Type, target);
        }

        private void PickupHeldDropLocally(int id)
        {
            if (!serverHeldDrops.TryGetValue(id, out var data)) return;
            serverHeldDrops.Remove(id);
            OnHeldDropDespawned?.Invoke(id);
            GiveHeldDropToLocal(data.type);
        }

        [ClientRpc]
        private void GiveHeldDropClientRpc(HeldItemType type, ClientRpcParams _) => GiveHeldDropToLocal(type);

        private static void GiveHeldDropToLocal(HeldItemType type)
        {
            if (PlayerInventory.Local == null) return;
            switch (type)
            {
                case HeldItemType.LordsSyrup:    PlayerInventory.Local.AddLordsSyrup(); break;
                case HeldItemType.RespawnPotion: PlayerInventory.Local.AddRespawnPotion(1); break;
            }
        }
    }
}
