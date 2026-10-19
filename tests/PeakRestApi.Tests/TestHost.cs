using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PeakRestApi.Core.Game;
using PeakRestApi.Core.Http;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Tests;

/// <summary>
/// Runs the router while simulating the Unity main thread.
/// </summary>
/// <remarks>
/// Every request, read or write, is executed by the dispatcher on the game
/// main thread. The test host therefore has to pump the dispatcher the same
/// way the game does, otherwise a request would simply expire.
/// </remarks>
internal sealed class TestHost : IDisposable
{
    public const string Token = "0123456789abcdef0123456789abcdef0123456789abc=";

    public TestHost(Action<ApiOptions>? configure = null, FakeGameBridge? game = null)
    {
        Options = new ApiOptions
        {
            Host = "127.0.0.1",
            Port = 17454,
            RequireAuth = true,
            Token = Token,
            MaxRequestsPerSecond = 1000,
            RequestTimeoutMs = 2000,
        };

        configure?.Invoke(Options);

        Game = game ?? new FakeGameBridge();
        Dispatcher = new MainThreadDispatcher(Options.QueueCapacity);
        Router = new ApiRouter(Options, Game, Dispatcher);
    }

    public ApiOptions Options { get; }

    public FakeGameBridge Game { get; }

    public MainThreadDispatcher Dispatcher { get; }

    public ApiRouter Router { get; }

    /// <summary>
    /// Sends a request and pumps the dispatcher until it completes.
    /// </summary>
    public async Task<ApiResponse> Send(
        string method,
        string path,
        string? token = Token,
        string? idempotencyKey = null,
        string body = "",
        string? contentType = null)
    {
        var header = token == null ? null : "Bearer " + token;
        var pending = Router.Handle(method, path, header, idempotencyKey, body, contentType);

        var watch = Stopwatch.StartNew();
        while (!pending.IsCompleted && watch.ElapsedMilliseconds < Options.RequestTimeoutMs + 2000)
        {
            Dispatcher.Pump(8);
            await Task.Delay(1);
        }

        return await pending;
    }

    /// <summary>
    /// Sends a request without pumping, so the queued work expires.
    /// </summary>
    public async Task<ApiResponse> SendWithoutPumping(
        string method,
        string path,
        string? token = Token,
        string? idempotencyKey = null,
        string body = "",
        string? contentType = null)
    {
        var header = token == null ? null : "Bearer " + token;
        return await Router.Handle(method, path, header, idempotencyKey, body, contentType);
    }

    /// <summary>
    /// Sends a request, runs <paramref name="whileQueued"/> while the work is
    /// still waiting for the main thread and then pumps the dispatcher.
    /// </summary>
    public async Task<ApiResponse> SendWhileQueued(
        string method,
        string path,
        Action whileQueued,
        string? token = Token,
        string? idempotencyKey = null,
        string body = "",
        string? contentType = null)
    {
        var header = token == null ? null : "Bearer " + token;
        var pending = Router.Handle(method, path, header, idempotencyKey, body, contentType);

        whileQueued();

        var watch = Stopwatch.StartNew();
        while (!pending.IsCompleted && watch.ElapsedMilliseconds < Options.RequestTimeoutMs + 2000)
        {
            Dispatcher.Pump(8);
            await Task.Delay(1);
        }

        return await pending;
    }

    public void Dispose() => Dispatcher.Dispose();
}
