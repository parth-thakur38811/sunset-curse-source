using UnityEngine;
using SunsetCurse.Player;

namespace SunsetCurse.World
{
    /// <summary>
    /// A bush the player can eat from. Eating feeds the player (calls PlayerStats.EatFood),
    /// then the bush is bare for a while before its berries regrow — so it isn't infinite food.
    ///
    /// SETUP:
    ///   1. Put this on a bush object that has a Collider (so the raycast can hit it).
    ///   2. (Optional) Drag the berry/fruit child objects into "Berry Visuals" — they hide
    ///      while the bush is bare and reappear when it regrows.
    /// </summary>
    public class BerryBush : Interactable
    {
        [Header("Bush")]
        [Tooltip("Seconds before berries regrow after being eaten.")]
        [SerializeField] private float regrowSeconds = 25f;

        [Tooltip("Optional: berry/fruit meshes to hide while bare and show when grown.")]
        [SerializeField] private GameObject[] berryVisuals;

        private bool hasBerries = true;
        private float regrowTimer;

        private void Reset()
        {
            verb = "eat";   // default prompt becomes "Press E to eat"
        }

        /// <summary>
        /// Set up a bush that was created at runtime (e.g. by BerrySpawner). Reset() does NOT
        /// run for components added via AddComponent, so we configure it explicitly here.
        /// </summary>
        public void Configure(GameObject[] visuals, float regrow)
        {
            verb = "eat";
            berryVisuals = visuals;
            regrowSeconds = regrow;
            hasBerries = true;
            SetBerriesVisible(true);
        }

        public override bool CanInteract(GameObject interactor) => hasBerries;

        public override void Interact(GameObject interactor)
        {
            if (!hasBerries) return;

            // Feed whoever interacted (search parents so it works whether the collider is
            // on the player root or a child).
            var stats = interactor.GetComponentInParent<PlayerStats>();
            if (stats != null) stats.EatFood();

            hasBerries = false;
            regrowTimer = regrowSeconds;
            SetBerriesVisible(false);
        }

        private void Update()
        {
            if (hasBerries) return;

            regrowTimer -= Time.deltaTime;
            if (regrowTimer <= 0f)
            {
                hasBerries = true;
                SetBerriesVisible(true);
            }
        }

        private void SetBerriesVisible(bool visible)
        {
            if (berryVisuals == null) return;
            foreach (var g in berryVisuals)
                if (g != null) g.SetActive(visible);
        }
    }
}
