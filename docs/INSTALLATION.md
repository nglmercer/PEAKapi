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
{"status":"ready","apiVersion":"1.0.0","gameRunning":true,"inLevel":false,"isHost":false,"gameplayReady":false}
```

## Linux and Proton

PEAK runs through Proton on Linux. Two things are different there.

### The doorstop proxy has to win over the Proton builtin

Proton ships its own `winhttp.dll`. When the game starts, the loader finds
Proton's copy before the one in the game folder, so the BepInEx loader never
runs and no plugin is loaded. The game starts normally, which makes the
problem easy to miss: there is no error, only a missing plugin.

The fix is to let Wine prefer the copy in the game folder. In the Proton
prefix, add this to `pfx/user.reg`:

```reg
[Software\\Wine\\DllOverrides]
"winhttp"="native"
```

The prefix is `~/.local/share/Steam/steamapps/compatdata/3527290/pfx`. The
entry is created on the first start of the game, so the file may not exist
yet. Add the section above the first `[Software\\` section.

The same effect can be had with the environment variable
`WINEDLLOVERRIDES=winhttp=n`, but the environment variable has to be present
when the game process starts, which is awkward when Steam starts the game.
The registry entry works for every launch.

How to check that it worked: the file
`PEAK/BepInEx/LogOutput.log` has to exist and mention `PEAKapi`. If it does
not exist, the loader did not run.

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
