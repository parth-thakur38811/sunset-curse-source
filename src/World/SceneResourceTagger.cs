using System.Collections.Generic;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Walks the scene on Start and turns matching scene objects into harvestable
    /// <see cref="ResourceNode"/>s based on their name. So every imported asset-pack tree becomes
    /// Wood, every rock becomes Stone, and many foliage prefabs become Ritual Herb — without
    /// hand-prefabbing each one.
    ///
    /// Keywords are inspector-tuneable; blocklists prevent obvious mismatches (e.g. tree STUMPS
    /// don't become Wood). Objects that already have an Interactable (BerryBush, hand-placed
    /// ResourceNode prefabs, the crafting table) are skipped.
    ///
    /// Trees / rocks keep their solid collider — they're chunky world geometry the player walks
    /// around anyway. Herbs get their collider converted to a TRIGGER (raycast still hits via
    /// PlayerInteractor's QueryTriggerInteraction.Collide), so foliage stays walk-through.
    ///
    /// Runs once on Start. The ResourceSpawner's daily-respawn behaviour is unaffected; tagged
    /// scene objects DO NOT respawn after harvest (a chopped tree is gone for the rest of the
    /// run — that's intentional realism).
    ///
    /// SETUP:
    ///   1. In SampleScene, empty GameObject "SceneResourceTagger" + this script.
    ///   2. Adjust keyword lists in the Inspector if your asset pack uses different names.
    /// </summary>
    public class SceneResourceTagger : MonoBehaviour
    {
        [Header("Tree → Wood")]
        [Tooltip("Object name (or any parent name) containing any of these becomes a Wood node.")]
        [SerializeField] private string[] treeKeywords =
            { "tree", "pine", "oak", "birch", "evergreen", "fir", "spruce", "trunk" };
        [Tooltip("Names matching these are NOT treated as trees (avoids stumps / logs becoming trees).")]
        [SerializeField] private string[] treeBlocklist =
            { "stump", "log_", " log", "fallen" };
        [SerializeField] private int woodPerTree = 1;

        [Header("Rock → Stone")]
        [SerializeField] private string[] rockKeywords =
            { "rock", "stone", "boulder", "menhir", "ore" };
        [SerializeField] private string[] rockBlocklist = new string[0];
        [SerializeField] private int stonePerRock = 1;

        [Header("Foliage → Ritual Herb")]
        [SerializeField] private string[] herbKeywords =
            { "foliage", "fern", "shrub", "grass", "plant", "flower", "bush", "weed", "ivy", "mushroom" };
        [Tooltip("Names matching these stay non-harvestable (avoid berry bushes, fruit, decorations).")]
        [SerializeField] private string[] herbBlocklist =
            { "berry", "fruit" };
        [SerializeField] private int herbPerPlant = 1;

        [Header("Behaviour")]
        [Tooltip("Skip objects that already have an Interactable (BerryBush, pre-set ResourceNodes, crafting table).")]
        [SerializeField] private bool skipExistingInteractables = true;
        [Tooltip("Make tagged herb colliders walk-through (trigger). Player can still raycast-interact.")]
        [SerializeField] private bool herbsAreWalkthrough = true;

        [Header("Which categories to tag")]
        [Tooltip("Turn each category on/off. Default: trees + rocks only. Herbs are intentionally " +
                 "OFF so the rare/shiny Foliage 3 prefab spawned by ResourceSpawner stays the SOLE " +
                 "herb source — other foliage in the scene stays purely decorative and walk-through " +
                 "(FoliagePassThrough disables their colliders).")]
        [SerializeField] private bool tagTrees = true;
        [SerializeField] private bool tagRocks = true;
        [SerializeField] private bool tagHerbs = false;

        private void Start()
        {
            int trees = 0, rocks = 0, herbs = 0;
            var seen = new HashSet<GameObject>();

            // Iterate Colliders (every harvestable needs one for the raycast to hit anyway).
            // De-dup by GameObject in case an object has multiple colliders.
            foreach (var col in FindObjectsByType<Collider>())
            {
                if (col == null) continue;
                var go = col.gameObject;
                if (!seen.Add(go)) continue;
                if (skipExistingInteractables && go.GetComponentInParent<Interactable>() != null) continue;

                string[] tokens = Tokenize(HierName(go.transform));
                ResourceType? type = null;
                int amount = 1;
                bool makeWalkthrough = false;

                // Order: trees → rocks → herbs (so a "Tree" container with "foliage" leaves wins as Wood).
                if (tagTrees && MatchAny(tokens, treeKeywords) && !MatchAny(tokens, treeBlocklist))
                { type = ResourceType.Wood; amount = woodPerTree; }
                else if (tagRocks && MatchAny(tokens, rockKeywords) && !MatchAny(tokens, rockBlocklist))
                { type = ResourceType.Stone; amount = stonePerRock; }
                else if (tagHerbs && MatchAny(tokens, herbKeywords) && !MatchAny(tokens, herbBlocklist))
                { type = ResourceType.RitualHerb; amount = herbPerPlant; makeWalkthrough = herbsAreWalkthrough; }

                if (type == null) continue;

                // FoliagePassThrough may have disabled the collider already — re-enable so the raycast hits.
                if (!col.enabled) col.enabled = true;

                var node = go.AddComponent<ResourceNode>();
                // Day=0 for scene-tagged (permanent — never respawns) so the ID stays stable for
                // the whole run. Position is identical on every peer (same scene), so the hash is
                // identical → server-validated pickup despawns the tree for the entire team.
                int id = Inventory.ComputeNodeId(go.transform.position, type.Value, 0);
                node.Configure(type.Value, amount, id);

                if (makeWalkthrough)
                {
                    if (col is MeshCollider mc) mc.convex = true;   // triggers require convex
                    col.isTrigger = true;
                }

                if      (type == ResourceType.Wood)       trees++;
                else if (type == ResourceType.Stone)      rocks++;
                else                                       herbs++;
            }

            Debug.Log($"[SceneResourceTagger] Tagged {trees} trees, {rocks} rocks, {herbs} herbs.");
        }

        // PRESERVES CASE so Tokenize can split on camelCase boundaries.
        private static string HierName(Transform t)
        {
            string s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s += " " + p.name;
            return s;
        }

        /// <summary>
        /// Splits the hierarchy-name string into individual alphabetic tokens:
        ///   • non-letters (digits, underscores, parens, spaces) become separators;
        ///   • camelCase boundaries (lowercase → uppercase) also split.
        ///
        /// "PT_Generic_Rock_01 ForestContainer" → ["PT","Generic","Rock","Forest","Container"]
        /// "foliage 1(clone) ForestContainer"   → ["foliage","clone","Forest","Container"]
        ///
        /// MatchAny then checks whether ANY token STARTS WITH a needle — so the rock keyword
        /// "ore" no longer matches the "ore" hidden inside "fOREst". It only fires when a token
        /// genuinely begins with "ore" (e.g. "Ore_Rock_01" → "Ore" → match).
        /// </summary>
        private static string[] Tokenize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return System.Array.Empty<string>();
            var spaced = System.Text.RegularExpressions.Regex.Replace(raw, @"(?<=[a-z])(?=[A-Z])", " ");
            return System.Text.RegularExpressions.Regex.Split(spaced, @"[^a-zA-Z]+");
        }

        private static bool MatchAny(string[] tokens, string[] needles)
        {
            if (tokens == null || needles == null) return false;
            foreach (var raw in tokens)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string t = raw.ToLowerInvariant();
                foreach (var n in needles)
                {
                    if (string.IsNullOrEmpty(n)) continue;
                    if (t.StartsWith(n.ToLowerInvariant())) return true;
                }
            }
            return false;
        }
    }
}
