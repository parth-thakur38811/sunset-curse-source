using UnityEngine;
using StarterAssets;
using SunsetCurse.Audio;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Footstep, jump and land sounds for the first-person player. WORKS ON EVERY PLAYER INSTANCE
    /// (local AND remote) so co-op players can hear each other moving through the forest.
    ///
    /// HOW IT WORKS in multiplayer:
    ///   - Local owner: position is moved by the FPS controller; this script sees the deltas and
    ///     fires footstep cadence based on horizontal speed.
    ///   - Remote players: their NetworkTransform replicates position from the host; this script
    ///     sees the same deltas and fires footsteps the same way.
    ///   - Every footstep plays via <see cref="AudioManager.PlaySfx3D"/> at the player's WORLD
    ///     position, so other players hear it directionally with distance falloff.
    ///
    /// Walk vs. run is decided by speed (above <see cref="runThreshold"/> = run cadence + run
    /// clips). That doesn't depend on local input — works for remote players whose StarterAssets
    /// input component isn't running.
    ///
    /// Jump / land sounds need the StarterAssets FirstPersonController to know when we leave /
    /// touch the ground — they only fire for the LOCAL owner (where the FPS controller is enabled).
    /// Remote players just skip jump/land (the footsteps are the important cue anyway).
    ///
    /// SETUP: add to the player prefab. **IMPORTANT for multiplayer:** REMOVE FootstepAudio from
    /// the NetworkPlayer's "Owner Only Behaviours" list — it must run on every instance.
    /// Needs an AudioManager in the scene.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FootstepAudio : MonoBehaviour
    {
        [Header("Footsteps (random pick; leave a set empty to silence that gait)")]
        [SerializeField] private AudioClip[] walkClips;
        [SerializeField] private AudioClip[] runClips;
        [Tooltip("Seconds between steps while walking / running.")]
        [SerializeField] private float walkStepInterval = 0.5f;
        [SerializeField] private float runStepInterval = 0.33f;
        [Range(0f, 1f)] [SerializeField] private float walkVolume = 0.5f;
        [Range(0f, 1f)] [SerializeField] private float runVolume = 0.85f;
        [Tooltip("Horizontal speed (m/s) above which this counts as 'moving' and footsteps fire.")]
        [SerializeField] private float moveThreshold = 0.6f;
        [Tooltip("Horizontal speed (m/s) above which we switch to the RUN clip set and cadence.")]
        [SerializeField] private float runThreshold = 4.5f;
        [Tooltip("Max distance (m) at which OTHER players can hear this player's footsteps.")]
        [SerializeField] private float maxAudibleDistance = 30f;

        [Header("Jump / land (LOCAL owner only — needs the FPS controller's grounded check)")]
        [SerializeField] private AudioClip jumpClip;
        [SerializeField] private AudioClip landClip;
        [Range(0f, 1f)] [SerializeField] private float jumpVolume = 0.8f;
        [Range(0f, 1f)] [SerializeField] private float landVolume = 0.7f;

        private CharacterController controller;
        private FirstPersonController fpc;
        private float stepTimer;
        private bool wasGrounded = true;
        private Vector3 lastPosition;
        private bool positionCaptured;

        private void Start()
        {
            controller = GetComponent<CharacterController>();
            fpc = GetComponent<FirstPersonController>();
            lastPosition = transform.position;
        }

        private void Update()
        {
            // Horizontal speed from transform delta. This works on EVERY instance:
            //   - local owner → position moved by the FPS controller
            //   - remote players → position replicated by NetworkTransform
            float horizontalSpeed = 0f;
            if (positionCaptured)
            {
                Vector3 delta = transform.position - lastPosition;
                delta.y = 0f;
                horizontalSpeed = delta.magnitude / Mathf.Max(0.0001f, Time.deltaTime);
            }
            lastPosition = transform.position;
            positionCaptured = true;

            bool moving = horizontalSpeed > moveThreshold;
            bool running = horizontalSpeed > runThreshold;

            // Jump / land — only the local owner has an enabled FPS controller (the script is in
            // the NetworkPlayer's owner-only list). On remote instances fpc.enabled is false and
            // we skip this block; footsteps still play, jump/land just doesn't.
            if (fpc != null && fpc.enabled)
            {
                bool grounded = fpc.Grounded;
                Vector3 v = controller != null ? controller.velocity : Vector3.zero;

                if (wasGrounded && !grounded && v.y > 1f) PlayAtMe(jumpClip, jumpVolume);
                if (!wasGrounded && grounded) { PlayAtMe(landClip, landVolume); stepTimer = 0f; }
                wasGrounded = grounded;
            }

            // Footstep cadence.
            if (moving)
            {
                stepTimer -= Time.deltaTime;
                if (stepTimer <= 0f)
                {
                    if (running) PlayRandom(runClips, runVolume);
                    else         PlayRandom(walkClips, walkVolume);
                    stepTimer = running ? runStepInterval : walkStepInterval;
                }
            }
            else
            {
                stepTimer = 0f;   // so the first step plays instantly when movement resumes
            }
        }

        private void PlayRandom(AudioClip[] clips, float volume)
        {
            if (clips == null || clips.Length == 0) return;
            PlayAtMe(clips[Random.Range(0, clips.Length)], volume);
        }

        // 3D positional play AT THE PLAYER'S position. Other co-op players hear it directionally
        // with distance falloff; the local player still hears their own (distance ~0 = full volume).
        private void PlayAtMe(AudioClip clip, float volume)
        {
            if (clip == null || AudioManager.Instance == null) return;
            AudioManager.Instance.PlaySfx3D(clip, transform.position, volume, maxAudibleDistance);
        }
    }
}
