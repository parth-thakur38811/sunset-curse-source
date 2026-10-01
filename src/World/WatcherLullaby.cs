using UnityEngine;
using SunsetCurse.AI;
using SunsetCurse.Audio;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// The watcher's tell: a faint lullaby / female humming that fades in when the LOCAL player's
    /// ear (the camera) comes within her radius, swelling the closer you get to HER.
    ///
    /// Deliberately 2D — it seems to come from everywhere at once, so it warns you she's near
    /// WITHOUT pinpointing her (she's the audio-only huntress; a positional hum would spoil the
    /// compound's stealth puzzle). Night-only, because she shelters by day.
    ///
    /// Purely local: her position replicates via NetworkTransform and the clock phase is shared,
    /// so every peer computes its own volume — nothing to network here.
    ///
    /// SETUP: empty GameObject in the gameplay scene (e.g. "WatcherLullaby") + this script + drop
    /// a humming/lullaby loop into Lullaby Clip. Trigger Radius 0 = auto (her territory radius).
    /// </summary>
    public class WatcherLullaby : MonoBehaviour
    {
        [Tooltip("The looping hum/lullaby. Leave empty = silent (no errors).")]
        [SerializeField] private AudioClip lullabyClip;
        [Range(0f, 1f)]
        [SerializeField] private float maxVolume = 0.55f;
        [Tooltip("Distance from the WATCHER at which the humming becomes audible. 0 = use her " +
                 "territory radius automatically (the compound zone — her 'detect radius').")]
        [SerializeField] private float triggerRadius = 0f;
        [Tooltip("This close to her, the humming reaches full volume.")]
        [SerializeField] private float fullVolumeDistance = 12f;
        [Tooltip("How fast the volume eases in/out, in volume-per-second.")]
        [SerializeField] private float fadeSpeed = 0.5f;

        private AudioSource source;

        private void Start()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;   // everywhere-and-nowhere, on purpose
            source.volume = 0f;
            if (AudioManager.Instance != null)
                source.outputAudioMixerGroup = AudioManager.Instance.SfxGroup;
        }

        private void Update()
        {
            float target = 0f;

            var watcher = CompoundWatcherAI.Instance;
            bool night = GameClock.Instance == null ||
                         GameClock.Instance.CurrentPhase == GameClock.Phase.Night;
            // The camera is "the ear" — correct for the live player AND for spectators.
            var ear = Camera.main != null ? Camera.main.transform : null;

            if (night && watcher != null && ear != null && lullabyClip != null)
            {
                float radius = triggerRadius > 0f
                    ? triggerRadius
                    : Mathf.Max(1f, watcher.TerritoryRadius);
                float dist = Vector3.Distance(ear.position, watcher.transform.position);

                // 0 at the edge of her radius → maxVolume at fullVolumeDistance from her.
                if (dist <= radius)
                    target = maxVolume * Mathf.Clamp01(Mathf.InverseLerp(radius, fullVolumeDistance, dist));
            }

            source.volume = Mathf.MoveTowards(source.volume, target, fadeSpeed * Time.deltaTime);

            if (source.volume > 0.001f)
            {
                if (!source.isPlaying)
                {
                    source.clip = lullabyClip;
                    source.Play();
                }
            }
            else if (source.isPlaying && target <= 0f)
            {
                source.Stop();
            }
        }
    }
}
