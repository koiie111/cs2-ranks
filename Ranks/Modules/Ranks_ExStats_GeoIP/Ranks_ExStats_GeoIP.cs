using System.Net;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Dapper;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Ranks;
using RanksApi;

namespace Ranks_ExStats_GeoIP;

public class RanksExStatsGeoIp : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] ExStats GeoIP";
    public override string ModuleVersion => "v1.0.0";

    private const string DatabaseHandoffKey = "cs2-ranks:geoip:database-drain";
    private PlayerDatabaseQueue<Location>? _databaseWork;
    private readonly record struct Location(string SteamId, string Ip);
    private string _connection = "";
    private string _table = "";
    private readonly object _readerLock = new();
    private DatabaseReader? _reader;
    private Task _tableReady = Task.CompletedTask;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        var api = IRanksApi.Capability.Get();
        if (api == null) return;
        _connection = api.DatabaseConnectionString;
        _table = api.DatabaseTableName + "_geoip";
        var previous = AppDomain.CurrentDomain.GetData(DatabaseHandoffKey) as Dictionary<ulong, Task>;
        AppDomain.CurrentDomain.SetData(DatabaseHandoffKey, null);
        _databaseWork = new PlayerDatabaseQueue<Location>(SaveLocation, (_, newer) => newer,
            error => Logger.LogError(error, "Ranks GeoIP lookup or save failed"), previous);

        _tableReady = Task.Run(async () =>
        {
            try { await CreateTable(); }
            catch (Exception error) { Logger.LogError(error, "Could not initialize the GeoIP table"); }
        });
        RegisterListener<Listeners.OnClientAuthorized>((slot, id) =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || player.IsBot) return;

            var ip = player.IpAddress;
            var steamId = ReplaceFirstCharacter(id.SteamId2);

            if (ip == null)
            {
                Logger.LogError("The IP address of the player {player} is null!", player.PlayerName);
                return;
            }
            
            _databaseWork.Save(id.SteamId64, new Location(steamId, ip));
        });
    }

    public override void Unload(bool hotReload)
    {
        if (_databaseWork is not { } work) return;
        var pending = work.Seal();
        AppDomain.CurrentDomain.SetData(DatabaseHandoffKey, pending);
        _ = DisposeAfterWrites(pending.Values);
    }

    private async Task DisposeAfterWrites(IEnumerable<Task> pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        finally { _reader?.Dispose(); }
    }

    private async Task CreateTable()
    {
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();

        var query = $"""
                     CREATE TABLE IF NOT EXISTS `{_table}` (
                         `steam` varchar(32) NOT NULL,
                         `clientip` varchar(16) NOT NULL,
                         `country` varchar(48) NOT NULL,
                         `region` varchar(48) NOT NULL,
                         `city` varchar(48) NOT NULL,
                         `country_code` varchar(4) NOT NULL,
                         PRIMARY KEY (`steam`)
                     ) CHARSET=utf8;
                     """;

        await connection.ExecuteAsync(query);
    }

    private async Task SaveLocation(Location location)
    {
        var ip = location.Ip.Split(':')[0];
        await _tableReady;
        MaxMind.GeoIP2.Responses.CityResponse response;
        lock (_readerLock)
        {
            _reader ??= new DatabaseReader(Path.Combine(ModuleDirectory, "GeoLite2-City.mmdb"));
            response = _reader.City(IPAddress.Parse(ip));
        }

        var countryName = response.Country.Name ?? string.Empty;
        var cityName = response.City.Name ?? string.Empty;
        var regionName = response.MostSpecificSubdivision.Name ?? string.Empty;
        var countryCode = response.Country.IsoCode ?? string.Empty;

        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();

        var query = $"""
                         INSERT INTO `{_table}` (steam, clientip, country, region, city, country_code)
                         VALUES (@Steam, @Ip, @Country, @Region, @City, @Code)
                         ON DUPLICATE KEY UPDATE
                             clientip = @Ip,
                             country = @Country,
                             region = @Region,
                             city = @City,
                             country_code = @Code;
                     """;

        var parameters = new
        {
            Steam = location.SteamId,
            Ip = ip,
            Country = countryName,
            Region = regionName,
            City = cityName,
            Code = countryCode
        };
        try { await connection.ExecuteAsync(query, parameters); }
        catch (MySqlException error) when (error.Number == 1146)
        {
            await CreateTable();
            await connection.ExecuteAsync(query, parameters);
        }
    }

    private static string ReplaceFirstCharacter(string input)
    {
        if (input.Length <= 0) return input;

        var charArray = input.ToCharArray();
        charArray[6] = '1';

        return new string(charArray);
    }
}
