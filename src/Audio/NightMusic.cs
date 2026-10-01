using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.Audio
{
    /// <summary>
    /// Plays a looping music bed the moment NIGHT falls, and fades it back out at dawn.
    ///
    /// Purely local/cosmetic — the GameClock phase is already replicated to every peer, so
    /// everyone hears the change together without any networking here. Uses its OWN AudioSource
    /// (routed to the AudioManager's Music mixer group when one exists) instead of the shared
    /// ambient bed, so it can fade without fighting anything else.
    ///
    /// SETUP: empty GameObject in the gameplay scene (e.g. "NightMusic") + this script + drop
    /// your night track into Night Theme. That's it.
    /// </summary>
    public class NightMusic : MonoBehaviour
    {
        [Tooltip("The looping track that starts as night begins. Leave empty = silence (no errors).")]
        [SerializeField] private AudioClip nightTheme;
        [Range(0f, 1f)]
        [SerializeField] private float volume = 0.5f;
        [Tooltip("Seconds the track takes to fade in at dusk / fade out at dawn.")]
        [SerializeField] private float fadeSeconds = 4f;

        private AudioSource source;
        private float targetVolume;
        private bool subscribed;

        private void Start()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;   // 2D: fills the ears evenly, no falloff
            source.volume = 0f;
            if (AudioManager.Instance != null)
                source.outputAudioMixerGroup = AudioManager.Instance.MusicGroup;

            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart += HandleNight;
                GameClock.Instance.OnDayStart += HandleDay;
                subscribed = true;

                // Joined / respawned into an already-running night? Start immediately.
                if (GameClock.Instance.CurrentPhase == GameClock.Phase.Night) HandleNight(0);
            }
        }

        private void OnDestroy()
        {
            if (subscribed && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart -= HandleNight;
                GameClock.Instance.OnDayStart -= HandleDay;
            }
        }

        private void HandleNight(int day)
        {
            if (nightTheme == null) return;
            targetVolume = volume;
            if (!source.isPlaying)
            {
                source.clip = nightTheme;
                source.Play();
            }
        }

        private void HandleDay(int day) => targetVolume = 0f;

        private void Update()
        {
            if (source == null || !source.isPlaying) return;

            // Ease toward the target volume — fade-in at dusk, fade-out at dawn.
            float rate = fadeSeconds > 0.01f ? (volume / fadeSeconds) : 999f;
            source.volume = Mathf.MoveTowards(source.volume, targetVolume, rate * Time.deltaTime);

            if (targetVolume <= 0f && source.volume <= 0.001f)
                source.Stop();   // fully faded out — silence until the next dusk
        }
    }
}
