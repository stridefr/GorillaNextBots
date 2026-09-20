# NextBotsRagdoll: nextbot catches knock you down

Glue plugin between [NextBots](../NextBots/README.md) and [GorillaRagdoll](../GorillaRagdoll/README.md).
When a bot catches you, you go limp and get thrown the way the bot hit you. A kill cam films your
body flying, then turns to the bot that did it. Then you get up, or you respawn if the host has
**DEATH ON CATCH** on.

**Status: 0.3.** The single-player knockdown and kill cam have been seen working in game; multiplayer
has not been tested with a second player yet. Feature details and what comes next:
[ROADMAP.md](ROADMAP.md).

The bridge needs no changes to either mod. Delete `NextBotsRagdoll.dll` and both behave exactly as
before.

## Build

```
cd src/NextBotsRagdoll
dotnet build -c Release
```

This builds and deploys all three plugins, because the bridge compiles against the other two's
source. `-p:NoDeploy=true` skips the copy into the game folder.

## Try it

1. Private room or offline.
2. Press **F5** with the desktop window focused. The nearest bot "catches" you, or an invisible
   bot in front of you if none are spawned. This runs the real path: knockback, kill cam, sounds.
3. Look for the little film camera flying around your body, the screen in front of you (VR) and
   the picture-in-picture in the top right (monitor).

## What a catch does now

| DEATH ON CATCH (wrist panel) | Before | With the bridge |
|---|---|---|
| off (default) | haptics, nothing else | ragdoll + knockback + vignette, get up after `DownSeconds` |
| on | teleport to spawn | ragdoll + knockback + vignette, **then** respawn |


Getting up early (your ragdoll toggle, or F10 panic) still counts, so in death mode you cannot
escape the respawn by standing up.

## Folders it creates

```
BepInEx/plugins/NextBotsRagdoll/
  bots/     <NAME>.json per bot: weight, knockback, lift, tumble, downSeconds, hitSound
  sounds/   impact_soft/ impact_hard/ break/ hit/  - drop WAVs in; see its README.txt
```

Both folders get a README.txt on first run. A starter `.json` is written for every skin that
has none. Existing files are never overwritten.

## Config

`BepInEx/config/com.stridefr.nextbotsragdoll.cfg`. Delete it to pick up changed defaults.

| Section | Key | Default | |
|---|---|---|---|
| General | `Enabled` | true | off = plain NextBots catches |
| Knockback | `BaseSpeed` | 5 | m/s from any catch |
| | `SpeedTransfer` | 1 | share of the bot's speed added |
| | `Lift` | 3.5 | m/s upward |
| | `TravelBias` | 0.6 | 0 = shove, 1 = run over |
| | `Tumble` | 6 | rad/s |
| | `MaxLaunch` | 30 | m/s cap, even for heavy bots |
| Knockdown | `DownSeconds` | 4 | 0 = until you get up yourself |
| | `GetUpGrace` | 2 | seconds untargetable after getting up |
| | `DownedPlayersAreSafe` | true | host setting |
| Kill cam | `Enabled` | **false** | a second render of the scene; on for the replay |
| | `ShowCameraModel` | true | the film camera + view cone in the world |
| | `HeadsetScreen` | true | floating screen in VR |
| | `MonitorPictureInPicture` | true | top-right of the monitor |
| | `FocusDelay` | 1.2 | seconds on the body before swinging to the bot |
| | `Fov` / `ZoomFov` | 70 / 32 | |
| | `Resolution` | 512 | width, 16:9 |
| Death vignette | `Enabled` | true | red flash, colour drain, slow return |
| | `Strength` | 0.8 | never opaque in the middle, whatever this says |
| | `Wash` | 0.32 | how much covers the middle rather than the edges (restart to change) |
| | `RedSeconds` / `RecoverSeconds` | 0.3 / 2.5 | |
| Landing dust | `Enabled` | true | ring of dust, grit and a scuff where a body lands |
| | `ShowForOthers` | true | other players' landings too |
| | `MinSpeed` | 4 | m/s; slower impacts throw nothing |
| | `HardLandings` | true | landing hard as a normal gorilla puffs too |
| | `HardLandingSpeed` | 8 | m/s you must be falling when you land; 5 catches a wall jump |
| | `Amount` | 110 | puffs in the biggest burst - lower it if a landing dips VR frame rate |
| | `Opacity` | 1 | multiplies the look's own thickness |
| Impact sounds | `Enabled` | true | |
| | `Volume` | 0.9 | |
| | `SoftSpeed` / `HardSpeed` / `BreakSpeed` | 1.5 / 5 / 11 | m/s thresholds |
| | `ShareWithOthers` | true | send your ragdoll's impacts to the lobby |
| | `HearOthers` | true | play other players' impacts, hits and deaths |
| | `DeathSound` | EveryCatch | `EveryCatch`, `KilledOnly` (DEATH ON CATCH) or `Off` |
| | `ImportFromSourceGames` | true | fill empty sound folders from your own GMod/HL2 install |
| Testing | `TestCatchKey` | F5 | |
| | `TestBotSpeed` | 8 | m/s |

Knockback settings and bot files are per player: each player's own copy is used when they are
caught.

## How the direction is worked out

The knockback blends **away** (from the bot's centre through yours: a shove) with **travel** (the
way the bot was running: getting run over), mixed by `TravelBias`. Speed is
`(BaseSpeed + botSpeed × SpeedTransfer) × weight scale`, plus `Lift`, capped at `MaxLaunch`. The
torso also gets `Tumble` spin that tips it over in the direction of the hit. The launch is added
on top of the velocity GorillaRagdoll already gives the body when you collapse.

The bot's position and velocity come from NextBots' catch message, measured by the host at the
moment of the catch, so every player is thrown exactly as the host saw the hit.

## Bots leave downed players alone

With `DownedPlayersAreSafe` on, bots drop someone they have knocked down, and ignore them for
`GetUpGrace` seconds after they get up. This works through NextBots' existing
`WorldView.Excluded`, and only the host's copy matters. The host tracks its guests by listening
to GorillaRagdoll's own traffic: code 150 means someone is down, code 151 means they got up.

## Checking it in game

Look in `BepInEx/LogOutput.log` for:

- `NextBotsRagdoll 0.2.0 | catches now ragdoll you (enabled)`: the patch is installed.
- `[Sound] impact_soft=5 impact_hard=6 ...`: how many WAVs each set loaded.
- `[Sound] imported 23 sounds ... from Garry's Mod`: the empty folders were filled from your install.
- `[KillCam] built | lens 512x288 from '<camera>'`: the kill cam object exists. `lens NONE`
  means no camera to clone; the model still shows but the screen is blank.
- `[Bridge] knocked down by 'X' | 300 kg (x1.94) | bot 6.0 m/s | launch 22.4 m/s`
- `[Bridge] test catch (F5) by 'X', sent to the lobby`: the test key, and the rest of the room
  was told - their death logs should show the same row.
- `[Bots] profile 'X': 300 kg -> hits x1.94`: a bot file was read.
- `[Bridge] could not ragdoll (...)`: GorillaRagdoll refused. The reason is in the brackets,
  and the catch fell back to NextBots' own behaviour.

## Layout

```
Plugin.cs           BepInEx entry
KnockdownBridge.cs  NextBots catch hooks, launch, knockdown timer, respawn, host-side exclusion, F5
KillCam.cs          the kill cam object: film-camera model, lens, VR screen, monitor PiP (off by default)
DeathVignette.cs    the red flash, the colour draining, and the world coming back
Daze.cs             muffled hearing (low-pass on the audio listener) and a generated ear-ringing tone
ViewCameras.cs      which cameras are the player's own view, for effects drawn in front of it
ScreenSaturation.cs adds a full-screen pass to the game's URP renderers and drives the shader
Shaders/            the shader source, its compiled AssetBundle (embedded in the DLL), and how to rebuild it
Dust.cs             the landing dust: ring, grit, crack of fine dust, scuff marks
HardLanding.cs      landing hard on your feet: a fall that stops suddenly, read off the player's rigidbody
DustLook.cs         dust.json - the look, in the same file the design page writes
BotProfiles.cs      per-bot weight files
ImpactSounds.cs     Source-style sound sets, the listener on each ragdoll part, sharing (event 152)
SourceSoundImport.cs  fills empty sound folders from the player's own GMod/HL2 install
BridgeConfig.cs     the knobs
ROADMAP.md          every feature, built and planned, in detail
```
