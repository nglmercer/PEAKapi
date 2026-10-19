// Adapted from https://github.com/nglmercer/REPOapi (MIT). See third-party/REPOapi-NOTICE.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakRestApi.Core.Game;
using PeakRestApi.Core.Models;
using PeakRestApi.Core.Security;

namespace PeakRestApi.Core.Http;

/// <summary>
/// Maps HTTP requests onto game operations. Every gameplay operation is
/// validated here and then executed on the Unity main thread.
/// </summary>
public sealed class ApiRouter
{
    private static readonly HashSet<string> ReadPaths = new(StringComparer.Ordinal)
    {
        "/health",
        "/api/version",
        "/api/capabilities",
        "/api/game/state",
        "/openapi.json",
        "/api/players",
        "/api/entities",
        "/api/catalog/items",
        "/api/catalog/entities",
        "/api/catalog/hazards",
    };

    private static readonly HashSet<string> WritePaths = new(StringComparer.Ordinal)
    {
        "/api/spawn/item",
        "/api/spawn/entity",
        "/api/spawn/hazard",
        "/api/player/effect",
        "/api/entities/despawn",
        "/api/entities/clear",
    };

    private static readonly Lazy<ApiResponse> OpenApi = new(() =>
    {
        using var stream = typeof(ApiRouter).Assembly.GetManifestResourceStream("openapi.json")
                           ?? throw new InvalidOperationException("The embedded openapi.json resource is missing.");
        using var reader = new StreamReader(stream);
        return ApiResponse.Raw(reader.ReadToEnd());
    });

    private readonly ApiOptions options;
    private readonly IGameBridge game;
    private readonly MainThreadDispatcher dispatcher;
    private readonly RateLimiter limiter;
    private readonly IdempotencyStore duplicates;
    private readonly Action<string>? log;

    public ApiRouter(
        ApiOptions options,
        IGameBridge game,
        MainThreadDispatcher dispatcher,
        Action<string>? log = null)
    {
        this.options = options;
        this.game = game;
        this.dispatcher = dispatcher;
        this.log = log;
        limiter = new RateLimiter(options.MaxRequestsPerSecond);
        duplicates = new IdempotencyStore();
    }

    public async Task<ApiResponse> Handle(
        string method,
        string target,
        string? authorization,
        string? idempotencyKey = null,
        string body = "",
        string? contentType = null)
    {
        try
        {
            if (options.RequireAuth && !AuthenticationMiddleware.Authenticate(authorization, options.Token))
            {
                throw new ApiException(401, "UNAUTHORIZED", "A valid bearer token is required.");
            }

            if (!limiter.Allow())
            {
                throw new ApiException(429, "RATE_LIMITED", "Request rate exceeded; retry later.");
            }

            if (Encoding.UTF8.GetByteCount(body) > options.MaxRequestBytes)
            {
                throw new ApiException(413, "PAYLOAD_TOO_LARGE", "Request body exceeds the configured limit.");
            }

            var split = target.IndexOf('?');
            var path = split < 0 ? target : target.Substring(0, split);
            var search = ParseQuery(path, target, split);

            var isRead = ReadPaths.Contains(path);
            var isWrite = WritePaths.Contains(path);
            if (!isRead && !isWrite)
            {
                throw new ApiException(404, "NOT_FOUND", "Unknown endpoint.");
            }

            if (isRead && !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse.MethodNotAllowed("GET");
            }

            if (isWrite && !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse.MethodNotAllowed("POST");
            }

            if (path == "/openapi.json")
            {
                return OpenApi.Value;
            }

            if (isRead)
            {
                return await dispatcher.Execute(() => Read(path, search), options.RequestTimeoutMs).ConfigureAwait(false);
            }

            if (contentType == null
                || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(415, "UNSUPPORTED_MEDIA_TYPE", "POST requires application/json.");
            }

            // The key is required on every mutation, so it is checked before
            // the body is parsed.
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey!.Length > 128)
            {
                throw new ApiException(
                    400,
                    "IDEMPOTENCY_KEY_REQUIRED",
                    "POST requires Idempotency-Key (1-128 characters). Retain it when retrying.");
            }

            var request = StrictJson.ParseObject(body);
            if (StrictJson.ContainsNonFiniteNumber(request))
            {
                throw new ApiException(400, "INVALID_JSON", "Numbers must be finite; NaN and Infinity are not accepted.");
            }

            Validate(path, request);

            var generation = game.Generation;
            var fingerprint = path + "\n" + request.ToString(Formatting.None, Array.Empty<JsonConverter>());

            return await duplicates
                .Run(
                    idempotencyKey,
                    fingerprint,
                    () => dispatcher.Execute(() => Mutate(path, request, generation), options.RequestTimeoutMs))
                .ConfigureAwait(false);
        }
        catch (ApiException e)
        {
            log?.Invoke(e.Code);
            return ApiResponse.Error(e);
        }
        catch (Exception e)
        {
            log?.Invoke("Request failed: " + e.GetType().Name);
            return ApiResponse.Error(new ApiException(500, "INTERNAL_ERROR", "Request failed; inspect the game log."));
        }
    }

    private static string? ParseQuery(string path, string target, int split)
    {
        if (split < 0)
        {
            return null;
        }

        var category = CatalogCategory(path);
        if (category == null)
        {
            throw new ApiException(400, "INVALID_QUERY", "Filters are only supported on catalog endpoints.");
        }

        string? search = null;
        var queryKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in target.Substring(split + 1).Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            var p = pair.Split(new[] { '=' }, 2);
            if (!queryKeys.Add(p[0]))
            {
                throw new ApiException(400, "INVALID_QUERY", "Duplicate query filters are not supported.");
            }

            if (p[0] != "search" && p[0] != "category")
            {
                throw new ApiException(400, "INVALID_QUERY", "Only search and category filters are supported.");
            }

            if (p[0] == "search")
            {
                search = Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : string.Empty);
                if (search.Length > 128)
                {
                    throw new ApiException(400, "INVALID_QUERY", "The search filter is too long.");
                }
            }

            if (p[0] == "category" && (p.Length < 2 || p[1] != category))
            {
                throw new ApiException(400, "INVALID_QUERY", "category must match the catalog endpoint.");
            }
        }

        return search;
    }

    private static string? CatalogCategory(string path) =>
        path == "/api/catalog/items" ? "items"
        : path == "/api/catalog/entities" ? "entities"
        : path == "/api/catalog/hazards" ? "hazards"
        : null;

    private ApiResponse Read(string path, string? search)
    {
        switch (path)
        {
            case "/api/version":
                return new ApiResponse(200, game.Versions());
            case "/api/capabilities":
                return new ApiResponse(200, new { features = game.Capabilities() });
            case "/api/entities":
                return new ApiResponse(200, new { entities = game.ActiveInstances() });
            case "/api/players":
                return new ApiResponse(200, new { players = game.Players() });
        }

        if (path == "/health" || path == "/api/game/state")
        {
            var s = game.State();
            var ready = s.GameRunning && s.InLevel;
            if (path == "/health")
            {
                return new ApiResponse(200, new
                {
                    status = "ready",
                    apiVersion = "1.0.0",
                    gameRunning = s.GameRunning,
                    inLevel = s.InLevel,
                    isHost = s.IsHost,
                    gameplayReady = ready,
                });
            }

            return new ApiResponse(200, new
            {
                gameRunning = s.GameRunning,
                inLevel = s.InLevel,
                isHost = s.IsHost,
                gameplayReady = ready,
                scene = s.Scene,
                sessionId = s.SessionId,
            });
        }

        var category = CatalogCategory(path);
        if (category == null)
        {
            throw new ApiException(404, "NOT_FOUND", "Unknown endpoint.");
        }

        var kind = category == "items" ? CatalogKind.Items
            : category == "entities" ? CatalogKind.Entities
            : CatalogKind.Hazards;

        return new ApiResponse(200, new Dictionary<string, object>
        {
            [category] = game.Catalog(kind, search),
        });
    }

    private ApiResponse Mutate(string path, JObject request, long generation)
    {
        var state = game.State();

        if (game.Generation != generation)
        {
            throw new ApiException(
                409,
                "GAME_SESSION_CHANGED",
                "The game session changed while this command was queued; refresh game state and retry with a new key.");
        }

        if (!state.GameRunning)
        {
            throw new ApiException(409, "GAME_NOT_READY", "The game is not running.");
        }

        if (!state.InLevel)
        {
            throw new ApiException(409, "GAME_NOT_READY", "No active gameplay level is available.");
        }

        if (options.RequireHost && !state.IsHost)
        {
            throw new ApiException(409, "HOST_REQUIRED", "This action requires the room host.");
        }

        var capabilities = game.Capabilities();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            object result;
            switch (path)
            {
                case "/api/spawn/item":
                    Require(options.AllowItemSpawns, capabilities.Items, "Item spawning is disabled by configuration.");
                    result = game.SpawnItem(ReadItemRequest(request));
                    break;
                case "/api/spawn/entity":
                    Require(options.AllowEntitySpawns, capabilities.Entities, "Entity spawning is disabled by configuration.");
                    result = game.SpawnEntity(ReadEntityRequest(request));
                    break;
                case "/api/spawn/hazard":
                    Require(options.AllowHazardSpawns, capabilities.Hazards, "Hazard spawning is disabled by configuration.");
                    result = game.SpawnHazard(ReadHazardRequest(request));
                    break;
                case "/api/player/effect":
                    Require(true, capabilities.Effects, "Player effects are not supported.");
                    result = game.ApplyPlayerEffect(ReadEffectRequest(request));
                    break;
                case "/api/entities/despawn":
                    result = game.Despawn(request.Value<string>("instanceId"));
                    break;
                default:
                    result = game.Clear();
                    break;
            }

            return new ApiResponse(200, new { success = true, result });
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception e)
        {
            log?.Invoke("Game action failed: " + e.GetType().Name + ": " + e.Message);
            throw new ApiException(500, "ACTION_FAILED", "The game action failed; inspect the game log.");
        }
        finally
        {
            if (options.Verbose)
            {
                log?.Invoke(path + " completed in " + clock.ElapsedMilliseconds + "ms");
            }
        }
    }

    private static void Require(bool enabled, FeatureCapability capability, string disabledMessage)
    {
        if (!enabled)
        {
            throw new ApiException(501, "UNSUPPORTED_ACTION", disabledMessage);
        }

        if (!capability.Available)
        {
            throw new ApiException(
                501,
                "UNSUPPORTED_ACTION",
                capability.Reason ?? "This action is not supported by the installed game version.");
        }
    }

    private SpawnItemRequest ReadItemRequest(JObject request) => new()
    {
        ItemId = request.Value<string>("itemId")!,
        Quantity = request.Value<int?>("quantity") ?? 1,
        Position = ReadPosition(request["position"]),
    };

    private SpawnEntityRequest ReadEntityRequest(JObject request) => new()
    {
        EntityId = request.Value<string>("entityId")!,
        Quantity = request.Value<int?>("quantity") ?? 1,
        Position = ReadPosition(request["position"]),
        LifetimeSeconds = ReadLifetime(request["lifetimeSeconds"]),
    };

    private SpawnHazardRequest ReadHazardRequest(JObject request) => new()
    {
        HazardId = request.Value<string>("hazardId")!,
        Quantity = request.Value<int?>("quantity") ?? 1,
        Position = ReadPosition(request["position"]),
        LifetimeSeconds = ReadLifetime(request["lifetimeSeconds"]),
    };

    private PlayerEffectRequest ReadEffectRequest(JObject request) => new()
    {
        EffectId = request.Value<string>("effectId")!,
        PlayerId = request.Value<string>("playerId")!,
    };

    private static double? ReadLifetime(JToken? token)
    {
        if (token == null)
        {
            return null;
        }

        return token.Type == JTokenType.Integer ? token.Value<long>() : (double?)token.Value<double>();
    }

    private static PositionRequest ReadPosition(JToken? token)
    {
        if (!(token is JObject p))
        {
            throw new ApiException(400, "INVALID_POSITION", "A position object is required.");
        }

        var position = new PositionRequest
        {
            Mode = p.Value<string>("mode") ?? string.Empty,
            PlayerId = p.Value<string>("playerId"),
        };

        if (p["x"] != null)
        {
            position.X = ToDouble(p["x"]!);
        }

        if (p["y"] != null)
        {
            position.Y = ToDouble(p["y"]!);
        }

        if (p["z"] != null)
        {
            position.Z = ToDouble(p["z"]!);
        }

        position.Validate();
        return position;
    }

    private static double ToDouble(JToken token) =>
        token.Type == JTokenType.Integer ? token.Value<long>() : token.Value<double>();

    private void Validate(string path, JObject r)
    {
        string[] allowed;
        switch (path)
        {
            case "/api/spawn/item":
                allowed = new[] { "itemId", "quantity", "position" };
                break;
            case "/api/spawn/entity":
                allowed = new[] { "entityId", "quantity", "position", "lifetimeSeconds" };
                break;
            case "/api/spawn/hazard":
                allowed = new[] { "hazardId", "quantity", "position", "lifetimeSeconds" };
                break;
            case "/api/player/effect":
                allowed = new[] { "effectId", "playerId" };
                break;
            case "/api/entities/despawn":
                allowed = new[] { "instanceId" };
                break;
            default:
                allowed = new[] { "kind" };
                break;
        }

        // The shape of the request is checked before its content, so a
        // malformed request always reports the structural problem first.
        if (r.Properties().Any(p => !allowed.Contains(p.Name)))
        {
            throw new ApiException(400, "UNKNOWN_FIELD", "Request contains an unknown field.");
        }

        switch (path)
        {
            case "/api/spawn/item":
                RequiredString(r, "itemId");
                ValidateQuantity(r);
                ValidatePosition(r["position"]);
                break;
            case "/api/spawn/entity":
                RequiredString(r, "entityId");
                ValidateQuantity(r);
                ValidatePosition(r["position"]);
                ValidateLifetime(r["lifetimeSeconds"]);
                break;
            case "/api/spawn/hazard":
                RequiredString(r, "hazardId");
                ValidateQuantity(r);
                ValidatePosition(r["position"]);
                ValidateLifetime(r["lifetimeSeconds"]);
                break;
            case "/api/player/effect":
                RequiredString(r, "effectId");
                RequiredString(r, "playerId");
                break;
            case "/api/entities/despawn":
                RequiredString(r, "instanceId");
                break;
            default:
                if (r["kind"] != null)
                {
                    var kind = r.Value<string>("kind");
                    if (kind != "all" && kind != "entity" && kind != "hazard")
                    {
                        throw new ApiException(400, "INVALID_REQUEST", "kind must be one of all, entity or hazard.");
                    }
                }

                break;
        }
    }

    private void ValidateQuantity(JObject r)
    {
        if (r["quantity"] == null)
        {
            return;
        }

        if (r["quantity"]!.Type != JTokenType.Integer || !InRange(r["quantity"]!, 1, options.MaxSpawnQuantity))
        {
            throw new ApiException(
                400,
                "INVALID_QUANTITY",
                "quantity must be an integer from 1 to " + options.MaxSpawnQuantity + ".");
        }
    }

    private static void ValidateLifetime(JToken? token)
    {
        if (token == null)
        {
            return;
        }

        if ((token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            || !InRange(token, 1, 300))
        {
            throw new ApiException(400, "INVALID_LIFETIME", "lifetimeSeconds must be a number from 1 to 300.");
        }
    }

    private static bool InRange(JToken value, long min, long max) =>
        long.TryParse(
            value.ToString(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number)
        && number >= min
        && number <= max;

    private static void RequiredString(JObject r, string key)
    {
        if (r[key]?.Type != JTokenType.String
            || string.IsNullOrWhiteSpace(r.Value<string>(key))
            || r.Value<string>(key)!.Length > 256)
        {
            throw new ApiException(
                400,
                "INVALID_REQUEST",
                key + " must be a nonempty string of at most 256 characters.");
        }
    }

    private static void ValidatePosition(JToken? token)
    {
        if (!(token is JObject p))
        {
            throw new ApiException(400, "INVALID_POSITION", "A position object with mode is required.");
        }

        if (p.Properties().Any(v => !new[] { "mode", "playerId", "x", "y", "z" }.Contains(v.Name)))
        {
            throw new ApiException(400, "INVALID_POSITION", "The position object contains an unknown field.");
        }

        RequiredString(p, "mode");
        if (p["playerId"] != null)
        {
            RequiredString(p, "playerId");
        }

        foreach (var axis in new[] { "x", "y", "z" })
        {
            if (p[axis] != null && p[axis]!.Type != JTokenType.Float && p[axis]!.Type != JTokenType.Integer)
            {
                throw new ApiException(400, "INVALID_POSITION", "Coordinates must be numbers.");
            }
        }

        var mode = p.Value<string>("mode");
        var allowed = mode == "near_player" ? new[] { "mode", "playerId" }
            : mode == "world_position" ? new[] { "mode", "x", "y", "z" }
            : new[] { "mode" };

        if (p.Properties().Any(v => !allowed.Contains(v.Name)))
        {
            throw new ApiException(400, "INVALID_POSITION", "Position fields must match the selected mode.");
        }
    }
}
