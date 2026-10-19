using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Models;
using PeakRestApi.Networking;
using UnityEngine;

namespace PeakRestApi.Game;

/// <summary>
/// A hazard that PEAKapi knows how to ask the game for.
/// </summary>
internal sealed class HazardKind
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// The Photon prefab names to try, in order.
    /// </summary>
    public string[] PrefabNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set when the hazard is created through the game's item path.
    /// </summary>
    public string? ItemName { get; init; }

    /// <summary>
    /// The component that drives the hazard's behaviour in the level. The
    /// hazard is handed over to it with a Photon RPC, exactly like the game's
    /// own spawner does.
    /// </summary>
    public string? AiComponentTypeName { get; init; }

    /// <summary>
    /// The RPC the game uses to hand the hazard over to its AI.
    /// </summary>
    public string? AiInitRpc { get; init; }

    public int MaxPerType { get; init; } = 2;

    public double DefaultLifetimeSeconds { get; init; } = 45;
}

/// <summary>
/// Spawns the environmental hazards of PEAK.
/// </summary>
/// <remarks>
/// Each hazard type has its own creation path and its own cap. A tornado is
/// expensive for the game to simulate, so it gets a stricter cap than a
/// dynamite pickup. When a hazard needs the game's AI to drive it, the AI
/// component of the level is located and the game's own initialisation RPC is
/// replayed; when that component is missing the hazard is still created and
/// the missing initialisation is reported instead of being hidden.
/// </remarks>
internal sealed class HazardSpawnService
{
    internal static readonly HazardKind[] Known =
    {
        new HazardKind
        {
            Id = "dynamite",
            DisplayName = "Dynamite",
            ItemName = "Dynamite",
            MaxPerType = 3,
            DefaultLifetimeSeconds = 30,
        },
        new HazardKind
        {
            Id = "tornado",
            DisplayName = "Tornado",
            PrefabNames = new[] { "Tornado" },
            AiComponentTypeName = "TornadoSpawner",
            AiInitRpc = "RPCA_InitTornado",
            MaxPerType = 1,
            DefaultLifetimeSeconds = 30,
        },
        new HazardKind
        {
            Id = "tumbleweed",
            DisplayName = "Tumbleweed",
            PrefabNames = new[] { "TumbleWeed", "Tumbleweed" },
            MaxPerType = 2,
            DefaultLifetimeSeconds = 30,
        },
    };

    private readonly GameContext context;
    private readonly PhotonSpawner spawner;
    private readonly CatalogService catalog;
    private readonly PositionResolver positions;

    public HazardSpawnService(
        GameContext context,
        PhotonSpawner spawner,
        CatalogService catalog,
        PositionResolver positions)
    {
        this.context = context;
        this.spawner = spawner;
        this.catalog = catalog;
        this.positions = positions;
    }

    public IReadOnlyList<CatalogEntry> Catalog(long generation)
    {
        var result = new List<CatalogEntry>();
        foreach (var kind in Known)
        {
            var (available, reason) = Probe(kind, generation);
            result.Add(new CatalogEntry
            {
                Id = kind.Id,
                Name = kind.DisplayName,
                Category = "hazard",
                Origin = null,
                Spawnable = available,
                Reason = reason,
            });
        }

        return result;
    }

    public string? UnavailableReason(long generation)
    {
        if (!context.Types.GameLoaded)
        {
            return "The PEAK game assemblies are not loaded yet.";
        }

        if (!spawner.Available)
        {
            return "Photon is not available, so networked hazards cannot be created.";
        }

        var available = Known.Count(k => Probe(k, generation).Available);
        if (available == 0)
        {
            return "None of the known hazard kinds were found in the running game.";
        }

        return null;
    }

    public SpawnOutcome Spawn(SpawnHazardRequest request, long generation)
    {
        var outcome = new SpawnOutcome();
        var kind = Known.FirstOrDefault(k => string.Equals(k.Id, request.HazardId, StringComparison.OrdinalIgnoreCase));
        if (kind == null)
        {
            throw new ApiException(404, "UNKNOWN_HAZARD", "No hazard with the id '" + request.HazardId + "' exists.");
        }

        var (available, reason) = Probe(kind, generation);
        if (!available)
        {
            throw new ApiException(501, "UNSUPPORTED_ACTION", reason ?? "This hazard cannot be spawned.");
        }

        if (!context.Quotas.TryReserve("hazard", kind.Id, request.Quantity, kind.MaxPerType))
        {
            throw new ApiException(
                409,
                "QUOTA_EXCEEDED",
                "The hazard cap for '" + kind.Id + "' is reached; despawn or clear the existing hazards first.");
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
                    outcome.Failures.Add(createFailure ?? "The hazard could not be created.");
                    break;
                }

                var lifetime = request.LifetimeSeconds ?? kind.DefaultLifetimeSeconds;
                var id = context.Ownership.Register("hazard", kind.Id, created, generation, EntitySpawnService.Now() + lifetime);
                outcome.Spawned.Add(new SpawnedInstance { Id = id, CatalogId = kind.Id });
            }
        }
        finally
        {
            var missing = request.Quantity - outcome.Spawned.Count;
            if (missing > 0)
            {
                context.Quotas.Release("hazard", kind.Id, missing);
            }
        }

        outcome.Count = outcome.Spawned.Count;
        return outcome;
    }

    private object? Create(HazardKind kind, Vector3 position, out string? failure)
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

        foreach (var prefabName in kind.PrefabNames)
        {
            var created = spawner.Instantiate(prefabName, position, Quaternion.identity, out var error);
            if (created == null)
            {
                failure = error;
                continue;
            }

            if (kind.AiInitRpc != null)
            {
                InitialiseAi(kind, created);
            }

            failure = null;
            return created;
        }

        return null;
    }

    /// <summary>
    /// Hands the hazard over to the game's own AI component.
    /// </summary>
    private void InitialiseAi(HazardKind kind, object created)
    {
        try
        {
            var componentType = context.Types.GameAssembly?.GetType(kind.AiComponentTypeName!, false);
            if (componentType == null)
            {
                return;
            }

            var controller = SceneComponents.FindFirst(componentType);
            if (controller == null)
            {
                return;
            }

            var view = GameMembers.Get(controller, "view") ?? GameMembers.Get(controller, "photonView");
            if (view == null)
            {
                return;
            }

            var viewId = GameMembers.Get(view, "ViewID");
            if (viewId == null)
            {
                return;
            }

            var hazardView = (created as GameObject)?.GetComponent(context.Types.PhotonView?.Name ?? "PhotonView");
            if (hazardView == null)
            {
                return;
            }

            PhotonSpawner.CallRpc(hazardView, kind.AiInitRpc!, "All", viewId);
        }
        catch (Exception)
        {
        }
    }

    private (bool Available, string? Reason) Probe(HazardKind kind, long generation)
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

        foreach (var name in kind.PrefabNames)
        {
            if (context.Types.HasGameType(name) || SceneComponents.PrefabExists(name))
            {
                return (true, null);
            }
        }

        return (false, "The running game does not contain a prefab named '" + kind.PrefabNames[0] + "'.");
    }
}
