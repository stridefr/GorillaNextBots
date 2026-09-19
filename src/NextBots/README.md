# NextBots — a Gorilla Tag BepInEx mod

Garry's-Mod-style nextbots for Gorilla Tag: entities that hunt players across the map, a
point-and-place spawn menu, and an in-VR settings panel for tuning them live.

**Status: the full single-player loop is verified working in game** — spawn, hunt, catch.
Networking is built (v0.26.0) but **not yet verified with a second client**. See
[Current status](#current-status).

> Part of [GorillaNextBots](../../README.md). Works on its own. Add
> [GorillaRagdoll](../GorillaRagdoll/README.md) and the [NextBotsRagdoll](../NextBotsRagdoll/README.md)
> bridge and a catch knocks the player down instead.

---

## Build

Requires the .NET SDK. All references come straight out of the game install, so there is
nothing to restore.

```
cd src/NextBots
dotnet build -c Release
```

The build deploys `NextBots.dll` into
`C:\SteamLibrary\steamapps\common\Gorilla Tag\BepInEx\plugins\NextBots\` automatically.
Pass `-p:NoDeploy=true` to skip that.

The game path is set once in `NextBots.csproj` (`<GameDir>`).

## Adding bots

Drop PNGs into `BepInEx/plugins/NextBots/skins/`. Each becomes a spawnable type, named after
the file. No asset bundle and no Unity project — nextbots have always been images.

With an empty folder the mod uses a generated checkerboard placeholder, so it is obvious
that no skins are installed rather than looking like an invisible-bot bug.

## Controls

| Input | Does |
|---|---|
| Left controller **Y** (secondary) | Toggle the wrist panel (configurable) |
| **F7** | Keyboard fallback for the same toggle |
| Right hand, point | Aim; a ghost shows where a bot would land |
| Panel `< PREV` / `NEXT >` | Browse spawn types or settings |
| Panel `−` / `+` | Adjust the selected setting (hold to repeat) |
| Panel `TUNE`/`SPAWN` | Switch page |
| Panel `SPAWN` / `UNDO` / `CLEAR` | Place, undo last, remove all |
| **F8** | Write a recon dump (read-only; needs `EnableRecon = true`) |
| **F9** | Recon dump *and* test a runtime NavMesh bake (same) |

**The keyboard keys (F7, F8, F9 and the F1 console) do not work on the current game version**:
NextBots still reads the keyboard through Unity's legacy input, which Gorilla Tag no longer ships.
Everything is reachable from the wrist panel. See *Hard-won facts* below.

Y is safe: QuickFreeCam binds keyboard only, GorillaShirts uses a physical in-world stand,
and GT itself only reads the face buttons to pose the avatar's thumb.

---

## Decisions taken

**NavMesh strategy — runtime bake, then steering.** Three tiers, in
[NavMeshProvider.cs](Runtime/NavMeshProvider.cs):

1. **Existing** — the map shipped a baked NavMesh. Use it.
2. **Baked** — bake one at runtime from the map's *physics colliders* in a box around the
   player, asynchronously. (Render meshes are usually not CPU-readable in a built player, so
   a render-mesh bake silently yields nothing. And a synchronous bake over a map-sized volume
   stalls the frame for seconds, which in VR is not a hitch but nausea.) Re-bakes when the
   player leaves the baked volume, which is what makes rotating maps work.
3. **Steering** — [SteeringLocomotor.cs](Runtime/SteeringLocomotor.cs).
   Whisker steering with no pathfinding. Genuinely dumber — it will walk into a U-shaped
   dead end — but the brain's stuck escalation covers exactly that failure.

**Venue gate — private room, master client.** Utilla's modded-room routing broke with a game
update, so the venue is now a **private (code-locked) room where you are the master client**.
Utilla modded rooms still count when Utilla works; nothing depends on it. See
[Venue.cs](Runtime/Venue.cs).

**A public lobby is still a hard no**, checked first and with no override. Requiring master
client is not only a venue rule, it is the authority model: one client simulates.

Utilla is a *soft* BepInEx dependency, so a broken or missing Utilla cannot stop the mod
loading.

---

## Layout

```
src/NextBots/
  Plugin.cs               BepInEx entry, Utilla modded-room gate
  Brain/
    BrainTypes.cs         BotState, PlayerSnapshot, IBotBody, IWorldView
    NextBotBrain.cs       the decision ladder — pure logic, no Unity dependencies to speak of
  Config/
    Tunable.cs            described settings, so the panel browses them generically
    NextBotSettings.cs    every number the brain reads, + network serialisation
  Runtime/
    WorldView.cs          rig sweep, derived velocities, sightline raycasts
    NavMeshProvider.cs    existing / runtime-bake / steering
    SteeringLocomotor.cs  tier-3 movement
    NextBot.cs            one bot; IBotBody over GameAgent, NavMeshAgent or steering
    BotVisual.cs          the billboard
    BotSkins.cs           PNG loading
  UI/
    HandTips.cs           fingertips from GT's own hand trigger colliders
    PanelButton.cs        swept segment-vs-AABB press test, hold-to-repeat
    UiResources.cs        shader/font resolution that survives asset stripping
    NextBotMenu.cs        the wrist panel
    SpawnAimer.cs         aim ray + ghost preview
    IBotDirector.cs       the narrow surface the menu drives
  Debugging/
    BotDebugVisual.cs     real NavMeshPath.corners, destination, target line, state colour
  Recon/
    ReconDump.cs          read-only diagnostics (F8) + bake test (F9)
```

The brain talks only to `IBotBody` and `IWorldView`, so it is identical whether a bot is a
real GT `GameAgent`, a NavMeshAgent we own, or a steered billboard — and it can be exercised
without a running game.

---

## Current status

### Verified working in game (v0.4.2)

Confirmed from the BepInEx log during live testing, not assumed:

- Plugin loads and self-starts (does **not** depend on Utilla — see below)
- Venue gate: `private NJNHHBBHB (master) | spawning: allowed`
- Panel opens, four-button browser, config page
- Runtime NavMesh bake: `collected 314 collider sources … 3241 vertices` in 0.03 s,
  and re-bakes correctly when the player leaves the volume
- Spawn: `[Bots] spawned 'PLACEHOLDER' … (1 live, nav=Baked)`
- Undo / clear: `[Bots] cleared 1`
- **Catch: `[Catch] caught by 'PLACEHOLDER'`** — target selection, pathing, contact and
  the respawn hook all working

### Networking — built, untested with two clients

**Option B**: our own Photon `RaiseEvent` channel, codes 140–145 (Option A is closed —
see below). The master client is the only simulation; everyone else holds puppets.

| What | How it reaches guests |
| --- | --- |
| Spawn / undo / clear | Reliable spawn and despawn events. Skin sent **by name**, so guests' skins folders need the same file, not the same order |
| Movement | Host broadcasts every bot's position at 10 Hz; guests dead-reckon between packets. Speed, acceleration, jumping etc. therefore match automatically — a guest never simulates |
| Settings | Sent on join, and again within 0.5 s of any change on the host. Guests cannot edit (`HOST CONTROLS SETTINGS`); their own values are restored when they leave |
| Late joiners | Ask the host 1.5 s after joining; host re-sends every bot plus settings |
| Host leaves | Everyone drops that host's bots (nobody is driving them any more) |
| Catch | Host detects it and tells only the caught player, whose own client runs the effect |
| Sound | Local to each listener: guests run occlusion and distance themselves |

Log lines to check when testing: `[Net] settings changed; sent to the lobby.` (host),
`[Net] applied N synced settings from the host.` and `[Bots] remote bot N ('X') appeared.`
(guest).

### Hard-won facts from live testing

Things that cost a debugging cycle each, recorded so they are not rediscovered:

- **Option A is closed for stock maps.** All 37 zones have a `GameEntityManager`, but only
  3 (`customMaps`, `ghostReactor`, `ghostReactorDrill`) have a non-null `gameAgentManager`.
  Every stock zone is `<null>`, so a `GameAgent` cannot be driven there.
- **Utilla loads but never raises `GameInitialized`** on this install. Anything that waits
  for that event silently never starts. The plugin now always runs its own startup
  coroutine and treats Utilla as a bonus.
- **BepInEx persists config values and prefers the file over the code default.** Changing a
  default in source does nothing once the `.cfg` exists. Delete the file to re-default.
- **GT's face buttons only read through `ControllerInputPoller`.** Unity XR
  `InputDevices` reports `P=0 S=0` permanently on the Quest 3 via OpenVR, even while the
  poller reads `1`.
- **Legacy `UnityEngine.Input` does not work at all.** GT runs Unity's *new* Input System, so
  the first call into `Input.GetKey` throws `InvalidOperationException` rather than returning
  false. This was previously recorded here as "keyboard only works when the window has focus",
  which was wrong: **NextBots' F7/F8/F9 bindings have never fired.** Reading keys needs
  `UnityEngine.InputSystem` (`Keyboard.current[Key.F8].wasPressedThisFrame`) against
  `Unity.InputSystem.dll`. Confirmed live 2026-09-08 from a GorillaRagdoll log line; see
  [SafeInput.cs](../GorillaRagdoll/Runtime/SafeInput.cs) for a backend-probing implementation.
  (Window focus is still required on top of that.)
- **TMP `fontSize` is not world units.** Measured `line height at size 1 = 0.1200`, so
  naive sizing renders text at 12% of the requested size. `PanelText` now calibrates
  against the loaded font asset.
- **A narrow NavMesh bake layer mask collects almost nothing.** Driving `NavMeshBuilder`
  directly and counting sources is what made this diagnosable.

### Known constraints to design around

- Entity creation is capped at **100 per type per manager per session**, and the counter does
  not decrement on destroy. (Only relevant if Option A is ever revisited.)
- `MonkeAgent` is GT's anti-cheat and rate-limits RPCs (`rpcCallLimit = 50`). The bot
  channel stays well under it: one state packet for all bots at 10 Hz, settings at most
  twice a second.
- `GameEntity.Init` iterates `builtInEntities` without a null check. A runtime-built prefab
  must assign an empty list or it throws.
