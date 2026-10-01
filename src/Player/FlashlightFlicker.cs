using UnityEngine;
using SunsetCurse.AI;
using SunsetCurse.Core;

namespace SunsetCurse.Player
{
    /// <summary>
    /// "The flashlight flickers when she's near." Modulates the flashlight's <see cref="Light"/>
    /// intensity with Perlin-noise-driven flicker whose STRENGTH scales with proximity to the
    /// monster — barely noticeable far away, violent when she's right on top of you.
    ///
    /// SETUP: put this on the player, next to <see cref="Flashlight"/>. No Inspector wiring needed
    /// — the script finds the runtime-created "Flashlight" Light under Camera.main automatically.
    /// </summary>
    public class FlashlightFlicker : MonoBehaviour
    {
        [Header("Proximity range")]
        [Tooltip("Flicker starts at this distance (very faint).")]
        [SerializeField] private float maxRange = 18f;
        [Tooltip("At this distance and closer, flicker is at full strength.")]
        [SerializeField] private float minRange = 3f;

        [Header("Flicker shape")]
        [Range(0f, 1f)]
        [Tooltip("Max amount the intensity can dip / spike, as a fraction of base. 0.7 = up to ±70%.")]
        [SerializeField] private float maxFlickerStrength = 0.7f;
        [Tooltip("How fast the flicker noise advances. Higher = jitterier.")]
        [SerializeField] private float flickerSpeed = 12f;

        [Header("When")]
        [Tooltip("Only flicker at night.")]
        [SerializeField] private bool nightOnly = true;

        private Light targetLight;
        private float baseIntensity;
        private bool baseCaptured;
        private float perlinSeed;
        private Transform monster;
        private GameClock clock;

        private void Start()
        {
            var ai = FindAnyObjectByType<MonsterAI>();
            if (ai != null) monster = ai.transform;
            clock = GameClock.Instance;
            perlinSeed = Random.value * 1000f;
        }

        private void Update()
        {
            // Late lookup — the Flashlight script creates the Light at runtime as a child of
            // Camera.main, so we may not see it the first frame.
            if (targetLight == null)
            {
                TryFindFlashlight();
                if (targetLight == null) return;
            }

            // Off (or no monster)? Restore baseline and bail.
            if (!targetLight.enabled || monster == null)
            {
                if (baseCaptured) targetLight.intensity = baseIntensity;
                return;
            }

            bool nightActive = !nightOnly || (clock != null && clock.CurrentPhase == GameClock.Phase.Night);
            if (!nightActive)
            {
                targetLight.intensity = baseIntensity;
                return;
            }

            float dist = Vector3.Distance(transform.position, monster.position);
            float proximity = 1f - Mathf.Clamp01(Mathf.InverseLerp(minRange, maxRange, dist));
            if (proximity <= 0f)
            {
                targetLight.intensity = baseIntensity;
                return;
            }

            // Perlin → 0..1, recentre to -1..1, scale by max strength and proximity.
            float noise = Mathf.PerlinNoise(Time.time * flickerSpeed, perlinSeed);
            float dip = (noise - 0.5f) * 2f * maxFlickerStrength * proximity;
            targetLight.intensity = Mathf.Max(0f, baseIntensity * (1f + dip));
        }

        private void TryFindFlashlight()
        {
            var cam = Camera.main;
            if (cam == null) return;

            // Flashlight.cs names its runtime light GameObject "Flashlight" under the main camera.
            var found = cam.transform.Find("Flashlight");
            if (found != null) targetLight = found.GetComponent<Light>();

            // Fallback: any Light child of the camera.
            if (targetLight == null) targetLight = cam.GetComponentInChildren<Light>(true);

            if (targetLight != null)
            {
                baseIntensity = targetLight.intensity;
                baseCaptured = true;
            }
        }
    }
}
