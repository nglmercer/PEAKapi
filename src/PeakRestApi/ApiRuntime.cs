using System;
using BepInEx.Logging;
using PeakRestApi.Core.Game;
using PeakRestApi.Core.Http;
using PeakRestApi.Config;
using PeakRestApi.Game;

namespace PeakRestApi;

/// <summary>
/// Owns the REST listener and the gameplay command queue.
/// </summary>
/// <remarks>
/// The gameplay work is executed from the Unity main thread. BepInEx adds the
/// plugin itself to a persistent game object, so the plugin's own
/// <c>Update</c> is the pump; no extra game object is created, which also
/// avoids creating Unity objects before the engine is initialised.
/// </remarks>
internal sealed class ApiRuntime : IDisposable
{
    private readonly ManualLogSource log;
    private readonly ApiConfig config;
    private MainThreadDispatcher? dispatcher;
    private PeakGameAdapter? adapter;
    private RestServer? server;
    private bool started;
    private bool tickFailureLogged;

    public ApiRuntime(ManualLogSource logSource, ApiConfig apiConfig)
    {
        log = logSource;
        config = apiConfig;
    }

    /// <summary>
    /// Starts the REST listener. Starting twice is a no-op.
    /// </summary>
    public void StartServer()
    {
        if (started)
        {
            return;
        }

        started = true;

        var options = config.ToOptions();
        if (!options.Enabled)
        {
            log.LogInfo("PEAKapi is disabled in the configuration; the REST server was not started.");
            return;
        }

        try
        {
            options.Validate();
        }
        catch (Exception e)
        {
            log.LogError("PEAKapi configuration is invalid: " + e.Message);
            return;
        }

        dispatcher = new MainThreadDispatcher(options.QueueCapacity);
        adapter = new PeakGameAdapter(config.ToGameConfig(), message => log.LogInfo("[PEAKapi] " + message));
        var router = new ApiRouter(options, adapter, dispatcher, message => log.LogWarning("[PEAKapi] " + message));

        server = new RestServer(options, router, message => log.LogInfo("[PEAKapi] " + message));

        try
        {
            server.Start();
        }
        catch (Exception e)
        {
            // The most common cause on Windows is that the URL prefix is not
            // reserved for this user, not that another process uses the port.
            log.LogError(
                "PEAKapi could not bind " + options.Host + ":" + options.Port + " (" + e.Message + "). "
                + "Check that the port is free and that the address may be bound by this user.");
            try
            {
                server.Dispose();
            }
            catch (Exception)
            {
                // The server never started, so there is nothing to release.
            }

            server = null;
        }
    }

    /// <summary>
    /// Called from the Unity main thread. Refreshes the game bindings and
    /// executes the queued gameplay commands.
    /// </summary>
    public void Tick()
    {
        try
        {
            adapter?.Tick();
            dispatcher?.Pump(config.CommandsPerFrame.Value);
            tickFailureLogged = false;
        }
        catch (Exception e)
        {
            // A game binding failure must never escape into Unity's frame
            // loop: log it once per distinct failure instead of every frame.
            if (!tickFailureLogged)
            {
                tickFailureLogged = true;
                log.LogWarning("PEAKapi game tick failed; gameplay commands are paused until it recovers: " + e);
            }
        }
    }

    /// <summary>
    /// Stops the REST listener and releases the command queue.
    /// </summary>
    /// <remarks>
    /// The order matters. The dispatcher is released first, so every request
    /// that is still waiting for the Unity main thread fails immediately
    /// instead of blocking the shutdown until its timeout expires. The
    /// server is stopped with a bounded wait, because it runs on a thread
    /// that is not the main thread.
    /// </remarks>
    public void StopServer()
    {
        if (!started)
        {
            return;
        }

        started = false;

        // Release the queue first: the pending requests are answered with
        // 503 right away, so the server does not have to wait for them.
        dispatcher?.Dispose();
        dispatcher = null;

        var current = server;
        server = null;
        adapter = null;

        if (current != null)
        {
            try
            {
                var stopped = current.StopAsync();
                if (!stopped.Wait(TimeSpan.FromSeconds(5)))
                {
                    log.LogWarning("PEAKapi could not stop the REST server within 5 seconds; it was disposed anyway.");
                }
            }
            catch (Exception e)
            {
                log.LogWarning("PEAKapi could not stop the REST server cleanly: " + e.Message);
            }

            try
            {
                current.Dispose();
            }
            catch (Exception e)
            {
                log.LogWarning("PEAKapi could not release the REST server: " + e.Message);
            }
        }
    }

    public void Dispose() => StopServer();
}
