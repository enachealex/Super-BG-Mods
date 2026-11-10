# SBG Splitscreen

Local splitscreen for Super Battle Golf: up to 4 players on one PC, in one game window, in a game you host. Online players can join the same game as usual.

- Player 1 uses keyboard, mouse and the first controller.
- Anyone with another controller presses **A** to join. They leave from their pause menu (**Leave Game**).
- Two players split top/bottom or side by side; three or four use quarters.

Built against game version 1.3.1 (Steam build 26135757).

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64, 5.4.23 or later) into the game folder and run the game once.
2. Copy `SBGSplitscreen.dll` into `<game>\BepInEx\plugins\SBGSplitscreen\`.

Settings are in `<game>\BepInEx\config\local.sbg.splitscreen.cfg` (created on first run):

| Section | Setting | Default | Meaning |
| --- | --- | --- | --- |
| General | `Enabled` | true | Turn the mod off without removing it. |
| General | `MaxPlayers` | 4 | Players on this PC (1-4). |
| Layout | `TwoPlayers` | TopBottom | Two-player layout: `TopBottom` or `SideBySide`. |
| Players | `Player2Name` ... | Player 2 ... | Names shown for joined players. |
| Display | `Monitor` | (empty) | Which monitor the game uses; also settable in Settings > Graphics > Monitor (mod). |
| Items | `PistolSpread` | 30 | In games you host: dueling pistol spread as a % of the game's (players on this PC only). |
| Items | `PistolHitArea` | 25 | In games you host: how much of the extra room around a player's body still counts as a pistol hit (100 = the game's own). Enforced by the host for online players too. |

Gameplay changes (the `Items` section) apply only while you host. When you join someone else's game the mod puts the game's own values back.

## Build

Needs the .NET SDK and a local game install with BepInEx.

```
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Super Battle Golf"
```

References come from `<GameDir>\Super Battle Golf_Data\Managed` and `<GameDir>\BepInEx\core`. The build copies the DLL into the game's plugins folder; add `-p:DeployToGame=false` to skip that.

## How it works

The game is written for exactly one local player: lots of code reads "the local player", "the camera", "the current gamepad" from static fields. Rather than rewrite that code, the mod keeps a set of those statics per player (a *context*) and swaps them in around any code that runs on behalf of a player.

- `Core/Contexts.cs`: the per-player static values and `Enter`/`Restore`.
- `Core/ContextPatcher.cs`: wraps player-owned components' methods (including coroutines, async methods and closures) so they run in their owner's context.
- `Core/Owners.cs`: works out which local player an object belongs to.
- `Net/`: extra players join the host's own Mirror server through in-process connections (`LocalPlayerConnection`), so to the server and to online clients they are ordinary players. `NetPatches.cs` routes RPCs and target RPCs to the right context.
- `Input/InputContexts.cs`: gives each player their own device and input actions.
- `View/`: cameras and viewport layout, one HUD copy per player, per-player pause menus, and fixes where shared world objects (checkpoints, homing warnings, hole intro, button prompts) showed only player 1's state.
- `ItemTweaks.cs`: the host-only pistol settings.

## Testing

`DebugCommands.cs` is only active when the game is started with `--sbg-debug`. It then reads commands from `<game>\BepInEx\SBGSplitscreen\cmd.txt` and writes a status report to `status.txt` beside it. Commands include `host`, `vpad add` / `vpad 0 press a` (virtual controllers for joining), `give`, `equip`, `pistolat`, `entercart`, `carts`, `checkpoints` and more; see the `switch` in `Run`.

Other launch flags: `--sbg-mute`, `--sbg-no-focus` (start without taking focus), `--sbg-window x,y,w,h`.

## Known limits

- Splitscreen players can only join a game hosted on this PC, not one hosted by someone else.
- Dueling pistol spread is worked out by each shooter's own game, so `PistolSpread` cannot change it for online players. `PistolHitArea` does apply to them.
