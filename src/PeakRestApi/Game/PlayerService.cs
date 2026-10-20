using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Models;
using UnityEngine;

namespace PeakRestApi.Game;

/// <summary>
/// A player that PEAKapi can see in the running game.
/// </summary>
internal sealed class PlayerHandle
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required object Character { get; init; }

    public required Vector3 Position { get; init; }

    public bool IsLocal { get; init; }

    public bool IsHost { get; init; }

    public bool IsAlive { get; init; }

    public bool IsGhost { get; init; }

    /// <summary>
    /// True when the character can receive an item: alive, not a ghost and not
    /// completely passed out.
    /// </summary>
    public bool CanReceiveItems => IsAlive && !IsGhost && !FullyPassedOut;

    public bool FullyPassedOut { get; init; }
}

/// <summary>
/// Enumerates the characters of the running level.
/// </summary>
internal sealed class PlayerService
{
    private readonly GameContext context;

    public PlayerService(GameContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// The characters currently present in the scene.
    /// </summary>
    public IReadOnlyList<PlayerHandle> Enumerate()
    {
        var types = context.Types;
        if (types.Character == null)
        {
            return Array.Empty<PlayerHandle>();
        }

        var characters = FindCharacters(types.Character);
        var local = GameMembers.GetStatic(types.Character, "localCharacter");
        var result = new List<PlayerHandle>(characters.Count);

        foreach (var character in characters)
        {
            if (character == null)
            {
                continue;
            }

            var view = GameMembers.Get(character, "view");
            var data = GameMembers.Get(character, "data");

            var dead = data != null && GameMembers.GetBool(data, "dead");
            var passedOut = data != null && GameMembers.GetBool(data, "fullyPassedOut");
            var ghost = GameMembers.GetBool(character, "IsGhost");
            var isLocal = ReferenceEquals(character, local) || (view != null && GameMembers.GetBool(view, "IsMine"));

            result.Add(new PlayerHandle
            {
                Id = BuildId(character, view),
                Name = GameMembers.GetString(character, "name"),
                Character = character,
                Position = ReadPosition(character),
                IsLocal = isLocal,
                IsHost = isLocal && IsHostPeer(),
                IsAlive = !dead,
                IsGhost = ghost,
                FullyPassedOut = passedOut,
            });
        }

        return result;
    }

    /// <summary>
    /// Finds a player by its session scoped id.
    /// </summary>
    public PlayerHandle? Find(string id) =>
        Enumerate().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Picks a random player that is eligible to receive an item.
    /// </summary>
    public PlayerHandle? PickEligible(System.Random random)
    {
        var eligible = Enumerate().Where(p => p.CanReceiveItems).ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        return eligible[random.Next(eligible.Count)];
    }

    private bool IsHostPeer() => context.Authority.IsHost;

    private static List<object> FindCharacters(Type characterType)
    {
        var found = new List<object>();

        // The game keeps the authoritative list of characters in a level.
        var all = GameMembers.GetStatic(characterType, "AllCharacters");
        if (all is System.Collections.IEnumerable list)
        {
            foreach (var character in list)
            {
                if (character != null)
                {
                    found.Add(character);
                }
            }

            if (found.Count > 0)
            {
                return found;
            }
        }

        try
        {
            var objects = UnityEngine.Object.FindObjectsByType(
                characterType,
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            foreach (var o in objects)
            {
                if (o != null)
                {
                    found.Add(o);
                }
            }
        }
        catch (Exception)
        {
            // The scene may not be ready yet; an empty list is the safe answer.
        }

        return found;
    }

    private static Vector3 ReadPosition(object character)
    {
        try
        {
            var transform = GameMembers.Get(character, "transform");
            if (transform is Transform t)
            {
                return t.position;
            }
        }
        catch (Exception)
        {
        }

        return Vector3.zero;
    }

    /// <summary>
    /// Builds a session scoped, opaque id for a character.
    /// </summary>
    private static string BuildId(object character, object? view)
    {
        if (view != null)
        {
            var owner = GameMembers.Get(view, "Owner");
            var actorId = GameMembers.Get(owner, "ActorNumber") ?? GameMembers.Get(owner, "ActorId");
            if (actorId is int id)
            {
                return "p" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        try
        {
            var unity = (UnityEngine.Object)character;
            return "i" + unity.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "u" + Math.Abs(character.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<PlayerInfo> Describe()
    {
        var result = new List<PlayerInfo>();
        foreach (var player in Enumerate())
        {
            result.Add(new PlayerInfo
            {
                Id = player.Id,
                Name = string.IsNullOrWhiteSpace(player.Name) ? "Player " + player.Id : player.Name,
                IsLocal = player.IsLocal,
                IsHost = player.IsHost,
                IsAlive = player.IsAlive && !player.IsGhost,
                IsSpectating = player.IsGhost,
            });
        }

        return result;
    }
}
