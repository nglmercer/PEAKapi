# Configuration

The configuration lives in `BepInEx/config/org.peak.rest-api.cfg`. It is
created on the first start and can be edited while the game is closed.

The token is generated on the first start and is **not** printed anywhere. If
you change it, every client has to use the new value.

```ini
[HTTP]

## Start the embedded REST server.
Enabled = true

## The interface the REST server binds to. Use a literal IP address.
Host = 127.0.0.1

## The port the REST server binds to.
Port = 17454

## Require a bearer token on every request.
RequireAuth = true

## Allow binding to a non loopback address. This exposes the API to the network.
AllowRemote = false

## The bearer token. Generated on first start when left empty.
AuthenticationToken = ...

[Limits]

## Server side request rate limit.
MaxRequestsPerSecond = 20

## The largest quantity a single spawn request may ask for.
MaxSpawnQuantity = 5

## The largest number of API owned entities that may exist at once.
MaxActiveEntities = 8

## The largest number of API owned hazards that may exist at once.
MaxActiveHazards = 4

## The largest accepted request body, in bytes.
MaxRequestBytes = 16384

## The number of gameplay commands that may wait for the main thread.
QueueCapacity = 64

## How long a queued command may wait before it expires.
RequestTimeoutMs = 5000

## How many gameplay commands are executed per frame.
CommandsPerFrame = 2

[Gameplay]

## Allow item spawns.
AllowItemSpawns = true

## Allow entity spawns.
AllowEntitySpawns = true

## Allow hazard spawns.
AllowHazardSpawns = true

## Only the room host may change the world.
RequireHost = true

## Reject spawn positions that are occupied or too close to a player.
SafePlacement = true

[Diagnostics]

## Log extra diagnostics.
Verbose = false
```

## Notes on the individual settings

### Host and AllowRemote

`Host` has to be a literal IP address, not a name. `127.0.0.1` keeps the API
on the machine that runs the game. Anything else needs `AllowRemote = true`,
authentication and a token of at least 43 characters, or the plugin refuses to
start.

Exposing the API to a network means that anybody who can reach the port can
ask the game to spawn things, as long as they have the token. Do not do it on
an untrusted network. There is no TLS, so the token travels in the clear.

### The limits

The limits are enforced by PEAKapi itself and do not depend on the
automation client honouring a cooldown. A client that is misconfigured, or a
second client that connects by accident, still cannot flood the game.

`MaxRequestsPerSecond` is a sliding window over all requests, reads
included.

`MaxActiveEntities` and `MaxActiveHazards` are the number of objects that may
exist at the same time. There is a stricter per type cap on top of that: a
tornado is expensive for the game to simulate, so at most one may exist at a
time, while a dynamite pickup may exist three times.

`QueueCapacity` is the number of gameplay commands that may wait for the
Unity main thread. A request that arrives when the queue is full gets
`503 QUEUE_FULL` instead of waiting forever.

`RequestTimeoutMs` is how long a queued command may wait. When it expires the
command is **not** executed and the client receives `504 COMMAND_TIMEOUT`.

`CommandsPerFrame` bounds how much work a single frame may do, so that a
burst of requests cannot make the game stutter.

### The gameplay switches

`AllowItemSpawns`, `AllowEntitySpawns` and `AllowHazardSpawns` switch whole
groups of actions off. A disabled group answers `501 UNSUPPORTED_ACTION`, so
a client can tell the difference between "not allowed here" and "not
supported by this game version".

`RequireHost` keeps the rule that only the room host may change the world.
Turning it off lets a client in a public lobby change the world for everybody
else. Leave it on unless you know why you turned it off.

`SafePlacement` rejects a spawn that would land inside geometry or on top of
a player. It cannot be perfect, because PEAK drives its characters with a
custom controller rather than with the physics engine, but it is a cheap
second line of defence.

### Verbose

`Verbose` adds timing information to the log. It never changes the behaviour
of a request.
