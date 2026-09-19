# GorillaNextBots

**Garry's Mod-style nextbots and ragdolls for Gorilla Tag.** Spawn nextbots that hunt your
friends across the map. When one catches you, your gorilla goes limp, gets thrown the way the bot
hit you, and a kill cam replays it.

By **stridefr**.

> **Private lobbies only.** All three mods switch themselves off in public lobbies. They are
> for playing with friends who have the mod too, not for spawning things on strangers.

---

## What's in it

Three BepInEx plugins. Install all three for the full experience, or just the ones you want.

| Plugin | What it does |
|---|---|
| **NextBots** | Spawn nextbots from a panel on your wrist. They find their way to you, cut you off, jump gaps, climb ledges, and sneak up while your back is turned. Any PNG or GIF becomes a bot, and a WAV with the same name becomes its chase sound. |
| **GorillaRagdoll** | Full-body ragdoll for your gorilla. Go limp at the press of a button, tumble with real physics, and get up where you land. You can watch in first or third person in the headset, and there's a separate camera for the monitor. Other players with the mod see your ragdoll too. |
| **NextBotsRagdoll** | Connects the two. A nextbot catching you knocks you down and throws you in the direction it hit you, with a kill cam, per-bot weights and impact sounds. Needs both of the others. |

## Install

**You need:**
- Gorilla Tag on PC
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases), 64-bit (tested on 5.4.23)
- [Utilla](https://github.com/sirkingbinx/Utilla)

**Then:**
1. Download `GorillaNextBots-<version>.zip` from [Releases](../../releases).
2. Extract it into your Gorilla Tag folder, so the DLLs end up in `Gorilla Tag\BepInEx\plugins\`.
   The zip already has the right folders inside.
3. Start the game once. The mods create their folders and config files on first launch.

To update, extract the new zip over the old one; your settings are kept. To uninstall, delete the
`NextBots`, `GorillaRagdoll` and `NextBotsRagdoll` folders from `BepInEx\plugins\`.

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
| **Knockdowns** | **F5** | Pretend the nearest bot just caught you, to try it out |
| **VR camera** | Right stick | Circle around your ragdoll and move the camera up or down (third-person view) |
| | Left stick | Move the camera closer or further away |
| **Monitor camera** | Hold right mouse | Look around |
| | A / D, W / S | Circle the body, raise or lower the camera |
| | Scroll | Zoom |
| | C | Look back at your body |

Keyboard keys only work while the game window has focus, so in the headset use the controller
buttons.

### Getting caught

When a bot gets within about a metre of you:

- **You ragdoll**, and your body is thrown in the direction the bot was running. A faster bot
  throws you harder, a sprinting ambush hardest of all, and heavy bots hit harder still.
- **A kill cam** films your body flying, then swings round to the bot that got you. It appears on
  a floating screen in VR and in the corner of the monitor. A little film camera flying around
  your body shows where the shot is being taken from.
- **The bot moves on** to someone else and leaves you alone for a couple of seconds after you get
  up, so you can't be caught again the moment you stand.
- With **DEATH ON CATCH** switched on in the wrist panel, you respawn after the knockdown instead
  of getting up where you landed.

### Bot weights

After the first launch there's a file per bot in `BepInEx\plugins\NextBotsRagdoll\bots\`. Set a
bot's `weight` in kilograms: 80 is normal, and a 320 kg bot hits twice as hard as an 80 kg one.
You can also change how high it launches you, how long you stay down, and which sound it makes.
The files are reloaded when you save them, so you can tune with the game running. The
`README.txt` in that folder lists every option.

### Impact sounds (optional)

Your ragdoll can make the classic Garry's Mod body-impact sounds when it hits the ground or gets
hit by a bot. The sounds belong to Valve, so they are **not included**. If you own Garry's Mod,
copy them from your own install: after the first launch, `BepInEx\plugins\NextBotsRagdoll\sounds\README.txt`
explains exactly which files go where. Any WAVs you like work too.

## Settings

- **Bots:** the wrist panel's `TUNE` page. Speed, acceleration, ambushes, jumping, climbing,
  catch range, sound and more. In a room, the host's settings apply to everyone.
- **Ragdoll and cameras:** press **F4** on the monitor.
- **Everything:** the config files in `BepInEx\config\`: `com.stridefr.nextbots.cfg`,
  `com.stridefr.gorillaragdoll.cfg` and `com.stridefr.nextbotsragdoll.cfg`.

## Known issues

- **Multiplayer has had much less testing than single player.** Bots, ragdolls and knockdowns
  are all built to work in a lobby, but expect rough edges. Bug reports are very welcome.
- **The NextBots keyboard shortcuts (F1, F7) don't work** on the current game version. Use the
  wrist panel.
- **In the third-person VR camera, players without the mod see you floating** where your camera
  is, because in VR your camera and your networked body are the same thing.
- **The kill cam draws the scene a second time** while you're down. If your frame rate drops, lower
  `Resolution` in the kill cam settings or turn the kill cam off.

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

## Credits

Made by **stridefr**.

Inspired by Garry's Mod nextbots. Built on [BepInEx](https://github.com/BepInEx/BepInEx),
[Harmony](https://github.com/pardeike/Harmony) and [Utilla](https://github.com/sirkingbinx/Utilla).

Not affiliated with or endorsed by Another Axiom or Facepunch. Mods can break when the game
updates. Use them at your own risk, and only in private lobbies.

## License

[MIT](LICENSE). Use it, change it, and put it in your own mods; just keep the credit.
