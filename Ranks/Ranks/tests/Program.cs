using System.Diagnostics;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using MySqlConnector;
using Ranks;

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
        await database.AddUserToDb("Test player", steamId.SteamId2);
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

        var reset = typeof(Database).GetMethod("ResetPlayerData",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)reset.Invoke(database, new object[] { steamId.SteamId2, 42, table })!;
        var afterReset = await database.GetUserStatsFromDb(steamId.SteamId2);
        if (afterReset is not { playtime: 0, value: 42 }) throw new InvalidOperationException("Incorrect reset");
        await database.UpdateUserStatsDb(steamId, afterReset, TimeSpan.FromSeconds(3), 3);
        var reconnected = await database.GetUserStatsFromDb(steamId.SteamId2);
        if (reconnected is not { playtime: 3, value: 42 }) throw new InvalidOperationException("Incorrect post-reset save");
        Console.WriteLine("MariaDB delayed save, additive playtime, reset and subsequent reload passed.");
    }
    finally
    {
        await connection.ExecuteAsync($"DROP TABLE IF EXISTS `{table}`");
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
