using UnityEngine;
using SunsetCurse.Audio;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Mirrors the saved <see cref="GameSettings.Sensitivity"/> onto the StarterAssets
    /// <c>FirstPersonController.RotationSpeed</c> field. Re-applies whenever the player changes
    /// the sensitivity slider (we listen to <see cref="GameSettings.OnSensitivityChanged"/>).
    ///
    /// Uses reflection so we don't take a hard compile-time dependency on StarterAssets —
    /// consistent with the rest of the codebase (PlayerStats, GameOverController also do this).
    ///
    /// SETUP: add this component to the PlayerCapsule prefab, NEXT TO FirstPersonController.
    /// No Inspector fields — it does the right thing automatically.
    /// </summary>
    public class SensitivityApplier : MonoBehaviour
    {
        private object fpsController;
        private System.Reflection.FieldInfo rotationSpeedField;

        private void Start()
        {
            CacheFpsControllerField();
            Apply();
            GameSettings.OnSensitivityChanged += Apply;
        }

        private void OnDestroy()
        {
            GameSettings.OnSensitivityChanged -= Apply;
        }

        private void CacheFpsControllerField()
        {
            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                if (mb.GetType().Name != "FirstPersonController") continue;

                fpsController = mb;
                rotationSpeedField = mb.GetType().GetField("RotationSpeed",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public);
                if (rotationSpeedField == null)
                {
                    Debug.LogWarning("[SensitivityApplier] Found FirstPersonController but no public " +
                                     "RotationSpeed field — sensitivity slider will have no effect.", this);
                }
                return;
            }
            Debug.LogWarning("[SensitivityApplier] No FirstPersonController on this GameObject — " +
                             "sensitivity slider will have no effect.", this);
        }

        private void Apply()
        {
            if (fpsController == null || rotationSpeedField == null) return;
            // StarterAssets' RotationSpeed is a float multiplier on the look input. Default 1.0.
            rotationSpeedField.SetValue(fpsController, GameSettings.Sensitivity);
        }
    }
}
