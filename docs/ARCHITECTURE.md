# Architecture

```text
TikTok LIVE events (TikTools native Rust event stream)
       |
TikTools automation / gift matching / cooldown / command budget
       |
TikTools schema-v3 declarative plugin: peak.game
       | HTTP + Authorization: Bearer <secret>
       v
PEAKapi embedded HTTP server (localhost:17454)
       | auth -> validation -> rate-limit -> idempotency
       | -> bounded queue -> Unity main-thread dispatcher
       v
PEAK adapter (C# BepInEx 5)
       | item registry / entity registry / position resolver / lifecycle
       | host and room authority and supported Photon network spawning
       v
PEAK gameplay and remote clients (replication only when confirmed)
```

## The two projects

### `PeakRestApi.Core`

The HTTP, security and dispatch layer. It has **no** dependency on the game,
on Unity or on BepInEx, which is what makes it testable.

| Folder | What is in it |
|---|---|
| `Http/` | `RestServer`, `ApiRouter`, `AuthenticationMiddleware`, `StrictJson`, `ApiResponse` |
| `Security/` | `RateLimiter`, `IdempotencyStore`, `SpawnQuotaRegistry` |
| `Game/` | `MainThreadDispatcher`, `IGameBridge` |
| `Models/` | The DTOs, the options and the error type |

### `PeakRestApi`

The BepInEx plugin. It is the only project that knows about the game.

| Folder | What is in it |
|---|---|
| `Plugin.cs` | The BepInEx entry point and the Unity main thread pump |
| `ApiRuntime.cs` | The REST listener and the command queue |
| `Config/` | The BepInEx configuration |
| `Game/` | The game services and the adapter that implements `IGameBridge` |
| `Networking/` | The Photon authority and the Photon spawner |
| `Bridge/` | The late bound access to the game assemblies |

## The request path

1. `RestServer` accepts the connection, enforces the body size limit and
   reads the body with a deadline.
2. `AuthenticationMiddleware` compares the bearer token. The comparison is
   constant time with respect to the token contents.
3. `ApiRouter` checks the rate limit, matches the route, checks the verb and
   the content type, and parses the body with `StrictJson`.
4. The body is validated: unknown fields first, then the content of each
   field.
5. The request is handed to `IdempotencyStore`, which either starts the work
   or returns the result of the work that is already running under that key.
6. `MainThreadDispatcher` queues the work. The work is executed by the Unity
   main thread, at most `CommandsPerFrame` commands per frame.
7. `PeakGameAdapter` re-checks the session generation, the level and the
   authority right before it changes anything.

Nothing that touches Unity or Photon ever runs on the HTTP thread. The HTTP
thread only reads bytes, validates and enqueues.

## Why the game layer is reflection based

PEAK's `Assembly-CSharp` and the Photon assemblies are proprietary. They
cannot be referenced from a redistributable build and they cannot be
committed to this repository. A build of the plugin therefore cannot bind to
them at compile time.

The game layer binds to them late, through the small adapter in
`src/PeakRestApi/Bridge`:

* `GameTypes` resolves the game types and caches them. `SingletonAsset<T>`
  for example lives in `Zorro.Core.Runtime.dll` and not in
  `Assembly-CSharp`, and `Item.UIData` is a nested type.
* `GameMembers` reads and invokes members. A member that does not exist
  returns null instead of throwing, which is what lets the adapter report a
  precise reason instead of crashing.
* `SceneComponents` finds the components the level places, which is how the
  prefab name of an entity is discovered.

The alternative, a hand written reference assembly, was rejected because a
signature that does not match the real one is a runtime crash inside the
game, and this build cannot be tested against the real assemblies.

The cost is that the game layer cannot be exercised by the unit tests
directly. It is covered by a fake bridge instead, and the binding itself is
verified against the real assemblies of PEAK 2.6.b, see
[COMPATIBILITY.md](COMPATIBILITY.md).

## The dispatcher

`MainThreadDispatcher` is a bounded queue with a deadline. A command that
expires is completed with a `504` and is **never** executed, so a client that
times out cannot cause a late mutation. The work is executed on the Unity
main thread, which is the only thread that may touch game objects.

The dispatcher is the reason every request, read or write, has to wait for a
frame. A server that is reachable but does not answer within
`RequestTimeoutMs` is a game that is not running its main loop, not a broken
server.

## The game adapter

`PeakGameAdapter` implements `IGameBridge` and composes the services:

* `GameStateService` answers whether a level is loaded. The game tracks the
  airport itself in `GameUtils.m_inAirport`, and the scene name is only used
  as a fallback.
* `PlayerService` reads `Character.AllCharacters`, the list the game itself
  maintains, and turns it into session scoped player ids.
* `CatalogService` reads `SingletonAsset<ItemDatabase>.Instance.Objects`, the
  game's own item registry. Nothing is instantiated while listing.
* `ItemSpawnService` hands an item to a character with
  `GameUtils.InstantiateAndGrab`, the game's own delivery call, and places an
  item in the world with `PhotonNetwork.InstantiateItemRoom`.
* `EntitySpawnService` and `HazardSpawnService` create their objects through
  Photon's network prefab registry. The prefab name is read from the component
  the level places, for example `MushroomZombieSpawner.mushroomZombiePrefab`,
  so that the name is the one the game registered and not a guess.
* `PositionResolver` turns a position request into a world position and
  rejects the ones that are occupied or too close to a player.
* `SpawnOwnershipRegistry` remembers what PEAKapi created, so that only those
  objects are ever destroyed.

## The capability report

`/api/capabilities` is computed from the running game, not from a static
list. A feature is only reported as available when the adapter verified that
the underlying API exists and that the level contains what it needs.

This is why the report is granular. A client can tell the difference between
"the plugin is broken" and "this game version does not have a Beehive in this
level", and show the reason.

## The ownership and the cleanup

Everything PEAKapi creates is registered with an opaque instance id and the
gameplay generation it belongs to. When the scene or the room changes, the
generation increases, the entries of the old generation are dropped and the
caps are reset.

Objects that PEAKapi did not create are never touched. A despawn request for
an unknown id is a `404`, and a despawn of an object that the local peer does
not own is refused instead of destroying something that belongs to somebody
else.
