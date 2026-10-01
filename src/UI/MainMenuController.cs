using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using Unity.Netcode;
using SunsetCurse.Audio;
using SunsetCurse.Core;
using SunsetCurse.Net;

namespace SunsetCurse.UI
{
    /// <summary>
    /// The main menu, built entirely in code (consistent with our other UI: HUD, GameOver,
    /// ScreenMessage). Shows the background image, the game title, the studio credit, and the
    /// Play / Settings / Quit buttons, then plays the menu music through the AudioManager.
    ///
    /// It starts HIDDEN and is faded in by <see cref="RevealMenu"/> — the <see cref="SplashScreen"/>
    /// calls that when the studio intro finishes (so the menu appears as the splash fades away).
    /// If there is no SplashScreen in the scene, it reveals itself immediately.
    ///
    /// SETUP (in the MainMenu scene):
    ///   1. Create an empty GameObject "MainMenu", add this component.
    ///   2. Assign Background Image (your menu art, imported as a Sprite), Menu Music and Game
    ///      Music clips, and set Game Scene Name to your gameplay scene (must be in Build Settings).
    /// </summary>
    public class MainMenuController : MonoBehaviour
    {
        [Header("Branding")]
        [SerializeField] private string gameTitle = "SUNSET CURSE";
        [SerializeField] private string studioCredit = "Couch Crash Studio";
        [Tooltip("Your menu background. Import the image with Texture Type = Sprite (2D and UI).")]
        [SerializeField] private Sprite backgroundImage;
        [Tooltip("OPTIONAL: a pre-made title logo (PNG with transparency). If set it REPLACES the " +
                 "text title — the best route for a stylised horror wordmark.")]
        [SerializeField] private Sprite titleImage;
        [Tooltip("OPTIONAL: a horror TMP font for all menu text. Import a .ttf, then right-click it " +
                 "in Project > Create > TextMeshPro > Font Asset, and drop the asset here.")]
        [SerializeField] private TMP_FontAsset menuFont;

        [Header("Audio (placeholders OK)")]
        [SerializeField] private AudioClip menuMusic;
        [SerializeField] private AudioClip gameMusic;
        [SerializeField] private AudioClip buttonClickSfx;

        [Header("Flow")]
        [Tooltip("Gameplay scene to load on Play. MUST be added to File > Build Settings.")]
        [SerializeField] private string gameSceneName = "SampleScene";
        [SerializeField] private float fadeDuration = 0.6f;

        private CanvasGroup menuGroup;   // title + credit + buttons (fades in after the splash)
        private CanvasGroup fadeCover;   // full-screen black, fades in for the scene transition
        private CanvasGroup infoGroup;   // the Info page overlay
        private CanvasGroup difficultyGroup;  // Difficulty + Mode selection (after Play)
        private CanvasGroup mpLobbyGroup;     // Multiplayer Lobby (after Difficulty in MP mode)
        private CanvasGroup settingsGroup;    // Settings page (volume + sensitivity)
        private Slider settingsVolumeSlider;
        private Slider settingsSensitivitySlider;
        private TMP_Text settingsVolumeValueLabel;
        private TMP_Text settingsSensitivityValueLabel;
        private TMP_Text easyLabel, mediumLabel, hardLabel;
        private TMP_Text singleLabel, multiLabel;
        private TMP_Text nights2Label, nights4Label, nights7Label;
        private RectTransform titleRect;
        private bool revealed;
        private bool leaving;

        // Multiplayer Lobby UI refs.
        private TMP_InputField mpUsernameInput;
        private TMP_InputField mpJoinCodeInput;
        private TMP_Text mpStatusText;
        private TMP_Text mpJoinCodeDisplay;
        private TMP_Text mpPlayersText;
        private TMP_Text mpToastText;
        private Button mpHostBtn, mpJoinBtn, mpStartGameBtn;

        // Multiplayer Lobby state.
        private readonly Dictionary<ulong, string> mpKnownUsernames = new Dictionary<ulong, string>();
        private string mpLastToast;
        private float mpLastToastTime;
        private const float MpToastDuration = 4f;
        private bool mpLobbySubscribed;
        private bool mpUsernameRegistered;
        private bool mpLobbyVisible;

        private static readonly Color BloodRed = new Color(0.62f, 0.06f, 0.06f);
        private static readonly Color Bone = new Color(0.90f, 0.86f, 0.80f);
        private const float MarginX = 160f;   // shared left edge for title + buttons + credit

        private const string InfoText =
            "<b><color=#E0B8B8>THE CURSE</color></b>\n" +
            "A traveler. A breakdown. A village that should not exist.\n" +
            "Each dusk steals minutes from your daylight. Each night, the forest grows bolder.\n" +
            "Hold the village. Recover what the curse demands. Break it — before the sun stops rising at all.\n\n" +
            "<b><color=#E0B8B8>SURVIVAL</color></b>\n" +
            "<b>Day</b>  —  Forage berries. Plan. Stay close to the village.\n" +
            "<b>Night</b>  —  The compound gates open. Take what you came for and run.\n" +
            "Seven nights, seven ritual potions. Begin the ritual at the altar — but survive what it draws to you.\n" +
            "<i>Touch is death.</i>\n\n" +
            "<b><color=#E0B8B8>CONTROLS</color></b>\n" +
            "<b>WASD</b>  Move        <b>Mouse</b>  Look        <b>Shift</b>  Sprint\n" +
            "<b>E</b>  Interact        <b>F</b>  Flashlight        <b>Esc</b>  Pause / Settings\n\n" +
            "<b><color=#E0B8B8>CREDITS</color></b>\n" +
            "A solo project by <b>Parth Thakur</b> for <b>Couch Crash Studio</b>.\n" +
            "Built in Unity 6. Assets from Unity Asset Store and Sketchfab.com " +
            "(Cinemachine, Flooded Grounds, Free Fantasy Items, JP Environmental Asset Pack, " +
            "JTS Pagoda Architecture, Mixamo animations, Numb — Ambient Music, Polytope Plague Doctor, " +
            "StarterAssets, Stylized Player Character, VillagePack).\n\n" +
            "<b><color=#E0B8B8>LEGAL</color></b>\n" +
            "© Couch Crash Studios. All rights reserved.\n" +
            "Reproduction or distribution of this game's content without written permission is prohibited.";

        private const string Version = "Version 1.0.0  —  Early Build";

        private void Start()
        {
            EnsureEventSystem();
            BuildUI();

            // The splash will reveal us; if there isn't one, show immediately.
            if (FindFirstObjectByType<SplashScreen>() == null) RevealMenu();
        }

        /// <summary>Fade the menu in and start the menu music. Safe to call more than once.</summary>
        public void RevealMenu()
        {
            if (revealed) return;
            revealed = true;

            if (menuMusic != null && AudioManager.Instance != null)
                AudioManager.Instance.PlayAmbient(menuMusic);

            StartCoroutine(FadeInRoutine());
        }

        // ────────────────────────────── Button handlers ──────────────────────────────

        private void OnPlay()
        {
            if (leaving) return;
            Click();
            RefreshDifficultyLabels();
            StartCoroutine(FadeCanvasGroup(difficultyGroup, 1f, true));
        }

        private void OnPickDifficulty(Difficulty d)
        {
            Click();
            // All three difficulties available in both modes — host picks for the lobby.
            DifficultyPreference.Selected = d;
            RefreshDifficultyLabels();
        }

        private void OnPickMode(GameMode m)
        {
            Click();
            DifficultyPreference.Mode = m;
            // No restrictions — host's chosen difficulty applies regardless of mode.
            RefreshDifficultyLabels();
        }

        private void OnPickNights(int nights)
        {
            Click();
            DifficultyPreference.TotalNights = nights;   // 2 / 4 / 7
            RefreshDifficultyLabels();
        }

        private void OnStartGame()
        {
            if (leaving) return;
            Click();

            // MULTIPLAYER → open the Multiplayer Lobby page (username + host/join + START GAME).
            // The actual scene transition happens when the host presses START GAME inside the lobby.
            if (DifficultyPreference.Mode == GameMode.Multiplayer)
            {
                StartCoroutine(FadeCanvasGroup(difficultyGroup, 0f, false));
                StartCoroutine(FadeCanvasGroup(mpLobbyGroup, 1f, true));
                mpLobbyVisible = true;
                ResetMpLobbyState();
                return;
            }

            // SINGLE PLAYER → straight to the gameplay scene.
            if (!Application.CanStreamedLevelBeLoaded(gameSceneName))
            {
                Debug.LogError($"[MainMenu] Scene '{gameSceneName}' is not in Build Settings — " +
                               "add it via File > Build Settings (Add Open Scenes).", this);
                ScreenMessage.Show("Game scene not set up yet", 2.5f);
                return;
            }
            Debug.Log($"[MainMenu] Starting game — Difficulty={DifficultyPreference.Selected}, Mode={DifficultyPreference.Mode}");
            leaving = true;
            StartCoroutine(PlayRoutine());
        }

        // ───────────────────────── Multiplayer Lobby handlers ─────────────────────────

        private void OnHostClick()
        {
            Click();
            if (!ValidateUsername()) return;
            if (NetworkBootstrap.Instance == null) { SetMpStatus("No NetworkBootstrap in scene.", true); return; }
            NetworkBootstrap.Instance.StartHostWithRelay();
        }

        private void OnJoinClick()
        {
            Click();
            if (!ValidateUsername()) return;
            if (string.IsNullOrWhiteSpace(mpJoinCodeInput.text)) { SetMpStatus("Join code is required.", true); return; }
            if (NetworkBootstrap.Instance == null) { SetMpStatus("No NetworkBootstrap in scene.", true); return; }
            NetworkBootstrap.Instance.StartClientWithRelay(mpJoinCodeInput.text);
        }

        private void OnStartGameClick()
        {
            Click();
            if (NetworkLobby.Instance != null && NetworkLobby.Instance.IsSpawned)
            {
                Debug.Log($"[MainMenu] Host pressing START GAME with {mpKnownUsernames.Count} player(s).");
                NetworkLobby.Instance.StartGame();
                // NetworkLobby triggers an NGO scene load → all clients transition into gameplay.
                // We can also fade to black locally for a smoother visual.
                StartCoroutine(FadeOutLobby());
            }
        }

        private IEnumerator FadeOutLobby()
        {
            // Disable interaction so they can't double-press.
            mpLobbyGroup.interactable = false;
            float t = 0f;
            while (t < fadeDuration)
            {
                t += Time.unscaledDeltaTime;
                fadeCover.alpha = Mathf.Clamp01(t / fadeDuration);
                yield return null;
            }
            fadeCover.alpha = 1f;
            // NGO scene load is in flight — Unity will swap scenes shortly.
        }

        private void OnMpLobbyBack()
        {
            Click();
            // Disconnect if currently connected so the user can return to the menu cleanly.
            if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient))
            {
                NetworkManager.Singleton.Shutdown();
            }
            mpLobbyVisible = false;
            StartCoroutine(FadeCanvasGroup(mpLobbyGroup, 0f, false));
            StartCoroutine(FadeCanvasGroup(difficultyGroup, 1f, true));
        }

        private bool ValidateUsername()
        {
            if (string.IsNullOrWhiteSpace(mpUsernameInput.text))
            {
                SetMpStatus("Username is REQUIRED.", true);
                return false;
            }
            return true;
        }

        private void SetMpStatus(string text, bool isError = false)
        {
            if (mpStatusText == null) return;
            mpStatusText.text = text;
            mpStatusText.color = isError ? new Color(1f, 0.45f, 0.45f) : new Color(0.78f, 0.78f, 0.78f);
        }

        private void ResetMpLobbyState()
        {
            mpKnownUsernames.Clear();
            mpUsernameRegistered = false;
            mpLastToast = null;
            if (mpJoinCodeDisplay != null) mpJoinCodeDisplay.text = "";
            if (mpPlayersText != null) mpPlayersText.text = "Players (0/4):";
            SetMpStatus("Enter a username, then HOST a session or paste a code and JOIN.");
        }

        // Rebuilds our local cache from the networked roster (single source of truth = NetworkLobby).
        private void HandleMpPlayerListChanged()
        {
            mpKnownUsernames.Clear();
            if (NetworkLobby.Instance == null) return;
            foreach (var entry in NetworkLobby.Instance.Players)
                mpKnownUsernames[entry.clientId] = entry.username.ToString();
        }

        private void HandleMpPlayerToast(string username)
        {
            mpLastToast = $"{username} has joined.";
            mpLastToastTime = Time.unscaledTime;
        }

        // Runs every frame WHILE the Multiplayer Lobby panel is open. Refreshes status text, the
        // join code, the player counter/list, and the fading "X has joined." toast. Also lazily
        // subscribes to NetworkLobby (it only exists after host clicks HOST) and auto-registers
        // the local user's name once connected.
        private void Update()
        {
            if (!mpLobbyVisible) return;

            // Lazy subscribe once NetworkLobby is spawned (scene NetworkObject — only after someone
            // hosts). NetworkList auto-syncs late joiners so we don't need to remember per-event.
            if (!mpLobbySubscribed && NetworkLobby.Instance != null && NetworkLobby.Instance.IsSpawned)
            {
                NetworkLobby.Instance.OnPlayerListChanged += HandleMpPlayerListChanged;
                NetworkLobby.Instance.OnPlayerJoined += HandleMpPlayerToast;
                mpLobbySubscribed = true;
                HandleMpPlayerListChanged();   // fire once now so the UI populates immediately
            }

            var nm = NetworkManager.Singleton;
            bool connected = nm != null && (nm.IsServer || nm.IsClient);
            bool isHost = nm != null && nm.IsHost;

            // Auto-register OUR username once we're connected AND NetworkLobby exists.
            if (!mpUsernameRegistered && connected
                && NetworkLobby.Instance != null && NetworkLobby.Instance.IsSpawned)
            {
                NetworkLobby.Instance.RegisterMyUsername(mpUsernameInput.text);
                mpUsernameRegistered = true;
            }

            // Lock the username + join inputs once connected, hide HOST/JOIN, show START GAME (host only).
            mpUsernameInput.interactable = !connected;
            mpJoinCodeInput.interactable = !connected;
            mpHostBtn.gameObject.SetActive(!connected);
            mpJoinBtn.gameObject.SetActive(!connected);
            mpStartGameBtn.gameObject.SetActive(connected && isHost);
            // Don't let host start a session of one (this is the soft rule — they CAN if they really want).
            mpStartGameBtn.interactable = isHost && mpKnownUsernames.Count >= 1;

            // Status line.
            if (NetworkBootstrap.Instance != null)
            {
                if (!connected) SetMpStatus(NetworkBootstrap.Instance.Status);
                else if (isHost) SetMpStatus("Connected as HOST. Share the code, wait for players, then press START GAME.");
                else SetMpStatus("Connected as CLIENT. Waiting for HOST to press START GAME…");
            }

            // Join code display (host only).
            if (NetworkBootstrap.Instance != null && connected && isHost && !string.IsNullOrEmpty(NetworkBootstrap.Instance.JoinCode))
                mpJoinCodeDisplay.text = $"JOIN CODE:  {NetworkBootstrap.Instance.JoinCode}";
            else
                mpJoinCodeDisplay.text = "";

            // Player counter + list.
            int count = mpKnownUsernames.Count;
            var sb = new System.Text.StringBuilder();
            sb.Append("Players (").Append(count).Append("/4):");
            foreach (var name in mpKnownUsernames.Values)
            {
                sb.Append('\n').Append("  • ").Append(name);
            }
            mpPlayersText.text = sb.ToString();

            // Fading "X has joined." toast.
            if (!string.IsNullOrEmpty(mpLastToast))
            {
                float age = Time.unscaledTime - mpLastToastTime;
                if (age > MpToastDuration)
                {
                    mpToastText.text = "";
                }
                else
                {
                    float fade = 1f - Mathf.Clamp01(age / MpToastDuration);
                    mpToastText.text = mpLastToast;
                    mpToastText.color = new Color(1f, 0.88f, 0.45f, fade);
                }
            }

            // If user disconnected (e.g. via shutdown), reset registration so they can try again.
            if (!connected)
            {
                mpUsernameRegistered = false;
                if (mpKnownUsernames.Count > 0) mpKnownUsernames.Clear();
            }
        }

        private void OnDifficultyBack()
        {
            Click();
            StartCoroutine(FadeCanvasGroup(difficultyGroup, 0f, false));
        }

        private void RefreshDifficultyLabels()
        {
            var d = DifficultyPreference.Selected;
            if (easyLabel != null)   easyLabel.text   = (d == Difficulty.Easy   ? "► " : "    ") + "EASY";
            if (mediumLabel != null) mediumLabel.text = (d == Difficulty.Medium ? "► " : "    ") + "MEDIUM";
            if (hardLabel != null)   hardLabel.text   = (d == Difficulty.Hard   ? "► " : "    ") + "HARD";

            var m = DifficultyPreference.Mode;
            if (singleLabel != null) singleLabel.text = (m == GameMode.SinglePlayer ? "► " : "    ") + "SINGLE PLAYER";
            if (multiLabel != null)  multiLabel.text  = (m == GameMode.Multiplayer  ? "► " : "    ") + "MULTIPLAYER (up to 4)";

            int n = DifficultyPreference.TotalNights;
            if (nights2Label != null) nights2Label.text = (n == 2 ? "► " : "    ") + "2 NIGHTS";
            if (nights4Label != null) nights4Label.text = (n == 4 ? "► " : "    ") + "4 NIGHTS";
            if (nights7Label != null) nights7Label.text = (n == 7 ? "► " : "    ") + "7 NIGHTS";
        }

        private void OnSettings()
        {
            Click();
            // Refresh sliders from GameSettings each time we open (in case in-game pause changed them).
            if (settingsVolumeSlider != null) settingsVolumeSlider.value = GameSettings.MasterVolume;
            if (settingsSensitivitySlider != null) settingsSensitivitySlider.value = GameSettings.Sensitivity;
            RefreshSettingsValueLabels();
            StartCoroutine(FadeCanvasGroup(settingsGroup, 1f, true));
        }

        private void OnSettingsBack()
        {
            Click();
            GameSettings.Save(); // flush to disk
            StartCoroutine(FadeCanvasGroup(settingsGroup, 0f, false));
        }

        private void OnSettingsVolumeChanged(float v)
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
            RefreshSettingsValueLabels();
        }

        private void OnSettingsSensitivityChanged(float v)
        {
            GameSettings.Sensitivity = v;
            RefreshSettingsValueLabels();
        }

        private void RefreshSettingsValueLabels()
        {
            if (settingsVolumeValueLabel != null)
                settingsVolumeValueLabel.text = $"{Mathf.RoundToInt(GameSettings.MasterVolume * 100f)}%";
            if (settingsSensitivityValueLabel != null)
                settingsSensitivityValueLabel.text = $"{GameSettings.Sensitivity:0.00}";
        }

        private void OnInfo()
        {
            Click();
            StartCoroutine(FadeCanvasGroup(infoGroup, 1f, true));
        }

        private void OnInfoBack()
        {
            Click();
            StartCoroutine(FadeCanvasGroup(infoGroup, 0f, false));
        }

        private void OnQuit()
        {
            Click();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void Click()
        {
            if (buttonClickSfx != null && AudioManager.Instance != null)
                AudioManager.Instance.PlaySfx2D(buttonClickSfx);
        }

        // ────────────────────────────── Routines ──────────────────────────────

        private IEnumerator FadeInRoutine()
        {
            float t = 0f;
            Vector2 to = titleRect.anchoredPosition;
            Vector2 from = new Vector2(to.x, to.y + 40f);   // subtle vertical rise (keep x)
            while (t < fadeDuration)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / fadeDuration);
                menuGroup.alpha = k;
                titleRect.anchoredPosition = Vector2.Lerp(from, to, k);   // subtle rise
                yield return null;
            }
            menuGroup.alpha = 1f;
            menuGroup.interactable = true;
            menuGroup.blocksRaycasts = true;
            titleRect.anchoredPosition = to;
        }

        private IEnumerator PlayRoutine()
        {
            menuGroup.interactable = false;

            // Swap to game music now — the AudioManager persists across the scene load.
            if (gameMusic != null && AudioManager.Instance != null)
                AudioManager.Instance.PlayAmbient(gameMusic);

            // Fade to black.
            float t = 0f;
            while (t < fadeDuration)
            {
                t += Time.unscaledDeltaTime;
                fadeCover.alpha = Mathf.Clamp01(t / fadeDuration);
                yield return null;
            }
            fadeCover.alpha = 1f;

            Time.timeScale = 1f;   // make sure gameplay starts unpaused
            SceneManager.LoadSceneAsync(gameSceneName);
        }

        // ────────────────────────────── UI construction ──────────────────────────────

        private void BuildUI()
        {
            var canvas = NewCanvas("MainMenu_Canvas", 50);

            // Background image (or a dark fallback colour), stretched full-screen.
            var bg = NewStretchImage(canvas.transform, "Background");
            if (backgroundImage != null) { bg.sprite = backgroundImage; bg.color = Color.white; }
            else bg.color = new Color(0.04f, 0.04f, 0.06f);
            bg.preserveAspect = false;

            // Heavier dark vignette so the art reads moody and text stays legible.
            NewStretchImage(canvas.transform, "Dim").color = new Color(0f, 0f, 0f, 0.58f);

            // The animated block (everything that fades in together).
            var groupGO = new GameObject("Menu", typeof(RectTransform), typeof(CanvasGroup));
            groupGO.transform.SetParent(canvas.transform, false);
            StretchFull((RectTransform)groupGO.transform);
            menuGroup = groupGO.GetComponent<CanvasGroup>();
            menuGroup.alpha = 0f;
            menuGroup.interactable = false;
            menuGroup.blocksRaycasts = false;

            // Title — a stylised image logo if one is provided, otherwise styled flickering text.
            if (titleImage != null)
            {
                var logoGO = new GameObject("TitleLogo", typeof(RectTransform), typeof(Image));
                logoGO.transform.SetParent(groupGO.transform, false);
                var lrt = (RectTransform)logoGO.transform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0f, 0.5f);   // left-aligned with the buttons
                lrt.pivot = new Vector2(0f, 0.5f);
                lrt.anchoredPosition = new Vector2(MarginX, 220f);
                lrt.sizeDelta = new Vector2(900f, 300f);
                var limg = logoGO.GetComponent<Image>();
                limg.sprite = titleImage;
                limg.preserveAspect = true;
                limg.raycastTarget = false;
                titleRect = lrt;
                StartCoroutine(FlickerRoutine(limg));
            }
            else
            {
                var title = NewText(groupGO.transform, gameTitle, 120, new Vector2(MarginX, 220f),
                                    new Vector2(1500f, 260f), Bone);
                title.fontStyle = FontStyles.Bold;
                StyleTitle(title);
                titleRect = title.rectTransform;
                StartCoroutine(FlickerRoutine(title));
            }

            // Buttons — left column, stacked, all sharing the title's left edge.
            NewButton(groupGO.transform, "Play",     new Vector2(MarginX, -40f),  OnPlay);
            NewButton(groupGO.transform, "Settings", new Vector2(MarginX, -150f), OnSettings);
            NewButton(groupGO.transform, "Info",     new Vector2(MarginX, -260f), OnInfo);
            NewButton(groupGO.transform, "Quit",     new Vector2(MarginX, -370f), OnQuit);

            // Studio credit, bottom-left (aligned with the same margin).
            var credit = NewText(groupGO.transform, studioCredit, 28, Vector2.zero,
                                 new Vector2(700f, 60f), new Color(0.7f, 0.62f, 0.6f));
            credit.fontStyle = FontStyles.Italic;
            credit.characterSpacing = 10f;
            var crt = credit.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 0f);
            crt.pivot = new Vector2(0f, 0f);
            crt.anchoredPosition = new Vector2(MarginX, 60f);

            // Info page overlay (hidden until the Info button is pressed).
            infoGroup = BuildInfoPanel(canvas.transform);

            // Difficulty + Mode selection page (hidden until Play is pressed).
            difficultyGroup = BuildDifficultyPanel(canvas.transform);

            // Multiplayer Lobby page (hidden until Play→Difficulty→START in MP mode).
            mpLobbyGroup = BuildMultiplayerLobbyPanel(canvas.transform);

            // Settings page (hidden until Settings is pressed).
            settingsGroup = BuildSettingsPanel(canvas.transform);

            // Black cover for the Play transition (on its own top canvas, starts invisible).
            var coverCanvas = NewCanvas("MainMenu_Fade", 80);
            var cover = NewStretchImage(coverCanvas.transform, "Cover");
            cover.color = Color.black;
            fadeCover = cover.gameObject.AddComponent<CanvasGroup>();
            fadeCover.alpha = 0f;
            fadeCover.blocksRaycasts = false;
        }

        private Canvas NewCanvas(string name, int order)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            var c = go.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            return c;
        }

        private Image NewStretchImage(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            StretchFull((RectTransform)go.transform);
            return go.GetComponent<Image>();
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private TMP_Text NewText(Transform parent, string content, int size, Vector2 pos, Vector2 dim, Color color)
        {
            var go = new GameObject("Text_" + content, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);   // left-center anchor (left layout)
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = dim;

            var text = go.AddComponent<TextMeshProUGUI>();   // TMP: reliable, crisp at any size
            if (menuFont != null) text.font = menuFont;       // horror font if you've assigned one
            text.text = content;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.Left;       // left-aligned so edges line up
            text.overflowMode = TextOverflowModes.Overflow;  // never clip the glyphs
            text.raycastTarget = false;
            return text;
        }

        private Button NewButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);   // left-anchored, like the title
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(460f, 76f);

            // Invisible click area — the label itself is the button (clean left-aligned list).
            var img = go.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0f);

            // Label flush to the bar's left edge so it lines up with the title.
            var text = NewText(go.transform, label.ToUpperInvariant(), 40, Vector2.zero,
                               new Vector2(460f, 70f), Color.white);
            text.name = "Label";
            text.characterSpacing = 10f;

            // Tint the LABEL (not the bar) blood-red on hover — reads as a horror menu.
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

        /// <summary>A TMP_InputField built in code with placeholder + text sub-objects (Unity can't
        /// just AddComponent&lt;TMP_InputField&gt; — it needs a Text Area / Placeholder / Text setup).</summary>
        private TMP_InputField NewInputField(Transform parent, string placeholder, int maxLength, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Input_" + placeholder, typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var box = go.GetComponent<Image>();
            box.color = new Color(0.10f, 0.08f, 0.10f, 0.95f);

            // Text Area (mask region inside the input box).
            var taGo = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            taGo.transform.SetParent(go.transform, false);
            var tart = (RectTransform)taGo.transform;
            tart.anchorMin = Vector2.zero; tart.anchorMax = Vector2.one;
            tart.offsetMin = new Vector2(20f, 6f); tart.offsetMax = new Vector2(-20f, -6f);

            // Placeholder text (shown while the field is empty).
            var phGo = new GameObject("Placeholder", typeof(RectTransform));
            phGo.transform.SetParent(taGo.transform, false);
            var phrt = (RectTransform)phGo.transform;
            phrt.anchorMin = Vector2.zero; phrt.anchorMax = Vector2.one;
            phrt.offsetMin = Vector2.zero; phrt.offsetMax = Vector2.zero;
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            if (menuFont != null) ph.font = menuFont;
            ph.text = placeholder;
            ph.fontSize = 30;
            ph.color = new Color(0.5f, 0.45f, 0.5f);
            ph.alignment = TextAlignmentOptions.MidlineLeft;
            ph.raycastTarget = false;
            ph.fontStyle = FontStyles.Italic;

            // Actual user-typed text.
            var txGo = new GameObject("Text", typeof(RectTransform));
            txGo.transform.SetParent(taGo.transform, false);
            var txrt = (RectTransform)txGo.transform;
            txrt.anchorMin = Vector2.zero; txrt.anchorMax = Vector2.one;
            txrt.offsetMin = Vector2.zero; txrt.offsetMax = Vector2.zero;
            var tx = txGo.AddComponent<TextMeshProUGUI>();
            if (menuFont != null) tx.font = menuFont;
            tx.text = "";
            tx.fontSize = 30;
            tx.color = Bone;
            tx.alignment = TextAlignmentOptions.MidlineLeft;
            tx.raycastTarget = false;

            var input = go.GetComponent<TMP_InputField>();
            input.textViewport = tart;
            input.textComponent = tx;
            input.placeholder = ph;
            input.text = "";
            input.characterLimit = maxLength;
            input.lineType = TMP_InputField.LineType.SingleLine;
            return input;
        }

        /// <summary>Give a TMP title a horror look: bone→blood gradient, black outline, soft red glow.</summary>
        private void StyleTitle(TMP_Text t)
        {
            t.color = Color.white;                 // let the gradient show at full strength
            t.characterSpacing = 8f;
            t.enableVertexGradient = true;
            t.colorGradient = new VertexGradient(Bone, Bone, BloodRed, BloodRed); // pale top, bloody bottom

            var mat = t.fontMaterial;              // a material instance unique to this text
            mat.EnableKeyword("OUTLINE_ON");
            mat.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
            mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.22f);

            mat.EnableKeyword("UNDERLAY_ON");      // soft dark-red glow/shadow bleeding out behind
            mat.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0.45f, 0f, 0f, 0.75f));
            mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0f);
            mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, 0f);
            mat.SetFloat(ShaderUtilities.ID_UnderlayDilate, 0.4f);
            mat.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.45f);
        }

        /// <summary>Occasional failing-light flicker on a graphic (title/logo) for dread.</summary>
        private IEnumerator FlickerRoutine(Graphic g)
        {
            Color baseColor = g.color;
            while (true)
            {
                yield return new WaitForSecondsRealtime(Random.Range(2.5f, 6f));
                int bursts = Random.Range(2, 5);
                for (int i = 0; i < bursts; i++)
                {
                    g.color = new Color(baseColor.r, baseColor.g, baseColor.b, Random.Range(0.2f, 0.55f));
                    yield return new WaitForSecondsRealtime(Random.Range(0.03f, 0.08f));
                    g.color = baseColor;
                    yield return new WaitForSecondsRealtime(Random.Range(0.04f, 0.1f));
                }
                g.color = baseColor;
            }
        }

        /// <summary>The Info page: fully-opaque overlay with the story / controls / credits / legal
        /// sections and a Back button.</summary>
        private CanvasGroup BuildInfoPanel(Transform parent)
        {
            var panelGO = new GameObject("InfoPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            panelGO.transform.SetParent(parent, false);
            StretchFull((RectTransform)panelGO.transform);
            // Fully opaque — the menu buttons must not bleed through behind the text.
            panelGO.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.03f, 1f);

            var group = panelGO.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            // Heading — slightly bigger, anchored top.
            var heading = NewText(panelGO.transform, "INFO", 80, new Vector2(MarginX, 440f),
                                  new Vector2(1200f, 100f), Bone);
            heading.fontStyle = FontStyles.Bold;

            // Body — top-anchored so the long text flows DOWN from the heading instead of being
            // centred (which would push the bottom past the Back button).
            var bodyGO = new GameObject("Text_InfoBody", typeof(RectTransform));
            bodyGO.transform.SetParent(panelGO.transform, false);
            var brt = (RectTransform)bodyGO.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0f, 0.5f);
            brt.pivot = new Vector2(0f, 1f);                          // top-left pivot
            brt.anchoredPosition = new Vector2(MarginX, 380f);
            brt.sizeDelta = new Vector2(1600f, 760f);

            var body = bodyGO.AddComponent<TextMeshProUGUI>();
            if (menuFont != null) body.font = menuFont;
            body.text = InfoText;
            body.fontSize = 28;
            body.color = new Color(0.88f, 0.85f, 0.82f);
            body.alignment = TextAlignmentOptions.TopLeft;
            body.overflowMode = TextOverflowModes.Overflow;
            body.lineSpacing = 6f;
            body.richText = true;                                     // for <b> + <color> tags
            body.raycastTarget = false;

            // Version stamp, bottom-left.
            var version = NewText(panelGO.transform, Version, 24, Vector2.zero,
                                  new Vector2(700f, 50f), new Color(0.45f, 0.43f, 0.48f));
            version.fontStyle = FontStyles.Italic;
            var vrt = version.rectTransform;
            vrt.anchorMin = vrt.anchorMax = new Vector2(0f, 0f);
            vrt.pivot = new Vector2(0f, 0f);
            vrt.anchoredPosition = new Vector2(MarginX, 40f);

            // Back, also bottom-left but above the version stamp.
            var backBtn = NewButton(panelGO.transform, "Back", new Vector2(MarginX, 0f), OnInfoBack);
            var brrt = (RectTransform)backBtn.transform;
            brrt.anchorMin = brrt.anchorMax = new Vector2(0f, 0f);
            brrt.pivot = new Vector2(0f, 0f);
            brrt.anchoredPosition = new Vector2(MarginX, 100f);

            return group;
        }

        /// <summary>The Difficulty + Mode page: shown after Play. User picks EASY/MEDIUM/HARD plus
        /// SINGLE PLAYER / MULTIPLAYER, then a START button proceeds to the gameplay scene.</summary>
        private CanvasGroup BuildDifficultyPanel(Transform parent)
        {
            var panelGO = new GameObject("DifficultyPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            panelGO.transform.SetParent(parent, false);
            StretchFull((RectTransform)panelGO.transform);
            panelGO.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.03f, 1f);

            var group = panelGO.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            // "SELECT DIFFICULTY" heading
            var heading = NewText(panelGO.transform, "SELECT DIFFICULTY", 64, new Vector2(MarginX, 380f),
                                  new Vector2(1200f, 100f), Bone);
            heading.fontStyle = FontStyles.Bold;

            // Difficulty buttons (label refs cached so we can show ► selection marker)
            var btnEasy = NewButton(panelGO.transform, "EASY", new Vector2(MarginX, 260f), () => OnPickDifficulty(Difficulty.Easy));
            easyLabel = btnEasy.GetComponentInChildren<TMP_Text>();
            var btnMed = NewButton(panelGO.transform, "MEDIUM", new Vector2(MarginX, 180f), () => OnPickDifficulty(Difficulty.Medium));
            mediumLabel = btnMed.GetComponentInChildren<TMP_Text>();
            var btnHard = NewButton(panelGO.transform, "HARD", new Vector2(MarginX, 100f), () => OnPickDifficulty(Difficulty.Hard));
            hardLabel = btnHard.GetComponentInChildren<TMP_Text>();

            // "NIGHTS" heading + length buttons (right column) — 2 / 4 / 7 day-night cycles.
            var nightsHeading = NewText(panelGO.transform, "NIGHTS", 48, new Vector2(MarginX + 720f, 260f),
                                        new Vector2(600f, 80f), Bone);
            nightsHeading.fontStyle = FontStyles.Bold;
            var btn2 = NewButton(panelGO.transform, "2 NIGHTS", new Vector2(MarginX + 720f, 170f), () => OnPickNights(2));
            nights2Label = btn2.GetComponentInChildren<TMP_Text>();
            var btn4 = NewButton(panelGO.transform, "4 NIGHTS", new Vector2(MarginX + 720f, 90f), () => OnPickNights(4));
            nights4Label = btn4.GetComponentInChildren<TMP_Text>();
            var btn7 = NewButton(panelGO.transform, "7 NIGHTS", new Vector2(MarginX + 720f, 10f), () => OnPickNights(7));
            nights7Label = btn7.GetComponentInChildren<TMP_Text>();

            // "MODE" heading
            var modeHeading = NewText(panelGO.transform, "MODE", 48, new Vector2(MarginX, 0f),
                                      new Vector2(1200f, 80f), Bone);
            modeHeading.fontStyle = FontStyles.Bold;

            // Mode buttons
            var btnSingle = NewButton(panelGO.transform, "SINGLE PLAYER", new Vector2(MarginX, -90f), () => OnPickMode(GameMode.SinglePlayer));
            singleLabel = btnSingle.GetComponentInChildren<TMP_Text>();
            var btnMulti = NewButton(panelGO.transform, "MULTIPLAYER", new Vector2(MarginX, -170f), () => OnPickMode(GameMode.Multiplayer));
            multiLabel = btnMulti.GetComponentInChildren<TMP_Text>();

            // START button (the actual scene transition)
            NewButton(panelGO.transform, "START", new Vector2(MarginX, -290f), OnStartGame);

            // Back to main menu
            NewButton(panelGO.transform, "Back", new Vector2(MarginX, -390f), OnDifficultyBack);

            return group;
        }

        /// <summary>The Multiplayer Lobby page: username (required) + HOST / JOIN + player counter +
        /// joined toasts + a centre-bottom START GAME button (host-only). Shown after Difficulty
        /// when GameMode is Multiplayer; the START GAME button triggers NGO scene-load for everyone.</summary>
        private CanvasGroup BuildMultiplayerLobbyPanel(Transform parent)
        {
            var panelGO = new GameObject("MultiplayerLobbyPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            panelGO.transform.SetParent(parent, false);
            StretchFull((RectTransform)panelGO.transform);
            panelGO.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.03f, 1f);

            var group = panelGO.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var t = panelGO.transform;

            // Heading
            var heading = NewText(t, "MULTIPLAYER LOBBY", 64, new Vector2(MarginX, 400f), new Vector2(1200f, 100f), Bone);
            heading.fontStyle = FontStyles.Bold;

            // Username (required)
            NewText(t, "USERNAME (required)", 26, new Vector2(MarginX, 290f), new Vector2(600f, 40f), Bone);
            mpUsernameInput = NewInputField(t, "Your name…", 18, new Vector2(MarginX, 230f), new Vector2(560f, 64f));

            // HOST button
            mpHostBtn = NewButton(t, "HOST NEW SESSION", new Vector2(MarginX, 130f), OnHostClick);

            // Join code field + JOIN
            NewText(t, "OR JOIN WITH CODE", 26, new Vector2(MarginX + 700f, 290f), new Vector2(400f, 40f), Bone);
            mpJoinCodeInput = NewInputField(t, "ABC123", 6, new Vector2(MarginX + 700f, 230f), new Vector2(300f, 64f));
            mpJoinBtn = NewButton(t, "JOIN", new Vector2(MarginX + 700f, 130f), OnJoinClick);

            // Status line
            mpStatusText = NewText(t, "Enter a username, then HOST a session or paste a code and JOIN.", 24,
                                   new Vector2(MarginX, 50f), new Vector2(1500f, 40f), new Color(0.78f, 0.78f, 0.78f));

            // Join code display (host only, populated dynamically)
            mpJoinCodeDisplay = NewText(t, "", 36, new Vector2(MarginX, -10f), new Vector2(1500f, 50f), new Color(1f, 0.85f, 0.35f));
            mpJoinCodeDisplay.fontStyle = FontStyles.Bold;

            // Player counter + list
            mpPlayersText = NewText(t, "Players (0/4):", 28, new Vector2(MarginX, -200f), new Vector2(1200f, 240f), Bone);
            mpPlayersText.alignment = TextAlignmentOptions.TopLeft;
            mpPlayersText.lineSpacing = 8f;

            // "X has joined." toast — top-centre, fades in/out
            mpToastText = NewText(t, "", 36, Vector2.zero, new Vector2(900f, 60f), new Color(1f, 0.88f, 0.45f, 0f));
            mpToastText.fontStyle = FontStyles.Bold;
            mpToastText.alignment = TextAlignmentOptions.Center;
            var toastRt = mpToastText.rectTransform;
            toastRt.anchorMin = toastRt.anchorMax = new Vector2(0.5f, 1f);
            toastRt.pivot = new Vector2(0.5f, 1f);
            toastRt.anchoredPosition = new Vector2(0f, -80f);

            // START GAME (center-bottom, host-only)
            mpStartGameBtn = NewCenterButton(t, "START GAME", new Vector2(0f, 110f), OnStartGameClick);
            mpStartGameBtn.gameObject.SetActive(false);

            // Back button
            NewButton(t, "Back", new Vector2(MarginX, -430f), OnMpLobbyBack);

            return group;
        }

        // Variant of NewButton anchored to BOTTOM-CENTRE (for the START GAME prominent button).
        private Button NewCenterButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var btn = NewButton(parent, label, pos, onClick);
            var rt = (RectTransform)btn.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);   // bottom-centre anchor
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(620f, 96f);
            // Make the label text centered inside the wider button.
            var lbl = btn.GetComponentInChildren<TMP_Text>();
            if (lbl != null)
            {
                lbl.alignment = TextAlignmentOptions.Center;
                lbl.fontSize = 48;
            }
            return btn;
        }

        /// <summary>Settings page: Master Volume + Sensitivity sliders, live-applying via
        /// AudioManager + GameSettings. Same UI is used by the in-game pause menu.</summary>
        private CanvasGroup BuildSettingsPanel(Transform parent)
        {
            var panelGO = new GameObject("SettingsPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            panelGO.transform.SetParent(parent, false);
            StretchFull((RectTransform)panelGO.transform);
            panelGO.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.03f, 1f);

            var group = panelGO.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var t = panelGO.transform;

            // Heading
            var heading = NewText(t, "SETTINGS", 72, new Vector2(MarginX, 360f), new Vector2(1200f, 100f), Bone);
            heading.fontStyle = FontStyles.Bold;

            // MASTER VOLUME
            NewText(t, "MASTER VOLUME", 32, new Vector2(MarginX, 200f), new Vector2(600f, 50f), Bone);
            settingsVolumeSlider = NewSlider(t, new Vector2(MarginX, 140f), new Vector2(700f, 40f),
                                             0f, 1f, GameSettings.MasterVolume, OnSettingsVolumeChanged);
            settingsVolumeValueLabel = NewText(t, "100%", 32, new Vector2(MarginX + 730f, 140f),
                                               new Vector2(150f, 50f), new Color(1f, 0.85f, 0.35f));

            // SENSITIVITY
            NewText(t, "LOOK SENSITIVITY", 32, new Vector2(MarginX, 30f), new Vector2(600f, 50f), Bone);
            settingsSensitivitySlider = NewSlider(t, new Vector2(MarginX, -30f), new Vector2(700f, 40f),
                                                  0.1f, 3.0f, GameSettings.Sensitivity, OnSettingsSensitivityChanged);
            settingsSensitivityValueLabel = NewText(t, "1.00", 32, new Vector2(MarginX + 730f, -30f),
                                                    new Vector2(150f, 50f), new Color(1f, 0.85f, 0.35f));

            // Back button
            NewButton(t, "Back", new Vector2(MarginX, -260f), OnSettingsBack);

            RefreshSettingsValueLabels();
            return group;
        }

        /// <summary>Build a UGUI Slider in code with the proper Background / Fill / Handle hierarchy
        /// (Unity's Slider component requires this exact structure to function).</summary>
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

            // Background bar
            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(go.transform, false);
            var bgRt = (RectTransform)bgGO.transform;
            bgRt.anchorMin = new Vector2(0f, 0.3f); bgRt.anchorMax = new Vector2(1f, 0.7f);
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0.12f, 0.10f, 0.13f, 0.95f);

            // Fill area + fill
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

            // Handle area + handle
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

        /// <summary>Fade a CanvasGroup to an alpha, toggling whether it catches clicks at the end.</summary>
        private IEnumerator FadeCanvasGroup(CanvasGroup g, float target, bool interactiveAtEnd)
        {
            g.blocksRaycasts = true;                  // catch clicks while it's on screen
            float start = g.alpha;
            float t = 0f;
            const float dur = 0.35f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                g.alpha = Mathf.Lerp(start, target, t / dur);
                yield return null;
            }
            g.alpha = target;
            g.interactable = interactiveAtEnd;
            g.blocksRaycasts = interactiveAtEnd;
        }

        /// <summary>UGUI buttons need an EventSystem. Create one wired for the new Input System.</summary>
        private void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            var module = go.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();   // so clicks/navigation work without an actions asset
        }
    }
}
