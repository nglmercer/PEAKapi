# Changelog

All notable changes to PEAKapi are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

* `scripts/peak-linux.sh`, a helper for the Linux and Proton setup. It finds
  the Steam library, the game and the Proton prefix, checks the installation,
  writes a redacted diagnostic report, keeps a backup of every file it
  touches and can put the game folder back. Every mutating command supports
  `--dry-run`.
* `scripts/peak-repro-matrix.sh`, the five way loader matrix. Each case is a
  fresh Steam launch with the logs cleared beforehand, and the artifacts are
  written to `artifacts/matrix/`.
* `tests/shell/peak-linux-test.sh`, a test suite for the helper. It builds a
  fake Steam installation and never touches a real game.
* `docs/LINUX_ROOT_CAUSE.md`, the report of what was measured on Linux and
  Proton, with the loader traces, the disassembly of the shipped proxy and
  the parts that are still not proven.
* `/health` and `/api/game/state` now tell the reasons for the readiness
  apart: `serverAlive`, `gameInitialized`, `sceneLoaded`, `hostAuthority`,
  `authorityKnown` and `eosState`.
* An opt-in `eos-disable` and `eos-restore` for a local diagnosis. The EOS
  SDK is renamed, never deleted, and the operation is recorded in the backup
  manifest.

### Fixed

* The Linux loader setup is documented with the Steam launch option
  `WINEDLLOVERRIDES="winhttp=n,b" %command%`. The registry edit of
  `pfx/user.reg` that the documentation used to recommend was measured on
  this game and does not work: the entry is written and read back, and the
  loader still prefers the builtin.
* `gameplayReady` follows the published contract again. It is only true in a
  level, with the authority to change the world. It was true in a level even
  without the host authority.
* The host authority is not claimed before the networking layer is loaded.
  An unknown authority is not an authority.
* `gameRunning` is not hard coded to `true` any more. It reports whether the
  game's assemblies are loaded.
* A despawn that fails no longer loses the instance id. The object stays
  known under the id the client has, so it can be despawned later.
* A despawn can no longer destroy a player. An item that was handed to a
  character is not a world object and is marked as not despawnable.
* A destroy that failed is no longer reported as a success. The error is
  passed on, so the object stays known and its quota is not released.
* The entity catalog does not claim that an entity is available any more just
  because the list of candidate prefab names is not empty. The running game
  has to contain one of them.
* The shutdown does not block the Unity main thread for the whole request
  timeout any more. The command queue is released first, so the pending
  requests fail at once.
* The REST server and its cancellation source are released on shutdown and
  when the port cannot be bound.
* `RestServer.Start` refuses a second start cleanly instead of leaving the
  listener with a duplicated prefix.
* `scripts/live-check.sh` sends the `Idempotency-Key` that every mutation
  needs, and it finds the game with the helper instead of assuming a path.

### Changed

* The diagnostic report does not contain the bearer token and does not
  contain the home directory of the user.
* `status` in `/health` keeps its meaning: the API server answers. The
  readiness is `gameplayReady` and the breakdown next to it.

### Known limitations

* Multiplayer replication is not verified. The API reports
  `replicationVerified: false` and the capability report says the same.
* The gameplay behaviour of a spawn inside a level is not verified yet, see
  `docs/TESTING.md`.
* Player effects are not implemented; the endpoint answers `501`.
* The `EOS_Initialize` crash on Proton is not fixed by this release. The
  analysis is in `docs/LINUX_ROOT_CAUSE.md`. The recommended launch option
  keeps the proxy forwarding to the real `winhttp.dll`, which is the
  configuration in which the forwarding table is complete.

## [1.0.0] - 2026-10-19

### Added

* A BepInEx 5 plugin for PEAK that serves an authenticated REST API on the
  loopback interface.
* The game independent core with the HTTP server, the router, the bearer
  authentication, the strict JSON parsing, the rate limiter, the idempotency
  store, the spawn quotas and the Unity main thread dispatcher.
* The REST contract: `/health`, `/api/version`, `/api/capabilities`,
  `/api/game/state`, `/openapi.json`, `/api/players`, `/api/catalog/items`,
  `/api/catalog/entities`, `/api/catalog/hazards`, `/api/entities`,
  `/api/spawn/item`, `/api/spawn/entity`, `/api/spawn/hazard`,
  `/api/player/effect`, `/api/entities/despawn` and `/api/entities/clear`.
* An item catalog that is read from the game's own item registry, so that
  modded items are in it too.
* Item spawning that hands an item to a character with the game's own
  `GameUtils.InstantiateAndGrab` and places an item in the world with the
  game's own networked item path.
* Entity and hazard spawning through Photon's network prefab registry. The
  prefab name is read from the component the level places, so that it is the
  name the game registered and not a guess.
* A capability report that says, for every feature, whether it works and, if
  it does not, why.
* A configuration file at `BepInEx/config/org.peak.rest-api.cfg` with a
  cryptographically secure token that is generated on the first start.
* The `peak.game` declarative plugin for TikTools, with the token import, the
  connection page, the dynamic options and the actions.
* The `peak-giftwall` TikTools profile with ten example rules, all disabled
  by default.
* Unit tests for the whole core, a script that checks the OpenAPI contract
  against the implementation, packaging scripts and a CI workflow.

### Known limitations

* Multiplayer replication is not verified. The API reports
  `replicationVerified: false` and the capability report says the same.
* The gameplay behaviour of a spawn inside a level is not verified yet, see
  `docs/TESTING.md`.
* Player effects are not implemented; the endpoint answers `501`.
* Under Proton, the BepInEx loader needs the `winhttp` DLL override that is
  described in `docs/INSTALLATION.md`.

[1.0.0]: https://github.com/nglmercer/PEAKapi/releases/tag/v1.0.0
