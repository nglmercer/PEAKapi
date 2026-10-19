using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Core.Models;
using UnityEngine;

namespace PeakRestApi.Game;

/// <summary>
/// Turns a requested position into a concrete, safe world position.
/// </summary>
/// <remarks>
/// Three modes are supported: <c>near_player</c>, <c>random_player</c> and
/// <c>world_position</c>. The resolver never silently falls back to an
/// unrelated place: when no acceptable position exists the request fails with
/// a 422 instead of spawning something in the wrong spot.
/// </remarks>
internal sealed class PositionResolver
{
    private const float MinPlayerDistance = 3f;
    private const float BodyRadius = 0.75f;
    private static readonly float[] Offsets = { 4f, 5.5f, 7f, 9f };

    private readonly GameContext context;
    private readonly PlayerService players;
    private readonly System.Random random = new System.Random();

    public PositionResolver(GameContext context, PlayerService players)
    {
        this.context = context;
        this.players = players;
    }

    /// <summary>
    /// Resolves a position request.
    /// </summary>
    /// <param name="request">The requested placement.</param>
    /// <param name="target">The player the placement is anchored to, when there is one.</param>
    /// <param name="position">The resolved world position.</param>
    /// <param name="reason">Why the request could not be satisfied.</param>
    public bool TryResolve(
        PositionRequest request,
        out Vector3 position,
        out PlayerHandle? target,
        out string? reason)
    {
        position = Vector3.zero;
        target = null;
        reason = null;

        var all = players.Enumerate();

        switch (request.Mode)
        {
            case "near_player":
            {
                if (string.IsNullOrWhiteSpace(request.PlayerId))
                {
                    reason = "near_player requires a playerId.";
                    return false;
                }

                target = players.Find(request.PlayerId!);
                if (target == null)
                {
                    reason = "The requested player is not part of this session.";
                    return false;
                }

                break;
            }

            case "random_player":
            {
                target = players.PickEligible(random);
                if (target == null)
                {
                    reason = "No player in this session can currently receive a spawn.";
                    return false;
                }

                break;
            }

            case "world_position":
            {
                var candidate = new Vector3((float)request.X!.Value, (float)request.Y!.Value, (float)request.Z!.Value);
                if (!IsSafe(candidate, all))
                {
                    reason = "The requested world position is occupied or too close to a player.";
                    return false;
                }

                position = candidate;
                return true;
            }

            default:
                reason = "Unknown position mode.";
                return false;
        }

        var anchor = target!.Position;
        foreach (var distance in Offsets)
        {
            for (var step = 0; step < 8; step++)
            {
                var angle = (float)(step * Math.PI / 4.0);
                var candidate = anchor + new Vector3(Mathf.Cos(angle) * distance, 0.2f, Mathf.Sin(angle) * distance);
                if (IsSafe(candidate, all))
                {
                    position = candidate;
                    return true;
                }
            }
        }

        reason = "No safe position was found near the requested player.";
        target = null;
        return false;
    }

    /// <summary>
    /// True when nothing blocks the position and no player is too close.
    /// </summary>
    private bool IsSafe(Vector3 candidate, IReadOnlyList<PlayerHandle> all)
    {
        foreach (var player in all)
        {
            var flat = new Vector3(candidate.x - player.Position.x, 0f, candidate.z - player.Position.z);
            if (flat.magnitude < MinPlayerDistance)
            {
                return false;
            }
        }

        if (!context.Config.SafePlacement)
        {
            return true;
        }

        return !IsInsideGeometry(candidate);
    }

    /// <summary>
    /// Uses the physics scene to detect geometry at the candidate position.
    /// </summary>
    /// <remarks>
    /// PEAK drives its characters with a custom controller, so this check only
    /// covers colliders that are present in the physics scene. It never reports
    /// a position as verified when the physics scene is empty.
    /// </remarks>
    private static bool IsInsideGeometry(Vector3 candidate)
    {
        try
        {
            var probe = candidate + Vector3.up * 0.6f;
            return Physics.CheckSphere(probe, BodyRadius, ~0, QueryTriggerInteraction.Ignore);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
