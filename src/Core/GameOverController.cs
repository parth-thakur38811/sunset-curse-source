using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using SunsetCurse.Player;
using SunsetCurse.Audio;
using SunsetCurse.AI;

namespace SunsetCurse.Core
{
    /// <summary>
    /// Runs the end of the game:
    ///   - PLAYER KILLED (monster touch / starvation): plays a pained grunt, "collapses" the camera
    ///     to the ground as if you fall, then fades in the GAME OVER screen.
    ///   - FINAL NIGHT LOST (GameClock.OnGameOver): fades in the GAME OVER screen.
    ///   - ALL SHARDS COLLECTED (EscapeTracker.OnEscapeReady, i.e. 7/7): fades in the VICTORY screen.
    ///
    /// Each screen shows the art + a Continue button that returns to the MainMenu scene. The game
    /// freezes once a screen is up.
    ///
    /// SETUP: keep this on a manager object. Assign Game Over Image + Victory Image (black-background
    /// PNG/JPG are fine), and a few Damage Sounds (the imported grunts). Make sure "MainMenu" is in
    /// File > Build Settings.
    /// </summary>
    public class GameOverController : MonoBehaviour
    {
        public static GameOverController Instance { get; private set; }

        [Header("Screens")]
        [SerializeField] private Sprite gameOverImage;
        [SerializeField] private Sprite victoryImage;
        [SerializeField] private float fadeDuration = 1.2f;
        [SerializeField] private string mainMenuScene = "MainMenu";

        [Header("Death — pained grunt (one picked at random)")]
        [SerializeField] private AudioClip[] damageSounds;

        [Header("Death — stinger (the SHE-GOT-YOU sound)")]
        [Tooltip("Loud one-shot played the moment you die (separate from the grunt). A screech / " +
                 "violin sting / harsh hit works well. Plays in 2D so it fills the player's ears.")]
        [SerializeField] private AudioClip deathStinger;
        [Range(0f, 1f)] [SerializeField] private float deathStingerVolume = 1f;

        [Header("Death — camera collapse")]
        [Tooltip("How long the camera takes to TURN and face the monster before collapsing.")]
        [SerializeField] private float turnDuration = 0.6f;
        [SerializeField] private float collapseDuration = 1.6f;
        [Tooltip("How far the view sinks toward the ground (metres).")]
        [SerializeField] private float dropHeight = 1.6f;
        [Tooltip("How far the view rolls onto its side (degrees).")]
        [SerializeField] private float rollDegrees = 80f;
        [Tooltip("How far the view pitches down as you fall (degrees).")]
        [SerializeField] private float pitchDegrees = 12f;
        [Tooltip("Camera Field of View to zoom INTO as you die (lower = tighter zoom on her). " +
                 "Set to 0 to disable zoom.")]
        [SerializeField] private float zoomFovTarget = 32f;

        private PlayerStats player;
        private bool ended;
        private readonly HashSet<PlayerStats> subscribed = new HashSet<PlayerStats>();
        private float playerRefreshTimer;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void Start()
        {
            RefreshPlayerSubscriptions();

            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnGameOver += ShowGameOver;   // permanent night = loss
                GameClock.Instance.OnEscape += ShowVictory;      // ritual complete = win (see RitualSite)
            }
            // Note: collecting 7/7 no longer auto-wins — it just enables the escape ritual. Victory
            // fires from RitualSite → GameClock.TriggerEscape() → OnEscape above.
        }

        private void Update()
        {
            // Re-find PlayerStats periodically. The scene PlayerCapsule was deleted when we set up
            // multiplayer, so the actual player object is INSTANTIATED later by NGO — well after
            // our Start runs. Without this, OnPlayerDied is never wired up and the death sequence
            // never plays. Also handles new players joining a co-op session.
            playerRefreshTimer -= Time.unscaledDeltaTime;
            if (playerRefreshTimer > 0f) return;
            playerRefreshTimer = 1f;
            RefreshPlayerSubscriptions();
        }

        private void RefreshPlayerSubscriptions()
        {
            foreach (var ps in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
            {
                if (ps == null || subscribed.Contains(ps)) continue;
                ps.OnPlayerDied += HandleDeath;
                subscribed.Add(ps);
                if (player == null) player = ps;
                Debug.Log($"[GameOverController] Subscribed to '{ps.name}'.OnPlayerDied.", this);
            }
        }

        private void OnDestroy()
        {
            foreach (var ps in subscribed) if (ps != null) ps.OnPlayerDied -= HandleDeath;
            subscribed.Clear();
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnGameOver -= ShowGameOver;
                GameClock.Instance.OnEscape -= ShowVictory;
            }
        }

        // ───────────────────────────── Death sequence ─────────────────────────────

        private void HandleDeath()
        {
            // CO-OP: if any teammate is still alive, this player enters SpectatorMode instead of
            // ending the game for everyone. We only run the collapse + GAME OVER sequence once
            // the LAST living player has fallen.
            if (HasOtherAlivePlayers())
            {
                // Just play the grunt locally so the dying player still gets feedback. The actual
                // GAME OVER screen + collapse waits for the all-dead condition (HandleAllDead).
                if (damageSounds != null && damageSounds.Length > 0 && AudioManager.Instance != null)
                    AudioManager.Instance.PlaySfx2D(damageSounds[Random.Range(0, damageSounds.Length)]);
                if (deathStinger != null && AudioManager.Instance != null)
                    AudioManager.Instance.PlaySfx2D(deathStinger);
                Debug.Log("[GameOverController] Local player died — teammates still alive, entering SPECTATOR.");
                return;   // SpectatorMode (subscribed to PlayerStats.OnPlayerDied too) takes over the view
            }

            if (ended) return;
            ended = true;   // claim the end now so nothing else double-fires

            if (damageSounds != null && damageSounds.Length > 0 && AudioManager.Instance != null)
                AudioManager.Instance.PlaySfx2D(damageSounds[Random.Range(0, damageSounds.Length)]);

            // The SHE-GOT-YOU stinger — separate from the grunt so they layer.
            if (deathStinger != null && AudioManager.Instance != null)
                AudioManager.Instance.PlaySfx2D(deathStinger);

            // Stop the player controlling the view; stop Cinemachine driving the camera so we can
            // topple it ourselves (reflection so we don't hard-depend on those packages).
            if (player != null) DisableComponentByName(player.gameObject, "FirstPersonController");

            var cam = Camera.main;
            if (cam != null)
            {
                DisableComponentByName(cam.gameObject, "CinemachineBrain");
                StartCoroutine(CollapseThenGameOver(cam));
            }
            else
            {
                ShowScreen(gameOverImage);   // no camera to topple — just show it
            }
        }

        /// <summary>True iff at least one OTHER PlayerInventory in the scene reports alive.
        /// Used to gate GAME OVER in co-op so spectator mode can take the dead player.</summary>
        private static bool HasOtherAlivePlayers()
        {
            foreach (var inv in FindObjectsByType<SunsetCurse.Core.PlayerInventory>(FindObjectsSortMode.None))
            {
                if (inv == null || inv == SunsetCurse.Core.PlayerInventory.Local) continue;
                if (inv.IsAlive) return true;
            }
            return false;
        }

        private IEnumerator CollapseThenGameOver(Camera cam)
        {
            Transform camT = cam.transform;
            Vector3 startPos = camT.position;
            Quaternion startRot = camT.rotation;
            float startFov = cam.fieldOfView;

            // Find the NEAREST monster (main stalker OR the compound watcher — whichever one got
            // you) so we can TURN toward her first, then topple — makes it feel like she's the one
            // who got you. If neither is around (e.g. starvation by day), skip the turn.
            bool haveMonster = MonsterRegistry.TryGetNearest(startPos, out Vector3 monsterPos, out _);

            // The upright rotation that looks straight at her chest from where we're standing.
            Quaternion faceRot = startRot;
            if (haveMonster)
            {
                Vector3 toMonster = (monsterPos + Vector3.up * 1.4f) - startPos;
                if (toMonster.sqrMagnitude > 0.01f) faceRot = Quaternion.LookRotation(toMonster);
            }

            // ── PHASE 1: TURN to face her (stay upright). Skipped if there's no monster. ──
            if (haveMonster && turnDuration > 0f)
            {
                float tt = 0f;
                while (tt < turnDuration)
                {
                    tt += Time.unscaledDeltaTime;
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(tt / turnDuration));
                    camT.rotation = Quaternion.Slerp(startRot, faceRot, k);
                    yield return null;
                }
                camT.rotation = faceRot;
            }

            // ── PHASE 2: COLLAPSE to the ground while still facing her, zooming in. ──
            Vector3 endPos = startPos + Vector3.down * dropHeight;
            Quaternion collapsedRot = faceRot * Quaternion.Euler(pitchDegrees, 0f, rollDegrees);
            float t = 0f;
            while (t < collapseDuration)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / collapseDuration);
                float eased = k * k;                       // accelerate, like falling
                camT.position = Vector3.Lerp(startPos, endPos, eased);
                camT.rotation = Quaternion.Slerp(faceRot, collapsedRot, eased);
                if (zoomFovTarget > 0f)
                    cam.fieldOfView = Mathf.Lerp(startFov, zoomFovTarget, eased);
                yield return null;
            }

            ShowScreen(gameOverImage);
        }

        // ───────────────────────────── Screens ─────────────────────────────

        // Called by the clock/tracker events (these fire before HandleDeath claims `ended`, so they
        // guard themselves).
        public void ShowGameOver() { if (!ended) { ended = true; ShowScreen(gameOverImage); } }
        public void ShowVictory()  { if (!ended) { ended = true; ShowScreen(victoryImage); } }

        private void ShowScreen(Sprite image)
        {
            EnsureEventSystem();
            var group = BuildScreen(image);
            StartCoroutine(FadeInThenFreeze(group));
        }

        private IEnumerator FadeInThenFreeze(CanvasGroup group)
        {
            if (player != null) DisableComponentByName(player.gameObject, "FirstPersonController");

            float t = 0f;
            while (t < fadeDuration)
            {
                t += Time.unscaledDeltaTime;       // unscaled so the fade plays even when we freeze
                group.alpha = Mathf.Clamp01(t / fadeDuration);
                yield return null;
            }
            group.alpha = 1f;

            Time.timeScale = 0f;                    // freeze everything (monsters included)
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            group.interactable = true;
            group.blocksRaycasts = true;
        }

        private void Continue()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(mainMenuScene);
        }

        private CanvasGroup BuildScreen(Sprite image)
        {
            var canvasGO = new GameObject("EndScreen_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300;             // above every other HUD
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var group = canvasGO.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            NewStretchImage(canvasGO.transform, "Black").color = Color.black;

            if (image != null)
            {
                var imgGO = new GameObject("Wordmark", typeof(RectTransform), typeof(Image));
                imgGO.transform.SetParent(canvasGO.transform, false);
                var irt = (RectTransform)imgGO.transform;
                irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
                irt.pivot = new Vector2(0.5f, 0.5f);
                irt.anchoredPosition = new Vector2(0f, 70f);
                irt.sizeDelta = new Vector2(1200f, 520f);
                var img = imgGO.GetComponent<Image>();
                img.sprite = image;
                img.preserveAspect = true;
                img.raycastTarget = false;
            }

            BuildButton(canvasGO.transform, "Continue", new Vector2(0f, -300f), Continue);
            return group;
        }

        private void BuildButton(Transform parent, string label, Vector2 pos, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(340f, 76f);
            go.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.09f, 0.9f);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(go.transform, false);
            var lrt = (RectTransform)labelGO.transform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var text = labelGO.AddComponent<TextMeshProUGUI>();
            text.text = label.ToUpperInvariant();
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 34;
            text.color = Color.white;
            text.raycastTarget = false;

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = text;
            var colors = btn.colors;
            colors.normalColor = new Color(0.9f, 0.86f, 0.8f);
            colors.highlightedColor = new Color(0.7f, 0.1f, 0.1f);   // blood-red on hover
            colors.pressedColor = new Color(0.45f, 0.05f, 0.05f);
            colors.fadeDuration = 0.12f;
            btn.colors = colors;
            btn.onClick.AddListener(onClick);
        }

        private Image NewStretchImage(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return go.GetComponent<Image>();
        }

        private static void DisableComponentByName(GameObject go, string typeName)
        {
            foreach (var mb in go.GetComponents<MonoBehaviour>())
                if (mb != null && mb.GetType().Name == typeName) mb.enabled = false;
        }

        private void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            var module = go.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }
    }
}
