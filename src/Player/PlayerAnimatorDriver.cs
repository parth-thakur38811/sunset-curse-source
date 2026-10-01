using UnityEngine;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Sets the player Animator's "Speed" float from frame-to-frame horizontal movement, so the
    /// character animates correctly on EVERY instance:
    ///   - local owner: the FPS controller drives transform.position; we see the delta and animate.
    ///   - remote players: NetworkTransform drives transform.position; we see the SAME deltas and
    ///     animate the same way.
    ///
    /// Pair this with a NetworkAnimator on the prefab so the Animator state is replicated, and the
    /// blend tree's Speed parameter ends up smooth and consistent across all clients.
    ///
    /// SETUP: add this to the character GameObject (the one with the Animator component — likely a
    /// child of PlayerCapsule). No Inspector wiring needed — it auto-finds the Animator on the same
    /// GameObject and uses its own transform's world position (which moves with PlayerCapsule).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimatorDriver : MonoBehaviour
    {
        [Tooltip("Float parameter on the Animator Controller to feed with horizontal speed.")]
        [SerializeField] private string speedParameter = "Speed";

        [Tooltip("Trigger parameter fired when an upward-velocity spike is detected. Leave EMPTY if " +
                 "you don't have a Jump state in your controller.")]
        [SerializeField] private string jumpTriggerParameter = "Jump";

        [Tooltip("Bool parameter set true while the player is on the ladder (drives the climb state). " +
                 "Leave EMPTY if your controller has no climb state.")]
        [SerializeField] private string climbingParameter = "Climbing";
        [Tooltip("Float parameter for climb direction: +1 up, -1 down, 0 holding still on the ladder.")]
        [SerializeField] private string climbDirParameter = "ClimbDir";

        [Tooltip("Higher = snappier animation response; lower = smoother. 8–12 is comfortable.")]
        [SerializeField] private float smoothing = 10f;

        [Tooltip("Upward vertical velocity (m/s) above which we consider it a jump. Higher = fewer " +
                 "false positives from walking up slopes / stairs. 4 is a safe default.")]
        [SerializeField] private float jumpVelocityThreshold = 4f;

        private Animator anim;
        private Core.PlayerInventory inventory;   // the networked climb state lives here
        private Vector3 lastPosition;
        private bool positionCaptured;
        private float smoothedSpeed;
        private float smoothedClimbDir;
        private bool armedForJump = true;     // re-arms after vertical velocity drops back near 0

        private void Start()
        {
            anim = GetComponent<Animator>();
            inventory = GetComponentInParent<Core.PlayerInventory>();
            lastPosition = transform.position;
        }

        private void Update()
        {
            if (anim == null) return;

            float horizontalSpeed = 0f;
            float verticalVelocity = 0f;
            if (positionCaptured)
            {
                Vector3 delta = transform.position - lastPosition;
                verticalVelocity = delta.y / Mathf.Max(0.0001f, Time.deltaTime);
                Vector3 horizontalDelta = new Vector3(delta.x, 0f, delta.z);
                horizontalSpeed = horizontalDelta.magnitude / Mathf.Max(0.0001f, Time.deltaTime);
            }
            lastPosition = transform.position;
            positionCaptured = true;

            // Lerp toward the target speed so the blend tree doesn't jitter when starting / stopping.
            smoothedSpeed = Mathf.Lerp(smoothedSpeed, horizontalSpeed, smoothing * Time.deltaTime);
            anim.SetFloat(speedParameter, smoothedSpeed);

            // Climbing — read the networked flag (correct on EVERY peer: owner writes it, remotes
            // read the replicated value) and drive the climb state + up/down direction.
            if (!string.IsNullOrEmpty(climbingParameter) && inventory != null)
            {
                bool climbing = inventory.IsClimbing;
                anim.SetBool(climbingParameter, climbing);
                if (!string.IsNullOrEmpty(climbDirParameter))
                {
                    smoothedClimbDir = Mathf.Lerp(smoothedClimbDir, inventory.ClimbDir, smoothing * Time.deltaTime);
                    anim.SetFloat(climbDirParameter, smoothedClimbDir);
                }
            }

            // Jump trigger: fire ONCE per jump on a vertical-velocity spike upward, then re-arm only
            // after vertical velocity drops back toward zero (so we don't re-fire while in the air).
            if (!string.IsNullOrEmpty(jumpTriggerParameter))
            {
                if (armedForJump && verticalVelocity > jumpVelocityThreshold)
                {
                    anim.SetTrigger(jumpTriggerParameter);
                    armedForJump = false;
                }
                else if (!armedForJump && verticalVelocity < 0.5f)
                {
                    armedForJump = true;
                }
            }
        }
    }
}
