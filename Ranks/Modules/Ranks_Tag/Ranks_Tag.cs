using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using Dapper;
using MySqlConnector;
using RanksApi;

namespace Ranks_Tag;

public class RanksTag : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[Ranks] Tag";
    public override string ModuleVersion => "v1.0.0";

    private IRanksApi? _api;
    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = IRanksApi.Capability.Get();
        if (_api == null) return;

        AddTimer(5f, () =>
        {
            if (_tags.Count > 256) _tags.Clear();
            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid) continue;
                var level = _api.GetLevelFromExperience(_api.GetPlayerExperience(player));
                if (!_tags.TryGetValue(level.Name, out var tag))
                {
                    tag = $"[{Regex.Replace(level.Name, @"\{[A-Za-z]+}", "")}]";
                    _tags.Add(level.Name, tag);
                }

                if (string.Equals(player.Clan, tag, StringComparison.Ordinal)) continue;
                player.Clan = tag;
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
            }
        }, TimerFlags.REPEAT);
    }
}
