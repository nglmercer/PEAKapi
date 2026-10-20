# Compatibility

## What was checked against the real game

PEAK **2.6.b**, build id `25739797`, Unity **6000.3.15f1**, BepInEx
**5.4.23.3** (the PEAK pack, version `5.4.75301`). The game assemblies were
read from
`~/.local/share/Steam/steamapps/common/PEAK/PEAK_Data/Managed/`.

The following game APIs were read out of the shipped assemblies and confirmed
to exist with the shape the adapter expects.

| API | Where it is | Used for |
|---|---|---|
| `GameUtils.instance` | `GameUtils` | The delivery entry point |
| `GameUtils.InstantiateAndGrab(Item, Character, int)` | `GameUtils` | Handing an item to a character |
| `GameUtils.m_inAirport` | `GameUtils` | Telling the airport apart from a level |
| `GameUtils.AIRPORT_SCENE_NAME` | `GameUtils` | The fallback scene check |
| `Zorro.Core.SingletonAsset<T>.Instance` | `Zorro.Core.Runtime.dll` | The item registry |
| `Zorro.Core.DatabaseAsset<T, TItem>.Objects` | `Zorro.Core.Runtime.dll` | The list of items |
| `ItemDatabase` | `Assembly-CSharp` | The item database asset |
| `Item.UIData.itemName` | `Assembly-CSharp` | The display name of an item |
| `Item.ALL_ACTIVE_ITEMS` | `Assembly-CSharp` | Confirming that an item was created |
| `Character.localCharacter` | `Assembly-CSharp` | The local player |
| `Character.AllCharacters` | `Assembly-CSharp` | The players of the session |
| `Character.data.dead`, `Character.data.fullyPassedOut` | `Assembly-CSharp` | The eligibility of a target |
| `Character.IsGhost` | `Assembly-CSharp` | The eligibility of a target |
| `Character.view` | `Assembly-CSharp` | The Photon view of a character |
| `MushroomZombieSpawner.mushroomZombiePrefab` | `Assembly-CSharp` | The prefab name of the zombie |
| `Beehive.beeSwarmPrefab` | `Assembly-CSharp` | The prefab name of the swarm |
| `ScoutmasterSpawner` | `Assembly-CSharp` | The scoutmaster is placed by the level, not spawned |
| `TornadoSpawner` | `Assembly-CSharp` | The AI of the tornado |
| `PhotonNetwork.Instantiate(string, Vector3, Quaternion, byte, object[])` | `PhotonUnityNetworking.dll` | Creating a network object |
| `PhotonNetwork.InstantiateItemRoom(string, Vector3, Quaternion, bool)` | `PhotonUnityNetworking.dll` | Creating an item in the room |
| `PhotonView.RPC(string, RpcTarget, object[])` | `PhotonUnityNetworking.dll` | Handing a hazard to the game's AI |
| `PhotonNetwork.Destroy(PhotonView)` | `PhotonUnityNetworking.dll` | Removing an object for everybody |
| `PhotonNetwork.IsMasterClient`, `PhotonNetwork.OfflineMode`, `PhotonNetwork.InRoom`, `PhotonNetwork.IsConnected` | `PhotonUnityNetworking.dll` | Who may change the world |

The entity types `MushroomZombie`, `BeeSwarm`, `Scorpion`, `Scoutmaster`,
`Tornado`, `TumbleWeed` and `Dynamite` all exist in `Assembly-CSharp`.

The EOS integration of the game was read out of the shipped assemblies as
well: `Portningsbolaget.EOSCommon.EpicOnlineService` and
`Epic.OnlineServices.Platform.PlatformInterface` are the entry points of the
crash that is analysed in [LINUX_ROOT_CAUSE.md](LINUX_ROOT_CAUSE.md). The
plugin can see that the game build contains the EOS integration
(`eosState: present`) and nothing more; it does not claim to know whether the
SDK initialised.

## What was not verified

The table above says the APIs exist. It does **not** say that a spawn works
in a level, and it does not say that a second client sees it.

| Gate | State |
|---|---|
| The plugin loads and the REST server starts | verified on PEAK 2.6.b under Proton 11.0-100 |
| The bearer token is enforced | verified, an unauthenticated request gets `401` |
| The loader override makes the proxy win | verified with the Wine loader trace, see [LINUX_ROOT_CAUSE.md](LINUX_ROOT_CAUSE.md) |
| The item catalog lists the real items | **not verified in a level**, the code path is implemented and unit tested with a fake bridge |
| An item reaches a player | **not verified in a level** |
| An entity appears and behaves | **not verified in a level** |
| A hazard appears and behaves | **not verified in a level** |
| A second client sees the object | **not verified, two clients were never run** |
| The object is removed for everybody | **not verified in a level** |

`replicationVerified` is `false` in every response until a two client session
proves otherwise. The capability report says the same.

## Version sensitivity

The game layer binds late, so a new game version rarely breaks the build. It
can still break the behaviour, in these ways:

* A renamed type, for example `MushroomZombieSpawner`, makes the entity
  undiscoverable. The capability report then says that the running level does
  not contain a Mushroom Zombie, instead of failing.
* A changed prefab name, for example when the game registers the items under
  a different prefix than `0_Items/`, makes a spawn fail. The error message
  contains what Photon said.
* A changed `GameUtils` entry point disables the item path. The capability
  report says that the game version does not expose it.

The PEAK modding template pins the target framework to `netstandard2.1` and
the Unity reference assemblies to `6000.0.36`. PEAK itself runs on
`6000.3.15f1`, which is inside the same major version, so the reference
assemblies are a safe compile target.

## The doorstop fork

The PEAK BepInEx pack ships a patched doorstop
([UnityDoorstop-PEAK](https://github.com/CyR1en/UnityDoorstop-PEAK)) that can
bypass the `-force-vulkan` parameter. The generic BepInEx 5.4.23.3
`winhttp.dll` also loads under Proton and starts the plugin, but it replaces
the game's `GetProcAddress`, which the game uses through the Steamworks
runtime. The PEAK fork is the supported combination.

The shipped `winhttp.dll` of the PEAK pack was disassembled for this
release. It forwards its exports with jump table thunks, which are
transparent for every calling convention, and it is **not** built from the
`proxygen.lua` that is in the fork's repository today: that generator still
emits call wrappers with a zero argument prototype, which corrupts the
arguments of every function with more than four parameters. The two have to
be reconciled before the fork is built again. The details, and the exports
that resolve to nothing, are in
[LINUX_ROOT_CAUSE.md](LINUX_ROOT_CAUSE.md).

## References

* [PEAK modding template](https://github.com/PEAKModding/BepInExTemplate)
* [PEAKLib](https://github.com/PEAKModding/PEAKLib) — the community API for
  PEAK. PEAKapi does not depend on it, but it is the place to look for the
  supported way to register a modded item.
* [ItemBrowser](https://github.com/cherrycove/ItemBrowser) — the source of the
  `InstantiateAndGrab` and `SingletonAsset<ItemDatabase>` patterns.
* [HazardChaos](https://thunderstore.io/c/peak/p/Bananegame/HazardChaos/) —
  a behavioural reference for the hazard prefab names. It is not a proof that
  a generic prefab spawn works, and PEAKapi does not depend on it.
