using UnityEngine;
using UnityEngine.InputSystem;
using StarterAssets;
using SunsetCurse.World;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Ladder climbing for the radio tower. Auto-added to the PlayerCapsule by PlayerHealthSync —
    /// no prefab wiring, and a plain MonoBehaviour (owner-authoritative movement replicates
    /// through NetworkTransform, so teammates see the climb with zero extra networking).
    ///
    /// CONTROLS: stand at the ladder → M grabs on → W/S climb up/down (mouse look still works) →
    /// M lets go, or step off automatically at the very top / bottom.
    ///
    /// HOW: while climbing the StarterAssets controller is disabled (its gravity would drag you
    /// down), and this script drives the CharacterController + camera directly. Going DOWNED or
    /// dying force-exits the climb without touching the controller (those states own it).
    /// </summary>
    public class PlayerClimb : MonoBehaviour
    {
        [Header("Climbing")]
        [SerializeField] private Key climbKey = Key.M;
        [Tooltip("Climb speed in metres per second.")]
        [SerializeField] private float climbSpeed = 2.5f;
        [Tooltip("How strongly you're pulled onto the ladder's line (stops sideways drift).")]
        [SerializeField] private float snapStrength = 6f;
        [Tooltip("Mouse-look sensitivity while on the ladder (the normal controller is off).")]
        [SerializeField] private float lookSensitivity = 1f;

        private CharacterController cc;
        private PlayerStats stats;
        private MonoBehaviour movementController;   // StarterAssets FirstPersonController
        private StarterAssetsInputs input;
        private Transform cameraRoot;
        private float camPitch;

        private bool climbing;
        private TowerLadder ladder;

        private void Start()
        {
            cc = GetComponent<CharacterController>();
            stats = GetComponent<PlayerStats>();
            input = GetComponent<StarterAssetsInputs>();
            var t = transform.Find("PlayerCameraRoot");
            cameraRoot = t != null ? t : null;

            // Same trick PlayerStats uses: find the controller by its public speed fields, so we
            // don't hard-depend on the StarterAssets type staying put.
            foreach (var mb in GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var type = mb.GetType();
                if (type.GetField("MoveSpeed") != null && type.GetField("SprintSpeed") != null)
                {
                    movementController = mb;
                    break;
                }
            }
        }

        private void Update()
        {
            // Owner only — remote copies just replay the replicated transform.
            if (Core.PlayerInventory.Local == null ||
                Core.PlayerInventory.Local.gameObject != gameObject) return;
            if (Keyboard.current == null) return;

            // Death / downed always wins: bail out of the climb but leave the controller to the
            // state that disabled it (revive re-enables it, death never does).
            if (stats != null && (!stats.IsAlive || stats.IsDowned))
            {
                if (climbing) { climbing = false; ladder = null; }
                return;
            }

            if (Keyboard.current[climbKey].wasPressedThisFrame)
            {
                // Letting go NEAR THE TOP steps you off onto the platform instead of dropping you.
                if (climbing) ExitClimb(nudgeOff: NearLadderTop());
                else TryStartClimb();
            }

            if (climbing)
            {
                TickClimb();
                // Guidance at the top: this is the moment to get off onto the platform.
                if (climbing && NearLadderTop())
                    SunsetCurse.UI.ScreenMessage.Show("Press M", 0.3f);
            }
            else
            {
                // Standing at the ladder → keep the grab hint on screen while they're in the zone.
                if (TowerLadder.AtPosition(transform.position) != null)
                    SunsetCurse.UI.ScreenMessage.Show("Press M to climb the ladder", 0.3f);
            }
        }

        private bool NearLadderTop()
            => ladder != null && transform.position.y >= ladder.TopY - 2.5f;

        private void TryStartClimb()
        {
            ladder = TowerLadder.AtPosition(transform.position);
            if (ladder == null)
            {
                SunsetCurse.UI.ScreenMessage.Show("There's nothing to climb here.", 1.5f);
                return;
            }
            climbing = true;
            if (movementController != null) movementController.enabled = false;
            camPitch = 0f;
            Core.PlayerInventory.Local?.SetClimbState(true, 0f);   // remotes start the climb anim
            SunsetCurse.UI.ScreenMessage.Show("Press W/S to move", 3f);
        }

        private void ExitClimb(bool nudgeOff)
        {
            climbing = false;
            Core.PlayerInventory.Local?.SetClimbState(false, 0f);   // remotes end the climb anim
            if (movementController != null && stats != null && stats.IsAlive && !stats.IsDowned)
                movementController.enabled = true;
            if (nudgeOff && ladder != null && cc != null)
            {
                // Step off onto the platform: a firm shove toward the ladder's exit side.
                cc.Move(ladder.ExitForward * 1.1f + Vector3.up * 0.4f);
            }
            ladder = null;
        }

        private void TickClimb()
        {
            if (ladder == null || cc == null) { ExitClimb(false); return; }

            float v = input != null ? input.move.y : 0f;   // W = +1, S = -1

            // Publish climb direction so remote players' animators play up / down / hold correctly.
            Core.PlayerInventory.Local?.SetClimbState(true, v);

            // Pull gently onto the ladder line so you can't drift off the rungs.
            Vector3 line = ladder.LinePoint(transform.position.y);
            Vector3 toLine = line - transform.position;
            toLine.y = 0f;

            cc.Move((Vector3.up * (v * climbSpeed) + toLine * snapStrength) * Time.deltaTime);

            // Mouse look (the normal controller is disabled, so we drive it here).
            if (input != null && cameraRoot != null)
            {
                Vector2 look = input.look;
                transform.Rotate(0f, look.x * lookSensitivity * 0.12f, 0f);
                camPitch = Mathf.Clamp(camPitch - look.y * lookSensitivity * 0.12f, -85f, 85f);
                cameraRoot.localRotation = Quaternion.Euler(camPitch, 0f, 0f);
            }

            // Reached the very top while pushing up → step off onto the platform.
            if (v > 0f && transform.position.y >= ladder.TopY - 0.4f) { ExitClimb(nudgeOff: true); return; }
            // Back at the bottom while pushing down → just let go.
            if (v < 0f && transform.position.y <= ladder.BottomY + 0.2f) { ExitClimb(nudgeOff: false); }
        }
    }
}
