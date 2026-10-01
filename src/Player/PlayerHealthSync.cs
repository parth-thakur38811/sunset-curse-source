using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Networked damage receiver on PlayerCapsule. The host-authoritative MonsterAI can't call
    /// PlayerStats.TakeDamage directly on a remote player (their PlayerStats is owner-only enabled,
    /// so on the host's machine the remote player's PlayerStats is DISABLED — any TakeDamage call
    /// would no-op). This component routes the kill via a targeted ClientRpc that fires on the
    /// VICTIM's machine, where their PlayerStats is enabled AND their PlayerInventory.Local is
    /// correctly bound, so the existing Respawn-Potion intercept + death flow runs as designed.
    ///
    /// SETUP: add to PlayerCapsule prefab (root). Do NOT put it in NetworkPlayer's "Owner Only
    /// Behaviours" — every peer must have it enabled to receive the targeted ClientRpc.
    /// </summary>
    public class PlayerHealthSync : NetworkBehaviour
    {
        private PlayerInventory inv;

        private void Awake()
        {
            inv = GetComponent<PlayerInventory>();
            // These helpers are created in code so the PlayerCapsule prefab needs no manual
            // wiring — every player copy gets them. (Plain MonoBehaviours only: NGO forbids
            // adding NetworkBehaviours to an already-spawned object at runtime.)
            if (GetComponent<ReviveTarget>() == null) gameObject.AddComponent<ReviveTarget>();
            if (GetComponent<SpawnAtSafeHouse>() == null) gameObject.AddComponent<SpawnAtSafeHouse>();
            if (GetComponent<PlayerClimb>() == null) gameObject.AddComponent<PlayerClimb>();
            if (GetComponent<KrotkofalRadio>() == null) gameObject.AddComponent<KrotkofalRadio>();
            if (GetComponent<TeleportPotionUser>() == null) gameObject.AddComponent<TeleportPotionUser>();
        }

        // ─────────────── Corpse hiding: no standing body left behind on death ───────────────

        // Downed-pose state: the character model child (the thing with the Animator) gets tipped
        // over on REMOTE machines while this player is downed, so teammates see a collapsed body
        // instead of an upright statue. The owner sees their own first-person camera drop instead.
        private Animator bodyAnimator;
        private Transform bodyRoot;
        private Quaternion bodyHomeRot;
        private Vector3 bodyHomePos;
        private bool poseApplied;

        public override void OnNetworkSpawn()
        {
            inv = GetComponent<PlayerInventory>();
            bodyAnimator = GetComponentInChildren<Animator>(true);
            if (bodyAnimator != null) bodyRoot = bodyAnimator.transform;
            // Re-evaluate this player's visibility + pose whenever ANY player's alive/downed
            // state changes (fires on every peer, including for late joiners on spawn).
            PlayerInventory.OnAnyAliveChanged += UpdateBodyVisibility;
            PlayerInventory.OnAnyAliveChanged += UpdateDownedPose;
            UpdateBodyVisibility();
            UpdateDownedPose();
        }

        public override void OnNetworkDespawn()
        {
            PlayerInventory.OnAnyAliveChanged -= UpdateBodyVisibility;
            PlayerInventory.OnAnyAliveChanged -= UpdateDownedPose;
        }

        /// <summary>Tip the character model over while downed (remote view only). Rotation-based —
        /// needs no "collapsed" animation clip: the Animator is frozen so the body holds its last
        /// pose, tipped onto its back. Everything restores exactly on revive (or hides on death).</summary>
        private void UpdateDownedPose()
        {
            if (inv == null) return;
            bool collapsed = inv.IsAlive && inv.IsDowned && IsSpawned && !IsOwner;
            if (collapsed == poseApplied) return;

            if (collapsed)
            {
                // Resolve the body NOW, not at spawn: PlayerAppearance may have SWAPPED the visual
                // model after our OnNetworkSpawn cached it (both run on spawn, order isn't
                // guaranteed) — tipping the old HIDDEN model left the visible swapped one standing
                // bolt upright, so teammates saw a downed player "standing straight".
                ResolveBodyRoot();
                if (bodyRoot == null) return;
                poseApplied = true;
                bodyHomeRot = bodyRoot.localRotation;
                bodyHomePos = bodyRoot.localPosition;
                if (bodyAnimator != null) bodyAnimator.enabled = false;   // hold the pose — no idle anim while "unconscious"
                bodyRoot.localRotation = bodyHomeRot * Quaternion.Euler(-80f, 0f, 0f);
                bodyRoot.localPosition = bodyHomePos + new Vector3(0f, 0.25f, 0f);
            }
            else
            {
                poseApplied = false;
                if (bodyRoot == null) return;
                bodyRoot.localRotation = bodyHomeRot;
                bodyRoot.localPosition = bodyHomePos;
                if (bodyAnimator != null) bodyAnimator.enabled = true;
            }
        }

        /// <summary>Find the model teammates actually SEE: PlayerAppearance's active (possibly
        /// swapped) model first, else the active Animator child (the default body).</summary>
        private void ResolveBodyRoot()
        {
            var appearance = GetComponent<PlayerAppearance>();
            if (appearance != null && appearance.ActiveModel != null)
            {
                bodyRoot = appearance.ActiveModel.transform;
                bodyAnimator = appearance.ActiveModel.GetComponentInChildren<Animator>(true);
                return;
            }
            var anim = GetComponentInChildren<Animator>(false);   // ACTIVE children only
            if (anim != null)
            {
                bodyAnimator = anim;
                bodyRoot = anim.transform;
            }
        }

        /// <summary>Show the character body only for an ALIVE player that ISN'T us (first-person
        /// already hides our own body). A dead player is hidden on EVERY peer — no standing corpse.
        /// UI (HUD / nameplate canvases) is never touched.</summary>
        private void UpdateBodyVisibility()
        {
            if (inv == null) return;
            bool show = inv.IsAlive && !IsOwner;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r.GetComponentInParent<Canvas>() != null) continue;   // leave UI alone
                r.enabled = show;
            }

            // A DEAD body must stop BLOCKING the living too: hiding only the renderers left an
            // invisible CharacterController standing in everyone's way. Death is permanent (the
            // Respawn Potion intercepts BEFORE death), so this never needs re-enabling — and we
            // deliberately only ever DISABLE, so live players' colliders are never touched.
            if (!inv.IsAlive)
            {
                foreach (var c in GetComponentsInChildren<Collider>(true))
                    if (c != null) c.enabled = false;
            }
        }

        /// <summary>Server-only entry. Sends a targeted ClientRpc to the OWNER of this player,
        /// who locally applies the damage. Safe to call from MonsterAI on the host.</summary>
        public void ServerKillPlayer()
        {
            if (!IsSpawned) { KillLocal(); return; }   // SP / offline path
            if (!IsServer) { Debug.LogWarning("[PlayerHealthSync] ServerKillPlayer called on non-server.", this); return; }

            var target = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
            };
            KillClientRpc(target);
        }

        [ClientRpc]
        private void KillClientRpc(ClientRpcParams _) => KillLocal();

        private void KillLocal()
        {
            // Apply damage to the LOCAL PlayerStats — this is the owner's machine, so PlayerStats
            // is enabled, PlayerInventory.Local is bound, and TriggerRespawn (if a potion is held)
            // works correctly. Otherwise the normal death + spectator/game-over flow runs.
            var stats = GetComponent<PlayerStats>();
            if (stats != null) stats.TakeDamage(99999f);
        }

        // ─────────────── Revive routing (the kill pattern, in reverse) ───────────────

        /// <summary>Called on the REVIVER's machine when their channel completes (ReviveTarget).
        /// Routes to the server, which validates and forwards to the victim-owner's machine —
        /// the only place their PlayerStats actually runs.</summary>
        public void RequestRevive()
        {
            if (!IsSpawned) { ReviveLocal(); return; }   // SP / offline path
            RequestReviveServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestReviveServerRpc()
        {
            // Validate against the replicated flags — rejects stale/duplicate requests (e.g. two
            // teammates finishing their channels at nearly the same moment).
            if (inv == null) inv = GetComponent<PlayerInventory>();
            if (inv == null || !inv.IsAlive || !inv.IsDowned) return;

            var target = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
            };
            ReviveClientRpc(target);
        }

        [ClientRpc]
        private void ReviveClientRpc(ClientRpcParams _) => ReviveLocal();

        private void ReviveLocal()
        {
            var stats = GetComponent<PlayerStats>();
            if (stats != null) stats.TriggerRevive();
        }
    }
}
