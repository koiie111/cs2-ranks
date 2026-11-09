using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using RanksApi;
using Microsoft.Extensions.Logging;

namespace Ranks_FakeRank;

[MinimumApiVersion(329)]
public class RanksFakeRank : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] Fake Rank";
    public override string ModuleVersion => "v1.0.3-replication.3";

    private Config? _config;
    private IRanksApi? _api;
    private int _maxRank;
    private readonly Dictionary<int, AppliedRank> _applied = new();
    private readonly RankReplication _replication = new();
    private DateTime _nextErrorLog;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        TryInitialize();
        AddTimer(0.25f, Reconcile, TimerFlags.REPEAT);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtonsChanged);
        RegisterListener<Listeners.OnTick>(ReconcileCached);
        RegisterListener<Listeners.OnMapStart>(_ => ClearState());
        RegisterEventHandler<EventPlayerDisconnect>((e, _) =>
        {
            if (e.Userid != null)
            {
                _applied.Remove(e.Userid.Slot);
                _replication.Remove(e.Userid.Slot);
            }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerSpawn>((e, _) =>
        {
            if (Eligible(e.Userid)) UpdatePlayer(e.Userid!, force: true);
            return HookResult.Continue;
        });
        AddCommand("css_fakerank_reload", "", (player, _) =>
        {
            if (player != null || _api == null) return;
            _config = _api.LoadConfig<Config>("ranks_fakerank");
            _maxRank = RankProjection.MaxRank(_config.FakeRank);
            ClearState();
            Reconcile();
        });
        AddCommand("css_fakerank_status", "Show FakeRank replication diagnostics (server console only)", PrintStatus);
    }

    public override void Unload(bool hotReload)
    {
        if (_api != null) _api.RankChanged -= OnRankChanged;
        ClearState();
    }

    private bool TryInitialize()
    {
        if (_api != null && _config != null) return true;
        IRanksApi? api;
        try { api = IRanksApi.Capability.Get(); }
        catch (KeyNotFoundException) { return false; }
        if (api == null) return false;
        _config = api.LoadConfig<Config>("ranks_fakerank");
        _api = api;
        _maxRank = RankProjection.MaxRank(_config.FakeRank);
        _api.RankChanged += OnRankChanged;
        return true;
    }

    private static bool Eligible(CCSPlayerController? player) =>
        player is { IsValid: true, Connected: PlayerConnectedState.PlayerConnected, IsBot: false, IsHLTV: false };

    private void UpdatePlayer(CCSPlayerController player, bool force = false)
    {
        if (_api == null || _config == null) return;
        var raw = _config.Type == 0 ? _api.GetPlayerExperience(player) : _api.GetPlayerRank(player);
        if (!RankProjection.TryProject(_config.Type, raw, _config.FakeRank, _maxRank, out var value))
        {
            _applied.Remove(player.Slot);
            return;
        }
        var type = (sbyte)(_config.Type == 0 ? 11 : 12);
        var next = new AppliedRank(Identity(player), new RankValues(111, type, value));
        _applied[player.Slot] = next;
        Apply(player, next, force);
    }

    private void OnRankChanged(CCSPlayerController player, int _, bool __)
    {
        if (Eligible(player)) UpdatePlayer(player);
    }

    private void ApplyCached(CCSPlayerController player)
    {
        if (!_applied.TryGetValue(player.Slot, out var rank) ||
            rank.Identity != Identity(player))
            UpdatePlayer(player, force: true);
        else
            Apply(player, rank);
    }

    private void Reconcile()
    {
        if (!TryInitialize()) return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!Eligible(player)) continue;
            // Poll direct API XP writes too; Apply also detects live controller drift.
            UpdatePlayer(player!);
        }
    }

    private void OnButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if ((pressed & PlayerButtons.Scoreboard) == 0 || !Eligible(player) || !TryInitialize()) return;
        // RevealAll includes every player: refresh values before sending.
        foreach (var other in Utilities.GetPlayers())
            if (Eligible(other))
            {
                UpdatePlayer(other!);
            }
        // Native FakeRanks-RevealAll owns disclosure. Repeated managed reveal
        // packets cause client-side rank flicker even with stable controller fields.
    }

    private void ReconcileCached()
    {
        if (_config == null) return;

        // The client opens TAB before its input reaches the server. Keep native
        // fields warm even while TAB is closed, so disclosure never starts from
        // the engine's cleared fields between the 0.25-second API polls.
        foreach (var player in Utilities.GetPlayers())
            if (Eligible(player)) ApplyCached(player!);
    }

    private void Apply(CCSPlayerController player, AppliedRank rank, bool force = false)
    {
        try { _replication.Apply(player.Slot, rank, new ControllerRankFields(player), force); }
        catch (Exception ex)
        {
            if (DateTime.UtcNow < _nextErrorLog) return;
            _nextErrorLog = DateTime.UtcNow.AddSeconds(10);
            Logger.LogError(ex, "FakeRank replication failed for slot {Slot}; will retry", player.Slot);
        }
    }

    private static PlayerIdentity Identity(CCSPlayerController player) => new(player.SteamID, player.UserId, player.Handle);

    private void ClearState()
    {
        _applied.Clear();
        _replication.Clear();
    }

    private void PrintStatus(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return;
        int? slot = null;
        if (command.ArgCount > 2 || command.ArgCount == 2 &&
            (!int.TryParse(command.GetArg(1), out var parsed) || parsed < 0))
        {
            command.ReplyToCommand("Usage: css_fakerank_status [slot 0-based]");
            return;
        }
        if (command.ArgCount == 2) slot = int.Parse(command.GetArg(1));
        command.ReplyToCommand($"[FakeRank] version={ModuleVersion} api={_api != null} config={_config != null} mode={_config?.Type} checks={_replication.Checks} applied={_replication.Applied} skipped={_replication.Skipped} notifications={_replication.Notifications} drift={_replication.DriftRepairs} failures={_replication.Failures} reveal=external-metamod sync=continuous");
        command.ReplyToCommand($"[FakeRank] networked wins={Schema.IsSchemaFieldNetworked("CCSPlayerController", "m_iCompetitiveWins")} type={Schema.IsSchemaFieldNetworked("CCSPlayerController", "m_iCompetitiveRankType")} value={Schema.IsSchemaFieldNetworked("CCSPlayerController", "m_iCompetitiveRanking")}");
        foreach (var player in Utilities.GetPlayers())
        {
            if (!Eligible(player) || slot.HasValue && player.Slot != slot.Value) continue;
            var raw = _api == null || _config == null ? -1 : _config.Type == 0 ? _api.GetPlayerExperience(player) : _api.GetPlayerRank(player);
            int expectedValue = 0;
            var ready = _config != null && RankProjection.TryProject(_config.Type, raw, _config.FakeRank, _maxRank, out expectedValue);
            var expected = ready ? new RankValues(111, (sbyte)(_config!.Type == 0 ? 11 : 12), expectedValue).ToString() : "waiting";
            var live = new ControllerRankFields(player).Read();
            command.ReplyToCommand($"[FakeRank] slot={player.Slot} userid={player.UserId} steam={player.SteamID} raw={raw} expected={expected} live={live} notified={_replication.LastNotified(player.Slot)}");
        }
    }

    private readonly struct ControllerRankFields(CCSPlayerController player) : ICompetitiveRankFields
    {
        public RankValues Read() => new(player.CompetitiveWins, player.CompetitiveRankType, player.CompetitiveRanking);
        public void Write(RankValues values)
        {
            player.CompetitiveWins = values.Wins;
            player.CompetitiveRankType = values.Type;
            player.CompetitiveRanking = values.Value;
        }
        public void Notify(RankFields field) => Utilities.SetStateChanged(player, "CCSPlayerController", field switch
        {
            RankFields.Wins => "m_iCompetitiveWins",
            RankFields.Type => "m_iCompetitiveRankType",
            RankFields.Value => "m_iCompetitiveRanking",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        });
    }
}

public static class RankProjection
{
    public static bool TryProject(int type, int raw, IReadOnlyDictionary<int, int> fakeRanks, int maxRank, out int value)
    {
        value = 0;
        if (raw < 0) return false; // RanksApi uses -1 while this player's data is unavailable.
        value = Project(type, raw, fakeRanks, maxRank);
        return true;
    }

    public static int MaxRank(IReadOnlyDictionary<int, int> fakeRanks)
    {
        var max = 1;
        foreach (var key in fakeRanks.Keys) max = Math.Max(max, key);
        return max;
    }

    public static int Project(int type, int raw, IReadOnlyDictionary<int, int> fakeRanks, int maxRank)
    {
        if (type == 0) return Math.Min(raw, 99999);
        return Math.Min(fakeRanks[Math.Clamp(raw, 1, maxRank)], 99999);
    }
}

public class Config
{
    public int Type { get; set; } = 1;
    public Dictionary<int, int> FakeRank { get; set; } = new()
    {
        [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5] = 5, [6] = 6,
        [7] = 7, [8] = 8, [9] = 9, [10] = 10, [11] = 11, [12] = 12,
        [13] = 13, [14] = 14, [15] = 15, [16] = 16, [17] = 17, [18] = 18,
    };
}
