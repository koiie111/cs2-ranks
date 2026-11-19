using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using RanksApi;

namespace Ranks;

[MinimumApiVersion(210)]
public class Ranks : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleDescription => "Adds a rating system to the server";
    public override string ModuleName => "[Ranks] Core";
    public override string ModuleVersion => "v2.0.5";

    public string DbConnectionString = string.Empty;

    public Config Config = null!;
    public Database Database = null!;
    public RanksApi RanksApi = null!;

    public readonly ConcurrentDictionary<ulong, User> Users = new();
    private readonly Dictionary<int, Session> _sessions = new();
    private const string DatabaseHandoffKey = "cs2-ranks:core:database-drain";
    private PlayerDatabaseQueue<SaveRequest>? _databaseWork;
    private Task _tableReady = Task.CompletedTask;
    private bool _unloaded;

    private sealed class Session(SteamID steamId)
    {
        public SteamID SteamId { get; } = steamId;
        public long SavedAt { get; set; } = Stopwatch.GetTimestamp();
    }
    private RankThresholds _thresholds = null!;
    private CHandle<CCSGameRulesProxy>? _rulesProxy;
    private int? _populationTick;
    private int _population;
    private int? _missingRulesTick;

    public readonly FakeConVar<bool> RanksEnable = new("css_lr_enable", "", true);
    
    public override void Load(bool hotReload)
    {
        RanksApi = new RanksApi(this);
        Capabilities.RegisterPluginCapability(IRanksApi.Capability, () => RanksApi);

        Config = LoadConfig();
        _thresholds = new RankThresholds(Config.Ranks);
        DbConnectionString = BuildConnectionString();
        Database = new Database(this, DbConnectionString);
        var previous = AppDomain.CurrentDomain.GetData(DatabaseHandoffKey) as Dictionary<ulong, Task>;
        AppDomain.CurrentDomain.SetData(DatabaseHandoffKey, null);
        _databaseWork = new PlayerDatabaseQueue<SaveRequest>(Save, CombineSaves,
            error => Logger.LogError(error, "Ranks database operation failed"), previous);
        _tableReady = Task.Run(Database.CreateTable);

        AddCommandListener("say", CommandListener_Say);
        AddCommandListener("say_team", CommandListener_Say);
        
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(_ => InvalidateMapState());
        RegisterListener<Listeners.OnMapEnd>(InvalidateMapState);
        RegisterListener<Listeners.OnClientPutInServer>(_ => _populationTick = null);
        RegisterListener<Listeners.OnClientDisconnectPost>(_ => _populationTick = null);
        RegisterEventHandler<EventPlayerTeam>((_, _) => InvalidatePopulation());
        RegisterEventHandler<EventPlayerSpawn>((_, _) => InvalidatePopulation());
        
        RegisterEventHandler<EventWeaponFire>(EventWeaponFire);
        RegisterEventHandler<EventPlayerHurt>(EventPlayerHurt);
        RegisterEventHandler<EventPlayerDeath>(EventPlayerDeath);
        
        RegisterEventHandler<EventRoundStart>(EventRoundStart);
        RegisterEventHandler<EventRoundEnd>(EventRoundEnd);
        RegisterEventHandler<EventRoundMvp>(EventRoundMvp);
        
        BombEvents();
        CreateMenu();
        
        AddTimer(5.0f, UpdateUserStatsTimer, TimerFlags.REPEAT);

        if (hotReload)
            foreach (var player in Utilities.GetPlayers())
                if (!player.IsBot && player.AuthorizedSteamID is { } steamId)
                    OnClientAuthorized(player.Slot, steamId);
    }

    public override void Unload(bool hotReload)
    {
        if (_databaseWork is not { } work) return;
        UpdateUserStatsTimer();
        _unloaded = true;
        AppDomain.CurrentDomain.SetData(DatabaseHandoffKey, work.Seal());
        _databaseWork = null;
    }
    
    [ConsoleCommand("css_rank")]
    public void OnCmdRank(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller is not { IsValid: true }) return;

        var steamId = new SteamID(controller.SteamID);
        if (!Users.TryGetValue(steamId.SteamId64, out var user)) return;
        var identity = CapturePlayer(controller);
        var snapshot = user.Snapshot();
        _ = Task.Run(() => GetUserStats(identity, steamId, snapshot));
    }

    [RequiresPermissions("@css/root")]
    [CommandHelper(1, "<username or #userid or @steamid> [exp (def. 0)]")]
    [ConsoleCommand("css_lr_giveexp")]
    public void OnCmdGiveExp(CCSPlayerController? player, CommandInfo info)
    {
        var target = Utils.GetPlayer(info.GetArg(1));

        if (target == null)
        {
            ReplyToCommand(player, "Target is not valid!");
            return;
        }

        var exp = 0;
        if (info.ArgCount >= 3)
            exp = int.TryParse(info.GetArg(2), out var value) ? value : 0;

        RanksApi.GivePlayerExperience(target, exp);
        ReplyToCommand(player, Localizer["command.give_exp", exp, target.PlayerName]);
        ReplyToCommand(target, Localizer["command.target.give_exp", player?.PlayerName ?? "Console", exp]);
    }

    [RequiresPermissions("@css/root")]
    [CommandHelper(1, "<username or #userid or @steamid> [exp (def. 0)]")]
    [ConsoleCommand("css_lr_takeexp")]
    public void OnCmdTakeExp(CCSPlayerController? player, CommandInfo info)
    {
        var target = Utils.GetPlayer(info.GetArg(1));

        if (target == null)
        {
            ReplyToCommand(player, "Target is not valid!");
            return;
        }

        var exp = 0;
        if (info.ArgCount >= 3)
            exp = int.TryParse(info.GetArg(2), out var value) ? value : 0;

        RanksApi.TakePlayerExperience(target, exp);
        ReplyToCommand(player, Localizer["command.take_exp", exp, target.PlayerName]);
        ReplyToCommand(target, Localizer["command.target.take_exp", player?.PlayerName ?? "Console", exp]);
    }

    [ConsoleCommand("css_lr_reload")]
    public void OnCmdReloadCfg(CCSPlayerController? controller, CommandInfo info)
    {
        if (controller != null) return;

        Config = LoadConfig();
        _thresholds = new RankThresholds(Config.Ranks);
        Logger.LogInformation("Configuration successfully rebooted");
    }

    [ConsoleCommand("css_top")]
    public void OnCmdTop(CCSPlayerController? controller, CommandInfo command)
        => QueueTopPlayers(controller);

    private void QueueTopPlayers(CCSPlayerController? controller)
    {
        if (controller is not { IsValid: true }) return;
        var identity = CapturePlayer(controller);
        _ = Task.Run(() => ShowTopPlayers(identity));
    }

    private async Task ShowTopPlayers(PlayerIdentity identity)
    {
        var topPlayersSorted = await Database.GetTopPlayers();

        if (topPlayersSorted == null) return;

        await Server.NextFrameAsync(() =>
        {
            if (_unloaded || ResolvePlayer(identity) is not { } controller) return;
            controller.PrintToChat(Localizer["top.Title"]);
            var rank = 1;
            foreach (var player in topPlayersSorted)
            {
                if (!controller.IsValid) continue;

                //controller.PrintToChat(
                //    $"{rank++}. {ChatColors.Blue}{player.name} \x01[{ChatColors.Olive}{ReplaceColorTags(GetLevelFromExperience(player.value).Name)}\x01] -\x06 Experience: {ChatColors.Blue}{player.value}");
                controller.PrintToChat(
                    $"{rank++}. {Localizer["top.Players", player.name, ReplaceColorTags(GetLevelFromExperience(player.value).Name), player.value]}");
            }
        });
    }
    
    private HookResult CommandListener_Say(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return HookResult.Continue;
        var msg = Utils.GetTextInsideQuotes(info.ArgString);

        if (msg.StartsWith('!') || msg.StartsWith('/')) return HookResult.Continue;

        if (Config.UseCommandWithoutPrefix)
        {
            switch (msg)
            {
                case "rank":
                    OnCmdRank(player, info);
                    return HookResult.Continue;
                case "top":
                    OnCmdTop(player, info);
                    return HookResult.Continue;
            }
        }

        return HookResult.Continue;
    }
    
    private void OnClientAuthorized(int slot, SteamID id)
    {
        var player = Utilities.GetPlayerFromSlot(slot);

        if (player is null || player.IsBot) return;
        _populationTick = null;
        var playerName = player.PlayerName;

        if (_sessions.TryGetValue(slot, out var existing) && existing.SteamId.SteamId64 == id.SteamId64)
            return;
        FinishSession(slot);
        var session = new Session(id);
        _sessions[slot] = session;
        _databaseWork!.Run(id.SteamId64, () => LoadSession(slot, playerName, session));
    }
    
    private async Task LoadSession(int slot, string playerName, Session session)
    {
        await _tableReady.ConfigureAwait(false);
        var steamId = session.SteamId;
        var userExists = await Database.UserExists(steamId.SteamId2);

        if (!userExists)
            await Database.AddUserToDb(playerName, steamId.SteamId2);

        var user = await Database.GetUserStatsFromDb(steamId.SteamId2);

        if (user == null) return;

        Server.NextFrame(() =>
        {
            if (_unloaded || !_sessions.TryGetValue(slot, out var current) || current != session) return;
            var initPoints = Config.InitialExperiencePoints;
            if (user.value <= 0 && initPoints > 0)
                user.value = initPoints;
            Users[steamId.SteamId64] = user;
        });
    }
    
    private void UpdateUserStatsTimer()
    {
        if (_sessions.Count == 0) return;
        var now = Stopwatch.GetTimestamp();
        var lastConnect = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var table = Config.TableName;
        foreach (var session in _sessions.Values)
        {
            if (Users.TryGetValue(session.SteamId.SteamId64, out var user))
                _databaseWork!.Save(session.SteamId.SteamId64, CaptureSave(session, user, now, lastConnect, table));
        }
    }

    private readonly record struct SaveRequest(SteamID SteamId, User Snapshot, TimeSpan Playtime, long LastConnect,
        string Table);

    private static SaveRequest CaptureSave(Session session, User user, long now, long lastConnect, string table)
    {
        var elapsed = Stopwatch.GetElapsedTime(session.SavedAt, now);
        session.SavedAt = now;
        return new SaveRequest(session.SteamId, user.Snapshot(), elapsed, lastConnect, table);
    }

    private static SaveRequest? CombineSaves(SaveRequest older, SaveRequest newer) =>
        older.Table == newer.Table ? newer with { Playtime = older.Playtime + newer.Playtime } : null;

    private async Task Save(SaveRequest save)
    {
        await _tableReady.ConfigureAwait(false);
        await Database.UpdateUserStatsDb(save.SteamId, save.Snapshot, save.Playtime, save.LastConnect, save.Table)
            .ConfigureAwait(false);
    }

    private HookResult EventWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        if (!RanksEnable.Value) return HookResult.Continue;
        var player = @event.Userid;
        if (player != null)
            UpdateUserStatsLocal(player, shoots: 1);

        return HookResult.Continue;
    }

    private HookResult EventPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!RanksEnable.Value) return HookResult.Continue;
        var attacker = @event.Attacker;

        if (attacker is { IsValid: true, IsBot: false })
        {
            UpdateUserStatsLocal(attacker, hits: 1);
        }

        return HookResult.Continue;
    }
    
     private HookResult EventPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!RanksEnable.Value || !PopulationAllowsStats() || !IsLiveGame()) return HookResult.Continue;

        var victim = @event.Userid;
        var attacker = @event.Attacker;
        var assister = @event.Assister;

        var configEvent = Config.Events.EventPlayerDeath;
        var additionally = Config.Events.Additionally;

        var expAttacker = 0;

        if (attacker is not null && attacker.IsValid)
        {
            if (attacker.IsBot || victim is not null && victim.IsBot)
                return HookResult.Continue;

            if (attacker != victim)
            {
                if (victim is not null && attacker.TeamNum == victim.TeamNum && !Config.TeamKillAllowed)
                    UpdateUserStatsLocal(attacker, "KillingAnAlly",
                        exp: configEvent.KillingAnAlly);
                else
                {
                    var attackerStats = GetStatsUser(attacker);
                    var weaponName = @event.Weapon;

                    if (weaponName.Contains("knife", StringComparison.Ordinal) ||
                        weaponName.Contains("bayonet", StringComparison.Ordinal))
                        weaponName = "knife";

                    if (Config.TypeStatistics is 1)
                    {
                        if (victim != null && attackerStats is { value: not 0 } &&
                            Users.TryGetValue(victim.SteamID, out var victimUser))
                            expAttacker = (victimUser.value / attackerStats.value * 5.0f).RoundToNearest();

                        if (expAttacker < 1)
                            expAttacker = 1;

                    }
                    else
                        expAttacker = configEvent.Kills;

                    if (attackerStats != null)
                    {
                        ApplyStats(attacker, attackerStats, "PerKill", exp: expAttacker, kills: 1);
                        if (@event.Penetrated > 0)
                            ApplyStats(attacker, attackerStats, "KillingThroughWall", exp: additionally.Penetrated);
                        if (@event.Thrusmoke)
                            ApplyStats(attacker, attackerStats, "MurderThroughSmoke", exp: additionally.Thrusmoke);
                        if (@event.Noscope)
                            ApplyStats(attacker, attackerStats, "MurderWithoutScope", exp: additionally.Noscope);
                        if (@event.Headshot)
                            ApplyStats(attacker, attackerStats, "MurderToTheHead", exp: additionally.Headshot, headshots: 1);
                        if (@event.Attackerblind)
                            ApplyStats(attacker, attackerStats, "BlindMurder", exp: additionally.Attackerblind);
                        if (Config.Weapon.TryGetValue(weaponName, out var exp))
                            ApplyStats(attacker, attackerStats, "MurderWith", exp: exp, messageArgument: weaponName);
                    }
                }
            }
        }

        if (victim is not null && victim.IsValid)
        {
            if (victim.IsBot)
                return HookResult.Continue;

            if (attacker != victim)
            {
                if (Config.TypeStatistics is 1)
                {
                    var expVictim = (expAttacker * Config.KillCoefficient).RoundToNearest();
                    if (expVictim < 1)
                    {
                        expVictim = 1;
                    }

                    UpdateUserStatsLocal(victim, "PerDeath", exp: -expVictim,
                        death: 1);
                }
                else
                    UpdateUserStatsLocal(victim, "PerDeath", exp: configEvent.Deaths,
                        death: 1);
            }
            else
                UpdateUserStatsLocal(victim, "suicide", exp: configEvent.Suicide);
        }

        if (assister is not null && assister.IsValid)
        {
            if (assister.IsBot) return HookResult.Continue;
            UpdateUserStatsLocal(assister, "AssistingInAKill", exp: configEvent.Assists, assist: 1);
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int slot)
    {
        _populationTick = null;
        if (!_sessions.ContainsKey(slot)) return;
        FinishSession(slot, Utilities.GetPlayerFromSlot(slot)?.PlayerName);
    }

    private void FinishSession(int slot, string? name = null)
    {
        if (!_sessions.Remove(slot, out var session) ||
            !Users.TryRemove(session.SteamId.SteamId64, out var user)) return;
        if (name != null) user.name = name;
        var save = CaptureSave(session, user, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Config.TableName);
        _databaseWork!.Save(session.SteamId.SteamId64, save);
    }

    private HookResult EventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        var playerCount = PlayersCount;
        if (Config.MinPlayers > playerCount && RanksEnable.Value)
        {
            PrintToChatAll(Localizer["NotEnoughPlayers", playerCount, Config.MinPlayers]);
        }

        return HookResult.Continue;
    }

    private HookResult EventRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (@event.Reason is (int)RoundEndReason.RoundDraw) return HookResult.Continue;
        var winner = @event.Winner;

        var configEvent = Config.Events.EventRoundEnd;

        if (!RanksEnable.Value || !PopulationAllowsStats() || !IsLiveGame()) return HookResult.Continue;

        foreach (var player in Utilities.GetPlayers().Where(u => u is { IsValid: true, IsBot: false }))
        {
            if (player.Team is not CsTeam.Spectator)
            {
                if (player.TeamNum != winner)
                    UpdateUserStatsLocal(player, "LosingRound", exp: configEvent.Loser, roundlose: 1);
                else
                    UpdateUserStatsLocal(player, "WinningRound", exp: configEvent.Winner, roundwin: 1);
            }
        }

        return HookResult.Continue;
    }
    
    private HookResult EventRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid) return HookResult.Continue;
        
        UpdateUserStatsLocal(player, "Mvp", exp: Config.Events.EventRoundMvp);

        return HookResult.Continue;
    }

    private void BombEvents()
    {
        RegisterEventHandler<EventBombDropped>((@event, _) =>
        {
            var configEvent = Config.Events.EventPlayerBomb;
            var player = @event.Userid;

            if (player != null && player.IsValid)
                UpdateUserStatsLocal(player, "dropping_bomb", exp: configEvent.DroppedBomb);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombDefused>((@event, _) =>
        {
            var configEvent = Config.Events.EventPlayerBomb;
            var player = @event.Userid;

            if (player != null && player.IsValid)
                UpdateUserStatsLocal(player, "defusing_bomb", exp: configEvent.DefusedBomb);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombPickup>((@event, _) =>
        {
            var configEvent = Config.Events.EventPlayerBomb;
            var player = @event.Userid;

            if (player != null && player.IsValid)
                UpdateUserStatsLocal(player, "raising_bomb", exp: configEvent.PickUpBomb);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventBombPlanted>((@event, _) =>
        {
            var configEvent = Config.Events.EventPlayerBomb;
            var player = @event.Userid;

            if (player != null && player.IsValid)
                UpdateUserStatsLocal(player, "planting_bomb", exp: configEvent.PlantedBomb);
            return HookResult.Continue;
        });
    }
    
    private void CreateMenu()
    {
        AddCommand("css_lvl", "", (player, _) =>
        {
            if (player == null) return;

            var title = Localizer["menu.title"];
            var menu = new CenterHtmlMenu(title, this);
            var ranksMenu = new CenterHtmlMenu(title, this);

            menu.AddMenuOption(Localizer["menu.allranks"], (_, _) => ranksMenu.Open(player));
            menu.AddMenuOption(Localizer["menu.reset"], (_, _) =>
            {
                var subMenu = new CenterHtmlMenu(Localizer["menu.reset.title"], this)
                {
                    PostSelectAction = PostSelectAction.Close
                };

                subMenu.AddMenuOption(Localizer["menu.reset.yes"], (controller, _) =>
                {
                    ResetRank(controller);
                });

                subMenu.AddMenuOption(Localizer["menu.reset.no"],
                    (controller, option) => PrintToChat(controller, Localizer["reset.reset.canceled"]));

                subMenu.Open(player);
            }, !Config.StatisticsResetEnabled);

            foreach (var (rankName, rankValue) in Config.Ranks)
            {
                ranksMenu.AddMenuOption(Localizer["menu.allranks.show", rankName, rankValue], null!, true);
            }

            menu.AddMenuOption(Localizer["menu.top10"],
                (controller, _) => QueueTopPlayers(controller));

            RanksApi.OnCreatedMenu(player, menu);
            menu.Open(player);
        });
    }
    
    private void ResetRank(CCSPlayerController player)
    {
        if (!player.IsValid || !_sessions.TryGetValue(player.Slot, out var session) ||
            session.SteamId.SteamId64 != player.SteamID || !Users.ContainsKey(player.SteamID)) return;
        var steamId2 = session.SteamId.SteamId2;
        var initialExperience = Config.InitialExperiencePoints;
        var table = Config.TableName;
        _databaseWork!.Run(session.SteamId.SteamId64, () => Database.ResetPlayerData(steamId2, initialExperience, table));
        Users[player.SteamID] = new User
        {
            steam = steamId2.ReplaceFirstCharacter(),
            name = player.PlayerName,
            value = initialExperience
        };
        session.SavedAt = Stopwatch.GetTimestamp();
        PrintToChat(player, Localizer["menu.reset.complete"]);
    }

    private void UpdateUserStatsLocal(CCSPlayerController? player, string? messageKey = null,
        int exp = 0, int kills = 0, int death = 0, int assist = 0,
        int shoots = 0, int hits = 0, int headshots = 0, int roundwin = 0, int roundlose = 0,
        string? messageArgument = null)
    {
        if (!RanksEnable.Value || player == null || GetStatsUser(player) is not { } user ||
            !PopulationAllowsStats() || !IsLiveGame())
            return;

        ApplyStats(player, user, messageKey, exp, kills, death, assist, shoots, hits, headshots, roundwin, roundlose,
            messageArgument);
    }

    private User? GetStatsUser(CCSPlayerController player) =>
        player.Team != CsTeam.Spectator && Users.TryGetValue(player.SteamID, out var user) ? user : null;

    private void ApplyStats(CCSPlayerController player, User user, string? messageKey,
        int exp, int kills = 0, int death = 0, int assist = 0,
        int shoots = 0, int hits = 0, int headshots = 0, int roundwin = 0, int roundlose = 0,
        string? messageArgument = null)
    {
        if (!RanksEnable.Value) return;
        var newExp = RanksApi.OnPlayerExperienceChanged(player, exp);
        exp = newExp ?? exp;
        user.value += exp;

        user.kills += kills;
        user.deaths += death;
        user.assists += assist;
        user.round_lose += roundlose;
        user.round_win += roundwin;
        user.headshots += headshots;
        user.hits += hits;
        user.shoots += shoots;

        if (user.value <= 0) user.value = Config.InitialExperiencePoints;

        var rank = _thresholds.Find(user.value);
        if (rank.Level == 0 && rank.NextXp != 0 && user.rank != 0)
        {
            var promoted = user.rank < 0;
            PrintToChat(player, Localizer[promoted ? "Up" : "Down", string.Empty]);
            user.rank = 0;
            RanksApi.OnRankChanged(player, 0, promoted);
        }
        else if (rank.Level != 0)
        {
            user.rank = rank.Level;
        }

        var nextXp = rank.NextXp;
        if (exp != 0 && Config.ShowExperienceMessages)
        {
            var msg = messageKey == null ? string.Empty : messageArgument == null
                ? Localizer[messageKey].Value
                : Localizer[messageKey, messageArgument].Value;
            var identity = CapturePlayer(player);
            Server.NextFrame(() =>
            {
                if (!_unloaded && ResolvePlayer(identity) is { } current)
                    PrintToChat(current,
                        $"{(exp > 0 ? "\x0C+" : "\x02")}{exp} XP \x08{msg} {(nextXp == 0 ? string.Empty : $"{Localizer["next_level", nextXp]}")}");
            });
        }
    }

    public (string Name, int Level) GetLevelFromExperience(long experience)
    {
        var rank = _thresholds.Find(experience);
        return (rank.Name, rank.Level);
    }

    private async Task GetUserStats(PlayerIdentity identity, SteamID steamId, User user)
    {
        var totalTime = TimeSpan.FromSeconds(user.playtime);
        //var formattedTime = totalPlayTime.ToString(@"hh\:mm\:ss");
        var formattedTime =
            $"{(totalTime.Days > 0 ? $"{Localizer["days", totalTime.Days]}, " : "")}" +
            $"{(totalTime.Hours > 0 ? $"{Localizer["hours", totalTime.Hours]}, " : "")}" +
            $"{(totalTime.Minutes > 0 ? $"{Localizer["minutes", totalTime.Minutes]}, " : "")}" +
            $"{(totalTime.Seconds > 0 ? $"{Localizer["seconds", totalTime.Seconds]}" : "")}";
        //var currentPlayTime = (DateTime.Now - _loginTime[index]).ToString(@"hh\:mm\:ss");
        var getPlayerTop = await Database.GetPlayerRankAndTotal(steamId.SteamId2);

        await Server.NextFrameAsync(() =>
        {
            if (_unloaded || ResolvePlayer(identity) is not { } controller) return;

            var headshotPercentage =
                (double)user.headshots / (user.kills + 1) * 100;
            double kdr = 0;
            if (user is { kills: > 0, deaths: > 0 })
                kdr = (double)user.kills / (user.deaths + 1);

            PrintToChat(controller,
                "-------------------------------------------------------------------");
            if (getPlayerTop != null)
                PrintToChat(controller,
                    Localizer["rank.YourPosition", getPlayerTop.PlayerRank, getPlayerTop.TotalPlayers]);

            PrintToChat(controller,
                Localizer["rank.Experience", user.value, ReplaceColorTags(GetLevelFromExperience(user.value).Name)]);
            PrintToChat(controller,
                Localizer["rank.KDA", user.kills, user.headshots, user.deaths, user.assists]);
            PrintToChat(controller, Localizer["rank.Rounds", user.round_win, user.round_lose]);
            PrintToChat(controller,
                Localizer["rank.KDR", headshotPercentage.ToString("0.00"), kdr.ToString("0.00")]);
            PrintToChat(controller, Localizer["rank.PlayTime", formattedTime]);
            PrintToChat(controller,
                "-------------------------------------------------------------------");
        });
    }

    private string BuildConnectionString()
    {
        var dbConfig = Config.Connection;

        Console.WriteLine("Building connection string");
        var builder = new MySqlConnectionStringBuilder
        {
            Database = dbConfig.Database,
            UserID = dbConfig.User,
            Password = dbConfig.Password,
            Server = dbConfig.Host,
            Port = (uint)dbConfig.Port
        };

        Console.WriteLine("OK!");
        return builder.ConnectionString;
    }

    private Config LoadConfig()
    {
        var configPath = Path.Combine(RanksApi.CoreConfigDirectory, "ranks.json");
        if (!File.Exists(configPath)) return CreateConfig(configPath);

        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;

        return config;
    }

    private Config CreateConfig(string configPath)
    {
        var config = new Config
        {
            TableName = "lvl_base",
            Prefix = "[ {BLUE}Ranks {DEFAULT}]",
            TeamKillAllowed = true,
            UseCommandWithoutPrefix = true,
            ShowExperienceMessages = true,
            MinPlayers = 4,
            InitialExperiencePoints = 500,
            StatisticsResetEnabled = true,
            TypeStatistics = 0,
            KillCoefficient = 1.04f,
            Events = new EventsExpSettings
            {
                EventRoundMvp = 12,
                EventPlayerDeath = new PlayerDeath
                    { Kills = 13, Deaths = -20, Assists = 5, KillingAnAlly = -10, Suicide = -15 },
                EventPlayerBomb = new Bomb { DroppedBomb = -5, DefusedBomb = 3, PickUpBomb = 3, PlantedBomb = 4 },
                EventRoundEnd = new RoundEnd { Winner = 5, Loser = -8 },
                Additionally = new Additionally
                    { Headshot = 1, Noscope = 2, Attackerblind = 1, Thrusmoke = 1, Penetrated = 2 }
            },
            Weapon = new Dictionary<string, int>
            {
                ["knife"] = 5,
                ["awp"] = 2
            },
            Ranks = new Dictionary<string, int>
            {
                { "None", 0 },
                { "Silver I", 50 },
                { "Silver II", 100 },
                { "Silver III", 150 },
                { "Silver IV", 300 },
                { "Silver Elite", 400 },
                { "Silver Elite Master", 500 },
                { "Gold Nova I", 600 },
                { "Gold Nova II", 700 },
                { "Gold Nova III", 800 },
                { "Gold Nova Master", 900 },
                { "Master Guardian I", 1000 },
                { "Master Guardian II", 1400 },
                { "Master Guardian Elite", 1600 },
                { "BigStar", 2100 },
                { "Legendary Eagle", 2600 },
                { "Legendary Eagle Master", 2900 },
                { "Supreme", 3400 },
                { "The Global Elite", 4500 }
            },
            Connection = new RankDb
            {
                Host = "HOST",
                Database = "DATABASE_NAME",
                User = "USER_NAME",
                Password = "PASSWORD",
                Port = 3306
            }
        };

        File.WriteAllText(configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return config;
    }

    public void PrintToChat(CCSPlayerController player, string message)
    {
        if (!player.IsValid) return;

        player.PrintToChat($"{ReplaceColorTags(Config.Prefix)} {message}");
    }

    private void ReplyToCommand(CCSPlayerController? player, string message)
    {
        if (player == null)
            Logger.LogInformation(message);
        else
        {
            if (!player.IsValid) return;
            PrintToChat(player, message);
        }
    }

    public void PrintToChatAll(string message)
    {
        Server.PrintToChatAll($"{ReplaceColorTags(Config.Prefix)} {message}");
    }

    private string ReplaceColorTags(string input)
    {
        return input.ReplaceColorTags();
    }

    private bool PopulationAllowsStats() => Config.MinPlayers <= 0 || PlayersCount >= Config.MinPlayers;

    private HookResult InvalidatePopulation()
    {
        _populationTick = null;
        return HookResult.Continue;
    }

    private void InvalidateMapState()
    {
        _rulesProxy = null;
        _missingRulesTick = null;
        _populationTick = null;
    }

    private bool IsLiveGame()
    {
        if (_rulesProxy?.Value is not { IsValid: true } proxy)
        {
            var tick = Server.TickCount;
            if (_missingRulesTick == tick) return false;
            proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            if (proxy == null)
            {
                _missingRulesTick = tick;
                return false;
            }
            _rulesProxy = new CHandle<CCSGameRulesProxy>(proxy.EntityHandle.Raw);
            _missingRulesTick = null;
        }

        return proxy.GameRules is { WarmupPeriod: false };
    }

    private int PlayersCount
    {
        get
        {
            var tick = Server.TickCount;
            if (_populationTick == tick) return _population;

            var count = 0;
            foreach (var player in Utilities.GetPlayers())
            {
                if (player is { IsBot: false, IsValid: true, TeamNum: not (0 or 1), PlayerPawn.Value.IsValid: true })
                    count++;
            }

            _population = count;
            _populationTick = tick;
            return count;
        }
    }

    private readonly record struct PlayerIdentity(ulong SteamId, int? UserId, uint Handle);

    private static PlayerIdentity CapturePlayer(CCSPlayerController player) =>
        new(player.SteamID, player.UserId, player.EntityHandle.Raw);

    private static CCSPlayerController? ResolvePlayer(PlayerIdentity identity)
    {
        var player = new CHandle<CCSPlayerController>(identity.Handle).Value;
        return player is { IsValid: true } && player.SteamID == identity.SteamId &&
               player.UserId == identity.UserId ? player : null;
    }

}

public class PlayerStats
{
    public int PlayerRank { get; init; }
    public int TotalPlayers { get; init; }
}

public class Config
{
    public required string TableName { get; init; }
    public required string Prefix { get; init; }
    public bool TeamKillAllowed { get; init; }
    public bool UseCommandWithoutPrefix { get; init; }
    public bool ShowExperienceMessages { get; init; }
    public int MinPlayers { get; init; }
    public int InitialExperiencePoints { get; init; }
    public bool StatisticsResetEnabled { get; init; }
    public int TypeStatistics { get; init; }
    public float KillCoefficient { get; init; }
    public EventsExpSettings Events { get; init; } = null!;
    public Dictionary<string, int> Weapon { get; init; } = null!;
    public Dictionary<string, int> Ranks { get; init; } = null!;
    public RankDb Connection { get; init; } = null!;
}
