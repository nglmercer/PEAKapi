using System;
using System.Collections.Generic;

namespace PeakRestApi.Core.Models;

/// <summary>
/// A non-sensitive view of the current game session.
/// </summary>
public sealed class GameSnapshot
{
    public bool GameRunning { get; set; }

    public bool InLevel { get; set; }

    public bool IsHost { get; set; }

    public string Scene { get; set; } = "unknown";

    public string SessionId { get; set; } = string.Empty;
}

/// <summary>
/// Version information for every component PEAKapi can observe.
/// </summary>
public sealed class ApiVersions
{
    public string ApiVersion { get; set; } = "1.0.0";

    public string PluginVersion { get; set; } = "unknown";

    public string GameVersion { get; set; } = "unknown";

    public string BepInExVersion { get; set; } = "unknown";

    public string PeakLibVersion { get; set; } = "not installed";

    public string UnityVersion { get; set; } = "unknown";

    public string Platform { get; set; } = "unknown";
}

/// <summary>
/// A single capability entry. Unsupported features always carry a reason so a
/// client can explain itself to the operator instead of guessing.
/// </summary>
public sealed class FeatureCapability
{
    public bool Available { get; set; }

    public string? Reason { get; set; }

    /// <summary>
    /// Set when the feature has a verification state of its own, for example
    /// multiplayer replication.
    /// </summary>
    public bool? Verified { get; set; }

    public static FeatureCapability Supported(string? reason = null) =>
        new() { Available = true, Reason = reason };

    public static FeatureCapability Unsupported(string reason) =>
        new() { Available = false, Reason = reason };
}

/// <summary>
/// The full capability report. Nothing here is optimistic: an entry is only
/// marked available when the adapter verified the underlying game API.
/// </summary>
public sealed class CapabilitiesReport
{
    public FeatureCapability Items { get; set; } = FeatureCapability.Unsupported("Not initialised.");

    public FeatureCapability Entities { get; set; } = FeatureCapability.Unsupported("Not initialised.");

    public FeatureCapability Hazards { get; set; } = FeatureCapability.Unsupported("Not initialised.");

    public FeatureCapability Effects { get; set; } = FeatureCapability.Unsupported("Not initialised.");

    public FeatureCapability MultiplayerReplication { get; set; } =
        new()
        {
            Available = false,
            Verified = false,
            Reason = "Multiplayer replication has not been verified in a two-client session.",
        };

    public FeatureCapability WorldPlacement { get; set; } = FeatureCapability.Unsupported("Not initialised.");
}

/// <summary>
/// One entry of a spawnable catalog.
/// </summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string? Origin { get; set; }

    public bool Spawnable { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// A player in the current session, identified by a session scoped opaque id.
/// </summary>
public sealed class PlayerInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsLocal { get; set; }

    public bool IsHost { get; set; }

    public bool IsAlive { get; set; }

    public bool IsSpectating { get; set; }
}

/// <summary>
/// The requested placement for a spawn.
/// </summary>
public sealed class PositionRequest
{
    public string Mode { get; set; } = "near_player";

    public string? PlayerId { get; set; }

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Z { get; set; }

    public void Validate()
    {
        if (Mode != "near_player" && Mode != "random_player" && Mode != "world_position")
        {
            throw new ApiException(400, "INVALID_POSITION", "Unknown position mode.");
        }

        if (Mode == "near_player" && string.IsNullOrWhiteSpace(PlayerId))
        {
            throw new ApiException(400, "INVALID_POSITION", "near_player requires playerId.");
        }

        if (Mode == "world_position" && (!Finite(X) || !Finite(Y) || !Finite(Z)))
        {
            throw new ApiException(400, "INVALID_POSITION", "Finite x, y and z within +/-10000 are required.");
        }
    }

    private static bool Finite(double? value) =>
        value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && Math.Abs(value.Value) <= 10000;
}

/// <summary>
/// A request to spawn one catalog item.
/// </summary>
public sealed class SpawnItemRequest
{
    public string ItemId { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    public PositionRequest Position { get; set; } = new();
}

/// <summary>
/// A request to spawn one catalog entity.
/// </summary>
public sealed class SpawnEntityRequest
{
    public string EntityId { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    public PositionRequest Position { get; set; } = new();

    public double? LifetimeSeconds { get; set; }
}

/// <summary>
/// A request to spawn one catalog hazard.
/// </summary>
public sealed class SpawnHazardRequest
{
    public string HazardId { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    public PositionRequest Position { get; set; } = new();

    public double? LifetimeSeconds { get; set; }
}

/// <summary>
/// A request to apply an allowlisted gameplay effect to a player.
/// </summary>
public sealed class PlayerEffectRequest
{
    public string EffectId { get; set; } = string.Empty;

    public string PlayerId { get; set; } = string.Empty;
}

/// <summary>
/// One object created by the API.
/// </summary>
public sealed class SpawnedInstance
{
    public string Id { get; set; } = string.Empty;

    public string CatalogId { get; set; } = string.Empty;

    public string? PlayerId { get; set; }
}

/// <summary>
/// The result of a spawn request. <see cref="Spawned"/> only ever contains
/// objects that were really created; partial success is reported honestly.
/// </summary>
public sealed class SpawnOutcome
{
    public List<SpawnedInstance> Spawned { get; set; } = new();

    public int Count { get; set; }

    public bool ReplicationVerified { get; set; }

    public List<string> Failures { get; set; } = new();
}

/// <summary>
/// The result of a despawn or clear request.
/// </summary>
public sealed class DespawnOutcome
{
    public int Removed { get; set; }

    public List<string> RemovedIds { get; set; } = new();

    public List<string> Skipped { get; set; } = new();
}

/// <summary>
/// The set of objects the API currently owns.
/// </summary>
public sealed class OwnedInstance
{
    public string Id { get; set; } = string.Empty;

    public string CatalogId { get; set; } = string.Empty;

    public string Kind { get; set; } = "item";

    public string? PlayerId { get; set; }

    public double? ExpiresAtUnixSeconds { get; set; }
}
