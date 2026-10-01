using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Owns THE ONE diary book in the world: each dawn it moves to a new random forest spot and
    /// carries that day's story (yesterday's copy is removed — never more than one book exists).
    /// Also owns the popup UI shown when a player reads it; dismissed with E or Esc.
    ///
    /// SETUP:
    ///   1. Empty GameObject "DiaryPageController" + this script.
    ///   2. Drop a Page Prefab (must have a DiaryPage + Collider) into the slot.
    ///   3. Set Forest Center to your village transform.
    ///   4. The Page Contents array is pre-filled with 7 themed lore strings (one per day).
    ///      Tweak them to your taste in the Inspector.
    /// </summary>
    public class DiaryPageController : MonoBehaviour
    {
        public static DiaryPageController Instance { get; private set; }

        [Header("Spawning")]
        [SerializeField] private GameObject pagePrefab;
        [SerializeField] private Transform forestCenter;
        [SerializeField] private float minRadius = 25f;
        [SerializeField] private float maxRadius = 100f;
        [SerializeField] private float pageScale = 0.3f;
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("Lore (one entry per day 1..7)")]
        [TextArea(1, 4)]
        [SerializeField] private string[] pageTitles = new string[]
        {
            "Day 1 — The Road That Brought Me Here",
            "Day 2 — The Well",
            "Day 3 — Something in the Trees",
            "Day 4 — What He Asks Of Us",
            "Day 5 — Three Gone",
            "Day 6 — Rain",
            "Day 7 — The Final Night",
        };

        [TextArea(3, 10)]
        [SerializeField] private string[] pageContents = new string[]
        {
            "My car broke down on a road that isn't on any map. Engine cut out at dusk — " +
            "forest on both sides, no signal, nothing.\n\n" +
            "I saw lights through the trees. A settlement. I walked toward them thinking I'd " +
            "borrow a phone, maybe sleep on someone's floor, be gone by morning.\n\n" +
            "The elder sat me down before I even asked. He already knew about the car.\n\n" +
            "He says the road won't open again until we gather seven Ritual Potions locked inside " +
            "the compound at the forest's heart. She guards them. He won't say what she is — only " +
            "that she comes out at night, and that we must not run.\n\n" +
            "Seven days. Then it's permanent night.\n\n" +
            "I have to stay. I don't think I have a choice.",

            "The well has run dry. The elder calls it 'the curse drinking deep.' He will not say " +
            "more before nightfall.",

            "I saw her among the trees. Not the woman from the well. Something wearing her " +
            "clothes. The others call it scenting our fear.",

            "We have begun gathering what the elder asks. Stones the colour of bone. Pages from " +
            "books no village should own. Salt for the threshold.",

            "Three are missing tonight. They were the loudest sprinters yesterday. The forest did " +
            "not give them back.",

            "The sky bruised over today and the rain did not stop. The elder smiled. He said she " +
            "walks louder in the wet — but so do we, and now neither of us is silent.",

            "The final night begins at dusk. If we cannot finish what we started — if we cannot " +
            "lift the seven from her ground — there will be no sunrise. The elder says: hold the " +
            "altar, draw her in, and pray."
        };

        // Popup UI refs (built at runtime).
        private CanvasGroup popupGroup;
        private ScrollRect scrollRect;
        private TMP_Text popupTitle;
        private TMP_Text popupBody;
        private bool popupOpen;
        private InputAction closeAction;
        private int openedOnFrame = -1;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            BuildPopupUI();

            // Event-driven close — more reliable than polling wasPressedThisFrame
            // when cursor lock state changes.
            closeAction = new InputAction(type: InputActionType.Button);
            closeAction.AddBinding("<Keyboard>/e");
            closeAction.AddBinding("<Keyboard>/escape");
            closeAction.performed += _ =>
            {
                // openedOnFrame guard: ignore the same E press that opened the popup.
                if (popupOpen && Time.frameCount > openedOnFrame)
                    CloseUI();
            };
            closeAction.Enable();
        }

        private void Start()
        {
            if (GameClock.Instance != null)
            {
                GameClock.Instance.OnDayStart += HandleDayStart;
                // If we entered the scene AFTER GameClock already fired Day 1's event, catch up.
                if (GameClock.Instance.CurrentPhase == GameClock.Phase.Day)
                    HandleDayStart(GameClock.Instance.CurrentDay);
            }
        }

        private void OnDestroy()
        {
            if (GameClock.Instance != null) GameClock.Instance.OnDayStart -= HandleDayStart;
            closeAction?.Disable();
            closeAction?.Dispose();
        }

        // ───────────────────────── Page spawning ─────────────────────────

        // The ONE diary book in the world. Each dawn it's replaced (new spot + that day's story)
        // — yesterday's copy is destroyed first, so exactly one book exists at any time.
        private GameObject currentPage;

        private void HandleDayStart(int day)
        {
            if (day < 1 || day > pageContents.Length) return;
            SpawnPageForDay(day);
        }

        private void SpawnPageForDay(int day)
        {
            if (pagePrefab == null) { Debug.LogWarning("[DiaryPageController] No page prefab assigned.", this); return; }

            Vector3 center = forestCenter != null ? forestCenter.position : transform.position;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float dist = Mathf.Sqrt(Mathf.Lerp(minRadius * minRadius, maxRadius * maxRadius, Random.value));
                Vector3 probe = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * dist;

                Vector3 rayStart = probe + Vector3.up * 200f;
                if (!Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 500f, groundMask,
                                     QueryTriggerInteraction.Ignore))
                    continue;

                // ONE book rule: remove yesterday's before placing today's.
                if (currentPage != null) Destroy(currentPage);

                var go = Instantiate(pagePrefab, hit.point + Vector3.up * 0.05f,
                                     Quaternion.Euler(90f, Random.Range(0f, 360f), 0f), transform);
                go.transform.localScale = Vector3.one * pageScale;
                MakeReadable(go, day);
                currentPage = go;
                Debug.Log($"[DiaryPageController] Day {day} page placed at {hit.point}.", this);
                return;
            }
            Debug.LogWarning($"[DiaryPageController] Could not place Day {day} page after 30 attempts.", this);
        }

        /// <summary>Guarantees the spawned book is actually interactable, whatever prefab was
        /// dropped in the slot: adds the DiaryPage component if the prefab lacks one, and adds a
        /// generous BoxCollider if there's no collider (imported models often ship without any —
        /// and without a collider PlayerInteractor's raycast can never see the book).</summary>
        private void MakeReadable(GameObject go, int day)
        {
            var dp = go.GetComponent<DiaryPage>();
            if (dp == null) dp = go.AddComponent<DiaryPage>();
            dp.Configure(day);

            if (go.GetComponentInChildren<Collider>() == null)
            {
                var box = go.AddComponent<BoxCollider>();
                var rends = go.GetComponentsInChildren<Renderer>();
                if (rends.Length > 0)
                {
                    Bounds b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    box.center = go.transform.InverseTransformPoint(b.center);
                    Vector3 size = go.transform.InverseTransformVector(b.size);
                    box.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                }
            }

            // A small prop scaled down by pageScale ends up with a collider too thin for the
            // interaction ray to hit reliably — pad every collider up to ~0.5m in world units.
            foreach (var col in go.GetComponentsInChildren<BoxCollider>())
            {
                Vector3 world = Vector3.Scale(col.size, col.transform.lossyScale);
                Vector3 scale = col.transform.lossyScale;
                col.size = new Vector3(
                    world.x < 0.5f && scale.x > 0.0001f ? 0.5f / scale.x : col.size.x,
                    world.y < 0.5f && scale.y > 0.0001f ? 0.5f / scale.y : col.size.y,
                    world.z < 0.5f && scale.z > 0.0001f ? 0.5f / scale.z : col.size.z);
            }
        }

        // ───────────────────────── Popup ─────────────────────────

        public void ShowPage(int pageNumber, string overrideText)
        {
            string body = !string.IsNullOrEmpty(overrideText)
                ? overrideText
                : (pageNumber >= 1 && pageNumber <= pageContents.Length
                    ? pageContents[pageNumber - 1]
                    : "(blank page)");

            string title = (pageNumber >= 1 && pageNumber <= pageTitles.Length)
                ? pageTitles[pageNumber - 1]
                : $"Day {pageNumber}";
            if (popupTitle != null) popupTitle.text = title;
            if (popupBody != null) popupBody.text = body;
            // Force layout rebuild so ContentSizeFitter updates before we reset scroll.
            Canvas.ForceUpdateCanvases();
            if (scrollRect != null) scrollRect.normalizedPosition = new Vector2(0f, 1f);
            popupGroup.alpha = 1f;
            popupGroup.interactable = true;
            popupGroup.blocksRaycasts = true;
            popupOpen = true;
            openedOnFrame = Time.frameCount;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void CloseUI()
        {
            popupGroup.alpha = 0f;
            popupGroup.interactable = false;
            popupGroup.blocksRaycasts = false;
            popupOpen = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ───────────────────────── UI construction ─────────────────────────

        private void BuildPopupUI()
        {
            var canvasGO = new GameObject("DiaryPopup_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 220;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            popupGroup = canvasGO.GetComponent<CanvasGroup>();
            popupGroup.alpha = 0f;
            popupGroup.interactable = false;
            popupGroup.blocksRaycasts = false;

            // Dim full-screen background.
            var bgGO = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(canvasGO.transform, false);
            var bgRt = (RectTransform)bgGO.transform;
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0.02f, 0.02f, 0.03f, 0.95f);

            // Centred parchment-style panel.
            var panelGO = new GameObject("PaperPanel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            var prt = (RectTransform)panelGO.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(1100f, 680f);
            panelGO.GetComponent<Image>().color = new Color(0.10f, 0.09f, 0.10f, 0.98f);

            // Title.
            popupTitle = NewText(panelGO.transform, "Day 1", 40,
                                 new Vector2(0f, 260f), new Vector2(1000f, 70f),
                                 new Color(0.95f, 0.85f, 0.7f), true);

            // ── Scrollable body ───────────────────────────────────────────────

            // ScrollRect host — needs an Image (even invisible) to receive scroll events.
            var scrollGO = new GameObject("ScrollView",
                typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGO.transform.SetParent(panelGO.transform, false);
            var scrollRt = (RectTransform)scrollGO.transform;
            scrollRt.anchorMin = scrollRt.anchorMax = new Vector2(0.5f, 0.5f);
            scrollRt.pivot = new Vector2(0.5f, 0.5f);
            scrollRt.anchoredPosition = new Vector2(0f, -15f);
            scrollRt.sizeDelta = new Vector2(950f, 430f);
            scrollGO.GetComponent<Image>().color = Color.clear;
            scrollRect = scrollGO.GetComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.scrollSensitivity = 35f;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.inertia = false;

            // Viewport — clips overflowing text via Mask.
            var viewportGO = new GameObject("Viewport",
                typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportGO.transform.SetParent(scrollGO.transform, false);
            var viewportRt = (RectTransform)viewportGO.transform;
            viewportRt.anchorMin = Vector2.zero;
            viewportRt.anchorMax = Vector2.one;
            viewportRt.offsetMin = Vector2.zero;
            viewportRt.offsetMax = Vector2.zero;
            // Alpha must be > 0 for the Mask component to clip children.
            viewportGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.004f);
            viewportGO.GetComponent<Mask>().showMaskGraphic = false;
            scrollRect.viewport = viewportRt;

            // Content — expands vertically to fit however much text there is.
            var contentGO = new GameObject("Content",
                typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRt = (RectTransform)contentGO.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = Vector2.zero;
            var vlg = contentGO.GetComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(8, 8, 8, 8);
            var csf = contentGO.GetComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = contentRt;

            // Body text inside the content container.
            var bodyGO = new GameObject("BodyText", typeof(RectTransform));
            bodyGO.transform.SetParent(contentGO.transform, false);
            popupBody = bodyGO.AddComponent<TextMeshProUGUI>();
            popupBody.fontSize = 30;
            popupBody.color = new Color(0.88f, 0.84f, 0.78f);
            popupBody.alignment = TextAlignmentOptions.TopLeft;
            popupBody.fontStyle = FontStyles.Italic;
            popupBody.lineSpacing = 12f;
            popupBody.overflowMode = TextOverflowModes.Overflow;
            popupBody.enableWordWrapping = true;
            popupBody.raycastTarget = false;

            // ── Close hint ────────────────────────────────────────────────────
            NewText(panelGO.transform, "[ Scroll to read  |  E or Esc to close ]", 22,
                    new Vector2(0f, -300f), new Vector2(900f, 40f),
                    new Color(0.55f, 0.50f, 0.52f), false);
        }

        private TMP_Text NewText(Transform parent, string content, int size, Vector2 pos, Vector2 dim, Color color, bool bold)
        {
            var go = new GameObject("Text_" + content, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = dim;

            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            if (bold) text.fontStyle = FontStyles.Bold;
            return text;
        }
    }
}
