using System;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Security;
using PeakRestApi.Networking;

namespace PeakRestApi.Game;

/// <summary>
/// The shared state every game facing service needs.
/// </summary>
internal sealed class GameContext
{
    public GameContext(
        GameTypes types,
        ApiConfigLike config,
        PhotonAuthority authority,
        SpawnOwnershipRegistry ownership,
        SpawnQuotaRegistry quotas,
        Action<string> log)
    {
        Types = types;
        Config = config;
        Authority = authority;
        Ownership = ownership;
        Quotas = quotas;
        Log = log;
    }

    public GameTypes Types { get; }

    public ApiConfigLike Config { get; }

    public PhotonAuthority Authority { get; }

    public SpawnOwnershipRegistry Ownership { get; }

    public SpawnQuotaRegistry Quotas { get; }

    public Action<string> Log { get; }
}

/// <summary>
/// The subset of the plugin configuration the game layer needs.
/// </summary>
internal sealed class ApiConfigLike
{
    public bool RequireHost { get; set; } = true;

    public bool SafePlacement { get; set; } = true;

    public int MaxActiveEntities { get; set; } = 8;

    public int MaxActiveHazards { get; set; } = 4;

    public bool Verbose { get; set; }
}
