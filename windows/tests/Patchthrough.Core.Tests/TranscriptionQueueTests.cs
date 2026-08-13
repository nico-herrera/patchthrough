using Patchthrough.Core;

namespace Patchthrough.Core.Tests;

/// <summary>
/// The queue drives a status line the user watches during a meeting, and it
/// decides whether a failed session is retried or lost. The work itself is a
/// delegate here, so these tests control exactly when each session finishes.
/// </summary>
public sealed class TranscriptionQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pt-queue-" + Guid.NewGuid().ToString("N")[..8]);

    public TranscriptionQueueTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A session directory that is pending: meta.json, no transcript.</summary>
    private string Pending(string id)
    {
        var directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "meta.json"), """{ "files": { "mic": "mic.m4a" } }""");
        return directory;
    }

    /// <summary>Wait for a condition the drain thread satisfies, or fail the test.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    [Fact]
    public async Task SessionsTranscribeInTheOrderTheyWereQueued()
    {
        var done = new List<string>();
        await using var queue = new TranscriptionQueue((dir, _) =>
        {
            lock (done) done.Add(new DirectoryInfo(dir).Name);
            return Task.CompletedTask;
        });

        queue.Enqueue(Pending("2026.08.03-1400"));
        queue.Enqueue(Pending("2026.08.03-1500"));
        queue.Enqueue(Pending("2026.08.03-1600"));

        await Until(() => { lock (done) return done.Count == 3; }, "three sessions");
        Assert.Equal(["2026.08.03-1400", "2026.08.03-1500", "2026.08.03-1600"], done);
    }

    [Fact]
    public async Task OneSessionRunsAtATime()
    {
        var concurrent = 0;
        var peak = 0;
        var finished = 0;
        await using var queue = new TranscriptionQueue(async (_, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            // Engines hold hundreds of megabytes of model. Two at once is not a
            // slowdown, it is a machine running out of memory.
            Interlocked.Exchange(ref peak, Math.Max(peak, now));
            await Task.Delay(20);
            Interlocked.Decrement(ref concurrent);
            Interlocked.Increment(ref finished);
        });

        queue.Enqueue(Pending("2026.08.03-1400"));
        queue.Enqueue(Pending("2026.08.03-1500"));

        await Until(() => Volatile.Read(ref finished) == 2, "both sessions");
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task QueueingTheSameSessionTwiceTranscribesItOnce()
    {
        var runs = 0;
        var gate = new TaskCompletionSource();
        await using var queue = new TranscriptionQueue(async (_, token) =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task.WaitAsync(token);
        });

        var directory = Pending("2026.08.03-1400");
        queue.Enqueue(directory);
        await Until(() => Volatile.Read(ref runs) == 1, "the first session to start");
        // Queued again while it is being transcribed. A second run would waste
        // minutes of CPU and interleave two engines in one transcribe.log.
        queue.Enqueue(directory);
        gate.SetResult();

        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Idle, "the queue to drain");
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task TheStatusLineNamesTheSessionAndCountsWhatIsWaiting()
    {
        var seen = new List<TranscriptionQueueSnapshot>();
        var release = new TaskCompletionSource();
        await using var queue = new TranscriptionQueue(async (_, token) => await release.Task.WaitAsync(token));
        queue.StatusChanged += snapshot => { lock (seen) seen.Add(snapshot); };

        // The first session is held inside the delegate, so the two that follow
        // are both waiting before either is dequeued. Without that the drain can
        // start before the second Enqueue lands, and the count is a race.
        queue.Enqueue(Pending("2026.08.03-1400"));
        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Transcribing, "the first session");
        Assert.Equal("2026.08.03-1400", queue.Snapshot.SessionName);

        queue.Enqueue(Pending("2026.08.03-1500"));
        queue.Enqueue(Pending("2026.08.03-1600"));
        release.SetResult();

        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Idle, "the queue to drain");
        lock (seen)
        {
            var transcribing = seen.Where(s => s.State == TranscriptionQueueState.Transcribing).ToList();
            Assert.Equal(
                ["2026.08.03-1400", "2026.08.03-1500", "2026.08.03-1600"],
                transcribing.Select(s => s.SessionName));
            // Each session reports what is still behind it, which is what
            // "transcribing X, 1 queued" tells the user.
            Assert.Equal([0, 1, 0], transcribing.Select(s => s.QueuedCount));
        }
        Assert.Equal(0, queue.Snapshot.QueuedCount);
    }

    [Fact]
    public async Task AFailedSessionDoesNotStopTheOnesBehindIt()
    {
        var done = new List<string>();
        await using var queue = new TranscriptionQueue((dir, _) =>
        {
            var name = new DirectoryInfo(dir).Name;
            lock (done) done.Add(name);
            return name.EndsWith("1400", StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("no engine could be prepared"))
                : Task.CompletedTask;
        });

        queue.Enqueue(Pending("2026.08.03-1400"));
        queue.Enqueue(Pending("2026.08.03-1500"));

        await Until(() => { lock (done) return done.Count == 2; }, "both sessions attempted");
        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Failed, "the failure to surface");
        // The failure names the session that failed, not the one that followed.
        Assert.Equal("2026.08.03-1400", queue.Snapshot.SessionName);
    }

    [Fact]
    public async Task AFailureSurvivesUntilTheNextSessionStarts()
    {
        var release = new TaskCompletionSource();
        var started = 0;
        await using var queue = new TranscriptionQueue(async (dir, token) =>
        {
            Interlocked.Increment(ref started);
            if (new DirectoryInfo(dir).Name.EndsWith("1400", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("failed");
            }
            await release.Task.WaitAsync(token);
        });

        queue.Enqueue(Pending("2026.08.03-1400"));
        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Failed, "the failure to surface");

        // A drain that goes quiet must not erase a failure the user never saw.
        Assert.Equal(TranscriptionQueueState.Failed, queue.Snapshot.State);

        queue.Enqueue(Pending("2026.08.03-1500"));
        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Transcribing, "the next session");
        Assert.Equal("2026.08.03-1500", queue.Snapshot.SessionName);

        release.SetResult();
        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Idle, "the queue to drain");
    }

    [Fact]
    public async Task CompletionReportsSuccessAndFailurePerSession()
    {
        var results = new List<(string Name, string? Error)>();
        await using var queue = new TranscriptionQueue((dir, _) =>
            new DirectoryInfo(dir).Name.EndsWith("1400", StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask);
        queue.SessionCompleted += (dir, error) =>
        {
            lock (results) results.Add((new DirectoryInfo(dir).Name, error?.Message));
        };

        queue.Enqueue(Pending("2026.08.03-1400"));
        queue.Enqueue(Pending("2026.08.03-1500"));

        await Until(() => { lock (results) return results.Count == 2; }, "both results");
        lock (results)
        {
            Assert.Equal(("2026.08.03-1400", "boom"), results[0]);
            Assert.Equal(("2026.08.03-1500", null), results[1]);
        }
    }

    [Fact]
    public async Task ASessionDeletedWhileItWaitedIsACancellationNotAFailure()
    {
        var attempted = new List<string>();
        var completions = 0;
        var release = new TaskCompletionSource();
        await using var queue = new TranscriptionQueue(async (dir, token) =>
        {
            lock (attempted) attempted.Add(new DirectoryInfo(dir).Name);
            if (new DirectoryInfo(dir).Name.EndsWith("1400", StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(token);
            }
        });
        queue.SessionCompleted += (_, _) => Interlocked.Increment(ref completions);

        queue.Enqueue(Pending("2026.08.03-1400"));
        var doomed = Pending("2026.08.03-1500");
        queue.Enqueue(doomed);
        await Until(() => { lock (attempted) return attempted.Count == 1; }, "the first session to start");

        // The user moved it to the Recycle Bin while it sat in the queue.
        Directory.Delete(doomed, recursive: true);
        release.SetResult();

        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Idle, "the queue to drain");
        // Reporting a failure here would tell the user to read a transcribe.log
        // inside the folder they just deleted, and would leave the status line
        // stuck on a session that no longer exists.
        lock (attempted) Assert.Equal(["2026.08.03-1400"], attempted);
        Assert.Equal(1, completions);
        Assert.Equal(TranscriptionQueueState.Idle, queue.Snapshot.State);
    }

    [Fact]
    public async Task PendingSessionsAreQueuedOldestFirst()
    {
        // The launch-time rescan. A crash or a quit mid-transcription leaves the
        // audio and meta.json in place, and the filesystem is the queue.
        Pending("2026.08.03-1600");
        Pending("2026.08.03-1400");
        Pending("2026.08.03-1500");
        var transcribed = Path.Combine(_root, "2026.08.03-1300");
        Directory.CreateDirectory(transcribed);
        File.WriteAllText(Path.Combine(transcribed, "meta.json"), "{}");
        File.WriteAllText(Path.Combine(transcribed, "transcript.json"), "{}");

        var done = new List<string>();
        await using var queue = new TranscriptionQueue((dir, _) =>
        {
            lock (done) done.Add(new DirectoryInfo(dir).Name);
            return Task.CompletedTask;
        });

        var added = queue.EnqueuePending(_root);

        Assert.Equal(3, added);
        await Until(() => { lock (done) return done.Count == 3; }, "three sessions");
        // Oldest first, so a backlog comes out in the order it happened. The
        // already-transcribed session is not work.
        Assert.Equal(["2026.08.03-1400", "2026.08.03-1500", "2026.08.03-1600"], done);
    }

    [Fact]
    public async Task ARescanDoesNotRequeueWhatIsAlreadyQueued()
    {
        var runs = 0;
        var release = new TaskCompletionSource();
        await using var queue = new TranscriptionQueue(async (_, token) =>
        {
            Interlocked.Increment(ref runs);
            await release.Task.WaitAsync(token);
        });

        Pending("2026.08.03-1400");
        Pending("2026.08.03-1500");
        Assert.Equal(2, queue.EnqueuePending(_root));
        await Until(() => Volatile.Read(ref runs) == 1, "the first session to start");

        // Neither the session in flight nor the one still waiting is work again.
        Assert.Equal(0, queue.EnqueuePending(_root));

        release.SetResult();
        await Until(() => queue.Snapshot.State == TranscriptionQueueState.Idle, "the queue to drain");
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task DisposeCancelsTheSessionInFlightAndLeavesItPending()
    {
        var observed = new TaskCompletionSource<CancellationToken>();
        var cancelled = new TaskCompletionSource();
        var queue = new TranscriptionQueue(async (_, token) =>
        {
            observed.SetResult(token);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        });

        queue.Enqueue(Pending("2026.08.03-1400"));
        var token = await observed.Task;
        Assert.False(token.IsCancellationRequested);

        await queue.DisposeAsync();

        // The engine gets the chance to stop rather than being abandoned
        // mid-write. The session stays pending and the next launch retries it.
        await cancelled.Task;
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposeGivesUpOnAnEngineThatCannotBeCancelled()
    {
        // A token is a request, and a native inference call cannot observe one
        // until it returns. Quit has to finish anyway.
        var started = new TaskCompletionSource();
        var stuck = new TaskCompletionSource();
        var queue = new TranscriptionQueue(async (_, _) =>
        {
            started.SetResult();
            await stuck.Task;
        });
        queue.Enqueue(Pending("2026.08.03-1400"));
        await started.Task;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await queue.DisposeAsync();
        clock.Stop();

        // Bounded, not indefinite. The session is still pending on disk, so the
        // next launch retries it: nothing unrecoverable was abandoned.
        Assert.True(clock.Elapsed < TranscriptionQueue.ShutdownGrace * 3,
            $"dispose took {clock.Elapsed}, so a wedged engine would hang Quit");
        stuck.SetResult();
    }

    [Fact]
    public async Task QueueingAfterDisposeDoesNothing()
    {
        var runs = 0;
        var queue = new TranscriptionQueue((_, _) => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
        await queue.DisposeAsync();

        queue.Enqueue(Pending("2026.08.03-1400"));
        Assert.Equal(0, queue.EnqueuePending(_root));

        await Task.Delay(50);
        Assert.Equal(0, runs);
    }
}
