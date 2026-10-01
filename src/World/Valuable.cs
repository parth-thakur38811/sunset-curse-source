using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// A ritual potion the players must collect at night to break the curse. Each one is tied to
    /// a SPAWN GENERATION assigned by <see cref="NightValuableSpawner"/> when it's created — the
    /// server uses that generation to validate pickups (so two near-simultaneous E presses can't
    /// both claim the same potion).
    ///
    /// Picking up does NOT destroy locally — the server bumps the spawner's generation and every
    /// peer's HandleGenerationChanged tears down their copy in sync. That keeps the world view
    /// consistent across all peers.
    /// </summary>
    public class Valuable : Interactable
    {
        [Tooltip("How much this is worth toward the escape goal.")]
        [SerializeField] private int worth = 1;

        // Server-assigned identifier — the spawner gives us this in Configure(). The pickup RPC
        // sends this back so the server can reject stale grabs (e.g. someone right-clicks the
        // ghost of yesterday's potion).
        private int generation;

        private void Reset()
        {
            verb = "take the potion";
        }

        // Night-only: returns false during the day, so no prompt and no pickup.
        public override bool CanInteract(GameObject interactor)
        {
            var clock = GameClock.Instance;
            return clock == null || clock.CurrentPhase == GameClock.Phase.Night;
        }

        public override string Verb =>
            CanInteract(null) ? verb : "take the potion (only at night)";

        public override void Interact(GameObject interactor)
        {
            if (!CanInteract(interactor)) return;
            // Route through the SHARED, NETWORKED spawner — server picks the winner, broadcasts
            // despawn to every peer, and increments the shared EscapeTracker.
            if (NightValuableSpawner.Instance != null)
                NightValuableSpawner.Instance.RequestPickup(generation);
        }

        /// <summary>Spawner-only — sets worth + generation atomically when the Valuable is created.</summary>
        public void Configure(int worth, int generation)
        {
            this.worth = worth;
            this.generation = generation;
            verb = "take the potion";
        }

        /// <summary>Single-arg overload kept for any older callers; pairs the potion with
        /// generation 0 (won't be server-validated correctly, so prefer the 2-arg version).</summary>
        public void Configure(int worth) => Configure(worth, 0);
    }
}
