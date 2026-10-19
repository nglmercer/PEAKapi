using System;
using System.Collections.Generic;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Core.Game;

/// <summary>
/// The catalogs PEAKapi can expose.
/// </summary>
public enum CatalogKind
{
    Items,
    Entities,
    Hazards,
}

/// <summary>
/// The game facing contract of PEAKapi. The HTTP layer never talks to the
/// game directly: it validates a request, dispatches it to the Unity main
/// thread and then calls into this bridge, which is the only place that is
/// allowed to touch game objects.
/// </summary>
public interface IGameBridge
{
    /// <summary>
    /// Increases whenever the scene, room or gameplay generation changes. A
    /// command that was queued under a stale generation is rejected instead of
    /// being replayed against a different session.
    /// </summary>
    long Generation { get; }

    GameSnapshot State();

    ApiVersions Versions();

    CapabilitiesReport Capabilities();

    IReadOnlyList<CatalogEntry> Catalog(CatalogKind kind, string? search);

    IReadOnlyList<PlayerInfo> Players();

    IReadOnlyList<OwnedInstance> ActiveInstances();

    SpawnOutcome SpawnItem(SpawnItemRequest request);

    SpawnOutcome SpawnEntity(SpawnEntityRequest request);

    SpawnOutcome SpawnHazard(SpawnHazardRequest request);

    SpawnOutcome ApplyPlayerEffect(PlayerEffectRequest request);

    DespawnOutcome Despawn(string? instanceId);

    DespawnOutcome Clear();
}
