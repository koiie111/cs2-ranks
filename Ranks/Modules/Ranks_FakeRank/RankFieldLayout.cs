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
        layout.Validate(Schema.GetClassSize(controller));
        // SetStateChanged only logs and returns for a non-networked field.
        foreach (var field in new[] { "m_iCompetitiveWins", "m_iCompetitiveRankType", "m_iCompetitiveRanking" })
            if (!Schema.IsSchemaFieldNetworked(controller, field))
                throw new InvalidOperationException($"Competitive rank field {field} is not networked");
        return layout;
    }

    internal void Validate(int size)
    {
        // Schema offsets are signed shorts. Use the widths of the generated CSS properties.
        if (Wins <= 0 || Wins + sizeof(int) > size ||
            Type <= 0 || Type + sizeof(sbyte) > size ||
            Value <= 0 || Value + sizeof(int) > size ||
            Wins < Value + sizeof(int) && Value < Wins + sizeof(int) ||
            Type >= Wins && Type < Wins + sizeof(int) ||
            Type >= Value && Type < Value + sizeof(int))
            throw new InvalidOperationException("Invalid or overlapping competitive rank field offsets");
    }

    public RankValues Read(nint controller) => new(
        Marshal.ReadInt32(controller, Wins),
        unchecked((sbyte)Marshal.ReadByte(controller, Type)),
        Marshal.ReadInt32(controller, Value));
}
