using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.Player;
using SunsetCurse.UI;

namespace SunsetCurse.World
{
    /// <summary>
    /// The daily first-aid kit. Press E to heal a chunk of HP — but it's SHARED: the first
    /// player to claim it wins (server-validated through Inventory) and it despawns for the
    /// whole team. Placed by <see cref="BandageSpawner"/> (one per dawn, same spot on every peer).
    ///
    /// SETUP: drop on a small 3D model prefab. Needs a Collider so the interaction raycast hits it.
    /// </summary>
    public class BandagePickup : Interactable
    {
        [Tooltip("How much HP to restore. PlayerStats clamps at maxHealth, so generous is fine.")]
        [SerializeField] private float healAmount = 30f;

        private int day;   // which dawn's kit this is — set by BandageSpawner

        public void Configure(int d) => day = d;

        public override string Verb => "use bandage";

        public override void Interact(GameObject interactor)
        {
            // First-to-claim wins; the server arbitrates. The prop is NOT destroyed here — it
            // vanishes on every peer via Inventory.OnBandageTaken (BandageSpawner listens), so
            // a player who loses the race doesn't keep a ghost copy.
            Inventory.RequestTakeBandage(day, healAmount);
        }

        /// <summary>Runs only on the WINNER's machine (targeted ClientRpc from Inventory).</summary>
        public static void GrantLocal(float heal)
        {
            var inv = PlayerInventory.Local;
            var ps = inv != null ? inv.GetComponent<PlayerStats>() : null;
            if (ps == null) ps = Object.FindAnyObjectByType<PlayerStats>();   // SP fallback
            if (ps == null) return;
            ps.Heal(heal);
            ScreenMessage.Show($"+{heal:0} HP", 1.5f);
        }
    }
}
