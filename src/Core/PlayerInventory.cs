using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SunsetCurse.Core
{
    /// <summary>The parts of the radio-tower escape (+ the golden key that opens the tower's
    /// gate). Declared here (Core) because the per-player carry flags live on PlayerInventory;
    /// the World scripts reference it too.</summary>
    public enum RadioItemKind : byte { Battery = 0, Transformer = 1, Krotkofal = 2, GoldenKey = 3 }

    /// <summary>Carry state of the shared river bucket (see RiverWaterBucket): None = not
    /// carrying, Empty = grabbed from the shed, Filled = holding river water (the ritual's
    /// offering). Declared in Core beside RadioItemKind for the same reason.</summary>
    public enum BucketState : byte { None = 0, Empty = 1, Filled = 2 }

    /// <summary>
    /// Per-player inventory. Lives on PlayerCapsule (one NGO instance per connected player).
    /// Owner-authoritative — each client writes their OWN counts directly via NetworkVariable,
    /// NGO replicates to every other peer (so a future scoreboard could show everybody's stash).
    ///
    /// In single-player there's just one PlayerInventory (the host) and it's both owner and server,
    /// so it behaves the same way. The InventoryUI / CraftingTable / Torch always look at the
    /// LOCAL player's inventory via <see cref="Local"/>; they never touch other players'.
    ///
    /// Shared world state (the crafting table built / position, monster-stun broadcast) lives on
    /// <see cref="Inventory"/> instead — this class is strictly the per-player slice.
    ///
    /// SETUP:
    ///   1. Open the PlayerCapsule prefab.
    ///   2. Add this PlayerInventory component (anywhere on the root). It only needs a NetworkObject
    ///      on the same GameObject, which the prefab already has.
    /// </summary>
    public class PlayerInventory : NetworkBehaviour
    {
        /// <summary>The LOCAL player's inventory. Null until their PlayerCapsule has spawned.</summary>
        public static PlayerInventory Local { get; private set; }

        /// <summary>Every live PlayerInventory on THIS machine — local AND remote players (the
        /// whole player prefab replicates to every peer, so this is the full roster everywhere).
        /// Lets systems like the ritual's river-water check and the bucket respawner iterate
        /// players without a FindObjectsByType sweep every frame.</summary>
        public static readonly List<PlayerInventory> All = new List<PlayerInventory>();

        /// <summary>Any change to the LOCAL player's inventory (counts or item flags).</summary>
        public static event Action OnLocalChanged;
        /// <summary>Fires when the LOCAL player just crafted their torch — drives the flashlight buff.</summary>
        public static event Action OnLocalTorchCrafted;

        // ───────────── Owner-write NetworkVariables ─────────────
        // The owning client writes directly; everyone reads. Default write-perm is Server, which
        // would force every Add/Consume through a ServerRpc — that's slower and noisier in chat.

        private readonly NetworkVariable<int>  netWood = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<int>  netStone = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<int>  netRitualHerb = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> netHasTorch = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> netHasLordsSyrup = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> netLordsSyrupUsed = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // New persistent crafts — both stack as counters so a player can carry / craft multiple.
        // Stone Distractor: thrown to lure the monster, picked back up after use (non-destructible).
        // Respawn Potion:    consumed automatically on death to revive the player.
        private readonly NetworkVariable<int>  netStoneDistractors = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<int>  netRespawnPotions = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        // Dawn "Teleport Potion" (SP) / "Revival Potion" (MP) — spawns each dawn, grabbed off the
        // world (NOT crafted). SP: press T to teleport ≥45m away. MP: hold T while downed to
        // self-revive. Distinct from the crafted netRespawnPotions above (which auto-fires on death).
        private readonly NetworkVariable<int>  netTeleportPotions = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Radio-tower escape parts — owner-write like everything else per-player. Replicated so
        // the HOST can read who carries what (the nightly spawner's "what's still missing" pool).
        private readonly NetworkVariable<bool> netHasBattery = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> netHasTransformer = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> netHasKrotkofal = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<bool> netHasGoldenKey = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public bool HasBattery     => netHasBattery.Value;
        public bool HasTransformer => netHasTransformer.Value;
        public bool HasKrotkofal   => netHasKrotkofal.Value;
        public bool HasGoldenKey   => netHasGoldenKey.Value;

        // The river bucket (RiverWaterBucket spawns ONE in the world) — whoever grabs it carries
        // it Empty, fills it in the river (hold X), and must bring the Filled bucket to the ritual.
        // Owner-write byte like every other per-player item; replicated so the host can validate
        // the ritual's water requirement and respawn the bucket if its carrier dies.
        private readonly NetworkVariable<byte> netBucket = new NetworkVariable<byte>(
            (byte)BucketState.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public BucketState Bucket   => (BucketState)netBucket.Value;
        public bool HasBucket       => netBucket.Value != (byte)BucketState.None;
        public bool HasFilledBucket => netBucket.Value == (byte)BucketState.Filled;

        /// <summary>Owner-write bucket state (pickups arrive via targeted ClientRpc, fills happen
        /// locally on the carrier's machine — so this always runs on the right peer).</summary>
        public void SetBucket(BucketState state)
        {
            if (!IsOwnerSafe) return;
            if (netBucket.Value == (byte)state) return;
            netBucket.Value = (byte)state;
            if (!IsSpawned) OnLocalChanged?.Invoke();
        }

        public bool HasRadioItem(RadioItemKind k) => k switch
        {
            RadioItemKind.Battery     => netHasBattery.Value,
            RadioItemKind.Transformer => netHasTransformer.Value,
            RadioItemKind.GoldenKey   => netHasGoldenKey.Value,
            _                         => netHasKrotkofal.Value,
        };

        /// <summary>Give/take a radio part (owner only — pickups arrive via targeted ClientRpc,
        /// so this always runs on the right machine).</summary>
        public void SetRadioItem(RadioItemKind k, bool own)
        {
            if (!IsOwnerSafe) return;
            switch (k)
            {
                case RadioItemKind.Battery:     netHasBattery.Value = own; break;
                case RadioItemKind.Transformer: netHasTransformer.Value = own; break;
                case RadioItemKind.GoldenKey:   netHasGoldenKey.Value = own; break;
                default:                        netHasKrotkofal.Value = own; break;
            }
            if (!IsSpawned) OnLocalChanged?.Invoke();
        }

        // Replicated alive flag — owner-write. PlayerStats sets this to false on death so other
        // peers know which players are still alive (used by SpectatorMode + GameOverController).
        private readonly NetworkVariable<bool> netIsAlive = new NetworkVariable<bool>(
            true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public bool IsAlive => netIsAlive.Value;
        /// <summary>Fires on every peer when ANY player's alive state changes.</summary>
        public static event System.Action OnAnyAliveChanged;

        // Replicated DOWNED flag — owner-write. True while the player is bleeding out and can be
        // revived by a teammate. IsAlive stays TRUE while downed (downed ≠ dead): the monster
        // ignores downed players (MonsterAI.Huntable) but the body stays visible for the rescue.
        private readonly NetworkVariable<bool> netIsDowned = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public bool IsDowned => netIsDowned.Value;

        /// <summary>Called by PlayerStats when this player goes down / gets revived (owner-write).</summary>
        public void SetDowned(bool downed)
        {
            if (!IsOwnerSafe) return;
            if (netIsDowned.Value == downed) return;
            netIsDowned.Value = downed;
            if (!IsSpawned) OnAnyAliveChanged?.Invoke();   // SP path (no NetworkVariable callback)
        }

        // Replicated CLIMBING state — owner-write. PlayerClimb (owner-only) publishes it; every
        // peer's PlayerAnimatorDriver reads it to play the ladder animation. We network it instead
        // of guessing from movement so a fall can't be mistaken for a climb. ClimbDir: +1 up,
        // -1 down, 0 = holding on the ladder.
        private readonly NetworkVariable<bool> netIsClimbing = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> netClimbDir = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public bool IsClimbing => netIsClimbing.Value;
        public float ClimbDir  => netClimbDir.Value;

        /// <summary>Called by PlayerClimb (owner) as the player grabs / rides / leaves the ladder.</summary>
        public void SetClimbState(bool climbing, float dir)
        {
            if (!IsOwnerSafe) return;
            if (netIsClimbing.Value != climbing) netIsClimbing.Value = climbing;
            float d = Mathf.Clamp(dir, -1f, 1f);
            if (!Mathf.Approximately(netClimbDir.Value, d)) netClimbDir.Value = d;
        }

        // Replicated health + hunger (0..1), owner-write. PlayerStats (owner-only) publishes these
        // each frame so OTHER machines — chiefly a spectator watching this player — can show the
        // correct bars. Without this a spectator only sees their own (dead) 0-health HUD.
        private readonly NetworkVariable<float> netHealth01 = new NetworkVariable<float>(
            1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> netHunger01 = new NetworkVariable<float>(
            1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public float Health01 => netHealth01.Value;
        public float Hunger01 => netHunger01.Value;

        public void PublishStats(float health01, float hunger01)
        {
            if (!IsOwnerSafe) return;
            float h = Mathf.Clamp01(health01), g = Mathf.Clamp01(hunger01);
            if (!Mathf.Approximately(netHealth01.Value, h)) netHealth01.Value = h;
            if (!Mathf.Approximately(netHunger01.Value, g)) netHunger01.Value = g;
        }

        // ───────────── Read API ─────────────
        public int  Wood              => netWood.Value;
        public int  Stone             => netStone.Value;
        public int  RitualHerb        => netRitualHerb.Value;
        public bool HasTorch          => netHasTorch.Value;
        public bool HasLordsSyrup     => netHasLordsSyrup.Value;
        public bool LordsSyrupUsed    => netLordsSyrupUsed.Value;
        public int  StoneDistractors  => netStoneDistractors.Value;
        public int  RespawnPotions    => netRespawnPotions.Value;
        public int  TeleportPotions   => netTeleportPotions.Value;
        public bool HasStoneDistractor => netStoneDistractors.Value > 0;
        public bool HasRespawnPotion   => netRespawnPotions.Value > 0;
        public bool HasTeleportPotion  => netTeleportPotions.Value > 0;

        public int Get(ResourceType t) => t switch
        {
            ResourceType.Wood       => Wood,
            ResourceType.Stone      => Stone,
            ResourceType.RitualHerb => RitualHerb,
            _ => 0
        };

        public bool CanAfford(int wood, int stone, int herb)
            => Wood >= wood && Stone >= stone && RitualHerb >= herb;

        // ───────────── Write API (owner only) ─────────────

        public void Add(ResourceType type, int amount)
        {
            if (!IsOwnerSafe || amount <= 0) return;
            switch (type)
            {
                case ResourceType.Wood:       netWood.Value       += amount; break;
                case ResourceType.Stone:      netStone.Value      += amount; break;
                case ResourceType.RitualHerb: netRitualHerb.Value += amount; break;
            }
            if (!IsSpawned) OnLocalChanged?.Invoke(); // SP fallback (no NetworkVariable callback)
        }

        public bool TryConsume(ResourceType type, int amount)
        {
            if (!IsOwnerSafe || Get(type) < amount) return false;
            switch (type)
            {
                case ResourceType.Wood:       netWood.Value       -= amount; break;
                case ResourceType.Stone:      netStone.Value      -= amount; break;
                case ResourceType.RitualHerb: netRitualHerb.Value -= amount; break;
            }
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        public bool TryConsumeMany(int wood, int stone, int herb)
        {
            if (!IsOwnerSafe || !CanAfford(wood, stone, herb)) return false;
            if (wood  > 0) netWood.Value       -= wood;
            if (stone > 0) netStone.Value      -= stone;
            if (herb  > 0) netRitualHerb.Value -= herb;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        /// <summary>Atomic craft — checks affordability, deducts, sets the item flag. Returns
        /// false if the local player can't afford or already owns one.</summary>
        public bool TryCraftTorch(int costWood, int costStone)
        {
            if (!IsOwnerSafe || netHasTorch.Value) return false;
            if (!CanAfford(costWood, costStone, 0)) return false;
            netWood.Value     -= costWood;
            netStone.Value    -= costStone;
            netHasTorch.Value  = true;
            if (!IsSpawned) { OnLocalChanged?.Invoke(); OnLocalTorchCrafted?.Invoke(); }
            return true;
        }

        public bool TryCraftLordsSyrup(int costWood, int costHerb)
        {
            if (!IsOwnerSafe || netHasLordsSyrup.Value || netLordsSyrupUsed.Value) return false;
            if (!CanAfford(costWood, 0, costHerb)) return false;
            netWood.Value          -= costWood;
            netRitualHerb.Value    -= costHerb;
            netHasLordsSyrup.Value  = true;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        /// <summary>Pour the syrup. Removes it from this player's inventory and tells the
        /// shared <see cref="Inventory"/> singleton to broadcast the monster stun to every peer.</summary>
        public bool TryUseLordsSyrup()
        {
            if (!IsOwnerSafe || !netHasLordsSyrup.Value || netLordsSyrupUsed.Value) return false;
            netHasLordsSyrup.Value  = false;
            netLordsSyrupUsed.Value = true;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            Inventory.TriggerLordsSyrupStun();
            return true;
        }

        // ───────────── Stone Distractor + Respawn Potion ─────────────

        public bool TryCraftStoneDistractor(int costStone, int costHerb)
        {
            if (!IsOwnerSafe) return false;
            if (!CanAfford(0, costStone, costHerb)) return false;
            netStone.Value             -= costStone;
            netRitualHerb.Value        -= costHerb;
            netStoneDistractors.Value  += 1;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        public bool TryCraftRespawnPotion(int costWood, int costHerb)
        {
            if (!IsOwnerSafe) return false;
            if (!CanAfford(costWood, 0, costHerb)) return false;
            netWood.Value           -= costWood;
            netRitualHerb.Value     -= costHerb;
            netRespawnPotions.Value += 1;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        /// <summary>Remove one distractor from inventory (it's now in the world). Returns false
        /// if none held.</summary>
        public bool TryRemoveStoneDistractor()
        {
            if (!IsOwnerSafe || netStoneDistractors.Value <= 0) return false;
            netStoneDistractors.Value -= 1;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        /// <summary>Pick a distractor up off the world. Called server-side via Inventory's broadcast
        /// pickup flow (only the picker's client adds it).</summary>
        public void AddStoneDistractor(int count = 1)
        {
            if (!IsOwnerSafe || count <= 0) return;
            netStoneDistractors.Value += count;
            if (!IsSpawned) OnLocalChanged?.Invoke();
        }

        public bool TryConsumeRespawnPotion()
        {
            if (!IsOwnerSafe || netRespawnPotions.Value <= 0) return false;
            netRespawnPotions.Value -= 1;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        /// <summary>Owner-side setter — Torch.cs flips this to false when it fires (the torch is
        /// consumed each use, AND wiped at dawn if unused). Setting back to true happens via the
        /// crafting flow only.</summary>
        public void SetTorchOwned(bool owned)
        {
            if (!IsOwnerSafe) return;
            if (netHasTorch.Value == owned) return;
            netHasTorch.Value = owned;
            if (!IsSpawned) OnLocalChanged?.Invoke();
        }

        /// <summary>Remove the syrup from this player's inventory (it just went out the door —
        /// either dropped to the world OR used). The "ever crafted" intent is preserved via
        /// netLordsSyrupUsed staying true once flipped, so the player can't double-craft.</summary>
        public bool TryRemoveLordsSyrup()
        {
            if (!IsOwnerSafe || !netHasLordsSyrup.Value) return false;
            netHasLordsSyrup.Value = false;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        /// <summary>Pick a syrup up from the world. Doesn't touch netLordsSyrupUsed — if the picker
        /// has never crafted one themselves, they can still craft later AND keep this one.</summary>
        public void AddLordsSyrup()
        {
            if (!IsOwnerSafe) return;
            if (netHasLordsSyrup.Value) return;   // already have one (game design: max 1 in hand)
            netHasLordsSyrup.Value = true;
            if (!IsSpawned) OnLocalChanged?.Invoke();
        }

        public bool TryRemoveRespawnPotion()
        {
            if (!IsOwnerSafe || netRespawnPotions.Value <= 0) return false;
            netRespawnPotions.Value -= 1;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        public void AddRespawnPotion(int count = 1)
        {
            if (!IsOwnerSafe || count <= 0) return;
            netRespawnPotions.Value += count;
            if (!IsSpawned) OnLocalChanged?.Invoke();
        }

        // ── Dawn Teleport/Revival Potion ──
        public void AddTeleportPotion(int count = 1)
        {
            if (!IsOwnerSafe || count <= 0) return;
            netTeleportPotions.Value += count;
            if (!IsSpawned) OnLocalChanged?.Invoke();
        }

        public bool TryConsumeTeleportPotion()
        {
            if (!IsOwnerSafe || netTeleportPotions.Value <= 0) return false;
            netTeleportPotions.Value -= 1;
            if (!IsSpawned) OnLocalChanged?.Invoke();
            return true;
        }

        // ───────────── Lifecycle ─────────────

        public override void OnNetworkSpawn()
        {
            // Alive/downed listeners run on EVERY peer (not owner-only) so anyone can know who's down.
            netIsAlive.OnValueChanged += FireAnyAlive;
            netIsDowned.OnValueChanged += FireAnyAlive;

            if (IsOwner)
            {
                Local = this;
                netWood.OnValueChanged             += FireLocalInt;
                netStone.OnValueChanged            += FireLocalInt;
                netRitualHerb.OnValueChanged       += FireLocalInt;
                netHasTorch.OnValueChanged         += OnTorchChanged;
                netHasLordsSyrup.OnValueChanged    += FireLocalBool;
                netLordsSyrupUsed.OnValueChanged   += FireLocalBool;
                netStoneDistractors.OnValueChanged += FireLocalInt;
                netRespawnPotions.OnValueChanged   += FireLocalInt;
                netTeleportPotions.OnValueChanged  += FireLocalInt;
                // Radio-escape parts too — the inventory panel shows carried-item cards for them.
                netHasBattery.OnValueChanged       += FireLocalBool;
                netHasTransformer.OnValueChanged   += FireLocalBool;
                netHasKrotkofal.OnValueChanged     += FireLocalBool;
                netHasGoldenKey.OnValueChanged     += FireLocalBool;
                netBucket.OnValueChanged           += FireLocalByte;

                // Torch is now per-night: at every dawn the owner resets their HasTorch flag so
                // they must re-craft for tonight. (Crafting table reset is host-side in Inventory.)
                if (GameClock.Instance != null) GameClock.Instance.OnDayStart += OnDawnResetTorch;

                // Initial nudge so any UI that subscribed before we spawned picks us up.
                OnLocalChanged?.Invoke();
            }
        }

        public override void OnNetworkDespawn()
        {
            netIsAlive.OnValueChanged -= FireAnyAlive;
            netIsDowned.OnValueChanged -= FireAnyAlive;

            if (IsOwner)
            {
                netWood.OnValueChanged             -= FireLocalInt;
                netStone.OnValueChanged            -= FireLocalInt;
                netRitualHerb.OnValueChanged       -= FireLocalInt;
                netHasTorch.OnValueChanged         -= OnTorchChanged;
                netHasLordsSyrup.OnValueChanged    -= FireLocalBool;
                netLordsSyrupUsed.OnValueChanged   -= FireLocalBool;
                netStoneDistractors.OnValueChanged -= FireLocalInt;
                netRespawnPotions.OnValueChanged   -= FireLocalInt;
                netTeleportPotions.OnValueChanged  -= FireLocalInt;
                netHasBattery.OnValueChanged       -= FireLocalBool;
                netHasTransformer.OnValueChanged   -= FireLocalBool;
                netHasKrotkofal.OnValueChanged     -= FireLocalBool;
                netHasGoldenKey.OnValueChanged     -= FireLocalBool;
                netBucket.OnValueChanged           -= FireLocalByte;
                if (GameClock.Instance != null) GameClock.Instance.OnDayStart -= OnDawnResetTorch;
                if (Local == this) Local = null;
                OnLocalChanged?.Invoke();   // UI clears
            }
        }

        private void OnDawnResetTorch(int day)
        {
            // Day 1 is the initial state — nothing to clear. Days 2+ wipe yesterday's torch.
            if (day <= 1) return;
            if (!IsOwnerSafe) return;
            if (netHasTorch.Value) netHasTorch.Value = false;
        }

        /// <summary>Called by PlayerStats when this player's alive state changes (owner-write).</summary>
        public void SetAlive(bool alive)
        {
            if (!IsOwnerSafe) return;
            netIsAlive.Value = alive;
            // Death loses the bucket. RiverWaterBucket (server) notices "no living carrier" and
            // respawns it at the shed, so the water objective can never soft-lock on a corpse.
            if (!alive && netBucket.Value != (byte)BucketState.None)
                netBucket.Value = (byte)BucketState.None;
            if (!IsSpawned) OnAnyAliveChanged?.Invoke();   // SP path
        }

        private static void FireAnyAlive(bool _, bool __) => OnAnyAliveChanged?.Invoke();

        // In single-player the host's PlayerCapsule may never get NGO-spawned (offline play).
        // Awake sets Local optimistically so the UI shows something immediately; OnNetworkSpawn
        // overrides it once we go networked.
        private void Awake()
        {
            if (Local == null) Local = this;
            if (!All.Contains(this)) All.Add(this);
        }

        public override void OnDestroy()
        {
            if (Local == this) Local = null;
            All.Remove(this);
            base.OnDestroy();   // NGO's own NetworkBehaviour cleanup must still run
        }

        // Owner-write only is enforced by the NetworkVariable; but in single-player the object
        // isn't spawned, IsOwner returns false. Treat "not spawned" as "we're the only player so
        // we are by definition the owner". This is the same trick GameClock uses for SP fallback.
        private bool IsOwnerSafe => !IsSpawned || IsOwner;

        private void FireLocalInt(int _, int __)        => OnLocalChanged?.Invoke();
        private void FireLocalBool(bool _, bool __)     => OnLocalChanged?.Invoke();
        private void FireLocalByte(byte _, byte __)     => OnLocalChanged?.Invoke();
        private void OnTorchChanged(bool prev, bool next)
        {
            OnLocalChanged?.Invoke();
            if (next && !prev) OnLocalTorchCrafted?.Invoke();
        }
    }
}
