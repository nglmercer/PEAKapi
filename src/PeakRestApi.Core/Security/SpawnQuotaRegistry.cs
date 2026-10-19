using System;
using System.Collections.Generic;

namespace PeakRestApi.Core.Security;

/// <summary>
/// Server side caps for live gameplay objects. These limits are enforced by
/// PEAKapi itself and do not depend on the automation client honouring a
/// cooldown.
/// </summary>
public sealed class SpawnQuotaRegistry
{
    private readonly object gate = new object();
    private readonly Dictionary<string, int> perType = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly int maxEntities;
    private readonly int maxHazards;

    public SpawnQuotaRegistry(int maxEntities, int maxHazards)
    {
        this.maxEntities = Math.Max(0, maxEntities);
        this.maxHazards = Math.Max(0, maxHazards);
    }

    public int ActiveEntities
    {
        get
        {
            lock (gate)
            {
                return perType.TryGetValue("entity", out var count) ? count : 0;
            }
        }
    }

    public int ActiveHazards
    {
        get
        {
            lock (gate)
            {
                return perType.TryGetValue("hazard", out var count) ? count : 0;
            }
        }
    }

    public int ActiveOfType(string kind)
    {
        lock (gate)
        {
            return perType.TryGetValue(kind, out var count) ? count : 0;
        }
    }

    /// <summary>
    /// Reserves room for <paramref name="quantity"/> objects of
    /// <paramref name="kind"/>. Returns false when a cap would be exceeded.
    /// </summary>
    public bool TryReserve(string kind, string catalogId, int quantity, int maxPerType)
    {
        if (quantity < 1)
        {
            return false;
        }

        lock (gate)
        {
            var global = kind switch
            {
                "entity" => maxEntities,
                "hazard" => maxHazards,
                _ => int.MaxValue,
            };

            var current = perType.TryGetValue(kind, out var kindCount) ? kindCount : 0;
            if (current + quantity > global)
            {
                return false;
            }

            var typeKey = kind + ":" + catalogId;
            var perTypeCount = perType.TryGetValue(typeKey, out var existing) ? existing : 0;
            if (maxPerType > 0 && perTypeCount + quantity > maxPerType)
            {
                return false;
            }

            perType[kind] = current + quantity;
            perType[typeKey] = perTypeCount + quantity;
            return true;
        }
    }

    public void Release(string kind, string catalogId, int quantity)
    {
        if (quantity < 1)
        {
            return;
        }

        lock (gate)
        {
            var typeKey = kind + ":" + catalogId;
            if (perType.TryGetValue(typeKey, out var perTypeCount))
            {
                perType[typeKey] = Math.Max(0, perTypeCount - quantity);
            }

            if (perType.TryGetValue(kind, out var kindCount))
            {
                perType[kind] = Math.Max(0, kindCount - quantity);
            }
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            perType.Clear();
        }
    }
}
