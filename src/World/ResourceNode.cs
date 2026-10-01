using System.Collections.Generic;
using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.UI;

namespace SunsetCurse.World
{
    /// <summary>
    /// A gatherable resource in the world (a tree, a rock, a glowing herb). Pickup is NETWORK-
    /// AUTHORITATIVE — pressing E sends a ServerRpc with a deterministic node ID; the server
    /// appends it to a synced list and every peer despawns their local copy at the same instant.
    /// So one player chopping a tree destroys it for the entire team.
    ///
    /// The ID is a hash of position+type+day, identical on every peer because the spawn positions
    /// themselves are deterministic (shared world seed → ResourceSpawner, scene-placed → fixed
    /// positions). See <see cref="Inventory.ComputeNodeId"/>.
    /// </summary>
    public class ResourceNode : Interactable
    {
        [SerializeField] private ResourceType type = ResourceType.Wood;
        [Tooltip("How much of the resource one harvest awards.")]
        [SerializeField] private int amount = 1;
        [Tooltip("Override the verb prompt. Leave EMPTY to auto-generate from type.")]
        [SerializeField] private string verbOverride = "";
        [Tooltip("Seconds the player must HOLD E to harvest this node — a circle fills at the " +
                 "centre of the screen while they hold. Harvesting takes time on purpose: it " +
                 "roots you to the spot, which is a risk at night.")]
        [SerializeField] private float harvestHoldSeconds = 1.5f;

        /// <summary>Deterministic ID assigned by the spawner/tagger. 0 = not yet networked
        /// (legacy / SP-only / not synced). Set via <see cref="Configure(ResourceType,int,int)"/>.</summary>
        public int NodeId { get; private set; }
        public ResourceType Type => type;
        public int Amount => amount;

        // Static registry so the Inventory's despawn broadcast can find local instances by ID.
        // Multiple nodes with the same ID is technically possible (hash collisions); we tolerate
        // it by storing a list — DespawnLocalById destroys all of them.
        private static readonly Dictionary<int, List<ResourceNode>> registry =
            new Dictionary<int, List<ResourceNode>>();

        public override string Verb =>
            !string.IsNullOrEmpty(verbOverride) ? verbOverride : DefaultVerb(type);

        /// <summary>Harvesting is a hold-E channel (PlayerInteractor draws the filling circle).</summary>
        public override float HoldSeconds => harvestHoldSeconds;

        /// <summary>Label under the progress circle while the player holds E.</summary>
        public override string HoldPrompt => type switch
        {
            ResourceType.Wood       => "Chopping Tree, Hold E",
            ResourceType.Stone      => "Picking up stone, Hold E",
            ResourceType.RitualHerb => "Harvesting Ritual Herb, Hold E",
            _                       => "Harvesting, Hold E"
        };

        public override void Interact(GameObject interactor)
        {
            // Route through the SHARED, NETWORKED Inventory — server validates uniqueness +
            // broadcasts despawn to every peer + grants the picker their inventory bump.
            // Picker no longer Destroys locally; the broadcast does that on every peer.
            if (NodeId != 0)
            {
                Inventory.RequestConsumeNode(NodeId, type, amount);
            }
            else
            {
                // Pre-rework / non-networked fallback: just grant locally and destroy.
                if (PlayerInventory.Local != null) PlayerInventory.Local.Add(type, amount);
                ScreenMessage.Show($"+{amount} {DisplayName(type)}", 1.5f);
                Destroy(gameObject);
                return;
            }
            ScreenMessage.Show($"+{amount} {DisplayName(type)}", 1.5f);
        }

        /// <summary>Runtime configuration — used by SceneResourceTagger after AddComponent.</summary>
        public void Configure(ResourceType newType, int newAmount)
        {
            type = newType;
            amount = Mathf.Max(1, newAmount);
        }

        /// <summary>Full configuration including the networked NodeId. Spawners + tagger should
        /// use this so pickup syncs to every peer.</summary>
        public void Configure(ResourceType newType, int newAmount, int nodeId)
        {
            type = newType;
            amount = Mathf.Max(1, newAmount);
            SetNodeId(nodeId);
        }

        /// <summary>Register this node under its networked id. If the id is ALREADY in the
        /// consumed list (we're a late-spawning instance after another peer already chopped it),
        /// self-despawn immediately so the world stays consistent.</summary>
        public void SetNodeId(int nodeId)
        {
            // Remove from old bucket if we were previously registered.
            if (NodeId != 0 && registry.TryGetValue(NodeId, out var oldList))
            {
                oldList.Remove(this);
                if (oldList.Count == 0) registry.Remove(NodeId);
            }
            NodeId = nodeId;
            if (nodeId == 0) return;

            if (!registry.TryGetValue(nodeId, out var list))
            {
                list = new List<ResourceNode>();
                registry[nodeId] = list;
            }
            list.Add(this);

            // If this node was already consumed before we existed, despawn at the end of frame.
            if (Inventory.IsNodeConsumed(nodeId))
            {
                Destroy(gameObject);
            }
        }

        /// <summary>Called by Inventory's OnListChanged when ANY peer's pickup is confirmed.
        /// Destroys every local ResourceNode registered under this id.</summary>
        public static void DespawnLocalById(int nodeId)
        {
            if (!registry.TryGetValue(nodeId, out var list)) return;
            // Copy because Destroy → OnDestroy → removes from list, would mutate during iteration.
            var copy = new List<ResourceNode>(list);
            foreach (var n in copy) if (n != null) Destroy(n.gameObject);
            registry.Remove(nodeId);
        }

        private void OnDestroy()
        {
            if (NodeId == 0) return;
            if (registry.TryGetValue(NodeId, out var list))
            {
                list.Remove(this);
                if (list.Count == 0) registry.Remove(NodeId);
            }
        }

        private static string DefaultVerb(ResourceType t) => t switch
        {
            ResourceType.Wood => "chop wood",
            ResourceType.Stone => "pick up stone",
            ResourceType.RitualHerb => "harvest ritual herb",
            _ => "gather"
        };

        private static string DisplayName(ResourceType t) => t switch
        {
            ResourceType.Wood => "Wood",
            ResourceType.Stone => "Stone",
            ResourceType.RitualHerb => "Ritual Herb",
            _ => "Resource"
        };
    }
}
