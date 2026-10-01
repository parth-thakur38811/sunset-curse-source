using System.Collections.Generic;
using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Marks the CLIMBABLE ladder of the radio tower: a tall box trigger over the ladder mesh.
    /// PlayerClimb (on the player) finds the ladder you're standing at and moves you up/down its
    /// vertical line while you're in climb mode (M).
    ///
    /// SETUP (done by Tools ▸ Sunset Curse ▸ 8; hand-tweak after):
    ///   • Child of the tower, BoxCollider with Is Trigger ON, stretched over the whole ladder
    ///     (about 1m wide/deep, full ladder height).
    ///   • The object's blue Z arrow should point AWAY from the ladder toward where a climber
    ///     steps OFF at the top (the platform) — that's the exit direction.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class TowerLadder : MonoBehaviour
    {
        private static readonly List<TowerLadder> all = new List<TowerLadder>();

        private BoxCollider zone;

        private void Awake()
        {
            zone = GetComponent<BoxCollider>();
            zone.isTrigger = true;   // never block movement — it's a detection volume
        }

        private void OnEnable()  => all.Add(this);
        private void OnDisable() => all.Remove(this);

        public float TopY    => zone.bounds.max.y;
        public float BottomY => zone.bounds.min.y;

        /// <summary>The direction a climber steps off at the top (toward the platform).</summary>
        public Vector3 ExitForward => transform.forward;

        /// <summary>The point on the ladder's vertical line at the given height — climbers are
        /// pulled onto this line so they can't drift off the rungs sideways.</summary>
        public Vector3 LinePoint(float y)
        {
            Vector3 c = zone.bounds.center;
            return new Vector3(c.x, y, c.z);
        }

        public bool Contains(Vector3 pos, float slack = 0.6f)
        {
            Bounds b = zone.bounds;
            b.Expand(slack * 2f);
            return b.Contains(pos);
        }

        /// <summary>The ladder at this position, or null.</summary>
        public static TowerLadder AtPosition(Vector3 pos)
        {
            foreach (var l in all)
                if (l != null && l.Contains(pos)) return l;
            return null;
        }

        private void OnDrawGizmosSelected()
        {
            var b = GetComponent<BoxCollider>();
            if (b == null) return;
            Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.6f);
            Gizmos.DrawWireCube(b.bounds.center, b.bounds.size);
            Gizmos.DrawRay(b.bounds.center, transform.forward * 1.5f);   // exit direction
        }
    }

    // NOTE: TowerRadioProp, GoldenGate and TowerRepairPoint used to live in this file too.
    // They were moved to their own correctly-named files (TowerRadioProp.cs / GoldenGate.cs /
    // TowerRepairPoint.cs). A MonoBehaviour whose class name doesn't match its file name has NO
    // real script asset — saving one into a scene embeds an editor-only MonoScript object in the
    // scene file, and every BUILD then crashed on load with "The file 'level1' is corrupted!".
    // Keep ONE MonoBehaviour per file, named after the class.
}
