# Sunset Curse

A four-player co-op horror survival game built in Unity 6. A traveller's car breaks down beside a
cursed forest settlement, and the locals reveal he can never leave — unless the group gathers
enough valuables to break the curse. Players have **seven in-game days**. Each night the sun sets
a little less, and on day seven night becomes permanent.

**Scale:** 105 C# scripts · ~24,600 lines · single Unity 6 project

> *Role: Solo developer - Coded with assistance of Claude code.

> **This repository contains the source code only.** The full Unity project also includes licensed
> Unity Asset Store art, audio and animation packs, which are excluded here for licensing and size
> reasons. Every file in `src/` was written for this project.

---

## Gameplay

- **Day** — forage berries to hold back hunger, gather wood/stone/herbs, craft tools, read the
  lore pages, and plan the night's raid.
- **Night** — the monsters leave their ground. Raid the fortified compound for one hidden Ritual
  Potion per night, and scavenge the parts needed to repair a derelict radio tower.
- **Escape** — two independent win conditions, completable in either order: perform the altar
  **ritual** (a hold-the-line channel that broadcasts noise and draws every monster to you), and
  repair the tower to transmit an **SOS** that calls in a rescue helicopter.
- **Pressure** — the safe daylight window shrinks every day (30 seconds less every subsequent day); miss the deadline and night is permanent.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Engine | Unity **6000.4.8f1** (Unity 6) |
| Language | **C#** (all gameplay, networking, tooling) · ShaderLab/HLSL for water surface work |
| Rendering | Universal Render Pipeline **17.4.0** |
| Multiplayer | Netcode for GameObjects **2.0.0** + Unity Relay **1.1.0** + Authentication **3.3.0** |
| Input | Unity Input System **1.19.0** (no legacy input) |
| AI / Pathfinding | AI Navigation **2.0.12** (NavMesh, runtime-baked) |
| Camera | Cinemachine **3.1.6** |
| Audio | AudioMixer bus routing (Master ▸ Music / SFX) with PlayerPrefs-backed settings |
| Tooling | Custom Unity Editor extensions (~2,900 lines of editor-only automation) |

---

## Architecture

**Host-authoritative multiplayer.** All shared world state lives on the host and replicates to
peers; clients drive only their own input and movement.

- **Replication primitives** — `NetworkVariable` for shared scalars (day/phase, escape count,
  ritual progress), `NetworkList` for collections that must survive late joins (world drops,
  consumed resource nodes, lobby roster), `ServerRpc` for client requests, and *targeted*
  `ClientRpc` for per-player grants.
- **Deterministic world generation** — the host seeds one replicated world seed; every client
  derives its forest, berry, and daily-pickup scatter from `seed XOR day`. Identical worlds on
  every machine with **zero position traffic**. World objects get deterministic hashed IDs so
  consuming one despawns it team-wide through a single synced integer.
- **Conflict arbitration** — every pickup is server-validated first-grab-wins, so two players
  racing for the same item can never both receive it.
- **Late-join correctness** — new peers replay list-backed state on connect rather than relying
  on one-shot broadcasts.
- **Event-driven time** — a single `GameClock` owns day/night phases and fires C# events;
  ~15 consumer systems subscribe instead of polling in `Update()`.
- **Offline parity** — every networked system falls back to a local path, so single-player runs
  through the same code without a live session.

### Backend services
Unity Gaming Services provides anonymous authentication and **Relay**-brokered connectivity —
players host or join with a short code, with no port forwarding or dedicated server. The lobby
(username registration, roster, join announcements, host-gated start) runs on the netcode layer
before the gameplay scene loads.

---

## Features

**Survival** — health/hunger with a lock-on-eat model, night-time starvation damage, health-scaled
movement speed, bandages, downed-and-revive with bleed-out, spectator mode, fall recovery.

**Monster AI** — two distinct hunters. A roaming **stalker** with deliberately imperfect knowledge:
sight (radius + view cone + line-of-sight raycasts), hearing (sprinting is loud, walking is quiet,
standing still is silent), and scent-based pursuit, moving through Wander → Hunt → Search → lose-scent
states. A territorial **compound watcher** that hunts by sound alone inside its own zone. Plus a
jumpscare director and a false-cue system that plays red-herring growls, so audio can never be fully
trusted.

**Crafting & items** — a rebuildable crafting bench with drag-and-drop inventory: torch (blinds and
distracts), stun syrup, throwable stone distractor, respawn potion, and a river-water bucket used as
a ritual offering.

**World systems** — procedural forest scatter, day/night lighting with shrinking daylight, weather
that muffles noise in *both* directions, a nightly random **omen** system (blood moon, watched safe
house, downpour, restless dead), synced doors, a safe house that monsters cannot enter, and a
procedural river with real current, buoyancy, and Perlin-displaced water.

**Presentation** — animated splash and menu, intro cutscene, difficulty and night-count selection,
death/victory cinematics, world-space nameplates, proximity heartbeat/scream stings, and
positional footstep audio.

---

## Engineering Highlights

- **Deterministic-by-seed world sync** — replaced per-object position replication with one shared
  integer, eliminating an entire class of desync bugs and the bandwidth that came with it.
- **Imperfect-knowledge AI** — sensing built from sight cones, LOS raycasts, and a player-noise
  model rather than direct position access, so the monster can genuinely lose you.
- **Custom editor tooling** — a 24-command scene-builder suite that automates model placement,
  collider generation, URP material rebuilding, animator construction, and navmesh setup;
  idempotent and re-runnable.
- **Procedural water** — runtime mesh generation with two-octave Perlin displacement, a trigger-driven
  current, and buoyancy that handles both `CharacterController` and `Rigidbody` bodies cleanly.
- **Runtime NavMesh baking** from physics colliders, with carving obstacles so swinging doors open
  and close paths for the AI.
- **Debugging under constraint** — several fixes required reasoning about Unity's frame order
  (Animator writes land between `Update` and `LateUpdate`) and about editor-vs-build serialization
  differences that only surfaced in shipped builds.

---

## Repository Layout

```
src/
├── Core/      # game clock, shared inventory, escape tracking, omens, difficulty   (9 scripts)
├── Player/    # movement, stats, interaction, climbing, per-player items          (23 scripts)
├── World/     # spawners, crafting, ritual, radio tower, river, doors, weather    (48 scripts)
├── AI/        # stalker, compound watcher, jumpscare director, alert bus           (5 scripts)
├── Net/       # relay bootstrap, lobby, session state, per-player ownership        (6 scripts)
├── UI/        # menus, inventory, HUD, cutscenes                                   (8 scripts)
├── Audio/     # mixer-routed audio manager and settings                            (4 scripts)
└── Editor/    # scene-building and asset-pipeline automation                       (2 scripts)

media/         # screenshots and gameplay video
```

### Suggested reading order

| File | Why it's worth a look |
|---|---|
| `Core/GameClock.cs` | Server-authoritative time; the event backbone the whole game hangs off |
| `AI/MonsterAI.cs` | The imperfect-knowledge stalker — sight, hearing, scent, state machine |
| `Core/Inventory.cs` | Shared world state: seeds, synced node consumption, drop registries |
| `Net/NetworkBootstrap.cs` | Relay allocation, authentication, host/join flow |
| `World/RiverFlowController.cs` | Procedural water mesh, Perlin displacement, current volume |
| `Editor/SunsetSceneBuilder.cs` | The editor automation suite |

---

## Credits

The full game additionally uses licensed third-party art, audio and animation (Unity Asset Store packs and Mixamo animations), which are not redistributed in this repository.
