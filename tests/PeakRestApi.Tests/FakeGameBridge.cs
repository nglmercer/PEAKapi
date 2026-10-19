using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeakRestApi.Core.Game;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Tests;

/// <summary>
/// A deterministic stand in for the running game.
/// </summary>
internal sealed class FakeGameBridge : IGameBridge
{
    private readonly Dictionary<string, CatalogEntry> items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogEntry> entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogEntry> hazards = new(StringComparer.Ordinal);
    private readonly List<PlayerInfo> players = new();

    public long Generation { get; set; } = 1;

    public bool InLevel { get; set; } = true;

    public bool IsHost { get; set; } = true;

    public int SpawnCalls { get; private set; }

    public int DespawnCalls { get; private set; }

    public int ClearCalls { get; private set; }

    public bool ItemsAvailable { get; set; } = true;

    public bool EntitiesAvailable { get; set; } = true;

    public bool HazardsAvailable { get; set; } = true;

    public bool EffectsAvailable { get; set; }

    public Exception? SpawnFailure { get; set; }

    public string? LastSpawnedItemId { get; private set; }

    public FakeGameBridge AddItem(string id, string name)
    {
        items[id] = new CatalogEntry { Id = id, Name = name, Category = "item", Spawnable = true };
        return this;
    }

    public FakeGameBridge AddEntity(string id, string name, bool spawnable = true)
    {
        entities[id] = new CatalogEntry { Id = id, Name = name, Category = "entity", Spawnable = spawnable };
        return this;
    }

    public FakeGameBridge AddHazard(string id, string name, bool spawnable = true)
    {
        hazards[id] = new CatalogEntry { Id = id, Name = name, Category = "hazard", Spawnable = spawnable };
        return this;
    }

    public FakeGameBridge AddPlayer(string id, string name)
    {
        players.Add(new PlayerInfo { Id = id, Name = name, IsLocal = true, IsHost = true, IsAlive = true });
        return this;
    }

    public GameSnapshot State() => new()
    {
        GameRunning = true,
        InLevel = InLevel,
        IsHost = IsHost,
        Scene = "WilIsland",
        SessionId = "test",
    };

    public ApiVersions Versions() => new()
    {
        ApiVersion = "1.0.0",
        PluginVersion = "test",
        GameVersion = "2.6.b",
        BepInExVersion = "5.4.23.3",
        PeakLibVersion = "not installed",
        UnityVersion = "6000.3.15f1",
        Platform = "test",
    };

    public CapabilitiesReport Capabilities() => new()
    {
        Items = ItemsAvailable ? FeatureCapability.Supported() : FeatureCapability.Unsupported("items disabled"),
        Entities = EntitiesAvailable ? FeatureCapability.Supported() : FeatureCapability.Unsupported("entities disabled"),
        Hazards = HazardsAvailable ? FeatureCapability.Supported() : FeatureCapability.Unsupported("hazards disabled"),
        Effects = EffectsAvailable ? FeatureCapability.Supported() : FeatureCapability.Unsupported("not verified"),
        MultiplayerReplication = new FeatureCapability
        {
            Available = false,
            Verified = false,
            Reason = "not verified",
        },
    };

    public IReadOnlyList<CatalogEntry> Catalog(CatalogKind kind, string? search)
    {
        IEnumerable<CatalogEntry> source = kind switch
        {
            CatalogKind.Items => items.Values,
            CatalogKind.Entities => entities.Values,
            CatalogKind.Hazards => hazards.Values,
            _ => Array.Empty<CatalogEntry>(),
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            source = source.Where(e =>
                e.Name.Contains(search!, StringComparison.OrdinalIgnoreCase)
                || e.Id.Contains(search!, StringComparison.OrdinalIgnoreCase));
        }

        return source.ToList();
    }

    public IReadOnlyList<PlayerInfo> Players() => players;

    public IReadOnlyList<OwnedInstance> ActiveInstances() => Array.Empty<OwnedInstance>();

    public SpawnOutcome SpawnItem(SpawnItemRequest request)
    {
        SpawnCalls++;
        LastSpawnedItemId = request.ItemId;
        if (SpawnFailure != null)
        {
            throw SpawnFailure;
        }

        return new SpawnOutcome
        {
            Spawned = { new SpawnedInstance { Id = "item-1", CatalogId = request.ItemId } },
            Count = 1,
            ReplicationVerified = false,
        };
    }

    public SpawnOutcome SpawnEntity(SpawnEntityRequest request)
    {
        SpawnCalls++;
        if (SpawnFailure != null)
        {
            throw SpawnFailure;
        }

        return new SpawnOutcome
        {
            Spawned = { new SpawnedInstance { Id = "entity-1", CatalogId = request.EntityId } },
            Count = 1,
        };
    }

    public SpawnOutcome SpawnHazard(SpawnHazardRequest request)
    {
        SpawnCalls++;
        if (SpawnFailure != null)
        {
            throw SpawnFailure;
        }

        return new SpawnOutcome
        {
            Spawned = { new SpawnedInstance { Id = "hazard-1", CatalogId = request.HazardId } },
            Count = 1,
        };
    }

    public SpawnOutcome ApplyPlayerEffect(PlayerEffectRequest request) =>
        throw new ApiException(501, "UNSUPPORTED_ACTION", "No gameplay effect has been verified against the running game.");

    public DespawnOutcome Despawn(string? instanceId)
    {
        DespawnCalls++;
        return new DespawnOutcome { Removed = 1, RemovedIds = { instanceId ?? string.Empty } };
    }

    public DespawnOutcome Clear()
    {
        ClearCalls++;
        return new DespawnOutcome { Removed = 0 };
    }
}

/// <summary>
/// A dispatcher that never pumps, used to prove that queued work expires
/// without ever reaching the game.
/// </summary>
internal sealed class NullPump
{
    public static void Nothing()
    {
        Thread.Sleep(1);
    }
}
