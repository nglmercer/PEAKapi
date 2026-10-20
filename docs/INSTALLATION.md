# Installation

## 1. Install BepInEx

PEAKapi needs BepInEx 5. Install the
[BepInEx pack for PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/)
(version 5.4.75301 or newer).

The pack contains a `BepInEx` folder, `doorstop_config.ini`, `winhttp.dll` and
`.doorstop_version`. Copy all of them into the folder that contains
`PEAK.exe`.

## 2. Build PEAKapi

```bash
git clone https://github.com/nglmercer/PEAKapi.git
cd PEAKapi
dotnet build -c Release
```

You need the .NET 10 SDK. The plugin itself targets .NET Standard 2.1, which
is what the PEAK modding template requires; the SDK version is only a build
requirement.

## 3. Install the plugin

Copy these files into `PEAK/BepInEx/plugins/`:

| File | From |
|---|---|
| `com.github.nglmercer.PEAKapi.dll` | `src/PeakRestApi/bin/Release/netstandard2.1/` |
| `PeakRestApi.Core.dll` | `src/PeakRestApi/bin/Release/netstandard2.1/` |
| `Newtonsoft.Json.dll` | `src/PeakRestApi/bin/Release/netstandard2.1/` |

PEAK already ships `Newtonsoft.Json` 13.0.2 in `PEAK_Data/Managed`. BepInEx
reuses the copy the game already loaded, so the shipped file is only a
fallback. Delete it if you prefer.

The `scripts/package.sh` and `scripts/package.ps1` scripts produce a zip with
exactly this layout.

On Linux the helper script does the same and keeps a backup of everything it
touches:

```bash
./scripts/peak-linux.sh install --plugin-dir src/PeakRestApi/bin/Release/netstandard2.1
```

It refuses to overwrite a `winhttp.dll` that it did not put there, and
`./scripts/peak-linux.sh restore` puts everything back.

## 4. Start PEAK once

The first start writes the configuration to
`BepInEx/config/org.peak.rest-api.cfg` and generates the bearer token. The
token is 44 characters of Base64 and is never written to the log.

```bash
# Linux, Steam
CFG="$HOME/.local/share/Steam/steamapps/common/PEAK/BepInEx/config/org.peak.rest-api.cfg"
sed -n 's/^AuthenticationToken *= *//p' "$CFG"
```

## 5. Check that it works

```bash
TOKEN=$(sed -n 's/^AuthenticationToken *= *//p' "$CFG" | tr -d '\r\n')
curl -sS -H "Authorization: Bearer $TOKEN" http://127.0.0.1:17454/health
```

Outside of a level the answer is:

```json
{"status":"ready","apiVersion":"1.0.0","gameRunning":true,"inLevel":false,"isHost":false,"gameplayReady":false,"serverAlive":true,"gameInitialized":true,"sceneLoaded":true,"hostAuthority":false,"authorityKnown":true,"eosState":"present"}
```

`status` only says that the API server answers. `gameplayReady` is the
readiness signal, and it is only true in a level, with the authority to
change the world.

## Linux and Proton

PEAK runs through Proton on Linux. Two things are different there.

### The doorstop proxy has to win over the Proton builtin

Proton ships its own `winhttp.dll`. PEAK's `UnityPlayer.dll` imports
`WINHTTP.dll`, and the BepInEx loader works by putting a proxy with that name
next to `PEAK.exe`. Without a DLL override, Wine prefers its builtin, the
proxy is never loaded, Doorstop never runs and no plugin is loaded. The game
starts normally, which makes the problem easy to miss: there is no error,
only a missing plugin.

The fix is the Steam launch option, because Steam evaluates it for every
launch. In Steam → PEAK → Properties → General → Launch Options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

The order of the modes matters. `n,b` prefers the native DLL and keeps the
builtin as a fallback; `b,n` would prefer the builtin and the proxy would
never be loaded. `n` also loads the proxy, but it leaves the proxy without a
`winhttp.dll` to forward to, which is what the
[EOS crash in the root cause report](LINUX_ROOT_CAUSE.md) is about. Use
`n,b`.

**Do not edit `pfx/user.reg` for this.** The registry entry
`HKCU\Software\Wine\DllOverrides\winhttp=native` was measured on this game:
the entry was written, read back and still did not make the proxy win. The
prefix is also rewritten when Steam updates it, so the entry is not durable.

`protontricks winecfg` remains available for people who prefer the registry,
but it is not the default and it was not measured to work for this game.

How to check that it worked: the file `PEAK/BepInEx/LogOutput.log` has to
exist and mention `PEAKapi`, and its timestamp has to be from the current
launch. An old log is not evidence. `./scripts/peak-linux.sh verify` checks
all of that and tells the different failures apart.

### The game has to be started by Steam

PEAK uses Steamworks.NET. Started outside Steam it calls
`RestartAppIfNecessary` and shuts down again after a few seconds, before the
first frame. Start the game from the Steam client.

## Updating

Replace the three files in `BepInEx/plugins/` and start the game again. The
configuration and the token are kept.

## Uninstalling

Delete the three files from `BepInEx/plugins/`. To remove everything,
including the configuration, delete `BepInEx/config/org.peak.rest-api.cfg` as
well.

On Linux, `./scripts/peak-linux.sh uninstall` removes the plugin and puts the
loader files back to the state they had before the install. It needs the
backup that `./scripts/peak-linux.sh backup` made.
