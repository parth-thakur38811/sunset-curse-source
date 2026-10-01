using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using TMPro;
using SunsetCurse.Core;
using SunsetCurse.Audio;

namespace SunsetCurse.UI
{
    /// <summary>
    /// The shared co-op inventory panel. Press I to open. Built entirely in code (no scene wiring
    /// beyond the spawner GameObject + the crafting-table prefab reference).
    ///
    /// Panel contents:
    ///   • RESOURCE CARDS (Wood / Stone / Ritual Herb) — show the current count from
    ///     <see cref="Inventory"/>. Each is a draggable Minecraft-style item icon.
    ///   • HELD ITEMS (Torch / Lord's Syrup) — appear once crafted. Syrup has a USE button.
    ///   • BUILD CRAFTING TABLE recipe — three drop slots. Drag resource cards into the matching
    ///     slot until it shows e.g. 20/20. When all three slots are filled, BUILD lights up; click
    ///     it and a smoke effect plays, the table prefab spawns at your feet, the inventory
    ///     deducts the materials, and the UI closes.
    ///   • FILL FROM INVENTORY button — instant-fill the slots to the recipe amount (no drag spam).
    ///
    /// Multiplayer: every write goes through Inventory (NetworkBehaviour singleton) so all peers
    /// see the same shared counts, the same crafted items, and the same crafting table position.
    ///
    /// SETUP:
    ///   1. In SampleScene, create empty GameObject "InventoryUI" + add this component.
    ///   2. Drag your imported CraftingTable prefab into Crafting Table Prefab slot.
    ///   3. (Optional) Assign resource icons + sound effects.
    /// </summary>
    public class InventoryUI : MonoBehaviour
    {
        public static InventoryUI Instance { get; private set; }

        [Header("Crafting table build recipe")]
        [SerializeField] private int tableWoodCost = 20;
        [SerializeField] private int tableStoneCost = 10;
        [SerializeField] private int tableHerbCost = 4;

        [Header("Table prefab (user-imported)")]
        [Tooltip("The crafting table prefab. Spawns at the player's feet on BUILD. Needs a Collider " +
                 "+ the CraftingTable component on it.")]
        [SerializeField] private GameObject craftingTablePrefab;
        [Tooltip("Distance in front of the player the table appears.")]
        [SerializeField] private float tablePlacementDistance = 2.2f;

        [Header("Droppable held-item prefabs (user-imported)")]
        [Tooltip("Visual prefab spawned when a player DROPS their Lord's Syrup. Falls back to a " +
                 "purple glowing sphere if unassigned.")]
        [SerializeField] private GameObject lordsSyrupPrefab;
        [Tooltip("Visual prefab spawned when a player DROPS their Respawn Potion. Falls back to " +
                 "a green glowing sphere if unassigned.")]
        [SerializeField] private GameObject respawnPotionPrefab;

        [Header("Dropped-resource models (user-imported, optional)")]
        [Tooltip("Model spawned when a player DROPS Wood on the ground. Falls back to a brown " +
                 "glowing sphere if unassigned.")]
        [SerializeField] private GameObject woodDropPrefab;
        [Tooltip("Model spawned when a player DROPS Stone. Falls back to a grey glowing sphere.")]
        [SerializeField] private GameObject stoneDropPrefab;
        [Tooltip("Model spawned when a player DROPS Ritual Herb. Falls back to a green glowing sphere.")]
        [SerializeField] private GameObject herbDropPrefab;

        [Header("Stone Distractor look (optional)")]
        [Tooltip("MODEL for the thrown Stone Distractor. If assigned it replaces the procedural " +
                 "grey rock (and the texture below is ignored). Falls back to the rock if unassigned.")]
        [SerializeField] private GameObject stoneDistractorPrefab;
        [Tooltip("Dropped-resource + distractor MODELS are auto-scaled so their largest side is this " +
                 "many metres — lower this if your drops look too big. 0 = keep the prefab's own size.")]
        [SerializeField] private float dropModelSize = 0.5f;
        [Tooltip("Texture applied to the PROCEDURAL Stone Distractor rock (only used when no " +
                 "Stone Distractor Prefab is assigned above).")]
        [SerializeField] private Texture2D stoneDistractorTexture;

        [Header("Icons (optional — coloured squares used if missing)")]
        [SerializeField] private Sprite woodIcon;
        [SerializeField] private Sprite stoneIcon;
        [SerializeField] private Sprite herbIcon;
        [SerializeField] private Sprite torchIcon;
        [SerializeField] private Sprite syrupIcon;
        [SerializeField] private Sprite distractorIcon;
        [SerializeField] private Sprite potionIcon;

        [Header("Radio-escape item icons (drop your imported PNGs here)")]
        [Tooltip("Icon for the BATTERY card at the bottom of the inventory. A coloured square is " +
                 "used if left empty.")]
        [SerializeField] private Sprite batteryIcon;
        [Tooltip("Icon for the ELECTRICAL TRANSFORMER card.")]
        [SerializeField] private Sprite transformerIcon;
        [Tooltip("Icon for the HANDHELD RADIO (krotkofal) card.")]
        [SerializeField] private Sprite handheldRadioIcon;
        [Tooltip("Icon for the GOLDEN KEY card.")]
        [SerializeField] private Sprite goldenKeyIcon;

        [Header("River bucket (drop your inventory image here)")]
        [Tooltip("Icon for the BUCKET card at the bottom of the inventory (shown while carried; " +
                 "the label flips between EMPTY and RIVER WATER). A sky-blue square is used if " +
                 "left empty.")]
        [SerializeField] private Sprite bucketIcon;

        [Header("Audio (optional)")]
        [SerializeField] private AudioClip openSfx;
        [SerializeField] private AudioClip closeSfx;
        [SerializeField] private AudioClip dropSfx;
        [SerializeField] private AudioClip craftCompleteSfx;
        [SerializeField] private AudioClip syrupUseSfx;
        [Tooltip("Thud sound played when a stone distractor lands. Audible to all peers.")]
        [SerializeField] private AudioClip stoneDistractorLandSfx;

        [Header("Input")]
        [SerializeField] private Key toggleKey = Key.I;

        // Inventory icon colours (used as both icon tint AND fallback square colour).
        private static readonly Color WoodColor      = new Color(0.55f, 0.34f, 0.18f);
        private static readonly Color StoneColor     = new Color(0.55f, 0.55f, 0.58f);
        private static readonly Color HerbColor      = new Color(0.30f, 0.78f, 0.35f);
        private static readonly Color TorchColor     = new Color(1.00f, 0.65f, 0.25f);
        private static readonly Color SyrupColor     = new Color(0.65f, 0.30f, 0.85f);
        private static readonly Color DistractorColor = new Color(0.55f, 0.55f, 0.58f);   // grey stone
        private static readonly Color PotionColor     = new Color(0.40f, 0.85f, 0.55f);   // emerald
        private static readonly Color BucketColor     = new Color(0.55f, 0.75f, 0.95f);   // sky blue

        // Build state — per session, how much has been "placed" into each recipe slot.
        // (Not actually deducted from Inventory until BUILD is pressed.)
        private int placedWood, placedStone, placedHerb;

        // UI refs.
        private Canvas canvas;
        private GameObject root;
        private CanvasGroup rootGroup;
        private bool isOpen;

        private TMP_Text woodCountLabel, stoneCountLabel, herbCountLabel;
        private TMP_Text slotWoodLabel, slotStoneLabel, slotHerbLabel;
        private Image    slotWoodFill,  slotStoneFill,  slotHerbFill;
        private GameObject torchCard, syrupCard, distractorCard, potionCard, teleportCard;
        private TMP_Text teleportStatusLabel;
        private GameObject radioBatteryCard, radioTransformerCard, radioKrotkofalCard, radioKeyCard;
        private GameObject bucketCard;
        private TMP_Text bucketNameLabel;
        private Button buildBtn, fillBtn, useSyrupBtn, dropSyrupBtn, toggleTorchBtn, throwDistractorBtn, dropPotionBtn, closeBtn;
        private TMP_Text buildBtnLabel, statusLabel, torchStatusLabel, distractorStatusLabel, potionStatusLabel;

        // Drag overlay.
        private Image dragGhost;
        private bool dragActive;

        // Cursor restore — remember whether the player was in the locked state when they opened.
        private CursorLockMode preLock;
        private bool preCursorVisible;

        // ────────────────────────────── Lifecycle ──────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            EnsureEventSystem();   // required for drag/drop + button raycasts
            BuildPanel();
            Hide();
            // Counts + crafted items are per-player → subscribe to LOCAL player's changes.
            PlayerInventory.OnLocalChanged += Refresh;
            // The crafting-table built event + the syrup-used broadcast are SHARED.
            Inventory.OnCraftingTableBuilt    += HandleCraftingTableBuilt;
            Inventory.OnCraftingTableDespawned += HandleCraftingTableDespawned;
            Inventory.OnLordsSyrupUsed         += HandleLordsSyrupUsed;
            // World drops (dragged-out resources) get spawned on every peer when Inventory broadcasts.
            SunsetCurse.World.DroppedResource.EnsureSubscribed();
            // Stone distractors — same pattern. Pass our landing-sfx to the static so every spawn uses it.
            SunsetCurse.World.StoneDistractor.EnsureSubscribed();
            SunsetCurse.World.StoneDistractor.LandingSfx = stoneDistractorLandSfx;
            SunsetCurse.World.StoneDistractor.StoneTexture = stoneDistractorTexture;
            SunsetCurse.World.StoneDistractor.StonePrefab  = stoneDistractorPrefab;
            SunsetCurse.World.StoneDistractor.ModelTargetSize = dropModelSize;
            // Dropped wood/stone/herb models (optional; procedural spheres if unassigned).
            SunsetCurse.World.DroppedResource.WoodPrefab  = woodDropPrefab;
            SunsetCurse.World.DroppedResource.StonePrefab = stoneDropPrefab;
            SunsetCurse.World.DroppedResource.HerbPrefab  = herbDropPrefab;
            SunsetCurse.World.DroppedResource.ModelTargetSize = dropModelSize;
            // Lord's Syrup / Respawn Potion drops — push the user-imported prefabs to the world component.
            SunsetCurse.World.DroppableHeldItem.EnsureSubscribed();
            SunsetCurse.World.DroppableHeldItem.LordsSyrupPrefab    = lordsSyrupPrefab;
            SunsetCurse.World.DroppableHeldItem.RespawnPotionPrefab = respawnPotionPrefab;
            Refresh();
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            var module = go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            module.AssignDefaultActions();
        }

        private void OnDestroy()
        {
            PlayerInventory.OnLocalChanged -= Refresh;
            Inventory.OnCraftingTableBuilt    -= HandleCraftingTableBuilt;
            Inventory.OnCraftingTableDespawned -= HandleCraftingTableDespawned;
            Inventory.OnLordsSyrupUsed         -= HandleLordsSyrupUsed;
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (Keyboard.current == null) return;
            // Block toggle in the main menu / on game over to avoid the panel sticking around.
            if (SceneManager.GetActiveScene().name == "MainMenu") return;

            // Only I toggles the inventory. Esc is handled by PauseMenuController — if the inventory
            // is open it closes the inventory (without also popping the settings overlay); otherwise
            // it toggles settings. This avoids the old "Esc closes inventory AND opens settings" bug.
            if (Keyboard.current[toggleKey].wasPressedThisFrame) Toggle();

            // Q anywhere in-world throws a stone distractor (no need to open inventory first).
            if (Keyboard.current[Key.Q].wasPressedThisFrame) OnThrowDistractor();
        }

        // ────────────────────────────── Public API ──────────────────────────────

        /// <summary>Is the inventory panel currently open? Read by PauseMenuController so Esc can
        /// close the inventory instead of opening the settings overlay on top of it.</summary>
        public bool IsOpen => isOpen;

        public void Toggle() { if (isOpen) Hide(); else Show(); }

        public void Show()
        {
            if (isOpen) return;
            isOpen = true;
            rootGroup.alpha = 1f;
            rootGroup.interactable = true;
            rootGroup.blocksRaycasts = true;

            preLock = Cursor.lockState;
            preCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            PlaySfx(openSfx);
            Refresh();
        }

        public void Hide()
        {
            if (!isOpen) return;
            isOpen = false;
            if (rootGroup != null)
            {
                rootGroup.alpha = 0f;
                rootGroup.interactable = false;
                rootGroup.blocksRaycasts = false;
            }
            CancelDrag();
            // Always lock the cursor back when the inventory closes — even if it was unlocked when
            // we opened, the in-game state we want to restore is "locked + hidden" (gameplay).
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (statusLabel != null) statusLabel.text = "";
            PlaySfx(closeSfx);
        }

        // ────────────────────────────── Panel build ──────────────────────────────

        private void BuildPanel()
        {
            canvas = UIHelpers.NewCanvas("InventoryUI_Canvas", 110, transform);

            // Dimmed full-screen backdrop — closes nothing on click but absorbs raycasts so the
            // gameplay underneath doesn't receive them.
            var backdrop = UIHelpers.NewPanel(canvas.transform, "Backdrop", Vector2.zero,
                                              new Vector2(4000f, 4000f), new Color(0f, 0f, 0f, 0.55f));
            backdrop.raycastTarget = true;

            // Main panel — sized so the three columns + the radio strip all breathe.
            var panel = UIHelpers.NewPanel(canvas.transform, "Panel", Vector2.zero,
                                           new Vector2(1300f, 800f), new Color(0.05f, 0.05f, 0.07f, 0.98f));
            root = panel.gameObject;
            rootGroup = root.AddComponent<CanvasGroup>();
            // Start HIDDEN. A fresh CanvasGroup defaults to alpha 1, and Start()'s Hide() call
            // early-returns because isOpen is already false — so without this the panel would be
            // visible on spawn until you toggled I twice.
            rootGroup.alpha = 0f;
            rootGroup.interactable = false;
            rootGroup.blocksRaycasts = false;

            // Header (left-aligned, kept off the panel edge).
            UIHelpers.NewLabel(panel.transform, "INVENTORY", 44,
                new Vector2(-430f, 352f), new Vector2(360f, 56f),
                new Color(0.95f, 0.86f, 0.55f), TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            UIHelpers.NewLabel(panel.transform, "Drag resources into the recipe slots — or press FILL.",
                20, new Vector2(-250f, 312f), new Vector2(700f, 26f),
                new Color(0.7f, 0.7f, 0.72f), TextAlignmentOptions.MidlineLeft);

            // ─────────── LEFT COLUMN: resources (x = -500) ───────────
            UIHelpers.NewLabel(panel.transform, "RESOURCES", 24,
                new Vector2(-500f, 258f), new Vector2(260f, 30f),
                new Color(0.92f, 0.92f, 0.92f), TextAlignmentOptions.Center, FontStyles.Bold);
            BuildResourceCard(panel.transform, ResourceType.Wood,       new Vector2(-500f, 150f),  WoodColor, woodIcon,
                              out woodCountLabel);
            BuildResourceCard(panel.transform, ResourceType.Stone,      new Vector2(-500f, 10f),   StoneColor, stoneIcon,
                              out stoneCountLabel);
            BuildResourceCard(panel.transform, ResourceType.RitualHerb, new Vector2(-500f, -130f), HerbColor, herbIcon,
                              out herbCountLabel);

            // ─────────── MIDDLE COLUMN: build crafting table (x = 0) ───────────
            UIHelpers.NewLabel(panel.transform, "BUILD CRAFTING TABLE", 26,
                new Vector2(0f, 258f), new Vector2(460f, 34f),
                new Color(1f, 0.78f, 0.42f), TextAlignmentOptions.Center, FontStyles.Bold);

            BuildRecipeSlot(panel.transform, ResourceType.Wood,       new Vector2(-130f, 150f),
                            WoodColor, woodIcon, tableWoodCost,
                            out slotWoodLabel,  out slotWoodFill);
            BuildRecipeSlot(panel.transform, ResourceType.Stone,      new Vector2(   0f, 150f),
                            StoneColor, stoneIcon, tableStoneCost,
                            out slotStoneLabel, out slotStoneFill);
            BuildRecipeSlot(panel.transform, ResourceType.RitualHerb, new Vector2( 130f, 150f),
                            HerbColor, herbIcon, tableHerbCost,
                            out slotHerbLabel,  out slotHerbFill);

            buildBtn = UIHelpers.NewButton(panel.transform, "BUILD", new Vector2(0f, 8f),
                new Vector2(280f, 60f), OnBuild, out buildBtnLabel);
            fillBtn  = UIHelpers.NewButton(panel.transform, "FILL FROM INVENTORY", new Vector2(0f, -62f),
                new Vector2(280f, 44f), OnFillRecipe, out _);
            statusLabel = UIHelpers.NewLabel(panel.transform, "", 20,
                new Vector2(0f, -128f), new Vector2(500f, 50f),
                new Color(1f, 0.55f, 0.40f), TextAlignmentOptions.Center);

            // ─────────── RIGHT COLUMN: held items, one under another (x = 480) ───────────
            UIHelpers.NewLabel(panel.transform, "HELD ITEMS", 24,
                new Vector2(480f, 258f), new Vector2(300f, 30f),
                new Color(0.92f, 0.92f, 0.92f), TextAlignmentOptions.Center, FontStyles.Bold);

            torchCard = BuildHeldItemCard(panel.transform, "TORCH",
                "Flash (T) — distracts her 10s. One per night.",
                new Vector2(480f, 155f), TorchColor, torchIcon, out toggleTorchBtn, out torchStatusLabel);
            toggleTorchBtn.onClick.AddListener(OnFlashTorch);

            syrupCard = BuildHeldItemCard(panel.transform, "LORD'S SYRUP",
                "Stun the monster for 30 seconds.",
                new Vector2(480f, 5f), SyrupColor, syrupIcon, out useSyrupBtn, out _);
            useSyrupBtn.onClick.AddListener(OnUseSyrup);
            // Split the footer into USE (left) + DROP (right).
            ResizeButton(useSyrupBtn, new Vector2(-66f, -48f), new Vector2(124f, 34f));
            dropSyrupBtn = UIHelpers.NewButton(syrupCard.transform, "DROP", new Vector2(66f, -48f),
                new Vector2(124f, 34f), OnDropSyrup, out _);
            AttachHeldDrag(syrupCard, HeldDragKind.LordsSyrup, SyrupColor, syrupIcon);

            distractorCard = BuildHeldItemCard(panel.transform, "STONE DISTRACTOR",
                "Throw (Q) — lures her 15s. Reusable.",
                new Vector2(480f, -145f), DistractorColor, distractorIcon, out throwDistractorBtn, out distractorStatusLabel);
            throwDistractorBtn.onClick.AddListener(OnThrowDistractor);
            AttachHeldDrag(distractorCard, HeldDragKind.StoneDistractor, DistractorColor, distractorIcon);

            potionCard = BuildHeldItemCard(panel.transform, "RESPAWN POTION",
                "Auto-revives you on death.",
                new Vector2(480f, -295f), PotionColor, potionIcon, out dropPotionBtn, out potionStatusLabel);
            dropPotionBtn.onClick.RemoveAllListeners();
            dropPotionBtn.onClick.AddListener(OnDropPotion);
            AttachHeldDrag(potionCard, HeldDragKind.RespawnPotion, PotionColor, potionIcon);

            // Dawn Teleport (SP) / Revival (MP) Potion — status-only card (used with G, not dropped).
            teleportCard = BuildHeldItemCard(panel.transform,
                DifficultyPreference.Mode == GameMode.Multiplayer ? "REVIVAL POTION" : "TELEPORT POTION",
                DifficultyPreference.Mode == GameMode.Multiplayer
                    ? "Hold G while downed to revive."
                    : "Press G to teleport far away.",
                new Vector2(480f, -445f), new Color(0.55f, 0.4f, 1f), potionIcon,
                out var teleportBtn, out teleportStatusLabel);
            teleportBtn.gameObject.SetActive(false);   // no button — the potion is used with G in-world

            // ─────────── BOTTOM STRIP: radio-escape items (shown only while carried) ───────────
            UIHelpers.NewLabel(panel.transform, "RADIO ESCAPE ITEMS", 20,
                new Vector2(-290f, -238f), new Vector2(560f, 26f),
                new Color(0.60f, 0.78f, 0.95f), TextAlignmentOptions.Center, FontStyles.Bold);
            radioBatteryCard     = BuildRadioItemCard(panel.transform, "BATTERY",
                new Vector2(-500f, -308f), new Color(0.95f, 0.85f, 0.30f), batteryIcon);
            radioTransformerCard = BuildRadioItemCard(panel.transform, "TRANSFORMER",
                new Vector2(-360f, -308f), new Color(0.60f, 0.70f, 0.95f), transformerIcon);
            radioKrotkofalCard   = BuildRadioItemCard(panel.transform, "HANDHELD RADIO",
                new Vector2(-220f, -308f), new Color(0.55f, 0.90f, 0.75f), handheldRadioIcon);
            radioKeyCard         = BuildRadioItemCard(panel.transform, "GOLDEN KEY",
                new Vector2(-80f, -308f), new Color(1.00f, 0.78f, 0.20f), goldenKeyIcon);

            // River bucket — one more small card on the strip (shown only while carried); its
            // name label doubles as the state readout (EMPTY vs RIVER WATER).
            bucketCard = BuildRadioItemCard(panel.transform, "BUCKET",
                new Vector2(60f, -308f), BucketColor, bucketIcon);
            bucketNameLabel = bucketCard.GetComponentInChildren<TMP_Text>();

            // Close — bottom, in the clear gap between the bucket card and the held column.
            closeBtn = UIHelpers.NewButton(panel.transform, "Close (I / Esc)",
                new Vector2(230f, -352f), new Vector2(190f, 44f), Hide, out _);

            // Drag ghost (hidden until drag).
            var ghostGO = new GameObject("DragGhost", typeof(RectTransform), typeof(Image));
            ghostGO.transform.SetParent(canvas.transform, false);
            dragGhost = ghostGO.GetComponent<Image>();
            dragGhost.color = new Color(1f, 1f, 1f, 0f);
            dragGhost.raycastTarget = false;
            var ghostRt = (RectTransform)ghostGO.transform;
            ghostRt.sizeDelta = new Vector2(70f, 70f);
        }

        /// <summary>Card on the left side: shows resource count + acts as a draggable source.</summary>
        private void BuildResourceCard(Transform parent, ResourceType type, Vector2 pos,
                                       Color color, Sprite icon, out TMP_Text countLabel)
        {
            var go = new GameObject($"Card_{type}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(180f, 130f);
            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.12f, 0.10f, 0.10f, 0.95f);
            bg.raycastTarget = true;

            // Icon
            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(go.transform, false);
            var iconRt = (RectTransform)iconGO.transform;
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 1f);
            iconRt.pivot = new Vector2(0.5f, 1f);
            iconRt.anchoredPosition = new Vector2(0f, -10f);
            iconRt.sizeDelta = new Vector2(70f, 70f);
            var iconImg = iconGO.GetComponent<Image>();
            iconImg.sprite = icon;
            iconImg.color = color;
            iconImg.raycastTarget = false;

            // Name
            UIHelpers.NewLabel(go.transform, type.ToString().ToUpperInvariant(), 18,
                new Vector2(0f, -50f), new Vector2(160f, 20f), new Color(0.92f, 0.92f, 0.92f),
                TextAlignmentOptions.Center, FontStyles.Bold);

            // Count "x12"
            countLabel = UIHelpers.NewLabel(go.transform, "x0", 36,
                new Vector2(0f, -75f), new Vector2(160f, 36f),
                new Color(1f, 1f, 0.8f), TextAlignmentOptions.Center, FontStyles.Bold);

            // Drag handler
            var drag = go.AddComponent<DraggableResource>();
            drag.Setup(this, type, color, icon);
        }

        /// <summary>Middle column: a drop slot for one resource. Shows N/MAX progress fill.</summary>
        private void BuildRecipeSlot(Transform parent, ResourceType type, Vector2 pos,
                                     Color color, Sprite icon, int max,
                                     out TMP_Text label, out Image fill)
        {
            var go = new GameObject($"Slot_{type}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(120f, 140f);
            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.10f, 1f);
            bg.raycastTarget = true;

            // Fill bar (rises as items are placed)
            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(go.transform, false);
            var fillRt = (RectTransform)fillGO.transform;
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(1f, 0f);
            fillRt.pivot = new Vector2(0.5f, 0f);
            fillRt.offsetMin = new Vector2(4f, 4f);
            fillRt.offsetMax = new Vector2(-4f, -4f);
            fill = fillGO.GetComponent<Image>();
            fill.color = new Color(color.r, color.g, color.b, 0.35f);
            fill.raycastTarget = false;

            // Icon
            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(go.transform, false);
            var iconRt = (RectTransform)iconGO.transform;
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 1f);
            iconRt.pivot = new Vector2(0.5f, 1f);
            iconRt.anchoredPosition = new Vector2(0f, -8f);
            iconRt.sizeDelta = new Vector2(50f, 50f);
            var iconImg = iconGO.GetComponent<Image>();
            iconImg.sprite = icon;
            iconImg.color = color;
            iconImg.raycastTarget = false;

            // Label "0 / 20"
            label = UIHelpers.NewLabel(go.transform, $"0 / {max}", 22,
                new Vector2(0f, -55f), new Vector2(110f, 30f),
                new Color(1f, 1f, 0.85f), TextAlignmentOptions.Center, FontStyles.Bold);

            // Drop handler
            var drop = go.AddComponent<RecipeDropTarget>();
            drop.Setup(this, type);
        }

        /// <summary>Bottom row: a small card for one radio-escape item (battery / transformer /
        /// handheld radio / golden key). Shows the imported PNG icon (tinted white so the art reads
        /// true) or a coloured square if none is assigned. Visible only while carried.</summary>
        private GameObject BuildRadioItemCard(Transform parent, string name, Vector2 pos, Color color, Sprite icon)
        {
            var go = new GameObject($"Radio_{name}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(132f, 96f);
            go.GetComponent<Image>().color = new Color(0.10f, 0.10f, 0.12f, 0.95f);

            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(go.transform, false);
            var iconRt = (RectTransform)iconGO.transform;
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 1f);
            iconRt.pivot = new Vector2(0.5f, 1f);
            iconRt.anchoredPosition = new Vector2(0f, -8f);
            iconRt.sizeDelta = new Vector2(44f, 44f);
            var iconImg = iconGO.GetComponent<Image>();
            iconImg.sprite = icon;
            iconImg.color = icon != null ? Color.white : color;   // real art shows true colour
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;

            UIHelpers.NewLabel(go.transform, name, 13,
                new Vector2(0f, -34f), new Vector2(126f, 40f), new Color(0.92f, 0.92f, 0.92f),
                TextAlignmentOptions.Top, FontStyles.Bold);

            return go;
        }

        /// <summary>Right column: held-item card. Icon top-left, name beside it, wrapped description
        /// across the middle, button(s) in the footer — all sized to stay inside the card.</summary>
        private GameObject BuildHeldItemCard(Transform parent, string name, string desc, Vector2 pos,
                                              Color color, Sprite icon, out Button useBtn, out TMP_Text statusOut)
        {
            var go = new GameObject($"Held_{name}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(300f, 140f);
            go.GetComponent<Image>().color = new Color(0.10f, 0.08f, 0.10f, 0.95f);

            // Icon — upper-left.
            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(go.transform, false);
            var iconRt = (RectTransform)iconGO.transform;
            iconRt.anchorMin = iconRt.anchorMax = new Vector2(0.5f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.anchoredPosition = new Vector2(-116f, 34f);
            iconRt.sizeDelta = new Vector2(46f, 46f);
            var iconImg = iconGO.GetComponent<Image>();
            iconImg.sprite = icon;
            iconImg.color = icon != null ? Color.white : color;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;

            // Name — beside the icon.
            UIHelpers.NewLabel(go.transform, name, 20,
                new Vector2(30f, 40f), new Vector2(200f, 26f), color,
                TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            // Description — full width across the middle, wrapped so it can't bleed.
            statusOut = UIHelpers.NewLabel(go.transform, desc, 14,
                new Vector2(6f, 2f), new Vector2(272f, 44f),
                new Color(0.80f, 0.80f, 0.80f), TextAlignmentOptions.Top);
            // Footer button.
            useBtn = UIHelpers.NewButton(go.transform, "USE", new Vector2(0f, -48f),
                new Vector2(264f, 34f), () => {}, out _);

            return go;
        }

        // ────────────────────────────── Refresh / state ──────────────────────────────

        private void Refresh()
        {
            if (woodCountLabel == null) return;

            var inv = PlayerInventory.Local;
            int wood  = inv != null ? inv.Wood       : 0;
            int stone = inv != null ? inv.Stone      : 0;
            int herb  = inv != null ? inv.RitualHerb : 0;

            woodCountLabel.text  = $"x{wood}";
            stoneCountLabel.text = $"x{stone}";
            herbCountLabel.text  = $"x{herb}";

            // Cap slot values to what THIS PLAYER's inventory still actually has (in case it dropped).
            placedWood  = Mathf.Min(placedWood,  wood,  tableWoodCost);
            placedStone = Mathf.Min(placedStone, stone, tableStoneCost);
            placedHerb  = Mathf.Min(placedHerb,  herb,  tableHerbCost);

            UpdateSlotVisual(slotWoodLabel,  slotWoodFill,  placedWood,  tableWoodCost);
            UpdateSlotVisual(slotStoneLabel, slotStoneFill, placedStone, tableStoneCost);
            UpdateSlotVisual(slotHerbLabel,  slotHerbFill,  placedHerb,  tableHerbCost);

            // BUILD enables only when slots are FULL and the (shared) table doesn't exist yet.
            bool tableAlready = Inventory.HasCraftingTable;
            bool slotsFull = placedWood >= tableWoodCost
                          && placedStone >= tableStoneCost
                          && placedHerb >= tableHerbCost;
            buildBtn.interactable = !tableAlready && slotsFull && inv != null;
            buildBtnLabel.text = tableAlready ? "Table already built" : "BUILD";

            // FILL enables when the LOCAL player can afford the recipe and the table isn't built.
            bool canAfford = inv != null && inv.CanAfford(tableWoodCost, tableStoneCost, tableHerbCost);
            fillBtn.interactable = !tableAlready && canAfford && !slotsFull;

            // Held items — per-player.
            bool hasTorch    = inv != null && inv.HasTorch;
            bool hasSyrup    = inv != null && inv.HasLordsSyrup;
            bool syrupUsed   = inv != null && inv.LordsSyrupUsed;
            int  distractors = inv != null ? inv.StoneDistractors : 0;
            int  potions     = inv != null ? inv.RespawnPotions : 0;
            int  teleports   = inv != null ? inv.TeleportPotions : 0;
            torchCard.SetActive(hasTorch);
            syrupCard.SetActive(hasSyrup || syrupUsed);
            distractorCard.SetActive(distractors > 0);
            potionCard.SetActive(potions > 0);
            if (teleportCard != null)
            {
                teleportCard.SetActive(teleports > 0);
                if (teleportStatusLabel != null) teleportStatusLabel.text = $"x{teleports}";
            }

            // Radio-escape items — per-player carry flags (replicated owner-write bools).
            if (radioBatteryCard != null)
            {
                radioBatteryCard.SetActive(inv != null && inv.HasBattery);
                radioTransformerCard.SetActive(inv != null && inv.HasTransformer);
                radioKrotkofalCard.SetActive(inv != null && inv.HasKrotkofal);
                radioKeyCard.SetActive(inv != null && inv.HasGoldenKey);
            }

            // River bucket — shown while carried; label doubles as the empty/filled readout.
            if (bucketCard != null)
            {
                BucketState bucket = inv != null ? inv.Bucket : BucketState.None;
                bucketCard.SetActive(bucket != BucketState.None);
                if (bucketNameLabel != null)
                    bucketNameLabel.text = bucket == BucketState.Filled
                        ? "BUCKET - RIVER WATER"
                        : "BUCKET - EMPTY";
            }

            // Stone Distractor card
            throwDistractorBtn.interactable = distractors > 0;
            var throwLabel = throwDistractorBtn.GetComponentInChildren<TMP_Text>();
            if (throwLabel != null) throwLabel.text = distractors > 0 ? $"THROW (Q)  x{distractors}" : "—";
            if (distractorStatusLabel != null)
                distractorStatusLabel.text = distractors > 0
                    ? $"Held: {distractors}. Throw → monster investigates 15s."
                    : "—";

            // Respawn Potion card (no button — passive)
            if (potionStatusLabel != null)
                potionStatusLabel.text = potions > 0
                    ? $"Held: {potions}. Auto-activates on death."
                    : "—";

            // Torch card — shows whether the night's flash has been used yet.
            var localTorch = Camera.main != null
                ? Camera.main.GetComponentInParent<SunsetCurse.Player.Torch>()
                : Object.FindAnyObjectByType<SunsetCurse.Player.Torch>();
            bool torchUsedTonight = localTorch != null && localTorch.UsedThisNight;
            toggleTorchBtn.interactable = hasTorch && !torchUsedTonight;
            var torchBtnLabel = toggleTorchBtn.GetComponentInChildren<TMP_Text>();
            if (torchBtnLabel != null)
                torchBtnLabel.text = !hasTorch ? "—"
                                    : torchUsedTonight ? "USED TONIGHT" : "FLASH (T)";
            if (torchStatusLabel != null)
                torchStatusLabel.text = torchUsedTonight
                    ? "Spent for tonight. Resets at dawn."
                    : "Ready — aim at the monster and press T.";

            // Syrup card — USE pours it (monster stun), DROP ejects to world for pickup by anyone.
            useSyrupBtn.interactable = hasSyrup && !syrupUsed;
            dropSyrupBtn.interactable = hasSyrup;
            var useLabel = useSyrupBtn.GetComponentInChildren<TMP_Text>();
            if (useLabel != null)
                useLabel.text = syrupUsed ? "USED" : (hasSyrup ? "USE" : "—");
            var dropSyrLabel = dropSyrupBtn.GetComponentInChildren<TMP_Text>();
            if (dropSyrLabel != null) dropSyrLabel.text = hasSyrup ? "DROP" : "—";

            // Potion card — single DROP button (USE is automatic on death).
            dropPotionBtn.interactable = potions > 0;
            var dropPotLabel = dropPotionBtn.GetComponentInChildren<TMP_Text>();
            if (dropPotLabel != null) dropPotLabel.text = potions > 0 ? "DROP" : "—";
        }

        /// <summary>Public hook so <see cref="SunsetCurse.Player.Torch"/> can ping us to refresh
        /// the card label when the player toggles via the T key (the local change otherwise wouldn't
        /// fire any Inventory event).</summary>
        public void RefreshExternal() => Refresh();

        private static void UpdateSlotVisual(TMP_Text label, Image fill, int current, int max)
        {
            label.text = $"{current} / {max}";
            float k = max <= 0 ? 0f : (float)current / max;
            var rt = (RectTransform)fill.transform;
            // Stretch the fill vertically — anchorMax.y = fraction.
            rt.anchorMax = new Vector2(1f, k);
        }

        // ────────────────────────────── Drag / drop ──────────────────────────────

        public void OnDragBegin(ResourceType type, Color color, Sprite icon)
        {
            var inv = PlayerInventory.Local;
            int available = inv != null ? inv.Get(type) : 0;
            if (available <= 0) { dragActive = false; return; }
            // Don't allow dragging beyond what we can still place.
            int placed = GetPlaced(type);
            int max = GetMax(type);
            if (placed >= Mathf.Min(max, available)) { dragActive = false; return; }

            dragActive = true;
            dragGhost.sprite = icon;
            dragGhost.color = new Color(color.r, color.g, color.b, 0.85f);
            UpdateGhostPosition();
        }

        public void OnDragMove(Vector2 screenPos)
        {
            if (!dragActive) return;
            UpdateGhostPosition(screenPos);
        }

        public void OnDragEnd(Vector2 screenPos, ResourceType type)
        {
            if (!dragActive) { CancelDrag(); return; }

            // If released OUTSIDE the panel, eject ALL of that resource into the world as a
            // pickup any teammate can grab. The OnDrop event for a slot only fires when released
            // on a slot, so "outside the panel" is the clean signal here.
            bool insidePanel = root != null && RectTransformUtility.RectangleContainsScreenPoint(
                (RectTransform)root.transform, screenPos, null);
            if (!insidePanel) DropResourceToWorld(type);

            CancelDrag();
        }

        private void DropResourceToWorld(ResourceType type)
        {
            var inv = PlayerInventory.Local;
            if (inv == null) return;
            int amount = inv.Get(type);
            if (amount <= 0) { statusLabel.text = $"No {type} to drop."; return; }

            // Atomic: pull out of inventory first; if that fails (race), bail.
            if (!inv.TryConsume(type, amount))
            {
                statusLabel.text = "Drop failed — inventory changed.";
                return;
            }

            Vector3 pos = ComputeWorldDropPos();
            Inventory.RequestDropResource(type, amount, pos);
            statusLabel.text = $"Dropped {amount} {type} — teammates can pick it up.";
        }

        private Vector3 ComputeWorldDropPos()
        {
            // ~1.8 m in front of the local player's view, dropped to the surface below. Cast from
            // eye height (not the sky) so drops inside a house land on the FLOOR, not the roof.
            var cam = FindLocalPlayerCamera();
            if (cam == null) return Vector3.zero;
            Vector3 fwd = cam.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 candidate = cam.position + fwd * 1.8f;
            if (Physics.Raycast(candidate, Vector3.down, out var hit, 12f,
                                ~0, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.1f;
            if (Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out hit, 50f,
                                ~0, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.1f;
            return candidate + Vector3.down * 1.5f;
        }

        public void OnDropOnSlot(ResourceType slotType, ResourceType draggedType)
        {
            if (!dragActive) return;
            if (slotType != draggedType)
            {
                statusLabel.text = "Wrong slot — drag matching resource.";
                return;
            }
            // BULK FILL: one drag tops the slot up to its recipe amount (capped by what's in
            // your inventory). The actual deduction still only happens on BUILD — slots are just
            // a visual allocation. Surplus stays in your inventory.
            int max = GetMax(draggedType);
            int placed = GetPlaced(draggedType);
            int available = PlayerInventory.Local != null ? PlayerInventory.Local.Get(draggedType) : 0;
            int spaceLeft = max - placed;
            int unplacedAvailable = available - placed;
            int toAdd = Mathf.Max(0, Mathf.Min(spaceLeft, unplacedAvailable));
            if (toAdd > 0) TryPlace(draggedType, toAdd);
        }

        private void UpdateGhostPosition() => UpdateGhostPosition(Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero);

        private void UpdateGhostPosition(Vector2 screenPos)
        {
            var rt = (RectTransform)dragGhost.transform;
            // Convert screen → canvas local.
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)canvas.transform, screenPos, null, out var local);
            rt.anchoredPosition = local;
        }

        private void CancelDrag()
        {
            dragActive = false;
            if (dragGhost != null) dragGhost.color = new Color(1f, 1f, 1f, 0f);
        }

        private void TryPlace(ResourceType type, int amount)
        {
            int placed = GetPlaced(type);
            int max = GetMax(type);
            int available = PlayerInventory.Local != null ? PlayerInventory.Local.Get(type) : 0;
            int canAdd = Mathf.Min(amount, max - placed, available - placed);
            if (canAdd <= 0) return;
            SetPlaced(type, placed + canAdd);
            PlaySfx(dropSfx);
            Refresh();
        }

        private int GetPlaced(ResourceType type) => type switch
        {
            ResourceType.Wood       => placedWood,
            ResourceType.Stone      => placedStone,
            ResourceType.RitualHerb => placedHerb,
            _ => 0
        };

        private void SetPlaced(ResourceType type, int v)
        {
            switch (type)
            {
                case ResourceType.Wood:       placedWood  = v; break;
                case ResourceType.Stone:      placedStone = v; break;
                case ResourceType.RitualHerb: placedHerb  = v; break;
            }
        }

        private int GetMax(ResourceType type) => type switch
        {
            ResourceType.Wood       => tableWoodCost,
            ResourceType.Stone      => tableStoneCost,
            ResourceType.RitualHerb => tableHerbCost,
            _ => 0
        };

        // ────────────────────────────── Button handlers ──────────────────────────────

        private void OnFillRecipe()
        {
            if (Inventory.HasCraftingTable) return;
            var inv = PlayerInventory.Local;
            if (inv == null || !inv.CanAfford(tableWoodCost, tableStoneCost, tableHerbCost))
            {
                statusLabel.text = $"Need {tableWoodCost} W + {tableStoneCost} S + {tableHerbCost} H (YOUR inventory).";
                return;
            }
            placedWood = tableWoodCost;
            placedStone = tableStoneCost;
            placedHerb = tableHerbCost;
            PlaySfx(dropSfx);
            Refresh();
        }

        private void OnBuild()
        {
            if (Inventory.HasCraftingTable) return;
            if (placedWood < tableWoodCost || placedStone < tableStoneCost || placedHerb < tableHerbCost)
            {
                statusLabel.text = "Fill all three slots first.";
                return;
            }
            var inv = PlayerInventory.Local;
            if (inv == null)
            {
                statusLabel.text = "No local player.";
                return;
            }
            if (craftingTablePrefab == null)
            {
                statusLabel.text = "ERROR: Crafting Table Prefab not assigned on InventoryUI.";
                Debug.LogError("[InventoryUI] craftingTablePrefab is not set in the Inspector.", this);
                return;
            }

            // Atomic per-player deduct. Returns false if our inventory changed underfoot.
            if (!inv.TryConsumeMany(tableWoodCost, tableStoneCost, tableHerbCost))
            {
                statusLabel.text = "Inventory changed — not enough materials any more.";
                return;
            }

            // Pick a spawn pos in front of the local player.
            Vector3 spawnPos = ComputeTableSpawnPos();

            // Tell the SHARED Inventory to flip "table built" + remember position; every peer
            // then locally spawns the prefab via HandleCraftingTableBuilt.
            Inventory.RequestBuildCraftingTable(spawnPos);

            placedWood = placedStone = placedHerb = 0;
            statusLabel.text = "Building crafting table…";
            Hide();
        }

        private Vector3 ComputeTableSpawnPos()
        {
            // Walk along the local player's forward by `tablePlacementDistance`.
            var localPlayer = FindLocalPlayerCamera();
            if (localPlayer == null) return Vector3.zero;
            Vector3 fwd = localPlayer.forward;
            fwd.y = 0f; fwd.Normalize();
            Vector3 candidate = localPlayer.position + fwd * tablePlacementDistance;
            // Drop to the surface below — casting from EYE height, not from 5m up. Indoors a
            // sky-cast starts above the ceiling and lands the table on the ROOF (which is why
            // the table "never showed up" when built inside the safe house). From eye height the
            // ray hits the house FLOOR inside, or the ground outside.
            if (Physics.Raycast(candidate, Vector3.down, out var hit, 12f,
                                ~0, QueryTriggerInteraction.Ignore))
                return hit.point;
            // Nothing directly below (over a ledge?) — try the old sky-cast, then our own feet.
            if (Physics.Raycast(candidate + Vector3.up * 5f, Vector3.down, out hit, 50f,
                                ~0, QueryTriggerInteraction.Ignore))
                return hit.point;
            return candidate + Vector3.down * 1.6f;
        }

        private Transform FindLocalPlayerCamera()
        {
            // Camera.main is our local FPS view; its position/forward = where we're aiming.
            return Camera.main != null ? Camera.main.transform : null;
        }

        private void OnUseSyrup()
        {
            var inv = PlayerInventory.Local;
            if (inv == null || !inv.HasLordsSyrup || inv.LordsSyrupUsed) return;
            if (!inv.TryUseLordsSyrup()) return;
            // syrupUseSfx is played by HandleLordsSyrupUsed below, which fires on EVERY peer
            // (via Inventory's networked broadcast) — that way teammates hear the pour too.
            statusLabel.text = "Lord's Syrup poured. The monster freezes for 30s.";
        }

        private void OnFlashTorch()
        {
            // Mouse path — same effect as pressing T. The Torch component does the aim check
            // (which means the player must be looking at the monster when they click — so this
            // is most useful if they aim first, then open inventory, then click. T key in-world
            // is the primary path.)
            var torch = Camera.main != null
                ? Camera.main.GetComponentInParent<SunsetCurse.Player.Torch>()
                : Object.FindAnyObjectByType<SunsetCurse.Player.Torch>();
            if (torch == null) { statusLabel.text = "No torch found on the local player."; return; }
            torch.TryFlash();
        }

        private void OnThrowDistractor()
        {
            var inv = PlayerInventory.Local;
            if (inv == null || !inv.HasStoneDistractor) return;
            // Compute throw landing position — in front of where the player is looking, on the ground.
            Vector3 landingPos = ComputeWorldDropPos();
            if (!inv.TryRemoveStoneDistractor()) return;
            Inventory.RequestSpawnDistractor(landingPos);
            statusLabel.text = "Distractor thrown — monster will investigate.";
        }

        private void OnDropSyrup()
        {
            var inv = PlayerInventory.Local;
            if (inv == null || !inv.HasLordsSyrup) return;
            Vector3 pos = ComputeWorldDropPos();
            if (!inv.TryRemoveLordsSyrup()) return;
            Inventory.RequestSpawnHeldDrop(HeldItemType.LordsSyrup, pos);
            statusLabel.text = "Lord's Syrup dropped — teammates can pick it up.";
        }

        private void OnDropPotion()
        {
            var inv = PlayerInventory.Local;
            if (inv == null || inv.RespawnPotions <= 0) return;
            Vector3 pos = ComputeWorldDropPos();
            if (!inv.TryRemoveRespawnPotion()) return;
            Inventory.RequestSpawnHeldDrop(HeldItemType.RespawnPotion, pos);
            statusLabel.text = "Respawn Potion dropped — teammates can pick it up.";
        }

        /// <summary>Move + resize an existing button (used after BuildHeldItemCard to fit two
        /// buttons side-by-side in a card footer).</summary>
        private static void ResizeButton(Button btn, Vector2 anchoredPos, Vector2 size)
        {
            var rt = (RectTransform)btn.transform;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            // The label child is a stretched RectTransform; resize it too so the text reflows.
            foreach (var t in btn.GetComponentsInChildren<RectTransform>(true))
                if (t != rt) t.sizeDelta = size;
        }

        /// <summary>Bolts a drag handler onto a held-item card so the player can grab the card,
        /// drag it OUTSIDE the panel, and drop the item into the world.</summary>
        private void AttachHeldDrag(GameObject card, HeldDragKind kind, Color tint, Sprite icon)
        {
            var dh = card.AddComponent<DraggableHeldItem>();
            dh.Setup(this, kind, tint, icon);
        }

        /// <summary>Called by DraggableHeldItem.OnBeginDrag — light up the ghost icon (same one the
        /// resource cards use, just with a different tint/sprite).</summary>
        public void OnHeldDragBegin(HeldDragKind kind, Color color, Sprite icon)
        {
            // Only allow dragging if we actually hold the item.
            var inv = PlayerInventory.Local;
            if (inv == null) { dragActive = false; return; }
            bool hold = kind switch
            {
                HeldDragKind.StoneDistractor => inv.HasStoneDistractor,
                HeldDragKind.LordsSyrup      => inv.HasLordsSyrup,
                HeldDragKind.RespawnPotion   => inv.RespawnPotions > 0,
                _ => false,
            };
            if (!hold) { dragActive = false; return; }

            dragActive = true;
            dragGhost.sprite = icon;
            dragGhost.color = new Color(color.r, color.g, color.b, 0.85f);
            UpdateGhostPosition();
        }

        /// <summary>Drag ended — if the player released OUTSIDE the panel, eject the item to the
        /// world. Inside the panel = cancel (no slot accepts held items).</summary>
        public void OnHeldDragEnd(Vector2 screenPos, HeldDragKind kind)
        {
            if (!dragActive) { CancelDrag(); return; }
            bool insidePanel = root != null && RectTransformUtility.RectangleContainsScreenPoint(
                (RectTransform)root.transform, screenPos, null);
            if (!insidePanel) DropHeldItemToWorld(kind);
            CancelDrag();
        }

        private void DropHeldItemToWorld(HeldDragKind kind)
        {
            switch (kind)
            {
                case HeldDragKind.StoneDistractor: OnThrowDistractor(); break;
                case HeldDragKind.LordsSyrup:      OnDropSyrup(); break;
                case HeldDragKind.RespawnPotion:   OnDropPotion(); break;
            }
        }

        // ────────────────────────────── Event handlers ──────────────────────────────

        private GameObject spawnedTable;

        private void HandleCraftingTableBuilt()
        {
            // Plays on EVERY peer the moment the table is built — local smoke + table prefab spawn.
            Vector3 pos = Inventory.CraftingTablePos;
            StartCoroutine(BuildSmokeRoutine(pos));
            PlaySfx(craftCompleteSfx);
        }

        private void HandleCraftingTableDespawned()
        {
            // Daily reset: the SHARED Inventory.HasCraftingTable flipped to false (Inventory's
            // server-side OnDayStart hook). Every peer destroys their local table GameObject.
            if (spawnedTable != null) { Destroy(spawnedTable); spawnedTable = null; }
        }

        private void HandleLordsSyrupUsed()
        {
            // Fires on EVERY peer (driven by Inventory's networked broadcast). MonsterAI handles
            // its own stun via the same event. Here we give visual + audio feedback so teammates
            // know it's happening.
            StartCoroutine(FlashRoutine(SyrupColor, 0.6f));
            PlaySfx(syrupUseSfx);
        }

        // ────────────────────────────── Smoke / FX ──────────────────────────────

        private IEnumerator BuildSmokeRoutine(Vector3 worldPos)
        {
            SpawnSmokeParticles(worldPos);

            // Short on-screen gray smoke wash for everybody in range so the moment reads.
            var wash = UIHelpers.NewPanel(canvas.transform, "SmokeWash", Vector2.zero,
                                          new Vector2(4000f, 4000f), new Color(0.7f, 0.7f, 0.72f, 0f));
            wash.raycastTarget = false;
            float t = 0f;
            const float fadeIn = 0.45f, hold = 0.20f, fadeOut = 0.85f;
            while (t < fadeIn) { t += Time.unscaledDeltaTime; wash.color = new Color(0.7f, 0.7f, 0.72f, Mathf.Lerp(0f, 0.78f, t / fadeIn)); yield return null; }
            yield return new WaitForSecondsRealtime(hold);
            // Spawn the actual table once the wash is at peak, so it visually "appears through the smoke".
            SpawnCraftingTablePrefab(worldPos);
            t = 0f;
            while (t < fadeOut) { t += Time.unscaledDeltaTime; wash.color = new Color(0.7f, 0.7f, 0.72f, Mathf.Lerp(0.78f, 0f, t / fadeOut)); yield return null; }
            Destroy(wash.gameObject);
        }

        private void SpawnCraftingTablePrefab(Vector3 worldPos)
        {
            if (craftingTablePrefab == null) return;
            // Face the player.
            Quaternion rot = Quaternion.identity;
            var cam = FindLocalPlayerCamera();
            if (cam != null)
            {
                Vector3 lookFrom = cam.position; lookFrom.y = worldPos.y;
                rot = Quaternion.LookRotation(lookFrom - worldPos);
            }
            // Spawn locally on each peer — Inventory.HasCraftingTable already synced. Keep a
            // reference so HandleCraftingTableDespawned can clean it up at dawn.
            if (spawnedTable != null) Destroy(spawnedTable);
            spawnedTable = Instantiate(craftingTablePrefab, worldPos, rot);
        }

        private void SpawnSmokeParticles(Vector3 worldPos)
        {
            var go = new GameObject("CraftingSmoke");
            go.transform.position = worldPos + Vector3.up * 0.8f;
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = 1.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.6f, 2.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 2.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
            main.startColor = new Color(0.75f, 0.75f, 0.78f, 0.7f);
            main.maxParticles = 200;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction = ParticleSystemStopAction.Destroy;

            var emission = ps.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 80) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.6f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.gray, 0f), new GradientColorKey(new Color(0.5f, 0.5f, 0.55f), 1f) },
                new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var sizeOL = ps.sizeOverLifetime;
            sizeOL.enabled = true;
            var sizeCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 2.2f));
            sizeOL.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            // Use a default URP-compatible material (white particle).
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.material = new Material(Shader.Find("Sprites/Default"));

            ps.Play();
        }

        private IEnumerator FlashRoutine(Color tint, float duration)
        {
            var wash = UIHelpers.NewPanel(canvas.transform, "Flash", Vector2.zero,
                                          new Vector2(4000f, 4000f), new Color(tint.r, tint.g, tint.b, 0f));
            wash.raycastTarget = false;
            float t = 0f;
            float peak = duration * 0.35f;
            while (t < peak) { t += Time.unscaledDeltaTime; wash.color = new Color(tint.r, tint.g, tint.b, Mathf.Lerp(0f, 0.45f, t / peak)); yield return null; }
            t = 0f;
            float fade = duration - peak;
            while (t < fade) { t += Time.unscaledDeltaTime; wash.color = new Color(tint.r, tint.g, tint.b, Mathf.Lerp(0.45f, 0f, t / fade)); yield return null; }
            Destroy(wash.gameObject);
        }

        private void PlaySfx(AudioClip clip)
        {
            if (clip != null && AudioManager.Instance != null) AudioManager.Instance.PlaySfx2D(clip);
        }
    }

    // ────────────────────────────── Drag handlers ──────────────────────────────

    /// <summary>Attach to each resource card. Starts a drag of the matching ResourceType.</summary>
    public class DraggableResource : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private InventoryUI ui;
        public ResourceType Type { get; private set; }
        private Color color;
        private Sprite icon;

        public void Setup(InventoryUI owner, ResourceType t, Color c, Sprite i)
        {
            ui = owner; Type = t; color = c; icon = i;
        }

        public void OnBeginDrag(PointerEventData e) => ui.OnDragBegin(Type, color, icon);
        public void OnDrag(PointerEventData e)      => ui.OnDragMove(e.position);
        public void OnEndDrag(PointerEventData e)   => ui.OnDragEnd(e.position, Type);
    }

    /// <summary>Attach to each recipe slot. Calls back into the UI when a draggable lands on it.</summary>
    public class RecipeDropTarget : MonoBehaviour, IDropHandler
    {
        private InventoryUI ui;
        private ResourceType type;

        public void Setup(InventoryUI owner, ResourceType t)
        {
            ui = owner; type = t;
        }

        public void OnDrop(PointerEventData e)
        {
            var src = e.pointerDrag != null ? e.pointerDrag.GetComponent<DraggableResource>() : null;
            if (src == null) return;
            ui.OnDropOnSlot(type, src.Type);
        }
    }

    /// <summary>What KIND of held item a draggable card represents — picked up by the drop-out
    /// handler in InventoryUI to call the right drop method.</summary>
    public enum HeldDragKind { StoneDistractor, LordsSyrup, RespawnPotion }

    /// <summary>Drag handler attached to each held-item card. When the player drags the card
    /// OUTSIDE the inventory panel and releases, the item is ejected to the world.</summary>
    public class DraggableHeldItem : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private InventoryUI ui;
        private HeldDragKind kind;
        private Color color;
        private Sprite icon;

        public void Setup(InventoryUI owner, HeldDragKind k, Color c, Sprite i)
        {
            ui = owner; kind = k; color = c; icon = i;
            // Card's background Image is raycast-target by default — that's what catches the pointer.
        }

        public void OnBeginDrag(PointerEventData e) => ui.OnHeldDragBegin(kind, color, icon);
        public void OnDrag(PointerEventData e)      => ui.OnDragMove(e.position);
        public void OnEndDrag(PointerEventData e)   => ui.OnHeldDragEnd(e.position, kind);
    }
}
