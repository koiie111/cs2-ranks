using Ranks_FakeRank;
using CounterStrikeSharp.API;
using System.Runtime.InteropServices;

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
Check(RankProjection.TryProject(0, 100000, ranks, max, out var xp) && xp == 99999, "loaded XP clamp");
Check(RankProjection.TryProject(1, 90, ranks, max, out var icon) && icon == 180, "custom rank upper clamp");
Check(RankProjection.TryProject(1, 1, new Dictionary<int, int> { [1] = 120000 }, 1, out var capped) && capped == 99999, "custom value clamp");
Check(!RankProjection.TryProject(1, 3, ranks, max, out _), "missing rank mapping withheld");
Check(!RankProjection.TryProject(1, 1, new Dictionary<int, int>(), 1, out _), "empty rank mapping withheld");

// Offsets come from the server schema. Exercise discontiguous fields and their exact widths.
new RankFieldLayout(4, 8, 9).Validate(13); // Adjacent fields, last byte at the class boundary.
foreach (var invalid in new[] {
    new RankFieldLayout(0, 8, 12), new RankFieldLayout(4, 16, 12),
    new RankFieldLayout(4, 8, 13), new RankFieldLayout(4, 8, 3),
    new RankFieldLayout(4, 6, 12), new RankFieldLayout(4, 15, 12) })
{
    var rejected = false;
    try { invalid.Validate(16); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, $"invalid schema {invalid}");
}
var address = Marshal.AllocHGlobal(32);
try
{
    Marshal.WriteInt32(address, 20, 111);
    Marshal.WriteInt32(address, 4, 0x010203fe);
    Marshal.WriteInt32(address, 12, 99999);
    Check(new RankFieldLayout(20, 4, 12).Read(address) == new RankValues(111, -2, 99999), "native field read");
}
finally { Marshal.FreeHGlobal(address); }

var connection = new PlayerIdentity(76561198000000001, 1, 0x1001);
var desired = new AppliedRank(connection, new RankValues(111, 11, 12345));
var participant = new RanksFakeRank.Participant();
Check(participant.ShouldRefresh(false), "first tick can initialize a new connection");
Check(participant.ShouldRefresh(true), "full poll probes a pending connection");
for (var tick = 0; tick < 64; tick++)
    Check(!participant.ShouldRefresh(false), "unavailable data waits for the next poll");
Check(participant.ShouldRefresh(true), "timer or TAB retries an unavailable rank");
participant.Desired = desired;
Check(participant.ShouldRefresh(false), "known rank is checked even with TAB closed");
participant.OnAuthorized();
Check(participant.Desired == null && participant.ShouldRefresh(false), "authorization reopens immediate discovery");
var state = new RankReplication();
AppliedRank? notified = null;
var fields = new FakeFields();
Check(state.Apply(ref notified, desired, fields), "first apply");
Check(fields.Live == desired.Values && fields.Writes == 1, "full triple written");
Check(fields.Notified.SequenceEqual(new[] { RankFields.Wins, RankFields.Type, RankFields.Value }), "all fields notified initially");
Check(notified == desired, "successful notify cached");
for (var tick = 0; tick < 640; tick++) Check(!state.Apply(ref notified, desired, fields), "unchanged tick skipped");
Check(fields.Writes == 1 && state.Notifications == 3, "no per-tick writes or notifications");

// Exercise all combinations of engine drift, not only the XP field.
for (var mask = 1; mask < 8; mask++)
{
    fields.Live = new RankValues((mask & 1) != 0 ? 0 : 111, (sbyte)((mask & 2) != 0 ? 0 : 11), (mask & 4) != 0 ? 0 : 12345);
    fields.Notified.Clear();
    Check(state.Apply(ref notified, desired, fields), "engine drift repaired");
    Check(fields.Live == desired.Values, "drift restored full triple");
    Check(fields.LastWritten == (RankFields)mask, "only drifted fields written");
    Check(fields.Notified.Aggregate(RankFields.None, (a, b) => a | b) == (RankFields)mask, "only drifted fields notified");
}
Check(state.DriftRepairs == 7, "drift counter");

var updated = desired with { Values = desired.Values with { Value = 12346 } };
fields.Notified.Clear();
Check(state.Apply(ref notified, updated, fields), "XP change with TAB held");
Check(fields.Notified.SequenceEqual(new[] { RankFields.Value }), "XP change notifies rank only");
// Another writer can already have changed live memory. The last-notified state
// still differs, so a matching read must not suppress the network notification.
fields.Live = desired.Values;
fields.Notified.Clear();
var writesBeforeNotify = fields.Writes;
Check(state.Apply(ref notified, desired, fields) && fields.Notified.SequenceEqual(new[] { RankFields.Value }), "desired change requires notify even if live matches");
Check(fields.Writes == writesBeforeNotify, "matching live fields are not rewritten");

foreach (var identity in new[]
{
    connection with { SteamId = connection.SteamId + 1 },
    connection with { UserId = 2 },
    connection with { Controller = connection.Controller + (1u << 15) }
})
{
    fields.Notified.Clear();
    Check(state.Apply(ref notified, desired with { Identity = identity }, fields), "slot reuse or new controller");
    Check(fields.Notified.Count == 3, "new identity notifies identical values");
}
notified = null;
fields.Notified.Clear();
Check(state.Apply(ref notified, desired, fields) && fields.Notified.Count == 3, "fresh session notifies even when all fields match");
fields.Notified.Clear();
Check(state.Apply(ref notified, desired, fields, force: true) && fields.Notified.Count == 3, "spawn forces full notify");

// Distinct slots/identities must never share a rank or notification cache.
var other = new AppliedRank(connection with { SteamId = connection.SteamId + 10, UserId = 10 }, new RankValues(111, 12, 18));
var otherFields = new FakeFields();
AppliedRank? otherNotified = null;
Check(state.Apply(ref otherNotified, other, otherFields), "second player initialized");
Check(fields.Live == desired.Values && otherFields.Live == other.Values, "player values remain separate");
Check(notified == desired && otherNotified == other, "per-slot cache isolation");

foreach (var failure in new[] { RankFields.Wins, RankFields.Type, RankFields.Value })
{
    var failedState = new RankReplication();
    AppliedRank? failedNotification = null;
    var failingFields = new FakeFields { FailOn = failure };
    var threw = false;
    try { failedState.Apply(ref failedNotification, desired, failingFields); }
    catch (InvalidOperationException) { threw = true; }
    Check(threw && failedState.Failures == 1, "notification failure reported");
    Check(failedNotification == null, "partial notification never cached as success");
    failingFields.FailOn = RankFields.None;
    failingFields.Notified.Clear();
    Check(failedState.Apply(ref failedNotification, desired, failingFields), "failed notification retried despite matching live memory");
    Check(failingFields.Notified.Count == 3, "retry notifies full triple");

    foreach (var force in new[] { false, true })
    {
        if (!force) failingFields.Live = default;
        failingFields.FailOn = failure;
        try { failedState.Apply(ref failedNotification, desired, failingFields, force); }
        catch (InvalidOperationException) { }
        Check(failedNotification == null && failingFields.Live == desired.Values, "established notification invalidated");
        failingFields.FailOn = RankFields.None;
        failingFields.Notified.Clear();
        Check(failedState.Apply(ref failedNotification, desired, failingFields) && failingFields.Notified.Count == 3, "failed drift/spawn retried on next normal tick");
    }
}
fields.Live = default;
fields.FailAfterWins = true;
try { state.Apply(ref notified, desired, fields); }
catch (InvalidOperationException) { }
Check(notified == null && fields.Live == new RankValues(111, 0, 0), "partial field write invalidates success");
fields.FailAfterWins = false;
fields.Notified.Clear();
Check(state.Apply(ref notified, desired, fields) && fields.Live == desired.Values, "remaining fields repaired on retry");
Check(fields.LastWritten == (RankFields.Type | RankFields.Value) && fields.Notified.Count == 3, "partial write retries full notification");
Console.WriteLine("FakeRank schema, projection, polling, steady ticks, drift, lifecycle and replication retry checks passed");

static void Check(bool success, string name)
{
    if (!success) throw new Exception(name);
}

sealed class FakeFields : ICompetitiveRankFields
{
    public RankValues Live;
    public int Writes;
    public RankFields LastWritten;
    public bool FailAfterWins;
    public RankFields FailOn;
    public readonly List<RankFields> Notified = new();
    public RankValues Read() => Live;
    public void Write(RankValues values, RankFields dirty)
    {
        LastWritten = dirty;
        if ((dirty & RankFields.Wins) != 0) Live = Live with { Wins = values.Wins };
        if (FailAfterWins) throw new InvalidOperationException("simulated partial write");
        if ((dirty & RankFields.Type) != 0) Live = Live with { Type = values.Type };
        if ((dirty & RankFields.Value) != 0) Live = Live with { Value = values.Value };
        Writes++;
    }
    public void Notify(RankFields field)
    {
        if (field == FailOn) throw new InvalidOperationException("simulated notify failure");
        Notified.Add(field);
    }
}
