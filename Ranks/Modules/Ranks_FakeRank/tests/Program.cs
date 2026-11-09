using Ranks_FakeRank;
using CounterStrikeSharp.API;

var ranks = new Dictionary<int, int> { [1] = 10, [2] = 20, [18] = 180 };
var max = RankProjection.MaxRank(ranks);
Check(max == 18, "maximum key");
Check(RankProjection.Project(0, -1, ranks, max) == -1, "unloaded XP");
Check(RankProjection.Project(0, 99999, ranks, max) == 99999, "XP ceiling");
Check(RankProjection.Project(0, 100000, ranks, max) == 99999, "XP clamp");
Check(RankProjection.Project(1, 0, ranks, max) == 10, "zero rank");
Check(RankProjection.Project(1, -1, ranks, max) == 10, "unloaded rank");
Check(RankProjection.Project(1, 2, ranks, max) == 20, "rank mapping");
Check(RankProjection.Project(1, 100, ranks, max) == 180, "upper rank clamp");
Check(RankProjection.Project(1, 1, new Dictionary<int, int> { [1] = 120000 }, 1) == 99999, "installed rank value ceiling");
Check(((PlayerButtons.Scoreboard | PlayerButtons.Forward) & PlayerButtons.Scoreboard) != 0, "TAB while moving");
Check((PlayerButtons.Forward & PlayerButtons.Scoreboard) == 0, "movement without TAB");
Check(!RankProjection.TryProject(0, -1, ranks, max, out _), "unloaded XP is not sent");
Check(!RankProjection.TryProject(1, -1, ranks, max, out _), "unloaded rank is not sent");
Check(RankProjection.TryProject(0, 0, ranks, max, out var zero) && zero == 0, "loaded zero XP");
Check(RankProjection.TryProject(1, 0, ranks, max, out var first) && first == 10, "loaded zero rank retains mapping");

var connection = new PlayerIdentity(76561198000000001, 1, (nint)0x1000);
var desired = new AppliedRank(connection, new RankValues(111, 11, 12345));
var state = new RankReplication();
var fields = new FakeFields();
Check(state.Apply(0, desired, fields), "first apply");
Check(fields.Live == desired.Values && fields.Writes == 1, "full triple written");
Check(fields.Notified.SequenceEqual(new[] { RankFields.Wins, RankFields.Type, RankFields.Value }), "all fields notified initially");
Check(state.LastNotified(0) == desired, "successful notify cached");
for (var tick = 0; tick < 640; tick++) Check(!state.Apply(0, desired, fields), "unchanged tick skipped");
Check(fields.Writes == 1 && state.Notifications == 3, "no per-tick writes or notifications");

// Exercise all combinations of engine drift, not only the XP field.
for (var mask = 1; mask < 8; mask++)
{
    fields.Live = new RankValues((mask & 1) != 0 ? 0 : 111, (sbyte)((mask & 2) != 0 ? 0 : 11), (mask & 4) != 0 ? 0 : 12345);
    fields.Notified.Clear();
    Check(state.Apply(0, desired, fields), "engine drift repaired");
    Check(fields.Live == desired.Values, "drift restored full triple");
    Check(fields.Notified.Aggregate(RankFields.None, (a, b) => a | b) == (RankFields)mask, "only drifted fields notified");
}
Check(state.DriftRepairs == 7, "drift counter");

var updated = desired with { Values = desired.Values with { Value = 12346 } };
fields.Notified.Clear();
Check(state.Apply(0, updated, fields), "XP change with TAB held");
Check(fields.Notified.SequenceEqual(new[] { RankFields.Value }), "XP change notifies rank only");
// Another writer can already have changed live memory. The last-notified state
// still differs, so a matching read must not suppress the network notification.
fields.Live = desired.Values;
fields.Notified.Clear();
Check(state.Apply(0, desired, fields) && fields.Notified.SequenceEqual(new[] { RankFields.Value }), "desired change requires notify even if live matches");

foreach (var identity in new[]
{
    connection with { SteamId = connection.SteamId + 1 },
    connection with { UserId = 2 },
    connection with { Controller = (nint)0x2000 }
})
{
    fields.Notified.Clear();
    Check(state.Apply(0, desired with { Identity = identity }, fields), "slot reuse or new controller");
    Check(fields.Notified.Count == 3, "new identity notifies identical values");
}
state.Remove(0);
fields.Notified.Clear();
Check(state.Apply(0, desired, fields) && fields.Notified.Count == 3, "disconnect clears notification state");
state.Clear();
Check(state.LastNotified(0) == null && state.Apply(0, desired, fields), "map/config reset clears notification state");
fields.Notified.Clear();
Check(state.Apply(0, desired, fields, force: true) && fields.Notified.Count == 3, "spawn forces full notify");

// Distinct slots/identities must never share a rank or notification cache.
var other = new AppliedRank(connection with { SteamId = connection.SteamId + 10, UserId = 10 }, new RankValues(111, 12, 18));
var otherFields = new FakeFields();
Check(state.Apply(1, other, otherFields), "second player initialized");
Check(fields.Live == desired.Values && otherFields.Live == other.Values, "player values remain separate");
Check(state.LastNotified(0) == desired && state.LastNotified(1) == other, "per-slot cache isolation");

foreach (var failure in new[] { RankFields.Wins, RankFields.Type, RankFields.Value })
{
    var failedState = new RankReplication();
    var failingFields = new FakeFields { FailOn = failure };
    var threw = false;
    try { failedState.Apply(0, desired, failingFields); }
    catch (InvalidOperationException) { threw = true; }
    Check(threw && failedState.Failures == 1, "notification failure reported");
    Check(failedState.LastNotified(0) == null, "partial notification never cached as success");
    failingFields.FailOn = RankFields.None;
    failingFields.Notified.Clear();
    Check(failedState.Apply(0, desired, failingFields), "failed notification retried despite matching live memory");
    Check(failingFields.Notified.Count == 3, "retry notifies full triple");
}
Console.WriteLine("FakeRank projection, 640 steady ticks, drift combinations, lifecycle, multi-player isolation and failure-retry checks passed");

static void Check(bool success, string name)
{
    if (!success) throw new Exception(name);
}

sealed class FakeFields : ICompetitiveRankFields
{
    public RankValues Live;
    public int Writes;
    public RankFields FailOn;
    public readonly List<RankFields> Notified = new();
    public RankValues Read() => Live;
    public void Write(RankValues values) { Live = values; Writes++; }
    public void Notify(RankFields field)
    {
        if (field == FailOn) throw new InvalidOperationException("simulated notify failure");
        Notified.Add(field);
    }
}
