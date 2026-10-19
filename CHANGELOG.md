# Changelog

All notable changes to PEAKapi are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

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
