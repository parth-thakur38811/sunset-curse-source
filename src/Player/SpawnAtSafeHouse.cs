using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Places the player INSIDE the safe house when the gameplay scene starts. Auto-added to the
    /// PlayerCapsule at runtime by <see cref="PlayerHealthSync"/> — no prefab wiring needed.
    ///
    /// HOW IT WORKS: each client positions its OWN player (the PlayerCapsule's NetworkTransform is
    /// owner-authoritative, so an owner-side teleport replicates to everyone — same pattern as the
    /// Respawn Potion). Players fan out in a small ring around the safe-house centre so four of
    /// them don't spawn inside each other. Runs once per gameplay-scene load; if the scene has no
    /// SafeHouse (main menu, or it hasn't been placed yet), it quietly does nothing and the
    /// default spawn position is used.
    ///
    /// NOTE: deliberately a plain MonoBehaviour, NOT a NetworkBehaviour — NGO requires the
    /// NetworkBehaviour list of a spawned object to be identical on every peer, so runtime-added
    /// components must never be NetworkBehaviours.
    /// </summary>
    public class SpawnAtSafeHouse : MonoBehaviour
    {
        [Tooltip("How far from the safe-house centre each player stands, in metres.")]
        [SerializeField] private float ringRadius = 1.5f;
        [Tooltip("How long to wait for the SafeHouse to exist before giving up (scene load order).")]
        [SerializeField] private float waitTimeout = 12f;

        private NetworkObject netObj;
        private bool positioned;

        private void Awake()
        {
            netObj = GetComponent<NetworkObject>();
            // In multiplayer the player object is spawned in the MENU scene and carried into the
            // game — OnNetworkSpawn never re-fires, so the scene-load callback is our real hook.
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Start() => TryBegin();   // covers spawning directly INTO the gameplay scene

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            positioned = false;   // a new scene load = a new game start
            TryBegin();
        }

        private void TryBegin()
        {
            if (positioned || !isActiveAndEnabled) return;
            StopAllCoroutines();
            StartCoroutine(PositionWhenReady());
        }

        private IEnumerator PositionWhenReady()
        {
            // Owner only — every machine moves just its own player.
            if (netObj != null && netObj.IsSpawned && !netObj.IsOwner) yield break;

            // The SafeHouse's Awake may run after ours during the scene load — wait for it.
            float deadline = Time.time + waitTimeout;
            while (World.SafeHouse.Instance == null && Time.time < deadline) yield return null;

            var safe = World.SafeHouse.Instance;
            if (safe == null || positioned) yield break;   // no safe house here — default spawn
            positioned = true;

            // Ring slot from the client id, so up to 8 players never overlap.
            ulong id = netObj != null && netObj.IsSpawned ? netObj.OwnerClientId : 0UL;
            float angle = (id % 8UL) * Mathf.PI * 2f / 8f;
            Vector3 target = safe.Center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ringRadius;

            // Land on the safe house FLOOR. IMPORTANT: cast from just above the floor marker
            // (~2m — below the ceiling, above the floor), NOT from the sky — a sky-cast hits the
            // ROOF first and everyone spawns on top of the house.
            if (Physics.Raycast(target + Vector3.up * 2f, Vector3.down, out var hit, 10f,
                                ~0, QueryTriggerInteraction.Ignore))
                target = hit.point + Vector3.up * 0.2f;

            Teleport(target);
            Debug.Log($"[SpawnAtSafeHouse] Player placed inside the safe house at {target}.", this);
        }

        // Same reliable warp the Respawn Potion uses: CharacterController off (it fights transform
        // writes), NetworkTransform.Teleport (replicates as a SNAP, not a slide), then resync.
        private void Teleport(Vector3 pos)
        {
            var cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            var netTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
            if (netTransform != null && netTransform.IsSpawned)
                netTransform.Teleport(pos, transform.rotation, transform.localScale);
            else
                transform.position = pos;

            if (cc != null) cc.enabled = true;
            Physics.SyncTransforms();
        }
    }
}
