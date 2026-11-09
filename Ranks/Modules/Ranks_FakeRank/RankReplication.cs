using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FakeRankChecks")]

namespace Ranks_FakeRank;

internal readonly record struct PlayerIdentity(ulong SteamId, int? UserId, nint Controller);
internal readonly record struct RankValues(int Wins, sbyte Type, int Value);
internal readonly record struct AppliedRank(PlayerIdentity Identity, RankValues Values);

[Flags]
internal enum RankFields { None = 0, Wins = 1, Type = 2, Value = 4, All = Wins | Type | Value }

internal interface ICompetitiveRankFields
{
    RankValues Read();
    void Write(RankValues values);
    void Notify(RankFields field);
}

// Separate desired values from successfully notified values. A failed write/notify
// must be retried, and live drift must be repaired even when the cache is unchanged.
internal sealed class RankReplication
{
    private readonly Dictionary<int, AppliedRank> _notified = new();
    public long Checks { get; private set; }
    public long Skipped { get; private set; }
    public long Applied { get; private set; }
    public long Notifications { get; private set; }
    public long DriftRepairs { get; private set; }
    public long Failures { get; private set; }

    public AppliedRank? LastNotified(int slot) => _notified.TryGetValue(slot, out var value) ? value : null;
    public void Remove(int slot) => _notified.Remove(slot);
    public void Clear() => _notified.Clear();

    public bool Apply<T>(int slot, AppliedRank desired, T fields, bool force = false)
        where T : ICompetitiveRankFields
    {
        Checks++;
        try
        {
            var live = fields.Read();
            var sameConnection = _notified.TryGetValue(slot, out var previous) && previous.Identity == desired.Identity;
            var changed = sameConnection && !force
                ? Difference(live, desired.Values) | Difference(previous.Values, desired.Values)
                : RankFields.All;
            if (changed == RankFields.None)
            {
                Skipped++;
                return false;
            }

            // Write the complete triple before making any part eligible for a snapshot.
            fields.Write(desired.Values);
            Notify(RankFields.Wins);
            Notify(RankFields.Type);
            Notify(RankFields.Value);
            _notified[slot] = desired;
            Applied++;
            if (sameConnection && previous.Values == desired.Values && live != desired.Values) DriftRepairs++;
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
            Failures++;
            throw;
        }
    }

    private static RankFields Difference(RankValues a, RankValues b) =>
        (a.Wins != b.Wins ? RankFields.Wins : RankFields.None) |
        (a.Type != b.Type ? RankFields.Type : RankFields.None) |
        (a.Value != b.Value ? RankFields.Value : RankFields.None);
}
