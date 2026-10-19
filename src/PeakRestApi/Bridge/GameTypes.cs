using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PeakRestApi.Bridge;

/// <summary>
/// The game and Photon types PEAKapi binds to.
/// </summary>
/// <remarks>
/// The set of resolvable types is the honest definition of what this build of
/// PEAKapi can do. When a type cannot be resolved the matching capability
/// reports as unavailable and the matching endpoint answers 501 instead of
/// pretending to work.
/// </remarks>
internal sealed class GameTypes
{
    public Assembly? GameAssembly { get; private set; }

    public Type? ItemDatabase { get; private set; }

    public Type? Item { get; private set; }

    public Type? Character { get; private set; }

    public Type? GameUtils { get; private set; }

    public Type? ItemUidData { get; private set; }

    public Type? PhotonNetwork { get; private set; }

    public Type? PhotonView { get; private set; }

    public Type? PhotonPlayer { get; private set; }

    /// <summary>
    /// <c>SingletonAsset&lt;ItemDatabase&gt;</c>, the entry point to the game's
    /// item registry.
    /// </summary>
    public Type? ItemDatabaseSingleton { get; private set; }

    /// <summary>
    /// Every type name declared by the game assembly. Used to probe whether a
    /// hostile entity or hazard exists in the running game.
    /// </summary>
    public IReadOnlyCollection<string> GameTypeNames { get; private set; } = Array.Empty<string>();

    public bool GameLoaded => GameAssembly != null;

    public bool PhotonLoaded => PhotonNetwork != null;

    /// <summary>
    /// Tries to resolve the game types. Safe to call repeatedly: the game
    /// assemblies are not loaded during the very first frames.
    /// </summary>
    public bool Refresh()
    {
        var game = FindAssembly("Assembly-CSharp");
        if (game == null)
        {
            return false;
        }

        if (ReferenceEquals(game, GameAssembly))
        {
            return true;
        }

        GameAssembly = game;
        ItemDatabase = game.GetType("ItemDatabase", false);
        Item = game.GetType("Item", false);
        Character = game.GetType("Character", false);
        GameUtils = game.GetType("GameUtils", false);
        ItemUidData = game.GetType("ItemUIData", false)
                      ?? Item?.GetNestedType("ItemUIData", BindingFlags.Public | BindingFlags.NonPublic);

        // SingletonAsset<T> lives in Zorro.Core.Runtime.dll, not in
        // Assembly-CSharp, so it is resolved across every loaded assembly.
        var singleton = FindType("Zorro.Core.SingletonAsset`1") ?? FindType("SingletonAsset`1");
        if (singleton != null && ItemDatabase != null)
        {
            try
            {
                ItemDatabaseSingleton = singleton.MakeGenericType(ItemDatabase);
            }
            catch (Exception)
            {
                ItemDatabaseSingleton = null;
            }
        }

        PhotonNetwork = FindType("Photon.Pun.PhotonNetwork");
        PhotonView = FindType("Photon.Pun.PhotonView");
        PhotonPlayer = FindType("Photon.Realtime.PhotonPlayer");

        GameTypeNames = CollectTypeNames(game);
        return true;
    }

    /// <summary>
    /// True when the running game declares a type with the given name.
    /// </summary>
    public bool HasGameType(string name) =>
        GameTypeNames.Contains(name);

    private static IReadOnlyCollection<string> CollectTypeNames(Assembly assembly)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var type in assembly.GetTypes())
            {
                names.Add(type.Name);
            }
        }
        catch (ReflectionTypeLoadException e)
        {
            foreach (var type in e.Types.Where(t => t != null))
            {
                names.Add(type!.Name);
            }
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        return names;
    }

    private static Assembly? FindAssembly(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }
            catch (Exception)
            {
                // A dynamic assembly without a readable name is not interesting.
            }
        }

        return null;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // Ignore assemblies that refuse to answer.
            }
        }

        return null;
    }
}
