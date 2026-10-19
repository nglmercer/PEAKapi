using System;
using System.Collections.Generic;
using System.Linq;
using PeakRestApi.Core.Models;
using UnityEngine;

namespace PeakRestApi.Game;

/// <summary>
/// Tracks the objects PEAKapi created so it can despawn exactly those and
/// nothing else.
/// </summary>
/// <remarks>
/// Only objects that PEAKapi itself created are tracked. Entries are keyed by
/// an opaque id and carry the gameplay generation they belong to, so an
/// object that survived a scene change is never mistaken for a fresh one.
/// </remarks>
internal sealed class SpawnOwnershipRegistry
{
    internal sealed class Entry
    {
        public required string Id { get; init; }

        public required string CatalogId { get; init; }

        public required string Kind { get; init; }

        public required object Target { get; init; }

        public long Generation { get; set; }

        public double? ExpiresAtUnixSeconds { get; set; }
    }

    private readonly object gate = new object();
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private long counter;

    /// <summary>
    /// Registers a created object and returns its opaque instance id.
    /// </summary>
    public string Register(string kind, string catalogId, object target, long generation, double? expiresAtUnixSeconds)
    {
        lock (gate)
        {
            counter++;
            var id = kind + "-" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
                     + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

            entries[id] = new Entry
            {
                Id = id,
                CatalogId = catalogId,
                Kind = kind,
                Target = target,
                Generation = generation,
                ExpiresAtUnixSeconds = expiresAtUnixSeconds,
            };

            return id;
        }
    }

    /// <summary>
    /// Removes an entry and returns it, or null when the id is unknown.
    /// </summary>
    public Entry? Take(string id)
    {
        lock (gate)
        {
            if (entries.TryGetValue(id, out var entry))
            {
                entries.Remove(id);
                return entry;
            }

            return null;
        }
    }

    /// <summary>
    /// Looks an entry up without removing it.
    /// </summary>
    public Entry? Find(string id)
    {
        lock (gate)
        {
            return entries.TryGetValue(id, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// All live entries, optionally filtered by kind.
    /// </summary>
    public IReadOnlyList<Entry> Snapshot(string? kind = null)
    {
        lock (gate)
        {
            return entries.Values
                .Where(e => kind == null || string.Equals(e.Kind, kind, StringComparison.Ordinal))
                .ToList();
        }
    }

    /// <summary>
    /// Forgets every entry that belongs to a previous gameplay generation.
    /// </summary>
    public int PurgeStale(long generation)
    {
        lock (gate)
        {
            var stale = entries.Values.Where(e => e.Generation != generation).Select(e => e.Id).ToList();
            foreach (var id in stale)
            {
                entries.Remove(id);
            }

            return stale.Count;
        }
    }

    /// <summary>
    /// Removes every entry.
    /// </summary>
    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
        }
    }
}
