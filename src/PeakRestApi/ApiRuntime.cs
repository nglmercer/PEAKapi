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
            log.LogError(
                "PEAKapi could not bind " + options.Host + ":" + options.Port + " (" + e.Message + "). "
                + "Another process is probably using the port.");
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
    public void StopServer()
    {
        if (!started)
        {
            return;
        }

        started = false;

        try
        {
            server?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            log.LogWarning("PEAKapi could not stop the REST server cleanly: " + e.Message);
        }

        server = null;
        dispatcher?.Dispose();
        dispatcher = null;
        adapter = null;
    }

    public void Dispose() => StopServer();
}
