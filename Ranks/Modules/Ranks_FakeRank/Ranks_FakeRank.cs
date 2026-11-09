using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using RanksApi;

namespace Ranks_FakeRank;

[MinimumApiVersion(329)]
public class RanksFakeRank : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] Fake Rank";
    public override string ModuleVersion => "v1.0.2-perf.1";

    private Config? _config;
    private IRanksApi? _api;
    private int _maxRank;
    private readonly Dictionary<int, AppliedRank> _applied = new();
    private readonly List<CCSPlayerController> _viewers = new();

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        TryInitialize();
        AddTimer(0.25f, Reconcile, TimerFlags.REPEAT);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtonsChanged);
        RegisterListener<Listeners.OnTick>(RevealWhileHeld);
        RegisterListener<Listeners.OnMapStart>(_ => _applied.Clear());
        RegisterEventHandler<EventPlayerDisconnect>((e, _) =>
        {
            if (e.Userid != null) _applied.Remove(e.Userid.Slot);
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
            _applied.Clear();
            Reconcile();
        });
    }

    public override void Unload(bool hotReload)
    {
        if (_api != null) _api.RankChanged -= OnRankChanged;
        _viewers.Clear();
        _applied.Clear();
    }

    private bool TryInitialize()
    {
        if (_api != null && _config != null) return true;
        _api = IRanksApi.Capability.Get();
        if (_api == null) return false;
        _config = _api.LoadConfig<Config>("ranks_fakerank");
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
        var value = RankProjection.Project(_config.Type, raw, _config.FakeRank, _maxRank);
        var type = (sbyte)(_config.Type == 0 ? 11 : 12);
        var identity = new PlayerIdentity(player.SteamID, player.UserId);
        var next = new AppliedRank(identity, 111, type, value);
        if (force || !_applied.TryGetValue(player.Slot, out var previous) || previous != next)
            Apply(player, next);
        _applied[player.Slot] = next;
    }

    private void OnRankChanged(CCSPlayerController player, int _, bool __)
    {
        if (Eligible(player)) UpdatePlayer(player);
    }

    private void ApplyCached(CCSPlayerController player)
    {
        if (!_applied.TryGetValue(player.Slot, out var rank) ||
            rank.Identity != new PlayerIdentity(player.SteamID, player.UserId))
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
            // Keep the client's rank fields warm before the first TAB packet.
            // Earlier 4 Hz-only candidates failed; held TAB still writes each tick.
            UpdatePlayer(player!, force: true);
        }
    }

    private void OnButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if ((pressed & PlayerButtons.Scoreboard) == 0 || !Eligible(player) || !TryInitialize()) return;
        // RevealAll includes every player: refresh values before sending.
        foreach (var other in Utilities.GetPlayers())
            if (Eligible(other))
            {
                UpdatePlayer(other!, force: true);
            }
        var filter = new RecipientFilter();
        filter.Add(player);
        SendReveal(filter);
    }

    private void RevealWhileHeld()
    {
        if (_config == null) return;
        _viewers.Clear();
        foreach (var player in Utilities.GetPlayers())
        {
            if (!Eligible(player)) continue;
            if ((player.Buttons & PlayerButtons.Scoreboard) != 0)
                _viewers.Add(player);
        }
        if (_viewers.Count == 0) return;

        // The tested build needed per-tick field writes while TAB was held.
        // Refresh all visible ranks before revealing, including late joiners.
        var filter = new RecipientFilter();
        foreach (var viewer in _viewers) filter.Add(viewer);
        foreach (var player in Utilities.GetPlayers())
            if (Eligible(player)) ApplyCached(player!);
        SendReveal(filter);
    }

    private static void SendReveal(RecipientFilter filter) =>
        UserMessage.FromPartialName("CCSUsrMsg_ServerRankRevealAll").Send(filter);

    private static void Apply(CCSPlayerController player, AppliedRank rank)
    {
        player.CompetitiveWins = rank.Wins;
        player.CompetitiveRankType = rank.Type;
        player.CompetitiveRanking = rank.Value;
    }
}

internal readonly record struct PlayerIdentity(ulong SteamId, int? UserId);
internal readonly record struct AppliedRank(PlayerIdentity Identity, int Wins, sbyte Type, int Value);

public static class RankProjection
{
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
