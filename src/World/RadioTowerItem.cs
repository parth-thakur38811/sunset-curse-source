using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// The nightly compound spawn of a radio part (battery / transformer / krotkofal). Pickup is
    /// validated PER PART on the server (the part must still be in the world), so two players
    /// pressing E on the same part in the same instant can't both claim it — and grabbing
    /// different parts never blocks anyone. Added in code by RadioItemSpawner — never placed by hand.
    /// </summary>
    public class RadioTowerItem : Interactable
    {
        private RadioItemKind kind;

        public void Configure(RadioItemKind kind)
        {
            this.kind = kind;
            verb = $"take the {RadioTowerState.NameOf(kind)}";
        }

        // Same night-only rule as the ritual potion (it despawns at dawn anyway — belt & braces).
        public override bool CanInteract(GameObject interactor)
        {
            var clock = GameClock.Instance;
            return clock == null || clock.CurrentPhase == GameClock.Phase.Night;
        }

        public override void Interact(GameObject interactor)
        {
            if (!CanInteract(interactor)) return;
            if (RadioItemSpawner.Instance != null)
                RadioItemSpawner.Instance.RequestPickup(kind);
        }
    }

    /// <summary>
    /// Findability dressing for a spawned radio part: a faint warm glow that only burns at NIGHT
    /// (when the parts are actually out and the forest is black), plus a slow spin so the model
    /// reads as "a pickup" and not scenery. Purely local/cosmetic — every peer dresses its own
    /// copy, nothing to network. Added in code by RadioTowerState.CreateItemVisual; the look is
    /// tuned on the RadioTowerEscape object's Inspector.
    /// </summary>
    public class RadioItemGlow : MonoBehaviour
    {
        private Light glow;
        private float spinSpeed;

        public void Configure(Color color, float intensity, float range, float spinDegreesPerSecond)
        {
            spinSpeed = spinDegreesPerSecond;
            if (intensity <= 0f) return;   // glow disabled in the Inspector

            var go = new GameObject("ItemGlow");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * 0.25f;
            glow = go.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = color;
            glow.intensity = intensity;
            glow.range = range;
            glow.shadows = LightShadows.None;
            glow.enabled = false;   // the Update reconcile flips it on at night
        }

        private void Update()
        {
            if (spinSpeed != 0f)
                transform.Rotate(0f, spinSpeed * Time.deltaTime, 0f, Space.World);

            // Night-only, reconciled every frame (same pattern as the monster's glow) — death-drops
            // can sit through a dawn, so the light must switch itself off in daylight.
            if (glow != null)
            {
                bool night = GameClock.Instance == null ||
                             GameClock.Instance.CurrentPhase == GameClock.Phase.Night;
                if (glow.enabled != night) glow.enabled = night;
            }
        }
    }

    /// <summary>
    /// A radio item lying on the ground because its carrier DIED. Pickup routes through
    /// RadioTowerState's replicated drop list (server removes the entry → every peer's visual
    /// despawns → the picker alone is granted the item). Added in code by RadioTowerState.
    /// </summary>
    public class RadioDropPickup : Interactable
    {
        private int dropId;

        public void Configure(int dropId, RadioItemKind kind)
        {
            this.dropId = dropId;
            verb = $"pick up the {RadioTowerState.NameOf(kind)}";
        }

        public override void Interact(GameObject interactor)
        {
            if (RadioTowerState.Instance != null)
                RadioTowerState.Instance.RequestPickupDrop(dropId);
        }
    }
}
