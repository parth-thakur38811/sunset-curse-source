using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Networked door state for co-op. ONE of these in the scene (NetworkObject + this script). It
    /// holds a <see cref="NetworkList{int}"/> of the IDs of every OPEN door; when the list changes,
    /// each peer opens/closes its matching local <see cref="Door"/>. Late joiners replay the list on
    /// spawn. This mirrors the resource-node consumed-list pattern — no per-door NetworkObject needed.
    ///
    /// Each Door computes a deterministic ID from its (fixed, scene-placed) position, so the ID is
    /// identical on every machine. Any player opening a door → ServerRpc → the host updates the list
    /// → everyone (and the host's monster, for sight) sees the same door state.
    ///
    /// SETUP: empty GameObject "DoorSync" + NetworkObject + this script. That's it — Doors find it.
    /// If it's absent, Doors still work locally (single-player), just un-synced.
    /// </summary>
    public class DoorSync : NetworkBehaviour
    {
        public static DoorSync Instance { get; private set; }

        private readonly NetworkList<int> netOpenDoors = new NetworkList<int>();

        // Local registry so we can find the Door(s) for an ID (collisions tolerated as a list).
        private static readonly Dictionary<int, List<Door>> registry = new Dictionary<int, List<Door>>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            netOpenDoors.OnListChanged += OnListChanged;
            // Late-join: snap any already-open doors open (no animation) now that we have the list.
            for (int i = 0; i < netOpenDoors.Count; i++) ApplyLocal(netOpenDoors[i], true, immediate: true);
        }

        public override void OnNetworkDespawn()
        {
            netOpenDoors.OnListChanged -= OnListChanged;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();   // NGO's own NetworkBehaviour cleanup must still run
        }

        private void OnListChanged(NetworkListEvent<int> e)
        {
            if (e.Type == NetworkListEvent<int>.EventType.Add ||
                e.Type == NetworkListEvent<int>.EventType.Insert)
                ApplyLocal(e.Value, true, immediate: false);
            else if (e.Type == NetworkListEvent<int>.EventType.Remove ||
                     e.Type == NetworkListEvent<int>.EventType.RemoveAt)
                ApplyLocal(e.Value, false, immediate: false);
        }

        // ───────────── Door registration ─────────────

        public static void Register(int id, Door door)
        {
            if (!registry.TryGetValue(id, out var list)) { list = new List<Door>(); registry[id] = list; }
            list.Add(door);
            // If this door spawned AFTER its state was already set open, reflect it immediately.
            if (Instance != null && Instance.IsOpenId(id)) door.SetOpenFromNetwork(true, immediate: true);
        }

        public static void Unregister(int id, Door door)
        {
            if (registry.TryGetValue(id, out var list))
            {
                list.Remove(door);
                if (list.Count == 0) registry.Remove(id);
            }
        }

        private bool IsOpenId(int id)
        {
            for (int i = 0; i < netOpenDoors.Count; i++) if (netOpenDoors[i] == id) return true;
            return false;
        }

        private static void ApplyLocal(int id, bool open, bool immediate)
        {
            if (!registry.TryGetValue(id, out var list)) return;
            foreach (var d in list) if (d != null) d.SetOpenFromNetwork(open, immediate);
        }

        // ───────────── Toggle routing (called by Door.Interact on any peer) ─────────────

        public static void RequestSetOpen(int id, bool open)
        {
            if (Instance == null) { ApplyLocal(id, open, immediate: false); return; }   // no DoorSync → local only
            Instance.Route(id, open);
        }

        private void Route(int id, bool open)
        {
            if (!IsSpawned || IsServer) ServerSetOpen(id, open);
            else                        SetOpenServerRpc(id, open);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SetOpenServerRpc(int id, bool open) => ServerSetOpen(id, open);

        private void ServerSetOpen(int id, bool open)
        {
            int idx = -1;
            for (int i = 0; i < netOpenDoors.Count; i++) if (netOpenDoors[i] == id) { idx = i; break; }
            bool currentlyOpen = idx >= 0;
            if (open && !currentlyOpen) netOpenDoors.Add(id);          // OnListChanged → open everywhere
            else if (!open && currentlyOpen) netOpenDoors.RemoveAt(idx);// OnListChanged → close everywhere

            if (!IsSpawned) ApplyLocal(id, open, immediate: false);    // SP fallback (no NV callback)
        }

        /// <summary>Deterministic door ID from its fixed scene position (rounded to 0.1m). Same on
        /// every peer, so open/close syncs by ID exactly like the resource-node consumed list.</summary>
        public static int ComputeDoorId(Vector3 pos)
        {
            unchecked
            {
                int x = Mathf.RoundToInt(pos.x * 10f);
                int y = Mathf.RoundToInt(pos.y * 10f);
                int z = Mathf.RoundToInt(pos.z * 10f);
                return ((x * 73856093) ^ (y * 19349663) ^ (z * 83492791)) ^ 0x00D00D;  // door salt
            }
        }
    }
}
