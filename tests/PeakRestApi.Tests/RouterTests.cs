using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PeakRestApi.Core.Http;
using PeakRestApi.Core.Models;
using Xunit;

namespace PeakRestApi.Tests;

public sealed class RouterTests
{
    private static JObject Parse(string json) => JObject.Parse(json);

    [Fact]
    public async Task RejectsRequestsWithoutAToken()
    {
        using var host = new TestHost();

        var response = await host.Send("GET", "/health", token: null);

        Assert.Equal(401, response.Status);
        Assert.Equal("UNAUTHORIZED", Parse(response.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task RejectsRequestsWithTheWrongToken()
    {
        using var host = new TestHost();

        var response = await host.Send("GET", "/health", token: new string('x', 43));

        Assert.Equal(401, response.Status);
    }

    [Fact]
    public async Task ServesHealthForAnAuthenticatedRequest()
    {
        using var host = new TestHost();

        var response = await host.Send("GET", "/health");
        var body = Parse(response.Json);

        Assert.Equal(200, response.Status);
        Assert.Equal("ready", body["status"]?.ToString());
        Assert.Equal("1.0.0", body["apiVersion"]?.ToString());
        Assert.True(body["gameRunning"]?.Value<bool>());
        Assert.True(body["inLevel"]?.Value<bool>());
        Assert.True(body["isHost"]?.Value<bool>());
        Assert.True(body["gameplayReady"]?.Value<bool>());
    }

    [Fact]
    public async Task ReportsGameplayAsNotReadyOutsideOfALevel()
    {
        using var host = new TestHost();
        host.Game.InLevel = false;

        var response = await host.Send("GET", "/health");
        var body = Parse(response.Json);

        Assert.False(body["gameplayReady"]?.Value<bool>());
    }

    [Fact]
    public async Task ReportsGameplayAsNotReadyWithoutHostAuthority()
    {
        using var host = new TestHost();
        host.Game.IsHost = false;

        var response = await host.Send("GET", "/health");
        var body = Parse(response.Json);

        // The published contract says that gameplayReady is false without
        // host authority, because a guest may not change the world.
        Assert.False(body["gameplayReady"]?.Value<bool>());
        Assert.False(body["isHost"]?.Value<bool>());
        Assert.True(body["inLevel"]?.Value<bool>());
    }

    [Fact]
    public async Task ReportsGameplayAsReadyForTheHostInALevel()
    {
        using var host = new TestHost();

        var response = await host.Send("GET", "/health");
        var body = Parse(response.Json);

        Assert.True(body["gameplayReady"]?.Value<bool>());
        Assert.True(body["hostAuthority"]?.Value<bool>());
        Assert.True(body["authorityKnown"]?.Value<bool>());
    }

    [Fact]
    public async Task ReportsTheReadinessBreakdown()
    {
        using var host = new TestHost();
        host.Game.InLevel = false;

        var response = await host.Send("GET", "/health");
        var body = Parse(response.Json);

        // Every reason is told apart, and nothing claims a state that was
        // not observed.
        Assert.True(body["serverAlive"]?.Value<bool>());
        Assert.True(body["gameRunning"]?.Value<bool>());
        Assert.True(body["gameInitialized"]?.Value<bool>());
        Assert.True(body["sceneLoaded"]?.Value<bool>());
        Assert.False(body["inLevel"]?.Value<bool>());
        Assert.False(body["gameplayReady"]?.Value<bool>());
        Assert.Equal("present", body["eosState"]?.ToString());
    }

    [Fact]
    public async Task ReportsTheGameAsNotRunningWhenItIsNotInitialized()
    {
        using var host = new TestHost();
        host.Game.GameInitialized = false;

        var response = await host.Send("GET", "/health");
        var body = Parse(response.Json);

        Assert.False(body["gameRunning"]?.Value<bool>());
        Assert.False(body["gameInitialized"]?.Value<bool>());
        Assert.False(body["gameplayReady"]?.Value<bool>());
        Assert.Equal("unknown", body["eosState"]?.ToString());
    }

    [Fact]
    public async Task ReportsAnUnknownEosStateWhenTheGameIsNotLoaded()
    {
        using var host = new TestHost();
        host.Game.GameInitialized = false;

        var response = await host.Send("GET", "/health");
        var body = Parse(response.Json);

        Assert.Equal("unknown", body["eosState"]?.ToString());
    }

    [Fact]
    public async Task ReportsTheGameState()
    {
        using var host = new TestHost();
        host.Game.IsHost = false;

        var response = await host.Send("GET", "/api/game/state");
        var body = Parse(response.Json);

        Assert.Equal(200, response.Status);
        Assert.False(body["gameplayReady"]?.Value<bool>());
        Assert.True(body["authorityKnown"]?.Value<bool>());
        Assert.Equal("WilIsland", body["scene"]?.ToString());
    }

    [Fact]
    public async Task ServesTheVersionReport()
    {
        using var host = new TestHost();

        var response = await host.Send("GET", "/api/version");
        var body = Parse(response.Json);

        Assert.Equal(200, response.Status);
        Assert.Equal("1.0.0", body["apiVersion"]?.ToString());
        Assert.Equal("2.6.b", body["gameVersion"]?.ToString());
    }

    [Fact]
    public async Task ServesGranularCapabilities()
    {
        using var host = new TestHost();
        host.Game.EntitiesAvailable = false;

        var response = await host.Send("GET", "/api/capabilities");
        var features = Parse(response.Json)["features"]!;

        Assert.True(features["items"]!["available"]?.Value<bool>());
        Assert.False(features["entities"]!["available"]?.Value<bool>());
        Assert.Equal("entities disabled", features["entities"]!["reason"]?.ToString());
        Assert.False(features["multiplayerReplication"]!["verified"]?.Value<bool>());
    }

    [Fact]
    public async Task ServesTheGameState()
    {
        using var host = new TestHost();

        var response = await host.Send("GET", "/api/game/state");
        var body = Parse(response.Json);

        Assert.Equal(200, response.Status);
        Assert.Equal("WilIsland", body["scene"]?.ToString());
    }

    [Fact]
    public async Task ServesThePlayerList()
    {
        using var host = new TestHost();
        host.Game.AddPlayer("p1", "Scout");

        var response = await host.Send("GET", "/api/players");
        var players = Parse(response.Json)["players"]!;

        Assert.Single(players);
        Assert.Equal("p1", players[0]!["id"]?.ToString());
    }

    [Fact]
    public async Task RejectsUnknownPaths()
    {
        using var host = new TestHost();

        var response = await host.Send("GET", "/api/nope");

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task RejectsTheWrongVerbAndAdvertisesTheRightOne()
    {
        using var host = new TestHost();

        var read = await host.Send("POST", "/health");
        Assert.Equal(405, read.Status);
        Assert.Equal("GET", read.Allow);

        var write = await host.Send("GET", "/api/spawn/item");
        Assert.Equal(405, write.Status);
        Assert.Equal("POST", write.Allow);
    }

    [Fact]
    public async Task RequiresJsonContentTypeForMutations()
    {
        using var host = new TestHost();

        var response = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key",
            body: "{\"itemId\":\"x\"}",
            contentType: "text/plain");

        Assert.Equal(415, response.Status);
    }

    [Fact]
    public async Task RejectsDuplicateJsonProperties()
    {
        using var host = new TestHost();

        var response = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key",
            body: "{\"itemId\":\"a\",\"itemId\":\"b\"}",
            contentType: "application/json");

        Assert.Equal(400, response.Status);
        Assert.Equal("INVALID_JSON", Parse(response.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task RejectsJsonComments()
    {
        using var host = new TestHost();

        var response = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key",
            body: "{/* comment */\"itemId\":\"a\"}",
            contentType: "application/json");

        Assert.Equal(400, response.Status);
    }

    [Fact]
    public async Task RejectsNonFiniteNumbers()
    {
        using var host = new TestHost();

        var response = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key",
            body: "{\"itemId\":\"a\",\"quantity\":NaN}",
            contentType: "application/json");

        Assert.Equal(400, response.Status);
    }

    [Fact]
    public async Task RejectsUnknownFields()
    {
        using var host = new TestHost();

        var response = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key",
            body: "{\"itemId\":\"a\",\"surprise\":1}",
            contentType: "application/json");

        Assert.Equal(400, response.Status);
        Assert.Equal("UNKNOWN_FIELD", Parse(response.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task RejectsAnOversizedBody()
    {
        using var host = new TestHost(o => o.MaxRequestBytes = 256);

        var response = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key",
            body: new string('x', 512),
            contentType: "application/json");

        Assert.Equal(413, response.Status);
    }

    [Fact]
    public async Task RequiresAnIdempotencyKeyForMutations()
    {
        using var host = new TestHost();

        var response = await host.Send(
            "POST",
            "/api/spawn/item",
            body: "{\"itemId\":\"a\"}",
            contentType: "application/json");

        Assert.Equal(400, response.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", Parse(response.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task RunsTheSameIdempotencyKeyOnlyOnce()
    {
        using var host = new TestHost();
        var body = "{\"itemId\":\"apple\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}";

        var response = await host.Send("POST", "/api/spawn/item", idempotencyKey: "key-1", body: body, contentType: "application/json");

        Assert.Equal(200, response.Status);
        Assert.Equal(1, host.Game.SpawnCalls);
        Assert.Contains("\"success\":true", response.Json);

        // The same key with the same body returns the stored result.
        var repeat = await host.Send("POST", "/api/spawn/item", idempotencyKey: "key-1", body: body, contentType: "application/json");
        Assert.Equal(200, repeat.Status);
        Assert.Equal(1, host.Game.SpawnCalls);
    }

    [Fact]
    public async Task ReportsAConflictWhenTheSameKeyIsUsedWithAnotherBody()
    {
        using var host = new TestHost();

        await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key-2",
            body: "{\"itemId\":\"apple\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}",
            contentType: "application/json");

        var conflict = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key-2",
            body: "{\"itemId\":\"pear\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}",
            contentType: "application/json");

        Assert.Equal(409, conflict.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", Parse(conflict.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task RejectsMutationsWhenTheGameIsNotReady()
    {
        using var host = new TestHost();
        host.Game.InLevel = false;

        var result = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key-3",
            body: "{\"itemId\":\"apple\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}",
            contentType: "application/json");

        Assert.Equal(409, result.Status);
        Assert.Equal("GAME_NOT_READY", Parse(result.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task RejectsMutationsFromANonHost()
    {
        using var host = new TestHost();
        host.Game.IsHost = false;

        var result = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key-4",
            body: "{\"itemId\":\"apple\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}",
            contentType: "application/json");

        Assert.Equal(409, result.Status);
        Assert.Equal("HOST_REQUIRED", Parse(result.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task RejectsMutationsWhenTheSessionChangedWhileQueued()
    {
        using var host = new TestHost();

        var result = await host.SendWhileQueued(
            "POST",
            "/api/spawn/item",
            whileQueued: () => host.Game.Generation = 99,
            idempotencyKey: "key-5",
            body: "{\"itemId\":\"apple\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}",
            contentType: "application/json");

        Assert.Equal(409, result.Status);
        Assert.Equal("GAME_SESSION_CHANGED", Parse(result.Json)["error"]?["code"]?.ToString());
        Assert.Equal(0, host.Game.SpawnCalls);
    }

    [Fact]
    public async Task ReportsUnsupportedActionsWithA501()
    {
        using var host = new TestHost();
        host.Game.EntitiesAvailable = false;

        var result = await host.Send(
            "POST",
            "/api/spawn/entity",
            idempotencyKey: "key-6",
            body: "{\"entityId\":\"scorpion\",\"quantity\":1,\"position\":{\"mode\":\"random_player\"}}",
            contentType: "application/json");

        Assert.Equal(501, result.Status);
        Assert.Equal("UNSUPPORTED_ACTION", Parse(result.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task ReportsAnInvalidQuantity()
    {
        using var host = new TestHost();

        var result = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key-7",
            body: "{\"itemId\":\"apple\",\"quantity\":99,\"position\":{\"mode\":\"random_player\"}}",
            contentType: "application/json");

        Assert.Equal(400, result.Status);
        Assert.Equal("INVALID_QUANTITY", Parse(result.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task ReportsAnInvalidPositionMode()
    {
        using var host = new TestHost();

        var result = await host.Send(
            "POST",
            "/api/spawn/item",
            idempotencyKey: "key-8",
            body: "{\"itemId\":\"apple\",\"quantity\":1,\"position\":{\"mode\":\"teleport\"}}",
            contentType: "application/json");

        Assert.Equal(400, result.Status);
        Assert.Equal("INVALID_POSITION", Parse(result.Json)["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task FiltersTheCatalogBySearch()
    {
        using var host = new TestHost();
        host.Game.AddItem("apple", "Apple").AddItem("pear", "Pear");

        var result = await host.Send("GET", "/api/catalog/items?search=app");
        var items = Parse(result.Json)["items"]!;

        Assert.Equal(200, result.Status);
        Assert.Single(items);
        Assert.Equal("apple", items[0]!["id"]?.ToString());
    }

    [Fact]
    public async Task RejectsDuplicateQueryFilters()
    {
        using var host = new TestHost();

        var result = await host.Send("GET", "/api/catalog/items?search=a&search=b");

        Assert.Equal(400, result.Status);
    }

    [Fact]
    public async Task ServesTheOpenApiContract()
    {
        using var host = new TestHost();

        var result = await host.Send("GET", "/openapi.json");

        Assert.Equal(200, result.Status);
        var contract = JObject.Parse(result.Json);
        Assert.Equal("3.0.3", contract["openapi"]?.ToString());
        Assert.NotNull(contract["paths"]!["/health"]);
    }

    [Fact]
    public async Task RateLimitsRequests()
    {
        using var host = new TestHost(o => o.MaxRequestsPerSecond = 2);

        var codes = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var result = await host.Send("GET", "/health");
            codes.Add(result.Status);
        }

        Assert.Contains(429, codes);
    }
}
