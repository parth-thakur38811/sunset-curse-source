using Unity.Netcode;
using UnityEngine;
using StarterAssets;
using SunsetCurse.Core;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Reports how much NOISE the player is making right now, as a radius (in metres) within which
    /// the monster can hear them:
    ///   - RUNNING  → loud  (big radius — draws the monster from far away)
    ///   - walking  → quiet (small radius)
    ///   - standing → silent (0)
    /// The monster's hearing reads <see cref="NoiseRadius"/>.
    ///
    /// MULTIPLAYER: the movement noise is computed by the OWNING client (the only machine where
    /// this player's CharacterController is actually Move()'d and the sprint input is real) and
    /// published through an owner-write NetworkVariable. The HOST — which runs the monster AI —
    /// reads the replicated value. Without this the host could never hear remote players run,
    /// because on the host a remote player is driven by NetworkTransform (position is set directly),
    /// so its CharacterController.velocity stays ~0 and this whole component is owner-only-disabled.
    ///
    /// SETUP: put this on the player (PlayerCapsule, alongside PlayerStats / the controller). The
    /// PlayerCapsule already has a NetworkObject, which this NetworkBehaviour needs.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerNoise : NetworkBehaviour
    {
        [Tooltip("Heard-from radius while standing still (usually 0 = silent).")]
        [SerializeField] private float idleNoise = 0f;
        [Tooltip("Heard-from radius while walking.")]
        [SerializeField] private float walkNoise = 6f;
        [Tooltip("Heard-from radius while RUNNING — loud; this is what draws the monster.")]
        [SerializeField] private float runNoise = 24f;
        [Tooltip("Minimum horizontal speed (m/s) to count as moving.")]
        [SerializeField] private float moveThreshold = 0.6f;
        [Tooltip("Heard-from radius spike when the player JUMPS or LANDS — a thud is loud. Feeds " +
                 "the same replicated noise the monsters listen to.")]
        [SerializeField] private float jumpNoise = 20f;
        [Tooltip("How long the jump/land noise spike lasts, in seconds.")]
        [SerializeField] private float jumpNoiseSeconds = 0.8f;

        // Owner-write movement noise. The owner computes it locally and writes it; every peer
        // (including the host) reads the replicated value. Read perm = Everyone so the host-run
        // monster can read a remote player's noise.
        private readonly NetworkVariable<float> netNoiseRadius = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Single-player / offline fallback (no NetworkManager running → NetworkVariable is inert).
        private float localNoiseRadius;

        /// <summary>Current radius the monster can hear the player from (metres). Combines the
        /// replicated movement noise with any <see cref="ExtraNoise"/> (ritual etc.), taking the
        /// bigger of the two.</summary>
        public float NoiseRadius
        {
            get
            {
                float move = IsSpawned ? netNoiseRadius.Value : localNoiseRadius;
                return Mathf.Max(move, ExtraNoise);
            }
        }

        /// <summary>An external noise broadcast (e.g. the escape ritual) — the bigger of this and
        /// the movement noise is used. Set on the SERVER for the channeler; because it's folded
        /// into <see cref="NoiseRadius"/> via Max (not through the owner-write NetworkVariable), the
        /// host-run monster is drawn even when a CLIENT is channeling. Reset to 0 when nothing loud
        /// is happening. Not networked — only the server, which runs the monster, reads it.</summary>
        public float ExtraNoise { get; set; }

        private CharacterController controller;
        private StarterAssetsInputs input;
        private float lastPublished = -1f;
        private bool wasGrounded = true;
        private float jumpImpulseTimer;   // > 0 while a jump/land thud is still "ringing"
        private float airborneTime;       // filters CharacterController grounded-flicker on steps

        private void Start()
        {
            controller = GetComponent<CharacterController>();
            input = GetComponent<StarterAssetsInputs>();
        }

        private void Update()
        {
            // This component is owner-only-enabled (see NetworkPlayer's list), so Update runs only
            // on the local owner — exactly the machine where velocity + sprint are meaningful.
            if (controller == null) return;

            Vector3 v = controller.velocity;
            float horizontal = new Vector2(v.x, v.z).magnitude;
            bool moving = horizontal > moveThreshold;
            bool running = input != null && input.sprint && moving;   // actually pressing sprint
            float moveNoise = running ? runNoise : (moving ? walkNoise : idleNoise);

            // Jump / landing thud: a leaving-the-ground push-off or a REAL landing spikes the noise
            // briefly. Both monsters hear it through the same replicated radius.
            //
            // IMPORTANT FILTERS: CharacterController.isGrounded flickers constantly while walking
            // over steps, ramps and uneven house floors — every flicker used to count as a
            // "landing", so plain WALKING radiated jump-level noise and the compound watcher
            // hunted quietly-walking players. Now a landing only counts after ≥ 0.25s genuinely
            // airborne, and a push-off needs real jump velocity (~4.5 m/s), not a step-up bump.
            bool grounded = controller.isGrounded;
            if (!grounded) airborneTime += Time.deltaTime;
            if (grounded != wasGrounded)
            {
                bool jumpedUp = !grounded && v.y > 3f;                  // a real jump push-off
                bool realLanding = grounded && airborneTime >= 0.25f;   // a real fall, not a step
                if (jumpedUp || realLanding) jumpImpulseTimer = jumpNoiseSeconds;
                if (grounded) airborneTime = 0f;
                wasGrounded = grounded;
            }
            if (jumpImpulseTimer > 0f)
            {
                jumpImpulseTimer -= Time.deltaTime;
                moveNoise = Mathf.Max(moveNoise, jumpNoise);
            }
            // Rain (Weather.NoiseMultiplier) muffles you — the monster hears the smaller radius.
            // Ritual broadcasts (ExtraNoise) are NOT muffled — they're supernatural noise.
            moveNoise *= Weather.NoiseMultiplier;

            localNoiseRadius = moveNoise;   // keeps SP working (NoiseRadius reads this when !IsSpawned)

            if (!IsSpawned || !IsOwner) return;
            // Only publish on a meaningful change so we're not marking the NetworkVariable dirty
            // every single frame.
            if (Mathf.Abs(moveNoise - lastPublished) > 0.05f)
            {
                netNoiseRadius.Value = moveNoise;
                lastPublished = moveNoise;
            }
        }
    }
}
