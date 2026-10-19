# REST API

Base URL: `http://127.0.0.1:17454`

Every request needs the bearer token:

```
Authorization: Bearer <token>
```

The token is in `BepInEx/config/org.peak.rest-api.cfg`, section `HTTP`, key
`AuthenticationToken`.

Every `POST` also needs an `Idempotency-Key` header with 1 to 128 characters.
The same key with the same body runs the command exactly once. The same key
with a different body is a `409 IDEMPOTENCY_CONFLICT`.

The machine readable contract is [`openapi.json`](openapi.json) and is served
from `/openapi.json`.

## Responses

A successful mutation looks like this:

```json
{
  "success": true,
  "result": {
    "spawned": [{ "id": "entity-1-3", "catalogId": "scorpion" }],
    "count": 1,
    "replicationVerified": false,
    "failures": []
  }
}
```

`replicationVerified` is `false` until a two client session proved that the
object reaches the other players. PEAKapi never claims replication it has not
seen.

A failure looks like this:

```json
{
  "success": false,
  "error": {
    "code": "HOST_REQUIRED",
    "message": "This action requires the room host.",
    "details": null
  }
}
```

## Status codes

| Code | When |
|---|---|
| 400 | The request is malformed: bad JSON, an unknown field, an invalid quantity, position or lifetime |
| 401 | The token is missing or wrong |
| 404 | The catalog entry, player or instance does not exist |
| 405 | The wrong verb; the `Allow` header says which one is right |
| 409 | The host is required, the game is not ready, the session changed, or the idempotency key conflicts |
| 413 | The body is larger than `MaxRequestBytes` |
| 415 | The body was not `application/json` |
| 422 | No safe position could be found |
| 429 | The request rate limit was exceeded |
| 500 | The command failed while it ran |
| 501 | The action is not supported by this game version or this configuration |
| 503 | The server is overloaded or shutting down |
| 504 | The command expired before it ran. No action was performed |

## GET /health

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

`gameplayReady` is the field a client should look at. It is only true when
the game is running, a level is loaded and the local peer may change the
world. A reachable server on its own is not enough.

## GET /api/version

```json
{
  "apiVersion": "1.0.0",
  "pluginVersion": "1.0.0",
  "gameVersion": "2.6.b",
  "bepInExVersion": "5.4.23.3",
  "peakLibVersion": "not installed",
  "unityVersion": "6000.3.15f1",
  "platform": "Windows/Unix"
}
```

## GET /api/capabilities

```json
{
  "features": {
    "items": { "available": true, "reason": null },
    "entities": { "available": false, "reason": "The running level does not contain a 'Mushroom Zombie'." },
    "hazards": { "available": false, "reason": "Photon is not available in this game session." },
    "effects": { "available": false, "reason": "No gameplay effect has been verified against the running game." },
    "worldPlacement": { "available": false, "reason": "..." },
    "multiplayerReplication": { "available": false, "verified": false, "reason": "..." }
  }
}
```

Every entry carries a reason when it is not available. A client should show
that reason instead of a generic failure.

## GET /api/game/state

```json
{
  "gameRunning": true,
  "inLevel": true,
  "isHost": true,
  "gameplayReady": true,
  "scene": "WilIsland",
  "sessionId": "WilIsland-a4"
}
```

## GET /api/players

```json
{
  "players": [
    { "id": "p3", "name": "Scout", "isLocal": false, "isHost": true, "isAlive": true, "isSpectating": false }
  ]
}
```

The id is opaque and only valid for the current session. It is derived from
the Photon actor number, not from the Steam id, so it does not leak anything.

## GET /api/catalog/items

```json
{
  "items": [
    { "id": "Apple", "name": "Apple", "category": "item", "origin": null, "spawnable": true, "reason": null }
  ]
}
```

The list is read from the game's own item registry, so modded items are in it
too. The id is the prefab name and is what a spawn request uses. Two prefabs
with the same name are given a `#2`, `#3` suffix so that both stay
addressable.

`?search=` filters on the id and the name, case insensitively.

## GET /api/catalog/entities and GET /api/catalog/hazards

Same shape, with the `entities` and `hazards` keys. An entry that the
running game does not contain is still listed, with `spawnable: false` and a
reason.

## GET /api/entities

The objects PEAKapi created and still owns.

```json
{
  "entities": [
    { "id": "entity-1-3", "catalogId": "scorpion", "kind": "entity", "playerId": null, "expiresAtUnixSeconds": 1789730000 }
  ]
}
```

## POST /api/spawn/item

```json
{
  "itemId": "Apple",
  "quantity": 1,
  "position": { "mode": "near_player", "playerId": "p3" }
}
```

* `near_player` hands the item to the named player. The player has to be
  alive and not a ghost.
* `random_player` hands the item to a random eligible player.
* `world_position` places the item in the world, through the game's own
  networked item path, at `x`, `y` and `z`.

The response lists the created objects in `spawned`. A partially successful
batch also fills `failures` with the reason for each object that was not
created, and `count` is the number that was.

## POST /api/spawn/entity

```json
{
  "entityId": "scorpion",
  "quantity": 1,
  "position": { "mode": "random_player" },
  "lifetimeSeconds": 60
}
```

`lifetimeSeconds` is optional and is bounded to 1 to 300 seconds.

The entity has to exist in the running game. PEAKapi probes the running
level for it, and a kind that is not there answers `501` with the reason
rather than pretending.

## POST /api/spawn/hazard

```json
{
  "hazardId": "tornado",
  "quantity": 1,
  "position": { "mode": "random_player" },
  "lifetimeSeconds": 30
}
```

## POST /api/player/effect

```json
{ "effectId": "heal", "playerId": "p3" }
```

No effect has been verified against the running game, so this endpoint always
answers `501 UNSUPPORTED_ACTION`. It is part of the contract so that a client
can discover the situation instead of guessing.

## POST /api/entities/despawn

```json
{ "instanceId": "entity-1-3" }
```

Only objects that PEAKapi created can be despawned. An unknown id is a
`404`.

## POST /api/entities/clear

```json
{ "kind": "all" }
```

`kind` is optional and is one of `all`, `entity` or `hazard`. Objects of the
game and of other mods are never removed.

## The strict request rules

* The body has to be a single JSON object.
* Duplicate properties are rejected.
* Comments are rejected.
* `NaN` and `Infinity` are rejected.
* Unknown properties are rejected.
* The body may not be larger than `MaxRequestBytes`.
* Numbers that are not finite are rejected, in the body as well as in the
  position.
