using Dapper;
using MySqlConnector;

namespace Ranks_ExStats_Hits;

internal sealed class HitStatisticsStore(string connectionString, string table)
{
    // All connections use the same table. Read its current shape even when the player has no row.
    private volatile bool _storesTotal;
    private static readonly (HitData Counter, string Column)[] Columns =
    [
        (HitData.HdDmgHealth, "DmgHealth"), (HitData.HdDmgArmor, "DmgArmor"),
        (HitData.HdHitHead, "Head"), (HitData.HdHitChest, "Chest"), (HitData.HdHitBelly, "Belly"),
        (HitData.HdHitLeftArm, "LeftArm"), (HitData.HdHitRightArm, "RightArm"),
        (HitData.HdHitLeftLeg, "LeftLeg"), (HitData.HdHitRightLeg, "RightLeg"), (HitData.HdHitNeak, "Neak")
    ];

    public async Task CreateTable()
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS `{table}`
            (
                `SteamID` varchar(32) NOT NULL PRIMARY KEY DEFAULT '',
                `DmgHealth` int NOT NULL DEFAULT 0,
                `DmgArmor` int NOT NULL DEFAULT 0,
                `Head` int NOT NULL DEFAULT 0,
                `Chest` int NOT NULL DEFAULT 0,
                `Belly` int NOT NULL DEFAULT 0,
                `LeftArm` int NOT NULL DEFAULT 0,
                `RightArm` int NOT NULL DEFAULT 0,
                `LeftLeg` int NOT NULL DEFAULT 0,
                `RightLeg` int NOT NULL DEFAULT 0,
                `Neak` int NOT NULL DEFAULT 0,
                `AllHits` int NULL
            ) CHARSET = utf8 COLLATE utf8_general_ci;
            """);
    }

    public async Task<Dictionary<HitData, int>> Load(string steamId)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var reader = await connection.ExecuteReaderAsync(
            $"SELECT * FROM `{table}` WHERE SteamID = @steamId", new { steamId });
        var totalColumn = -1;
        for (var i = 0; i < reader.FieldCount; i++)
            if (reader.GetName(i).Equals("AllHits", StringComparison.OrdinalIgnoreCase)) totalColumn = i;
        _storesTotal = totalColumn >= 0;

        var hits = Enum.GetValues<HitData>().ToDictionary(counter => counter, _ => 0);
        if (!await reader.ReadAsync()) return hits;
        foreach (var (counter, column) in Columns)
            hits[counter] = reader.GetInt32(reader.GetOrdinal(column));
        hits[HitData.HdHitAll] = totalColumn >= 0 && !reader.IsDBNull(totalColumn)
            ? reader.GetInt32(totalColumn)
            : hits.Where(pair => pair.Key > HitData.HdHitAll).Sum(pair => pair.Value);
        return hits;
    }

    public async Task Save(Dictionary<HitData, int> hits, string steamId)
    {
        await using var connection = new MySqlConnection(connectionString);
        var storesTotal = _storesTotal;
        var updateQuery = $"""
            INSERT INTO `{table}` (SteamID, DmgHealth, DmgArmor, Head, Chest, Belly, LeftArm, RightArm,
                                   LeftLeg, RightLeg, Neak{(storesTotal ? ", AllHits" : "")})
            VALUES (@SteamId, @DmgHealth, @DmgArmor, @Head, @Chest, @Belly, @LeftArm, @RightArm,
                    @LeftLeg, @RightLeg, @Neak{(storesTotal ? ", @AllHits" : "")})
            ON DUPLICATE KEY UPDATE DmgHealth = @DmgHealth, DmgArmor = @DmgArmor, Head = @Head,
                Chest = @Chest, Belly = @Belly, LeftArm = @LeftArm, RightArm = @RightArm,
                LeftLeg = @LeftLeg, RightLeg = @RightLeg, Neak = @Neak{(storesTotal ? ", AllHits = @AllHits" : "")}
            """;

        await connection.ExecuteAsync(updateQuery, new
        {
            AllHits = hits[HitData.HdHitAll],
            DmgHealth = hits[HitData.HdDmgHealth],
            DmgArmor = hits[HitData.HdDmgArmor],
            Head = hits[HitData.HdHitHead],
            Chest = hits[HitData.HdHitChest],
            Belly = hits[HitData.HdHitBelly],
            LeftArm = hits[HitData.HdHitLeftArm],
            RightArm = hits[HitData.HdHitRightArm],
            LeftLeg = hits[HitData.HdHitLeftLeg],
            RightLeg = hits[HitData.HdHitRightLeg],
            Neak = hits[HitData.HdHitNeak],
            SteamId = steamId
        });
    }
}
