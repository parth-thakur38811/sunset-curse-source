using UnityEngine;
using SunsetCurse.Audio;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Switches on heavy rain at the start of <see cref="rainStartDay"/> (default Day 6) and keeps
    /// it raining for the rest of the run. Rain visual = denser fog + a darker ambient tint;
    /// rain gameplay = <see cref="Weather.NoiseMultiplier"/> drops to <see cref="rainNoiseMultiplier"/>
    /// (default 0.5), which BOTH:
    ///   - shrinks the player's <see cref="SunsetCurse.Player.PlayerNoise"/> radius (you can
    ///     sprint without the monster hearing you as far)
    ///   - AND shrinks <see cref="SunsetCurse.AI.MonsterFootsteps"/> audible distance (you also
    ///     can't hear HER coming from as far). The rain works both ways — that's the trade.
    ///
    /// SETUP:
    ///   1. Empty GameObject "WeatherController" + this script.
    ///   2. (Optional) drop a rain ambient loop into Rain Ambient Loop (any rainy mp3/wav).
    ///   3. Tune Rain Fog Density / Rain Noise Multiplier in the Inspector if you want a different
    ///      vibe.
    /// </summary>
    public class WeatherController : MonoBehaviour
    {
        [Header("When")]
        [Tooltip("Rain starts at the beginning of THIS day and never stops.")]
        [SerializeField] private int rainStartDay = 6;

        // NOTE (2026-10-01): DayNightLighting rewrites fog/ambient EVERY frame, so these one-shot
        // values only show for a frame. The visible rain look (heavier fog, dimmer light) now lives
        // in DayNightLighting ▸ Rain, which reacts to Weather.IsRaining — tune it there.
        [Header("Visuals")]
        [Tooltip("How thick the fog gets in rain. 0.04 is heavy and oppressive.")]
        [SerializeField] private float rainFogDensity = 0.04f;
        [SerializeField] private Color rainFogColor = new Color(0.35f, 0.36f, 0.4f, 1f);
        [Tooltip("Ambient light tint while it's raining (darker / cooler / blue-grey).")]
        [SerializeField] private Color rainAmbient = new Color(0.32f, 0.34f, 0.4f, 1f);

        [Header("Gameplay")]
        [Range(0f, 1f)]
        [Tooltip("All noise (player AND monster) scaled by this in rain. 0.5 = sounds carry half " +
                 "as far. Lower = more masking but also harder to hear the monster.")]
        [SerializeField] private float rainNoiseMultiplier = 0.5f;

        [Header("Audio (optional)")]
        [Tooltip("A looping rainfall sound. Played via AudioManager.PlayAmbient — leave empty if " +
                 "you don't have a clip yet.")]
        [SerializeField] private AudioClip rainAmbientLoop;

        private float originalFogDensity;
        private Color originalFogColor;
        private Color originalAmbient;
        private bool originalsSaved;
        private bool rainActive;

        private void Awake()
        {
            // Weather is a STATIC class — its values outlive the scene. Without this reset, a game
            // that reached the Day-6 rain (or ended during a Downpour omen) handed IsRaining=true
            // and noise ×0.5 to the NEXT game: dry-looking, but the monster heard you at half range.
            Weather.Set(raining: false, noiseMultiplier: 1f);
        }

        private void Start()
        {
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnDayStart += HandleDayStart;
                // Catch the case where we entered the scene AFTER Day 1's OnDayStart fired.
                if (GameClock.Instance.CurrentPhase == GameClock.Phase.Day)
                    HandleDayStart(GameClock.Instance.CurrentDay);
            }
        }

        private void OnDestroy()
        {
            if (GameClock.Instance != null) GameClock.Instance.OnDayStart -= HandleDayStart;
        }

        private void HandleDayStart(int day)
        {
            if (day >= rainStartDay && !rainActive) StartRain(day);
        }

        private void StartRain(int day)
        {
            if (!originalsSaved)
            {
                originalFogDensity = RenderSettings.fogDensity;
                originalFogColor = RenderSettings.fogColor;
                originalAmbient = RenderSettings.ambientLight;
                originalsSaved = true;
            }

            Weather.Set(raining: true, noiseMultiplier: rainNoiseMultiplier);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = rainFogDensity;
            RenderSettings.fogColor = rainFogColor;
            RenderSettings.ambientLight = rainAmbient;

            if (rainAmbientLoop != null && AudioManager.Instance != null)
            {
                AudioManager.Instance.PlayAmbient(rainAmbientLoop);
            }

            rainActive = true;
            Debug.Log($"[WeatherController] Heavy rain begins on Day {day}. " +
                      $"NoiseMultiplier = {rainNoiseMultiplier:0.00} (both player + monster muffled).", this);
        }
    }
}
