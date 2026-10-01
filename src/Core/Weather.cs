using System;
using UnityEngine;

namespace SunsetCurse.Core
{
    /// <summary>
    /// Static weather state shared across systems. Currently used by:
    ///   - <see cref="SunsetCurse.Player.PlayerNoise"/>: multiplies the player's noise radius by
    ///     <see cref="NoiseMultiplier"/> so rain hides their movement from the monster.
    ///   - <see cref="SunsetCurse.AI.MonsterFootsteps"/>: multiplies max audible distance so the
    ///     monster's footsteps are muffled too (rain works BOTH ways — you can't hear her
    ///     coming either).
    ///
    /// <see cref="SunsetCurse.World.WeatherController"/> sets the state in response to GameClock
    /// day-start events (rain begins on Day 6 by default).
    /// </summary>
    public static class Weather
    {
        public static bool IsRaining { get; private set; }
        /// <summary>1.0 = clear, lower = sounds carry less far. Rain default = 0.5.</summary>
        public static float NoiseMultiplier { get; private set; } = 1f;

        /// <summary>Fires whenever the state changes — UI / VFX / audio can subscribe.</summary>
        public static event Action OnChanged;

        public static void Set(bool raining, float noiseMultiplier)
        {
            bool changed = IsRaining != raining || !Mathf.Approximately(NoiseMultiplier, noiseMultiplier);
            IsRaining = raining;
            NoiseMultiplier = noiseMultiplier;
            if (changed) OnChanged?.Invoke();
        }
    }
}
