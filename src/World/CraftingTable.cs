using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using SunsetCurse.Core;
using SunsetCurse.UI;

namespace SunsetCurse.World
{
    /// <summary>
    /// World-placed crafting bench. Spawned by <see cref="InventoryUI"/> after the player drags
    /// 20 Wood + 10 Stone + 4 Ritual Herb into the inventory's build recipe. Press E to open the
    /// crafting menu — two one-shot recipes for the whole run:
    ///
    ///   • TORCH        — boosts the local flashlight permanently (5 Wood + 1 Stone, 1 per game).
    ///   • LORD'S SYRUP — consumable that stuns the monster for 30s (2 RitualHerb + 1 Stone, 1 per game).
    ///
    /// Built-in code UI (no scene wiring beyond the table prefab + a Collider on it).
    ///
    /// SETUP for the table prefab:
    ///   1. Import your crafting table model into Assets/_Project/Prefabs/CraftingTable.prefab.
    ///   2. Add a Collider (Box / Mesh) — required for the player's interaction raycast.
    ///   3. Add this CraftingTable component.
    ///   4. Drop the prefab into <see cref="InventoryUI"/> craftingTablePrefab slot.
    ///   No NetworkObject required — the table is spawned locally on every peer when
    ///   Inventory.HasCraftingTable flips true (state is networked via Inventory).
    /// </summary>
    public class CraftingTable : Interactable
    {
        [Header("Torch recipe (per night)")]
        [SerializeField] private int torchWoodCost = 5;
        [SerializeField] private int torchStoneCost = 1;

        [Header("Lord's Syrup recipe (1 per game per player)")]
        [SerializeField] private int syrupHerbCost = 4;
        [SerializeField] private int syrupWoodCost = 10;

        [Header("Stone Distractor recipe (persistent, throwable, reusable)")]
        [SerializeField] private int distractorStoneCost = 5;
        [SerializeField] private int distractorHerbCost = 2;

        [Header("Respawn Potion recipe (1 extra life per potion)")]
        [SerializeField] private int potionWoodCost = 20;
        [SerializeField] private int potionHerbCost = 10;

        [Header("Audio (optional)")]
        [Tooltip("Plays once when a recipe completes.")]
        [SerializeField] private AudioClip craftSuccessSfx;

        // Built-in code UI
        private GameObject panelGO;
        private CanvasGroup panelGroup;
        private TMP_Text torchBtnLabel, syrupBtnLabel, distractorBtnLabel, potionBtnLabel, statusLabel;
        private Button torchBtn, syrupBtn, distractorBtn, potionBtn, closeBtn;
        private bool isOpen;

        public override string Verb => "open the crafting table";

        public override bool CanInteract(GameObject interactor) => !isOpen;

        public override void Interact(GameObject interactor)
        {
            OpenPanel();
        }

        private void Start()
        {
            BuildPanel();
            ClosePanel();
            // Local player's inventory drives the affordability / "already crafted" labels.
            PlayerInventory.OnLocalChanged += RefreshButtons;
        }

        private void OnDestroy()
        {
            PlayerInventory.OnLocalChanged -= RefreshButtons;
            if (panelGO != null) Destroy(panelGO);
        }

        private void Update()
        {
            if (!isOpen) return;
            // Esc closes (same as inventory). Don't steal E — it'd re-open via interactor.
            if (Keyboard.current != null && Keyboard.current[Key.Escape].wasPressedThisFrame)
                ClosePanel();
        }

        // ────────────────────────────── UI ──────────────────────────────

        private void BuildPanel()
        {
            var canvasGO = new GameObject("CraftingTable_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(canvasGO);                       // survives scene migration
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            panelGO = new GameObject("CraftingPanel",
                typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            panelGO.transform.SetParent(canvasGO.transform, false);
            var rt = (RectTransform)panelGO.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(700f, 820f);
            rt.anchoredPosition = Vector2.zero;
            panelGO.GetComponent<Image>().color = new Color(0.04f, 0.04f, 0.05f, 0.96f);
            panelGroup = panelGO.GetComponent<CanvasGroup>();

            // Title
            UIHelpers.NewLabel(panelGO.transform, "CRAFTING TABLE", 44,
                new Vector2(0f, 360f), new Vector2(660f, 60f),
                new Color(0.95f, 0.85f, 0.55f), TextAlignmentOptions.Center, FontStyles.Bold);

            // ─── Row positions ─── (every row = name + desc + button)
            // Each row uses ~140px vertically: name at top, desc 28 below, button 50 below desc.
            BuildRecipeRow(panelGO.transform, "TORCH", "Re-craft each night. Flash distracts monster (T).",
                            new Color(0.95f, 0.72f, 0.42f), 270f, OnCraftTorch, out torchBtn, out torchBtnLabel);

            BuildRecipeRow(panelGO.transform, "LORD'S SYRUP", "Stun the monster 30s. ONE per player per game.",
                            new Color(0.65f, 0.32f, 0.85f), 110f, OnCraftSyrup, out syrupBtn, out syrupBtnLabel);

            BuildRecipeRow(panelGO.transform, "STONE DISTRACTOR", "Throw to lure monster 15s. Pick up & reuse.",
                            new Color(0.55f, 0.55f, 0.58f), -50f, OnCraftDistractor, out distractorBtn, out distractorBtnLabel);

            BuildRecipeRow(panelGO.transform, "RESPAWN POTION", "Auto-revives you ≥60m from monster on death.",
                            new Color(0.40f, 0.85f, 0.55f), -210f, OnCraftPotion, out potionBtn, out potionBtnLabel);

            // Status
            statusLabel = UIHelpers.NewLabel(panelGO.transform, "",
                20, new Vector2(0f, -310f), new Vector2(660f, 30f),
                new Color(0.92f, 0.6f, 0.5f), TextAlignmentOptions.Center);

            // Close
            closeBtn = UIHelpers.NewButton(panelGO.transform, "Close (Esc)", new Vector2(0f, -370f),
                new Vector2(240f, 44f), ClosePanel, out _);
        }

        /// <summary>One recipe row centred at y=<paramref name="y"/>: name label + description +
        /// craft button. Button text is filled in by <see cref="RefreshButtons"/>.</summary>
        private void BuildRecipeRow(Transform parent, string name, string desc, Color nameColor,
                                    float y, UnityEngine.Events.UnityAction onClick,
                                    out Button btn, out TMP_Text btnLabel)
        {
            UIHelpers.NewLabel(parent, name, 28, new Vector2(0f, y), new Vector2(640f, 36f),
                               nameColor, TextAlignmentOptions.Center, FontStyles.Bold);
            UIHelpers.NewLabel(parent, desc, 18, new Vector2(0f, y - 30f), new Vector2(640f, 28f),
                               new Color(0.78f, 0.78f, 0.78f), TextAlignmentOptions.Center);
            btn = UIHelpers.NewButton(parent, name, new Vector2(0f, y - 75f),
                                      new Vector2(420f, 48f), onClick, out btnLabel);
        }

        private void OpenPanel()
        {
            isOpen = true;
            panelGroup.alpha = 1f;
            panelGroup.interactable = true;
            panelGroup.blocksRaycasts = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            RefreshButtons();
        }

        private void ClosePanel()
        {
            isOpen = false;
            if (panelGroup != null)
            {
                panelGroup.alpha = 0f;
                panelGroup.interactable = false;
                panelGroup.blocksRaycasts = false;
            }
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (statusLabel != null) statusLabel.text = "";
        }

        private void RefreshButtons()
        {
            if (torchBtn == null) return;

            var inv = PlayerInventory.Local;
            if (inv == null)
            {
                torchBtn.interactable = false; syrupBtn.interactable = false;
                distractorBtn.interactable = false; potionBtn.interactable = false;
                torchBtnLabel.text = syrupBtnLabel.text = distractorBtnLabel.text = potionBtnLabel.text = "—";
                return;
            }

            // Torch — per-night. Re-craftable each day; consumed on use OR at dawn.
            bool torchCrafted    = inv.HasTorch;
            bool torchAffordable = inv.CanAfford(torchWoodCost, torchStoneCost, 0);
            torchBtn.interactable = !torchCrafted && torchAffordable;
            torchBtnLabel.text = torchCrafted
                ? "Already have one for tonight"
                : $"Craft Torch  ({torchWoodCost} W + {torchStoneCost} S)";

            // Lord's Syrup — one per game (used or owned blocks crafting another).
            bool syrupCrafted = inv.HasLordsSyrup;
            bool syrupUsed    = inv.LordsSyrupUsed;
            bool syrupAffordable = inv.CanAfford(syrupWoodCost, 0, syrupHerbCost);
            syrupBtn.interactable = !syrupCrafted && !syrupUsed && syrupAffordable;
            if (syrupUsed)              syrupBtnLabel.text = "Lord's Syrup Used ✓";
            else if (syrupCrafted)      syrupBtnLabel.text = "In Inventory — Use from [I]";
            else                        syrupBtnLabel.text = $"Craft Lord's Syrup  ({syrupWoodCost} W + {syrupHerbCost} H)";

            // Stone Distractor — stackable (no max). Reusable: throw → pick up → throw again.
            bool distAffordable = inv.CanAfford(0, distractorStoneCost, distractorHerbCost);
            distractorBtn.interactable = distAffordable;
            distractorBtnLabel.text = $"Craft Distractor  ({distractorStoneCost} S + {distractorHerbCost} H)" +
                                      (inv.StoneDistractors > 0 ? $"   Held: {inv.StoneDistractors}" : "");

            // Respawn Potion — stackable, but expensive. Each one = one revive.
            bool potionAffordable = inv.CanAfford(potionWoodCost, 0, potionHerbCost);
            potionBtn.interactable = potionAffordable;
            potionBtnLabel.text = $"Craft Potion  ({potionWoodCost} W + {potionHerbCost} H)" +
                                  (inv.RespawnPotions > 0 ? $"   Held: {inv.RespawnPotions}" : "");
        }

        private void OnCraftTorch()
        {
            var inv = PlayerInventory.Local;
            if (inv == null) { statusLabel.text = "No local player."; return; }
            if (!inv.TryCraftTorch(torchWoodCost, torchStoneCost))
            {
                statusLabel.text = inv.HasTorch ? "Already crafted." : "Not enough materials.";
                return;
            }
            statusLabel.text = "Torch crafted!";
            PlaySuccess();
        }

        private void OnCraftSyrup()
        {
            var inv = PlayerInventory.Local;
            if (inv == null) { statusLabel.text = "No local player."; return; }
            if (!inv.TryCraftLordsSyrup(syrupWoodCost, syrupHerbCost))
            {
                statusLabel.text = (inv.HasLordsSyrup || inv.LordsSyrupUsed)
                    ? "You already have / used one."
                    : "Not enough materials.";
                return;
            }
            statusLabel.text = "Lord's Syrup brewed! Use it from your inventory (I).";
            PlaySuccess();
        }

        private void OnCraftDistractor()
        {
            var inv = PlayerInventory.Local;
            if (inv == null) { statusLabel.text = "No local player."; return; }
            if (!inv.TryCraftStoneDistractor(distractorStoneCost, distractorHerbCost))
            {
                statusLabel.text = "Not enough materials.";
                return;
            }
            statusLabel.text = "Stone Distractor crafted! Throw it from your inventory (I).";
            PlaySuccess();
        }

        private void OnCraftPotion()
        {
            var inv = PlayerInventory.Local;
            if (inv == null) { statusLabel.text = "No local player."; return; }
            if (!inv.TryCraftRespawnPotion(potionWoodCost, potionHerbCost))
            {
                statusLabel.text = "Not enough materials.";
                return;
            }
            statusLabel.text = "Respawn Potion brewed! Auto-activates on death.";
            PlaySuccess();
        }

        private void PlaySuccess()
        {
            if (craftSuccessSfx != null && SunsetCurse.Audio.AudioManager.Instance != null)
                SunsetCurse.Audio.AudioManager.Instance.PlaySfx2D(craftSuccessSfx);
        }
    }
}
