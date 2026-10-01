using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// The LOCKED GATE in the fence around the radio tower. A normal hinged <see cref="Door"/> —
    /// same setup (hinge empty + mesh child + collider), same co-op sync through DoorSync — except
    /// it refuses to open until someone turns the GOLDEN KEY in it (found in the compound on
    /// night 4). The unlocked state is shared via RadioTowerState, so one key turn opens the gate
    /// for the whole team, late joiners included.
    ///
    /// NOTE FOR FUTURE EDITS: never add an Update() here — it would hide Door's private Update,
    /// which animates the swing.
    ///
    /// THE DOORWAY: the fence ring stays FULLY SOLID (no walk-in). When this gate is OPENED (which
    /// needs the golden key), it disables the fence colliders + anti-jump barriers sitting in its
    /// doorway so the player can walk through; closing it re-enables them. So the door is the only
    /// way in, and you never have to hand-carve a gap in the fence.
    ///
    /// NOTE: this class MUST live in its own file named after the class. It used to sit inside
    /// TowerLadder.cs — Unity then had no real script asset for it, embedded an editor-only
    /// MonoScript object into SampleScene, and every BUILD crashed loading the scene with
    /// "The file 'level1' is corrupted!". One MonoBehaviour per file, always.
    /// </summary>
    public class GoldenGate : Door
    {
        private Collider[] doorwayObstructions;   // fence pieces / barriers this gate opens a hole in
        private bool obstructionsScanned;
        private Bounds doorwayBox;
        private bool boxComputed;

        // Measure the doorway from the CLOSED door's own bounds (before it can swing open).
        private void Start() => ComputeDoorwayBox();

        // Called by Door.SetOpenFromNetwork on every peer whenever the open state is applied.
        protected override void OnOpenChanged(bool open)
        {
            EnsureObstructionsScanned();
            if (doorwayObstructions == null) return;
            foreach (var c in doorwayObstructions)
                if (c != null) c.enabled = !open;   // OPEN → drop the fence in the doorway; CLOSED → restore
        }

        private void ComputeDoorwayBox()
        {
            var rends = GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                b.Expand(0.9f);   // reach the overlapping fence pieces + barriers just past the door edges
                doorwayBox = b;
            }
            else doorwayBox = new Bounds(transform.position + Vector3.up * 1.5f, new Vector3(3f, 4f, 3f));
            boxComputed = true;
        }

        // Lazy: on first open (well after the runtime fence barriers exist), collect every fence
        // collider / anti-jump barrier overlapping the doorway — but never this gate's own door.
        private void EnsureObstructionsScanned()
        {
            if (obstructionsScanned) return;
            obstructionsScanned = true;
            if (!boxComputed) ComputeDoorwayBox();

            var mine = new System.Collections.Generic.HashSet<Collider>(GetComponentsInChildren<Collider>(true));
            var list = new System.Collections.Generic.List<Collider>();
            foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsInactive.Include))
            {
                if (col == null || mine.Contains(col)) continue;
                if (col.GetComponentInParent<Door>() != null) continue;              // never another door
                if (!NameChainContains(col.transform, "fence")) continue;            // fence segments + FenceJumpBarriers
                if (col.bounds.Intersects(doorwayBox)) list.Add(col);
            }
            doorwayObstructions = list.ToArray();
        }

        private static bool NameChainContains(Transform t, string keyword)
        {
            for (Transform c = t; c != null; c = c.parent)
                if (c.name.ToLowerInvariant().Contains(keyword)) return true;
            return false;
        }

        public override string Verb
        {
            get
            {
                var st = RadioTowerState.Instance;
                if (st == null || st.GateUnlocked) return base.Verb;   // behaves like a normal door
                var inv = SunsetCurse.Core.PlayerInventory.Local;
                return inv != null && inv.HasRadioItem(SunsetCurse.Core.RadioItemKind.GoldenKey)
                    ? "unlock the gate with the golden key"
                    : "try the gate (locked)";
            }
        }

        public override bool CanInteract(GameObject interactor) => true;

        public override void Interact(GameObject interactor)
        {
            var st = RadioTowerState.Instance;
            if (st == null || st.GateUnlocked)
            {
                base.Interact(interactor);   // normal open/close, DoorSync'd
                return;
            }

            var inv = SunsetCurse.Core.PlayerInventory.Local;
            if (inv != null && inv.HasRadioItem(SunsetCurse.Core.RadioItemKind.GoldenKey))
                st.RequestUnlockGate();      // consumes the key (server echo) + announces to all
            else
                SunsetCurse.UI.ScreenMessage.Show("Locked tight. It needs a golden key.", 3f);
        }
    }
}
