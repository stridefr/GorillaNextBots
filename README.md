# GorillaNextBots

**Garry's Mod-style nextbots and ragdolls for Gorilla Tag.** Spawn nextbots that hunt your
friends across the map. When one catches you, your gorilla goes limp and gets thrown the way the
bot hit you, dust flies where you land, and the colour drains out of the world until you are back
on your feet.

By **stridefr**.

> **Private lobbies only.** All three mods switch themselves off in public lobbies. They are
> for playing with friends who have the mod too, not for spawning things on strangers.

---

## What's in it

Three BepInEx plugins. Install all three for the full experience, or just the ones you want.

| Plugin | What it does |
|---|---|
| **NextBots** | Spawn nextbots from a panel on your wrist. They find their way to you, cut you off, jump gaps, climb ledges, and sneak up while your back is turned. Any PNG or GIF becomes a bot, and a WAV with the same name becomes its chase sound. A Garry's Mod-style death log shows who got caught by what, in your headset and on the monitor. |
| **GorillaRagdoll** | Full-body ragdoll for your gorilla. Go limp at the press of a button, tumble with real physics, and get up where you land. You can watch in first or third person in the headset, and there's a separate camera for the monitor. Other players with the mod see your ragdoll too. |
| **NextBotsRagdoll** | Connects the two. A nextbot catching you knocks you down and throws you in the direction it hit you, with a death vignette, landing dust, per-bot weights and Garry's Mod impact and death sounds that the whole lobby hears. There is a kill cam too, switched off by default. Needs both of the others. |

## Install

**You need:**
- Gorilla Tag on PC
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases), 64-bit (tested on 5.4.23)
- [Utilla](https://github.com/sirkingbinx/Utilla)

**The easy way:** download **`GorillaNextBotsInstaller.exe`** from
[Releases](../../releases) and run it. It finds Gorilla Tag, installs BepInEx if you have not got
it, and puts the three mods in. Run it again any time to update - it tells you whether anything is
new, and does nothing if not. Windows will warn about an unsigned program the first time: choose
*More info*, then *Run anyway*. Close Gorilla Tag before updating, or Windows will not let the
files be replaced.

**By hand:**
1. Download `GorillaNextBots-<version>.zip` from [Releases](../../releases).
2. Extract it into your Gorilla Tag folder, so the DLLs end up in `Gorilla Tag\BepInEx\plugins\`.
   The zip already has the right folders inside.
3. Start the game once. The mods create their folders and config files on first launch.

To update, extract the new zip over the old one; your settings are kept. To uninstall, delete the
`NextBots`, `GorillaRagdoll` and `NextBotsRagdoll` folders from `BepInEx\plugins\`. The installer
never touches your settings, bot images, sounds or death log style.

## Adding bots

Put images in `BepInEx\plugins\NextBots\skins\`. Each one becomes a bot named after the file:

```
skins\
  obunga.png       <- the bot
  obunga.wav       <- its chase sound (optional, must be .wav)
  sanic.gif        <- animated bots work too
```

PNG, JPG and GIF are supported. With no images in the folder you get a red checkerboard
placeholder, so you can tell straight away that the folder is empty. In multiplayer, **everyone
needs the same images**: bots are matched by file name.

## Playing

Join a **private room**, or play offline. In a room, only the room's host can spawn bots, and
everyone else sees the host's bots.

### Controls

| | Input | Does |
|---|---|---|
| **Bots** | Left controller **Y** | Open/close the wrist panel |
| | Point your right hand | Aim where to spawn; a ghost preview shows where the bot will land |
| | Panel buttons | Pick a bot, spawn, undo, clear all, and tune settings on the `TUNE` page |
| **Ragdoll** | Left controller **X** (hold for a moment) | Go ragdoll / get up |
| | **F6** | Same, from the keyboard |
| | **F4** | Settings menu on the monitor |
| | **F10** | Panic button: instantly get up and put everything back |
| **Knockdowns** | **F5** | Pretend the nearest bot just caught you, to try it out. The rest of the lobby sees it in their death log too |
| **VR camera** | Right stick | Circle around your ragdoll and move the camera up or down (third-person view) |
| | Left stick | Move the camera closer or further away |
| **Monitor camera** | Hold right mouse, drag | Circle the body, raise or lower the camera |
| | A / D, W / S | The same, from the keyboard |
| | Scroll | Zoom |
| | C | Look back at your body |
| | **O** | Switch the slow automatic orbit on or off (see below) |

Keyboard keys only work while the game window has focus, so in the headset use the controller
buttons.

### Getting caught

When a bot gets within about a metre of you:

- **You ragdoll**, and your body is thrown in the direction the bot was running. A faster bot
  throws you harder, a sprinting ambush hardest of all, and heavy bots hit harder still.
- **Your hearing goes.** A heavy hit muffles everything, as if underwater, and leaves a thin high
  whine on top, both fading over a few seconds. Heavier bots hit harder, and only the very hardest
  ragdoll impacts and landings do it as well. It is quiet by default; `[10. Impact daze]` has the
  whine's volume, how dull it gets, how long it lasts, and `Enabled` to switch it off.
- **The edges of your view flash red** and the colour drains out of everything while you are down,
  coming back over a couple of seconds once you are up. It shows in the headset and on the monitor,
  including the third-person orbit camera, so it is in a screen recording. `[8. Death vignette]` has the strength,
  how long the red holds and how slowly the colour returns.
- **Dust.** A body landing throws up a ring of dust that runs out along the floor, grit that bounces,
  a crack of fine dust at the moment of impact, and a scuff mark that fades over ten seconds or so.
  The debris is real 3D rocks that fly, hit the actual floor, walls and tables, bounce, skid and
  settle wherever they land, then shrink away; `grit` in `dust.json` is how many (up to 60 a burst),
  `gritSize` how big, and 0 turns them off.
  Landing hard on your feet does it too - dropping off a roof - not just being knocked down.
  Everyone in the lobby sees it, including other players' landings. The look is in
  `BepInEx/plugins/NextBotsRagdoll/dust.json`; the numbers under `[9. Landing dust]` in the config
  switch it off, change how hard a landing has to be, and how many puffs the biggest burst uses.
- **The camera orbits by itself**, slowly, for filming: about 10 degrees a second with a gentle drift up
  and down and a slight breathing in and out, so it reads as a camera move rather than a turntable.
  Touch the mouse, A/D/W/S or the wheel and it is yours at once; leave it alone for a couple of
  seconds and it eases back in. Speed, drift and delay are under `[6. Camera (monitor)]`, or press
  **O** to turn it off.
- **A kill cam** can film your body flying and then swing round to the bot that got you, on a
  floating screen in VR and in the corner of the monitor. It is **off by default** - it draws the
  whole scene a second time - so switch `Enabled` on under `[4. Kill cam]` if you want it.
- **The bot moves on** to someone else and leaves you alone for a couple of seconds after you get
  up, so you can't be caught again the moment you stand.
- With **DEATH ON CATCH** switched on in the wrist panel, you respawn after the knockdown instead
  of getting up where you landed.
- **Everyone hears it.** The hit and Garry's Mod's death sound play where your body is, and every
  thud as your ragdoll lands is sent to the other players, timed to when they see it land.
- **The death log** shows it to the whole lobby: the bot's name (its image file name), its
  picture, and the player it caught.

### Death log

Every catch shows up in the corner of your headset view and of the monitor, Garry's Mod style:

```
Obunga [bot picture] stridefr
```

The look and position live in `BepInEx\plugins\NextBots\killfeed.json`, which is created on first
launch. Change `corner` to `TopLeft`, `TopRight`, `BottomLeft` or `BottomRight`, or restyle it
completely: colours, a box behind each row, an outline when it's you, icon size, how long rows
stay. Saving the file updates the game within a second, no restart needed. When more catches come
in than there are rows, the oldest one fades out as it's pushed off the end.

**Fonts.** Set `font` to the name of any font installed on your PC, like `Verdana`, `Impact` or
`Segoe UI`, and `bold` to `true` or `false`. Garry's Mod's own death notices are Verdana Bold. No
fonts come with the mod: it uses the ones installed on your PC, and if a font isn't installed that
text just uses the game's default.

The monitor is fussier than the headset: it can only use fonts Windows has installed **for all
users**, which is not what you get from double-clicking a font file and pressing Install. If the
log says a font isn't installed for all users, either right-click the file and pick *Install for
all users*, or set `monitorFont` to one that is - `Verdana` is always there - and keep your own
font in the headset.

### Bot weights

After the first launch there's a file per bot in `BepInEx\plugins\NextBotsRagdoll\bots\`. Set a
bot's `weight` in kilograms: 80 is normal, and a 320 kg bot hits twice as hard as an 80 kg one.
You can also change how high it launches you, how long you stay down, and which sound it makes.
The files are reloaded when you save them, so you can tune with the game running. The
`README.txt` in that folder lists every option.

### Impact sounds

Your ragdoll makes the classic Garry's Mod body sounds when it hits things, and you hear Garry's
Mod's death sound when someone gets caught. The sounds belong to Valve, so they are **not
included**. Instead:

- **If you own Garry's Mod or Half-Life 2 on Steam**, the mod copies the exact sounds those games
  use from your own install the first time it starts. Nothing to do.
- **If you don't**, drop any WAVs you like into the folders in `BepInEx\plugins\NextBotsRagdoll\sounds\`.
  The `README.txt` in there explains which folder is for what.

Other players only hear your ragdoll if they have sounds in their own folders too.

## Settings

- **Bots:** the wrist panel's `TUNE` page. Speed, acceleration, ambushes, jumping, climbing,
  catch range, sound and more. In a room, the host's settings apply to everyone.
- **Ragdoll and cameras:** press **F4** on the monitor.
- **Fonts:** the death log's is in `killfeed.json` (above). The wrist panel's is `Font` under
  `[UI]` in `com.stridefr.nextbots.cfg`, and the F4 menu's is `MenuFont` in
  `com.stridefr.gorillaragdoll.cfg`. Use the name of a font installed on your PC; restart the
  game after changing these two.
- **Everything:** the config files in `BepInEx\config\`: `com.stridefr.nextbots.cfg`,
  `com.stridefr.gorillaragdoll.cfg` and `com.stridefr.nextbotsragdoll.cfg`.

## Known issues

- **Multiplayer has had much less testing than single player.** Bots, ragdolls and knockdowns
  are all built to work in a lobby, but expect rough edges. Bug reports are very welcome.
- **Everyone in a lobby needs the same version.** Players on different versions don't see each
  other's bots.
- **The NextBots keyboard shortcuts (F1, F7) don't work** on the current game version. Use the
  wrist panel.
- **In the third-person VR camera, players without the mod see you floating** where your camera
  is, because in VR your camera and your networked body are the same thing.
- **The kill cam draws the scene a second time** while you're down, which is why it ships switched
  off. If you turn it on and your frame rate drops, lower `Resolution` in its settings.

## Something went wrong?

- Press **F10** to get out of any ragdoll trouble.
- Look in `Gorilla Tag\BepInEx\LogOutput.log`. Each mod logs what it's doing, prefixed
  `[Bots]`, `[Ragdoll]`, `[Bridge]` and so on.
- If you open an issue, attach that log file. It usually shows exactly what went wrong.

## Building from source

You need the [.NET SDK](https://dotnet.microsoft.com/download). The projects reference DLLs
straight from your game install, so there's nothing else to download.

```
cd src/NextBotsRagdoll
dotnet build -c Release -p:GameDir="C:\path\to\Gorilla Tag"
```

That builds all three plugins and copies them into your game's `BepInEx\plugins`. Add
`-p:NoDeploy=true` to skip the copy. Each mod has its own technical README:

- [NextBots](src/NextBots/README.md): pathfinding, the bot brain, networking
- [GorillaRagdoll](src/GorillaRagdoll/README.md): how the ragdoll drives GT's own avatar, and the camera modes
- [NextBotsRagdoll](src/NextBotsRagdoll/README.md): knockdowns, kill cam, weights, sounds
  ([roadmap](src/NextBotsRagdoll/ROADMAP.md))
- [Installer](src/Installer): the one-click installer and updater, a small .NET Framework app

## Credits

Made by **stridefr**.

Inspired by Garry's Mod nextbots. Built on [BepInEx](https://github.com/BepInEx/BepInEx),
[Harmony](https://github.com/pardeike/Harmony) and [Utilla](https://github.com/sirkingbinx/Utilla).

Not affiliated with or endorsed by Another Axiom or Facepunch. Mods can break when the game
updates. Use them at your own risk, and only in private lobbies.

## License

[MIT](LICENSE). Use it, change it, and put it in your own mods; just keep the credit.
