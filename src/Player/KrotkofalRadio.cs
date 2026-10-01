using UnityEngine;
using UnityEngine.InputSystem;
using SunsetCurse.Core;
using SunsetCurse.World;

namespace SunsetCurse.Player
{
    /// <summary>
    /// The player's side of the radio escape. Auto-added to the PlayerCapsule by PlayerHealthSync.
    ///
    ///   R — use the krotkofal (handheld radio). While the tower is broken it answers with
    ///       static; once the battery + transformer are attached, R TRANSMITS THE SOS. The
    ///       ritual is NOT required first — the two escape objectives complete in any order,
    ///       and the rescue arrives once both are done (RadioTowerState owns that logic).
    ///   H — attach tower parts by LOOKING at the radio unit on the tower platform (the _Low.fbx
    ///       prop with a TowerRadioProp marker). While aimed at it, a prompt shows what to do;
    ///       one press fits whatever you carry (battery, transformer, or both) → "Attached!".
    /// </summary>
    public class KrotkofalRadio : MonoBehaviour
    {
        [Header("Keys")]
        [SerializeField] private Key radioKey = Key.R;
        [Tooltip("Attach key, used while LOOKING at the radio unit on the tower platform.")]
        [SerializeField] private Key attachKey = Key.H;

        [Header("Radio unit aiming")]
        [Tooltip("How far away the radio unit can be looked at / worked on, in metres.")]
        [SerializeField] private float lookRange = 4.5f;

        private Camera aimCamera;

        private void Update()
        {
            // Owner only.
            var inv = PlayerInventory.Local;
            if (inv == null || inv.gameObject != gameObject) return;
            if (Keyboard.current == null) return;
            if (!inv.IsAlive || inv.IsDowned) return;

            var state = RadioTowerState.Instance;
            if (state == null) return;   // radio escape not set up in this scene

            // Attaching the battery / transformer is now a normal E-interaction on the radio unit
            // (TowerRadioProp) — far easier to aim at. This component only handles R (send the SOS).
            if (Keyboard.current[radioKey].wasPressedThisFrame) UseRadio(inv, state);
        }

        // Camera-forward raycast for the radio unit prop — same aiming model as PlayerInteractor,
        // but on its own key so "work on the machine" feels distinct from generic E-interactions.
        private bool IsLookingAtRadioUnit()
        {
            if (aimCamera == null) aimCamera = Camera.main;
            if (aimCamera == null) return false;
            if (!Physics.Raycast(aimCamera.transform.position, aimCamera.transform.forward,
                                 out var hit, lookRange, ~0, QueryTriggerInteraction.Collide))
                return false;
            return hit.collider.GetComponentInParent<TowerRadioProp>() != null;
        }

        // Live prompt while aimed at the radio unit (re-shown each frame so it stays up).
        private void ShowRadioUnitPrompt(PlayerInventory inv, RadioTowerState state)
        {
            if (state.TowerFixed) return;   // nothing left to do here
            if (inv.HasBattery || inv.HasTransformer)
                SunsetCurse.UI.ScreenMessage.Show($"Press {attachKey} to attach item", 0.3f);
            else
                SunsetCurse.UI.ScreenMessage.Show("I need a Battery/Transformer!", 0.3f);
        }

        private void UseRadio(PlayerInventory inv, RadioTowerState state)
        {
            if (!inv.HasKrotkofal || state.Transmitted) return;

            if (!state.TowerFixed)
            {
                SunsetCurse.UI.ScreenMessage.Show(
                    "»kshhh...« Only static. The tower is dead — it needs a battery and a transformer.", 4f);
                return;
            }
            // NOTE: no ritual check here — the SOS can go out before the ritual. The rescue
            // itself only arrives once BOTH objectives are done (RadioTowerState decides).
            if (state.TransmitRadius > 0f && state.Tower != null &&
                Vector3.Distance(transform.position, state.Tower.position) > state.TransmitRadius)
            {
                SunsetCurse.UI.ScreenMessage.Show("No signal here — get closer to the radio tower.", 3f);
                return;
            }

            state.RequestTransmit();   // server validates once more; victory flows from there
        }

        private void TryAttach(PlayerInventory inv, RadioTowerState state)
        {
            if (state.TowerFixed) return;

            bool b = inv.HasBattery, t = inv.HasTransformer;
            if (!b && !t)
            {
                SunsetCurse.UI.ScreenMessage.Show("I need a Battery/Transformer!", 2.5f);
                return;
            }
            state.RequestAttach(b, t);   // server takes what's needed, echoes back what was consumed
        }
    }
}
