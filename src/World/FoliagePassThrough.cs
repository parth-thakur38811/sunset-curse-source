using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Makes decorative foliage (bushes, grass, ferns, plants) walk-through so the player isn't
    /// blocked by it — while trees, rocks, buildings, fences and the gate stay solid.
    ///
    /// For each matching collider:
    ///   - if the object is INTERACTABLE (e.g. a berry bush you forage) → turn its collider into a
    ///     TRIGGER, so the body passes through but the look-raycast can still find it;
    ///   - otherwise (plain decoration) → DISABLE the collider entirely.
    ///
    /// Foliage is matched by NAME keyword (checking the object and its parents). Anything matching
    /// an "obstruction" keyword is ALWAYS left solid, even if it also looks like foliage, so trees
    /// and rocks are never opened up.
    ///
    /// Runs on Start and re-scans a couple of times to catch foliage spawned at runtime (berry
    /// bushes, scattered forest).
    ///
    /// SETUP: drop this on a manager object in the scene (e.g. GameManager). If a particular bush
    /// still blocks you, add a fragment of its name to Foliage Keywords in the Inspector.
    ///
    /// NOTE: the interaction raycast must detect triggers for berry bushes to keep working — see
    /// PlayerInteractor (it uses QueryTriggerInteraction.Collide).
    /// </summary>
    public class FoliagePassThrough : MonoBehaviour
    {
        [Tooltip("Name fragments that mark something as walk-through foliage (case-insensitive).")]
        [SerializeField] private string[] foliageKeywords =
            { "foliage", "grass", "bush", "fern", "plant", "flower", "leaf", "shrub", "weed",
              "berry", "ivy", "reed", "vegetation", "hedge", "mushroom" };

        [Tooltip("Name fragments that must STAY solid even if they also look like foliage.")]
        [SerializeField] private string[] keepSolidKeywords =
            { "tree", "evergreen", "pine", "oak", "birch", "trunk", "dead", "log", "stump",
              "rock", "stone", "boulder", "cliff", "house", "cabin", "fence", "gate", "wall",
              "well", "ground", "plane", "terrain", "pavilion", "guard", "struct", "door", "roof" };

        private void Start()
        {
            Scan();
            // Re-scan to catch foliage spawned just after load (berry bushes, scattered forest).
            Invoke(nameof(Scan), 0.5f);
            Invoke(nameof(Scan), 2.5f);
        }

        private void Scan()
        {
            var colliders = FindObjectsByType<Collider>();
            int opened = 0;
            foreach (var col in colliders)
            {
                if (col == null || !col.enabled || col.isTrigger) continue;   // already passable
                string n = HierName(col.transform);
                if (ContainsAny(n, keepSolidKeywords)) continue;              // never open trees/rocks/buildings
                if (!ContainsAny(n, foliageKeywords)) continue;               // only touch foliage

                if (col.GetComponentInParent<Interactable>() != null)
                {
                    // Gatherable resources (ResourceNode = wood/stone/ritual herb) must stay solid:
                    // converting their (often non-convex) MeshCollider to convex produces a degenerate
                    // hull that the look-raycast can miss, so "Press E" never appears. The player isn't
                    // expected to walk over them constantly like berry bushes — leave their original
                    // collider alone.
                    if (col.GetComponentInParent<ResourceNode>() != null) continue;

                    // Keep it raycastable for "Press E", but let the body pass through.
                    if (col is MeshCollider mc) mc.convex = true;             // triggers need convex meshes
                    col.isTrigger = true;
                }
                else
                {
                    col.enabled = false;                                      // plain decoration: no collider
                }
                opened++;
            }
            if (opened > 0)
                Debug.Log($"[FoliagePassThrough] Made {opened} foliage collider(s) walk-through.");
        }

        private static string HierName(Transform t)
        {
            // Include parent names so e.g. a "Leaves" mesh under "Bush2" is caught by "bush".
            string s = t.name.ToLowerInvariant();
            for (var p = t.parent; p != null; p = p.parent) s += " " + p.name.ToLowerInvariant();
            return s;
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (var x in needles)
                if (!string.IsNullOrEmpty(x) && haystack.Contains(x.ToLowerInvariant())) return true;
            return false;
        }
    }
}
