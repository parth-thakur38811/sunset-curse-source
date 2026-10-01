using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// The compound gate is now OPEN AT ALL TIMES — by design (2026-07-03): the compound started
    /// out day-locked, players discovered they could get in by day anyway, and the team decided
    /// they LIKE it that way. So this component no longer blocks anything and no longer shows the
    /// old "Cannot enter during daytime" popup.
    ///
    /// It stays in the scene (rather than being deleted) so the gate object keeps working without
    /// any editor surgery: on Start it simply disables the old blocking collider + the closed-gate
    /// visual, then goes to sleep.
    /// </summary>
    public class DaytimeGate : MonoBehaviour
    {
        [Tooltip("The old solid blocking collider. Auto-found (first non-trigger collider) if empty. " +
                 "Disabled on Start so the gate is always passable.")]
        [SerializeField] private Collider barrier;

        [Tooltip("Optional: the old closed-gate visual. Hidden on Start (the gate never closes).")]
        [SerializeField] private GameObject closedGateVisual;

        private void Start()
        {
            // Disable every solid collider on this gate object — the gate is decorative now.
            if (barrier != null) barrier.enabled = false;
            foreach (var c in GetComponents<Collider>())
                if (!c.isTrigger) c.enabled = false;

            if (closedGateVisual != null) closedGateVisual.SetActive(false);
        }
    }
}
