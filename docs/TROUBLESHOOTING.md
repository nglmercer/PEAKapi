# Troubleshooting

## The plugin does not load

**There is no `BepInEx/LogOutput.log`.**

That means the BepInEx loader never ran, not that the plugin failed. The
plugin is not even loaded yet in that case.

* Is `winhttp.dll` next to `PEAK.exe`? The BepInEx pack has to be unpacked
  into the game folder, not into a subfolder.
* Is `doorstop_config.ini` next to it as well?
* On Linux and Proton, Proton ships its own `winhttp.dll`, which wins over
  the copy in the game folder. Add the registry override described in
  [INSTALLATION.md](INSTALLATION.md):

  ```reg
  [Software\\Wine\\DllOverrides]
  "winhttp"="native"
  ```

  in `~/.local/share/Steam/steamapps/compatdata/3527290/pfx/user.reg`.

**The log exists but does not mention PEAKapi.**

The loader ran but the plugin was not found. Check that all three files are
in `BepInEx/plugins/`: `com.github.nglmercer.PEAKapi.dll`,
`PeakRestApi.Core.dll` and, if the game does not provide it,
`Newtonsoft.Json.dll`.

**The log mentions an exception while loading PEAKapi.**

The `Newtonsoft.Json` version in the plugin folder is probably different from
the one the game loaded. Delete the copy from `BepInEx/plugins/` and let the
game's own copy be used.

## The server is not reachable

```bash
curl -sS -m 5 http://127.0.0.1:17454/health
```

* `Connection refused` means the server is not running. Check the log for
  `could not bind`. Another process is probably using port 17454; change
  `Port` in the configuration and tell your client about it.
* A `401` means the server **is** running and the token is missing or wrong.
  That is the expected answer for a request without a token.

## The token does not work

The token is the 44 character Base64 value in
`BepInEx/config/org.peak.rest-api.cfg`. When you read the file with a shell
command, the trailing `=` is easy to lose, and the file uses Windows line
endings:

```bash
TOKEN=$(sed -n 's/^AuthenticationToken[[:space:]]*=[[:space:]]*//p' \
  "$CFG" | tr -d '\r\n')
```

`cut -d= -f2` is **wrong** here: it drops the padding of the token.

## The requests time out

A request that reaches the server but never answers means the Unity main
thread is not running the queue. That happens when the game is not in a
level, or when the game is shutting down.

* `504 COMMAND_TIMEOUT` means the command expired before it ran. No action
  was performed.
* The client should also look at `gameplayReady` in `/health`. A reachable
  server outside of a level is not a broken server.

## The game crashes in `EOS_Initialize` on Linux (not PEAKapi)

The crash handler writes a folder per crash under
`compatdata/3527290/pfx/drive_c/users/steamuser/AppData/Local/Temp/LandCrab/PEAK/Crashes/`.
If `output_log.txt` ends with this stack, the crash is in the game's own
Epic Online Services initialisation, before any gameplay:

```
Epic.OnlineServices.Bindings:EOS_Initialize
Portningsbolaget.EOSCommon.EpicOnlineService:.ctor
Portningsbolaget.Platforms.UserManager:Initialize
GameHandler:Initialize
GameBooter:AutoBoot
```

This was verified on 2026-10-19/20 by A/B launches: the game crashes with
the identical stack with PEAKapi removed, and again with BepInEx fully
disabled (`enabled = false` in `doorstop_config.ini`). No PEAKapi or
BepInEx frame appears in any crash dump. The fault is inside
`EOSSDK-Win64-Shipping.dll` running under Proton, so no PEAKapi update can
fix it. Things worth trying, in order:

1. Switch the Proton version for PEAK (Steam → Properties → Compatibility):
   try Proton 9/10 stable or GE-Proton instead of Proton Experimental.
2. Verify the game files (Steam → Properties → Installed Files).
3. Disable the Steam Overlay for PEAK.
4. If it still crashes, report it against PEAK/EOS on Proton with the
   newest `Crashes/Crash_*/output_log.txt` attached; mention EOS SDK
   1.19.1.2 and that the game crashes in `EOS_Initialize` on a vanilla
   install.

While the game is in this state the REST server answers for a few seconds
and then disappears with the game; see the next section.

## The game closes right after it starts on Linux

PEAK uses Steamworks.NET. Started outside Steam it asks Steam to restart it
and shuts down again. Start the game from the Steam client.

The message in the game's `Player.log` is:

```
[Steamworks.NET] Shutting down because RestartAppIfNecessary returned true. Steam will restart the application.
```

This has nothing to do with PEAKapi: it also happens with the BepInEx loader
removed. While the game is in that restart loop the REST server is up for a
few seconds and then disappears with the game. A request that arrives in that
window is answered with `504 COMMAND_TIMEOUT`, because the game is not
running its main loop yet.

## A spawn does nothing

Check the order of the answers:

1. `/health` has to report `gameplayReady: true`.
2. `/api/capabilities` has to report the feature you want as `available`.
   When it does not, the `reason` says why.
3. The catalog has to contain the id you send. A stale id from an older
   session is a `404`.

## A spawn is refused with 409

* `HOST_REQUIRED` means the local peer is not the room host. Only the host
  may change the world.
* `GAME_NOT_READY` means no level is loaded.
* `GAME_SESSION_CHANGED` means the scene or the room changed while the
  command was queued. Read the new state and retry with a **new**
  idempotency key.
* `QUOTA_EXCEEDED` means the cap for that type is reached. Despawn or clear
  the existing objects first.

## A spawn is refused with 422

`NO_SAFE_POSITION` means the resolver could not find a position that is
neither occupied nor too close to a player. This is more likely in a small
room with players in it. Move the players or use a different position mode.

## An entity or hazard is refused with 501

`UNSUPPORTED_ACTION` with a reason means the running game does not have what
the action needs. The catalog shows the same reason on the entry. A
discovered entity that PEAKapi has no adapter for is listed with
`spawnable: false`.

## The game lags when a lot of events arrive

Lower `MaxRequestsPerSecond` and `CommandsPerFrame` in the configuration, and
raise the cooldowns in the automation profile. The server side caps hold
either way, but a client that floods the queue gets `503` answers.

## The connection to PEAKapi fails in TikTools

* The token import needs the file in `BepInEx/config/`. If the game has never
  been started, the file does not exist yet.
* The import only reads the configuration of a candidate that you picked. It
  never scans the file system on its own.
* The address has to be the one the server binds to. The import reads `Host`
  and `Port` from the configuration and builds the URL from them.
