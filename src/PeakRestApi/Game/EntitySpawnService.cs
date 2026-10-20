using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Models;
using PeakRestApi.Networking;
using UnityEngine;

namespace PeakRestApi.Game;

/// <summary>
/// A hostile or neutral entity that PEAKapi knows how to ask the game for.
/// </summary>
internal sealed class EntityKind
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// The Photon prefab names to try, in order. These are the names the game
    /// itself uses.
    /// </summary>
    public string[] PrefabNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A component the level places that carries the prefab of this entity.
    /// The prefab name is read from it, which is how the game spawns it.
    /// </summary>
    public string? ComponentTypeName { get; init; }

    /// <summary>
    /// The field of that component that holds the prefab.
    /// </summary>
    public string? ComponentPrefabField { get; init; }

    /// <summary>
    /// Set when the entity is backed by an item in the item database.
    /// </summary>
    public string? ItemName { get; init; }

    public int MaxPerType { get; init; } = 3;

    public double DefaultLifetimeSeconds { get; init; } = 60;
}

/// <summary>
/// Spawns the hostile and neutral entities of PEAK.
/// </summary>
/// <remarks>
/// Entities are created through Photon's network prefab registry, the same
/// mechanism the game uses. The prefab name is not guessed: it is read from
/// the component the level places for that entity, exactly like
/// <c>MushroomZombieSpawner</c> does, or from the item database for the
/// entities that are items.
/// </remarks>
internal sealed class EntitySpawnService
{
    internal static readonly EntityKind[] Known =
    {
        new EntityKind
        {
            Id = "mushroom_zombie",
            DisplayName = "Mushroom Zombie",
            ComponentTypeName = "MushroomZombieSpawner",
            ComponentPrefabField = "mushroomZombiePrefab",
            PrefabNames = new[] { "MushroomZombie", "0_Items/MushroomZombie", "Zombie/MushroomZombie" },
            MaxPerType = 3,
            DefaultLifetimeSeconds = 60,
        },
        new EntityKind
        {
            Id = "bee_swarm",
            DisplayName = "Bee Swarm",
            ComponentTypeName = "Beehive",
            ComponentPrefabField = "beeSwarmPrefab",
            PrefabNames = new[] { "BeeSwarm" },
            MaxPerType = 2,
            DefaultLifetimeSeconds = 45,
        },
        new EntityKind
        {
            Id = "scorpion",
            DisplayName = "Scorpion",
            ItemName = "Scorpion",
            PrefabNames = new[] { "Scorpion" },
            MaxPerType = 3,
            DefaultLifetimeSeconds = 45,
        },
    };

    private readonly GameContext context;
    private readonly PhotonSpawner spawner;
    private readonly CatalogService catalog;
    private readonly PositionResolver positions;

    public EntitySpawnService(GameContext context, PhotonSpawner spawner, CatalogService catalog, PositionResolver positions)
    {
        this.context = context;
        this.spawner = spawner;
        this.catalog = catalog;
        this.positions = positions;
    }

    /// <summary>
    /// The entity catalog, including kinds the running game does not contain.
    /// </summary>
    public IReadOnlyList<CatalogEntry> Catalog(long generation)
    {
        var result = new List<CatalogEntry>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in Known)
        {
            known.Add(kind.Id);
            var (available, reason) = Probe(kind, generation);
            result.Add(new CatalogEntry
            {
                Id = kind.Id,
                Name = kind.DisplayName,
                Category = "entity",
                Origin = null,
                Spawnable = available,
                Reason = reason,
            });
        }

        // Report entity-like types the running game contains but that have no
        // verified spawn adapter yet.
        foreach (var name in context.Types.GameTypeNames
                     .Where(n => LooksLikeEntity(n) && !known.Contains(Normalize(n)))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            result.Add(new CatalogEntry
            {
                Id = Normalize(name),
                Name = name,
                Category = "entity",
                Origin = null,
                Spawnable = false,
                Reason = "The running game contains this entity but PEAKapi has no verified spawn adapter for it.",
            });
        }

        return result;
    }

    /// <summary>
    /// The reason entity spawning is unavailable, or null when it is available.
    /// </summary>
    public string? UnavailableReason(long generation)
    {
        if (!context.Types.GameLoaded)
        {
            return "The PEAK game assemblies are not loaded yet.";
        }

        if (!spawner.Available)
        {
            return "Photon is not available, so networked entities cannot be created.";
        }

        var available = Known.Count(k => Probe(k, generation).Available);
        if (available == 0)
        {
            return "None of the known entity kinds were found in the running game.";
        }

        return null;
    }

    public SpawnOutcome Spawn(SpawnEntityRequest request, long generation)
    {
        var outcome = new SpawnOutcome();
        var kind = Known.FirstOrDefault(k => string.Equals(k.Id, request.EntityId, StringComparison.OrdinalIgnoreCase));
        if (kind == null)
        {
            throw new ApiException(404, "UNKNOWN_ENTITY", "No entity with the id '" + request.EntityId + "' exists.");
        }

        var (available, reason) = Probe(kind, generation);
        if (!available)
        {
            throw new ApiException(501, "UNSUPPORTED_ACTION", reason ?? "This entity cannot be spawned.");
        }

        if (!context.Quotas.TryReserve("entity", kind.Id, request.Quantity, kind.MaxPerType))
        {
            throw new ApiException(
                409,
                "QUOTA_EXCEEDED",
                "The entity cap for '" + kind.Id + "' is reached; despawn or clear the existing entities first.");
        }

        try
        {
            for (var i = 0; i < request.Quantity; i++)
            {
                if (!positions.TryResolve(request.Position, out var position, out _, out var failure))
                {
                    outcome.Failures.Add("Could not find a safe position: " + failure);
                    break;
                }

                var created = Create(kind, position, out var createFailure);
                if (created == null)
                {
                    outcome.Failures.Add(createFailure ?? "The entity could not be created.");
                    break;
                }

                var lifetime = request.LifetimeSeconds ?? kind.DefaultLifetimeSeconds;
                var id = context.Ownership.Register("entity", kind.Id, created, generation, Now() + lifetime);
                outcome.Spawned.Add(new SpawnedInstance { Id = id, CatalogId = kind.Id });
            }
        }
        finally
        {
            var missing = request.Quantity - outcome.Spawned.Count;
            if (missing > 0)
            {
                context.Quotas.Release("entity", kind.Id, missing);
            }
        }

        outcome.Count = outcome.Spawned.Count;
        return outcome;
    }

    private object? Create(EntityKind kind, Vector3 position, out string? failure)
    {
        failure = null;

        if (kind.ItemName != null)
        {
            var created = spawner.InstantiateItemRoom(kind.ItemName, position, Quaternion.identity, out var error);
            if (created == null)
            {
                failure = error;
                return null;
            }

            return created;
        }

        foreach (var prefabName in PrefabNames(kind))
        {
            var created = spawner.Instantiate(prefabName, position, Quaternion.identity, out var error);
            if (created == null)
            {
                failure = error;
                continue;
            }

            failure = null;
            return created;
        }

        return null;
    }

    /// <summary>
    /// The prefab names to try for a kind, most specific first.
    /// </summary>
    internal IReadOnlyList<string> PrefabNames(EntityKind kind)
    {
        var names = new List<string>();

        if (kind.ComponentTypeName != null && kind.ComponentPrefabField != null)
        {
            var componentType = context.Types.GameAssembly?.GetType(kind.ComponentTypeName, false);
            if (componentType != null)
            {
                foreach (var component in SceneComponents.FindAll(componentType))
                {
                    var name = SceneComponents.PrefabNameOf(component, kind.ComponentPrefabField);
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                    {
                        names.Add(name!);
                    }
                }
            }
        }

        foreach (var name in kind.PrefabNames)
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    internal bool HasPhotonView(object target)
    {
        var photonViewType = context.Types.PhotonView;
        if (photonViewType == null)
        {
            return false;
        }

        try
        {
            var component = target as Component;
            return component != null && component.GetComponent(photonViewType) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private (bool Available, string? Reason) Probe(EntityKind kind, long generation)
    {
        if (!context.Types.GameLoaded)
        {
            return (false, "The PEAK game assemblies are not loaded yet.");
        }

        if (!spawner.Available)
        {
            return (false, "Photon is not available in this game session.");
        }

        if (kind.ItemName != null)
        {
            if (catalog.Find(kind.ItemName, generation) != null)
            {
                return (true, null);
            }

            return (false, "The running game does not contain an item named '" + kind.ItemName + "'.");
        }

        // The prefab names of an entity are only a list of candidates. The
        // running game has to actually contain one of them, either as a type
        // or as a prefab of the loaded scene. Reporting the entity as
        // available just because the candidate list is not empty would claim
        // a spawn that cannot work.
        foreach (var name in PrefabNames(kind))
        {
            if (context.Types.HasGameType(name) || SceneComponents.PrefabExists(name))
            {
                return (true, null);
            }
        }

        return (false, "The running level does not contain a '" + kind.DisplayName + "'.");
    }

    private static bool LooksLikeEntity(string name) =>
        name.EndsWith("Zombie", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Swarm", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Scorpion", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Scoutmaster", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => name.ToLowerInvariant().Replace(' ', '_');

    internal static double Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
