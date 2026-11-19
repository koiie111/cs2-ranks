using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Menu;
using Microsoft.Extensions.Logging;
using Ranks;
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
    private const string DatabaseHandoffKey = "cs2-ranks:hits:database-drain";
    private PlayerDatabaseQueue<SaveRequest>? _databaseWork;
    private HitStatisticsStore _store = null!;
    private Task _tableReady = Task.CompletedTask;
    private bool _unloaded;
    private IRanksApi? _api;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        var previous = AppDomain.CurrentDomain.GetData(DatabaseHandoffKey) as Dictionary<ulong, Task>;
        AppDomain.CurrentDomain.SetData(DatabaseHandoffKey, null);
        _store = new HitStatisticsStore(_api.DatabaseConnectionString, _api.DatabaseTableName + "_hits");
        _databaseWork = new PlayerDatabaseQueue<SaveRequest>(
            save => _store.Save(save.Hits, ReplaceFirstCharacter(save.SteamId.SteamId2)), (_, newer) => newer,
            error => Logger.LogError(error, "Ranks hit statistics database operation failed"), previous);
        _tableReady = Task.Run(InitializeTable);
        
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
        if (_databaseWork is not { } work) return;
        foreach (var slot in _connections.Keys.ToArray()) OnDisconnect(slot);
        _unloaded = true;
        AppDomain.CurrentDomain.SetData(DatabaseHandoffKey, work.Seal());
        _databaseWork = null;
    }

    private readonly record struct SaveRequest(Dictionary<HitData, int> Hits, SteamID SteamId);

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
        foreach (var (oldSlot, oldId) in _connections)
        {
            if (oldId.SteamId64 != id.SteamId64) continue;
            OnDisconnect(oldSlot);
            break;
        }
        var generation = ++_generation;
        _connections[slot] = id;
        _versions[slot] = generation;
        _databaseWork!.Run(id.SteamId64, () => LoadHitsFromDatabase(slot, generation, id));
    }

    private void OnDisconnect(int slot)
    {
        _versions.Remove(slot);
        var hasConnection = _connections.Remove(slot, out var steamId);
        if (Hits.Remove(slot, out var hits) && hasConnection)
        {
            var snapshot = new Dictionary<HitData, int>(hits);
            _databaseWork!.Save(steamId!.SteamId64, new SaveRequest(snapshot, steamId));
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
        if (group > HitData.HdHitAll && hits.TryGetValue(group, out var count))
            hits[group] = count + 1;

        return HookResult.Continue;
    }

    private async Task InitializeTable()
    {
        try { await _store.CreateTable(); }
        catch (Exception error) { Logger.LogError(error, "Could not initialize the hit statistics table"); }
    }

    private async Task LoadHitsFromDatabase(int slot, long generation, SteamID id)
    {
        await _tableReady.ConfigureAwait(false);
        Dictionary<HitData, int> hits;
        try
        {
            hits = await _store.Load(ReplaceFirstCharacter(id.SteamId2));
        }
        catch (Exception error)
        {
            if (error is MySqlConnector.MySqlException { Number: 1146 }) await InitializeTable();
            Logger.LogError(error, "Could not load hits for {SteamId}. Retrying in 30 seconds", id.SteamId64);
            Server.NextFrame(() =>
            {
                if (_unloaded) return;
                AddTimer(30f, () =>
                {
                    if (!_unloaded && _versions.TryGetValue(slot, out var current) && current == generation)
                        _databaseWork!.Run(id.SteamId64, () => LoadHitsFromDatabase(slot, generation, id));
                });
            });
            return;
        }
        Server.NextFrame(() =>
        {
            if (!_unloaded && _versions.TryGetValue(slot, out var current) && current == generation)
                Hits[slot] = hits;
        });
    }

    private static string ReplaceFirstCharacter(string input)
    {
        if (input.Length <= 0) return input;

        var charArray = input.ToCharArray();
        charArray[6] = '1';

        return new string(charArray);
    }
}
