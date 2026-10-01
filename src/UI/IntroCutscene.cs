using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using TMPro;

namespace SunsetCurse.UI
{
    /// <summary>
    /// The opening cinematic — a code-built, full-screen "motion comic" slideshow that plays once
    /// when the gameplay scene loads, BEFORE the player gets control:
    ///
    ///     camping in the forest → strange noises → they look at each other →
    ///     a scream + the screen flares RED → blackout → the world fades in (SPAWN).
    ///
    /// It's a sequence of BEATS. Each beat can show a full-screen image (with a slow Ken-Burns
    /// zoom), a caption, play a one-shot sound, tint the screen a colour, and/or flash. It works
    /// with ZERO art out of the box (captions + colour washes + the red flash), and you upgrade it
    /// by dropping your imported illustrations + audio clips into the beat slots in the Inspector.
    ///
    /// While it plays it covers the screen and freezes the local player; when it finishes it fades
    /// away to reveal gameplay. Press any key to skip.
    ///
    /// SETUP: Tools ▸ Sunset Curse ▸ 14 creates the object for you (or add this component to any
    /// GameObject in the gameplay scene). Then assign each beat's Image + Sound to taste.
    /// </summary>
    public class IntroCutscene : MonoBehaviour
    {
        [System.Serializable]
        public class Beat
        {
            [Tooltip("Full-screen illustration for this beat. Leave empty to show just the colour wash + caption.")]
            public Sprite image;
            [TextArea(1, 3)]
            [Tooltip("Caption shown near the bottom. Leave empty for no text.")]
            public string caption;
            [Tooltip("Seconds this beat stays on screen (excluding the cross-fade into it).")]
            public float duration = 3.5f;
            [Tooltip("One-shot sound played when this beat begins (branch snap, scream, sting...).")]
            public AudioClip sound;
            [Range(0f, 1f)] public float soundVolume = 1f;
            [Tooltip("Screen tint laid OVER the image for this beat. Alpha 0 = none. Use a red with " +
                     "high alpha for the scare, solid black for the blackout.")]
            public Color wash = new Color(0f, 0f, 0f, 0f);
            [Tooltip("If on, this beat SNAPS in (no cross-fade) and punches the wash to full instantly " +
                     "— use it for the scream/red-flash beat so it hits hard.")]
            public bool flash = false;
        }

        [Header("Playback")]
        [Tooltip("Play automatically when this scene starts. Turn OFF to disable the intro (e.g. while testing).")]
        [SerializeField] private bool playOnStart = true;
        [Tooltip("Cross-fade time between beats, in seconds.")]
        [SerializeField] private float crossFade = 0.8f;
        [Tooltip("How long the final reveal (overlay fading away to show the game) takes.")]
        [SerializeField] private float revealDuration = 1.5f;
        [Tooltip("Slow zoom applied to each image over the beat (1 = none, 1.12 = a gentle push-in).")]
        [SerializeField] private float kenBurnsZoom = 1.10f;

        [Header("Audio (optional)")]
        [Tooltip("Ambient bed looped under the WHOLE intro (crackling campfire + night forest). Fades out at the end.")]
        [SerializeField] private AudioClip ambientLoop;
        [Range(0f, 1f)] [SerializeField] private float ambientVolume = 0.6f;

        [Header("The beats (pre-filled with the story — add your images + sounds)")]
        [SerializeField] private Beat[] beats = DefaultBeats();

        // Runtime UI.
        private Canvas canvas;
        private CanvasGroup group;
        private Image imageA;       // current image
        private Image washOverlay;  // colour tint on top of the image
        private Image blackout;     // full black used for fades between beats + the reveal
        private TMP_Text caption;
        private TMP_Text skipHint;
        private AudioSource audioSrc;
        private bool skipped;
        private bool running;

        private void Start()
        {
            // Only in the actual gameplay scene — never the menu.
            if (!playOnStart) return;
            if (SceneManager.GetActiveScene().name == "MainMenu") return;
            Build();
            StartCoroutine(Run());
        }

        // ───────────────────────────── the sequence ─────────────────────────────

        private IEnumerator Run()
        {
            running = true;
            FreezePlayer(true);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            if (ambientLoop != null)
            {
                audioSrc.clip = ambientLoop;
                audioSrc.loop = true;
                audioSrc.volume = ambientVolume;
                audioSrc.Play();
            }

            // Start fully black, then play each beat.
            blackout.color = Color.black;

            for (int i = 0; i < beats.Length && !skipped; i++)
                yield return PlayBeat(beats[i]);

            yield return Reveal();

            FreezePlayer(false);
            running = false;
            Destroy(canvas.gameObject);
            Destroy(gameObject);
        }

        private IEnumerator PlayBeat(Beat b)
        {
            // Re-assert the freeze — the local player is spawned by NGO and may only appear a beat
            // or two into the intro, so freezing once at the start can miss it.
            FreezePlayer(true);

            // Swap in the image + caption for this beat while the screen is covered (black or the
            // previous wash), so the change isn't seen mid-fade.
            SetImage(b.image);
            caption.text = b.caption ?? string.Empty;
            washOverlay.color = new Color(b.wash.r, b.wash.g, b.wash.b, 0f);

            if (b.sound != null) audioSrc.PlayOneShot(b.sound, b.soundVolume);

            if (b.flash)
            {
                // Hard cut: reveal instantly and punch the wash to full (the scream / red flare).
                blackout.color = new Color(0, 0, 0, 0);
                washOverlay.color = b.wash;
            }
            else
            {
                // Cross-fade in: lift the black, and ease the wash to its target.
                yield return Fade(t =>
                {
                    blackout.color = new Color(0, 0, 0, 1f - t);
                    washOverlay.color = new Color(b.wash.r, b.wash.g, b.wash.b, b.wash.a * t);
                }, crossFade);
            }

            // Hold — with a slow Ken-Burns zoom on the image — for the beat's duration.
            float held = 0f;
            while (held < b.duration && !skipped)
            {
                held += Time.unscaledDeltaTime;
                if (imageA.sprite != null && kenBurnsZoom > 1f)
                {
                    float k = Mathf.Clamp01(held / Mathf.Max(0.01f, b.duration));
                    float s = Mathf.Lerp(1f, kenBurnsZoom, k);
                    imageA.rectTransform.localScale = new Vector3(s, s, 1f);
                }
                if (SkipPressed()) skipped = true;
                yield return null;
            }

            // Fade this beat down to black before the next one (unless we're skipping out).
            if (!skipped)
                yield return Fade(t =>
                {
                    blackout.color = new Color(0, 0, 0, t);
                }, crossFade * 0.7f);
        }

        private IEnumerator Reveal()
        {
            // From wherever we are (usually black), fade the whole overlay away to show the game.
            blackout.color = new Color(0, 0, 0, 1f);
            caption.text = string.Empty;
            if (skipHint != null) skipHint.text = string.Empty;

            float t = 0f;
            while (t < revealDuration)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / revealDuration);
                group.alpha = 1f - k;
                if (audioSrc.isPlaying && audioSrc.loop) audioSrc.volume = ambientVolume * (1f - k);
                yield return null;
            }
            group.alpha = 0f;
            audioSrc.Stop();
        }

        private IEnumerator Fade(System.Action<float> apply, float dur)
        {
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / dur));
                apply(k);
                if (SkipPressed()) { skipped = true; apply(1f); yield break; }
                yield return null;
            }
            apply(1f);
        }

        private void SetImage(Sprite s)
        {
            imageA.sprite = s;
            imageA.enabled = s != null;
            imageA.rectTransform.localScale = Vector3.one;   // reset the Ken-Burns zoom
        }

        private bool SkipPressed()
        {
            var kb = Keyboard.current;
            return kb != null && kb.anyKey.wasPressedThisFrame;
        }

        // ───────────────────────────── player freeze ─────────────────────────────

        // Best-effort: stop the LOCAL player from moving / looking / interacting while the intro
        // covers the screen. The player object is spawned by NGO so it may not exist for a frame
        // or two — that's fine, the opaque overlay covers the gap.
        private void FreezePlayer(bool freeze)
        {
            var local = SunsetCurse.Core.PlayerInventory.Local;
            if (local == null) return;
            SetComponentEnabled(local.gameObject, "FirstPersonController", !freeze);
            SetComponentEnabled(local.gameObject, "PlayerInteractor", !freeze);
        }

        private static void SetComponentEnabled(GameObject root, string typeName, bool enabled)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name == typeName) mb.enabled = enabled;
        }

        // ───────────────────────────── UI construction ─────────────────────────────

        private void Build()
        {
            EnsureEventSystem();

            canvas = UIHelpers.NewCanvas("IntroCutscene_Canvas", 500, transform);   // above everything
            group = canvas.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = false;
            group.blocksRaycasts = true;   // swallow clicks under the overlay

            // Base black (so there's never a transparent gap).
            UIHelpers.NewPanel(canvas.transform, "Base", Vector2.zero, new Vector2(4000f, 4000f), Color.black);

            // The full-screen image (stretched, cover). Disabled when a beat has no image.
            var imgGO = new GameObject("Image", typeof(RectTransform), typeof(Image));
            imgGO.transform.SetParent(canvas.transform, false);
            var irt = (RectTransform)imgGO.transform;
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
            irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
            imageA = imgGO.GetComponent<Image>();
            imageA.preserveAspect = false;   // fill the screen (cover)
            imageA.raycastTarget = false;
            imageA.enabled = false;

            // Colour wash over the image (per-beat tint + the red flare).
            washOverlay = UIHelpers.NewPanel(canvas.transform, "Wash", Vector2.zero,
                                             new Vector2(4000f, 4000f), new Color(0, 0, 0, 0));
            washOverlay.raycastTarget = false;

            // Caption near the bottom.
            caption = UIHelpers.NewLabel(canvas.transform, "", 40, new Vector2(0f, -360f),
                                         new Vector2(1500f, 200f), new Color(0.92f, 0.9f, 0.86f),
                                         TextAlignmentOptions.Center, FontStyles.Italic);
            var capOutline = caption.gameObject.AddComponent<UnityEngine.UI.Outline>();
            capOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            capOutline.effectDistance = new Vector2(2f, -2f);

            // Blackout used for the fades between beats + the reveal (on top of everything else).
            blackout = UIHelpers.NewPanel(canvas.transform, "Blackout", Vector2.zero,
                                          new Vector2(4000f, 4000f), Color.black);
            blackout.raycastTarget = false;

            // Skip hint, corner.
            skipHint = UIHelpers.NewLabel(canvas.transform, "Press any key to skip", 24,
                                          new Vector2(640f, -470f), new Vector2(600f, 40f),
                                          new Color(0.75f, 0.72f, 0.7f, 0.8f), TextAlignmentOptions.Right);

            audioSrc = gameObject.AddComponent<AudioSource>();
            audioSrc.playOnAwake = false;
            audioSrc.spatialBlend = 0f;   // 2D
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            go.AddComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }

        // ───────────────────────────── default story ─────────────────────────────

        /// <summary>The opening story, pre-written so the intro works with zero art. Drop your
        /// illustrations + sounds onto these beats in the Inspector to bring it to life.</summary>
        private static Beat[] DefaultBeats()
        {
            return new[]
            {
                new Beat {
                    caption = "The fire was warm. For a little while, the forest let us forget.",
                    duration = 4.5f, wash = new Color(0.10f, 0.06f, 0.02f, 0.25f),
                },
                new Beat {
                    caption = "Then — a sound between the trees. Wrong. Too close.",
                    duration = 3.5f, wash = new Color(0.02f, 0.03f, 0.06f, 0.35f),
                },
                new Beat {
                    caption = "We looked at each other. Nobody breathed.",
                    duration = 3.5f, wash = new Color(0.02f, 0.02f, 0.04f, 0.45f),
                },
                new Beat {
                    caption = "Then it came for us.",
                    duration = 1.6f, flash = true, wash = new Color(0.55f, 0.02f, 0.02f, 0.92f),
                },
                new Beat {
                    caption = "",
                    duration = 1.6f, wash = new Color(0f, 0f, 0f, 1f),
                },
            };
        }
    }
}
