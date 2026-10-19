# Testing

## What can be tested without the game

The whole `PeakRestApi.Core` project has no game dependency, so the HTTP
surface, the security layer and the dispatcher are covered by unit tests that
run everywhere.

```bash
dotnet test
```

The suite uses a fake game bridge, so the router is exercised exactly as it
is in the game, only without one. The dispatcher is driven the same way the
game drives it, by pumping it from the test.

| Area | What is covered |
|---|---|
| Authentication | A missing token, a wrong token, a malformed header, a strong generated token, redaction |
| Routing | The known routes, an unknown route, the wrong verb and the `Allow` header |
| Strict JSON | Duplicate properties, comments, `NaN`, an unknown field, an oversized body |
| Validation | An invalid quantity, an invalid position mode, an invalid lifetime, a missing required field |
| Idempotency | The same key runs once, the same key with another body is a conflict, concurrent duplicates share the result, a missing key is refused |
| Session safety | A session change while a command is queued rejects the command and does not run it |
| Readiness | Not in a level, not the host, an unsupported action |
| Rate limit | The sliding window |
| Dispatcher | A command that expires never runs, a full queue is refused, work after shutdown is refused, the work runs when it is pumped |
| Quotas | The global and the per type caps |
| The HTTP server | A real request over a real socket, the 401 with the `WWW-Authenticate` header, a second start is refused |
| The contract | The embedded `openapi.json` is valid and describes the routes |

## The game side

The game layer is covered in two ways.

**Against the real assemblies.** The API surface that the adapter binds to
was read out of the shipped PEAK 2.6.b assemblies and is listed in
[COMPATIBILITY.md](COMPATIBILITY.md). The adapter was written against those
exact names.

**Against a fake bridge.** The behaviour of the router, of the validation and
of the error codes is covered by the unit tests above.

What is **not** covered automatically is the behaviour of the game itself:
that an item really arrives, that a zombie really chases, that a second
client really sees the object. Those need a running game.

## The manual checklist

Everything below has to be done in a running game. Nothing in this list may be
marked as passed without an observation.

| # | Check | Expected | Result |
|---|---|---|---|
| 1 | Start PEAK with BepInEx and PEAKapi | `BepInEx/LogOutput.log` mentions PEAKapi, the listener is only on the configured interface | **pending** |
| 2 | Ask for `/health` in the menu | `gameplayReady` is `false` | **pending** |
| 3 | Enter a private level as the host | `isHost` is `true`, `gameplayReady` is `true` | **pending** |
| 4 | `GET /api/catalog/items` | The real items are listed, including a modded one if one is installed | **pending** |
| 5 | `POST /api/spawn/item` with a real id | The item appears, it can be picked up and used | **pending** |
| 6 | Join a second client | The item is visible to both, the authority stays with the host | **pending** |
| 7 | `POST /api/spawn/entity` for every spawnable entity | The entity appears, its AI starts, it collides | **pending** |
| 8 | `POST /api/spawn/hazard` for every spawnable hazard | The hazard appears, it behaves, it expires | **pending** |
| 9 | A mutation from a client that is not the host | `409 HOST_REQUIRED` | **pending** |
| 10 | A burst of requests at once | The caps hold, the frame rate holds, nothing is spawned twice | **pending** |
| 11 | A target that disconnects | The stale id is refused, the pending work does not jump to another player | **pending** |
| 12 | Leave and re-enter a level | The session is invalidated, the API owned objects are cleaned up | **pending** |
| 13 | Close the game with requests pending | No hanging listener, no hanging process | **pending** |
| 14 | Repeat the relevant checks on Windows | The same behaviour | **pending** |

### How to run the checklist

`scripts/live-check.sh` starts PEAK and captures the answers of the read
endpoints:

```bash
./scripts/live-check.sh /tmp/peak-live.txt
cat /tmp/peak-live.txt
```

It does not replace the checklist. The endpoints that need a level, a host
and a second client still have to be observed by a person.

## The reference scripts

`examples/` contains runnable scripts for curl, JavaScript, Python and
PowerShell. Each of them reads the catalog first and then uses an id that
really exists, instead of a made up one.

```bash
cd examples/curl
PEAK_API_TOKEN=... ./spawn-item.sh
```

## The TikTools side

The declarative plugin and the gift wall profile are covered by the TikTools
test suite:

```bash
cd TikTools-app
bun test src/web/features/peak-giftwall.test.ts
cargo test -p tiktools-plugin-api manifest
```

The Rust test parses the manifest with the real manifest validator, so a
manifest that TikTools would refuse cannot pass.

## The environment these results come from

| | |
|---|---|
| OS | CachyOS, Linux x86-64 |
| .NET SDK | 10.0.402 |
| Game | PEAK 2.6.b, build 25739797, Unity 6000.3.15f1 |
| BepInEx | 5.4.23.3 (PEAK pack 5.4.75301) |
| Runtime | Proton Experimental |

The plugin loads and serves HTTP under Proton. The gameplay checks in the
table above are still open.
