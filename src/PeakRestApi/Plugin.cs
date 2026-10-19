using System;
using BepInEx;
using BepInEx.Logging;
using PeakRestApi.Config;

namespace PeakRestApi;

/// <summary>
/// The PEAKapi BepInEx plugin entry point.
/// </summary>
/// <remarks>
/// BepInEx adds the plugin to a persistent game object, so the plugin's
/// <c>Update</c> is the Unity main thread pump for the gameplay queue.
/// </remarks>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("PEAK.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "org.peak.rest-api";
    public const string PluginName = "PEAKapi";
    public const string PluginVersion = "1.0.0";

    private ApiRuntime? runtime;
    private int frames;

    private void Awake()
    {
        try
        {
            var config = new ApiConfig();
            runtime = new ApiRuntime(Logger, config);
            runtime.StartServer();
        }
        catch (Exception e)
        {
            Logger.LogError("PEAKapi failed to start: " + e);
        }
    }

    private void Update()
    {
        if (frames < 3)
        {
            frames++;
            Logger.LogInfo("[PEAKapi] Unity update frame " + frames + ".");
        }

        runtime?.Tick();
    }

    private void OnDestroy()
    {
        runtime?.Dispose();
        runtime = null;
    }
}
