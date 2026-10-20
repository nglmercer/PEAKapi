# Linux and Proton: what was measured

This report records what was actually observed on the machine below, and what
is still not proven. It replaces the earlier, contradictory notes about the
`EOS_Initialize` crash.

Everything here was measured with the tools in `scripts/`: the loader
behaviour was read out of Wine's own loader trace (`WINEDEBUG=+loaddll`), the
game behaviour out of the game's own log and the crash handler's dump.

## The environment

| | |
|---|---|
| OS | CachyOS, Linux x86-64 |
| GPU | AMD Radeon RX 6600 (`AMD BC-250`, `RADV GFX1013`), vkd3d-proton `35.0.65535.65535` |
| Steam | native, one library at `~/.local/share/Steam` |
| Game | PEAK 2.6.b, build id `25739797`, Unity `6000.3.15f1` |
| BepInEx | 5.4.23.3 (PEAK pack 5.4.75301) |
| Doorstop | `4.4.1PEAK` (UnityDoorstop-PEAK) |
| Proton | 11.0-100 (`proton-11.0-2e-x86_64`); 7.0-6f and Experimental are also installed |
| EOS SDK | `EOSSDK-Win64-Shipping.dll` 1.19.1.2, sha256 `9c498fe31c8df1d9085dfa9c71c6d50ec6b569acdb29f1decb97be105b6341dc` |
| Doorstop proxy | `winhttp.dll` sha256 `1c8677ee9ebe604c5f83a105697ae2878b75201ded0b57eab8da37c14c2c8184`, internal name `doorstop.dll`, 82 exports |

The prefix is `~/.local/share/Steam/steamapps/compatdata/3527290/pfx`. It is
created by the first start of the game, so it may not exist on a fresh
installation.

## The loader: the proxy only wins with the right override

PEAK's `UnityPlayer.dll` imports `WINHTTP.dll`. The BepInEx loader works by
putting a proxy with that name next to `PEAK.exe`, so the proxy is loaded
instead of the real one and can start BepInEx.

Proton ships its own `winhttp.dll`, and Wine prefers a builtin DLL over a
native one that sits in the game folder. The proxy therefore has to be
preferred explicitly.

Measured with `WINEDEBUG=+loaddll`, the same game, the same prefix, only the
override changed:

| Override | What the loader did | Proxy loaded? |
|---|---|---|
| none | `Loaded L"Z:\...\PEAK\WINHTTP.dll" at 00006FFFFB760000: builtin` | no |
| `WINEDLLOVERRIDES=winhttp=b,n` | `Loaded L"Z:\...\PEAK\WINHTTP.dll" at 00006FFFFB760000: builtin` | no |
| registry `HKCU\Software\Wine\DllOverrides\winhttp=native` | `Loaded L"Z:\...\PEAK\WINHTTP.dll" at 00006FFFFD9E0000: builtin` | no |
| `WINEDLLOVERRIDES=winhttp=n` | `Loaded L"Z:\...\PEAK\WINHTTP.dll" at 0000000180000000: native` | yes |
| `WINEDLLOVERRIDES=winhttp=n,b` | `Loaded L"Z:\...\PEAK\WINHTTP.dll" at 0000000180000000: native` | yes |

Two things follow from this table, and both contradict what the documentation
used to say:

1. **The registry override does not work for this game.** The entry
   `HKCU\Software\Wine\DllOverrides\winhttp=native` was written into the
   prefix and read back successfully, and the loader still preferred the
   builtin. Editing `user.reg` is not a working configuration method here.
2. **The order of the modes matters.** `b,n` prefers the builtin and the
   proxy is never loaded. `n` and `n,b` prefer the native DLL, and the proxy
   is loaded.

The stable way to set the override is the Steam launch option, because Steam
evaluates it for every launch:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

With that launch option the environment of the game process contains
`WINEDLLOVERRIDES=winhttp=n,b` (read out of `/proc/<pid>/environ`), the proxy
is loaded as a native module, Doorstop runs, `output_log.txt` is written and
`BepInEx/LogOutput.log` names PEAKapi. Without it, none of that happens and
the game starts normally, which is what makes the problem easy to miss.

`protontricks winecfg` remains a supported alternative for people who prefer
the registry, but it is not the default and it was not measured to work for
this game.

## The `EOS_Initialize` crash

The crash was observed once, on 2026-10-19 at 20:58, with the proxy loaded
(the module list of the crash dump contains
`S:\steamapps\common\PEAK\WINHTTP.dll`). The stack, from the game's own
`output_log.txt`, was:

```
0x000000007F01583A (Mono JIT Code) Epic.OnlineServices.Bindings:EOS_Initialize
0x000000007F0114AB (Mono JIT Code) Epic.OnlineServices.Platform.PlatformInterface:Initialize
0x000000007F0104BB (Mono JIT Code) Portningsbolaget.EOSCommon.EpicOnlineService:.ctor
0x000000007F01006B (Mono JIT Code) Portningsbolaget.Platforms.UserManager:Initialize
0x000000007EF8FD4B (Mono JIT Code) GameHandler:Initialize
0x000000007B53C933 (Mono JIT Code) GameBooter:AutoBoot
```

Above the managed frames there are six frames inside
`eossdk-win64-shipping`. The crash happened directly after
`GameHandler initialized successfully!`, before any level was loaded, so no
PEAKapi code and no gameplay code was involved.

**The earlier claim that the crash also happens without BepInEx is wrong.** It
rests on treating `enabled = false` in `doorstop_config.ini` as a vanilla
baseline. It is not: Wine still loads `winhttp.dll` from the game folder
whatever the configuration file says, so the proxy is in the process and its
exports are bound in the process. The only true vanilla baseline is a game
folder without the proxy at all.

### The most likely mechanism

`EOSSDK-Win64-Shipping.dll` imports exactly two functions from
`WINHTTP.dll`:

```
WinHttpGetIEProxyConfigForCurrentUser
WinHttpGetDefaultProxyConfiguration
```

The proxy resolves every export it forwards by name against the real
`winhttp.dll` and stores the result in a table. If the real DLL cannot be
loaded, **every** entry of that table stays null and the export thunks jump to
address zero. A call to either of the two functions above then faults inside
the EOS SDK, which is exactly the observed stack.

The real DLL is loaded by `load_proxy` in
[UnityDoorstop-PEAK](https://github.com/CyR1en/UnityDoorstop-PEAK)
(`src/windows/proxy/proxy.h`): it tries `<cwd>\winhttp_alt.dll` first and falls
back to `C:\windows\system32\winhttp.dll`. Whether that fallback finds the
builtin depends on the override, and the loader trace shows the difference:

| Override | Proxy loaded | Real `winhttp.dll` loaded by `load_proxy` |
|---|---|---|
| `winhttp=n` | yes | **no** |
| `winhttp=n,b` | yes | yes (`Loaded L"C:\windows\system32\WINHTTP.dll" ... : builtin`) |

With `winhttp=n` the builtin is not a candidate, so the fallback finds
nothing, the table stays empty and every forwarded function is a jump to
null. With `winhttp=n,b` the builtin is the fallback and the forwarding works.

This is the hypothesis that fits the observations, and it is the reason
`n,b` is the recommended form and not `n`. It is described in
[the upstream issue](https://github.com/BepInEx/BepInEx/issues/1315) as a
general Proton problem with the loader DLLs.

**Status: not confirmed by a reproduction.** The crash was observed once, on
2026-10-19 at 20:58. It could not be reproduced on demand afterwards, not
with `winhttp=n,b` and not with `winhttp=n`. The matrix of the five
configurations, every one of them a fresh Steam launch, did not crash in any
of them either; see [RUNTIME_MATRIX.md](RUNTIME_MATRIX.md).

What **is** proven is the mechanism:

* The forwarding table is empty when the real `winhttp.dll` is not loaded,
  and the loader trace shows that it is not loaded with `winhttp=n`.
* The two functions the EOS SDK imports are in that table.
* A jump to address zero inside the EOS SDK faults in the EOS SDK, and the
  observed stack is exactly that: six frames inside `eossdk-win64-shipping`
  above the managed `EOS_Initialize` call.

What is **not** proven is that the run of 20:58 used the override that leaves
the table empty. The launch option of that run is not recorded, and the
prefix does not keep it.

## The native proxy, inspected

The shipped `winhttp.dll` was disassembled. It is **not** built from the
`proxygen.lua` that is in the repository today.

* The shipped binary forwards with **jump table thunks**: every export is a
  `jmpq *table[i]`. A tail jump keeps the argument registers and the stack
  exactly as the caller left them, so the forwarding is transparent for every
  signature, including the ones with more than four arguments.
* The generator in
  [`src/windows/build_tools/proxygen.lua`](https://github.com/CyR1en/UnityDoorstop-PEAK)
  of the fork still emits

  ```c
  void *exp_NAME() { return __NAME__(); }
  ```

  A call wrapper with a zero argument prototype. On the x64 Windows ABI the
  first four arguments travel in registers and would survive by accident, but
  every further argument travels on the stack and is read from the wrong
  offset, because the wrapper's own prologue moved the stack. For the
  functions in the list that take more than four arguments
  (`WinHttpSendRequest` takes nine, `WinHttpQueryHeaders` takes seven) that
  is memory corruption.

  The shipped binary was therefore built from a different generator than the
  one in the repository. **The two have to be reconciled before the fork is
  built again**, because a rebuild from the current source would replace an
  ABI-safe proxy with an unsafe one.

* The export list holds 82 names, and some of them do not belong to
  `winhttp.dll`:

  | Export | Belongs to | Resolved against Wine's `winhttp` |
  |---|---|---|
  | `GetFileVersionInfoW`, `VerQueryValueW`, `GetFileVersionInfoSizeW`, and 17 more | `version.dll` | null |
  | `Private1` | nothing | null |
  | `SvchostPushServiceGlobals` | `svchost.exe` | null |
  | the 62 `WinHttp*` names | `winhttp.dll` | resolved |

  A null entry is a jump to address zero. Nothing in the game's import table
  references those names today, so they are latent. They become a live crash
  the moment the proxy is installed as `version.dll`: the EOS SDK imports
  exactly `GetFileVersionInfoW`, `VerQueryValueW` and
  `GetFileVersionInfoSizeW` from `VERSION.dll`, and the proxy would answer
  them with null.

* The `GetProcAddress` interception is limited to the import table of
  `UnityPlayer.dll`. The EOS SDK imports `GetProcAddress` from `kernel32`
  itself, so its own lookups are not detoured. The detour forwards everything
  it does not handle to the real `GetProcAddress`.

## The runtime matrix

`scripts/peak-repro-matrix.sh` runs the five cases below, each in a fresh
Steam launch, with the logs cleared before every launch so that a stale log
can never be read as a result. The artifacts of a run are written to
`artifacts/matrix/<case>/` and the summary is in
`artifacts/matrix/REPORT.md`.

| # | Case | What it excludes |
|---|---|---|
| 1 | true vanilla | no proxy, no BepInEx, the original EOS SDK |
| 2 | proxy present, doorstop disabled | the proxy is still loaded, so this is **not** a vanilla baseline |
| 3 | proxy and BepInEx, empty plugin folder | PEAKapi is not loaded |
| 4 | proxy, BepInEx and PEAKapi | nothing, this is the shipped configuration |
| 5 | like 4 with the EOS SDK temporarily disabled | EOS is not in the game, a degraded mode |

The result of the run: **no case crashed.** The game reached the menu with
the original EOS SDK in every case, with and without the proxy and with and
without PEAKapi. The crash of 2026-10-19 could not be reproduced.

The results of the run that produced this report are in
[RUNTIME_MATRIX.md](RUNTIME_MATRIX.md).

## What is proven, what is suspected, what is disproven

**Proven**

* The game-local proxy is only loaded when the DLL override prefers the
  native DLL. Without it, BepInEx and PEAKapi never load, and the game starts
  normally.
* The game reaches the menu with the original EOS SDK when the proxy is not
  in the process. The proxy is therefore not needed for the game to work, and
  the EOS integration of the game is not broken on this Proton version.
* The `EOS_Initialize` crash needs the proxy, or at least it did not happen
  in any of the five matrix cases, and four of them had the proxy loaded.
* The registry override in `pfx/user.reg` does not make the proxy win for
  this game. The Steam launch option does.
* `WINEDLLOVERRIDES=winhttp=b,n` does not make the proxy win; `n` and `n,b`
  do.
* The shipped proxy forwards with ABI-safe jump thunks. The generator in the
  fork's repository does not.
* The EOS SDK imports two functions from `WINHTTP.dll`, and the proxy
  forwards both.
* The `version.dll` exports of the proxy resolve to null, and the EOS SDK
  imports exactly those names from `VERSION.dll`.

**Suspected**

* The `EOS_Initialize` crash is caused by the proxy's forwarding table being
  empty when the real `winhttp.dll` cannot be loaded, which turns the EOS
  SDK's proxy lookup into a jump to address zero. The mechanism fits the
  observed stack, and `winhttp=n` is the configuration in which the table is
  empty, but the crash was not reproduced on demand.
* The crash is intermittent. It happened once, in the run of 20:58, and not
  in the four matrix cases that had the proxy loaded.

**Disproven**

* That the crash needs BepInEx or PEAKapi. It happens before any level is
  loaded, and the crashing run's stack contains no managed frame of PEAKapi.
* That `enabled = false` in `doorstop_config.ini` is a vanilla baseline. The
  proxy is loaded by the loader regardless of the configuration file.
* That switching the Proton version is the fix. The loader trace shows the
  override, not the Proton version, decides whether the proxy is loaded.

**Not tested**

* That the game reaches a level and stays stable with PEAKapi on Proton.
  Needs a person to play.
* That a spawned item replicates to a second client.
* The `version.dll` route. It was not attempted, because the proxy's version
  exports resolve to null and the forwarding would be a jump to address zero.

## Reproducing this

```bash
# The loader decision, without starting the game.
cd ~/.local/share/Steam/steamapps/common/PEAK
STEAM_COMPAT_DATA_PATH=~/.local/share/Steam/steamapps/compatdata/3527290 \
WINEDEBUG=+loaddll WINEDLLOVERRIDES="winhttp=n,b" \
  ~/.local/share/Steam/steamapps/common/Proton\ 11.0/proton run PEAK.exe 2>&1 \
  | grep -i winhttp

# The whole matrix, with the artifacts.
./scripts/peak-repro-matrix.sh --wait 120 --keep-going
```

## References

* [BepInEx: Steam interoperability](https://docs.bepinex.dev/master/articles/advanced/steam_interop.html)
* [UnityDoorstop-PEAK](https://github.com/CyR1en/UnityDoorstop-PEAK)
* [BepInEx issue 1315: BepInEx and Proton](https://github.com/BepInEx/BepInEx/issues/1315)
* [PEAK BepInEx pack](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/)
