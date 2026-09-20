# Roadmap: GTag in the style of Garry's Mod

The feature list for NextBots + GorillaRagdoll + this bridge. Each entry says what the feature is,
how it would work with the code that already exists, and what stands in the way.

**Built** means the code is written and compiles against the game. The knockdown and the kill cam
have been seen working in single player; the rest of the built features, and anything
multiplayer, still need testing in game.

| # | Feature | Status | Lives in |
|---|---|---|---|
| 1 | Knockdown on catch | Built (0.1) | bridge |
| 2 | Kill cam, as a real object | Built (0.2) | bridge |
| 3 | Per-bot weights | Built (0.2) | bridge |
| 4 | Impact sounds (Source) | Built (0.2) | bridge |
| 5 | Jumpscare on catch | Planned | bridge |
| 6 | Exact hit data over the network | Built (0.3) | NextBots + bridge |
| 7 | Bots bump bodies | Planned | bridge |
| 8 | Arm bracing | Planned | GorillaRagdoll |
| 9 | Survival rounds | Planned | NextBots |
| 10 | Physgun | Planned | new plugin |
| 11 | Other players hear your impacts | Built (0.3) | bridge |
| 12 | Host-synced knockback settings | Planned | bridge |
| 13 | Weight affects how a bot moves | Planned | NextBots |
| 14 | Death log (killfeed) | Built (NextBots 0.27) | NextBots |

---

## 1. Knockdown on catch (built)

A catch ragdolls you and throws you the way the bot hit you. After `DownSeconds` you get up, or
you respawn if DEATH ON CATCH is on. The throw direction blends "shoved away from the bot" with
"carried along the way it was running". While you are down, bots switch to someone else. For
`GetUpGrace` seconds after you get up, they ignore you. See [README.md](README.md).

## 2. Kill cam, as a real object (built)

**What you see.** A small film camera, built from primitives: a body, two reels, a lens barrel,
blue glass and a blinking red REC light. A yellow view cone shows what it is filming. It flies
around your body while you are down, so you can watch the shot being set up. The footage goes to:

- **a floating screen in VR**, about 0.75 m in front of you and a little below eye level, with
  "KILLED BY / KNOCKED DOWN BY \<bot\>" above it and the stats below: bot weight, hit speed,
  throw speed and a get-up countdown.
- **the top-right corner of the monitor**, with the same title and stats.

**The shot, in two beats.**

1. **The throw** (0 to `FocusDelay`, 1.2 s by default). The camera starts above where the bot
   hit you. It swings out to the side of your flight, 3 m off to the side and 1.2 m up,
   whichever side has more open space. It follows the body so the whole launch crosses the frame.
2. **The killer.** It swings round behind your body and looks past it at the bot, zooming from
   `Fov` (70) to `ZoomFov` (32) over 0.6 s. This is Team Fortress 2's freeze cam, over your own
   body's shoulder. The view cone visibly narrows as it zooms.

**How it is made.** The lens clones the headset camera, is forced mono, has URP's per-camera
data copied across, and renders into a 512×288 texture. This is the same recipe as the ragdoll
mod's monitor camera, which already works in GT. It only renders while you are down. The model
and the screen are hidden from the lens while it renders, so the camera never films itself.
A spherecast pulls the camera in front of walls and trees.

**Why a screen, not your headset view.** The ragdoll mod's VR modes already own the headset view,
and a camera that swoops and zooms right in front of your eyes makes people motion sick. The
screen is fixed to your body, not your head: looking around never moves it, while turning with
the stick or orbiting brings it along. It moves with you exactly, even while you are flying.

**To see it.** Press **F5** with the desktop window focused. That fakes a catch by the nearest
bot, or by an invisible bot in front of you if none are spawned.

**Settings:** `4. Kill cam`: `Enabled`, `ShowCameraModel`, `HeadsetScreen`,
`MonitorPictureInPicture`, `FocusDelay`, `Fov`, `ZoomFov`, `Resolution`.

**Next steps:** a slow-motion replay of the last second before the catch (record the puppet's
poses into a ring buffer, then play them back); a "screenshot" flash plus a camera-shutter sound
when it zooms in; letting the VR player grab the screen and move it.

## 3. Per-bot weights (built)

Each bot gets a file, `plugins/NextBotsRagdoll/bots/<NAME>.json`. Starter files are written
automatically for every skin the first time the bots load:

```json
{ "weight": 300, "knockback": 1.0, "lift": -1, "tumble": -1, "downSeconds": 6, "hitSound": "hit_heavy" }
```

- **`weight`** (kg, default 80): knockback scales with **√(weight / 80)**. 20 kg hits ×0.5,
  80 kg ×1, 320 kg ×2, 720 kg ×3, capped at ×4. The square root is not arbitrary: a bot twice
  as heavy delivers twice the energy, and launch speed goes with the square root of energy. It
  also stops a 1000 kg bot sending you across the map.
- Weight also sets how **deep** the hit sounds (pitch 0.75–1.3) and how hard and long the
  **controllers buzz**.
- **`knockback`**: an extra multiplier for bots that should hit harder or softer than they weigh.
- **`lift` / `tumble` / `downSeconds`**: per-bot overrides. `-1` means use the config value.
- **`hitSound`**: which sound folder plays when this bot hits you.

Files are re-read whenever they change, so you can tune with the game running. They live in the
bridge's folder, not beside the PNGs, because NextBots warns about every non-image file in its
skins folder. **Everyone in a lobby should have the same files**, because the knockback is
worked out on the caught player's own machine.

## 4. Impact sounds from Garry's Mod / Source (built)

Four folders under `plugins/NextBotsRagdoll/sounds/`, created on first run along with a README:

| Folder | Plays when | Source files |
|---|---|---|
| `impact_soft/` | body bumps something above 1.5 m/s | `physics/body/body_medium_impact_soft1-7.wav` |
| `impact_hard/` | above 5 m/s | `physics/body/body_medium_impact_hard1-6.wav` |
| `break/` | above 11 m/s | `physics/body/body_medium_break2-4.wav` |
| `hit/` | the moment a bot hits you | `physics/flesh/flesh_impact_hard1-6.wav` |

Take these from your own GMod install: open `garrysmod/sourceengine/hl2_sound_misc_dir.vpk` with
VPKEdit or GCFScape. **Do not ship Valve's sounds in a published mod.** Tell players to copy them
from their own install instead.

Sounds play the way Source plays them. Each time, one variation is picked at random (never the
same one twice in a row) and its pitch is varied by ±5%. Volume scales with impact speed. A
whole body landing plays one thud rather than eight, because sounds are spaced at least 0.06 s
apart overall and 0.12 s apart per limb. A folder with no WAVs falls back to the next one down
(hit → break → hard → soft), so a half-filled setup still plays something. Any extra folder you
make is a set a bot profile can use by name.

Sounds work for **any** ragdoll, including one you started yourself with the toggle key. Files
are read with NextBots' own WAV loader, because this game install can't decode OGG or MP3. If a
file is refused in the log, it is a compressed (ADPCM) WAV: re-export it from Audacity as 16-bit
PCM.

**Settings:** `5. Impact sounds`: `Enabled`, `Volume`, `SoftSpeed`, `HardSpeed`, `BreakSpeed`.

## 5. Jumpscare on catch (built)

`Jumpscare.cs`. At the moment of the catch the bot's own texture is placed on a quad 0.4 m in
front of the eye camera, grown from small with a little overshoot, held, then faded as the kill
cam takes over. A GIF bot scares with its animation running, at the frame delays the file asks
for.

- Reuses `BotSkins.Find(...).Texture` and `Frames`, so it needs no new art.
- Comfort settings from the start, under `7. Jumpscare`: `Enabled`, `Seconds` (0.35 default,
  capped at 1.5), `Opacity` and `Distance`. It never strobes and never blacks the view out.
- Hidden from every camera that is not your eye, the same way the death log and the kill cam's
  own model are, so it cannot appear in the kill cam picture or the monitor camera.

Not done: a per-bot scream (`bots/<NAME>.scare.wav`). The bot's hit and death sounds already
play, so this would be a third sound on top of them.

## 6. Exact hit data over the network (planned)

Today a guest only hears "you were caught", and guesses that the nearest bot did it. The fix is to
add the bot's network ID and velocity to NextBots' catch message (event 144), which means bumping
its protocol version from 2 to 3. The guest then uses the exact bot, and the kill cam frames the
right one even in a crowd. The cost is that everyone in the lobby needs the new build, since the
protocol check drops messages from other versions. This is the one feature that requires
editing NextBots itself.

Built in v0.27.0, and extended in v0.28.0 (protocol 4): a catch now also says whether it is real
or the test key, so pressing F5 shows up in everyone's death log without letting any client claim
a kill on somebody else.

## 7. Bots bump bodies (planned)

Give every bot a kinematic capsule on the ragdoll's physics layer, moved with
`Rigidbody.MovePosition` so it passes its velocity on through contact. Then a bot running through
a downed body kicks it along, and a bot chasing someone else can trip over you. This only affects
**your own** ragdoll: other players' ragdolls are drawn from network data, not simulated. The
capsule must ignore the player's own colliders, the same way the puppet does in
`RagdollPuppet.WireCollisions`.

## 8. Arm bracing (planned)

This is step 1 of the Euphoria roadmap in the GorillaRagdoll README. Each frame, cast a ray along
the torso's velocity. If an impact is less than 0.3 s away, drive the hands toward the predicted
contact point, so you visibly throw your arms out before hitting a wall. It needs per-joint target
rotations that can be steered at runtime, which is the one missing piece that roadmap names. It
builds on `ActiveMode = Assisted`, which already exists. Head protection and rolling onto your
front come nearly free after that.

## 9. Survival rounds (planned)

The classic GMod nextbot server loop:

- A round timer and a count of **how many times each player was caught**, kept by the host from
  `BotManager.OnBotCaughtPlayer`.
- Last player standing wins. Alternatively, every catch costs 10 seconds of your survival time.
- Bots get faster every minute (`NextBotSettings.Speed`, which is already synced to guests).
- The scoreboard goes on a new wrist panel page. Results reach guests on a new event code (146).
- With DEATH ON CATCH on, it is an elimination round: you respawn as a spectator in the kill cam.

## 10. Physgun (planned)

Hold the trigger to grab whatever your aim ray hits: a ragdoll limb or a bot. The grab is a
spring joint to a point along the ray. The thumbstick pushes and pulls, and releasing throws
with your hand's velocity.

- Your **own** ragdoll can be grabbed directly.
- **Other players'** ragdolls cannot, because they are drawn from network data, not simulated.
  Grabbing one would need their client to own the pull, via a request event.
- **Bots** are simulated by the host, so a guest grabbing one needs a host request too.

Best as its own plugin: it is a tool, not a bot behaviour, and a later toolgun would sit next to it.

## 11. Other players hear your impacts (planned)

Other players' ragdolls are drawn from pose packets, so no collisions happen on their machines.
But the pose stream shows sudden velocity changes: if a torso's speed drops by more than
`HardSpeed` between two snapshots, play an impact at that position. Doing it this way needs no
new network traffic.

## 12. Host-synced knockback settings (planned)

At the moment each player uses their own `BaseSpeed`, `Lift` and so on. The host could send the
bridge's settings the same way NextBots sends its own (a float array on a new event code). Then
the whole lobby is thrown by the same rules, and the host's wrist panel controls them.

## 13. Weight affects how a bot moves (planned)

Heavy bots accelerate and turn more slowly, which makes them easier to dodge but harder-hitting.
Light ones are twitchy. This needs per-bot settings in NextBots, which today has one global
`NextBotSettings` shared by every bot. The bot profile file from #3 is the natural home for it.

## 14. Death vignette (built)

`DeathVignette.cs`. A quad a hand's width in front of the eye, with a radial gradient built in
code: a thin wash over the middle that thickens to the edges. On a catch it is blood red, drains
to ash grey over 0.45 s, holds while you are down, and fades out over `RecoverSeconds` once you
are up - which reads as the colour coming back into the world.

- A veil rather than a post-process, on purpose. Adding a full-screen pass to the game's own URP
  renderer is the sort of thing that breaks on the next game update; a quad does not.
- The middle of the view is never opaque, the red does not pulse, and `Enabled = false` removes it.
- Hidden from every camera that is not your eye, like the jumpscare and the death log.

## 15. Landing dust (planned)

A ring of dust where a ragdoll hits the ground: grains thrown outwards along the floor with a flat
shockwave ring under them, swelling and thinning as they go, drifting up rather than falling back.

- The numbers are being designed outside the game first, in `tools/dust-demo`, which draws the
  same maths on a canvas with sliders and writes the result to `dust.json`.
- In game it hangs off the impact listeners that already fire the impact sounds, so the trigger,
  the hit position and the speed threshold all exist. One `ParticleSystem` built in code, pooled,
  with the hard impact threshold deciding how big the puff is.
- Everyone should see everyone's, which means either sending it with the impact sound event (152)
  or spawning it locally from the ragdoll poses other clients already receive.
