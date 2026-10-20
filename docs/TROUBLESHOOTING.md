# Troubleshooting

## The plugin does not load

**There is no `BepInEx/LogOutput.log`.**

That means the BepInEx loader never ran, not that the plugin failed. The
plugin is not even loaded yet in that case.

* Is `winhttp.dll` next to `PEAK.exe`? The BepInEx pack has to be unpacked
  into the game folder, not into a subfolder.
* Is `doorstop_config.ini` next to it as well?
* On Linux and Proton, the launch option has to be set. Without it, Wine
  loads its own `winhttp.dll` and the proxy in the game folder is never
  loaded, so the loader never runs:

  ```
  WINEDLLOVERRIDES="winhttp=n,b" %command%
  ```

  See [INSTALLATION.md](INSTALLATION.md). Editing `pfx/user.reg` does not
  work for this game.

**The log exists but is old.**

`BepInEx/LogOutput.log` has to be from the current launch. A log from
yesterday only says that the injection worked once.
`./scripts/peak-linux.sh verify` compares the timestamp with the current
time and says so.

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

**This is not a PEAKapi problem, and it is not a plain Proton problem
either.** The full analysis, with the measurements, is in
[LINUX_ROOT_CAUSE.md](LINUX_ROOT_CAUSE.md). The short version:

* The crash happens before any level is loaded, so no PEAKapi code is
  involved. The stack contains no managed frame of PEAKapi.
* It happens when the BepInEx proxy is in the process. Without the proxy the
  same game on the same prefix initialises EOS without a problem.
* The proxy forwards the `winhttp.dll` functions by name to the real
  `winhttp.dll`. The EOS SDK imports two of them. When the real DLL cannot
  be loaded, the forwarding table is empty and the call becomes a jump to
  address zero, which faults inside the EOS SDK.
* The DLL override decides whether the real DLL is found. The override
  `winhttp=n` loads the proxy but leaves the proxy without a DLL to forward
  to. The override `winhttp=n,b` loads the proxy **and** the real DLL.

What to do:

1. Use the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`, not
   `winhttp=n` and not the registry. See [INSTALLATION.md](INSTALLATION.md).
2. Verify the game files (Steam → Properties → Installed Files).
3. Disable the Steam Overlay for PEAK.
4. If it still crashes, report it with the newest
   `Crashes/Crash_*/output_log.txt` attached; mention EOS SDK 1.19.1.2 and
   that the crash happens in `EOS_Initialize` with the BepInEx proxy
   loaded.

`./scripts/peak-linux.sh eos-disable` renames the EOS SDK for a local
diagnosis. It is a degraded mode, not a fix: online and crossplay features
can stop working, and it is never the default.

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
