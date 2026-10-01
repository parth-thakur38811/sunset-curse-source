using UnityEngine;
using UnityEngine.AI;
using SunsetCurse.Core;

namespace SunsetCurse.AI
{
    /// <summary>
    /// 3D positional footstep audio FOR the monster. As she moves, footstep one-shots fire at a
    /// cadence — walking is slow + soft, sprinting is fast + harsher. Plays in WORLD SPACE, so you
    /// hear which direction she's coming from before you see her.
    ///
    /// SETUP: put this on the same GameObject as MonsterAI (so it can read the NavMeshAgent
    /// velocity). Drop your walk and run footstep clips into the arrays (several variations each
    /// is best — it'll pick at random for variety). Leave Footstep Source EMPTY and the script
    /// will create a 3D AudioSource for you; fill it in if you want to wire a Mixer Group manually.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class MonsterFootsteps : MonoBehaviour
    {
        [Header("Audio")]
        [Tooltip("Optional pre-configured AudioSource (3D). Leave EMPTY and one will be created.")]
        [SerializeField] private AudioSource footstepSource;
        [Tooltip("Random pool of WALK footsteps (soft, slow). Several variations sound less robotic.")]
        [SerializeField] private AudioClip[] walkClips;
        [Tooltip("Random pool of RUN footsteps (heavier, faster).")]
        [SerializeField] private AudioClip[] runClips;
        [Range(0f, 1f)]
        [SerializeField] private float volume = 0.8f;

        [Header("Cadence")]
        [Tooltip("Seconds between footsteps while walking. Lower = faster steps.")]
        [SerializeField] private float walkStepInterval = 0.55f;
        [Tooltip("Seconds between footsteps while running.")]
        [SerializeField] private float runStepInterval = 0.32f;
        [Tooltip("Agent speed above this counts as 'running' for cadence selection.")]
        [SerializeField] private float runSpeedThreshold = 4f;
        [Tooltip("Below this speed she's basically still — no footsteps.")]
        [SerializeField] private float minSpeedToStep = 0.3f;

        [Header("3D falloff")]
        [Tooltip("How far away her footsteps can still be heard.")]
        [SerializeField] private float maxAudibleDistance = 35f;

        private NavMeshAgent agent;
        private float stepTimer;
        private bool lastWasRun;
        private Vector3 prevPos;
        private bool prevPosCaptured;

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            EnsureAudioSource();
        }

        private void EnsureAudioSource()
        {
            if (footstepSource == null) footstepSource = gameObject.AddComponent<AudioSource>();
            footstepSource.spatialBlend = 1f;                 // fully 3D — direction is the whole point
            footstepSource.playOnAwake = false;
            footstepSource.loop = false;
            footstepSource.rolloffMode = AudioRolloffMode.Linear;
            footstepSource.minDistance = 1f;
            footstepSource.maxDistance = maxAudibleDistance;
            footstepSource.volume = volume;
        }

        private void Update()
        {
            if (footstepSource == null) return;

            // Speed from TRANSFORM MOVEMENT, not agent.velocity — the NavMeshAgent is DISABLED on
            // every client (they follow the replicated NetworkTransform), so agent.velocity is 0
            // there and clients would never hear her. Transform deltas work on every peer.
            float speed = 0f;
            if (prevPosCaptured)
            {
                Vector3 d = transform.position - prevPos;
                d.y = 0f;
                speed = d.magnitude / Mathf.Max(0.0001f, Time.deltaTime);
            }
            prevPos = transform.position;
            prevPosCaptured = true;

            if (speed < minSpeedToStep)
            {
                // Stopped — reset so she doesn't double-tap a step the instant she moves again.
                stepTimer = 0f;
                return;
            }

            bool running = speed >= runSpeedThreshold;

            // Reset cadence timer cleanly when switching between walk and run.
            if (running != lastWasRun) { stepTimer = 0f; lastWasRun = running; }

            stepTimer -= Time.deltaTime;
            if (stepTimer > 0f) return;

            AudioClip[] pool = running ? runClips : walkClips;
            if (pool != null && pool.Length > 0)
            {
                AudioClip c = pool[Random.Range(0, pool.Length)];
                if (c != null)
                {
                    // Rain muffles HER footsteps too — distance falloff already in the AudioSource
                    // settings, but scale the per-shot volume down when raining so it reads quieter
                    // even at the same distance.
                    footstepSource.PlayOneShot(c, volume * Weather.NoiseMultiplier);
                }
            }

            stepTimer = running ? runStepInterval : walkStepInterval;
        }
    }
}
