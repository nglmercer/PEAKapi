using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PeakRestApi.Core.Game;
using PeakRestApi.Core.Http;
using PeakRestApi.Core.Models;
using PeakRestApi.Core.Security;
using Xunit;

namespace PeakRestApi.Tests;

public sealed class DispatcherTests
{
    [Fact]
    public async Task ExpiresQueuedWorkWithoutRunningIt()
    {
        using var dispatcher = new MainThreadDispatcher(8);
        var executed = false;

        var pending = dispatcher.Execute(() =>
        {
            executed = true;
            return new ApiResponse(200, new { ok = true });
        }, 150);

        // The dispatcher is never pumped, so the work must expire.
        var error = await Assert.ThrowsAsync<ApiException>(() => pending);
        Assert.Equal(504, error.Status);
        Assert.Equal("COMMAND_TIMEOUT", error.Code);

        // Pumping later must not run the expired command.
        dispatcher.Pump(4);
        await Task.Delay(50);
        Assert.False(executed);
    }

    [Fact]
    public async Task RunsQueuedWorkWhenPumped()
    {
        using var dispatcher = new MainThreadDispatcher(8);

        var pending = dispatcher.Execute(() => new ApiResponse(200, new { ok = true }), 5000);
        dispatcher.Pump(1);

        var response = await pending;
        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task RejectsWorkWhenTheQueueIsFull()
    {
        using var dispatcher = new MainThreadDispatcher(1);

        var first = dispatcher.Execute(() => new ApiResponse(200, new { ok = true }), 5000);
        var second = dispatcher.Execute(() => new ApiResponse(200, new { ok = true }), 5000);

        var error = await Assert.ThrowsAsync<ApiException>(() => second);
        Assert.Equal(503, error.Status);
        Assert.Equal("QUEUE_FULL", error.Code);

        dispatcher.Pump(4);
        await first;
    }

    [Fact]
    public async Task RejectsWorkAfterShutdown()
    {
        var dispatcher = new MainThreadDispatcher(4);
        dispatcher.Dispose();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            dispatcher.Execute(() => new ApiResponse(200, new { ok = true }), 500));

        Assert.Equal(503, error.Status);
        Assert.Equal("SHUTTING_DOWN", error.Code);
    }

    [Fact]
    public void LimitsTheRequestRate()
    {
        var limiter = new RateLimiter(3);

        Assert.True(limiter.Allow());
        Assert.True(limiter.Allow());
        Assert.True(limiter.Allow());
        Assert.False(limiter.Allow());
    }

    [Fact]
    public async Task SharesTheResultBetweenConcurrentDuplicates()
    {
        var store = new IdempotencyStore();
        var calls = 0;

        async Task<ApiResponse> Action()
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(60);
            return new ApiResponse(200, new { ok = true });
        }

        var first = store.Run("same", "payload", Action);
        var second = store.Run("same", "payload", Action);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(results[0].Json, results[1].Json);
    }

    [Fact]
    public async Task RejectsAConflictingIdempotencyKey()
    {
        var store = new IdempotencyStore();

        await store.Run("key", "payload-a", () => Task.FromResult(new ApiResponse(200, new { ok = true })));

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            store.Run("key", "payload-b", () => Task.FromResult(new ApiResponse(200, new { ok = true }))));

        Assert.Equal(409, error.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
    }

    [Fact]
    public async Task RequiresAnIdempotencyKey()
    {
        var store = new IdempotencyStore();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            store.Run(null, "payload", () => Task.FromResult(new ApiResponse(200, new { ok = true }))));

        Assert.Equal(400, error.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", error.Code);
    }

    [Fact]
    public void EnforcesTheSpawnQuotas()
    {
        var quotas = new SpawnQuotaRegistry(maxEntities: 4, maxHazards: 2);

        Assert.True(quotas.TryReserve("entity", "scorpion", 2, 3));
        Assert.Equal(2, quotas.ActiveEntities);
        Assert.False(quotas.TryReserve("entity", "scorpion", 2, 3));
        Assert.True(quotas.TryReserve("entity", "bee_swarm", 2, 3));
        Assert.False(quotas.TryReserve("entity", "zombie", 1, 3));

        quotas.Release("entity", "scorpion", 2);
        Assert.Equal(2, quotas.ActiveEntities);

        Assert.True(quotas.TryReserve("hazard", "tornado", 1, 1));
        Assert.False(quotas.TryReserve("hazard", "tornado", 1, 1));
    }
}

public sealed class AuthenticationTests
{
    [Fact]
    public void GeneratesAStrongToken()
    {
        var token = AuthenticationMiddleware.GenerateToken();

        Assert.Equal(44, token.Length);
        Assert.NotEqual(token, AuthenticationMiddleware.GenerateToken());
    }

    [Fact]
    public void AcceptsTheCorrectToken()
    {
        var token = AuthenticationMiddleware.GenerateToken();

        Assert.True(AuthenticationMiddleware.Authenticate("Bearer " + token, token));
        Assert.True(AuthenticationMiddleware.Authenticate("bearer " + token, token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    public void RejectsMalformedCredentials(string? header)
    {
        var token = AuthenticationMiddleware.GenerateToken();

        Assert.False(AuthenticationMiddleware.Authenticate(header, token));
    }

    [Fact]
    public void RedactsSecrets()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abc=";

        var redacted = AuthenticationMiddleware.Redact(token);

        Assert.DoesNotContain("0123456789abcdef", redacted);
        Assert.Contains("***", redacted);
        Assert.True(redacted.Length < token.Length);
    }
}

public sealed class RestServerTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abc=";

    [Fact]
    public async Task ServesTheApiOverHttp()
    {
        var options = new ApiOptions
        {
            Host = "127.0.0.1",
            Port = 17555,
            RequireAuth = true,
            Token = Token,
            MaxRequestsPerSecond = 1000,
        };

        var game = new FakeGameBridge().AddItem("apple", "Apple");
        using var dispatcher = new MainThreadDispatcher(16);
        var router = new ApiRouter(options, game, dispatcher);
        using var server = new RestServer(options, router, _ => { });

        server.Start();
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            // The game main thread is simulated by pumping the dispatcher.
            using var pump = new System.Threading.CancellationTokenSource();
            var pumping = Task.Run(() =>
            {
                while (!pump.IsCancellationRequested)
                {
                    dispatcher.Pump(8);
                    Thread.Sleep(1);
                }
            });

            var health = await client.GetStringAsync("http://127.0.0.1:17555/health");
            Assert.Contains("gameplayReady", health);

            using var anonymous = new HttpClient();
            var denied = await anonymous.GetAsync("http://127.0.0.1:17555/health");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("Bearer", denied.Headers.WwwAuthenticate.ToString());

            var catalog = await client.GetStringAsync("http://127.0.0.1:17555/api/catalog/items");
            Assert.Contains("apple", catalog);

            pump.Cancel();
            await pumping;
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task RefusesToStartTwice()
    {
        var options = new ApiOptions { Host = "127.0.0.1", Port = 17556, Token = Token };
        using var dispatcher = new MainThreadDispatcher(4);
        var router = new ApiRouter(options, new FakeGameBridge(), dispatcher);
        using var server = new RestServer(options, router, _ => { });

        server.Start();
        Assert.ThrowsAny<Exception>(() => server.Start());
        await server.StopAsync();
    }
}
