using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;
using SunsetCurse.Core;
using SunsetCurse.Audio;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Per-player handheld flashlight. F toggles a spot light that aims along the player's view.
    /// In multiplayer the on/off state replicates to every peer via a NetworkVariable, so
    /// teammates SEE each other's beam cones.
    ///
    /// Architecture:
    ///   • NetworkBehaviour with one NetworkVariable&lt;bool&gt; netIsOn (owner-write).
    ///   • Runs on EVERY peer — each one creates a local Light GameObject parented to this
    ///     player's PlayerCameraRoot (which moves with the player and survives scene migration —
    ///     unlike Camera.main, which is per-scene).
    ///   • Only the LOCAL OWNER handles the F-key input + builds the "Press F" hint UI.
    ///   • Non-owners just react to the NetworkVariable change to show/hide the beam.
    ///
    /// SETUP: keep this on PlayerCapsule. Leave it in NetworkPlayer's "Owner Only Behaviours"
    /// list — the input/hint paths bail when !IsOwner, but the OnValueChanged callback still
    /// fires while disabled, so the remote beam still toggles correctly.
    /// </summary>
    public class Flashlight : NetworkBehaviour
    {
        [Header("Beam look (tune to taste) — Phasmophobia-style: bright, long, hard circle")]
        [Tooltip("Near-white, very slightly cold so it reads as a cheap torch.")]
        [SerializeField] private Color color = new Color(0.95f, 0.96f, 1f);
        [Tooltip("High on purpose: the cookie's grey mid-ring multiplies the light DOWN, so the " +
                 "raw intensity must be strong for the ring to still read bright at range.")]
        [SerializeField] private float intensity = 100f;
        [Tooltip("How far the beam throws. Anything inside the circle should be visible out to here.")]
        [SerializeField] private float range = 70f;
        [Range(10f, 120f)] [SerializeField] private float outerAngle = 42f;
        [Tooltip("Inner angle as % of outer — high = hard-edged circle (the cookie shapes the rim too).")]
        [Range(0f, 100f)] [SerializeField] private float innerAnglePercent = 80f;
        [Tooltip("Shadows make walls/trees BLOCK the beam — essential for the fixed-circle look " +
                 "(off, the light bleeds through geometry).")]
        [SerializeField] private bool castShadows = true;
        [SerializeField] private Texture cookie;

        [Header("Input / feel")]
        [SerializeField] private Key toggleKey = Key.F;
        [SerializeField] private AudioClip clickSfx;
        [Tooltip("Fallback local offset from PlayerCameraRoot — used only when no HeldLanternProp " +
                 "is on this player. When the lantern exists, the beam emanates from THE LANTERN " +
                 "and this is ignored.")]
        [SerializeField] private Vector3 localOffset = new Vector3(0.2f, -0.2f, 0f);
        [Tooltip("World-space nudge to position the light slightly above the lantern model so the " +
                 "beam origin sits where you'd expect (top of the lantern, not inside it).")]
        [SerializeField] private Vector3 lanternEmitOffset = new Vector3(0f, 0.08f, 0f);

        // ───── Networked state ─────
        private readonly NetworkVariable<bool> netIsOn = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private Light beam;
        private TMP_Text hint;          // owner-only
        private bool subscribedToClock; // owner-only
        private HeldLanternProp lanternProp;
        private Transform cameraRoot;

        public bool IsOn => netIsOn.Value;

        private void Awake()
        {
            lanternProp = GetComponent<HeldLanternProp>() ?? GetComponentInChildren<HeldLanternProp>(true);
        }

        // ───────────────────────────── Lifecycle ─────────────────────────────

        public override void OnNetworkSpawn()
        {
            cameraRoot = FindCameraRoot();
            // The beam is created on EVERY peer so teammates see your flashlight too.
            CreateLight();
            if (beam != null) beam.enabled = netIsOn.Value;
            netIsOn.OnValueChanged += HandleNetIsOnChanged;

            // Owner-only: input prompt + GameClock subscription.
            if (IsOwner)
            {
                BuildHint();
                if (GameClock.Instance != null)
                {
                    GameClock.Instance.OnNightStart += HandlePhase;
                    GameClock.Instance.OnDayStart   += HandlePhase;
                    subscribedToClock = true;
                }
                RefreshHint();
            }
        }

        public override void OnNetworkDespawn()
        {
            netIsOn.OnValueChanged -= HandleNetIsOnChanged;
            if (subscribedToClock && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart -= HandlePhase;
                GameClock.Instance.OnDayStart   -= HandlePhase;
                subscribedToClock = false;
            }
        }

        // ───────────────────────────── Input (owner only) ─────────────────────────────

        private void Update()
        {
            // Remote viewers don't handle input for someone else's flashlight.
            if (IsSpawned && !IsOwner) return;
            if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
                Toggle();
        }

        private void Toggle()
        {
            bool next = !netIsOn.Value;
            if (IsSpawned)
            {
                if (!IsOwner) return;             // safety
                netIsOn.Value = next;             // OnValueChanged fires on every peer → beam toggles
            }
            else
            {
                // Offline fallback (no NGO running) — flip directly.
                if (beam != null) beam.enabled = next;
                RefreshHint();
            }

            if (clickSfx != null && AudioManager.Instance != null)
                AudioManager.Instance.PlaySfx2D(clickSfx);
        }

        private void HandleNetIsOnChanged(bool prev, bool next)
        {
            // Fires on every peer including the owner. Just mirror the state into the light.
            if (beam != null) beam.enabled = next;
            if (IsOwner) RefreshHint();
        }

        // ───────────────────────────── Light + hint construction ─────────────────────────────

        private void CreateLight()
        {
            // Parent to the PLAYER ROOT (not the camera root) so we can drive position + rotation
            // each LateUpdate without fighting the parent transform. Position comes from the
            // lantern prop (so the beam visually emanates from the lantern), rotation comes from
            // PlayerCameraRoot (so the beam aims along the player's view, not the wobbling hand).
            var go = new GameObject("FlashlightBeam");
            go.transform.SetParent(transform, false);

            beam = go.AddComponent<Light>();
            beam.type = LightType.Spot;
            beam.color = color;
            beam.intensity = intensity;
            beam.range = range;
            beam.spotAngle = outerAngle;
            beam.innerSpotAngle = outerAngle * Mathf.Clamp01(innerAnglePercent / 100f);
            beam.shadows = castShadows ? LightShadows.Soft : LightShadows.None;
            beam.renderMode = LightRenderMode.ForcePixel;
            if (cookie != null) beam.cookie = cookie;

            // Shadow resolution: URP's default tier for a new light is already HIGH (1024px on the
            // pipeline's tier table), which is what a view-filling spot light needs — nothing to set.

            beam.enabled = false;                                 // off until netIsOn says otherwise
        }

        /// <summary>Drive the beam transform AFTER the animation rig has updated the hand bone
        /// this frame — otherwise the light would lag one frame behind the lantern.</summary>
        private void LateUpdate()
        {
            if (beam == null) return;

            // Aim direction — always from PlayerCameraRoot (stable, follows owner's view; for
            // remote viewers it's body-yaw only, which matches the rest of the multiplayer aim).
            if (cameraRoot != null)
                beam.transform.rotation = cameraRoot.rotation;

            // Origin position — from the held lantern prop if present, otherwise the legacy
            // camera-root offset.
            Transform propT = lanternProp != null ? lanternProp.Prop : null;
            if (propT != null)
            {
                beam.transform.position = propT.position + propT.TransformDirection(lanternEmitOffset);
            }
            else if (cameraRoot != null)
            {
                beam.transform.position = cameraRoot.TransformPoint(localOffset);
            }
        }

        private Transform FindCameraRoot()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.CompareTag("CinemachineTarget") || t.name == "PlayerCameraRoot") return t;
            return null;
        }

        private void HandlePhase(int day) => RefreshHint();

        private bool IsNight =>
            GameClock.Instance != null && GameClock.Instance.CurrentPhase == GameClock.Phase.Night;

        private void RefreshHint()
        {
            if (hint != null)
                hint.text = (IsNight && !netIsOn.Value) ? $"Press {toggleKey} to turn on Flashlight" : string.Empty;
        }

        private void BuildHint()
        {
            var canvasGO = new GameObject("Flashlight_Hint",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var textGO = new GameObject("HintText", typeof(RectTransform));
            textGO.transform.SetParent(canvasGO.transform, false);
            var rt = (RectTransform)textGO.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -120f);
            rt.sizeDelta = new Vector2(900f, 60f);

            hint = textGO.AddComponent<TextMeshProUGUI>();
            hint.alignment = TextAlignmentOptions.Center;
            hint.fontSize = 30;
            hint.color = new Color(0.9f, 0.9f, 0.92f);
            hint.raycastTarget = false;
            hint.text = string.Empty;
        }
    }
}
