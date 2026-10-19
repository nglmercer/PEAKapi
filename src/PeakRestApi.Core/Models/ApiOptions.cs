using System;
using System.Net;

namespace PeakRestApi.Core.Models;

/// <summary>
/// Runtime configuration for the REST surface. Values come from the BepInEx
/// configuration file <c>org.peak.rest-api.cfg</c>.
/// </summary>
public sealed class ApiOptions
{
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 17454;

    public bool RequireAuth { get; set; } = true;

    public bool AllowRemote { get; set; }

    public string Token { get; set; } = string.Empty;

    public int MaxRequestsPerSecond { get; set; } = 20;

    public int MaxSpawnQuantity { get; set; } = 5;

    public int MaxActiveEntities { get; set; } = 8;

    public int MaxActiveHazards { get; set; } = 4;

    public int MaxRequestBytes { get; set; } = 16384;

    public int QueueCapacity { get; set; } = 64;

    public int RequestTimeoutMs { get; set; } = 5000;

    public int CommandsPerFrame { get; set; } = 2;

    public bool AllowItemSpawns { get; set; } = true;

    public bool AllowEntitySpawns { get; set; } = true;

    public bool AllowHazardSpawns { get; set; } = true;

    public bool RequireHost { get; set; } = true;

    public bool SafePlacement { get; set; } = true;

    public bool Verbose { get; set; }

    /// <summary>
    /// Validates the configuration and throws when the combination is unsafe.
    /// </summary>
    public void Validate()
    {
        if (!IPAddress.TryParse(Host, out var address))
        {
            throw new ArgumentException("Host must be a literal IP address.");
        }

        if (!IPAddress.IsLoopback(address) && (!AllowRemote || !RequireAuth || Token.Length < 43))
        {
            throw new ArgumentException(
                "Remote binding requires AllowRemote, authentication and a token of at least 43 characters.");
        }

        if (RequireAuth && Token.Length < 43)
        {
            throw new ArgumentException("Authentication requires a strong token of at least 43 characters.");
        }

        if (Port < 1 || Port > 65535
            || MaxRequestsPerSecond < 1 || MaxRequestsPerSecond > 1000
            || MaxSpawnQuantity < 1 || MaxSpawnQuantity > 100
            || MaxActiveEntities < 0 || MaxActiveEntities > 1000
            || MaxActiveHazards < 0 || MaxActiveHazards > 1000
            || MaxRequestBytes < 128 || MaxRequestBytes > 1048576
            || QueueCapacity < 1 || QueueCapacity > 4096
            || RequestTimeoutMs < 100 || RequestTimeoutMs > 60000
            || CommandsPerFrame < 1 || CommandsPerFrame > 10)
        {
            throw new ArgumentException("Configuration limits are out of range.");
        }
    }
}
