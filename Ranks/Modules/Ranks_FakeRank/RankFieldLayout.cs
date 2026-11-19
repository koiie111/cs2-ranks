using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory;

namespace Ranks_FakeRank;

// Only the layout survives a tick. The caller supplies a freshly resolved controller.
internal readonly record struct RankFieldLayout(int Wins, int Type, int Value)
{
    public static RankFieldLayout Resolve()
    {
        const string controller = "CCSPlayerController";
        var layout = new RankFieldLayout(
            Schema.GetSchemaOffset(controller, "m_iCompetitiveWins"),
            Schema.GetSchemaOffset(controller, "m_iCompetitiveRankType"),
            Schema.GetSchemaOffset(controller, "m_iCompetitiveRanking"));
        var size = Schema.GetClassSize(controller);
        if (layout.Wins <= 0 || layout.Wins + sizeof(int) > size ||
            layout.Type <= 0 || layout.Type + sizeof(sbyte) > size ||
            layout.Value <= 0 || layout.Value + sizeof(int) > size)
            throw new InvalidOperationException("Competitive rank fields are outside the controller schema");
        return layout;
    }

    public RankValues Read(nint controller) => new(
        Marshal.ReadInt32(controller, Wins),
        unchecked((sbyte)Marshal.ReadByte(controller, Type)),
        Marshal.ReadInt32(controller, Value));
}
