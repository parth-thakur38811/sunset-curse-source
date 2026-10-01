using UnityEngine;
using SunsetCurse.AI;
using SunsetCurse.Core;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Continuous low heartbeat (or breathing) layer that gets LOUDER and FASTER as the monster
    /// closes in. This is the single biggest "I can't relax" mechanic in horror — even when you
    /// can't see her, your ears tell you she's near.
    ///
    /// Pairs with <see cref="MonsterProximityScream"/> (one-shot sting) — they layer naturally.
    ///
    /// SETUP: put this on the player (PlayerCapsule). Drop a heartbeat audio clip into "Heartbeat
    /// Clip" (any short loopable thump works — pulses around 60–80 BPM read as "tense", 100+ BPM
    /// reads as "panicked"). It auto-creates a 2D AudioSource configured for looping playback.
    /// Optional: drag a Mixer Group (your SFX bus) into the AudioSource in the Inspector after it's
    /// created so the Settings volume controls it.
    /// </summary>
    public class MonsterProximityHeartbeat : MonoBehaviour
    {
        [Header("Audio")]
        [Tooltip("Looping heartbeat / breathing clip. Mono works fine.")]
        [SerializeField] private AudioClip heartbeatClip;
        [Tooltip("Optional: pre-configured AudioSource. Leave EMPTY and the script will create one " +
                 "(2D, looping). Fill it in if you want to route through a Mixer manually.")]
        [SerializeField] private AudioSource heartbeatSource;

        [Header("Proximity range")]
        [Tooltip("She's first heard at this distance (volume ramps up from zero).")]
        [SerializeField] private float maxRange = 35f;
        [Tooltip("At this distance and closer, the heartbeat is at max volume and max pitch.")]
        [SerializeField] private float minRange = 5f;

        [Header("Intensity")]
        [Range(0f, 1f)]
        [SerializeField] private float maxVolume = 0.85f;
        [Tooltip("Pitch when she's far (just inside maxRange). 0.8 = slow, calm thump.")]
        [Range(0.5f, 1.5f)]
        [SerializeField] private float minPitch = 0.85f;
        [Tooltip("Pitch when she's right on top of you. 1.4 = frantic.")]
        [Range(0.5f, 2f)]
        [SerializeField] private float maxPitch = 1.5f;

        [Header("When")]
        [Tooltip("Only beat at night (when the monster is hunting). Recommended.")]
        [SerializeField] private bool nightOnly = true;

        private GameClock clock;

        private void Start()
        {
            clock = GameClock.Instance;
            EnsureAudioSource();
        }

        private void EnsureAudioSource()
        {
            if (heartbeatSource == null)
            {
                heartbeatSource = gameObject.AddComponent<AudioSource>();
            }
            heartbeatSource.clip = heartbeatClip;
            heartbeatSource.loop = true;
            heartbeatSource.playOnAwake = false;
            heartbeatSource.spatialBlend = 0f;          // 2D — it's in YOUR ears, not from a spot
            heartbeatSource.volume = 0f;
            heartbeatSource.pitch = minPitch;
        }

        private void Update()
        {
            if (heartbeatSource == null || heartbeatClip == null) return;

            bool nightActive = !nightOnly || (clock != null && clock.CurrentPhase == GameClock.Phase.Night);
            if (!nightActive)
            {
                Silence();
                return;
            }

            // NEAREST monster each frame — the stalker in the forest or the watcher in the
            // compound, whichever is closer drives your pulse.
            if (!MonsterRegistry.TryGetNearest(transform.position, out _, out float dist) ||
                dist > maxRange)
            {
                Silence();
                return;
            }

            // 0 at maxRange (far), 1 at minRange or closer (point-blank).
            float t = 1f - Mathf.Clamp01(Mathf.InverseLerp(minRange, maxRange, dist));
            heartbeatSource.volume = Mathf.Lerp(0f, maxVolume, t);
            heartbeatSource.pitch = Mathf.Lerp(minPitch, maxPitch, t);

            if (!heartbeatSource.isPlaying) heartbeatSource.Play();
        }

        private void Silence()
        {
            if (heartbeatSource.isPlaying) heartbeatSource.Stop();
            heartbeatSource.volume = 0f;
        }
    }
}
