using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using MySqlConnector;

namespace Ranks;

public class Database
{
    private readonly Ranks _ranks;
    private readonly string _dbConnectionString;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Table, string Steam), TimeSpan>
        _unsentPlaytime = new();

    public Database(Ranks ranks, string connection)
    {
        _ranks = ranks;
        _dbConnectionString = connection;
    }

    public Task UpdateUserStatsDb(SteamID steamId, User user, TimeSpan playtime, long lastconnect) =>
        UpdateUserStatsDb(steamId, user, playtime, lastconnect, _ranks.Config.TableName);

    internal async Task<bool> UpdateUserStatsDb(SteamID steamId, User user, TimeSpan playtime, long lastconnect,
        string table, bool replacePlaytime = false)
    {
        var steam = steamId.SteamId2.ReplaceFirstCharacter();
        var key = (table, steam);
        if (_unsentPlaytime.TryRemove(key, out var pending) && !replacePlaytime) playtime += pending;
        var submitted = false;
        try
        {
            await using var connection = new MySqlConnection(_dbConnectionString);
            await connection.OpenAsync();

            var updateQuery = $@"
        UPDATE 
            `{table}`
        SET 
            `steam` = @SteamId,
            `name` = @Username,
            `value` = @Experience,
            `rank` = @LastLevel,
            `kills` = @Kills,
            `deaths` = @Deaths,
            `shoots` = @Shoots,
            `hits` = @Hits,
            `headshots` = @Headshots,
            `assists` = @Assists,
            `round_win` = @Roundwin,
            `round_lose` = @Roundlose,
            `playtime` = {(replacePlaytime ? "@PlayTime" : "`playtime` + @PlayTime")},
            `lastconnect` = @LastConnect
        WHERE 
            `steam` = @SteamId";

            submitted = true;
            var matched = await connection.ExecuteAsync(updateQuery, new
            {
                SteamId = steam,
                Username = user.name,
                LastLevel = user.rank,
                Experience = user.value,
                Kills = user.kills,
                Deaths = user.deaths,
                Shoots = user.shoots,
                Hits = user.hits,
                Headshots = user.headshots,
                Assists = user.assists,
                Roundwin = user.round_win,
                Roundlose = user.round_lose,
                PlayTime = playtime.TotalSeconds,
                LastConnect = lastconnect
            });
            if (matched == 0)
                throw new InvalidOperationException($"No stats row matched {steam} in {table}");
            return true;
        }
        catch (Exception e)
        {
            if (!submitted && !replacePlaytime) _unsentPlaytime[key] = playtime;
            Console.WriteLine(e);
            return false;
        }
    }

    public async Task<PlayerStats?> GetPlayerRankAndTotal(string steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_dbConnectionString);
            await connection.OpenAsync();

            var table = _ranks.Config.TableName;
            return await connection.QuerySingleAsync<PlayerStats>($"""
                SELECT (SELECT COUNT(*) + 1 FROM `{table}`
                        WHERE value > (SELECT value FROM `{table}` WHERE steam = @SteamId LIMIT 1)) AS PlayerRank,
                       (SELECT COUNT(*) FROM `{table}`) AS TotalPlayers
                """, new { SteamId = steamId.ReplaceFirstCharacter() });
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return null;
        }
    }


    public async Task AddUserToDb(string playerName, string steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_dbConnectionString);
            await connection.OpenAsync();

            await InsertUser(connection, _ranks.Config.TableName, playerName, steamId.ReplaceFirstCharacter(),
                _ranks.Config.InitialExperiencePoints);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private static Task InsertUser(MySqlConnection connection, string table, string name, string steam, int value) =>
        connection.ExecuteAsync($"""
            INSERT INTO `{table}` (steam, name, value, `rank`, kills, deaths, shoots, hits, headshots, assists,
                                   round_win, round_lose, playtime, lastconnect)
            VALUES (@steam, @name, @value, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            ON DUPLICATE KEY UPDATE steam = steam
            """, new { steam, name, value });

    internal async Task<User> LoadOrCreateUser(string table, string playerName, string steamId, int initialExperience)
    {
        var steam = steamId.ReplaceFirstCharacter();
        await using var connection = new MySqlConnection(_dbConnectionString);
        await connection.OpenAsync();
        var select = $"SELECT * FROM `{table}` WHERE steam = @steam LIMIT 1";
        var existing = await connection.QueryFirstOrDefaultAsync<User>(select, new { steam });
        if (existing != null) return existing;

        await InsertUser(connection, table, playerName, steam, initialExperience);
        return await connection.QuerySingleAsync<User>(select, new { steam });
    }

    public Task ResetPlayerData(string steamId) =>
        ResetPlayerData(steamId, _ranks.Config.InitialExperiencePoints, _ranks.Config.TableName);

    internal async Task ResetPlayerData(string steamId, int initialExperience, string table)
    {
        steamId = steamId.ReplaceFirstCharacter();
        _unsentPlaytime.TryRemove((table, steamId), out _);
        try
        {
            await using var dbConnection = new MySqlConnection(_dbConnectionString);
            await dbConnection.OpenAsync();

            var resetPlayerQuery = $@"
                UPDATE `{table}`
                SET
                    value = @DefaultValue,
                    rank = 0,
                    kills = 0,
                    deaths = 0,
                    shoots = 0,
                    hits = 0,
                    headshots = 0,
                    assists = 0,
                    round_win = 0,
                    round_lose = 0,
                    playtime = 0,
                    lastconnect = @LastConnect
                WHERE steam = @SteamId;";

            await dbConnection.ExecuteAsync(resetPlayerQuery, new
            {
                SteamId = steamId,
                DefaultValue = initialExperience,
                LastConnect = DateTimeOffset.Now.ToUnixTimeSeconds()
            });
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    public async Task CreateTable()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(_dbConnectionString);
            await dbConnection.OpenAsync();

            var createLrTable = $@"
            CREATE TABLE IF NOT EXISTS `{_ranks.Config.TableName}` (
                `steam` VARCHAR(255) NOT NULL,
                `name` VARCHAR(255) NOT NULL,
                `value` BIGINT NOT NULL,
                `rank` BIGINT NOT NULL,
                `kills` BIGINT NOT NULL,
                `deaths` BIGINT NOT NULL,
                `shoots` BIGINT NOT NULL,
                `hits` BIGINT NOT NULL,
                `headshots` BIGINT NOT NULL,
                `assists` BIGINT NOT NULL,
                `round_win` BIGINT NOT NULL,
                `round_lose` BIGINT NOT NULL,
                `playtime` BIGINT NOT NULL,
                `lastconnect` BIGINT NOT NULL,
                PRIMARY KEY (`steam`),
                KEY idx_ranks_value (`value`)
            );";

            await dbConnection.ExecuteAsync(createLrTable);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    public async Task<User?> GetUserStatsFromDb(string steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_dbConnectionString);
            await connection.OpenAsync();
            var user = await connection.QueryFirstOrDefaultAsync<User>(
                $"SELECT * FROM `{_ranks.Config.TableName}` WHERE `steam` = @SteamId",
                new { SteamId = steamId.ReplaceFirstCharacter() });

            return user;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return null;
    }
    
    public async Task<List<User>?> GetTopPlayers()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(_dbConnectionString);
            await dbConnection.OpenAsync();

            var selectTopPlayersQuery = $"SELECT * FROM {_ranks.Config.TableName} ORDER BY value DESC LIMIT 10;";

            var topPlayers = await dbConnection.QueryAsync<User>(selectTopPlayersQuery);

            return topPlayers.ToList();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return null;
    }

    public async Task<bool> UserExists(string steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_dbConnectionString);
            await connection.OpenAsync();

            var exists = await connection.ExecuteScalarAsync<bool>(
                $"SELECT EXISTS(SELECT 1 FROM `{_ranks.Config.TableName}` WHERE `steam` = @SteamId)",
                new { SteamId = steamId.ReplaceFirstCharacter() });

            return exists;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return false;
    }

}

public class RankDb
{
    public required string Host { get; init; }
    public required string Database { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
    public int Port { get; init; }
}
