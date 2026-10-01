using System;
using UnityEngine;

namespace SunsetCurse.Audio
{
    /// <summary>
    /// Persistent player settings, saved with PlayerPrefs so they survive between sessions and
    /// scene loads. This is the single source of truth for values like audio volumes and look
    /// sensitivity: the <see cref="AudioManager"/> reads volumes here on boot, the
    /// <see cref="SunsetCurse.Player.SensitivityApplier"/> reads sensitivity, and the Settings UI
    /// (MainMenu + in-game Pause) writes back via setters.
    ///
    /// Volumes are stored as LINEAR 0..1 (0 = silent, 1 = full). The AudioManager converts them
    /// to decibels for the AudioMixer — keep that conversion in one place so the UI stays simple.
    ///
    /// Sensitivity is the look-rotation multiplier (1.0 = StarterAssets default).
    /// </summary>
    public static class GameSettings
    {
        // PlayerPrefs keys kept private so nothing else hard-codes the strings.
        private const string MasterKey      = "vol_master";
        private const string MusicKey       = "vol_music";
        private const string SfxKey         = "vol_sfx";
        private const string SensitivityKey = "look_sensitivity";

        /// <summary>Fires whenever Sensitivity is set — Player listeners re-apply to FPS controller.</summary>
        public static event Action OnSensitivityChanged;

        // Defaults tuned for a horror game: music slightly under SFX so stings still cut through.
        public static float MasterVolume
        {
            get => PlayerPrefs.GetFloat(MasterKey, 1f);
            set => PlayerPrefs.SetFloat(MasterKey, Mathf.Clamp01(value));
        }

        public static float MusicVolume
        {
            get => PlayerPrefs.GetFloat(MusicKey, 0.8f);
            set => PlayerPrefs.SetFloat(MusicKey, Mathf.Clamp01(value));
        }

        public static float SfxVolume
        {
            get => PlayerPrefs.GetFloat(SfxKey, 1f);
            set => PlayerPrefs.SetFloat(SfxKey, Mathf.Clamp01(value));
        }

        /// <summary>Look sensitivity multiplier. 1.0 = StarterAssets default. Range 0.1–3.0 is sensible.</summary>
        public static float Sensitivity
        {
            get => PlayerPrefs.GetFloat(SensitivityKey, 1f);
            set
            {
                PlayerPrefs.SetFloat(SensitivityKey, Mathf.Clamp(value, 0.1f, 3.0f));
                OnSensitivityChanged?.Invoke();
            }
        }

        /// <summary>Flush any staged PlayerPrefs changes to disk now.</summary>
        public static void Save() => PlayerPrefs.Save();
    }
}
