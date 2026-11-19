using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Menu;
using Dapper;
using MySqlConnector;
using RanksApi;

namespace Ranks_ExStats_Hits;

public class RanksExStatsHits : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] ExStats Hits";
    public override string ModuleVersion => "v1.0.1";

    public readonly Dictionary<int, Dictionary<HitData, int>> Hits = new();
    private readonly Dictionary<int, SteamID> _connections = new();
    private long _generation;
    private readonly Dictionary<int, long> _versions = new();
    private Task _databaseTail = Task.CompletedTask;
    private bool _unloaded;
    private IRanksApi? _api;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        Enqueue(CreateTable);
        
        RegisterListener<Listeners.OnClientAuthorized>(OnAuthorized);
        RegisterListener<Listeners.OnClientDisconnect>(OnDisconnect);
 
        RegisterEventHandler<EventPlayerHurt>(EventPlayerHurt);
        
        _api.CreatedMenu += OnCreatedMenu;

        if (hotReload)
            foreach (var player in Utilities.GetPlayers())
                if (!player.IsBot && player.AuthorizedSteamID is { } steamId)
                    OnAuthorized(player.Slot, steamId);
    }

    public override void Unload(bool hotReload)
    {
        if (_api != null) _api.CreatedMenu -= OnCreatedMenu;
        foreach (var slot in _connections.Keys.ToArray()) OnDisconnect(slot);
        _unloaded = true;
    }

    private void Enqueue(Func<Task> operation)
    {
        _databaseTail = _databaseTail.ContinueWith(_ => operation(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private void OnCreatedMenu(CCSPlayerController player, IMenu menu)
    {
        menu.AddMenuOption(Localizer["hits_statistics"], OpenSubMenu);
    }

    private void OpenSubMenu(CCSPlayerController player, ChatMenuOption option)
    {
        var menu = new ChatMenu(Localizer["hit_statistics"]);
        var subMenu = new ChatMenu("");
        subMenu.AddMenuOption("Back", OpenSubMenu);

        if (Hits.TryGetValue(player.Slot, out var hits) && hits[HitData.HdHitAll] is not 0)
        {
            var allHits = hits[HitData.HdHitAll];
            var hitTitle = Localizer["hits"];

            menu.AddMenuOption(hitTitle, (controller, _) =>
            {
                subMenu.Title = hitTitle;

                var head = hits[HitData.HdHitHead];
                var chest = hits[HitData.HdHitChest];
                var belly = hits[HitData.HdHitBelly];
                var leftArm = hits[HitData.HdHitLeftArm];
                var rightArm = hits[HitData.HdHitRightArm];
                var leftLeg = hits[HitData.HdHitLeftLeg];
                var rightLeg = hits[HitData.HdHitRightLeg];
                
                subMenu.AddMenuOption(Localizer["hits_player",
                    allHits, 
                    head,
                    chest,
                    belly,
                    leftArm,
                    rightArm,
                    leftLeg,
                    rightLeg].Value.Replace('\n', '\u2029'), null!, true);
                
                subMenu.Open(controller);
            });

            var damageTitle = Localizer["damage"];
            menu.AddMenuOption(damageTitle, (controller, _) =>
            {
                subMenu.Title = damageTitle;

                var health = hits[HitData.HdDmgHealth];
                var armor = hits[HitData.HdDmgArmor];

                subMenu.AddMenuOption(Localizer["dmg_player",
                    health,
                    armor].Value.Replace('\n', '\u2029'), null!, true);

                subMenu.Open(controller);
            });
        }
        else
        {
            menu.AddMenuOption($"No data", null!, true);
        }

        menu.Open(player);
    }
    
    private void OnAuthorized(int slot, SteamID id)
    {
        if (_connections.TryGetValue(slot, out var current) && current.SteamId64 == id.SteamId64) return;
        OnDisconnect(slot);
        var generation = ++_generation;
        _connections[slot] = id;
        _versions[slot] = generation;
        Enqueue(() => LoadHitsFromDatabase(slot, generation, id));
    }

    private void OnDisconnect(int slot)
    {
        _versions.Remove(slot);
        var hasConnection = _connections.Remove(slot, out var steamId);
        if (Hits.Remove(slot, out var hits) && hasConnection)
        {
            var snapshot = new Dictionary<HitData, int>(hits);
            Enqueue(() => SavePlayerData(snapshot, steamId!));
        }
    }

    private HookResult EventPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var player = @event.Attacker;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        if (!Hits.TryGetValue(player.Slot, out var hits))
            return HookResult.Continue;

        hits[HitData.HdDmgHealth] += @event.DmgHealth;
        hits[HitData.HdDmgArmor] += @event.DmgArmor;

        hits[HitData.HdHitAll]++;
        var group = (HitData)@event.Hitgroup;
        if (group != HitData.HdHitAll && hits.TryGetValue(group, out var count))
            hits[group] = count + 1;

        return HookResult.Continue;
    }

    private async Task CreateTable()
    {
        try
        {
            await using var connection = new MySqlConnection(_api?.DatabaseConnectionString);
            await connection.OpenAsync();

            var query = $"""
                         CREATE TABLE IF NOT EXISTS `{_api?.DatabaseTableName}_hits`
                         (`SteamID` varchar(32) NOT NULL PRIMARY KEY DEFAULT '',
                         	`DmgHealth` int NOT NULL DEFAULT 0,
                         	`DmgArmor` int NOT NULL DEFAULT 0,
                         	`Head` int NOT NULL DEFAULT 0,
                         	`Chest` int NOT NULL DEFAULT 0,
                         	`Belly` int NOT NULL DEFAULT 0,
                         	`LeftArm` int NOT NULL DEFAULT 0,
                         	`RightArm` int NOT NULL DEFAULT 0,
                         	`LeftLeg` int NOT NULL DEFAULT 0,
                         	`RightLeg` int NOT NULL DEFAULT 0,
                         	`Neak` int NOT NULL DEFAULT 0) CHARSET = utf8 COLLATE utf8_general_ci;
                         """;

            await connection.ExecuteAsync(query);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private async Task AddPlayerToDatabase(string steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_api?.DatabaseConnectionString);
            await connection.OpenAsync();

            var query = $"INSERT INTO `{_api?.DatabaseTableName}_hits` (`SteamID`) VALUES (@SteamId);";

            await connection.ExecuteAsync(query, new { SteamId = steamId });
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private async Task SavePlayerData(Dictionary<HitData, int> hits, SteamID steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_api?.DatabaseConnectionString);
            await connection.OpenAsync();

            var updateQuery = $@"
                UPDATE `{_api?.DatabaseTableName}_hits`
                SET DmgHealth = @DmgHealth,
                    DmgArmor = @DmgArmor,
                    Head = @Head,
                    Chest = @Chest,
                    Belly = @Belly,
                    LeftArm = @LeftArm,
                    RightArm = @RightArm,
                    LeftLeg = @LeftLeg,
                    RightLeg = @RightLeg,
                    Neak = @Neak
                WHERE SteamID = @SteamId;";

            await connection.ExecuteAsync(updateQuery, new
            {
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
                SteamId = ReplaceFirstCharacter(steamId.SteamId2)
            });
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private async Task LoadHitsFromDatabase(int slot, long generation, SteamID id)
    {
        try
        {
            await using var connection = new MySqlConnection(_api?.DatabaseConnectionString);
            await connection.OpenAsync();

            var steamId = ReplaceFirstCharacter(id.SteamId2);

            var query = $"SELECT * FROM `{_api?.DatabaseTableName}_hits` WHERE SteamId = @SteamId;";
            var hitsData = await connection.QueryFirstOrDefaultAsync(query, new { SteamId = steamId });

            var hits = Enum.GetValues<HitData>().ToDictionary(hit => hit, _ => 0);

            if (hitsData == null)
            {
                await AddPlayerToDatabase(steamId);
            }
            else
            {
                hits[HitData.HdDmgHealth] = hitsData.DmgHealth;
                hits[HitData.HdDmgArmor] = hitsData.DmgArmor;
                hits[HitData.HdHitHead] = hitsData.Head;
                hits[HitData.HdHitChest] = hitsData.Chest;
                hits[HitData.HdHitBelly] = hitsData.Belly;
                hits[HitData.HdHitLeftArm] = hitsData.LeftArm;
                hits[HitData.HdHitRightArm] = hitsData.RightArm;
                hits[HitData.HdHitLeftLeg] = hitsData.LeftLeg;
                hits[HitData.HdHitRightLeg] = hitsData.RightLeg;
                hits[HitData.HdHitNeak] = hitsData.Neak;
                hits[HitData.HdHitAll] = hits.Values.Sum();
            }

            Server.NextFrame(() =>
            {
                if (!_unloaded && _versions.TryGetValue(slot, out var current) && current == generation)
                    Hits[slot] = hits;
            });
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
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
