using UnityEngine;
using UnityEngine.Audio;

namespace SunsetCurse.Audio
{
    /// <summary>
    /// The game's single audio hub. It:
    ///   - survives scene loads (DontDestroyOnLoad) so music doesn't restart between menu and game,
    ///   - plays the slow, constant horror "ambient bed",
    ///   - routes everything through an AudioMixer so the Settings sliders can control
    ///     Master / Music / SFX volumes independently,
    ///   - offers <see cref="PlaySfx2D"/> for simple non-positional one-shots (UI clicks, a jump
    ///     grunt, etc.).
    ///
    /// Positional SFX (footsteps) will use their own AudioSources on the player later — just set
    /// their Output to the SFX group so they obey the volume slider too.
    ///
    /// ── ONE-TIME EDITOR SETUP (do this in the MainMenu scene so it boots first) ─────────────
    ///   1. Create an AudioMixer:  Project window ▸ right-click ▸ Create ▸ Audio Mixer ▸ "GameMix".
    ///   2. Open it (double-click). Under "Master", add two child groups (the + on the Groups
    ///      header): rename them "Music" and "SFX".
    ///   3. For EACH of Master, Music, SFX: click the group, then in the Inspector right-click its
    ///      "Volume" ▸ "Expose 'Volume (of ...)' to script". Open the "Exposed Parameters" dropdown
    ///      (top-right of the Audio Mixer window) and rename them to exactly:
    ///         MasterVol , MusicVol , SfxVol
    ///   4. Make an empty GameObject "AudioManager", add this script, and assign Mixer + the Music
    ///      and SFX groups. Leave the ambient clip empty for now (placeholder).
    /// ─────────────────────────────────────────────────────────────────────────────────────────
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        /// <summary>The Music mixer group — extra music layers (NightMusic) route through this so
        /// the Settings music slider controls them too. Null until assigned in the Inspector.</summary>
        public AudioMixerGroup MusicGroup => musicGroup;

        /// <summary>The SFX mixer group — gameplay audio layers (WatcherLullaby) route through
        /// this so the Settings SFX slider controls them too.</summary>
        public AudioMixerGroup SfxGroup => sfxGroup;

        [Header("Mixer")]
        [SerializeField] private AudioMixer mixer;
        [SerializeField] private AudioMixerGroup musicGroup;
        [SerializeField] private AudioMixerGroup sfxGroup;

        [Tooltip("Exposed mixer parameter names — must match exactly what you named them in the mixer.")]
        [SerializeField] private string masterParam = "MasterVol";
        [SerializeField] private string musicParam  = "MusicVol";
        [SerializeField] private string sfxParam    = "SfxVol";

        [Header("Ambient bed (placeholder — drop a clip in later)")]
        [Tooltip("The slow, constant horror background theme. Leave empty until you have a clip.")]
        [SerializeField] private AudioClip ambientTheme;
        [Range(0f, 1f)]
        [SerializeField] private float ambientVolume = 1f;

        private AudioSource ambientSource;   // the looping bed
        private AudioSource sfxSource;        // 2D one-shots (PlayOneShot)

        private void Awake()
        {
            // Single instance that survives scene changes.
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Looping ambient source → Music group.
            ambientSource = gameObject.AddComponent<AudioSource>();
            ambientSource.outputAudioMixerGroup = musicGroup;
            ambientSource.loop = true;
            ambientSource.playOnAwake = false;
            ambientSource.spatialBlend = 0f;     // 2D: fills the world evenly, no falloff

            // 2D one-shot source → SFX group.
            sfxSource = gameObject.AddComponent<AudioSource>();
            sfxSource.outputAudioMixerGroup = sfxGroup;
            sfxSource.playOnAwake = false;
            sfxSource.spatialBlend = 0f;
        }

        private void Start()
        {
            // Apply saved volumes AFTER Awake — pushing mixer params too early can silently fail.
            ApplyAllVolumes();

            if (ambientTheme != null)
            {
                ambientSource.clip = ambientTheme;
                ambientSource.volume = ambientVolume;
                ambientSource.Play();
            }
        }

        private void OnApplicationQuit() => GameSettings.Save();

        // ── Volume control (the Settings UI calls these) ────────────────────────────────────────
        // Each setter persists to GameSettings AND pushes to the mixer, so the UI is a one-liner.

        public void SetMasterVolume(float linear01)
        {
            GameSettings.MasterVolume = linear01;
            // AudioListener.volume is a global Unity volume multiplier — works WITHOUT any
            // AudioMixer setup, so the slider always changes audibly. The mixer call below
            // additionally scales the Master group when the user HAS configured the mixer.
            AudioListener.volume = Mathf.Clamp01(linear01);
            SetMixerVolume(masterParam, linear01);
        }

        public void SetMusicVolume(float linear01)
        {
            GameSettings.MusicVolume = linear01;
            SetMixerVolume(musicParam, linear01);
        }

        public void SetSfxVolume(float linear01)
        {
            GameSettings.SfxVolume = linear01;
            SetMixerVolume(sfxParam, linear01);
        }

        /// <summary>Re-push all saved volumes to the mixer (call after loading settings / on boot).</summary>
        public void ApplyAllVolumes()
        {
            AudioListener.volume = Mathf.Clamp01(GameSettings.MasterVolume);
            SetMixerVolume(masterParam, GameSettings.MasterVolume);
            SetMixerVolume(musicParam,  GameSettings.MusicVolume);
            SetMixerVolume(sfxParam,    GameSettings.SfxVolume);
        }

        private void SetMixerVolume(string param, float linear01)
        {
            if (mixer == null || string.IsNullOrEmpty(param)) return;
            // Convert linear 0..1 to decibels. log10(0) is -infinity, so floor silence at -80 dB.
            float dB = linear01 <= 0.0001f ? -80f : Mathf.Log10(Mathf.Clamp01(linear01)) * 20f;
            mixer.SetFloat(param, dB);
        }

        // ── Simple SFX ──────────────────────────────────────────────────────────────────────────

        /// <summary>Play a non-positional one-shot (UI click, jump grunt, etc.) on the SFX bus.</summary>
        public void PlaySfx2D(AudioClip clip, float volume = 1f)
        {
            if (clip != null) sfxSource.PlayOneShot(clip, volume);
        }

        /// <summary>
        /// Play a POSITIONAL one-shot at a world position (so the player hears a direction), routed
        /// through the SFX bus. Used for the monster's growls/scream and the fake "false cue" sounds.
        /// </summary>
        public void PlaySfx3D(AudioClip clip, Vector3 position, float volume = 1f, float maxDistance = 45f)
        {
            if (clip == null) return;
            var go = new GameObject("Sfx3D");
            go.transform.position = position;
            var src = go.AddComponent<AudioSource>();
            src.outputAudioMixerGroup = sfxGroup;
            src.clip = clip;
            src.volume = volume;
            src.spatialBlend = 1f;                       // fully 3D / positional
            src.minDistance = 4f;
            src.maxDistance = maxDistance;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.Play();
            Destroy(go, clip.length + 0.2f);
        }

        // ── Ambient control (used later by the day/night + monster music director) ───────────────

        public void PlayAmbient(AudioClip clip, float volume = 1f, bool skipIfSame = false)
        {
            if (clip == null) return;
            // Re-entering a scene with the same track shouldn't restart it from the top.
            if (skipIfSame && ambientSource.clip == clip && ambientSource.isPlaying)
            {
                ambientSource.volume = volume;
                return;
            }
            ambientSource.clip = clip;
            ambientSource.volume = volume;
            ambientSource.Play();
        }

        public void StopAmbient()
        {
            if (ambientSource != null) ambientSource.Stop();
        }
    }
}
