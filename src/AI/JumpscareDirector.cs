using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using SunsetCurse.Core;
using SunsetCurse.Audio;

namespace SunsetCurse.AI
{
    /// <summary>
    /// The "boo" ghost. At night it makes a scary character appear SUDDENLY, up close, right in
    /// front of a player — screaming, with a sound sting — then vanish the instant they look away.
    /// It never walks, chases, or kills. Pure jumpscare.
    ///
    ///   • MULTIPLAYER: cycles through ALL alive players (a shuffled queue), so everyone gets
    ///     scared at least once per night before anyone repeats.
    ///   • SINGLE-PLAYER: the same (one) player, once each night.
    ///
    /// Host-authoritative: the server runs the timer + picks the target, then a targeted ClientRpc
    /// tells THAT player's machine to spawn the scare locally in front of their camera (the scare
    /// is personal — only the target sees it, so it's a plain local Instantiate, no NetworkObject).
    ///
    /// SETUP: one GameObject in the scene with a NetworkObject + this script. Assign Scary Prefab
    /// (your Zombie Scream character built by Tools ▸ Sunset Curse ▸ 20) and Scream Sfx
    /// (zombie_jumpscare). Needs an AudioManager in the scene.
    ///
    /// TESTING: press F9 (Debug Scare Key) in Play mode to scare yourself instantly — any time of
    /// day, no waiting for the night timer. Set the key to None before release.
    /// </summary>
    public class JumpscareDirector : NetworkBehaviour
    {
        [Header("The scare")]
        [Tooltip("Character prefab with an Animator that plays the scream on spawn (menu 20 builds it).")]
        [SerializeField] private GameObject scaryPrefab;
        [SerializeField] private AudioClip screamSfx;
        [Range(0f, 1f)] [SerializeField] private float screamVolume = 1f;

        [Header("Placement")]
        [Tooltip("How far in front of the player the character appears, in metres (open ground; " +
                 "a wall/tree inside this range pulls it in closer so it never spawns behind geometry).")]
        [SerializeField] private float spawnDistance = 8f;
        [Tooltip("Scaled so the character stands this tall, so it's always a good, visible size " +
                 "regardless of the model's import scale. (Players in this game are ~2.6m tall.)")]
        [SerializeField] private float targetHeight = 2.5f;
        [Tooltip("Character is destroyed once it leaves the view cone (player looked away). This is " +
                 "the cos of the half-angle — 0.4 ≈ a 66° half-cone.")]
        [SerializeField] private float lookAwayDot = 0.4f;
        [Tooltip("Hard cap: vanish after this long even if the player keeps staring.")]
        [SerializeField] private float maxVisibleSeconds = 2.5f;
        [Tooltip("Grace before the look-away check arms, so the scare doesn't pop out instantly.")]
        [SerializeField] private float minVisibleSeconds = 0.6f;
        [Tooltip("Extra rotation on top of face-the-player, for models whose authored forward/up " +
                 "isn't +Z/+Y: X = pitch, Y = yaw, Z = roll (degrees). If the creature faces " +
                 "down/away, tune this LIVE in Play mode (F9 to respawn) until it looks at you " +
                 "upright — then note the values, set them again after leaving Play mode " +
                 "(Play-mode edits revert!), and save the scene.")]
        [SerializeField] private Vector3 modelRotationOffset = Vector3.zero;

        [Header("Animation")]
        [Tooltip("ON: Animator disabled, authored pose held, moved by the procedural lunge — the " +
                 "mode that provably renders with the ORIGINAL mismatched scream clip. Turn OFF " +
                 "after rebuilding the prefab from a matched animation (Mixamo WITH-SKIN download " +
                 "+ menu 20): the clip then plays for real, and if it still fails to render, the " +
                 "0.3s self-heal snaps back to the statue automatically.")]
        [SerializeField] private bool statueMode = true;
        [Tooltip("How many metres the creature closes toward your face over the scare. 0 = static.")]
        [SerializeField] private float lungeDistance = 0.6f;
        [Tooltip("Random shiver amplitude in metres. 0 = off.")]
        [SerializeField] private float jitterAmount = 0f;

        [Header("Night visibility")]
        // WHY: the funny-fear model is authored PURE BLACK (its body texture is a solid-black PNG;
        // the source pack even ships 'shadeless-black.png' — it's meant to render UNLIT as a
        // silhouette against a lit background). URP/Lit shows albedo × light, and black albedo
        // reflects NOTHING — not even the flashlight — so at night it was a black model on a black
        // sky: perfectly placed (the log proved it) yet invisible. We restyle the spawned COPY
        // (instance materials only, the prefab/assets are untouched) into a pale apparition with
        // glowing red eyes, and add a brief light so it reads even with the flashlight off.
        [Tooltip("Repaint the spawned copy so it's visible in the dark (the model is authored solid black).")]
        [SerializeField] private bool spectralRestyle = true;
        [Tooltip("Skin/body colour of the apparition (pale, so lights actually illuminate it).")]
        [SerializeField] private Color ghostColor = new Color(0.72f, 0.78f, 0.88f);
        [Tooltip("Faint self-glow so full shadow can never swallow it (0 = only lit by lights).")]
        [SerializeField] private float ghostSelfGlow = 0.35f;
        [Tooltip("Eyes / mouth / teeth colour — these submeshes glow hot.")]
        [SerializeField] private Color hotColor = new Color(1f, 0.07f, 0.04f);
        [SerializeField] private float hotGlow = 3f;
        [Tooltip("A point light spawned with the scare so it's lit even if the player's flashlight " +
                 "is off. Dies with the character. 0 = no light.")]
        [SerializeField] private float scareLightIntensity = 6f;
        [SerializeField] private float scareLightRange = 8f;
        [SerializeField] private Color scareLightColor = new Color(0.75f, 0.82f, 1f);

        [Header("Timing (night only)")]
        [SerializeField] private float firstDelayMin = 25f;
        [SerializeField] private float firstDelayMax = 55f;
        [SerializeField] private float repeatDelayMin = 45f;
        [SerializeField] private float repeatDelayMax = 100f;

        [Header("Debugging")]
        [Tooltip("Play mode: press this key to scare YOURSELF instantly — any time of day, no " +
                 "waiting for the night timer. Works in the Editor and DEVELOPMENT builds only; " +
                 "release builds ignore it (Debug.isDebugBuild gate), so it can't ship as a cheat.")]
        [SerializeField] private Key debugScareKey = Key.F9;

        // Server-side scheduling.
        private readonly List<ulong> pending = new List<ulong>();   // alive clients not yet scared this cycle
        private float nextScareAt;
        private bool nightActive;

        public override void OnNetworkSpawn()
        {
            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart += HandleNight;
                GameClock.Instance.OnDayStart   += HandleDay;
                if (GameClock.Instance.CurrentPhase == GameClock.Phase.Night) HandleNight(GameClock.Instance.CurrentDay);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && GameClock.Instance != null)
            {
                GameClock.Instance.OnNightStart -= HandleNight;
                GameClock.Instance.OnDayStart   -= HandleDay;
            }
        }

        // RESTLESS DEAD omen: the jumpscares come far more often. 1 = normal cadence.
        private static float ScareDelayMult() => OmenManager.IsRestlessDead ? 0.35f : 1f;

        private void HandleNight(int day)
        {
            nightActive = true;
            RefillQueue();
            nextScareAt = Time.time + Random.Range(firstDelayMin, firstDelayMax) * ScareDelayMult();
        }

        private void HandleDay(int day) => nightActive = false;

        private void Update()
        {
            // Debug hotkey: instant local self-scare, day or night. The real scare fires 25–100s
            // into a night, which made every visibility fix cost a full playtest to check — this
            // turns each check into two seconds. Runs on any peer (the scare is local anyway).
            // Debug.isDebugBuild = true in the Editor + Development builds, FALSE in release —
            // so the key never ships. (Not an #if: conditionally-compiled serialized fields break
            // Unity's build serialization layout.)
            if (Debug.isDebugBuild && debugScareKey != Key.None && Keyboard.current != null &&
                Keyboard.current[debugScareKey].wasPressedThisFrame)
                SpawnScareLocal();

            if (!IsServer || !nightActive) return;
            if (Time.time < nextScareAt) return;

            if (pending.Count == 0) RefillQueue();        // everyone's been scared → new cycle
            if (pending.Count == 0) { nextScareAt = Time.time + 10f; return; }   // nobody alive

            ulong target = pending[0];
            pending.RemoveAt(0);
            nextScareAt = Time.time + Random.Range(repeatDelayMin, repeatDelayMax) * ScareDelayMult();

            if (!IsClientAliveStanding(target)) return;   // died/downed since queued → skip (they stay in a later cycle)

            if (!IsSpawned)                               // offline single-player
            {
                SpawnScareLocal();
            }
            else
            {
                ScareClientRpc(new ClientRpcParams
                { Send = new ClientRpcSendParams { TargetClientIds = new[] { target } } });
            }
        }

        /// <summary>Build the shuffled queue of every alive, standing player's clientId.</summary>
        private void RefillQueue()
        {
            pending.Clear();
            foreach (var inv in FindObjectsByType<PlayerInventory>())
                if (inv != null && inv.IsAlive && !inv.IsDowned) pending.Add(inv.OwnerClientId);
            // Fisher–Yates shuffle.
            for (int i = pending.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (pending[i], pending[j]) = (pending[j], pending[i]);
            }
        }

        private bool IsClientAliveStanding(ulong clientId)
        {
            foreach (var inv in FindObjectsByType<PlayerInventory>())
                if (inv != null && inv.OwnerClientId == clientId)
                    return inv.IsAlive && !inv.IsDowned;
            return false;
        }

        [ClientRpc]
        private void ScareClientRpc(ClientRpcParams _) => SpawnScareLocal();

        /// <summary>Runs on the TARGET's machine: pop the character into their face, scream, and
        /// watch for them to look away. Placement is deferred a couple of frames + uses the ACTUAL
        /// posed bounds — the model's BIND pose is flat (authored lying down), so measuring it on
        /// frame 0 dropped a 0.3m sliver on the floor below the crosshair (the "invisible monster").</summary>
        private void SpawnScareLocal()
        {
            var cam = Camera.main;
            if (cam == null) return;

            GameObject go = scaryPrefab != null
                ? Instantiate(scaryPrefab)
                : GameObject.CreatePrimitive(PrimitiveType.Capsule);   // last-ditch placeholder
            SunsetCurse.World.ModelArtifacts.Strip(go);   // kill any embedded camera that would hijack the view

            // Force skinned renderers to report the ACTUAL deformed bounds (default is the flat
            // bind-pose AABB) so the placement below reflects the screaming pose, not the T/lying pose.
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;

            if (statueMode)
            {
                // STATUE MODE (default). Three instrumented rounds proved the ORIGINAL scream
                // CLIP relocates the mesh in ways that defeat both root re-anchoring and a
                // LateUpdate bone-position freeze (isVisible stayed false until the Animator was
                // disabled — then it rendered perfectly). Animator OFF from frame one, skeleton
                // hard-frozen in the authored pose, moved by the procedural lunge instead.
                foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.enabled = false;
                go.AddComponent<FreezeBonePositions>().hardFreeze = true;
            }
            else
            {
                // ANIMATED MODE (untick Statue Mode after rebuilding the prefab from a matched
                // Mixamo WITH-SKIN export via menu 20). The clip plays for real; the soft freeze
                // still pins bone positions so stray translation keys can't relocate the mesh,
                // and the 0.3s self-heal in PlaceThenHaunt reverts to statue if it's culled.
                go.AddComponent<FreezeBonePositions>();
            }

            StartCoroutine(PlaceThenHaunt(go, cam));
        }

        private IEnumerator PlaceThenHaunt(GameObject go, Camera cam)
        {
            // Give the SkinnedMeshRenderer a frame or two to produce valid skinned bounds before
            // we measure/scale/place. (The Animator is disabled — statue mode — so the pose here
            // is the authored one, which is exactly what we want to measure.)
            yield return null;
            yield return null;
            if (go == null || cam == null) { if (go != null) Destroy(go); yield break; }
            var freeze = go.GetComponent<FreezeBonePositions>();   // added in SpawnScareLocal

            // GROUNDED placement: the creature stands ON THE TERRAIN `spawnDistance` ahead of
            // where the player faces (horizontally), feet planted — no more floating at eye
            // level. Trade-off (accepted by design): a player staring at the SKY may have it
            // below the frame edge until they level out; at 8m the angle down to it is small.
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();

            // Never behind geometry: clamp the distance with an eye-height ray along the same
            // flattened forward. DefaultRaycastLayers skips layer 2 (the invisible fence
            // barriers); Ignore skips triggers (berry bushes, pickups).
            float dist = spawnDistance;
            if (Physics.Raycast(cam.transform.position, fwd, out RaycastHit block, spawnDistance,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(1f, block.distance - 0.35f);

            // Upright, facing the player.
            go.transform.rotation = Quaternion.LookRotation(new Vector3(-fwd.x, 0f, -fwd.z))    // face the player
                                    * Quaternion.Euler(modelRotationOffset);                    // model-orientation fix

            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) { Debug.LogWarning("[JumpscareDirector] Spawned prefab has NO renderers — nothing to show."); Destroy(go); yield break; }

            // Belt-and-suspenders: force every renderer ON (a disabled SkinnedMeshRenderer would be
            // invisible), and make its materials render DOUBLE-SIDED so we still see the character
            // even if the model faces away (single-sided URP/Lit culls back faces → see-through).
            foreach (var r in rends)
            {
                r.enabled = true;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                foreach (var m in r.materials)   // .materials = per-instance copies, safe to edit
                {
                    if (m == null) continue;
                    if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // 0 = Off (two-sided)
                    if (spectralRestyle) RestyleMaterial(m);
                }
            }

            // Normalize by the LARGEST dimension (NOT height): if the scream animation didn't pose
            // the model it stays FLAT (~0.3m tall), and dividing by height blew the scale up ~6× —
            // you ended up standing inside a giant mesh seeing culled backfaces (= invisible).
            // Clamp the factor so a weird measurement can never produce an absurd size.
            Bounds b = Measure(rends);
            float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float scaleFactor = 1f;
            if (targetHeight > 0.01f && maxDim > 0.001f)
            {
                scaleFactor = Mathf.Clamp(targetHeight / maxDim, 0.02f, 8f);
                go.transform.localScale *= scaleFactor;
            }

            // Plant the FEET (bounds bottom) on the ground under the spot `dist` ahead. Fallback:
            // eye level, if there's somehow no ground below (shouldn't happen on this map).
            b = Measure(rends);
            Vector3 spot = cam.transform.position + fwd * dist;
            Vector3 anchorCenter;
            if (Physics.Raycast(spot + Vector3.up * 0.3f, Vector3.down, out RaycastHit ground, 40f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                anchorCenter = new Vector3(spot.x, ground.point.y + b.extents.y + 0.02f, spot.z);
            else
                anchorCenter = spot;
            go.transform.position += anchorCenter - b.center;

            // Diagnostics: tells us exactly what happened (paste this from the Console if it's still
            // wrong). Flat size.y ≈ size of one of the others → the scream animation isn't posing it.
            Debug.Log($"[JumpscareDirector] Scare spawned: renderers={rends.Length}, " +
                      $"boundsSize=({b.size.x:0.00},{b.size.y:0.00},{b.size.z:0.00}), scale×{scaleFactor:0.00}, " +
                      $"grounded at {anchorCenter} (cam {cam.transform.position}, dist {dist:0.00}m). " +
                      $"firstShader='{FirstShaderName(rends)}', spectralRestyle={spectralRestyle}.");

            // Light the scare from the player's side so the pale restyle actually catches light
            // even when the flashlight is off. Parented to the character → destroyed with it.
            // (Must happen AFTER ModelArtifacts.Strip, which deletes lights — it already did.)
            if (scareLightIntensity > 0f)
            {
                var lightGo = new GameObject("ScareLight");
                lightGo.transform.SetParent(go.transform, true);
                lightGo.transform.position = anchorCenter - fwd * (dist * 0.5f) + Vector3.up * 0.5f;   // between player and creature
                var li = lightGo.AddComponent<Light>();
                li.type = LightType.Point;
                li.color = scareLightColor;
                li.intensity = scareLightIntensity;
                li.range = scareLightRange;
                li.shadows = LightShadows.None;
            }

            if (screamSfx != null && AudioManager.Instance != null)
                AudioManager.Instance.PlaySfx3D(screamSfx, anchorCenter, screamVolume, 30f);

            // Haunt until the player looks away (or the hard cap), watching the visible centre.
            float t = 0f;
            bool probed = false;
            while (go != null)
            {
                t += Time.deltaTime;

                // RE-ANCHOR EVERY FRAME onto the motion target: the grounded spot + a slow lunge
                // toward the player + a tiny random shiver — then re-plant the feet on whatever
                // terrain is under the (possibly lunged) spot, so it stays GROUNDED the whole
                // time. The bounds-based anchor also cancels anything that tries to relocate the
                // mesh (the original mismatched scream clip did exactly that).
                Vector3 target = anchorCenter;
                if (lungeDistance > 0f && cam != null)
                {
                    Vector3 toCam = cam.transform.position - anchorCenter;
                    toCam.y = 0f;                       // close in along the ground
                    if (toCam.sqrMagnitude > 0.01f)
                        target += toCam.normalized * Mathf.SmoothStep(0f, lungeDistance, t / maxVisibleSeconds);
                }
                if (jitterAmount > 0f) target += Random.insideUnitSphere * jitterAmount;

                if (rends[0] != null)
                {
                    Bounds cur = Measure(rends);
                    // Terrain-follow: feet stay planted even as the pose height changes or the
                    // lunge crosses uneven ground (ray starts just above the creature's head).
                    if (Physics.Raycast(new Vector3(target.x, cur.center.y + cur.extents.y + 0.3f, target.z),
                                        Vector3.down, out RaycastHit under, 15f,
                                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        target.y = under.point.y + cur.extents.y + 0.02f;
                    Vector3 drift = target - cur.center;
                    if (drift.sqrMagnitude > 0.0000001f) go.transform.position += drift;
                }
                // One-shot probe + SELF-HEAL. isVisible comes from the engine's own culling, so
                // it settles "did Unity actually draw it". If it didn't, take the Animator out of
                // the equation entirely: the prefab view proves this mesh renders fine when
                // nothing animates it, so a statue-frozen skeleton is guaranteed to show.
                if (!probed && t >= 0.3f)
                {
                    probed = true;
                    bool vis = rends[0] != null && rends[0].isVisible;
                    Debug.Log($"[JumpscareDirector] Probe@0.3s: isVisible={vis}, " +
                              $"boundsCenter={(rends[0] != null ? rends[0].bounds.center.ToString() : "?")}.");
                    if (!vis)
                    {
                        foreach (var a in go.GetComponentsInChildren<Animator>(true)) a.enabled = false;
                        if (freeze != null) freeze.hardFreeze = true;   // rotations too → authored statue pose
                        yield return null;                              // one LateUpdate to settle the pose
                        if (go == null) break;
                        go.transform.position += anchorCenter - Measure(rends).center;
                        Debug.Log("[JumpscareDirector] Self-heal engaged: Animator OFF, skeleton statue-frozen " +
                                  "at the authored pose, re-centred in view. If the creature shows now, the " +
                                  "scream CLIP was still relocating it even past the position freeze.");
                    }
                }
                if (t >= maxVisibleSeconds) break;
                if (t >= minVisibleSeconds && cam != null)
                {
                    Vector3 toChar = (anchorCenter - cam.transform.position).normalized;
                    if (Vector3.Dot(cam.transform.forward, toChar) < lookAwayDot) break;
                }
                yield return null;
            }
            if (go != null) Destroy(go);
        }

        /// <summary>Repaint one instance material so the authored-black creature reads in the dark.
        /// Eyes/mouth/teeth go hot red; everything else becomes a pale, faintly self-lit apparition.
        /// The black base TEXTURE must be removed (not just tinted) — a black map multiplies any
        /// tint back to zero. Normal maps are kept, so the flashlight still reveals face/body detail.</summary>
        private void RestyleMaterial(Material m)
        {
            string n = m.name.ToLowerInvariant();
            bool hot = n.Contains("eye") || n.Contains("mouth") || n.Contains("teeth");
            Color baseCol = hot ? hotColor : ghostColor;
            Color glowCol = hot ? hotColor * hotGlow : ghostColor * ghostSelfGlow;

            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseCol);
            if (m.HasProperty("_Color"))     m.SetColor("_Color", baseCol);

            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionMap"))   m.SetTexture("_EmissionMap", null);
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", glowCol);
        }

        /// <summary>Runtime-added to the spawned scare (never saved into a scene/prefab, so the
        /// one-class-per-file rule's build-crash trap doesn't apply — see session 14b).
        ///
        /// WHY: the scream clip carries ONE baked position curve (Inspector: "Curves Pos: 1,
        /// Quat: 60") whose streamed values belong to the animation FBX's own scene layout — it
        /// flings the skeleton tens of metres every frame. And Unity's frame order is
        /// Update → coroutines → ANIMATOR WRITES POSE → LateUpdate → render, so any correction
        /// made from Update/coroutines is overwritten by the clip before the frame is drawn
        /// (probe: measured bounds sat dead-centre, isVisible=False). LateUpdate runs AFTER the
        /// Animator, so what we write here is what actually renders: every bone's localPosition
        /// snaps back to its prefab-authored value, while the 60 rotation curves — the actual
        /// scream — play untouched.</summary>
        private class FreezeBonePositions : MonoBehaviour
        {
            private Transform[] bones;
            private Vector3[] authoredPos;
            private Quaternion[] authoredRot;

            /// <summary>Self-heal mode: restore ROTATIONS too, turning the character into a
            /// statue in its authored pose — used with the Animator disabled, so nothing can
            /// relocate or repose the mesh at all.</summary>
            public bool hardFreeze;

            private void Awake()
            {
                // Capture at instantiate time, before the Animator's first write.
                bones = GetComponentsInChildren<Transform>(true);
                authoredPos = new Vector3[bones.Length];
                authoredRot = new Quaternion[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    authoredPos[i] = bones[i].localPosition;
                    authoredRot[i] = bones[i].localRotation;
                }
            }

            private void LateUpdate()
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null || bones[i] == transform) continue;   // never touch the root — placement owns it
                    bones[i].localPosition = authoredPos[i];
                    if (hardFreeze) bones[i].localRotation = authoredRot[i];
                }
            }
        }

        private static Bounds Measure(Renderer[] rends)
        {
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        private static string FirstShaderName(Renderer[] rends)
        {
            foreach (var r in rends)
                if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null)
                    return r.sharedMaterial.shader.name;
            return "(none)";
        }
    }
}
