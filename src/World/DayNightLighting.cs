using UnityEngine;
using UnityEngine.Rendering;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Makes the day/night cycle VISIBLE.
    ///
    /// Reads the <see cref="GameClock"/> every frame and:
    ///   - rotates the directional light (the sun) across the sky,
    ///   - brightens/warms it during the day, dims/cools it at night,
    ///   - blends the ambient light and fog so the whole scene gets dark.
    ///
    /// It does NOT keep its own timer — all timing comes from GameClock, so the
    /// visuals always match the real game state (and will match for all players
    /// once we add multiplayer).
    ///
    /// SETUP (in Unity):
    ///   1. Put this script on any GameObject (e.g. the "Sun" directional light, or
    ///      a "LightingManager" empty).
    ///   2. Drag your scene's Directional Light into the "Sun" field.
    ///   3. Make sure a GameClock exists in the scene.
    ///   4. Press Play — the sun will arc across the sky and night will fall.
    /// </summary>
    public class DayNightLighting : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The scene's main directional light (the sun/moon).")]
        [SerializeField] private Light sun;

        [Header("Sun direction")]
        [Tooltip("Left/right tilt of the sun's arc (degrees). Just for a nicer angle.")]
        [SerializeField] private float sunYaw = -30f;

        [Header("Sun colour & brightness")]
        [SerializeField] private Color dayColor = new Color(1f, 0.96f, 0.84f);   // warm white
        [SerializeField] private Color nightColor = new Color(0.5f, 0.6f, 0.9f); // cool moonlight
        [SerializeField] private float dayIntensity = 1.3f;
        // DARKENED 2026-07-08 for the Phasmophobia-style flashlight: the beam only reads as a hard
        // circle of light if the world OUTSIDE it is nearly black. (History: 0.08 → 0.13 → 0.05.)
        [SerializeField] private float nightIntensity = 0.05f;

        [Header("Ambient (overall scene light)")]
        [SerializeField] private Color dayAmbient = new Color(0.55f, 0.55f, 0.55f);
        // DARKENED 2026-07-08 (was 0.14/0.16/0.24): just enough deep blue to read silhouettes —
        // everything beyond the flashlight circle should feel like darkness.
        [SerializeField] private Color nightAmbient = new Color(0.045f, 0.05f, 0.085f);

        [Header("Fog (optional, helps sell the dark)")]
        [SerializeField] private bool controlFog = true;
        [SerializeField] private Color dayFog = new Color(0.7f, 0.75f, 0.8f);
        [SerializeField] private Color nightFog = new Color(0.02f, 0.03f, 0.06f);
        [Tooltip("Fog thickness (Exponential-Squared). Higher = you see less far. Thick night fog " +
                 "fades the world to black so the flashlight actually matters and night feels scary.")]
        [SerializeField] private float dayFogDensity = 0.004f;
        // THINNED 2026-07-08 (was 0.03): dense night fog ate the flashlight's long throw — objects
        // 40m+ inside the beam were fog-faded to nothing. Darkness now comes from the near-black
        // ambient/sky instead of fog, so the beam genuinely reaches its full range.
        [SerializeField] private float nightFogDensity = 0.012f;

        [Header("Sky brightness (the bright skybox is what keeps night feeling lit)")]
        [Tooltip("Dim the skybox itself at night. Needs a skybox with an _Exposure property " +
                 "(Unity's default Procedural skybox has one).")]
        [SerializeField] private bool controlSkybox = true;
        [SerializeField] private float daySkyExposure = 1.1f;
        // DARKENED 2026-07-08 (was 0.09): a bright sky silhouettes the world and undermines the
        // "darkness beyond the beam" look. Faint glow kept so the horizon isn't a void.
        [SerializeField] private float nightSkyExposure = 0.035f;

        [Tooltip("How sharply day flips to night around dawn/dusk. Higher = snappier.")]
        [SerializeField] private float transitionSharpness = 3f;

        private bool gameOver;
        private Material skyboxInstance;   // a private COPY so we never edit the skybox asset on disk

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

        private void Update()
        {
            var clock = GameClock.Instance;
            if (clock == null || sun == null) return;

            // 1) Work out where the sun is in the sky.
            //    Day:   pitch sweeps 0° -> 180° (rises, peaks at noon, sets).
            //    Night: pitch sweeps 180° -> 360° (travels below the world).
            float pitch;
            if (gameOver)
            {
                pitch = 270f; // straight down through the world = darkest point
            }
            else if (clock.CurrentPhase == GameClock.Phase.Day)
            {
                pitch = Mathf.Lerp(0f, 180f, clock.PhaseProgress01);
            }
            else
            {
                pitch = Mathf.Lerp(180f, 360f, clock.PhaseProgress01);
            }

            sun.transform.rotation = Quaternion.Euler(pitch, sunYaw, 0f);

            // 2) How much daylight is there right now? Based on the sun's height.
            //    Above horizon -> day; below -> night; smooth blend near dawn/dusk.
            float sunHeight = Mathf.Sin(pitch * Mathf.Deg2Rad);          // +1 noon, 0 horizon, -1 midnight
            float daylight = Mathf.Clamp01(0.5f + sunHeight * transitionSharpness);
            if (gameOver) daylight = 0f;

            // 3) Apply the blended look.
            sun.color = Color.Lerp(nightColor, dayColor, daylight);
            sun.intensity = Mathf.Lerp(nightIntensity, dayIntensity, daylight);
            RenderSettings.ambientLight = Color.Lerp(nightAmbient, dayAmbient, daylight);

            if (controlFog)
            {
                RenderSettings.fogColor = Color.Lerp(nightFog, dayFog, daylight);
                RenderSettings.fogDensity = Mathf.Lerp(nightFogDensity, dayFogDensity, daylight);
            }

            // Dim the sky dome at night so it stops washing the scene out.
            if (skyboxInstance != null)
                skyboxInstance.SetFloat("_Exposure", Mathf.Lerp(nightSkyExposure, daySkyExposure, daylight));
        }

        private void HandleGameOver() => gameOver = true;
    }
}
