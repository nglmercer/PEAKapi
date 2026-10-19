# PEAKapi

A local, authenticated REST API for [PEAK](https://store.steampowered.com/app/3527290/PEAK/).

PEAKapi runs inside the game as a BepInEx 5 plugin. It exposes the items,
entities and hazards of the running game over a small HTTP API on the loopback
interface, so that a live stream can turn TikTok events into gameplay without
touching the game files.

* **Game side:** `src/PeakRestApi` — a BepInEx 5 plugin for PEAK.
* **Core:** `src/PeakRestApi.Core` — the HTTP, security and dispatch layer. It
  has no game dependency and is covered by the unit tests.
* **Integration:** the [`peak.game`](https://github.com/nglmercer/TikTools-app/tree/remake/examples/peak-game)
  declarative plugin for [TikTools](https://github.com/nglmercer/TikTools-app).

## Status

| Feature | State |
|---|---|
| Authenticated REST surface on the loopback interface | implemented and unit tested |
| Health, version, capability, state and player endpoints | implemented and unit tested |
| Item catalog, read from the game's own item registry | implemented, verified against PEAK 2.6.b |
| Item spawn, handed to a character or placed in the world | implemented, **live behaviour not yet verified in a level** |
| Entity spawn (Mushroom Zombie, Bee Swarm, Scorpion) | implemented, **live behaviour not yet verified in a level** |
| Hazard spawn (Dynamite, Tornado, Tumbleweed) | implemented, **live behaviour not yet verified in a level** |
| Multiplayer replication | **not verified**; the API never claims it was |
| Player effects | not implemented, the endpoint answers `501` |
| Linux and Proton | the plugin loads and serves HTTP; the gameplay gates are the same as on Windows |

Nothing in this table is marked verified on the strength of a call that
returned. A feature is only called verified after it was seen working in a
running game.

## Requirements

* PEAK 2.6.b or newer with BepInEx 5.4.23.3 or newer.
* .NET 10 SDK to build. The plugin itself targets .NET Standard 2.1, which is
  what the PEAK modding template requires.

## Build

```bash
dotnet build -c Release
dotnet test
```

The plugin is written to
`src/PeakRestApi/bin/Release/netstandard2.1/com.github.nglmercer.PEAKapi.dll`.

## Install

Copy these three files into `PEAK/BepInEx/plugins/`:

* `com.github.nglmercer.PEAKapi.dll`
* `PeakRestApi.Core.dll`
* `Newtonsoft.Json.dll` (only needed if the game does not ship it; PEAK does)

See [docs/INSTALLATION.md](docs/INSTALLATION.md) for the full instructions,
including the Proton specific steps.

## Use

```bash
# The token is in BepInEx/config/org.peak.rest-api.cfg
export PEAK_API_TOKEN=$(sed -n 's/^AuthenticationToken *= *//p' \
  "$HOME/.local/share/Steam/steamapps/common/PEAK/BepInEx/config/org.peak.rest-api.cfg")

curl -sS -H "Authorization: Bearer $PEAK_API_TOKEN" \
  http://127.0.0.1:17454/health
```

```json
{
  "status": "ready",
  "apiVersion": "1.0.0",
  "gameRunning": true,
  "inLevel": false,
  "isHost": false,
  "gameplayReady": false
}
```

A running HTTP server is not gameplay readiness. `gameplayReady` is only true
while a level is loaded, the game is not in the airport and the local peer has
the authority to change the world.

More examples live in [examples/](examples/).

## API

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Reachability and gameplay readiness |
| GET | `/api/version` | API, game, BepInEx and Unity versions |
| GET | `/api/capabilities` | What is supported, and what is not |
| GET | `/api/game/state` | Level, session and authority |
| GET | `/openapi.json` | The OpenAPI 3 contract |
| GET | `/api/players` | The players of the session |
| GET | `/api/catalog/items` | The spawnable items |
| GET | `/api/catalog/entities` | The known entities |
| GET | `/api/catalog/hazards` | The known hazards |
| GET | `/api/entities` | The objects PEAKapi created |
| POST | `/api/spawn/item` | Hand out or place an item |
| POST | `/api/spawn/entity` | Spawn an entity |
| POST | `/api/spawn/hazard` | Spawn a hazard |
| POST | `/api/player/effect` | Apply a gameplay effect (currently `501`) |
| POST | `/api/entities/despawn` | Remove one object PEAKapi created |
| POST | `/api/entities/clear` | Remove every object PEAKapi created |

Every mutation needs a bearer token, a JSON body and an `Idempotency-Key`.
The full contract is in [docs/API.md](docs/API.md) and
[docs/openapi.json](docs/openapi.json).

## Safety model

* The server binds to `127.0.0.1` unless it is explicitly told otherwise, and
  it refuses to start without a strong token.
* A 32 byte token is generated on the first start with a cryptographically
  secure random source. It is never logged and never committed.
* Every mutation is rate limited, size limited, validated and executed at
  most once per idempotency key.
* Gameplay work is queued and executed on the Unity main thread. A command
  that expires before it runs is never executed.
* PEAKapi only ever destroys objects it created itself.

## Documentation

* [docs/INSTALLATION.md](docs/INSTALLATION.md) — install, including Proton
* [docs/CONFIGURATION.md](docs/CONFIGURATION.md) — every setting
* [docs/API.md](docs/API.md) — the REST contract
* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — how it is put together
* [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) — what was verified where
* [docs/TESTING.md](docs/TESTING.md) — what was tested and what is still open
* [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — common problems

## Licence

MIT. See [LICENSE](LICENSE). The HTTP, security and dispatcher layer is
adapted from [REPOapi](https://github.com/nglmercer/REPOapi), also MIT; see
[third-party/REPOapi-NOTICE.md](third-party/REPOapi-NOTICE.md).
