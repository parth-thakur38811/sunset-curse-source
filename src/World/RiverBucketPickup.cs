using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// The world bucket pickup. Press E to grab it; the grant is server-validated through
    /// <see cref="RiverWaterBucket"/> (first player to grab wins). Added in code by the
    /// spawner — no prefab wiring.
    /// </summary>
    public class RiverBucketPickup : Interactable
    {
        private int generation;

        public void Configure(int gen) => generation = gen;

        public override string Verb => "pick up the bucket";

        public override bool CanInteract(GameObject interactor)
            => PlayerInventory.Local == null || !PlayerInventory.Local.HasBucket;

        public override void Interact(GameObject interactor)
        {
            if (RiverWaterBucket.Instance != null)
                RiverWaterBucket.Instance.RequestPickup(generation);
        }
    }
}
