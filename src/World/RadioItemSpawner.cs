using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Spawns the radio parts in the compound at night — the same host-picks/everyone-mirrors
    /// pattern as <see cref="NightValuableSpawner"/>, but with ONE REPLICATED SLOT PER PART
    /// (battery / transformer / krotkofal), because on the final night several parts can be out
    /// at once.
    ///
    /// CADENCE (scales with the 2/4/7-night option):
    ///   • Normal nights: ONE random not-yet-in-play part spawns (7-night games: nights 1–3;
    ///     4-night games: same).
    ///   • FINAL night: ALL still-missing parts spawn at once — in a 2-night game that's night 2
    ///     with the remaining two parts (per design), and in longer games it's the last chance
    ///     for anything the group missed (anti-softlock).
    ///   A part is "in play" if someone carries it, it's attached to the tower, or it's lying as
    ///   a death-drop.
    ///
    /// SPOTS: never the ritual potion's spot (waits one frame after nightfall so the potion picks
    /// first), and parts never share a spot with each other.
    ///
    /// PICKUP: per-part server validation — the part must still be in the world. Two players
    /// pressing E on the SAME part in the same instant: the server clears its slot on the first
    /// request and rejects the second. Grabs of DIFFERENT parts never block each other.
    ///
    /// SETUP: Tools ▸ Sunset Curse ▸ 8 adds this beside NightValuableSpawner on the ItemSpawner
    /// object (same NetworkObject) and copies its hiding spots.
    /// </summary>
    public class RadioItemSpawner : NetworkBehaviour
    {
        public static RadioItemSpawner Instance { get; private set; }

        [Header("Hiding spots (copied from NightValuableSpawner by the setup tool)")]
        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

        // ───────────── Networked state: one spot index per PART (-1 = not in the world) ─────────────
        private readonly NetworkVariable<int> netIndexBattery     = new NetworkVariable<int>(-1);
        private readonly NetworkVariable<int> netIndexTransformer = new NetworkVariable<int>(-1);
        private readonly NetworkVariable<int> netIndexKrotkofal   = new NetworkVariable<int>(-1);
        private readonly NetworkVariable<int> netIndexGoldenKey   = new NetworkVariable<int>(-1);
        // Bumped after every batch of index writes → every peer rebuilds its local visuals once.
        private readonly NetworkVariable<int> netGeneration       = new NetworkVariable<int>(0);

        private readonly Dictionary<RadioItemKind, GameObject> currentItems =
            new Dictionary<RadioItemKind, GameObject>();
        private int lastIndex = -1;

        private NetworkVariable<int> IndexOf(RadioItemKind k) => k switch
        {
            RadioItemKind.Battery     => netIndexBattery,
            RadioItemKind.Transformer => netIndexTransformer,
            RadioItemKind.GoldenKey   => netIndexGoldenKey,
            _                         => netIndexKrotkofal,
        };

        private static readonly RadioItemKind[] AllKinds =
        {
            RadioItemKind.Battery, RadioItemKind.Transformer, RadioItemKind.Krotkofal,
            RadioItemKind.GoldenKey,
        };

        /// <summary>The night the golden key first spawns: night 4 per design, pulled in to the
        /// FINAL night in games shorter than 4 nights (2-night games: night 2) so the tower gate
        /// can always be opened.</summary>
        private static int GoldenKeyNight
            => GameClock.Instance != null ? Mathf.Min(4, GameClock.Instance.TotalDays) : 4;

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
                GameClock.Instance.OnNightStart += HandleNightServer;
                GameClock.Instance.OnDayStart   += HandleDayServer;
            }
            RebuildItems();   // late joiner: mirror whatever is live right now
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

        // ───────────── Server: nightly pick ─────────────

        private void HandleNightServer(int day) => StartCoroutine(PickAfterPotion(day));

        private IEnumerator PickAfterPotion(int day)
        {
            // Let NightValuableSpawner place the potion first so we can dodge its spot.
            yield return null;

            if (spawnPoints == null || spawnPoints.Count == 0) yield break;

            List<RadioItemKind> pool = RemainingKinds(day);
            if (pool.Count == 0)
            {
                Debug.Log($"[RadioItemSpawner] Night {day}: all radio parts are in play — nothing to spawn.");
                yield break;
            }

            // Final night → EVERYTHING still missing spawns (2-night games: night 2 = the other
            // two parts). Normal nights → one random part.
            bool finalNight = GameClock.Instance != null && day >= GameClock.Instance.TotalDays;
            int count = finalNight ? pool.Count : 1;

            var usedIndices = new List<int>();
            if (NightValuableSpawner.Instance != null && NightValuableSpawner.Instance.CurrentSpawnIndex >= 0)
                usedIndices.Add(NightValuableSpawner.Instance.CurrentSpawnIndex);

            for (int n = 0; n < count; n++)
            {
                RadioItemKind kind = pool[UnityEngine.Random.Range(0, pool.Count)];
                pool.Remove(kind);

                int index = UnityEngine.Random.Range(0, spawnPoints.Count);
                int safety = 0;
                while ((usedIndices.Contains(index) || index == lastIndex) && safety++ < 48)
                    index = UnityEngine.Random.Range(0, spawnPoints.Count);
                usedIndices.Add(index);
                lastIndex = index;

                IndexOf(kind).Value = index;
                Debug.Log($"[RadioItemSpawner] Night {day}: {kind} hidden at index {index}" +
                          (finalNight ? " (final night — spawning all remaining)." : "."));
            }

            netGeneration.Value += 1;   // one kick → every peer rebuilds visuals
        }

        /// <summary>Parts that still need to spawn: nobody carries them, not attached to the
        /// tower, not lying on the ground as a death-drop.</summary>
        private List<RadioItemKind> RemainingKinds(int day)
        {
            var pool = new List<RadioItemKind>();
            foreach (var k in AllKinds)
            {
                if (k == RadioItemKind.GoldenKey)
                {
                    // The key waits for its night (4, or the final night in shorter games) and
                    // stops spawning once the tower gate has been opened — it's spent history.
                    if (day < GoldenKeyNight) continue;
                    if (RadioTowerState.Instance != null && RadioTowerState.Instance.GateUnlocked) continue;
                }
                if (IndexOf(k).Value >= 0) continue;   // already out in the compound
                if (RadioTowerState.Instance != null && RadioTowerState.Instance.KindAttachedOrDropped(k))
                    continue;
                bool carried = false;
                foreach (var inv in FindObjectsByType<PlayerInventory>(FindObjectsSortMode.None))
                    if (inv != null && inv.HasRadioItem(k)) { carried = true; break; }
                if (!carried) pool.Add(k);
            }
            return pool;
        }

        private void HandleDayServer(int day)
        {
            bool any = false;
            foreach (var k in AllKinds)
            {
                if (IndexOf(k).Value < 0) continue;
                IndexOf(k).Value = -1;   // uncollected → back to the pool for a later night
                any = true;
            }
            if (any) netGeneration.Value += 1;
        }

        // ───────────── Every peer: mirror the slots ─────────────

        private void HandleGenerationChanged(int prev, int next) => RebuildItems();

        private void RebuildItems()
        {
            foreach (var kv in currentItems)
                if (kv.Value != null) Destroy(kv.Value);
            currentItems.Clear();

            if (RadioTowerState.Instance == null || spawnPoints == null) return;

            foreach (var k in AllKinds)
            {
                int index = IndexOf(k).Value;
                if (index < 0 || index >= spawnPoints.Count) continue;
                Transform spot = spawnPoints[index];
                if (spot == null) continue;

                var go = RadioTowerState.Instance.CreateItemVisual(k, spot.position);
                go.AddComponent<RadioTowerItem>().Configure(k);
                currentItems[k] = go;
            }
        }

        // ───────────── Pickup (server-validated per part, first grab wins) ─────────────

        public void RequestPickup(RadioItemKind kind)
        {
            if (!IsSpawned) { PickupServerSide(kind, ulong.MaxValue); return; }
            if (IsServer)   PickupServerSide(kind, NetworkManager.LocalClientId);
            else            PickupServerRpc((byte)kind);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PickupServerRpc(byte kind, ServerRpcParams p = default)
            => PickupServerSide((RadioItemKind)kind, p.Receive.SenderClientId);

        private void PickupServerSide(RadioItemKind kind, ulong picker)
        {
            if (IndexOf(kind).Value < 0) return;   // already grabbed (or never out) — reject
            IndexOf(kind).Value = -1;
            netGeneration.Value += 1;              // everyone despawns their copy of this part

            if (picker == ulong.MaxValue || (IsServer && IsSpawned && picker == NetworkManager.LocalClientId))
            {
                RadioTowerState.GrantLocal(kind);  // host / offline picked it up themselves
            }
            else
            {
                var target = new ClientRpcParams
                { Send = new ClientRpcSendParams { TargetClientIds = new[] { picker } } };
                GrantClientRpc((byte)kind, target);
            }

            // A radio part / golden key is a compound valuable too (UPDATE #5): opening the
            // compound to the stalker + a scent flare at the picker, exactly like the potion.
            Vector3? pickerPos = ResolvePickerPos(picker);
            if (pickerPos.HasValue) SunsetCurse.AI.MonsterAlerts.ReportValuableTaken(pickerPos.Value);
        }

        /// <summary>Where the picker is standing (they're on top of the part they just took).
        /// SP/offline (picker == MaxValue) → the local player; MP → the connected client's body.</summary>
        private Vector3? ResolvePickerPos(ulong picker)
        {
            if (picker == ulong.MaxValue || NetworkManager.Singleton == null)
            {
                var local = PlayerInventory.Local;
                return local != null ? local.transform.position : (Vector3?)null;
            }
            if (NetworkManager.Singleton.ConnectedClients.TryGetValue(picker, out var nc) &&
                nc.PlayerObject != null)
                return nc.PlayerObject.transform.position;
            return null;
        }

        [ClientRpc]
        private void GrantClientRpc(byte kind, ClientRpcParams _)
            => RadioTowerState.GrantLocal((RadioItemKind)kind);
    }
}
