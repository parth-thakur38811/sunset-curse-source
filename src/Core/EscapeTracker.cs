using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace SunsetCurse.Core
{
    /// <summary>
    /// SHARED, NETWORKED count of ritual potions collected across all players. The host owns the
    /// authoritative count via a NetworkVariable; every peer mirrors it locally and updates their
    /// own "Potions: X / Y" HUD when the value changes. Reach the required count (default 7) and
    /// every peer fires <see cref="OnEscapeReady"/> at the same moment.
    ///
    /// SETUP:
    ///   1. In SampleScene, add (or keep) a GameObject for this script.
    ///   2. Add a **NetworkObject** component to the same GameObject (auto-spawns on scene load
    ///      so the NetworkVariable replicates).
    ///   3. Tune "Required To Escape" in the Inspector (default 7).
    /// </summary>
    public class EscapeTracker : NetworkBehaviour
    {
        public static EscapeTracker Instance { get; private set; }

        [Tooltip("How many ritual potions needed to perform the escape ritual (default 7 = one per night).")]
        [SerializeField] private int requiredToEscape = 7;

        // Server-write, everyone reads. Increment via Collect() on the server (or via Spawner's
        // ServerRpc) — clients can't write directly.
        private readonly NetworkVariable<int> netCollected = new NetworkVariable<int>(0);
        // Required potion count = number of nights (2 / 4 / 7). Server seeds it from the chosen
        // night-count; replicates so every client's "x / N" HUD shows the correct N.
        private readonly NetworkVariable<int> netRequired = new NetworkVariable<int>(0);

        public event Action<int, int> OnProgressChanged;  // (collected, required)
        public event Action OnEscapeReady;

        public int Collected => netCollected.Value;
        public int Required => netRequired.Value > 0 ? netRequired.Value : requiredToEscape;
        public bool HasEnoughToEscape => Collected >= Required;

        private Text counterText;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();   // NGO's own NetworkBehaviour cleanup must still run
        }

        public override void OnNetworkSpawn()
        {
            netCollected.OnValueChanged += HandleCollectedChanged;
            netRequired.OnValueChanged += HandleRequiredChanged;

            // Server seeds the potion goal from the chosen night-count (2 / 4 / 7 = one per night),
            // via SessionState so it matches the host's choice for every player.
            if (IsServer)
                netRequired.Value = Mathf.Max(1, SunsetCurse.Net.SessionState.EffectiveTotalNights);

            // Late-joiner / initial — sync the UI to the values we already have.
            UpdateUI();
        }

        public override void OnNetworkDespawn()
        {
            netCollected.OnValueChanged -= HandleCollectedChanged;
            netRequired.OnValueChanged -= HandleRequiredChanged;
        }

        private void HandleRequiredChanged(int prev, int next) => UpdateUI();

        private void Start()
        {
            BuildCounterUI();
            UpdateUI();
        }

        // Whether the escape ritual has already been performed. Once it has, the "break the
        // curse at the altar!" banner is stale — the counter hides itself for good.
        private bool ritualCompleted;
        private SunsetCurse.World.RitualSiteNet ritualSite;

        private void Update()
        {
            // Cheap endgame-only poll: once all potions are in, watch for the ritual completing
            // (RitualSiteNet's netCompleted replicates to every peer) and clear the banner.
            if (ritualCompleted || !HasEnoughToEscape) return;
            if (ritualSite == null)
            {
                ritualSite = FindFirstObjectByType<SunsetCurse.World.RitualSiteNet>();
                if (ritualSite == null) return;
            }
            if (ritualSite.Completed)
            {
                ritualCompleted = true;
                UpdateUI();
            }
        }

        /// <summary>Add to the shared collected count. SERVER-ONLY — called from the spawner's
        /// ServerRpc when the pickup is validated. In SP the local player IS the server.</summary>
        public void Collect(int amount)
        {
            if (amount <= 0) return;
            if (IsSpawned && !IsServer) return;             // ignore client-side calls
            int prev = netCollected.Value;
            int next = Mathf.Clamp(prev + amount, 0, int.MaxValue);
            netCollected.Value = next;
            if (!IsSpawned) HandleCollectedChanged(prev, next);   // SP fallback (no NV callback offline)
            Debug.Log($"[EscapeTracker] Ritual Potions: {next}/{Required}");
        }

        private void HandleCollectedChanged(int prev, int next)
        {
            OnProgressChanged?.Invoke(next, Required);
            UpdateUI();
            if (next >= Required && prev < Required)
            {
                Debug.Log("[EscapeTracker] All ritual potions gathered — perform the ritual at the altar!");
                OnEscapeReady?.Invoke();
            }
        }

        private void UpdateUI()
        {
            if (counterText == null) return;
            if (ritualCompleted)
            {
                // Ritual performed — the potion objective is finished, the banner goes away.
                counterText.text = string.Empty;
            }
            else if (HasEnoughToEscape)
            {
                counterText.text = "All potions gathered — break the curse at the altar!";
                counterText.color = new Color(1f, 0.85f, 0.2f);
            }
            else
            {
                counterText.text = $"Ritual Potions: {Collected} / {Required}";
                counterText.color = Color.white;
            }
        }

        private void BuildCounterUI()
        {
            var canvasGO = new GameObject("EscapeCounter_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var textGO = new GameObject("CounterText", typeof(RectTransform));
            textGO.transform.SetParent(canvasGO.transform, false);
            var rt = (RectTransform)textGO.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -24f);
            rt.sizeDelta = new Vector2(700f, 50f);

            counterText = textGO.AddComponent<Text>();
            counterText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                               ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            counterText.alignment = TextAnchor.MiddleCenter;
            counterText.fontSize = 26;
            counterText.color = Color.white;
            counterText.raycastTarget = false;

            var outline = textGO.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
        }
    }
}
