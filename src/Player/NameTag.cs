using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using TMPro;
using SunsetCurse.Net;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Floating nameplate above a remote player. The username is stored ON THE PLAYER as an
    /// owner-write NetworkVariable, so it survives the menu→game scene load. (Previously this read
    /// the username from <see cref="NetworkLobby"/>, but that lobby is a MENU-scene object which
    /// despawns when the gameplay scene loads — so every remote nameplate came up blank in-game.)
    ///
    /// The owning client publishes its own name (from <see cref="NetworkBootstrap.LocalUsername"/>,
    /// set from the menu); every other peer reads the replicated value. Hidden for the local owner
    /// because you don't see your own name floating in front of you in first-person.
    ///
    /// SETUP: add to PlayerCapsule prefab. Do NOT put it in NetworkPlayer's "Owner Only
    /// Behaviours" — every peer needs it running on every OTHER player's instance to draw the tag.
    /// </summary>
    public class NameTag : NetworkBehaviour
    {
        [Header("Placement")]
        [Tooltip("Local-space Y offset from PlayerCapsule transform — should be slightly above the head.")]
        [SerializeField] private float heightAboveHead = 2.4f;
        [Tooltip("World-space text scale (Canvas world units are huge; 0.005–0.02 is sane).")]
        [SerializeField] private float worldScale = 0.01f;

        [Header("Look")]
        [SerializeField] private int fontSize = 36;
        [SerializeField] private Color textColor = new Color(0.95f, 0.95f, 0.95f);
        [SerializeField] private Color outlineColor = new Color(0f, 0f, 0f, 0.85f);

        // Owner-write username that lives on the player object → replicates to every peer and
        // survives the scene transition. Everyone reads; only the owner writes.
        private readonly NetworkVariable<FixedString32Bytes> netUsername =
            new NetworkVariable<FixedString32Bytes>(default,
                NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private Canvas canvas;
        private TextMeshProUGUI label;
        private SunsetCurse.Core.PlayerInventory aliveSource;   // hides the tag once this player dies

        private void Start() => BuildCanvas();

        public override void OnNetworkSpawn()
        {
            if (IsOwner) PublishLocalUsername();
        }

        private void LateUpdate()
        {
            if (canvas == null) return;

            // A dead player's tag dies with them — the body is hidden on death, and a nameplate
            // floating over empty air gave the corpse away to everyone.
            if (aliveSource == null) aliveSource = GetComponent<SunsetCurse.Core.PlayerInventory>();
            if (aliveSource != null && !aliveSource.IsAlive)
            {
                if (canvas.gameObject.activeSelf) canvas.gameObject.SetActive(false);
                return;
            }

            if (IsOwner)
            {
                // Never show your own nameplate. Keep trying to publish until the name is set —
                // covers the case where the player spawned before the username was committed
                // (e.g. the host's player exists before they finish typing / clicking HOST).
                if (canvas.gameObject.activeSelf) canvas.gameObject.SetActive(false);
                if (netUsername.Value.Length == 0) PublishLocalUsername();
                return;
            }

            if (label != null)
            {
                string name = ResolveName();
                if (!string.IsNullOrEmpty(name) && label.text != name) label.text = name;
            }

            // Billboard — face the LOCAL camera.
            var cam = Camera.main;
            if (cam != null)
                canvas.transform.forward = cam.transform.forward;
        }

        private void PublishLocalUsername()
        {
            if (!IsOwner) return;
            string local = NetworkBootstrap.LocalUsername;
            if (string.IsNullOrWhiteSpace(local)) return;
            local = NetworkLobby.ClampUsername(local);   // byte-safe: Hindi/emoji names can't overflow
            if (local.Length == 0) return;
            var fixedName = new FixedString32Bytes(local);
            if (!netUsername.Value.Equals(fixedName)) netUsername.Value = fixedName;
        }

        // Prefer the name replicated on the player itself; fall back to the persistent SessionState
        // roster (survives into the game scene), then the lobby roster (menu only).
        private string ResolveName()
        {
            if (netUsername.Value.Length > 0) return netUsername.Value.ToString();
            if (SessionState.Instance != null)
            {
                string n = SessionState.Instance.GetUsername(OwnerClientId);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            if (NetworkLobby.Instance != null)
            {
                string n = NetworkLobby.Instance.GetUsername(OwnerClientId);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            return string.Empty;
        }

        private void BuildCanvas()
        {
            var go = new GameObject("NameTag_Canvas", typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * heightAboveHead;
            go.transform.localScale = Vector3.one * worldScale;
            canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(400f, 80f);

            var textGO = new GameObject("Label", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var trt = (RectTransform)textGO.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

            label = textGO.AddComponent<TextMeshProUGUI>();
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = fontSize;
            label.color = textColor;
            label.raycastTarget = false;
            label.text = "";
            label.fontStyle = FontStyles.Bold;

            // Hide our own tag immediately if we already know we're the owner (OnNetworkSpawn may
            // have run before Start built the canvas).
            if (IsSpawned && IsOwner) canvas.gameObject.SetActive(false);
        }
    }
}
