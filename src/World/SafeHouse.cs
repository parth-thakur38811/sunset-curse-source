using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// The spawn / safe house. While EVERY living player is inside its radius, the monster treats
    /// nobody as huntable → she retreats at least <see cref="monsterRetreatDistance"/> metres away
    /// and roams. The instant anyone steps out, she resumes the scent-driven hunt on the exposed
    /// player. Players inside are un-targetable and un-killable (a genuine safe zone to regroup).
    ///
    /// It's a simple XZ distance check (no collider / physics needed), read by MonsterAI on the host.
    ///
    /// SETUP:
    ///   1. Put this on the safe-house GameObject (or an empty at its centre).
    ///   2. Set Radius so the blue gizmo sphere covers the house interior (top-down).
    ///   3. Tune Monster Retreat Distance (default 60m — the orange gizmo).
    /// </summary>
    public class SafeHouse : MonoBehaviour
    {
        public static SafeHouse Instance { get; private set; }

        [Tooltip("Distance from the centre that counts as 'inside the safe house', in metres (XZ).")]
        [SerializeField] private float radius = 8f;
        [Tooltip("While everyone is inside, the monster keeps at least this far from the safe house.")]
        [SerializeField] private float monsterRetreatDistance = 60f;
        [Tooltip("Optional centre override. Leave empty to use this object's position.")]
        [SerializeField] private Transform centerOverride;

        public Vector3 Center => centerOverride != null ? centerOverride.position : transform.position;
        public float RetreatDistance => monsterRetreatDistance;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Is a world position inside the safe zone? (XZ distance — ignores height.)</summary>
        public bool Contains(Vector3 worldPos)
        {
            // HOUSE WATCHED omen: the SafeHouse gives NO protection tonight — everyone inside is
            // huntable/killable. One check here disables the safety for every system that asks
            // (MonsterAI's Huntable filter, the watcher's catch, the retreat logic).
            if (SunsetCurse.Core.OmenManager.IsHouseWatched) return false;

            Vector3 c = Center;
            float dx = worldPos.x - c.x, dz = worldPos.z - c.z;
            return dx * dx + dz * dz <= radius * radius;
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 c = Center;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.7f);
            Gizmos.DrawWireSphere(c, radius);                       // safe zone
            Gizmos.color = new Color(1f, 0.55f, 0.2f, 0.4f);
            Gizmos.DrawWireSphere(c, monsterRetreatDistance);       // monster keep-out while sheltering
        }
    }
}
