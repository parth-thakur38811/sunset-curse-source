using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// The physical RADIO UNIT on the tower platform (the _Low.fbx prop). A standard
    /// <see cref="Interactable"/>: walk up to it and press E to attach the battery / transformer
    /// you're carrying (that's what "fixes" the tower). Needs a Collider (the setup tool adds one).
    ///
    /// NOTE: this class MUST live in its own file named after the class. It used to sit inside
    /// TowerLadder.cs — Unity then had no real script asset for it, embedded an editor-only
    /// MonoScript object into SampleScene, and every BUILD crashed loading the scene with
    /// "The file 'level1' is corrupted!". One MonoBehaviour per file, always.
    /// </summary>
    public class TowerRadioProp : Interactable
    {
        public override string Verb
        {
            get
            {
                var inv = SunsetCurse.Core.PlayerInventory.Local;
                bool hasPart = inv != null && (inv.HasBattery || inv.HasTransformer);
                return hasPart ? "attach the radio part" : "fix the radio tower";
            }
        }

        // Interactable only while the tower still needs fixing (both parts not yet attached).
        public override bool CanInteract(GameObject interactor)
        {
            var st = RadioTowerState.Instance;
            return st != null && !st.TowerFixed;
        }

        public override void Interact(GameObject interactor)
        {
            var st = RadioTowerState.Instance;
            if (st == null || st.TowerFixed) return;
            var inv = SunsetCurse.Core.PlayerInventory.Local;
            if (inv == null) return;
            if (inv.HasBattery || inv.HasTransformer)
                st.RequestAttach(inv.HasBattery, inv.HasTransformer);   // server takes what's needed, echoes back
            else
                SunsetCurse.UI.ScreenMessage.Show("I need a Battery or Transformer to fix the radio!", 3f);
        }
    }
}
