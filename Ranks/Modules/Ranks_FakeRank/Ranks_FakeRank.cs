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
    public override string ModuleVersion => "v1.0.4.1";

    internal sealed class Participant
    {
        public CCSPlayerController? Player;
        public AppliedRank? Desired;
        public AppliedRank? Notified;
        // New connections can initialize on their first ready tick. After a full
        // poll finds no data, the timer, TAB and lifecycle events own retries.
        private bool _hasPolled;

        public bool ShouldRefresh(bool fullPoll)
        {
            if (fullPoll) _hasPolled = true;
            return Desired.HasValue || fullPoll || !_hasPolled;
        }

        public void OnAuthorized()
        {
            Desired = null;
            _hasPolled = false;
        }
    }

    private Config? _config;
    private IRanksApi? _api;
    private int _maxRank;
    private readonly Dictionary<int, Participant> _participants = new();
    private bool _discoverPlayers = true;
    private RankFieldLayout? _layout;
    private DateTime _nextLayoutAttempt;
    private DateTime _nextConfigAttempt;
    private int? _lastTabTick;
    private bool _guidelinesBlocked;
    private readonly RankReplication _replication = new();
    private DateTime _nextErrorLog;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        TryInitialize();
        AddTimer(0.25f, PollRanks, TimerFlags.REPEAT);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtonsChanged);
        RegisterListener<Listeners.OnTick>(() => RefreshPlayers(poll: false));
        RegisterListener<Listeners.OnMapStart>(_ => ClearState());
        RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            _participants[slot] = new Participant();
            _lastTabTick = null;
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot => _participants.Remove(slot));
        RegisterListener<Listeners.OnClientAuthorized>((slot, _) =>
        {
            if (_participants.TryGetValue(slot, out var participant))
            {
                participant.OnAuthorized();
                _lastTabTick = null;
            }
        });
        RegisterEventHandler<EventPlayerSpawn>((e, _) =>
        {
            RefreshPlayer(e.Userid, force: true);
            return HookResult.Continue;
        });
        AddCommand("css_fakerank_reload", "", (player, _) =>
        {
            if (player != null) return;
            _nextConfigAttempt = default;
            var loaded = _api == null ? TryInitialize() : TryLoadConfig(_api);
            if (!loaded) return;
            ClearState();
            PollRanks();
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
        if (DateTime.UtcNow < _nextConfigAttempt || !TryLoadConfig(api)) return false;
        _api = api;
        _api.RankChanged += OnRankChanged;
        return true;
    }

    private bool TryLoadConfig(IRanksApi api)
    {
        try
        {
            var config = api.LoadConfig<Config>("ranks_fakerank");
            if (config.FakeRank is null && config.Type != 0)
                throw new InvalidDataException("FakeRank mapping is required in rank mode");
            config.FakeRank ??= new();
            var maxRank = RankProjection.MaxRank(config.FakeRank);
            _config = config;
            _maxRank = maxRank;
            return true;
        }
        catch (Exception ex)
        {
            _nextConfigAttempt = DateTime.UtcNow.AddSeconds(10);
            Logger.LogError(ex, "Could not load ranks_fakerank configuration");
            return false;
        }
    }

    private static bool Eligible(CCSPlayerController? player) =>
        player is { IsValid: true, Connected: PlayerConnectedState.PlayerConnected, IsBot: false, IsHLTV: false };

    private void OnRankChanged(CCSPlayerController player, int _, bool __) => RefreshPlayer(player);

    private void RefreshPlayer(CCSPlayerController? player, bool force = false)
    {
        if (player is null || !Eligible(player) || !PrepareLayout()) return;
        var slot = player.Slot;
        if (!_participants.TryGetValue(slot, out var participant))
        {
            participant = new Participant();
            _participants.Add(slot, participant);
        }
        participant.Player = player;
        PollPlayer(participant, player, force);
    }

    private void PollRanks()
    {
        if (TryInitialize()) RefreshPlayers(poll: true);
    }

    private void OnButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if ((pressed & PlayerButtons.Scoreboard) == 0) return;
        var tick = Server.TickCount;
        if (_lastTabTick == tick || !Eligible(player) || !TryInitialize()) return;
        RefreshPlayers(poll: true);
        _lastTabTick = tick;
    }

    private void RefreshPlayers(bool poll)
    {
        if (!PrepareLayout()) return;
        if (_discoverPlayers)
        {
            // Hot reload and map changes can leave clients already in the server.
            var capacity = Server.MaxPlayers;
            for (var slot = 0; slot < capacity; slot++)
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is { IsValid: true })
                    _participants.TryAdd(slot, new Participant { Player = player });
            }
            _discoverPlayers = false;
        }

        foreach (var (slot, participant) in _participants)
        {
            if (!participant.ShouldRefresh(poll)) continue;
            var player = participant.Player;
            // Handle resolves CSS's index + serial. Never retain its address across callbacks.
            var address = player?.Handle ?? 0;
            if (address == 0)
            {
                participant.Desired = null;
                participant.Notified = null;
                player = participant.Player = Utilities.GetPlayerFromSlot(slot);
                address = player?.Handle ?? 0;
                if (address == 0) continue; // PutInServer may precede controller creation.
            }

            if (participant.Desired is { } rank && !poll)
            {
                // Repair engine resets before the first local TAB draw, including while TAB is closed.
                Apply(participant, player!, address, rank);
                continue;
            }

            if (player!.Connected != PlayerConnectedState.PlayerConnected)
            {
                participant.Desired = null;
                continue;
            }
            if (player.IsBot || player.IsHLTV)
            {
                // Dictionary.Remove during enumeration is supported on .NET 8.
                _participants.Remove(slot);
                continue;
            }
            PollPlayer(participant, player, force: participant.Desired == null);
        }
    }

    private void PollPlayer(Participant participant, CCSPlayerController player, bool force)
    {
        var config = _config!;
        var raw = config.Type == 0 ? _api!.GetPlayerExperience(player) : _api!.GetPlayerRank(player);
        var address = player.Handle;
        if (address == 0 || !RankProjection.TryProject(config.Type, raw, config.FakeRank, _maxRank, out var value))
        {
            participant.Desired = null;
            return;
        }
        var identity = new PlayerIdentity(player.SteamID, player.UserId, player.EntityHandle.Raw);
        var rank = new AppliedRank(identity, new RankValues(111, (sbyte)(config.Type == 0 ? 11 : 12), value));
        participant.Desired = rank;
        Apply(participant, player, address, rank, force);
    }

    private void Apply(Participant participant, CCSPlayerController player, nint address, AppliedRank rank, bool force = false)
    {
        try
        {
            _replication.Apply(ref participant.Notified, rank, new ControllerRankFields(player, address, _layout!.Value), force);
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow < _nextErrorLog) return;
            _nextErrorLog = DateTime.UtcNow.AddSeconds(10);
            Logger.LogError(ex, "FakeRank replication failed for slot {Slot}; will retry", player.Slot);
        }
    }

    private bool PrepareLayout()
    {
        if (_config == null) return false;
        // Cached offsets must respect the same runtime policy as the generated accessors.
        if (CoreConfig.FollowCS2ServerGuidelines)
        {
            if (!_guidelinesBlocked)
            {
                Logger.LogWarning("FakeRank synchronization is disabled by FollowCS2ServerGuidelines");
                ClearState();
                _guidelinesBlocked = true;
            }
            return false;
        }
        _guidelinesBlocked = false;
        if (_layout.HasValue) return true;
        if (DateTime.UtcNow < _nextLayoutAttempt) return false;
        try
        {
            _layout = RankFieldLayout.Resolve();
            return true;
        }
        catch (Exception ex)
        {
            _nextLayoutAttempt = DateTime.UtcNow.AddSeconds(10);
            Logger.LogError(ex, "FakeRank could not resolve the controller schema; will retry");
            return false;
        }
    }

    private void ClearState()
    {
        _participants.Clear();
        _discoverPlayers = true;
        _lastTabTick = null;
        _layout = null;
        _nextLayoutAttempt = default;
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
        if (CoreConfig.FollowCS2ServerGuidelines)
        {
            command.ReplyToCommand("[FakeRank] Rank fields are disabled by FollowCS2ServerGuidelines.");
            return;
        }
        command.ReplyToCommand($"[FakeRank] networked wins={Schema.IsSchemaFieldNetworked("CCSPlayerController", "m_iCompetitiveWins")} type={Schema.IsSchemaFieldNetworked("CCSPlayerController", "m_iCompetitiveRankType")} value={Schema.IsSchemaFieldNetworked("CCSPlayerController", "m_iCompetitiveRanking")}");
        foreach (var player in Utilities.GetPlayers())
        {
            if (!Eligible(player) || slot.HasValue && player.Slot != slot.Value) continue;
            var raw = _api == null || _config == null ? -1 : _config.Type == 0 ? _api.GetPlayerExperience(player) : _api.GetPlayerRank(player);
            int expectedValue = 0;
            var ready = _config != null && RankProjection.TryProject(_config.Type, raw, _config.FakeRank, _maxRank, out expectedValue);
            var expected = ready ? new RankValues(111, (sbyte)(_config!.Type == 0 ? 11 : 12), expectedValue).ToString() : "waiting";
            var live = new RankValues(player.CompetitiveWins, player.CompetitiveRankType, player.CompetitiveRanking);
            _participants.TryGetValue(player.Slot, out var participant);
            command.ReplyToCommand($"[FakeRank] slot={player.Slot} userid={player.UserId} steam={player.SteamID} raw={raw} expected={expected} live={live} notified={participant?.Notified}");
        }
    }

    private readonly struct ControllerRankFields(CCSPlayerController player, nint address, RankFieldLayout layout) : ICompetitiveRankFields
    {
        public RankValues Read() => layout.Read(address);
        public void Write(RankValues values, RankFields dirty)
        {
            if ((dirty & RankFields.Wins) != 0) player.CompetitiveWins = values.Wins;
            if ((dirty & RankFields.Type) != 0) player.CompetitiveRankType = values.Type;
            if ((dirty & RankFields.Value) != 0) player.CompetitiveRanking = values.Value;
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
        var projected = raw;
        if (type != 0 && !fakeRanks.TryGetValue(Math.Clamp(raw, 1, maxRank), out projected))
            return false;
        value = Math.Min(projected, 99999);
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
