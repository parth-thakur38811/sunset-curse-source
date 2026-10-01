using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// The shared, networked heart of the RADIO TOWER ESCAPE. One per scene (+ NetworkObject).
    ///
    /// THE ESCAPE, START TO FINISH (the two objectives can be completed in EITHER ORDER):
    ///   A. Collect all ritual potions + perform the ritual at the altar (lifts the curse).
    ///   B. Fix the radio tower — attach the battery + transformer at the TOP (climb with M,
    ///      attach with H) → "Press R to send SOS signal!" — then the krotkofal carrier presses R.
    ///   The SOS can be sent as soon as the tower is fixed (no ritual required first), but the
    ///   helicopter only comes once BOTH are done: whichever finishes second triggers victory.
    ///
    /// WHAT THIS CLASS HOLDS (all server-authoritative, replicated to every peer):
    ///   • netBatteryAttached / netTransformerAttached / netTransmitted — the escape's 3 switches.
    ///   • netDrops (NetworkList) — radio items lying on the ground after a carrier DIED. Late
    ///     joiners replay the list on spawn, exactly like the resource-node/door patterns.
    ///
    /// SETUP: Tools ▸ Sunset Curse ▸ 8 builds and wires everything (object, prefab refs, tower).
    /// Assign the helicopter sound here once you've imported it.
    /// </summary>
    public class RadioTowerState : NetworkBehaviour
    {
        public static RadioTowerState Instance { get; private set; }

        [Header("The escape tower")]
        [Tooltip("The scout tower that becomes the escape radio tower (used for the transmit-range check).")]
        [SerializeField] private Transform towerTransform;
        [Tooltip("0 = the SOS can be transmitted from ANYWHERE once the tower is fixed + ritual done. " +
                 "> 0 = the krotkofal only has signal within this many metres of the tower.")]
        [SerializeField] private float transmitRadius = 0f;

        [Header("Item prefabs (the imported 3D models — spawns AND death-drops use these)")]
        [SerializeField] private GameObject batteryPrefab;
        [SerializeField] private GameObject transformerPrefab;
        [SerializeField] private GameObject krotkofalPrefab;
        [Tooltip("The GOLDEN KEY model — spawns in the compound on night 4 (or the final night in " +
                 "shorter games) and unlocks the fence gate around the radio tower.")]
        [SerializeField] private GameObject goldenKeyPrefab;
        [Tooltip("Uniform scale applied to spawned item models (tune if the FBX imports huge/tiny).")]
        [SerializeField] private float itemScale = 1f;

        [Header("Per-item scale (× Item Scale — so one oversized FBX can be shrunk alone)")]
        [SerializeField] private float batteryScale = 1f;
        [Tooltip("The transformer model imports much larger than the other parts — this shrinks " +
                 "JUST the transformer. Tune to taste.")]
        [SerializeField] private float transformerScale = 0.35f;
        [SerializeField] private float krotkofalScale = 1f;
        [SerializeField] private float goldenKeyScale = 1f;

        [Header("Spawn-size safety (fixes the golden key spawning giant → trapping/blinding you)")]
        [Tooltip("Golden key ONLY: after scaling, fit the key so its largest dimension is this many " +
                 "metres — small, like the battery/potion. 0 = fall back to Golden Key Scale.")]
        [SerializeField] private float goldenKeyTargetSize = 0.4f;
        [Tooltip("Universal cap: NO spawned item may exceed this many metres. A mis-scaled model " +
                 "once filled the screen and sealed the player in place — this can't happen again. " +
                 "0 = no cap. Normal parts are well under it, so it only clamps true giants.")]
        [SerializeField] private float maxItemWorldSize = 3f;

        [Header("Findability glow (spawned parts + death-drops, night-only)")]
        [Tooltip("Colour of the faint glow on radio parts lying in the world. Warm amber reads " +
                 "as 'man-made object' against the cold night forest.")]
        [SerializeField] private Color itemGlowColor = new Color(1f, 0.75f, 0.35f);
        [Tooltip("Glow brightness. Keep it faint — it should be findable, not a beacon. 0 = off.")]
        [SerializeField] private float itemGlowIntensity = 1.6f;
        [Tooltip("Glow reach in metres.")]
        [SerializeField] private float itemGlowRange = 3f;
        [Tooltip("Slow spin (degrees/second) so a part reads as a pickup. 0 = static.")]
        [SerializeField] private float itemSpinSpeed = 25f;

        [Header("Victory")]
        [Tooltip("Helicopter rotors clip — plays 3D on the flyby (or 2D if no chopper spawns).")]
        [SerializeField] private AudioClip helicopterClip;
        [Range(0f, 1f)] [SerializeField] private float helicopterVolume = 1f;
        [Tooltip("Seconds between the transmission and the victory screen — the chopper sweeps across " +
                 "the sky during this window, then Victory fades in.")]
        [SerializeField] private float victoryDelaySeconds = 6f;

        [Header("Victory — helicopter flyby (the rescue)")]
        [Tooltip("Your helicopter MODEL (imported from the Asset Store). Leave EMPTY to fly a simple " +
                 "procedural placeholder chopper until you have one.")]
        [SerializeField] private GameObject helicopterPrefab;
        [Tooltip("How high the chopper flies over you, in metres.")]
        [SerializeField] private float flybyAltitude = 55f;
        [Tooltip("How far across the sky it travels (the sweep length), in metres.")]
        [SerializeField] private float flybySpan = 400f;
        [Tooltip("Uniform scale for the helicopter model (tune if the imported model is huge/tiny).")]
        [SerializeField] private float helicopterScale = 1f;
        [Tooltip("On victory, turn the player's camera to LOCK onto the chopper and track it across " +
                 "the sky (cinematic). Off = the player keeps free look while it flies over.")]
        [SerializeField] private bool victoryCameraLock = true;

        // ───────────── Networked state ─────────────
        private readonly NetworkVariable<bool> netBatteryAttached     = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<bool> netTransformerAttached = new NetworkVariable<bool>(false);
        private readonly NetworkVariable<bool> netTransmitted         = new NetworkVariable<bool>(false);
        // The fence gate around the tower — locked until someone turns the golden key in it.
        private readonly NetworkVariable<bool> netGateUnlocked        = new NetworkVariable<bool>(false);

        /// <summary>A radio item lying on the ground (its carrier died). Replicated to all.</summary>
        public struct RadioDrop : INetworkSerializable, IEquatable<RadioDrop>
        {
            public int id;
            public byte kind;
            public Vector3 pos;
            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref id);
                s.SerializeValue(ref kind);
                s.SerializeValue(ref pos);
            }
            public bool Equals(RadioDrop other) => id == other.id;
        }
        private readonly NetworkList<RadioDrop> netDrops = new NetworkList<RadioDrop>();
        private int nextDropId = 1;   // server-only counter

        // Local-only
        private readonly Dictionary<int, GameObject> localDrops = new Dictionary<int, GameObject>();
        private RitualSiteNet ritualSite;
        private bool ritualUnlockShown;
        private bool rescueAnnounced;   // helicopter message/sfx plays exactly once per peer

        // ───────────── Public reads ─────────────
        public bool BatteryAttached     => netBatteryAttached.Value;
        public bool TransformerAttached => netTransformerAttached.Value;
        public bool TowerFixed          => netBatteryAttached.Value && netTransformerAttached.Value;
        public bool Transmitted         => netTransmitted.Value;
        public bool GateUnlocked        => netGateUnlocked.Value;
        public Transform Tower          => towerTransform;
        public float TransmitRadius     => transmitRadius;

        public bool RitualDone
        {
            get
            {
                if (ritualSite == null) ritualSite = FindFirstObjectByType<RitualSiteNet>();
                return ritualSite != null && ritualSite.Completed;
            }
        }

        private float ScaleFor(RadioItemKind k) => k switch
        {
            RadioItemKind.Battery     => batteryScale,
            RadioItemKind.Transformer => transformerScale,
            RadioItemKind.GoldenKey   => goldenKeyScale,
            _                         => krotkofalScale,
        };

        public GameObject PrefabFor(RadioItemKind k) => k switch
        {
            RadioItemKind.Battery     => batteryPrefab,
            RadioItemKind.Transformer => transformerPrefab,
            RadioItemKind.GoldenKey   => goldenKeyPrefab,
            _                         => krotkofalPrefab,
        };

        public static string NameOf(RadioItemKind k) => k switch
        {
            RadioItemKind.Battery     => "battery",
            RadioItemKind.Transformer => "electrical transformer",
            RadioItemKind.GoldenKey   => "golden key",
            _                         => "handheld radio",
        };

        /// <summary>Server-side: is this kind already attached to the tower or lying as a drop?
        /// (The nightly spawner also checks player inventories separately.)</summary>
        public bool KindAttachedOrDropped(RadioItemKind k)
        {
            if (k == RadioItemKind.Battery && netBatteryAttached.Value) return true;
            if (k == RadioItemKind.Transformer && netTransformerAttached.Value) return true;
            for (int i = 0; i < netDrops.Count; i++)
                if (netDrops[i].kind == (byte)k) return true;
            return false;
        }

        // ───────────── Lifecycle ─────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            netDrops.OnListChanged += OnDropsChanged;
            netBatteryAttached.OnValueChanged += OnPartAttached;
            netTransformerAttached.OnValueChanged += OnPartAttached;
            netTransmitted.OnValueChanged += OnTransmittedChanged;
            netGateUnlocked.OnValueChanged += OnGateUnlockedChanged;
            RebuildLocalDrops();   // late joiners see items already on the ground
        }

        public override void OnNetworkDespawn()
        {
            netDrops.OnListChanged -= OnDropsChanged;
            netBatteryAttached.OnValueChanged -= OnPartAttached;
            netTransformerAttached.OnValueChanged -= OnPartAttached;
            netTransmitted.OnValueChanged -= OnTransmittedChanged;
            netGateUnlocked.OnValueChanged -= OnGateUnlockedChanged;
        }

        private void Update()
        {
            // One-time guidance the moment the ritual completes (netCompleted replicates, so this
            // runs on every peer). The two objectives are order-independent: if the SOS already
            // went out, the curse was the LAST thing in the way — the rescue arrives now.
            if (!ritualUnlockShown && RitualDone)
            {
                ritualUnlockShown = true;
                if (Transmitted)
                    AnnounceRescue();
                else
                    SunsetCurse.UI.ScreenMessage.Show(
                        TowerFixed
                            ? "The curse lifts! Press R with the handheld radio to send the SOS."
                            : "The curse lifts... but the radio tower is still dead. It needs a battery and a transformer.",
                        6f);
            }
        }

        // ───────────── Attaching parts (F at the tower top) ─────────────

        /// <summary>Called on the ATTACHER's machine. The server takes what the tower still needs,
        /// then tells the attacher (targeted) which items were consumed so they clear their own
        /// owner-write inventory flags.</summary>
        public void RequestAttach(bool offerBattery, bool offerTransformer)
        {
            if (!IsSpawned) { ServerAttach(offerBattery, offerTransformer, ulong.MaxValue); return; }
            AttachServerRpc(offerBattery, offerTransformer);
        }

        [ServerRpc(RequireOwnership = false)]
        private void AttachServerRpc(bool b, bool t, ServerRpcParams p = default)
            => ServerAttach(b, t, p.Receive.SenderClientId);

        private void ServerAttach(bool offerBattery, bool offerTransformer, ulong sender)
        {
            bool consumeB = offerBattery && !netBatteryAttached.Value;
            bool consumeT = offerTransformer && !netTransformerAttached.Value;
            if (!consumeB && !consumeT) return;
            if (consumeB) netBatteryAttached.Value = true;
            if (consumeT) netTransformerAttached.Value = true;

            if (sender == ulong.MaxValue)   // offline path
            {
                ApplyConsumedLocally(consumeB, consumeT);
                OnPartAttached(false, true);
            }
            else
            {
                var target = new ClientRpcParams
                { Send = new ClientRpcSendParams { TargetClientIds = new[] { sender } } };
                ConsumedClientRpc(consumeB, consumeT, target);
            }
        }

        [ClientRpc]
        private void ConsumedClientRpc(bool b, bool t, ClientRpcParams _) => ApplyConsumedLocally(b, t);

        private static void ApplyConsumedLocally(bool b, bool t)
        {
            var inv = PlayerInventory.Local;
            if (inv == null) return;
            if (b) inv.SetRadioItem(RadioItemKind.Battery, false);
            if (t) inv.SetRadioItem(RadioItemKind.Transformer, false);
            if (b || t) SunsetCurse.UI.ScreenMessage.Show("Attached!", 2.5f);
        }

        private void OnPartAttached(bool _, bool __)
        {
            if (TowerFixed)
                SunsetCurse.UI.ScreenMessage.Show(
                    "Radio Tower fixed! Press R to send SOS signal!", 6f);
            else
                SunsetCurse.UI.ScreenMessage.Show(
                    $"Part attached — the tower still needs {(BatteryAttached ? "the transformer" : "the battery")}.", 4f);
        }

        // ───────────── The golden gate (fence around the tower) ─────────────

        /// <summary>Called on the machine of whoever turned the key (GoldenGate.Interact). Server
        /// flips the shared flag; the key holder gets a targeted echo to consume their key.</summary>
        public void RequestUnlockGate()
        {
            if (!IsSpawned) { ServerUnlockGate(ulong.MaxValue); return; }
            UnlockGateServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void UnlockGateServerRpc(ServerRpcParams p = default)
            => ServerUnlockGate(p.Receive.SenderClientId);

        private void ServerUnlockGate(ulong sender)
        {
            if (netGateUnlocked.Value) return;
            netGateUnlocked.Value = true;
            if (sender == ulong.MaxValue)
            {
                ConsumeKeyLocal();
                OnGateUnlockedChanged(false, true);   // offline: no NetworkVariable callback
            }
            else
            {
                var target = new ClientRpcParams
                { Send = new ClientRpcSendParams { TargetClientIds = new[] { sender } } };
                KeyConsumedClientRpc(target);
            }
        }

        [ClientRpc]
        private void KeyConsumedClientRpc(ClientRpcParams _) => ConsumeKeyLocal();

        private static void ConsumeKeyLocal()
        {
            var inv = PlayerInventory.Local;
            if (inv != null) inv.SetRadioItem(RadioItemKind.GoldenKey, false);
        }

        private void OnGateUnlockedChanged(bool _, bool now)
        {
            if (now)
                SunsetCurse.UI.ScreenMessage.Show(
                    "The golden key turns — the radio tower's gate stands open.", 5f);
        }

        // ───────────── Transmission (R with the krotkofal) ─────────────

        public void RequestTransmit()
        {
            if (!IsSpawned) { ServerTransmit(); return; }
            TransmitServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void TransmitServerRpc() => ServerTransmit();

        private void ServerTransmit()
        {
            // Server re-validates: the tower must be FIXED, but the ritual may come before OR
            // after the SOS — whichever objective finishes second triggers the rescue.
            if (netTransmitted.Value || !TowerFixed) return;
            netTransmitted.Value = true;
            if (!IsSpawned) OnTransmittedChanged(false, true);
            if (RitualDone) StartCoroutine(VictoryAfterDelay());
        }

        /// <summary>Called SERVER-SIDE by RitualSiteNet the moment the ritual completes. If the
        /// SOS is already out, the curse was the last thing holding the rescue back — victory.</summary>
        public void NotifyRitualCompletedServer()
        {
            if (netTransmitted.Value) StartCoroutine(VictoryAfterDelay());
        }

        private IEnumerator VictoryAfterDelay()
        {
            yield return new WaitForSeconds(victoryDelaySeconds);
            if (GameClock.Instance != null) GameClock.Instance.TriggerEscape();   // → Victory, all peers
        }

        private void OnTransmittedChanged(bool _, bool now)
        {
            if (!now) return;
            if (RitualDone)
                AnnounceRescue();
            else
                SunsetCurse.UI.ScreenMessage.Show(
                    "»MAYDAY« — the SOS is out! But the curse still smothers the skies... break it at the altar!", 6f);
        }

        /// <summary>The "rescue is coming" moment — message + helicopter rotors, once per peer.
        /// Reached from EITHER order: transmit-then-ritual or ritual-then-transmit.</summary>
        private void AnnounceRescue()
        {
            if (rescueAnnounced) return;
            rescueAnnounced = true;
            SunsetCurse.UI.ScreenMessage.Show(
                "»MAYDAY, MAYDAY« — rotors thump somewhere beyond the trees. Rescue is coming!", 6f);

            // The chopper sweeps across the sky over the local player (each peer spawns its own),
            // carrying the rotor audio 3D. It flies for the victory delay, then the Victory screen
            // fades in. If no model is assigned it flies a procedural placeholder + still plays the
            // rotors, so the moment always reads.
            HelicopterFlyby.Spawn(helicopterPrefab, helicopterClip, helicopterVolume,
                                  flybyAltitude, flybySpan, victoryDelaySeconds, helicopterScale,
                                  victoryCameraLock);
        }

        // ───────────── Death drops ─────────────

        /// <summary>Called on the DYING player's machine (owner). Clears their owner-write flags
        /// and asks the server to put the items on the ground where they fell.</summary>
        public void DropAllFromLocalInventory(Vector3 pos)
        {
            var inv = PlayerInventory.Local;
            if (inv == null) return;
            bool b = inv.HasBattery, t = inv.HasTransformer, k = inv.HasKrotkofal, g = inv.HasGoldenKey;
            if (!b && !t && !k && !g) return;

            if (b) inv.SetRadioItem(RadioItemKind.Battery, false);
            if (t) inv.SetRadioItem(RadioItemKind.Transformer, false);
            if (k) inv.SetRadioItem(RadioItemKind.Krotkofal, false);
            if (g) inv.SetRadioItem(RadioItemKind.GoldenKey, false);

            if (!IsSpawned) ServerDrop(pos, b, t, k, g);
            else DropServerRpc(pos, b, t, k, g);
        }

        [ServerRpc(RequireOwnership = false)]
        private void DropServerRpc(Vector3 pos, bool b, bool t, bool k, bool g) => ServerDrop(pos, b, t, k, g);

        private void ServerDrop(Vector3 pos, bool b, bool t, bool k, bool g)
        {
            if (b) AddDrop(RadioItemKind.Battery, pos, 0);
            if (t) AddDrop(RadioItemKind.Transformer, pos, 1);
            if (k) AddDrop(RadioItemKind.Krotkofal, pos, 2);
            if (g) AddDrop(RadioItemKind.GoldenKey, pos, 3);
            if (b || t || k || g)
                SunsetCurse.AI.MonsterAlerts.ReportLoudNoise(pos, 16f);   // clattering parts — audible
            if (!IsSpawned) RebuildLocalDrops();
        }

        private void AddDrop(RadioItemKind kind, Vector3 pos, int scatterSlot)
        {
            // Small per-item offset so multiple drops don't overlap inside each other.
            float a = scatterSlot * 2.1f;
            Vector3 scattered = pos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.7f;
            if (Physics.Raycast(scattered + Vector3.up * 1.5f, Vector3.down, out var hit, 20f,
                                ~0, QueryTriggerInteraction.Ignore))
                scattered = hit.point + Vector3.up * 0.05f;
            netDrops.Add(new RadioDrop { id = nextDropId++, kind = (byte)kind, pos = scattered });
        }

        public void RequestPickupDrop(int dropId)
        {
            if (!IsSpawned) { ServerPickupDrop(dropId, ulong.MaxValue); return; }
            PickupDropServerRpc(dropId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PickupDropServerRpc(int dropId, ServerRpcParams p = default)
            => ServerPickupDrop(dropId, p.Receive.SenderClientId);

        private void ServerPickupDrop(int dropId, ulong sender)
        {
            for (int i = 0; i < netDrops.Count; i++)
            {
                if (netDrops[i].id != dropId) continue;
                byte kind = netDrops[i].kind;
                netDrops.RemoveAt(i);   // first grab wins — a second request won't find the id
                if (sender == ulong.MaxValue)
                {
                    GrantLocal((RadioItemKind)kind);
                    RebuildLocalDrops();
                }
                else
                {
                    var target = new ClientRpcParams
                    { Send = new ClientRpcSendParams { TargetClientIds = new[] { sender } } };
                    GrantDropClientRpc(kind, target);
                }
                return;
            }
        }

        [ClientRpc]
        private void GrantDropClientRpc(byte kind, ClientRpcParams _) => GrantLocal((RadioItemKind)kind);

        /// <summary>Give one radio item to the LOCAL player (used by drops AND the nightly spawner).</summary>
        public static void GrantLocal(RadioItemKind kind)
        {
            var inv = PlayerInventory.Local;
            if (inv != null) inv.SetRadioItem(kind, true);
            string extra = kind switch
            {
                RadioItemKind.Krotkofal => " Press R to check it.",
                RadioItemKind.GoldenKey => " It opens the radio tower's gate.",
                _ => " Carry it to the top of the radio tower.",
            };
            SunsetCurse.UI.ScreenMessage.Show($"Picked up the {NameOf(kind)}.{extra}", 4f);
        }

        // ───────────── Local drop visuals (every peer mirrors the replicated list) ─────────────

        private void OnDropsChanged(NetworkListEvent<RadioDrop> _) => RebuildLocalDrops();

        private void RebuildLocalDrops()
        {
            // Remove visuals whose entry is gone.
            var stale = new List<int>();
            foreach (var kv in localDrops)
            {
                bool exists = false;
                for (int i = 0; i < netDrops.Count; i++) if (netDrops[i].id == kv.Key) { exists = true; break; }
                if (!exists) stale.Add(kv.Key);
            }
            foreach (int id in stale)
            {
                if (localDrops[id] != null) Destroy(localDrops[id]);
                localDrops.Remove(id);
            }

            // Spawn visuals for new entries.
            for (int i = 0; i < netDrops.Count; i++)
            {
                var d = netDrops[i];
                if (localDrops.ContainsKey(d.id)) continue;
                var go = CreateItemVisual((RadioItemKind)d.kind, d.pos);
                var pickup = go.AddComponent<RadioDropPickup>();
                pickup.Configure(d.id, (RadioItemKind)d.kind);
                localDrops[d.id] = go;
            }
        }

        /// <summary>Instantiate the item's 3D model (shared by drops and the nightly spawner):
        /// prefab if assigned, glowing box fallback if not, collider guaranteed for the E-raycast.</summary>
        public GameObject CreateItemVisual(RadioItemKind kind, Vector3 pos)
        {
            GameObject go;
            var prefab = PrefabFor(kind);
            if (prefab != null)
            {
                go = Instantiate(prefab, pos, Quaternion.identity, transform);
                go.transform.localScale = Vector3.one * (itemScale * ScaleFor(kind));
                NormalizeSpawnSize(go, kind);   // key → small; anything giant → clamped
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.SetParent(transform, true);
                go.transform.position = pos;
                // No prefab assigned → placeholder cube. Still honours the Inspector scales so
                // tuning behaves the same whether or not the real model is hooked up yet.
                go.transform.localScale = Vector3.one * (0.35f * itemScale * ScaleFor(kind));
            }
            go.name = $"RadioItem_{kind}";

            StripModelArtifacts(go);   // MUST run before we add our own glow below

            // Findability: faint night-only glow + slow spin (cosmetic, local to each peer).
            go.AddComponent<RadioItemGlow>()
              .Configure(itemGlowColor, itemGlowIntensity, itemGlowRange, itemSpinSpeed);

            EnsurePickupCollider(go);
            return go;
        }

        /// <summary>Strip view/audio junk that FBX / Sketchfab model exports smuggle in. BUG #1's
        /// real cause: the golden-key FBX shipped an embedded CAMERA node (the importer's
        /// importCameras flag was ON), so the instant the key spawned, ITS camera hijacked the
        /// view — the screen locked onto the key and you couldn't see yourself move, wherever you
        /// were. We destroy any embedded Camera / AudioListener / Light on the spawned model (our
        /// own RadioItemGlow, added afterwards, is the only light a pickup needs).</summary>
        private static void StripModelArtifacts(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Camera>(true))        Destroy(c);
            foreach (var a in go.GetComponentsInChildren<AudioListener>(true)) Destroy(a);
            foreach (var l in go.GetComponentsInChildren<Light>(true))         Destroy(l);
        }

        /// <summary>Guarantee ONE collider the E-raycast can hit, then make EVERY collider on the
        /// item a TRIGGER. Pickups must never physically block the player: a solid collider fitted
        /// to an oversized golden-key model once sealed the player in place at nightfall (BUG #1).
        /// PlayerInteractor's ray uses QueryTriggerInteraction.Collide, so E still works on triggers.</summary>
        private static void EnsurePickupCollider(GameObject go)
        {
            if (go.GetComponentInChildren<Collider>() == null)
            {
                // FBX models import without colliders — box it (from the now-normalized bounds).
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
            foreach (var col in go.GetComponentsInChildren<Collider>())
            {
                if (col is MeshCollider mc) mc.convex = true;   // a MeshCollider must be convex to be a trigger
                col.isTrigger = true;
            }
        }

        /// <summary>Keep spawned items sane in size: the golden key is fit to a small, key-sized
        /// target (it imports many times larger than the other parts), and ANY item that's still
        /// giant is clamped — so a mis-scaled model can never again fill the screen / trap the
        /// player. Measures the item's world renderer bounds and rescales uniformly.</summary>
        private void NormalizeSpawnSize(GameObject go, RadioItemKind kind)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return;
            Bounds b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            float maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (maxDim <= 0.0001f) return;

            if (kind == RadioItemKind.GoldenKey && goldenKeyTargetSize > 0f)
            {
                go.transform.localScale *= goldenKeyTargetSize / maxDim;   // fit key to target size
                return;
            }

            // Safety clamp for giants — applied to the model's BASE size (what it would measure
            // at a per-item scale of 1), NOT the final size. Clamping the final size silently
            // cancelled the per-item Inspector scales: a huge transformer came out at exactly
            // maxItemWorldSize no matter what Transformer Scale was set to. Now the cap shrinks
            // only the oversized BASE model and the per-item multiplier always applies on top
            // (final size = cap × per-item scale), so tuning in the Inspector always shows.
            float perItem = Mathf.Max(0.0001f, ScaleFor(kind));
            float baseDim = maxDim / perItem;
            if (maxItemWorldSize > 0f && baseDim > maxItemWorldSize)
            {
                go.transform.localScale *= maxItemWorldSize / baseDim;
                Debug.LogWarning($"[RadioTowerState] {kind} model measures {baseDim:0.0}m at per-item " +
                                 $"scale 1 — base capped to {maxItemWorldSize}m, then × {perItem:0.##} " +
                                 $"per-item scale = {maxItemWorldSize * perItem:0.00}m. Tune the " +
                                 "per-item scale in the Inspector to resize it.");
            }
        }
    }
}
