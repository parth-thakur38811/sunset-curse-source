using UnityEngine;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Parents a 3D prop (the lantern model) to the player's RIGHT-HAND bone so teammates see
    /// every player carrying something instead of a floating light cone. Runs on EVERY peer
    /// (NOT owner-only) so all clients see the lantern on all players.
    ///
    /// The light itself stays on the player's camera (see <see cref="Flashlight"/>) — purely
    /// cosmetic prop. Tweak the local-space offset/rotation/scale fields in the Inspector if the
    /// prop sits in the wrong spot — Mixamo / FBX hand bones don't all agree on forward axis.
    ///
    /// SETUP:
    ///   1. On PlayerCapsule prefab: add this component.
    ///   2. Drag Assets/Polytope Studio/Lowpoly_Props/Prefabs/PT_Hand_Lantern_01.prefab into
    ///      "Lantern Prefab".
    ///   3. (Optional) Tweak offsets if the lantern looks wrong on the hand.
    ///   4. DO NOT add to NetworkPlayer's owner-only lists — every peer needs to spawn it.
    /// </summary>
    public class HeldLanternProp : MonoBehaviour
    {
        [Header("Prop")]
        [Tooltip("The lantern / flashlight prop to attach. Recommended: PT_Hand_Lantern_01.")]
        [SerializeField] private GameObject lanternPrefab;

        [Header("Attachment to right-hand bone")]
        [Tooltip("Local-space offset from the bone (the bone faces forward along its local axis).")]
        [SerializeField] private Vector3 localOffset = new Vector3(0.02f, 0.05f, 0.08f);
        [Tooltip("Local-space rotation in Euler degrees.")]
        [SerializeField] private Vector3 localEuler = new Vector3(0f, 90f, 0f);
        [Tooltip("Scale relative to the bone. 0.4–0.6 is usually right for the PT lantern.")]
        [SerializeField] private float scale = 0.5f;

        [Header("Bone search (humanoid path tried first)")]
        [Tooltip("Fallback bone-name search if the rig isn't humanoid.")]
        [SerializeField] private string[] handNameKeywords = { "RightHand", "Right_Hand", "Hand_R", "R_Hand", "hand.R" };

        private GameObject spawnedProp;
        /// <summary>The spawned lantern's transform — Flashlight reads this to position the beam.</summary>
        public Transform Prop => spawnedProp != null ? spawnedProp.transform : null;

        private void Start() => Attach();

        /// <summary>Re-attach the lantern to the CURRENT (active) model's hand bone. Called by
        /// PlayerAppearance after it swaps in a per-player character model, so the lantern moves to
        /// the new hand and picks up its scale.</summary>
        public void ReAttach() => Attach();

        private void Attach()
        {
            if (lanternPrefab == null)
            {
                Debug.LogWarning("[HeldLanternProp] No lantern prefab assigned.", this);
                return;
            }

            // Idempotent — safe to call again after a model swap.
            if (spawnedProp != null) { Destroy(spawnedProp); spawnedProp = null; }

            Transform handBone = FindRightHandBone();
            if (handBone == null)
            {
                Debug.LogWarning("[HeldLanternProp] No right-hand bone found — prop not attached. " +
                                 "Add the bone name to handNameKeywords if your rig uses a different scheme.", this);
                return;
            }

            spawnedProp = Instantiate(lanternPrefab, handBone, false);
            spawnedProp.name = "HeldLantern";

            // COMPENSATE for the hand bone's WORLD scale. localPosition/localScale are multiplied by
            // the bone's lossyScale, so a character model that PlayerAppearance scaled UP (small
            // native model → big scale factor) gave a huge hand bone → a GIANT lantern in the wrong
            // spot. Dividing by the bone scale keeps the lantern a CONSISTENT world size + offset on
            // every player, whatever their model's scale.
            float boneScale = handBone.lossyScale.x;
            if (Mathf.Abs(boneScale) < 0.0001f) boneScale = 1f;

            spawnedProp.transform.localPosition = localOffset / boneScale;
            spawnedProp.transform.localEulerAngles = localEuler;
            spawnedProp.transform.localScale = Vector3.one * (scale / boneScale);
            // Strip any colliders the prefab might carry (we don't want it blocking interaction
            // raycasts or pushing the player around).
            foreach (var c in spawnedProp.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        }

        private Transform FindRightHandBone()
        {
            // Try humanoid mapping first.
            var anim = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
            if (anim != null && anim.isHuman)
            {
                var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                if (hand != null) return hand;
            }

            // Fallback: name search through children.
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                foreach (var k in handNameKeywords)
                    if (!string.IsNullOrEmpty(k) && n.Equals(k, System.StringComparison.OrdinalIgnoreCase))
                        return t;
                foreach (var k in handNameKeywords)
                    if (!string.IsNullOrEmpty(k) && n.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return t;
            }
            return null;
        }

        private void OnDestroy()
        {
            if (spawnedProp != null) Destroy(spawnedProp);
        }
    }
}
