using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakRestApi.Game;

/// <summary>
/// Answers the question the whole API depends on: is the game in a state where
/// gameplay may be changed right now?
/// </summary>
internal sealed class GameStateService
{
    private static readonly string[] MenuSceneHints =
    {
        "menu",
        "airport",
        "lobby",
        "boot",
        "loading",
        "title",
    };

    private readonly GameContext context;
    private readonly PlayerService players;

    public GameStateService(GameContext context, PlayerService players)
    {
        this.context = context;
        this.players = players;
    }

    /// <summary>
    /// The current scene name, or "unknown".
    /// </summary>
    public string Scene
    {
        get
        {
            try
            {
                var scene = SceneManager.GetActiveScene();
                return string.IsNullOrEmpty(scene.name) ? "unknown" : scene.name;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }

    /// <summary>
    /// True when a gameplay level is loaded.
    /// </summary>
    /// <remarks>
    /// A level is considered loaded when a local character exists and the game
    /// is not in the airport. The game tracks the airport itself in
    /// <c>GameUtils.m_inAirport</c>; when that member cannot be read the
    /// scene name is used as a conservative fallback.
    /// </remarks>
    public bool InLevel
    {
        get
        {
            var types = context.Types;
            if (types.Character == null)
            {
                return false;
            }

            var local = GameMembers.GetStatic(types.Character, "localCharacter");
            if (local == null)
            {
                return false;
            }

            if (types.GameUtils != null)
            {
                var gameUtils = GameMembers.GetStatic(types.GameUtils, "instance");
                if (gameUtils != null)
                {
                    var inAirport = GameMembers.Get(gameUtils, "m_inAirport");
                    if (inAirport is bool airport)
                    {
                        return !airport;
                    }
                }
            }

            var scene = Scene;
            foreach (var hint in MenuSceneHints)
            {
                if (scene.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public bool IsHost => context.Authority.IsHost;

    /// <summary>
    /// A session scoped identifier for the current room and level.
    /// </summary>
    public string SessionId
    {
        get
        {
            var parts = new List<string> { Scene };

            if (context.Types.PhotonNetwork != null)
            {
                var local = GameMembers.GetStatic(context.Types.PhotonNetwork, "LocalPlayer");
                var actor = GameMembers.Get(local, "ActorNumber") ?? GameMembers.Get(local, "ActorId");
                if (actor is int id)
                {
                    parts.Add("a" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            return string.Join("-", parts);
        }
    }

    public GameSnapshot Snapshot()
    {
        return new GameSnapshot
        {
            GameRunning = true,
            InLevel = InLevel,
            IsHost = IsHost,
            Scene = Scene,
            SessionId = SessionId,
        };
    }
}
