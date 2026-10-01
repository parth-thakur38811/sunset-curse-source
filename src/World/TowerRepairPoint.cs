using System.Collections.Generic;
using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Marks the repair spot at the TOP of the radio tower — stand inside it with the battery /
    /// transformer and press the Attach key (KrotkofalRadio handles the input). A box trigger
    /// covering the top platform.
    ///
    /// NOTE: this class MUST live in its own file named after the class. It used to sit inside
    /// TowerLadder.cs — Unity then had no real script asset for it, embedded an editor-only
    /// MonoScript object into SampleScene, and every BUILD crashed loading the scene with
    /// "The file 'level1' is corrupted!". One MonoBehaviour per file, always.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class TowerRepairPoint : MonoBehaviour
    {
        private static readonly List<TowerRepairPoint> all = new List<TowerRepairPoint>();

        private BoxCollider zone;

        private void Awake()
        {
            zone = GetComponent<BoxCollider>();
            zone.isTrigger = true;
        }

        private void OnEnable()  => all.Add(this);
        private void OnDisable() => all.Remove(this);

        public static bool AnyContains(Vector3 pos)
        {
            foreach (var p in all)
            {
                if (p == null) continue;
                Bounds b = p.zone.bounds;
                b.Expand(1f);
                if (b.Contains(pos)) return true;
            }
            return false;
        }

        private void OnDrawGizmosSelected()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.7f);
            Gizmos.DrawWireCube(b.bounds.center, b.bounds.size);
        }
    }
}
