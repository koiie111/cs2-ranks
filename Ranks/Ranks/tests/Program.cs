using System.Diagnostics;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using MySqlConnector;
using Ranks;
using Ranks_ExStats_Hits;

var random = new Random(329);
foreach (var size in new[] { 0, 1, 5, 25, 100 })
{
    for (var iteration = 0; iteration < 40; iteration++)
    {
        var ranks = GenerateRanks(size);
        var lookup = new RankThresholds(ranks);
        foreach (var previousLevel in new[] { -1, 0, 2, 99 })
        {
            for (var experience = -24; experience <= 110; experience++)
            {
                var expected = LegacyUpdate(ranks, experience, previousLevel);
                var rank = lookup.Find(experience);
                var notified = rank.Level == 0 && rank.NextXp != 0 && previousLevel != 0;
                var current = rank.Level == 0 && rank.NextXp == 0 ? previousLevel : rank.Level;
                if (expected != (rank.Name, rank.Level, rank.NextXp, current, notified))
                    throw new InvalidOperationException($"Rank mismatch at {experience}, prior {previousLevel}");
            }
        }
    }
}

Console.WriteLine("Rank lookup and legacy rank notification parity passed (108000 cases).");
var clockId = new SteamID(76561197960265729UL);
var clockUser = new User { steam = clockId.SteamId2, name = "Clock" };
var session = new Ranks.Ranks.Session(clockId);
var first = Ranks.Ranks.CaptureSave(session, clockUser, session.StartedAt + Stopwatch.Frequency * 56 / 10, 1, "stats");
var second = Ranks.Ranks.CaptureSave(session, clockUser, session.StartedAt + Stopwatch.Frequency * 112 / 10, 2, "stats");
var fresh = new Ranks.Ranks.Session(clockId, new Ranks.Ranks.ResetNotice(default));
var resetSave = Ranks.Ranks.CaptureSave(fresh, clockUser, fresh.StartedAt, 3, "stats");
if (first.Playtime.TotalSeconds != 5 || second.Playtime.TotalSeconds != 6 ||
    Ranks.Ranks.CombineSaves(first, second)?.Playtime.TotalSeconds != 11 ||
    Ranks.Ranks.CombineSaves(second, resetSave) != null)
    throw new InvalidOperationException("Fractional timer drift or coalescing across a reset");
await DatabaseQueueChecks.Run();
var config = GenerateRanks(25);
var thresholds = new RankThresholds(config);
Console.WriteLine("Rank calculation only. Elapsed ms and thread allocations, 1000 simulated ticks.");
foreach (var players in new[] { 0, 1, 24, 48 })
{
    var workload = Enumerable.Range(0, players * 1000).Select(_ => (long)random.Next(-5, 125)).ToArray();
    var before = Measure(workload, xp => LegacyUpdate(config, xp, 0).NextXp);
    var after = Measure(workload, xp => thresholds.Find(xp).NextXp);
    if (before.Checksum != after.Checksum) throw new InvalidOperationException("Workload mismatch");
    Console.WriteLine($"{players,2}: {before.Milliseconds:F3} ms / {before.Bytes} B -> {after.Milliseconds:F3} ms / {after.Bytes} B");
}

if (args is ["--database"])
{
    const string connectionString = "Server=127.0.0.1;Port=43316;Database=ranks_perf_check;User ID=root;Connection Timeout=2";
    var table = $"checks_{Guid.NewGuid():N}";
    var plugin = (Ranks.Ranks)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Ranks.Ranks));
    plugin.Config = new Config { TableName = table, Prefix = "", InitialExperiencePoints = 42 };
    var database = new Database(plugin, connectionString);
    var steamId = new SteamID(76561197960265729UL);
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    try
    {
        await database.CreateTable();
        var firstJoins = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => database.LoadOrCreateUser(table, "Test player", steamId.SteamId2, 42)));
        if (firstJoins.Any(row => row.value != 42) ||
            await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{table}`") != 1)
            throw new InvalidOperationException("Concurrent first joins created duplicate users");
        var returning = await database.LoadOrCreateUser(table, "New name", steamId.SteamId2, 999);
        if (returning.value != 42) throw new InvalidOperationException("Returning player was reinitialized");
        Console.WriteLine("24 concurrent first joins and returning player lookup passed.");
        var snapshot = await database.GetUserStatsFromDb(steamId.SteamId2) ??
            throw new InvalidOperationException("Missing initial row");
        snapshot.value = 123;
        await using (var blocker = new MySqlConnection(connectionString))
        {
            await blocker.OpenAsync();
            await blocker.ExecuteAsync($"LOCK TABLES `{table}` WRITE");
            var delayed = database.UpdateUserStatsDb(steamId, snapshot, TimeSpan.FromSeconds(5), 1);
            await Task.Delay(120);
            if (delayed.IsCompleted) throw new InvalidOperationException("Save did not wait for the database lock");
            await blocker.ExecuteAsync("UNLOCK TABLES");
            await delayed;
        }

        await database.UpdateUserStatsDb(steamId, snapshot, TimeSpan.FromSeconds(2), 2);
        var beforeReset = await database.GetUserStatsFromDb(steamId.SteamId2);
        if (beforeReset is not { playtime: 7, value: 123 }) throw new InvalidOperationException("Incorrect combined save");

        await database.ResetPlayerData(steamId.SteamId2);
        var afterReset = await database.GetUserStatsFromDb(steamId.SteamId2);
        if (afterReset is not { playtime: 0, value: 42 }) throw new InvalidOperationException("Incorrect reset");
        await database.UpdateUserStatsDb(steamId, afterReset, TimeSpan.FromSeconds(3), 3);
        var reconnected = await database.GetUserStatsFromDb(steamId.SteamId2);
        if (reconnected is not { playtime: 3, value: 42 }) throw new InvalidOperationException("Incorrect post-reset save");
        // The first acknowledged snapshot after a reset replaces session time. Replaying it is idempotent.
        for (var attempt = 0; attempt < 2; attempt++)
            if (!await database.UpdateUserStatsDb(steamId, afterReset, TimeSpan.FromSeconds(2), 4, table,
                    replacePlaytime: true)) throw new InvalidOperationException("Absolute reset write failed");
        await database.UpdateUserStatsDb(steamId, afterReset, TimeSpan.FromSeconds(5), 5);
        if (await database.GetUserStatsFromDb(steamId.SteamId2) is not { playtime: 7 })
            throw new InvalidOperationException("Replaying the reset snapshot doubled session time");
        if (await database.GetPlayerRankAndTotal(steamId.SteamId2) is not { PlayerRank: 1, TotalPlayers: 1 })
            throw new InvalidOperationException("Rank or total is incorrect");
        Console.WriteLine("MariaDB delayed save, additive playtime, idempotent reset and subsequent reload passed.");
        await DatabaseQueueChecks.RunMariaDb(database, new Database(plugin, connectionString),
            connectionString, table, steamId);

        var hitsTable = table + "_hits";
        var store = new HitStatisticsStore(connectionString, hitsTable);
        await store.CreateTable();
        var hits = await store.Load("player");
        hits[HitData.HdDmgHealth] = 99;
        hits[HitData.HdDmgArmor] = 40;
        hits[HitData.HdHitHead] = 2;
        hits[HitData.HdHitAll] = 3; // Includes a generic hit without a body part.
        await store.Save(hits, "player");
        if ((await store.Load("player"))[HitData.HdHitAll] != 3)
            throw new InvalidOperationException("Generic hit lost after reconnect");
        await connection.ExecuteAsync($"ALTER TABLE `{hitsTable}` DROP COLUMN AllHits");
        hits = await store.Load("player");
        if (hits[HitData.HdHitAll] != 2) throw new InvalidOperationException("Legacy hit count includes damage");
        await store.Save(hits, "player");
        await connection.ExecuteAsync($"ALTER TABLE `{hitsTable}` ADD COLUMN AllHits INT NULL");
        hits = await store.Load("player");
        if (hits[HitData.HdHitAll] != 2) throw new InvalidOperationException("Migrated hit count lost");
        hits[HitData.HdHitAll]++;
        await store.Save(hits, "player");
        if ((await store.Load("player"))[HitData.HdHitAll] != 3)
            throw new InvalidOperationException("Migrated total was not persisted");
        Console.WriteLine("New, legacy and migrated hit statistics round trips passed.");
    }
    finally
    {
        await connection.ExecuteAsync($"DROP TABLE IF EXISTS `{table}_hits`, `{table}`");
    }
}

Dictionary<string, int> GenerateRanks(int size) =>
    Enumerable.Range(0, size).ToDictionary(index => $"Rank {index}", _ => random.Next(-10, 100));

static (string Name, int Level, long NextXp, int Current, bool Notified) LegacyUpdate(
    Dictionary<string, int> ranks, long experience, int previousLevel)
{
    var matchingRank = (Name: string.Empty, Level: 0);
    var notified = false;
    var nextXp = 0L;
    foreach (var (_, required) in ranks.OrderBy(rank => rank.Value))
    {
        matchingRank = LegacyLookup(ranks, experience);
        if (experience < required)
        {
            notified = matchingRank.Level != previousLevel;
            previousLevel = matchingRank.Level;
            nextXp = required - experience;
            break;
        }

        previousLevel = matchingRank.Level;
    }

    return (matchingRank.Name, matchingRank.Level, nextXp, previousLevel, notified);
}

static (string Name, int Level) LegacyLookup(Dictionary<string, int> ranks, long experience)
{
    foreach (var rank in ranks.OrderByDescending(pair => pair.Value))
        if (experience >= rank.Value)
            return (rank.Key, ranks.Count(pair => pair.Value <= rank.Value));
    return (string.Empty, 0);
}

static (double Milliseconds, long Bytes, long Checksum) Measure(long[] workload, Func<long, long> calculate)
{
    for (var index = 0; index < 5000; index++) calculate(index % 125);
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    var checksum = 0L;
    foreach (var xp in workload) checksum += calculate(xp);
    return (Stopwatch.GetElapsedTime(start).TotalMilliseconds,
        GC.GetAllocatedBytesForCurrentThread() - allocated, checksum);
}
