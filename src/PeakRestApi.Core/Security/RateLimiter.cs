// Adapted from https://github.com/nglmercer/REPOapi (MIT). See third-party/REPOapi-NOTICE.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PeakRestApi.Core.Security;

/// <summary>
/// Sliding window rate limiter. The window is enforced server side and is
/// independent of any cooldown applied by the automation client.
/// </summary>
public sealed class RateLimiter
{
    private readonly object gate = new object();
    private readonly Queue<long> times = new Queue<long>();
    private readonly int limit;

    public RateLimiter(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        this.limit = limit;
    }

    public bool Allow()
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            while (times.Count > 0 && now - times.Peek() >= Stopwatch.Frequency)
            {
                times.Dequeue();
            }

            if (times.Count >= limit)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }
    }
}
