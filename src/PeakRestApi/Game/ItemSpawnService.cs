using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Models;
using PeakRestApi.Networking;
using UnityEngine;

namespace PeakRestApi.Game;

/// <summary>
/// Spawns items into the running game.
/// </summary>
/// <remarks>
/// Two verified paths are used, both taken from the game's own code:
/// <list type="bullet">
/// <item>
/// Handing an item to a character uses
/// <c>GameUtils.InstantiateAndGrab(item, character, 0)</c>. The game forwards
/// that call to the room host, which creates the item and interacts with the
/// character. This is the path used for <c>near_player</c> and
/// <c>random_player</c>.
/// </item>
/// <item>
/// Placing an item in the world uses
/// <c>PhotonNetwork.InstantiateItemRoom(name, position, rotation)</c>, the
/// same call the game uses when it drops an item. This is the path used for
/// <c>world_position</c>. The created object is tracked, so it can be
/// despawned again.
/// </item>
/// </list>
/// </remarks>
internal sealed class ItemSpawnService
{
    private readonly GameContext context;
    private readonly CatalogService catalog;
    private readonly PositionResolver positions;
    private readonly PlayerService players;

    public ItemSpawnService(GameContext context, CatalogService catalog, PositionResolver positions, PlayerService players)
    {
        this.context = context;
        this.catalog = catalog;
        this.positions = positions;
        this.players = players;
    }

    /// <summary>
    /// The reason item spawning is unavailable, or null when it is available.
    /// </summary>
    public string? UnavailableReason()
    {
        var types = context.Types;
        if (!types.GameLoaded)
        {
            return "The PEAK game assemblies are not loaded yet.";
        }

        if (types.GameUtils == null)
        {
            return "This PEAK version does not expose a GameUtils type.";
        }

        if (GameMembers.GetStatic(types.GameUtils, "instance") == null)
        {
            return "The game's GameUtils singleton is not available yet.";
        }

        if (GameMembers.FindMethod(types.GameUtils, "InstantiateAndGrab", 3) == null)
        {
            return "This PEAK version does not expose GameUtils.InstantiateAndGrab(item, character, amount).";
        }

        var catalogReason = catalog.UnavailableReason;
        if (catalogReason != null)
        {
            return catalogReason;
        }

        return null;
    }

    public SpawnOutcome Spawn(SpawnItemRequest request, long generation)
    {
        var outcome = new SpawnOutcome();

        var item = catalog.Find(request.ItemId, generation);
        if (item == null || item.Prefab == null)
        {
            throw new ApiException(404, "UNKNOWN_ITEM", "No item with the id '" + request.ItemId + "' exists.");
        }

        for (var i = 0; i < request.Quantity; i++)
        {
            if (request.Position.Mode == "world_position")
            {
                PlaceInWorld(request, item, generation, outcome);
            }
            else
            {
                HandToCharacter(request, item, generation, outcome);
            }
        }

        outcome.Count = outcome.Spawned.Count;
        return outcome;
    }

    /// <summary>
    /// Places the item in the world at a validated position.
    /// </summary>
    private void PlaceInWorld(SpawnItemRequest request, CatalogItem item, long generation, SpawnOutcome outcome)
    {
        if (!positions.TryResolve(request.Position, out var position, out _, out var failure))
        {
            throw new ApiException(422, "NO_SAFE_POSITION", failure ?? "No safe position could be resolved.");
        }

        var created = PhotonSpawnerInstantiateItem(item.Id, position, out var createFailure);
        if (created == null)
        {
            throw new ApiException(500, "SPAWN_FAILED", createFailure ?? "The item could not be created.");
        }

        var id = context.Ownership.Register("item", item.Id, created, generation, null);
        outcome.Spawned.Add(new SpawnedInstance { Id = id, CatalogId = item.Id });
    }

    /// <summary>
    /// Hands the item to a character.
    /// </summary>
    private void HandToCharacter(SpawnItemRequest request, CatalogItem item, long generation, SpawnOutcome outcome)
    {
        var target = ResolveTarget(request.Position, out var failure);
        if (target == null)
        {
            throw new ApiException(422, "NO_SAFE_POSITION", failure ?? "No eligible player could be resolved.");
        }

        if (!target.CanReceiveItems)
        {
            throw new ApiException(
                409,
                "TARGET_NOT_ELIGIBLE",
                "The selected player cannot receive items right now (dead, ghost or passed out).");
        }

        var before = CountActiveItems();
        var delivered = Deliver(item.Prefab, target.Character, out var deliveryFailure);
        if (!delivered)
        {
            throw new ApiException(500, "SPAWN_FAILED", deliveryFailure ?? "The item could not be delivered.");
        }

        var after = CountActiveItems();
        if (before.HasValue && after.HasValue && after.Value <= before.Value)
        {
            throw new ApiException(
                500,
                "SPAWN_FAILED",
                "The game accepted the delivery call but no item was created.");
        }

        // The item now lives in the player's inventory. There is no world
        // object that could be destroyed again, so the entry is marked as not
        // despawnable. Registering the character as the target would let a
        // despawn request destroy the player.
        var id = context.Ownership.Register("item", item.Id, target.Character, generation, null, despawnable: false);
        outcome.Spawned.Add(new SpawnedInstance { Id = id, CatalogId = item.Id, PlayerId = target.Id });
    }

    private PlayerHandle? ResolveTarget(PositionRequest request, out string? failure)
    {
        failure = null;

        if (request.Mode == "near_player")
        {
            if (string.IsNullOrWhiteSpace(request.PlayerId))
            {
                failure = "near_player requires a playerId.";
                return null;
            }

            var player = players.Find(request.PlayerId!);
            if (player == null)
            {
                failure = "The requested player is not part of this session.";
                return null;
            }

            return player;
        }

        if (request.Mode == "random_player")
        {
            var random = new System.Random();
            var eligible = players.Enumerate().Where(p => p.CanReceiveItems).ToList();
            if (eligible.Count == 0)
            {
                failure = "No player in this session can currently receive an item.";
                return null;
            }

            return eligible[random.Next(eligible.Count)];
        }

        failure = "Unknown position mode.";
        return null;
    }

    private object? PhotonSpawnerInstantiateItem(string itemId, Vector3 position, out string? failure)
    {
        failure = null;
        var types = context.Types;
        if (types.PhotonNetwork == null)
        {
            failure = "Photon is not available in this game session.";
            return null;
        }

        var arguments = new object?[] { itemId, position, Quaternion.identity };
        var result = GameMembers.Invoke(null, types.PhotonNetwork, "InstantiateItemRoom", arguments, out var error);
        if (error != null)
        {
            failure = "Photon could not instantiate the item '" + itemId + "': " + error.Message;
            return null;
        }

        if (result == null)
        {
            failure = "Photon returned no object for the item '" + itemId + "'. The prefab is probably not registered or the session has no room.";
            return null;
        }

        return result;
    }

    private bool Deliver(object prefab, object character, out string? failure)
    {
        failure = null;
        var types = context.Types;

        var gameUtils = GameMembers.GetStatic(types.GameUtils, "instance");
        if (gameUtils == null)
        {
            failure = "The game's GameUtils singleton is not available.";
            return false;
        }

        var arguments = new object?[] { prefab, character, 0 };
        var error = (Exception?)null;
        GameMembers.Invoke(gameUtils, types.GameUtils, "InstantiateAndGrab", arguments, out error);
        if (error != null)
        {
            failure = "The game rejected the item delivery: " + error.Message;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Counts the items that currently exist in the level, when the game
    /// exposes that list. Returns null when it cannot be determined.
    /// </summary>
    private int? CountActiveItems()
    {
        try
        {
            var itemType = context.Types.Item;
            if (itemType == null)
            {
                return null;
            }

            if (GameMembers.GetStatic(itemType, "ALL_ACTIVE_ITEMS") is not System.Collections.IEnumerable items)
            {
                return null;
            }

            var count = 0;
            foreach (var _ in items)
            {
                count++;
            }

            return count;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
