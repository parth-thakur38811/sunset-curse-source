using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.Audio;

namespace SunsetCurse.World
{
    /// <summary>
    /// Haunted river audio, LOCAL per client (each peer reacts to their OWN player — the
    /// WatcherLullaby pattern; nothing is network-synced because everyone's in/out-of-water
    /// state is their own):
    ///
    ///   • Stepping into the river at NIGHT → one-shot entry sting + a looping haunted
    ///     ambience fades in.
    ///   • Leaving the water (any time) → one-shot exit splash; the ambience fades out.
    ///   • Dawn while still in the water → the ambience fades out; nightfall while wading
    ///     fades it back in.
    ///
    /// Every clip/source below is an OPTIONAL placeholder — leave them empty until you have
    /// audio; all paths are null-guarded.
    ///
    /// SETUP: lives on the "RiverBucket" GameObject (Tools ▸ Sunset Curse ▸ 24).
    /// </summary>
    public class RiverAmbience : MonoBehaviour
    {
        [Header("River SFX — drop clips here")]
        [Tooltip("One-shot the moment the local player steps into the river AT NIGHT.")]
        [SerializeField] private AudioClip riverEnterNightSfx;
        [Tooltip("One-shot when the local player leaves the water (day or night).")]
        [SerializeField] private AudioClip riverExitSfx;
        [Tooltip("Looping haunted ambience while the local player is in the water at night. " +
                 "Fades in on entry, fades out on exit / at dawn.")]
        [SerializeField] private AudioClip riverHauntedAmbienceLoop;
        [Tooltip("Dedicated looping source for the ambience (3D). Leave EMPTY and one is " +
                 "created at runtime that follows the player; assign your own to customise " +
                 "rolloff / mixer routing.")]
        [SerializeField] private AudioSource riverAmbienceSource;

        [Header("Mix")]
        [Tooltip("Full volume of the haunted ambience loop once faded in.")]
        [Range(0f, 1f)]
        [SerializeField] private float ambienceVolume = 0.8f;
        [Tooltip("Fade speed in volume-per-second (0.8 ≈ just over a second each way).")]
        [SerializeField] private float fadeSpeed = 0.8f;

        private bool wasInWater;
        private bool sourceIsRuntime;

        private void Update()
        {
            // Whose ears? The local player's body; camera as a fallback (spectators still count).
            Transform ears = PlayerInventory.Local != null ? PlayerInventory.Local.transform
                           : Camera.main != null ? Camera.main.transform : null;
            var river = RiverFlowController.Instance;

            bool inWater = ears != null && river != null && river.IsInWater(ears.position);
            bool night = GameClock.Instance != null
                      && GameClock.Instance.CurrentPhase == GameClock.Phase.Night;

            // Edge transitions → one-shots.
            if (inWater && !wasInWater && night) PlayOneShot(riverEnterNightSfx);
            if (!inWater && wasInWater)          PlayOneShot(riverExitSfx);
            wasInWater = inWater;

            // Looping ambience: audible only while in the water at night; fades both ways.
            UpdateLoop(inWater && night, ears);
        }

        private void UpdateLoop(bool wantOn, Transform ears)
        {
            if (riverHauntedAmbienceLoop == null && riverAmbienceSource == null) return;

            if (wantOn && riverAmbienceSource == null) CreateRuntimeSource();
            if (riverAmbienceSource == null) return;

            // The runtime source follows the player so the 3D loop never falls out of earshot.
            if (sourceIsRuntime && ears != null) riverAmbienceSource.transform.position = ears.position;

            float target = wantOn ? ambienceVolume : 0f;
            riverAmbienceSource.volume =
                Mathf.MoveTowards(riverAmbienceSource.volume, target, fadeSpeed * Time.deltaTime);

            if (wantOn && !riverAmbienceSource.isPlaying)
            {
                if (riverAmbienceSource.clip == null) riverAmbienceSource.clip = riverHauntedAmbienceLoop;
                if (riverAmbienceSource.clip != null)
                {
                    riverAmbienceSource.loop = true;
                    riverAmbienceSource.Play();
                }
            }
            else if (!wantOn && riverAmbienceSource.isPlaying && riverAmbienceSource.volume <= 0.001f)
            {
                riverAmbienceSource.Stop();
            }
        }

        private void CreateRuntimeSource()
        {
            var go = new GameObject("RiverAmbienceSource");
            go.transform.SetParent(transform, false);
            riverAmbienceSource = go.AddComponent<AudioSource>();
            riverAmbienceSource.playOnAwake = false;
            riverAmbienceSource.loop = true;
            riverAmbienceSource.volume = 0f;
            riverAmbienceSource.spatialBlend = 1f;   // 3D, but it follows the player (always near)
            riverAmbienceSource.dopplerLevel = 0f;
            riverAmbienceSource.minDistance = 3f;
            riverAmbienceSource.maxDistance = 30f;
            riverAmbienceSource.rolloffMode = AudioRolloffMode.Linear;
            if (AudioManager.Instance != null && AudioManager.Instance.SfxGroup != null)
                riverAmbienceSource.outputAudioMixerGroup = AudioManager.Instance.SfxGroup;
            sourceIsRuntime = true;
        }

        private void PlayOneShot(AudioClip clip)
        {
            if (clip == null) return;
            if (AudioManager.Instance != null) AudioManager.Instance.PlaySfx2D(clip);
            else if (riverAmbienceSource != null) riverAmbienceSource.PlayOneShot(clip);
        }
    }
}
