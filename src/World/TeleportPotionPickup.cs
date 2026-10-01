using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// The dawn potion pickup. Press E to grab it; the grant is server-validated through
    /// <see cref="DawnPotionSpawner"/> (first player to grab wins). Added in code by the spawner —
    /// no prefab wiring.
    /// </summary>
    public class TeleportPotionPickup : Interactable
    {
        private int generation;

        public void Configure(int gen) => generation = gen;

        public override string Verb =>
            SunsetCurse.Core.DifficultyPreference.Mode == SunsetCurse.Core.GameMode.Multiplayer
                ? "take the Revival Potion"
                : "take the Teleport Potion";

        public override void Interact(GameObject interactor)
        {
            if (DawnPotionSpawner.Instance != null)
                DawnPotionSpawner.Instance.RequestPickup(generation);
        }
    }
}
