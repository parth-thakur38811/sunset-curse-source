using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Makes the day/night cycle VISIBLE.
    ///
    /// Reads the <see cref="GameClock"/> every frame and:
    ///   - rotates the directional light across the sky — the SUN by day, the MOON by night,
    ///   - brightens/warms it during the day, dims/cools it at night,
    ///   - blends the ambient light, fog and environment reflections so the whole scene gets dark,
    ///   - fades in a NIGHT COLOUR GRADE (a post-processing Volume profile) after dusk,
    ///   - reacts to rain (heavier fog, dimmer light) and the Blood Moon omen (red moonlight).
    ///
    /// It does NOT keep its own timer — all timing comes from GameClock, so the visuals always
    /// match the real game state on every peer.
    ///
    /// SETUP (in Unity):
    ///   1. Put this script on any GameObject (e.g. the "Sun" directional light, or
    ///      a "LightingManager" empty).
    ///   2. Drag your scene's Directional Light into the "Sun" field.
    ///   3. (Optional) drag a Volume Profile into "Night Grade Profile" for the night colour grade.
    ///   4. Make sure a GameClock exists in the scene and press Play.
    /// </summary>
    public class DayNightLighting : MonoBehaviour
    {
        /// <summary>0 = full daylight, 1 = full night. Other visual systems (mist, dust motes)
        /// read this instead of re-deriving the time of day.</summary>
        public static float Darkness01 { get; private set; }

        [Header("References")]
        [Tooltip("The scene's main directional light (the sun/moon).")]
        [SerializeField] private Light sun;

        [Header("Sun direction")]
        [Tooltip("Left/right tilt of the sun's arc (degrees). Just for a nicer angle.")]
        [SerializeField] private float sunYaw = -30f;

        [Header("Sun colour & brightness")]
        [SerializeField] private Color dayColor = new Color(1f, 0.96f, 0.84f);   // warm white
        [Tooltip("Night light colour — only used when Moonlight is OFF (the moon has its own colour).")]
        [SerializeField] private Color nightColor = new Color(0.5f, 0.6f, 0.9f);
        [SerializeField] private float dayIntensity = 1.3f;
        // DARKENED 2026-07-08 for the Phasmophobia-style flashlight: the beam only reads as a hard
        // circle of light if the world OUTSIDE it is nearly black. (History: 0.08 → 0.13 → 0.05.)
        [Tooltip("Night light intensity — only used when Moonlight is OFF.")]
        [SerializeField] private float nightIntensity = 0.05f;

        // ADDED 2026-10-01 (graphics pass): the night light used to keep sinking BELOW the world
        // (pitch 180→360), shining up through the ground — it lit nothing, cast no shadows. Now the
        // same light rises as a low MOON: cold, dim, and casting long soft tree shadows across the
        // forest floor. Darkness is preserved (intensity stays tiny); what changes is that the world
        // has SHAPE at night — silhouettes, pools of shadow, glints on the river.
        [Header("Moon (the night key light)")]
        [Tooltip("ON = at night the directional light becomes a moon arcing low across the sky.")]
        [SerializeField] private bool moonlight = true;
        [SerializeField] private Color moonColor = new Color(0.55f, 0.66f, 1f);
        [Tooltip("Moonlight strength. 0.3 = you can make out paths, buildings and trees, but detail and colour " +
                 "still need the flashlight. Lower = scarier but harder to navigate (0.14 was too dark).")]
        [SerializeField] private float moonIntensity = 0.3f;
        [Tooltip("Moon elevation (degrees above the horizon) at moonrise/moonset.")]
        [SerializeField] private float moonMinElevation = 14f;
        [Tooltip("Moon elevation at midnight. Low = longer, creepier shadows.")]
        [SerializeField] private float moonMaxElevation = 38f;
        [Tooltip("Compass direction of the moon at midnight (degrees).")]
        [SerializeField] private float moonYaw = 150f;
        [Tooltip("How far (degrees) the moon travels left→right over one night.")]
        [SerializeField] private float moonYawSweep = 70f;
        [Range(0f, 1f)]
        [Tooltip("Shadow darkness of moon shadows (1 = pitch black shadows).")]
        [SerializeField] private float moonShadowStrength = 0.85f;

        [Header("Blood Moon omen (OmenManager)")]
        [SerializeField] private Color bloodMoonColor = new Color(1f, 0.16f, 0.1f);
        [Tooltip("The blood moon is a little brighter than a normal moon — the red has to read.")]
        [SerializeField] private float bloodMoonIntensityMultiplier = 1.6f;
        [SerializeField] private Color bloodMoonFog = new Color(0.07f, 0.01f, 0.012f);
        [Tooltip("Night-grade colour filter while the Blood Moon is up.")]
        [SerializeField] private Color bloodMoonGradeFilter = new Color(1f, 0.7f, 0.68f);

        [Header("Ambient (overall scene light)")]
        [SerializeField] private Color dayAmbient = new Color(0.55f, 0.55f, 0.55f);
        // DARKENED 2026-07-08 (was 0.14/0.16/0.24): just enough deep blue to read silhouettes —
        // everything beyond the flashlight circle should feel like darkness.
        [SerializeField] private Color nightAmbient = new Color(0.11f, 0.12f, 0.17f);

        [Header("Fog (optional, helps sell the dark)")]
        [SerializeField] private bool controlFog = true;
        [SerializeField] private Color dayFog = new Color(0.7f, 0.75f, 0.8f);
        [SerializeField] private Color nightFog = new Color(0.05f, 0.06f, 0.09f);
        [Tooltip("Fog thickness (Exponential-Squared). Higher = you see less far. Thick night fog " +
                 "fades the world to black so the flashlight actually matters and night feels scary.")]
        [SerializeField] private float dayFogDensity = 0.004f;
        // THINNED 2026-07-08 (was 0.03): dense night fog ate the flashlight's long throw — objects
        // 40m+ inside the beam were fog-faded to nothing. Darkness now comes from the near-black
        // ambient/sky instead of fog, so the beam genuinely reaches its full range.
        [SerializeField] private float nightFogDensity = 0.012f;

        // ADDED 2026-10-01: WeatherController (Day-6 rain) and the Downpour omen set Weather.IsRaining,
        // but this script rewrote fog/ambient EVERY frame — so the rain look never actually showed.
        // Rain is now blended in here, where the per-frame values are owned.
        [Header("Rain (Day-6 rain + Downpour omen)")]
        [Tooltip("Fog density multiplier while it rains.")]
        [SerializeField] private float rainFogMultiplier = 2.2f;
        [Tooltip("Daytime fog colour while it rains (flat wet grey).")]
        [SerializeField] private Color rainDayFog = new Color(0.4f, 0.42f, 0.45f);
        [Range(0f, 1f)]
        [Tooltip("Sun/moon + ambient brightness multiplier in rain (overcast).")]
        [SerializeField] private float rainLightDim = 0.6f;

        [Header("Sky brightness (the bright skybox is what keeps night feeling lit)")]
        [Tooltip("Dim the skybox itself at night. Needs a skybox with an _Exposure property " +
                 "(Unity's default Procedural skybox has one).")]
        [SerializeField] private bool controlSkybox = true;
        [SerializeField] private float daySkyExposure = 1.1f;
        // DARKENED 2026-07-08 (was 0.09): a bright sky silhouettes the world and undermines the
        // "darkness beyond the beam" look. Faint glow kept so the horizon isn't a void.
        [SerializeField] private float nightSkyExposure = 0.08f;

        // ADDED 2026-10-01: the scene's reflection cubemap is captured from the DAYTIME sky, so every
        // glossy surface (wet rocks, metal, glass, the river) kept reflecting daylight at midnight.
        [Header("Environment reflections")]
        [SerializeField] private float dayReflectionIntensity = 1f;
        [SerializeField] private float nightReflectionIntensity = 0.2f;

        [Header("Night colour grade (post-processing)")]
        [Tooltip("Volume Profile faded in after dusk (weight = how dark it is). Colder, grainier, " +
                 "heavier vignette. Leave EMPTY for no night grade.")]
        [SerializeField] private VolumeProfile nightGradeProfile;
        [Tooltip("Must be above the scene's Global Volume priority (0).")]
        [SerializeField] private float nightGradePriority = 10f;

        [Tooltip("How sharply day flips to night around dawn/dusk. Higher = snappier.")]
        [SerializeField] private float transitionSharpness = 3f;

        private bool gameOver;
        private Material skyboxInstance;   // a private COPY so we never edit the skybox asset on disk
        private Volume nightVolume;
        private ColorAdjustments nightColorAdjust;   // lives on the volume's runtime profile COPY
        private Color nightBaseFilter = Color.white;
        private float bloodMoon01;   // smoothed 0..1 so the omen fades in instead of snapping
        private float rain01;

        private void Start()
        {
            // Make ambient colour controllable from script (instead of the skybox).
            RenderSettings.ambientMode = AmbientMode.Flat;

            if (controlFog)
            {
                RenderSettings.fog = true;
                // Density-based fog that closes in around the player — your scene's old Linear/300m
                // fog was effectively invisible, which is why dark night never read as dark.
                RenderSettings.fogMode = FogMode.ExponentialSquared;
            }

            // Work on a COPY of the skybox so dimming it at night never modifies the asset.
            if (controlSkybox && RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Exposure"))
            {
                skyboxInstance = new Material(RenderSettings.skybox);
                RenderSettings.skybox = skyboxInstance;
            }

            // Auto-find a directional light if none was assigned.
            if (sun == null)
            {
                foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
                {
                    if (l.type == LightType.Directional) { sun = l; break; }
                }
                if (sun == null)
                    Debug.LogWarning("[DayNightLighting] No directional light assigned or found.", this);
            }

            CreateNightGradeVolume();
        }

        // A runtime global Volume carrying the night grade. `volume.profile` (not sharedProfile)
        // hands back a private COPY, so tinting it for the Blood Moon never edits the asset on disk.
        private void CreateNightGradeVolume()
        {
            if (nightGradeProfile == null) return;
            var go = new GameObject("NightGradeVolume");
            go.transform.SetParent(transform, false);
            nightVolume = go.AddComponent<Volume>();
            nightVolume.isGlobal = true;
            nightVolume.priority = nightGradePriority;
            nightVolume.sharedProfile = nightGradeProfile;
            nightVolume.weight = 0f;
            if (nightVolume.profile.TryGet(out nightColorAdjust))
            {
                nightColorAdjust.colorFilter.overrideState = true;
                nightBaseFilter = nightColorAdjust.colorFilter.value;
            }
        }

        private void OnEnable()
        {
            // When the game is lost, lock the scene into permanent night.
            if (GameClock.Instance != null)
                GameClock.Instance.OnGameOver += HandleGameOver;
        }

        private void OnDisable()
        {
            if (GameClock.Instance != null)
                GameClock.Instance.OnGameOver -= HandleGameOver;
        }

        private void OnDestroy()
        {
            Darkness01 = 0f;   // static — don't hand a "night" value to the next scene
        }

        private void Update()
        {
            var clock = GameClock.Instance;
            if (clock == null || sun == null) return;

            bool isDay = clock.CurrentPhase == GameClock.Phase.Day;
            float progress = clock.PhaseProgress01;

            // 1) Where is the SUN?
            //    Day:   pitch sweeps 0° -> 180° (rises, peaks at noon, sets).
            //    Night: pitch sweeps 180° -> 360° (travels below the world).
            float pitch;
            if (gameOver)   pitch = 270f;   // straight down through the world = darkest point
            else if (isDay) pitch = Mathf.Lerp(0f, 180f, progress);
            else            pitch = Mathf.Lerp(180f, 360f, progress);

            // 2) How much daylight is there right now? Based on the sun's height.
            //    Above horizon -> day; below -> night; smooth blend near dawn/dusk.
            float sunHeight = Mathf.Sin(pitch * Mathf.Deg2Rad);          // +1 noon, 0 horizon, -1 midnight
            float daylight = Mathf.Clamp01(0.5f + sunHeight * transitionSharpness);
            if (gameOver) daylight = 0f;
            float night01 = 1f - daylight;
            Darkness01 = night01;

            // Omen + weather fade in/out over a few seconds instead of snapping.
            bloodMoon01 = Mathf.MoveTowards(bloodMoon01, OmenManager.IsBloodMoon ? 1f : 0f, Time.deltaTime / 4f);
            rain01      = Mathf.MoveTowards(rain01, Weather.IsRaining ? 1f : 0f, Time.deltaTime / 6f);
            float rainDim = Mathf.Lerp(1f, rainLightDim, rain01);

            // 3) Key light: sun by day, moon by night.
            Quaternion sunRot = Quaternion.Euler(pitch, sunYaw, 0f);
            Color keyColor;
            float keyIntensity;
            if (moonlight)
            {
                // Moon position along tonight's arc (0 = moonrise, 1 = moonset). During the DAY it
                // parks at moonset (morning) / moonrise (evening) so the dusk/dawn hand-over is
                // continuous; mid-day the jump is invisible because night01 ≈ 0 then.
                float moonP = gameOver ? 0.5f : (!isDay ? progress : (progress < 0.5f ? 1f : 0f));
                float elevation = Mathf.Lerp(moonMinElevation, moonMaxElevation, Mathf.Sin(moonP * Mathf.PI));
                float yaw = moonYaw + (moonP - 0.5f) * moonYawSweep;
                Quaternion moonRot = Quaternion.Euler(elevation, yaw, 0f);

                float swap = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.7f, night01));
                sun.transform.rotation = Quaternion.Slerp(sunRot, moonRot, swap);

                Color moonCol = Color.Lerp(moonColor, bloodMoonColor, bloodMoon01);
                float moonInt = moonIntensity * Mathf.Lerp(1f, bloodMoonIntensityMultiplier, bloodMoon01);
                keyColor = Color.Lerp(moonCol, dayColor, daylight);
                keyIntensity = Mathf.Lerp(moonInt, dayIntensity, daylight);
                // Dip the light while it swings from the setting sun over to the moon — hides the
                // few seconds of shadows sweeping across the ground at dusk/dawn.
                keyIntensity *= 1f - Mathf.Sin(swap * Mathf.PI) * 0.75f;
                sun.shadowStrength = Mathf.Lerp(moonShadowStrength, 1f, daylight);
            }
            else
            {
                sun.transform.rotation = sunRot;
                keyColor = Color.Lerp(nightColor, dayColor, daylight);
                keyIntensity = Mathf.Lerp(nightIntensity, dayIntensity, daylight);
            }
            sun.color = keyColor;
            sun.intensity = keyIntensity * rainDim;

            // 4) Ambient: blood moon stains the darkness red; rain greys it down.
            Color ambient = Color.Lerp(nightAmbient, dayAmbient, daylight);
            ambient = Color.Lerp(ambient, new Color(ambient.r * 1.8f, ambient.g * 0.55f, ambient.b * 0.55f),
                                 bloodMoon01 * night01);
            RenderSettings.ambientLight = ambient * Mathf.Lerp(1f, 0.8f, rain01);

            if (controlFog)
            {
                Color fog = Color.Lerp(nightFog, Color.Lerp(dayFog, rainDayFog, rain01), daylight);
                fog = Color.Lerp(fog, bloodMoonFog, bloodMoon01 * night01);
                RenderSettings.fogColor = fog;
                RenderSettings.fogDensity = Mathf.Lerp(nightFogDensity, dayFogDensity, daylight)
                                          * Mathf.Lerp(1f, rainFogMultiplier, rain01);
            }

            // Glossy surfaces stop reflecting the daytime sky after dark.
            RenderSettings.reflectionIntensity = Mathf.Lerp(nightReflectionIntensity, dayReflectionIntensity, daylight);

            // Dim the sky dome at night so it stops washing the scene out.
            if (skyboxInstance != null)
                skyboxInstance.SetFloat("_Exposure", Mathf.Lerp(nightSkyExposure, daySkyExposure, daylight) * rainDim);

            // 5) Night colour grade fades in with the dark; the Blood Moon tints it red.
            if (nightVolume != null)
            {
                nightVolume.weight = Mathf.SmoothStep(0f, 1f, night01);
                if (nightColorAdjust != null)
                    nightColorAdjust.colorFilter.value = Color.Lerp(nightBaseFilter, bloodMoonGradeFilter, bloodMoon01);
            }
        }

        private void HandleGameOver() => gameOver = true;
    }
}
