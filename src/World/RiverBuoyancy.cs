using UnityEngine;
using Unity.Netcode;
using StarterAssets;

namespace SunsetCurse.World
{
    /// <summary>
    /// Water interaction for anything that enters the river's trigger volume. Auto-added and
    /// bound by <see cref="RiverFlowController"/> — no manual setup needed — but it can also be
    /// pre-placed on prefabs and it will bind itself on first contact.
    ///
    ///   • CHARACTERCONTROLLER (players): the current pushes them downstream via cc.Move (safe to
    ///     combine with StarterAssets' own Move calls). In water deep enough to swim, gravity is
    ///     neutralised (the controller's vertical velocity is zeroed) and they bob up to float
    ///     depth. In this game's shin-deep river they simply wade and get shoved downstream.
    ///     Owner-local in multiplayer: remote copies of a player are driven by NetworkTransform,
    ///     so this script goes dormant on non-owned player objects.
    ///
    ///   • RIGIDBODY (props): classic spring buoyancy — sinks to the surface, a depth-proportional
    ///     upward force with damping floats it (gravity stays on; the spring partially cancels
    ///     it), and the current washes it downstream with a capped acceleration.
    /// </summary>
    public class RiverBuoyancy : MonoBehaviour
    {
        [Header("Current")]
        [Tooltip("How strongly the river current pushes a wading CHARACTER, in m/s at full strength.")]
        [SerializeField] private float characterPush = 1.6f;
        [Tooltip("Acceleration applied to RIGIDBODIES until they reach the current's speed.")]
        [SerializeField] private float bodyCurrentAccel = 4f;

        [Header("Buoyancy (rigidbodies + swimming characters)")]
        [Tooltip("Upward spring strength per metre of submersion.")]
        [SerializeField] private float buoyancyStrength = 22f;
        [Tooltip("Vertical damping so floating objects settle instead of bouncing.")]
        [SerializeField] private float buoyancyDamping = 3.5f;
        [Tooltip("A character starts SWIMMING when this fraction of its height is under water.")]
        [SerializeField] private float swimSubmersion = 0.55f;
        [Tooltip("How far below the surface a swimming character's feet settle, in metres.")]
        [SerializeField] private float floatDepth = 1.6f;

        // Cached once — no per-frame GetComponent / reflection lookups.
        private RiverFlowController river;
        private CharacterController cc;
        private FirstPersonController fpc;
        private Rigidbody rb;
        private NetworkObject netObj;
        private bool inWater;

        // StarterAssets keeps its gravity accumulator private; zeroing it while swimming is the
        // clean way to "partially disable gravity" for a CharacterController. Reflected ONCE.
        private static readonly System.Reflection.FieldInfo VerticalVelocityField =
            typeof(FirstPersonController).GetField("_verticalVelocity",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private void Awake()
        {
            cc     = GetComponent<CharacterController>();
            fpc    = GetComponent<FirstPersonController>();
            rb     = GetComponent<Rigidbody>();
            netObj = GetComponent<NetworkObject>();
        }

        /// <summary>Called by the river when this object enters/leaves its trigger volume.</summary>
        public void SetInWater(RiverFlowController r, bool inside)
        {
            river = r;
            inWater = inside && r != null;
        }

        private bool IsLocallyControlled =>
            netObj == null || netObj.IsOwner;   // offline objects count as ours

        // ── CharacterController path (players) — Update, like the controller itself ──────────
        private void Update()
        {
            if (!inWater || cc == null || !IsLocallyControlled) return;
            if (fpc != null && !fpc.enabled) return;   // downed / cutscene / remote-disabled: don't shove

            float surface = river.SurfaceHeightAt(transform.position);
            float feet = transform.position.y;
            float submersion = cc.height > 0.01f ? Mathf.Clamp01((surface - feet) / cc.height) : 0f;
            if (submersion <= 0f) return;   // above the waterline (e.g. standing on the bridge)

            // Downstream shove scales with how deep they're wading.
            Vector3 push = river.Current * (characterPush * submersion);

            if (submersion >= swimSubmersion)
            {
                // SWIMMING: neutralise the controller's own gravity and ease toward float depth.
                if (fpc != null && VerticalVelocityField != null)
                    VerticalVelocityField.SetValue(fpc, 0f);
                float targetY = surface - floatDepth;
                push.y = (targetY - feet) * 2f;   // gentle spring, m/s
            }

            cc.Move(push * Time.deltaTime);
        }

        // ── Rigidbody path (props) — FixedUpdate, like all physics ───────────────────────────
        private void FixedUpdate()
        {
            if (!inWater || rb == null || rb.isKinematic) return;

            float surface = river.SurfaceHeightAt(rb.position);
            float depth = surface - rb.position.y;
            if (depth <= 0f) return;

            // Spring + damping: proportional lift, settles at the surface. Gravity stays on —
            // the spring only wins while submerged, which is exactly "partially disabled".
            float lift = depth * buoyancyStrength - rb.linearVelocity.y * buoyancyDamping;
            rb.AddForce(Vector3.up * lift, ForceMode.Acceleration);

            // Wash downstream, but never accelerate past the current itself.
            Vector3 flat = rb.linearVelocity; flat.y = 0f;
            if (Vector3.Dot(flat, river.Current.normalized) < river.Current.magnitude)
                rb.AddForce(river.Current.normalized * bodyCurrentAccel, ForceMode.Acceleration);
        }
    }
}
