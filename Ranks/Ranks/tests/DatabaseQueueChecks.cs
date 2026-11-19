using Ranks;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using MySqlConnector;

internal static class DatabaseQueueChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly record struct Snapshot(int Experience, int Playtime);

    public static async Task Run()
    {
        await ReloadWaitsForFinalWrite();
        await SlowWriteDoesNotBlockOtherPlayers();
        Console.WriteLine("Database queue reload handoff, slow-write isolation, snapshot coalescing and reset order passed.");
    }

    public static async Task RunMariaDb(Database original, Database replacement, string connectionString,
        string table, SteamID steamId)
    {
        var other = new SteamID(76561197960265731UL);
        await original.AddUserToDb("Other player", other.SteamId2);
        var started = Signal();
        var oldRow = await original.GetUserStatsFromDb(steamId.SteamId2) ??
            throw new InvalidOperationException("Missing original row");
        var lockedSteam = oldRow.steam;
        await using var blocker = new MySqlConnection(connectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteScalarAsync<int>($"""
            SELECT value FROM `{table}` WHERE steam = @lockedSteam FOR UPDATE
            """, new { lockedSteam }, transaction);
        var oldQueue = Queue(async snapshot =>
        {
            var save = new User { steam = lockedSteam, name = oldRow.name, value = snapshot.Experience };
            var pending = original.UpdateUserStatsDb(steamId, save, TimeSpan.FromSeconds(snapshot.Playtime), 10);
            started.TrySetResult();
            await pending;
        });
        oldQueue.Save(steamId.SteamId64, new Snapshot(100, 5));
        await started.Task.WaitAsync(Timeout);
        for (var experience = 101; experience <= 130; experience++)
            oldQueue.Save(steamId.SteamId64, new Snapshot(experience, 5));
        var newQueue = Queue(_ => Task.CompletedTask, oldQueue.Seal());
        var otherLoaded = new TaskCompletionSource<User?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloaded = new TaskCompletionSource<User?>(TaskCreationOptions.RunContinuationsAsynchronously);
        newQueue.Run(steamId.SteamId64, async () =>
            reloaded.TrySetResult(await replacement.GetUserStatsFromDb(steamId.SteamId2)));
        newQueue.Run(other.SteamId64, async () =>
            otherLoaded.TrySetResult(await replacement.GetUserStatsFromDb(other.SteamId2)));
        try
        {
            if (await otherLoaded.Task.WaitAsync(Timeout) is not { name: "Other player" })
                throw new InvalidOperationException("Other player could not load while a row was locked");
            if (reloaded.Task.IsCompleted) throw new InvalidOperationException("Reload bypassed the locked write");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
        await Drain(newQueue);
        if (await reloaded.Task is not { value: 130 } row || row.playtime != oldRow.playtime + 155)
            throw new InvalidOperationException("Reload lost experience or queued playtime");
        Console.WriteLine("MariaDB locked-row reload and concurrent authorization of another SteamID passed.");
    }

    private static async Task ReloadWaitsForFinalWrite()
    {
        var started = Signal();
        var release = Signal();
        var loaded = Signal();
        var row = new Snapshot(0, 0);
        var original = Queue(async snapshot =>
        {
            if (snapshot.Experience == 100)
            {
                started.TrySetResult();
                await release.Task;
            }
            row = snapshot with { Playtime = row.Playtime + snapshot.Playtime };
        });
        original.Save(1, new Snapshot(100, 5));
        await started.Task.WaitAsync(Timeout);
        original.Save(1, new Snapshot(150, 2));
        var idleReplacement = Queue(_ => Task.CompletedTask, original.Seal());
        var replacement = Queue(_ => Task.CompletedTask, idleReplacement.Seal());
        var observed = default(Snapshot);
        replacement.Run(1, () =>
        {
            observed = row;
            loaded.TrySetResult();
            return Task.CompletedTask;
        });
        if (loaded.Task.IsCompleted) throw new InvalidOperationException("Reload read ahead of the old write");
        var nextReload = Queue(_ => Task.CompletedTask, replacement.Seal());
        Snapshot third = default;
        nextReload.Run(1, () => { third = row; return Task.CompletedTask; });
        release.TrySetResult();
        await Drain(nextReload);
        if (observed != new Snapshot(150, 7) || third != observed)
            throw new InvalidOperationException("A replacement instance loaded stale data");
    }

    private static async Task SlowWriteDoesNotBlockOtherPlayers()
    {
        var started = Signal();
        var release = Signal();
        var otherLoaded = Signal();
        var writes = new List<Snapshot>();
        var queue = Queue(async snapshot =>
        {
            writes.Add(snapshot);
            if (snapshot.Experience == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
        });
        queue.Save(1, new Snapshot(1, 5));
        await started.Task.WaitAsync(Timeout);
        for (var experience = 2; experience <= 100; experience++)
            queue.Save(1, new Snapshot(experience, 5));
        queue.Run(1, () => { writes.Add(default); return Task.CompletedTask; });
        queue.Save(1, new Snapshot(101, 2));
        queue.Save(1, new Snapshot(102, 3));
        queue.Run(2, () => { otherLoaded.TrySetResult(); return Task.CompletedTask; });
        try { await otherLoaded.Task.WaitAsync(Timeout); }
        finally { release.TrySetResult(); }
        await Drain(queue);
        Snapshot[] expected = [new(1, 5), new(100, 495), default, new(102, 5)];
        if (!writes.SequenceEqual(expected))
            throw new InvalidOperationException("Pending snapshots did not coalesce or crossed an operation boundary");
    }

    private static PlayerDatabaseQueue<Snapshot> Queue(Func<Snapshot, Task> save,
        IReadOnlyDictionary<ulong, Task>? previous = null) =>
        new(save, (older, newer) => newer with { Playtime = older.Playtime + newer.Playtime },
            error => throw new InvalidOperationException("A database operation failed", error), previous);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Drain(PlayerDatabaseQueue<Snapshot> queue) =>
        Task.WhenAll(queue.Seal().Values).WaitAsync(Timeout);
}
