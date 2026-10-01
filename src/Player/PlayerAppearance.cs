using Unity.Netcode;
using UnityEngine;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Gives each player a DIFFERENT character model, chosen by their join slot:
    ///   Player 1 (the host) → model[0], Player 2 → model[1], Player 3 → model[2], Player 4 → model[3].
    ///
    /// It swaps only the VISUAL model — everything networked (the controller, inventory, camera,
    /// collider, name tag…) stays on the ONE PlayerCapsule prefab, so there's nothing to duplicate
    /// and the four looks can't drift out of sync. The pick is derived from the replicated
    /// OwnerClientId, which every peer already knows for every player, so all four players see each
    /// other's correct model with no extra networking.
    ///
    /// Each swapped-in model is auto-scaled to <see cref="targetHeight"/> and planted feet-on-the-
    /// ground, so models of different native sizes all read the SAME height — no one looks tiny —
    /// and the first-person camera height (fixed on the capsule) stays in proportion.
    ///
    /// ANIMATION: the model keeps animating (walk / idle / jump / climb) by reusing the player's
    /// existing Animator Controller + a PlayerAnimatorDriver. For that to work your 4 prefabs should
    /// be **Humanoid-rigged** characters (Rig ▸ Animation Type = Humanoid on the import) so the
    /// shared controller retargets onto them. A non-rigged prop still appears, just static.
    ///
    /// SETUP:
    ///   1. Import your 4 character prefabs (e.g. into Assets/_Project/Prefabs/Characters/).
    ///   2. Add this component to the PlayerCapsule prefab (or run Tools ▸ Sunset Curse ▸ 15).
    ///   3. Drag the 4 prefabs into "Character Models" — element 0 = Player 1 … element 3 = Player 4.
    ///   4. Tune "Target Height" (~2.5 m) so they match the capsule.
    /// </summary>
    public class PlayerAppearance : NetworkBehaviour
    {
        [Header("Per-player models (element 0 = Player 1 / host, 1 = Player 2, ...)")]
        [Tooltip("Your 4 imported character prefabs. Picked by join slot. Leave an element EMPTY to " +
                 "keep the default model for that player.")]
        [SerializeField] private GameObject[] characterModels = new GameObject[4];

        [Header("Sizing — keeps everyone the same height so no one looks small")]
        [Tooltip("Each swapped-in model is scaled so it stands this tall (metres), then its feet are " +
                 "planted at the capsule base. Match the player capsule (~2.5 m).")]
        [SerializeField] private float targetHeight = 2.5f;

        private bool applied;   // apply exactly once per instance (guards double-spawn / scene migration)

        /// <summary>The visual model currently shown for this player — the swapped-in character
        /// if one was assigned, otherwise the default. PlayerHealthSync reads this so the downed
        /// tip-over targets the model teammates actually SEE (tipping the hidden default model
        /// left the visible one standing bolt upright).</summary>
        public GameObject ActiveModel { get; private set; }

        public override void OnNetworkSpawn()
        {
            ApplyAppearance();
        }

        private void ApplyAppearance()
        {
            // Runs on EVERY peer, for EVERY player (OwnerClientId is replicated, so all peers pick the
            // same model for a given player → uniform). Idempotent: NGO can raise OnNetworkSpawn again
            // across a MainMenu→gameplay scene transition, and we must not swap twice.
            if (applied) return;
            applied = true;

            // Locate the DEFAULT character model (the child holding the animator/driver) + its controller.
            var defaultDriver = GetComponentInChildren<PlayerAnimatorDriver>(true);
            Animator defaultAnim = defaultDriver != null
                ? defaultDriver.GetComponent<Animator>()
                : GetComponentInChildren<Animator>(true);
            RuntimeAnimatorController sharedController =
                defaultAnim != null ? defaultAnim.runtimeAnimatorController : null;
            GameObject defaultModelGO = null;
            if (defaultAnim != null)
            {
                Transform t = defaultAnim.transform;
                while (t.parent != null && t.parent != transform) t = t.parent;
                if (t != transform) defaultModelGO = t.gameObject;
            }

            GameObject activeModel = defaultModelGO;   // what the owner-hide (bug #4) targets

            // Pick this player's model by join slot.
            int index = characterModels != null && characterModels.Length > 0
                ? (int)(OwnerClientId % (ulong)characterModels.Length)
                : -1;
            GameObject prefab = index >= 0 ? characterModels[index] : null;

            // GUARD (bug #1): a full player/network prefab (has a NetworkObject) is NOT a plain visual
            // model — instantiating it as a child spawns a mess of nested controllers/inputs/network
            // components and left the character invisible. If someone assigns one (e.g. PlayerCapsule),
            // keep the DEFAULT model instead. To give a player the default look, leave the slot EMPTY.
            bool isNetworkPrefab = prefab != null && prefab.GetComponentInChildren<NetworkObject>(true) != null;
            if (isNetworkPrefab)
            {
                Debug.LogWarning($"[PlayerAppearance] Slot {index} is a NETWORK prefab (it has a " +
                    "NetworkObject) — that's a player prefab, not a plain character model. Keeping the " +
                    "DEFAULT model for this player. For the default look, just leave the slot EMPTY.", this);
                prefab = null;
            }

            if (prefab != null)
            {
                if (defaultModelGO != null) defaultModelGO.SetActive(false);
                // The NetworkAnimator (if any) pointed at that hidden animator — silence it; animation
                // self-drives per peer via PlayerAnimatorDriver, so it isn't needed.
                foreach (var na in GetComponentsInChildren<Unity.Netcode.Components.NetworkAnimator>(true))
                    na.enabled = false;

                var model = Instantiate(prefab, transform);
                model.name = $"CharacterModel_P{index + 1}";
                model.transform.localRotation = Quaternion.identity;
                model.transform.localPosition = Vector3.zero;
                StripNonVisual(model);   // visual only — no cameras / audio listeners / colliders-as-controllers
                NormalizeSize(model);
                WireAnimation(model, sharedController);
                activeModel = model;

                // Re-attach the held lantern to the NEW model's hand bone (+ fixes its scale) — the
                // lantern otherwise stayed on the hidden default model or inherited a giant hand scale.
                var lantern = GetComponentInChildren<HeldLanternProp>(true);
                if (lantern != null) lantern.ReAttach();
            }

            ActiveModel = activeModel;   // publish for PlayerHealthSync's downed pose

            // BUG #4: the LOCAL owner shouldn't see their own head / hair clip the first-person camera.
            // Render their body shadows-only — invisible to their camera, but it still casts a shadow.
            if (IsOwner && activeModel != null)
                foreach (var r in activeModel.GetComponentsInChildren<Renderer>(true))
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }

        /// <summary>Keep only visuals: drop any camera / audio listener / character controller a
        /// prefab smuggled in (an FBX can embed a camera; a stray CharacterController would fight
        /// the player's real one).</summary>
        private static void StripNonVisual(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Camera>(true)) Destroy(c);
            foreach (var a in go.GetComponentsInChildren<AudioListener>(true)) Destroy(a);
            foreach (var cc in go.GetComponentsInChildren<CharacterController>(true)) Destroy(cc);
            foreach (var no in go.GetComponentsInChildren<NetworkObject>(true)) Destroy(no);
        }

        /// <summary>Scale the model to targetHeight, then lift it so its feet sit at the capsule base
        /// — so every character reads the same height regardless of how big the source model is.</summary>
        private void NormalizeSize(GameObject model)
        {
            if (!TryGetBounds(model, out Bounds b)) return;
            if (b.size.y > 0.001f && targetHeight > 0f)
                model.transform.localScale *= targetHeight / b.size.y;

            if (TryGetBounds(model, out b))   // re-measure after scaling
                model.transform.position += Vector3.up * (transform.position.y - b.min.y);
        }

        private static bool TryGetBounds(GameObject go, out Bounds bounds)
        {
            var rends = go.GetComponentsInChildren<Renderer>(true);
            bounds = default;
            if (rends.Length == 0) return false;
            bounds = rends[0].bounds;
            foreach (var r in rends) bounds.Encapsulate(r.bounds);
            return true;
        }

        /// <summary>Make the new model animate off the shared controller + a PlayerAnimatorDriver —
        /// but ONLY if it's a Humanoid rig (that's what lets the shared clips retarget). A Generic /
        /// unrigged model is left exactly as it imported (it keeps its own animator if it has one,
        /// or stays a correctly-sized static figure) — we never force the Humanoid controller onto
        /// it, which would T-pose it.</summary>
        private void WireAnimation(GameObject model, RuntimeAnimatorController controller)
        {
            var anim = model.GetComponentInChildren<Animator>();
            bool humanoid = anim != null && anim.avatar != null && anim.avatar.isHuman;
            if (!humanoid || controller == null) return;   // leave non-Humanoid models as-is

            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;
            if (anim.GetComponent<PlayerAnimatorDriver>() == null)
                anim.gameObject.AddComponent<PlayerAnimatorDriver>();
        }
    }
}
