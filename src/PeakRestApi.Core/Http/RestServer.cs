// Adapted from https://github.com/nglmercer/REPOapi (MIT). See third-party/REPOapi-NOTICE.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Core.Http;

/// <summary>
/// The embedded HTTP listener. It only ever reads bytes, validates and
/// forwards to the router; it never touches the game directly.
/// </summary>
public sealed class RestServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly ApiRouter router;
    private readonly ApiOptions options;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new object();
    private readonly HashSet<Task> requests = new HashSet<Task>();
    private readonly SemaphoreSlim slots;
    private Task? acceptLoop;
    private Task? shutdown;
    private bool disposed;

    public RestServer(ApiOptions options, ApiRouter router, Action<string> log)
    {
        options.Validate();
        this.options = options;
        this.router = router;
        this.log = log;
        slots = new SemaphoreSlim(options.QueueCapacity);
    }

    /// <summary>
    /// The address the listener is bound to, or null when it is not running.
    /// </summary>
    public string? BoundAddress { get; private set; }

    public bool IsRunning => BoundAddress != null;

    public void Start()
    {
        lock (gate)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(RestServer));
            }

            if (listener.IsListening)
            {
                // A second start would add a second prefix and then fail in
                // the middle of the listener, leaving it in a half started
                // state.
                throw new InvalidOperationException("The REST server is already running.");
            }
        }

        var address = IPAddress.Parse(options.Host).ToString();
        var host = address.Contains(":") ? "[" + address + "]" : address;
        listener.Prefixes.Add("http://" + host + ":" + options.Port + "/");
        listener.Start();
        acceptLoop = Accept();
        BoundAddress = host + ":" + options.Port;
        log("REST server listening on " + BoundAddress);
    }

    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException)
            {
                if (!stop.IsCancellationRequested)
                {
                    log("HTTP accept stopped: " + e.GetType().Name);
                }

                return;
            }

            if (!slots.Wait(0))
            {
                try
                {
                    await WriteBounded(context, ApiResponse.Error(new ApiException(
                        503,
                        "SERVER_BUSY",
                        "HTTP request capacity exceeded."))).ConfigureAwait(false);
                }
                catch
                {
                    context.Response.Abort();
                }

                continue;
            }

            var task = Serve(context);
            lock (gate)
            {
                requests.Add(task);
            }

            _ = task.ContinueWith(
                t =>
                {
                    lock (gate)
                    {
                        requests.Remove(t);
                    }

                    slots.Release();
                },
                TaskScheduler.Default);
        }
    }

    private async Task Serve(HttpListenerContext context)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(options.RequestTimeoutMs);

        // Aborting closes slow or incomplete uploads. Once parsed, the dispatcher owns execution deadlines.
        using var abort = timeout.Token.Register(() =>
        {
            try
            {
                context.Response.Abort();
                context.Request.InputStream.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        try
        {
            var request = context.Request;
            ApiResponse result;

            if (options.RequireAuth
                && !AuthenticationMiddleware.Authenticate(request.Headers["Authorization"], options.Token))
            {
                result = ApiResponse.Error(new ApiException(401, "UNAUTHORIZED", "A valid bearer token is required."));
            }
            else if (request.ContentLength64 > options.MaxRequestBytes)
            {
                result = ApiResponse.Error(new ApiException(413, "PAYLOAD_TOO_LARGE", "Body exceeds the configured limit."));
            }
            else
            {
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await request.InputStream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (body.Length + count > options.MaxRequestBytes)
                    {
                        throw new ApiException(413, "PAYLOAD_TOO_LARGE", "Body exceeds the configured limit.");
                    }

                    body.Write(buffer, 0, count);
                }

                timeout.CancelAfter(Timeout.Infinite);

                string json;
                try
                {
                    json = new UTF8Encoding(false, true).GetString(body.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    throw new ApiException(400, "INVALID_JSON", "Body must be UTF-8.");
                }

                result = await router.Handle(
                    request.HttpMethod,
                    request.RawUrl ?? "/",
                    request.Headers["Authorization"],
                    request.Headers["Idempotency-Key"],
                    json,
                    request.ContentType).ConfigureAwait(false);
            }

            await WriteBounded(context, result).ConfigureAwait(false);
        }
        catch (ApiException e)
        {
            try
            {
                await WriteBounded(context, ApiResponse.Error(e)).ConfigureAwait(false);
            }
            catch
            {
                context.Response.Abort();
            }
        }
        catch (Exception e)
        {
            if (!stop.IsCancellationRequested)
            {
                log("HTTP connection ended: " + e.GetType().Name);
            }

            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task WriteBounded(HttpListenerContext context, ApiResponse response)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(options.RequestTimeoutMs);
        using var abort = deadline.Token.Register(() =>
        {
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        await Write(context, response).ConfigureAwait(false);
    }

    private static async Task Write(HttpListenerContext context, ApiResponse response)
    {
        var bytes = Encoding.UTF8.GetBytes(response.Json);
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        if (response.Status == 401)
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
        }

        if (response.Status == 429)
        {
            context.Response.Headers["Retry-After"] = "1";
        }

        if (response.Status == 405 && !string.IsNullOrEmpty(response.Allow))
        {
            context.Response.Headers["Allow"] = response.Allow;
        }

        context.Response.ContentLength64 = bytes.Length;
        context.Response.KeepAlive = false;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        context.Response.Close();
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            return shutdown ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        stop.Cancel();
        try
        {
            listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        if (acceptLoop != null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException)
            {
            }
        }

        Task[] pending;
        lock (gate)
        {
            pending = new Task[requests.Count];
            requests.CopyTo(pending);
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A request that was aborted while the listener shut down is expected.
        }

        BoundAddress = null;
        log("REST server stopped.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        // The listener is closed right away, so nothing waits for a request
        // that is still in flight. The graceful shutdown is done by
        // StopAsync, which the caller runs before this.
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        stop.Dispose();
    }
}
