using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using SunsetCurse.World;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Lets the player interact with the world. Each frame it raycasts forward from the
    /// camera; if it's looking at an <see cref="Interactable"/> within range, it shows a
    /// "Press E to ..." prompt and triggers it when E is pressed.
    ///
    /// Uses the new Input System (Keyboard.current). Builds its own little prompt label in
    /// code, so there's nothing to wire up.
    ///
    /// SETUP: put this on the player (PlayerCapsule). It uses Camera.main by default.
    /// </summary>
    public class PlayerInteractor : MonoBehaviour
    {
        [Header("Aiming")]
        [Tooltip("Camera to aim from. Defaults to Camera.main (your first-person camera).")]
        [SerializeField] private Camera aimCamera;
        [Tooltip("How far the player can reach to interact, in metres.")]
        [SerializeField] private float range = 3.5f;
        [Tooltip("Aim-assist radius. After a precise ray misses, a sphere of this radius is swept " +
                 "along the view so you only have to point the crosshair NEAR a small pickup " +
                 "(herb / ritual potion) instead of dead-on. 0 = precise ray only.")]
        [SerializeField] private float aimAssistRadius = 0.4f;
        [Tooltip("Which layers the interaction ray can hit. Leave as Everything if unsure.")]
        [SerializeField] private LayerMask hitMask = ~0;
        [Tooltip("How far PAST a thin blocking collider the ray may still find an interactable, " +
                 "in metres. This keeps prompts working when a non-interactable shell sits flush " +
                 "against a door/pickup (e.g. the tower gate's FRAME collider overlapping the gate " +
                 "door) — without letting you interact through actual walls.")]
        [SerializeField] private float blockerTolerance = 0.6f;

        [Header("Input")]
        [SerializeField] private Key interactKey = Key.E;

        private Interactable current;
        private Text promptText;
        private Text crosshair;
        private PlayerStats stats;   // to disable interaction while downed

        // ---- Hold-to-interact (harvesting) ----
        // Some interactables (ResourceNode: trees / stones / herbs) need E HELD for a few
        // seconds instead of a single press. While channeling, a circle fills at the centre
        // of the screen with a label underneath ("Chopping Tree, Hold E").
        private Interactable holdTarget;   // what we're currently channeling on (null = not holding)
        private float holdTimer;
        private GameObject holdRoot;       // circle + label container (hidden unless channeling)
        private Image holdFill;            // the radial progress ring
        private Text holdLabel;

        private void Start()
        {
            stats = GetComponent<PlayerStats>();
            if (aimCamera == null) aimCamera = Camera.main;
            if (aimCamera == null)
                Debug.LogWarning("[PlayerInteractor] No camera found (tag your camera 'MainCamera').", this);

            BuildPrompt();
        }

        private void Update()
        {
            // No interacting while downed — you're bleeding out, not shopping for berries.
            if (stats != null && stats.IsDowned)
            {
                CancelHold();
                current = null;
                UpdatePrompt();
                return;
            }

            current = FindTarget();

            bool ePressed = Keyboard.current != null && Keyboard.current[interactKey].wasPressedThisFrame;
            bool eHeld    = Keyboard.current != null && Keyboard.current[interactKey].isPressed;

            // NOTE: ReferenceEquals, not a plain null-check. If the node was DESTROYED mid-channel
            // (a teammate harvested it first in MP), Unity's overloaded == already calls it "null"
            // and a normal check would skip this block — leaving the circle stuck on screen.
            // ReferenceEquals still sees the stale reference, so we enter and clean up below.
            if (!ReferenceEquals(holdTarget, null))
            {
                // Channeling. Cancel if E was released, the node vanished (holdTarget == null uses
                // Unity's overload → true once destroyed), or the crosshair drifted off it.
                // Distance is implicitly enforced too: walk out of `range` and FindTarget stops
                // returning the node, so `current != holdTarget` cancels the channel.
                if (!eHeld || holdTarget == null || current != holdTarget)
                {
                    CancelHold();
                }
                else
                {
                    holdTimer += Time.deltaTime;
                    float need = Mathf.Max(0.01f, holdTarget.HoldSeconds);
                    if (holdFill != null) holdFill.fillAmount = Mathf.Clamp01(holdTimer / need);

                    if (holdTimer >= need)
                    {
                        var done = holdTarget;
                        CancelHold();
                        done.Interact(gameObject);
                        // Refresh so the prompt reflects the world post-harvest (node despawns).
                        current = FindTarget();
                    }
                }
            }
            else if (current != null && ePressed)
            {
                if (current.HoldSeconds > 0f)
                {
                    BeginHold(current);   // harvest channel — circle starts filling
                }
                else
                {
                    current.Interact(gameObject);
                    // Refresh prompt immediately (e.g. bush becomes bare → prompt disappears).
                    current = FindTarget();
                }
            }

            UpdatePrompt();
        }

        private void BeginHold(Interactable target)
        {
            holdTarget = target;
            holdTimer = 0f;
            if (holdRoot != null)
            {
                holdRoot.SetActive(true);
                if (holdFill != null) holdFill.fillAmount = 0f;
                if (holdLabel != null) holdLabel.text = target.HoldPrompt;
            }
        }

        private void CancelHold()
        {
            holdTarget = null;
            holdTimer = 0f;
            if (holdRoot != null) holdRoot.SetActive(false);
        }

        private Interactable FindTarget()
        {
            if (aimCamera == null) return null;

            Ray ray = new Ray(aimCamera.transform.position, aimCamera.transform.forward);

            // Exclude layer 2 ("Ignore Raycast"). The invisible anti-jump FENCE BARRIERS live on
            // that layer specifically so rays skip them — but a ~0 hitMask includes layer 2, so the
            // solid barrier in front of the tower gate (and near any fence) was blocking the
            // interaction ray and eating the "Press E" prompt. Masking it out fixes that.
            int mask = hitMask.value & ~(1 << 2);

            // 1) Precise ray — best when you're looking dead-on. Collide (not Ignore) so we still
            //    detect foliage that FoliagePassThrough turned into a trigger (berry bushes / herbs).
            //    We take ALL hits along the ray and walk them nearest-first, accepting the first
            //    Interactable found within `blockerTolerance` past the first solid blocker — so a
            //    thin overlapping shell (a gate frame, dense grass) can't eat the door's prompt,
            //    but anything genuinely behind a wall stays unreachable.
            var hits = Physics.RaycastAll(ray, range, mask, QueryTriggerInteraction.Collide);
            if (hits.Length > 0)
            {
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                float blockedAt = float.MaxValue;   // distance of the first solid non-interactable hit
                foreach (var h in hits)
                {
                    if (h.distance > blockedAt + blockerTolerance) break;   // behind a real wall now
                    var i = Resolve(h);
                    if (i != null) return i;
                    if (!h.collider.isTrigger && h.collider.transform.root != transform.root)
                        blockedAt = Mathf.Min(blockedAt, h.distance);
                }
            }

            // 2) Aim-assist sweep — a fat sphere down the view catches small props (herbs, the ritual
            //    potion) the thin ray narrowly missed, so pointing the crosshair NEAR them is enough.
            if (aimAssistRadius > 0f &&
                Physics.SphereCast(ray, aimAssistRadius, out RaycastHit sHit, range, mask, QueryTriggerInteraction.Collide))
            {
                return Resolve(sHit);
            }

            return null;
        }

        // Shared validation: not our own body, has an Interactable, and it currently accepts input.
        private Interactable Resolve(RaycastHit hit)
        {
            if (hit.collider == null) return null;
            if (hit.collider.transform.root == transform.root) return null;   // don't target ourselves
            var interactable = hit.collider.GetComponentInParent<Interactable>();
            if (interactable == null || !interactable.CanInteract(gameObject)) return null;
            return interactable;
        }

        private void UpdatePrompt()
        {
            bool channeling = !ReferenceEquals(holdTarget, null);

            if (promptText != null)
            {
                if (channeling || current == null)
                    promptText.text = string.Empty;   // the hold circle + label carry the info
                else if (current.HoldSeconds > 0f)
                    promptText.text = $"Hold {interactKey} to {current.Verb}";
                else
                    promptText.text = $"Press {interactKey} to {current.Verb}";
            }

            // Crosshair brightens to gold when it's over something you can interact with.
            if (crosshair != null)
                crosshair.color = current != null
                    ? new Color(1f, 0.85f, 0.35f, 0.95f)
                    : new Color(1f, 1f, 1f, 0.6f);
        }

        // ------------------------------------------------------------- prompt UI (in code)

        private void BuildPrompt()
        {
            var canvasGO = new GameObject("InteractPrompt_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Crosshair (+) dead-centre so the player can point precisely at pickups.
            var crossGO = new GameObject("Crosshair", typeof(RectTransform));
            crossGO.transform.SetParent(canvasGO.transform, false);
            var crt = (RectTransform)crossGO.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.pivot = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(48f, 48f);
            crosshair = crossGO.AddComponent<Text>();
            crosshair.font = GetDefaultFont();
            crosshair.alignment = TextAnchor.MiddleCenter;
            crosshair.fontSize = 36;
            crosshair.color = new Color(1f, 1f, 1f, 0.6f);
            crosshair.raycastTarget = false;
            crosshair.text = "+";

            var textGO = new GameObject("PromptText", typeof(RectTransform));
            textGO.transform.SetParent(canvasGO.transform, false);
            var rt = (RectTransform)textGO.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, -70f);   // just below the screen centre
            rt.sizeDelta = new Vector2(700f, 60f);

            promptText = textGO.AddComponent<Text>();
            promptText.font = GetDefaultFont();
            promptText.alignment = TextAnchor.MiddleCenter;
            promptText.fontSize = 28;
            promptText.color = Color.white;
            promptText.raycastTarget = false;
            promptText.text = string.Empty;

            var outline = textGO.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            BuildHoldUI(canvasGO.transform);
        }

        // Circle-with-label shown while the player HOLDS E on a harvestable (tree/stone/herb).
        // Built entirely in code like the rest of the prompt UI — nothing to wire in the editor.
        private void BuildHoldUI(Transform canvas)
        {
            holdRoot = new GameObject("HoldProgress", typeof(RectTransform));
            holdRoot.transform.SetParent(canvas, false);
            var root = (RectTransform)holdRoot.transform;
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;                 // dead centre, around the crosshair
            root.sizeDelta = new Vector2(110f, 110f);

            // Faint full ring so the player can see how much of the circle is left to fill.
            var backGO = new GameObject("RingBack", typeof(RectTransform));
            backGO.transform.SetParent(holdRoot.transform, false);
            Stretch((RectTransform)backGO.transform);
            var back = backGO.AddComponent<Image>();
            back.sprite = SunsetCurse.UI.UIHelpers.GetRingSprite();
            back.color = new Color(1f, 1f, 1f, 0.18f);
            back.raycastTarget = false;

            // The filling ring: Image "Filled / Radial 360" driven by holdTimer each frame.
            var fillGO = new GameObject("RingFill", typeof(RectTransform));
            fillGO.transform.SetParent(holdRoot.transform, false);
            Stretch((RectTransform)fillGO.transform);
            holdFill = fillGO.AddComponent<Image>();
            holdFill.sprite = SunsetCurse.UI.UIHelpers.GetRingSprite();
            holdFill.color = new Color(1f, 0.85f, 0.35f, 0.95f);  // same gold as the hot crosshair
            holdFill.raycastTarget = false;
            holdFill.type = Image.Type.Filled;
            holdFill.fillMethod = Image.FillMethod.Radial360;
            holdFill.fillOrigin = (int)Image.Origin360.Top;       // fills clockwise from 12 o'clock
            holdFill.fillClockwise = true;
            holdFill.fillAmount = 0f;

            // Label under the circle: "Chopping Tree, Hold E" etc.
            var labelGO = new GameObject("HoldLabel", typeof(RectTransform));
            labelGO.transform.SetParent(holdRoot.transform, false);
            var lrt = (RectTransform)labelGO.transform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0f); // bottom of the circle
            lrt.pivot = new Vector2(0.5f, 1f);
            lrt.anchoredPosition = new Vector2(0f, -14f);
            lrt.sizeDelta = new Vector2(700f, 40f);
            holdLabel = labelGO.AddComponent<Text>();
            holdLabel.font = GetDefaultFont();
            holdLabel.alignment = TextAnchor.MiddleCenter;
            holdLabel.fontSize = 26;
            holdLabel.color = Color.white;
            holdLabel.raycastTarget = false;
            var labelOutline = labelGO.AddComponent<Outline>();
            labelOutline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            labelOutline.effectDistance = new Vector2(1.5f, -1.5f);

            holdRoot.SetActive(false);   // only visible while actually channeling
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static Font GetDefaultFont()
        {
            // Unity 6 ships "LegacyRuntime.ttf"; older versions used "Arial.ttf".
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }
    }
}
