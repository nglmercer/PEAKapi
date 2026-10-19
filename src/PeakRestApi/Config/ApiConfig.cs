using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using PeakRestApi.Core.Http;
using PeakRestApi.Core.Models;
using PeakRestApi.Game;

namespace PeakRestApi.Config;

/// <summary>
/// The BepInEx configuration of PEAKapi.
/// </summary>
/// <remarks>
/// The file lives next to the other BepInEx configuration files, at
/// <c>BepInEx/config/org.peak.rest-api.cfg</c>. The authentication token is
/// generated once, with a cryptographically secure random source, and is
/// never written to the log.
/// </remarks>
internal sealed class ApiConfig
{
    public const string FileName = "org.peak.rest-api.cfg";

    private readonly ConfigFile file;

    public ApiConfig()
    {
        // The file must live in BepInEx/config so that the token can be
        // imported by other tools without guessing where it is.
        var path = Path.Combine(Paths.ConfigPath, FileName);
        file = new ConfigFile(path, true);

        Enabled = file.Bind("HTTP", "Enabled", true, "Start the embedded REST server.");
        Host = file.Bind("HTTP", "Host", "127.0.0.1", "The interface the REST server binds to. Use a literal IP address.");
        Port = file.Bind("HTTP", "Port", 17454, "The port the REST server binds to.");
        RequireAuth = file.Bind("HTTP", "RequireAuth", true, "Require a bearer token on every request.");
        AllowRemote = file.Bind("HTTP", "AllowRemote", false, "Allow binding to a non loopback address. This exposes the API to the network.");
        AuthenticationToken = file.Bind("HTTP", "AuthenticationToken", string.Empty, "The bearer token. Generated on first start when left empty.");

        MaxRequestsPerSecond = file.Bind("Limits", "MaxRequestsPerSecond", 20, "Server side request rate limit.");
        MaxSpawnQuantity = file.Bind("Limits", "MaxSpawnQuantity", 5, "The largest quantity a single spawn request may ask for.");
        MaxActiveEntities = file.Bind("Limits", "MaxActiveEntities", 8, "The largest number of API owned entities that may exist at once.");
        MaxActiveHazards = file.Bind("Limits", "MaxActiveHazards", 4, "The largest number of API owned hazards that may exist at once.");
        MaxRequestBytes = file.Bind("Limits", "MaxRequestBytes", 16384, "The largest accepted request body, in bytes.");
        QueueCapacity = file.Bind("Limits", "QueueCapacity", 64, "The number of gameplay commands that may wait for the main thread.");
        RequestTimeoutMs = file.Bind("Limits", "RequestTimeoutMs", 5000, "How long a queued command may wait before it expires.");
        CommandsPerFrame = file.Bind("Limits", "CommandsPerFrame", 2, "How many gameplay commands are executed per frame.");

        AllowItemSpawns = file.Bind("Gameplay", "AllowItemSpawns", true, "Allow item spawns.");
        AllowEntitySpawns = file.Bind("Gameplay", "AllowEntitySpawns", true, "Allow entity spawns.");
        AllowHazardSpawns = file.Bind("Gameplay", "AllowHazardSpawns", true, "Allow hazard spawns.");
        RequireHost = file.Bind("Gameplay", "RequireHost", true, "Only the room host may change the world.");
        SafePlacement = file.Bind("Gameplay", "SafePlacement", true, "Reject spawn positions that are occupied or too close to a player.");

        Verbose = file.Bind("Diagnostics", "Verbose", false, "Log extra diagnostics.");
    }

    public ConfigEntry<bool> Enabled { get; }

    public ConfigEntry<string> Host { get; }

    public ConfigEntry<int> Port { get; }

    public ConfigEntry<bool> RequireAuth { get; }

    public ConfigEntry<bool> AllowRemote { get; }

    public ConfigEntry<string> AuthenticationToken { get; }

    public ConfigEntry<int> MaxRequestsPerSecond { get; }

    public ConfigEntry<int> MaxSpawnQuantity { get; }

    public ConfigEntry<int> MaxActiveEntities { get; }

    public ConfigEntry<int> MaxActiveHazards { get; }

    public ConfigEntry<int> MaxRequestBytes { get; }

    public ConfigEntry<int> QueueCapacity { get; }

    public ConfigEntry<int> RequestTimeoutMs { get; }

    public ConfigEntry<int> CommandsPerFrame { get; }

    public ConfigEntry<bool> AllowItemSpawns { get; }

    public ConfigEntry<bool> AllowEntitySpawns { get; }

    public ConfigEntry<bool> AllowHazardSpawns { get; }

    public ConfigEntry<bool> RequireHost { get; }

    public ConfigEntry<bool> SafePlacement { get; }

    public ConfigEntry<bool> Verbose { get; }

    /// <summary>
    /// The generated token, creating and persisting one on first start.
    /// </summary>
    public string EnsureToken()
    {
        var token = AuthenticationToken.Value;
        if (!string.IsNullOrWhiteSpace(token) && token.Length >= 43)
        {
            return token;
        }

        token = AuthenticationMiddleware.GenerateToken();
        AuthenticationToken.Value = token;
        file.Save();
        return token;
    }

    /// <summary>
    /// The effective runtime options.
    /// </summary>
    public ApiOptions ToOptions()
    {
        return new ApiOptions
        {
            Enabled = Enabled.Value,
            Host = Host.Value,
            Port = Port.Value,
            RequireAuth = RequireAuth.Value,
            AllowRemote = AllowRemote.Value,
            Token = EnsureToken(),
            MaxRequestsPerSecond = MaxRequestsPerSecond.Value,
            MaxSpawnQuantity = MaxSpawnQuantity.Value,
            MaxActiveEntities = MaxActiveEntities.Value,
            MaxActiveHazards = MaxActiveHazards.Value,
            MaxRequestBytes = MaxRequestBytes.Value,
            QueueCapacity = QueueCapacity.Value,
            RequestTimeoutMs = RequestTimeoutMs.Value,
            CommandsPerFrame = CommandsPerFrame.Value,
            AllowItemSpawns = AllowItemSpawns.Value,
            AllowEntitySpawns = AllowEntitySpawns.Value,
            AllowHazardSpawns = AllowHazardSpawns.Value,
            RequireHost = RequireHost.Value,
            SafePlacement = SafePlacement.Value,
            Verbose = Verbose.Value,
        };
    }

    /// <summary>
    /// The game facing subset of the configuration.
    /// </summary>
    public ApiConfigLike ToGameConfig() => new()
    {
        RequireHost = RequireHost.Value,
        SafePlacement = SafePlacement.Value,
        MaxActiveEntities = MaxActiveEntities.Value,
        MaxActiveHazards = MaxActiveHazards.Value,
        Verbose = Verbose.Value,
    };

    /// <summary>
    /// A token safe description for the log.
    /// </summary>
    public string DescribeToken() => AuthenticationMiddleware.Redact(EnsureToken());

    /// <summary>
    /// Generates a token without persisting it. Used by the tests.
    /// </summary>
    public static string GenerateToken() => AuthenticationMiddleware.GenerateToken();

    /// <summary>
    /// A random helper kept for symmetry with the token generation.
    /// </summary>
    internal static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
    }
}
