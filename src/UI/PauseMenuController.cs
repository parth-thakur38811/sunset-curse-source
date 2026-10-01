using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using SunsetCurse.Audio;
using SunsetCurse.Core;

namespace SunsetCurse.UI
{
    /// <summary>
    /// In-game pause / settings overlay. Pressing Esc toggles it.
    ///
    ///   - SINGLE PLAYER: opening the overlay sets <c>Time.timeScale = 0</c> (game freezes).
    ///   - MULTIPLAYER: the world keeps running (we can't pause server-authoritative time without
    ///     desyncing other players). Cursor still unlocks so you can drag the sliders.
    ///
    /// Contents: same Master Volume + Look Sensitivity sliders as the MainMenu Settings page,
    /// PLUS an "EXIT TO MAIN MENU" button that shuts down NGO (if running) and loads MainMenu.
    ///
    /// SETUP: drop this on any GameObject in SampleScene (the GameClock GameObject is fine).
    /// No Inspector fields to set — it builds its own Canvas at runtime.
    /// </summary>
    public class PauseMenuController : MonoBehaviour
    {
        [SerializeField] private Key toggleKey = Key.Escape;
        [SerializeField] private string mainMenuSceneName = "MainMenu";
        [Tooltip("Optional horror TMP font (matches the menu's look). Leave empty for the default.")]
        [SerializeField] private TMP_FontAsset menuFont;

        private static readonly Color BloodRed = new Color(0.62f, 0.06f, 0.06f);
        private static readonly Color Bone = new Color(0.90f, 0.86f, 0.80f);
        private const float MarginX = 160f;

        private CanvasGroup group;
        private Slider volumeSlider, sensitivitySlider;
        private TMP_Text volumeValueLabel, sensitivityValueLabel;
        private bool open;
        private CursorLockMode prevCursorLock;
        private bool prevCursorVisible;

        private void Start()
        {
            EnsureEventSystem();
            BuildUI();
        }

        private void Update()
        {
            if (Keyboard.current == null) return;
            if (Keyboard.current[toggleKey].wasPressedThisFrame)
            {
                // If the settings overlay is closed but the INVENTORY is open, Esc just closes the
                // inventory — don't also pop settings on top of it. (Fixes the double-action bug.)
                if (!open && InventoryUI.Instance != null && InventoryUI.Instance.IsOpen)
                {
                    InventoryUI.Instance.Hide();
                    return;
                }
                if (open) Close(); else Open();
            }
        }

        // ───────────────────────────── Open / Close ─────────────────────────────

        private void Open()
        {
            open = true;
            // Pull latest values into the sliders in case they were changed elsewhere.
            if (volumeSlider != null) volumeSlider.value = GameSettings.MasterVolume;
            if (sensitivitySlider != null) sensitivitySlider.value = GameSettings.Sensitivity;
            RefreshValueLabels();

            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;

            // Pause time in single-player only. Multiplayer time stays running (server authority).
            if (DifficultyPreference.Mode == GameMode.SinglePlayer) Time.timeScale = 0f;

            // Remember cursor state and free it so the sliders/buttons can be clicked.
            prevCursorLock = Cursor.lockState;
            prevCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Close()
        {
            open = false;
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
            GameSettings.Save(); // flush slider tweaks to disk

            Time.timeScale = 1f;

            // Restore cursor — gameplay wants locked + hidden.
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ───────────────────────────── Slider handlers ─────────────────────────────

        private void OnVolumeChanged(float v)
        {
            if (AudioManager.Instance != null)
            {
                AudioManager.Instance.SetMasterVolume(v);
            }
            else
            {
                // Fallback if there's no AudioManager in the scene — still drives the global volume.
                GameSettings.MasterVolume = v;
                AudioListener.volume = Mathf.Clamp01(v);
            }
            RefreshValueLabels();
        }

        private void OnSensitivityChanged(float v)
        {
            GameSettings.Sensitivity = v;
            RefreshValueLabels();
        }

        private void RefreshValueLabels()
        {
            if (volumeValueLabel != null)
                volumeValueLabel.text = $"{Mathf.RoundToInt(GameSettings.MasterVolume * 100f)}%";
            if (sensitivityValueLabel != null)
                sensitivityValueLabel.text = $"{GameSettings.Sensitivity:0.00}";
        }

        // ───────────────────────────── Buttons ─────────────────────────────

        private void OnResume() => Close();

        private void OnExitToMainMenu()
        {
            Time.timeScale = 1f;
            GameSettings.Save();

            // Shut NGO down so the next session starts fresh.
            if (NetworkManager.Singleton != null
                && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient))
            {
                NetworkManager.Singleton.Shutdown();
            }

            // Unlock cursor for the menu.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            SceneManager.LoadScene(mainMenuSceneName);
        }

        // ───────────────────────────── UI construction ─────────────────────────────

        private void BuildUI()
        {
            var canvasGO = new GameObject("PauseMenu_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 250; // above HUD (100), below end-screen (300)
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            group = canvasGO.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            // Dim full-screen background.
            var bgGO = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(canvasGO.transform, false);
            StretchFull((RectTransform)bgGO.transform);
            bgGO.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.03f, 0.95f);

            var t = canvasGO.transform;

            // Heading
            var heading = NewText(t, "SETTINGS", 72, new Vector2(MarginX, 360f), new Vector2(1200f, 100f), Bone);
            heading.fontStyle = FontStyles.Bold;

            // MASTER VOLUME
            NewText(t, "MASTER VOLUME", 32, new Vector2(MarginX, 200f), new Vector2(600f, 50f), Bone);
            volumeSlider = NewSlider(t, new Vector2(MarginX, 140f), new Vector2(700f, 40f),
                                     0f, 1f, GameSettings.MasterVolume, OnVolumeChanged);
            volumeValueLabel = NewText(t, "100%", 32, new Vector2(MarginX + 730f, 140f),
                                       new Vector2(150f, 50f), new Color(1f, 0.85f, 0.35f));

            // SENSITIVITY
            NewText(t, "LOOK SENSITIVITY", 32, new Vector2(MarginX, 30f), new Vector2(600f, 50f), Bone);
            sensitivitySlider = NewSlider(t, new Vector2(MarginX, -30f), new Vector2(700f, 40f),
                                          0.1f, 3.0f, GameSettings.Sensitivity, OnSensitivityChanged);
            sensitivityValueLabel = NewText(t, "1.00", 32, new Vector2(MarginX + 730f, -30f),
                                            new Vector2(150f, 50f), new Color(1f, 0.85f, 0.35f));

            // RESUME (top-left next to Back)
            NewButton(t, "Resume", new Vector2(MarginX, -200f), OnResume);

            // EXIT TO MAIN MENU (centred at bottom for prominence)
            NewBigCenterButton(t, "EXIT TO MAIN MENU", new Vector2(0f, 90f), OnExitToMainMenu);

            RefreshValueLabels();
        }

        // ───────── UI helpers (mirrors MainMenuController so PauseMenu is self-contained) ─────────

        private TMP_Text NewText(Transform parent, string content, int size, Vector2 pos, Vector2 dim, Color color)
        {
            var go = new GameObject("Text_" + content, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = dim;

            var text = go.AddComponent<TextMeshProUGUI>();
            if (menuFont != null) text.font = menuFont;
            text.text = content;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.Left;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private Button NewButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(460f, 76f);

            var img = go.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);

            var text = NewText(go.transform, label.ToUpperInvariant(), 40, Vector2.zero,
                               new Vector2(460f, 70f), Color.white);
            text.name = "Label";
            text.characterSpacing = 10f;

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = text;
            var colors = btn.colors;
            colors.normalColor = Bone;
            colors.highlightedColor = BloodRed;
            colors.pressedColor = new Color(0.4f, 0.03f, 0.03f);
            colors.selectedColor = Bone;
            colors.fadeDuration = 0.12f;
            btn.colors = colors;
            btn.onClick.AddListener(onClick);
            return btn;
        }

        private Button NewBigCenterButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var btn = NewButton(parent, label, pos, onClick);
            var rt = (RectTransform)btn.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); // bottom-centre anchor
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(640f, 96f);
            var lbl = btn.GetComponentInChildren<TMP_Text>();
            if (lbl != null) { lbl.alignment = TextAlignmentOptions.Center; lbl.fontSize = 44; }
            return btn;
        }

        private Slider NewSlider(Transform parent, Vector2 pos, Vector2 size,
                                 float min, float max, float startValue,
                                 UnityEngine.Events.UnityAction<float> onChange)
        {
            var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(go.transform, false);
            var bgRt = (RectTransform)bgGO.transform;
            bgRt.anchorMin = new Vector2(0f, 0.3f); bgRt.anchorMax = new Vector2(1f, 0.7f);
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0.12f, 0.10f, 0.13f, 0.95f);

            var fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaGO.transform.SetParent(go.transform, false);
            var faRt = (RectTransform)fillAreaGO.transform;
            faRt.anchorMin = new Vector2(0f, 0.3f); faRt.anchorMax = new Vector2(1f, 0.7f);
            faRt.offsetMin = new Vector2(8f, 0f); faRt.offsetMax = new Vector2(-8f, 0f);

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            var fillRt = (RectTransform)fillGO.transform;
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero; fillRt.offsetMax = Vector2.zero;
            fillGO.GetComponent<Image>().color = BloodRed;

            var handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleAreaGO.transform.SetParent(go.transform, false);
            var haRt = (RectTransform)handleAreaGO.transform;
            haRt.anchorMin = Vector2.zero; haRt.anchorMax = Vector2.one;
            haRt.offsetMin = new Vector2(10f, 0f); haRt.offsetMax = new Vector2(-10f, 0f);

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(handleAreaGO.transform, false);
            var hRt = (RectTransform)handleGO.transform;
            hRt.anchorMin = new Vector2(0f, 0f); hRt.anchorMax = new Vector2(0f, 1f);
            hRt.sizeDelta = new Vector2(28f, 0f);
            handleGO.GetComponent<Image>().color = Bone;

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fillRt;
            slider.handleRect = hRt;
            slider.targetGraphic = handleGO.GetComponent<Image>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = startValue;
            slider.onValueChanged.AddListener(onChange);
            return slider;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            var module = go.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }
    }
}
