using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Bridge;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Game;

/// <summary>
/// A catalog entry together with the live game object behind it.
/// </summary>
internal sealed class CatalogItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Category { get; init; }

    public string? Origin { get; init; }

    public bool Spawnable { get; init; } = true;

    public string? Reason { get; init; }

    /// <summary>
    /// The live <c>Item</c> prefab. Kept as an object because the game type
    /// is bound late.
    /// </summary>
    public object? Prefab { get; init; }
}

/// <summary>
/// Discovers the spawnable items of the running game.
/// </summary>
/// <remarks>
/// The catalog is read from the game's own item registry
/// (<c>SingletonAsset&lt;ItemDatabase&gt;.Instance.Objects</c>), so modded items
/// appear next to the vanilla ones. Nothing is instantiated while listing.
/// </remarks>
internal sealed class CatalogService
{
    private readonly GameContext context;
    private readonly object gate = new object();
    private List<CatalogItem>? cache;
    private long cacheGeneration = -1;

    public CatalogService(GameContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// True when the game's item registry could be reached.
    /// </summary>
    public bool Available
    {
        get
        {
            var types = context.Types;
            if (types.ItemDatabaseSingleton == null)
            {
                return false;
            }

            return GameMembers.GetStatic(types.ItemDatabaseSingleton, "Instance") != null;
        }
    }

    /// <summary>
    /// The reason the catalog is not available, or null when it is.
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            var types = context.Types;
            if (types.GameAssembly == null)
            {
                return "The PEAK game assemblies are not loaded yet.";
            }

            if (types.ItemDatabase == null)
            {
                return "This PEAK version does not expose an ItemDatabase type.";
            }

            if (types.ItemDatabaseSingleton == null)
            {
                return "This PEAK version does not expose SingletonAsset<ItemDatabase>.";
            }

            if (!Available)
            {
                return "The item database is not populated yet; it becomes available once a level is loaded.";
            }

            return null;
        }
    }

    /// <summary>
    /// The full item catalog. The result is cached until the gameplay
    /// generation changes.
    /// </summary>
    public IReadOnlyList<CatalogItem> Items(long generation)
    {
        lock (gate)
        {
            if (cache != null && cacheGeneration == generation)
            {
                return cache;
            }

            cache = Build();
            cacheGeneration = generation;
            return cache;
        }
    }

    public CatalogItem? Find(string id, long generation) =>
        Items(generation).FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    private List<CatalogItem> Build()
    {
        var result = new List<CatalogItem>();
        var types = context.Types;

        var database = GameMembers.GetStatic(types.ItemDatabaseSingleton, "Instance");
        if (database == null)
        {
            return result;
        }

        if (GameMembers.Get(database, "Objects") is not System.Collections.IEnumerable objects)
        {
            return result;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefab in objects)
        {
            if (prefab == null)
            {
                continue;
            }

            var rawName = GameMembers.GetString(prefab, "name");
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var id = rawName;
            var suffix = 1;
            while (!used.Add(id))
            {
                // Two prefabs can share a name; keep both addressable.
                id = rawName + "#" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                suffix++;
            }

            var display = ReadDisplayName(prefab) ?? rawName;
            if (string.IsNullOrWhiteSpace(display))
            {
                display = rawName;
            }

            result.Add(new CatalogItem
            {
                Id = id,
                Name = display,
                Category = ReadCategory(prefab),
                Origin = null,
                Spawnable = true,
                Prefab = prefab,
            });
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string? ReadDisplayName(object prefab)
    {
        var uid = GameMembers.Get(prefab, "UIData");
        if (uid == null)
        {
            return null;
        }

        var name = GameMembers.Get(uid, "itemName");
        return name as string;
    }

    private static string? ReadCategory(object prefab)
    {
        var category = GameMembers.Get(prefab, "category");
        return category?.ToString();
    }
}
