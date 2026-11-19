using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FakeRankChecks")]

namespace Ranks_FakeRank;

internal readonly record struct PlayerIdentity(ulong SteamId, int? UserId, uint Controller);
internal readonly record struct RankValues(int Wins, sbyte Type, int Value);
internal readonly record struct AppliedRank(PlayerIdentity Identity, RankValues Values);

[Flags]
internal enum RankFields { None = 0, Wins = 1, Type = 2, Value = 4, All = Wins | Type | Value }

internal interface ICompetitiveRankFields
{
    RankValues Read();
    void Write(RankValues values, RankFields dirty);
    void Notify(RankFields field);
}

// Separate desired values from successfully notified values. A failed write/notify
// must be retried, and live drift must be repaired even when the cache is unchanged.
internal sealed class RankReplication
{
    public long Checks { get; private set; }
    public long Skipped { get; private set; }
    public long Applied { get; private set; }
    public long Notifications { get; private set; }
    public long DriftRepairs { get; private set; }
    public long Failures { get; private set; }

    public bool Apply<T>(ref AppliedRank? notified, AppliedRank desired, T fields, bool force = false)
        where T : ICompetitiveRankFields
    {
        Checks++;
        try
        {
            var live = fields.Read();
            if (!force && live == desired.Values && notified == desired)
            {
                Skipped++;
                return false;
            }

            var dirty = Difference(live, desired.Values);
            var changed = RankFields.All;
            if (!force && notified is { } previous && previous.Identity == desired.Identity)
                changed = dirty | Difference(previous.Values, desired.Values);

            // Make the whole triple coherent before the first notification.
            if (dirty != RankFields.None) fields.Write(desired.Values, dirty);
            Notify(RankFields.Wins);
            Notify(RankFields.Type);
            Notify(RankFields.Value);
            if (notified == desired && live != desired.Values) DriftRepairs++;
            notified = desired;
            Applied++;
            return true;

            void Notify(RankFields field)
            {
                if ((changed & field) == 0) return;
                fields.Notify(field);
                Notifications++;
            }
        }
        catch
        {
            // A drift repair or forced spawn can fail after Write even for an
            // already-notified rank. Matching live values must not hide that failure.
            notified = null;
            Failures++;
            throw;
        }
    }

    private static RankFields Difference(RankValues a, RankValues b) =>
        (a.Wins != b.Wins ? RankFields.Wins : RankFields.None) |
        (a.Type != b.Type ? RankFields.Type : RankFields.None) |
        (a.Value != b.Value ? RankFields.Value : RankFields.None);
}
