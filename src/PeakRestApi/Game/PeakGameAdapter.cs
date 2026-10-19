using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Game;
using PeakRestApi.Core.Models;
using PeakRestApi.Core.Security;
using PeakRestApi.Networking;
using UnityEngine;

namespace PeakRestApi.Game;

/// <summary>
/// The only component that talks to the running game.
/// </summary>
/// <remarks>
/// Every method is called on the Unity main thread, from the dispatcher. The
/// adapter keeps a gameplay generation that increases whenever the scene or the
/// room changes, so a command that was queued for a previous session is
/// rejected instead of being replayed.
/// </remarks>
internal sealed class PeakGameAdapter : IGameBridge
{
    private readonly GameContext context;
    private readonly GameTypes types = new();
    private readonly PhotonAuthority authority;
    private readonly PhotonSpawner spawner;
    private readonly PlayerService players;
    private readonly CatalogService catalog;
    private readonly GameStateService state;
    private readonly PositionResolver positions;
    private readonly ItemSpawnService itemSpawns;
    private readonly EntitySpawnService entitySpawns;
    private readonly HazardSpawnService hazardSpawns;

    private long generation = 1;
    private string? sessionKey;

    public PeakGameAdapter(ApiConfigLike config, Action<string> log)
    {
        authority = new PhotonAuthority(types);
        spawner = new PhotonSpawner(types);
        context = new GameContext(
            types,
            config,
            authority,
            new SpawnOwnershipRegistry(),
            new SpawnQuotaRegistry(config.MaxActiveEntities, config.MaxActiveHazards),
            log);

        players = new PlayerService(context);
        catalog = new CatalogService(context);
        state = new GameStateService(context, players);
        positions = new PositionResolver(context, players);
        itemSpawns = new ItemSpawnService(context, catalog, positions, players);
        entitySpawns = new EntitySpawnService(context, spawner, catalog, positions);
        hazardSpawns = new HazardSpawnService(context, spawner, catalog, positions);
    }

    public long Generation => generation;

    /// <summary>
    /// Called from the Unity main thread. Refreshes the game bindings and
    /// detects scene or room changes.
    /// </summary>
    public void Tick()
    {
        types.Refresh();

        var key = SafeSessionKey();
        if (sessionKey == null)
        {
            sessionKey = key;
            return;
        }

        if (!string.Equals(sessionKey, key, StringComparison.Ordinal))
        {
            sessionKey = key;
            generation++;
            context.Ownership.PurgeStale(generation);
            context.Quotas.Reset();
            context.Log("Gameplay generation changed to " + generation + " (" + key + ").");
        }
    }

    public GameSnapshot State() => state.Snapshot();

    public ApiVersions Versions()
    {
        var versions = new ApiVersions
        {
            ApiVersion = "1.0.0",
            PluginVersion = PluginVersion,
            GameVersion = ReadUnityString(() => Application.version),
            BepInExVersion = ReadBepInExVersion(),
            PeakLibVersion = ReadPeakLibVersion(),
            UnityVersion = ReadUnityString(() => Application.unityVersion),
            Platform = ReadUnityString(() => Application.platform.ToString()) + "/" + Environment.OSVersion.Platform,
        };

        return versions;
    }

    public CapabilitiesReport Capabilities()
    {
        var itemReason = itemSpawns.UnavailableReason();
        var entityReason = entitySpawns.UnavailableReason(generation);
        var hazardReason = hazardSpawns.UnavailableReason(generation);

        return new CapabilitiesReport
        {
            Items = itemReason == null
                ? FeatureCapability.Supported()
                : FeatureCapability.Unsupported(itemReason),
            Entities = entityReason == null
                ? FeatureCapability.Supported()
                : FeatureCapability.Unsupported(entityReason),
            Hazards = hazardReason == null
                ? FeatureCapability.Supported()
                : FeatureCapability.Unsupported(hazardReason),
            Effects = FeatureCapability.Unsupported("No gameplay effect has been verified against the running game."),
            WorldPlacement = FeatureCapability.Supported(
                "Items can be placed in the world through the game's networked item path."),
            MultiplayerReplication = new FeatureCapability
            {
                Available = false,
                Verified = false,
                Reason = "Multiplayer replication has not been verified in a two-client session yet.",
            },
        };
    }

    public IReadOnlyList<CatalogEntry> Catalog(CatalogKind kind, string? search)
    {
        var entries = kind switch
        {
            CatalogKind.Items => catalog.Items(generation)
                .Select(i => new CatalogEntry
                {
                    Id = i.Id,
                    Name = i.Name,
                    Category = "item",
                    Origin = i.Origin,
                    Spawnable = i.Spawnable,
                    Reason = i.Reason,
                })
                .ToList(),
            CatalogKind.Entities => entitySpawns.Catalog(generation).ToList(),
            CatalogKind.Hazards => hazardSpawns.Catalog(generation).ToList(),
            _ => new List<CatalogEntry>(),
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search!.Trim();
            entries = entries
                .Where(e => e.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                            || e.Id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        return entries;
    }

    public IReadOnlyList<PlayerInfo> Players() => players.Describe();

    public IReadOnlyList<OwnedInstance> ActiveInstances()
    {
        var result = new List<OwnedInstance>();
        foreach (var entry in context.Ownership.Snapshot())
        {
            if (entry.Kind == "item")
            {
                // A delivered item lives in a player's inventory, it is not a
                // world object PEAKapi could despawn.
                continue;
            }

            result.Add(new OwnedInstance
            {
                Id = entry.Id,
                CatalogId = entry.CatalogId,
                Kind = entry.Kind,
                ExpiresAtUnixSeconds = entry.ExpiresAtUnixSeconds,
            });
        }

        return result;
    }

    public SpawnOutcome SpawnItem(SpawnItemRequest request) => itemSpawns.Spawn(request, generation);

    public SpawnOutcome SpawnEntity(SpawnEntityRequest request) => entitySpawns.Spawn(request, generation);

    public SpawnOutcome SpawnHazard(SpawnHazardRequest request) => hazardSpawns.Spawn(request, generation);

    public SpawnOutcome ApplyPlayerEffect(PlayerEffectRequest request) =>
        throw new ApiException(
            501,
            "UNSUPPORTED_ACTION",
            "No gameplay effect has been verified against the running game.");

    public DespawnOutcome Despawn(string? instanceId)
    {
        var outcome = new DespawnOutcome();
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ApiException(400, "INVALID_REQUEST", "instanceId is required.");
        }

        var entry = context.Ownership.Take(instanceId!);
        if (entry == null)
        {
            throw new ApiException(
                404,
                "UNKNOWN_INSTANCE",
                "No API owned object with the id '" + instanceId + "' exists. It may already have been despawned.");
        }

        if (!Destroy(entry.Target))
        {
            context.Ownership.Register(entry.Kind, entry.CatalogId, entry.Target, entry.Generation, entry.ExpiresAtUnixSeconds);
            throw new ApiException(
                409,
                "DESPAWN_FAILED",
                "The object could not be destroyed, for example because the local peer does not own it.");
        }

        context.Quotas.Release(entry.Kind, entry.CatalogId, 1);
        outcome.Removed = 1;
        outcome.RemovedIds.Add(entry.Id);
        return outcome;
    }

    public DespawnOutcome Clear()
    {
        var outcome = new DespawnOutcome();
        foreach (var entry in context.Ownership.Snapshot())
        {
            if (entry.Kind == "item")
            {
                outcome.Skipped.Add(entry.Id + " (delivered items cannot be recalled)");
                continue;
            }

            if (Destroy(entry.Target))
            {
                context.Quotas.Release(entry.Kind, entry.CatalogId, 1);
                outcome.Removed++;
                outcome.RemovedIds.Add(entry.Id);
            }
            else
            {
                outcome.Skipped.Add(entry.Id);
            }
        }

        context.Ownership.Clear();
        context.Quotas.Reset();
        return outcome;
    }

    private static bool Destroy(object target) => PhotonSpawner.Destroy(target);

    private string? SafeSessionKey()
    {
        try
        {
            return state.Scene + "|" + state.SessionId;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ReadUnityString(Func<string> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ReadBepInExVersion()
    {
        try
        {
            var assembly = typeof(BepInEx.BaseUnityPlugin).Assembly;
            var version = assembly.GetName().Version;
            return version == null ? "unknown" : version.ToString();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ReadPeakLibVersion()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (name != null && name.StartsWith("com.github.PEAKModding.PEAKLib", StringComparison.Ordinal))
                {
                    var version = assembly.GetName().Version;
                    return name + " " + (version == null ? "unknown" : version.ToString());
                }
            }
        }
        catch (Exception)
        {
        }

        return "not installed";
    }

    internal static string PluginVersion =>
        typeof(PeakGameAdapter).Assembly.GetName().Version?.ToString() ?? "unknown";
}
