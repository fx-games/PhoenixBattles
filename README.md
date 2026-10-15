# Phoenix Battles

Two-player tactical battles in Phoenix Point, same idea as OXCE-Battles: pick a squad, fight one map, nothing else.
No campaign, no geoscape, no base management, no intro videos.

## How it works

The game is Unity 2019.4 with the Mono scripting backend, so we do not port anything. A C# mod
(`mod/`) is injected into the unmodified game with [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop)
and patches it at runtime with [Harmony](https://github.com/pardeike/Harmony). The official in-game mod loader is
disabled on Epic builds, hence Doorstop.

One mod DLL serves Mac and Windows. Only the loader differs:

| | Mac | Windows |
|---|---|---|
| loader | `launch/libdoorstop.dylib` via `DYLD_INSERT_LIBRARIES` (`launch/play-mac.sh`) | `launch/win/winhttp.dll` + `doorstop_config.ini` next to the game exe |
| managed dir | `PhoenixPointOSX.app/Contents/Resources/Data/Managed` | `PhoenixPointWin64_Data/Managed` |

Both builds must be the same game version (currently 1.30.3 "Firebird") for cross-play, since the mod references
`Assembly-CSharp` and the netcode will exchange save state.

What the mod does (`mod/*.cs`):

- `Entrypoint.cs`: Doorstop entry. Harmony patching is deferred until `Assembly-CSharp` is loaded; patching
  earlier breaks Unity internal calls.
- `Arena.cs`: skips the boot intro level and the ~55 s home screen cutscene, hides the campaign buttons and the
  DLC ad on the main menu, adds a small IMGUI "PHOENIX BATTLES" window (IMGUI is only used for this setup menu
  and the FIGHT button). `PB_AUTO=dump|default|battle|battle-endturn|squad` drives it from the shell for tests.
- `Squad.cs`: squad editing uses the game's own Personnel/equip screen. That screen only exists in the geoscape, so
  "Edit squad" starts a throwaway new game with intro and tutorial off, fills the Phoenix stores with 3 of every
  item, tops the roster up to 6 soldiers, hides the campaign chrome and opens `UIStateEditSoldier`. FIGHT turns the
  soldiers into deploy data (`GeoCharacter.GenerateActorDeployData`) and starts the battle.
- `Battle.cs`: builds a runtime `CustomMissionTypeDef` (two human factions, wipe-enemy objective), loads a plot the
  way the console `loadmap` command does, splits the plot's deploy zones into two halves (one per side), and sets
  both factions to `FactionControlType.Human` so the game runs it as hotseat.
- `Net.cs`: online play (see below).

## Online play

`server/` is a Go relay (TCP, newline-delimited JSON; the same protocol as OXCE-Battles' dev transport, trimmed:
no web UI, no WebSocket). `go build` in `server/`, run `./server -addr :8378`. There are no accounts and no lobby:
both players type the same room code into the in-game menu; the first join creates the room, the second one seats
the opponent (`{"t":"join","room":..,"key":..}`, `hello` -> `welcome{seat}`, `equip`, `loadout` -> `start`,
`act`/`state` forwarded to the other seat, `result` -> `over`).

The game is not deterministic (UnityEngine.Random everywhere), so turns are not replayed from inputs. Instead the
whole battle is handed over as a save at every turn change:

1. `equip`: both sides open the squad editor; FIGHT serializes the squad (`ActorDeployData[]` through the game's own
   save serializer, base64) and sends `loadout`. On `start` both sides build the identical battle from both loadouts
   and the relay's seed. Seat 0 is Phoenix (Player), seat 1 is New Jericho (Intruder); Phoenix moves first.
2. `TacticalFaction.PlayTurnCrt` is patched: when the *opponent's* faction is about to play, the local side writes a
   tactical save (`Level.WriteSavegame`), ships it as `state` and parks that coroutine forever. Input is dead while
   parked because no faction is playing a turn.
3. The receiving side writes the blob as a savegame record and loads it (`SavegameMetaData.GetLevelSceneBindingToLoad`
   + `FinishLevel(LoadLevelGameResult)`). The level reloads (~15 s) at the start of its own turn. AI factions
   (environment, wild beasts) run only on the side whose turn comes next, so they stay consistent.
4. Game over is detected by whichever side's simulation ends the battle (`GameOverEvent`); it sends `result`, the
   relay answers `over` to both, and the parked side returns to the menu with the verdict.

Live view of the opponent's turn: every ability the active player triggers through the UI
(`TacticalViewState.ActivateAbility`: move, shoot, throw, ...) is sent at once as `act{k:"ability", actor, def, target}`
with actors addressed by faction + `GeoUnitId`, and the parked side re-executes it on its own copy
(`ability.Activate(target)`). Vision is local, so an enemy stepping into your line of sight appears and shoots just
like an AI unit would. Hit rolls and reactions can differ on the parked copy; the save at turn end makes it exact
again. Body-part aiming is not replayed (the parked side shoots at the actor).

Known limits (lazy by design): no reconnect/resume; the runtime mission type does not survive a save, so
`TacMission.Init` rebuilds it on load; abilities activated outside the UI state path are not streamed.
Gotcha: a mid-turn save marks the current faction's actors `DoNotResetThisTurn`, so loading keeps their action
points as saved. The handover save is taken before the opponent's start-of-turn ran, so `HandoverCrt` calls
`ForceRestartTurn()` on their living actors first; without it the loaded side starts with 0 AP.

The loser: when `over` arrives mid-battle the mod sets the faction states (Won/Defeated) and calls
`Level.GameOver()`, so both sides see the normal battle summary; its continue button is routed to the main menu
(`TacticalView.GoToGeoscape` prefix).

Testing on one machine: `./run-net.sh [timeout] [log pattern]` builds a second mod copy into
`<game>/PhoenixBattles2`, starts the relay and two windowed instances (`PB_WINDOW=1`, `PB_LOG=<unity log>`) with
`PB_AUTO=net`, which auto-connects to room `test`, presses FIGHT after 30 s, plays one autopilot round per own turn
(each soldier shoots a visible enemy or walks toward them, streamed like UI actions) and then ends the turn.
The mod forces 1280x720 windowed mode when `PB_WINDOW` is set (the game's saved display options override Unity's
`-screen-fullscreen 0` flag, so the mod calls `Screen.SetResolution` itself). A verified run looks like this in
`<game>/PhoenixBattles*/PhoenixBattles.log`: `loadout N bytes` → `battle:` on both → `parked at battle start` (seat 1)
→ `turn save` / `loading opponent's save` alternating → `game over, win` + `over`. Four handovers and a verdict were
checked with 0 exceptions in both Unity logs.

## Build and run (Mac)

Requirements: the native Mac game at `/Users/Shared/Epic Games/PhoenixPoint` (override with `PP_DEST`/`PP_GAME`),
.NET SDK at `~/.dotnet`.

    ./install.sh                       # builds mod/ and copies DLLs + loader into <game>/PhoenixBattles
    "/Users/Shared/Epic Games/PhoenixPoint/PhoenixBattles/play-mac.sh"
    ./run-test.sh battle 300 'deploy:' # headless-ish smoke test: launches, waits for a log line, prints logs

Logs: `<game>/PhoenixBattles/PhoenixBattles.log` (mod) and
`~/Library/Logs/Snapshot Games Inc/Phoenix Point/Player.log` (Unity).

## Build and run (Windows)

1. Build: `dotnet build mod -c Release -p:GameManaged="<game>\PhoenixPointWin64_Data\Managed"`.
2. Copy `launch/win/winhttp.dll` and `launch/win/doorstop_config.ini` next to `PhoenixPointWin64.exe`.
3. Create `<game>\PhoenixBattles\` and copy `mod/bin/PhoenixBattles.dll` and `0Harmony.dll` into it
   (`doorstop_config.ini` points at `PhoenixBattles\PhoenixBattles.dll`).
4. Start the game normally. The mod log appears in `<game>\PhoenixBattles\PhoenixBattles.log`.

Untested so far: the Windows loader has not been run. The mod code itself has no platform-specific parts.

### Windows test task (for an agent on a Windows machine)

Goal: verify the loader and one online match, then fix what breaks and report back in this README.

1. Prerequisites: .NET SDK 8+, Go 1.22+, Phoenix Point 1.30.x (EGS or Steam) installed.
2. Build and install as above. Run the game; confirm `PhoenixBattles.log` shows `harmony patches applied` and
   `main menu up`. If Doorstop does not load, check `doorstop_config.ini` paths and that `winhttp.dll` sits next to
   the exe. The Unity log is `%USERPROFILE%\AppData\LocalLow\Snapshot Games Inc\Phoenix Point\Player.log`.
3. Hotseat smoke test: set env `PB_AUTO=squad PB_WINDOW=1`, start the game; expect `deploy:` in the mod log within
   ~90 s and 0 exceptions in Player.log.
4. Online test: port `run-net.sh` to PowerShell (it only copies the mod into a second folder, builds and starts
   `server`, and launches two instances with `PB_AUTO=net PB_WINDOW=1 PB_SERVER=127.0.0.1:8378 PB_ROOM=test`,
   the second one 20 s later). Expect alternating `autoplay:` and `net: replay` lines in both mod logs,
   `net: game over` with `PB_KILL=1`, and 0 exceptions.
5. Cross-platform: if a Mac is reachable, run the relay on one side and play one match Mac vs Windows.
6. Record results in the Status list below and commit the PowerShell script as `run-net.ps1`.

## Status

- [x] native Mac game + Doorstop + Harmony, boots to menu in ~7 s
- [x] hotseat battle: both squads deploy on opposite sides, turns alternate, viewer switches per faction
- [x] gear selection through the in-game Personnel screen (hotseat opponent currently gets a mirror of the squad)
- [x] online play: Go relay + save handover per turn (two instances on one Mac)
- [x] game-over verdict end to end online (`PB_KILL=1 ./run-net.sh` makes seat 0 wipe the enemy on turn 2: seat 0
      reports `win`, relay sends `over`, the parked seat 1 returns to the menu with "YOU LOST")
- [ ] Windows loader verified
