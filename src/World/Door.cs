using UnityEngine;
using SunsetCurse.Audio;

namespace SunsetCurse.World
{
    /// <summary>
    /// A hinged door you open/close by looking at it and pressing E. It swings around its HINGE
    /// (this GameObject), so the door model must be a CHILD offset sideways from the hinge — it
    /// pivots on its edge, like a real door.
    ///
    /// WHY A HINGE OBJECT: free house assets usually just have an empty doorway. You add your own
    /// door; a door rotates around one vertical edge, so the script goes on an empty AT that edge
    /// with the door mesh parented under it.
    ///
    /// MULTIPLAYER: open/close is SYNCED through <see cref="DoorSync"/> (one object in the scene) —
    /// one player opens it and everyone (plus the host's monster, for sight) sees the same state.
    /// If there's no DoorSync in the scene it just works locally (single-player).
    ///
    /// SETUP (in Unity):
    ///   1. Empty GameObject at one vertical EDGE of the doorway. Name it "Door".
    ///   2. Door model (or a stretched Cube ~0.9 × 2.1 × 0.08) as a CHILD, slid across to fill the
    ///      opening, with a Box Collider on it.
    ///   3. This script on the "Door" empty. Set Open Angle to +90 or -90 for swing direction.
    ///   4. For a closed door to block the monster's SIGHT, put the child collider on a layer that's
    ///      in MonsterAI ▸ "Sight Blockers".
    ///   5. (Co-op) add ONE "DoorSync" object to the scene — see DoorSync.cs.
    /// </summary>
    public class Door : Interactable
    {
        [Header("Swing")]
        [Tooltip("How far the door swings open, in degrees around local Y. Use -90 to swing the other way.")]
        [SerializeField] private float openAngle = 90f;
        [Tooltip("How fast the door rotates, in degrees per second.")]
        [SerializeField] private float openSpeed = 240f;
        [Tooltip("Locked doors can't be opened (E does nothing). Toggle via SetLocked from other scripts.")]
        [SerializeField] private bool locked = false;

        [Header("Audio (optional, 3D)")]
        [SerializeField] private AudioClip openSfx;
        [SerializeField] private AudioClip closeSfx;
        [Range(0f, 1f)] [SerializeField] private float sfxVolume = 0.9f;

        private bool isOpen;
        private Quaternion closedRot, openRot;
        private int doorId;

        public override string Verb => isOpen ? "close the door" : "open the door";
        public override bool CanInteract(GameObject interactor) => !locked;

        private void Awake()
        {
            closedRot = transform.localRotation;
            openRot = closedRot * Quaternion.Euler(0f, openAngle, 0f);
            transform.localRotation = closedRot;
            // Deterministic ID from the fixed scene position → identical on every peer.
            doorId = DoorSync.ComputeDoorId(transform.position);
        }

        private void OnEnable()  => DoorSync.Register(doorId, this);
        private void OnDisable() => DoorSync.Unregister(doorId, this);

        public override void Interact(GameObject interactor)
        {
            if (locked) return;
            // Route through DoorSync so every peer flips together (falls back to local if absent).
            DoorSync.RequestSetOpen(doorId, !isOpen);
        }

        /// <summary>Open/close from another system (a key auto-opening a vault, the monster slamming
        /// a door, …). Also synced.</summary>
        public void SetOpen(bool open) => DoorSync.RequestSetOpen(doorId, open);

        public void SetLocked(bool value) => locked = value;

        /// <summary>Applied by DoorSync when the networked state changes. <paramref name="immediate"/>
        /// snaps the pose (used for late-join sync) instead of animating.</summary>
        public void SetOpenFromNetwork(bool open, bool immediate)
        {
            bool changed = open != isOpen;
            isOpen = open;
            if (immediate)
            {
                transform.localRotation = open ? openRot : closedRot;
            }
            else if (changed)
            {
                var clip = open ? openSfx : closeSfx;
                if (clip != null && AudioManager.Instance != null)
                    AudioManager.Instance.PlaySfx3D(clip, transform.position, sfxVolume, 25f);
            }
            OnOpenChanged(open);
        }

        /// <summary>Hook for subclasses to react when the open state is applied (runs on every peer,
        /// including the immediate late-join sync). GoldenGate uses it to drop the fence colliders
        /// blocking its doorway while it's open. Base does nothing.</summary>
        protected virtual void OnOpenChanged(bool open) { }

        private void Update()
        {
            Quaternion target = isOpen ? openRot : closedRot;
            if (transform.localRotation != target)
                transform.localRotation = Quaternion.RotateTowards(
                    transform.localRotation, target, openSpeed * Time.deltaTime);
        }
    }
}
