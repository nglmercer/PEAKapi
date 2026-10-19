// Adapted from https://github.com/nglmercer/REPOapi (MIT, Copyright (c) 2026
// R.E.P.O. REST API contributors). See third-party/REPOapi-NOTICE.md.
//
// No Unity references live here: the game's Update loop is the only consumer.
// A queued action that expires before it is pumped is never executed, so a
// timed out HTTP request can never cause a late gameplay mutation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PeakRestApi.Core.Http;
using PeakRestApi.Core.Models;

namespace PeakRestApi.Core.Game;

/// <summary>
/// Bounded queue that hands gameplay work to the Unity main thread.
/// </summary>
public sealed class MainThreadDispatcher : IDisposable
{
    private sealed class Work
    {
        public Func<ApiResponse> Action = null!;

        public readonly TaskCompletionSource<ApiResponse> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Deadline;

        public int State; // 0 queued, 1 executing, 2 completed/cancelled; protected by gate
    }

    private readonly object gate = new object();
    private readonly Queue<Work> queue = new Queue<Work>();
    private readonly int capacity;
    private bool stopped;

    public MainThreadDispatcher(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    public int Pending
    {
        get
        {
            lock (gate)
            {
                return queue.Count;
            }
        }
    }

    /// <summary>
    /// Queues <paramref name="action"/> for the main thread. The returned task
    /// completes with the action's response, or with a 504 when the work
    /// expires before it is executed.
    /// </summary>
    public async Task<ApiResponse> Execute(Func<ApiResponse> action, int timeoutMs)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (timeoutMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        var work = new Work
        {
            Action = action,
            Deadline = Stopwatch.GetTimestamp() + (timeoutMs * Stopwatch.Frequency) / 1000,
        };

        lock (gate)
        {
            if (stopped)
            {
                throw new ApiException(503, "SHUTTING_DOWN", "The dispatcher is stopping.");
            }

            // Compact only under capacity pressure; normal admission stays O(1).
            if (queue.Count >= capacity)
            {
                var count = queue.Count;
                while (count-- > 0)
                {
                    var old = queue.Dequeue();
                    if (old.State == 0)
                    {
                        queue.Enqueue(old);
                    }
                }
            }

            if (queue.Count >= capacity)
            {
                throw new ApiException(503, "QUEUE_FULL", "The game command queue is full.");
            }

            queue.Enqueue(work);
        }

        using (var timer = new Timer(
                   _ =>
                   {
                       lock (gate)
                       {
                           if (work.State != 0)
                           {
                               return;
                           }

                           work.State = 2;
                           work.Completion.TrySetException(new ApiException(
                               504,
                               "COMMAND_TIMEOUT",
                               "Command expired before execution; no action was performed."));
                       }
                   },
                   null,
                   timeoutMs,
                   Timeout.Infinite))
        {
            return await work.Completion.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes up to <paramref name="limit"/> queued actions. Called from the
    /// Unity main thread.
    /// </summary>
    public void Pump(int limit)
    {
        for (var i = 0; i < limit;)
        {
            Work work;
            lock (gate)
            {
                if (stopped || queue.Count == 0)
                {
                    return;
                }

                work = queue.Dequeue();
                if (work.State != 0)
                {
                    continue;
                }

                if (Stopwatch.GetTimestamp() >= work.Deadline)
                {
                    work.State = 2;
                    work.Completion.TrySetException(new ApiException(
                        504,
                        "COMMAND_TIMEOUT",
                        "Command expired before execution; no action was performed."));
                    continue;
                }

                work.State = 1;
            }

            i++;
            try
            {
                work.Completion.TrySetResult(work.Action());
            }
            catch (Exception e)
            {
                work.Completion.TrySetException(e);
            }
            finally
            {
                lock (gate)
                {
                    work.State = 2;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            stopped = true;
            while (queue.Count > 0)
            {
                var w = queue.Dequeue();
                w.State = 2;
                w.Completion.TrySetException(new ApiException(503, "SHUTTING_DOWN", "The game is shutting down."));
            }
        }
    }
}
