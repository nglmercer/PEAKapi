// Adapted from https://github.com/nglmercer/REPOapi (MIT). See third-party/REPOapi-NOTICE.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using PeakRestApi.Core.Http;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Core.Security;

/// <summary>
/// Stores the outcome of a mutation keyed by its <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// The same key with the same canonical body is executed exactly once; a
/// second caller waits for and receives the first result. The same key with a
/// different body is a conflict. Entries that are still in flight are never
/// evicted to make room for new work.
/// </remarks>
public sealed class IdempotencyStore
{
    private sealed class Entry
    {
        public string Fingerprint = string.Empty;

        public long Completed;

        public TaskCompletionSource<ApiResponse> Result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new object();
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly int capacity;
    private readonly long ttlTicks;
    private long nextCleanup;

    public IdempotencyStore(int capacity = 2048, TimeSpan? ttl = null)
    {
        this.capacity = capacity < 1 ? 1 : capacity;
        ttlTicks = (ttl ?? TimeSpan.FromMinutes(10)).Ticks;
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    public async Task<ApiResponse> Run(string? key, string payload, Func<Task<ApiResponse>> action)
    {
        if (string.IsNullOrWhiteSpace(key) || key!.Length > 128)
        {
            throw new ApiException(
                400,
                "IDEMPOTENCY_KEY_REQUIRED",
                "POST requires Idempotency-Key (1-128 characters). Retain it when retrying.");
        }

        string hash;
        using (var sha = SHA256.Create())
        {
            hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        }

        Entry entry;
        var owner = false;
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= nextCleanup)
            {
                foreach (var expired in new List<string>(entries.Keys))
                {
                    if (entries[expired].Result.Task.IsCompleted
                        && now - entries[expired].Completed >= ttlTicks)
                    {
                        entries.Remove(expired);
                    }
                }

                nextCleanup = now + Stopwatch.Frequency;
            }

            if (entries.TryGetValue(key!, out entry!))
            {
                if (!string.Equals(entry.Fingerprint, hash, StringComparison.Ordinal))
                {
                    throw new ApiException(
                        409,
                        "IDEMPOTENCY_CONFLICT",
                        "This key was already used with a different request.");
                }
            }
            else
            {
                // In flight entries are never evicted, so a full store only
                // happens when too many distinct keys are genuinely in use.
                if (entries.Count >= capacity)
                {
                    throw new ApiException(
                        503,
                        "IDEMPOTENCY_CAPACITY",
                        "Idempotency storage is full; retry later.");
                }

                entry = new Entry { Fingerprint = hash };
                entries.Add(key!, entry);
                owner = true;
            }
        }

        if (owner)
        {
            ApiResponse response;
            try
            {
                response = await action().ConfigureAwait(false);
            }
            catch (ApiException e)
            {
                response = ApiResponse.Error(e);
            }
            catch
            {
                response = ApiResponse.Error(new ApiException(
                    500,
                    "ACTION_FAILED",
                    "Execution failed; inspect the game log before retrying with a new key."));
            }

            lock (gate)
            {
                entry.Completed = Stopwatch.GetTimestamp();
                entry.Result.TrySetResult(response);
            }
        }

        return await entry.Result.Task.ConfigureAwait(false);
    }
}
