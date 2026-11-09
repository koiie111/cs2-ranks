# FakeRank replication correction

Branch: `codex/fix-fakerank-replication`, baseline `d1d99ee1033fbd60bbb022ba00f64227ab3b4bf4`.
Module: `v1.0.3-replication.1`.

## Problem and change

The pre-O2 and O2 implementations write `m_iCompetitiveWins`,
`m_iCompetitiveRankType` and `m_iCompetitiveRanking` through schema references
without marking them changed for network transmission. Sending
`ServerRankRevealAll` does not perform that notification. The owner's recording
on AWP #2 shows rapidly changing values and values shared by multiple rows.

The correction writes the complete rank triple before calling
`Utilities.SetStateChanged` for changed fields. It tracks the last successfully
notified triple separately from desired values and also reads the controller:
live engine resets must be repaired even when desired XP/rank did not change.
The notification cache belongs to a slot's SteamID, UserID and controller handle.
Disconnect, map start, config reload and unload invalidate it; spawn requests a
full refresh. Failed notification attempts are not cached as successful.

Unavailable player data is withheld. Rank/XP projection, wins=111, both display
modes, API, JSON configuration and databases are otherwise retained. The initial
candidate retains O2's reveal cadence to isolate replication behavior. Manually
allocated reveal messages are disposed after sending.

## Checks

```
dotnet run --project Ranks/Modules/Ranks_FakeRank/tests/FakeRankChecks.csproj -c Release
dotnet build Ranks/Modules/Ranks_FakeRank/Ranks_FakeRank.csproj -c Release
git diff --check
```

Regression coverage includes both projections and the 99999 ceiling, unloaded
data, 640 unchanged ticks, all seven combinations of controller field drift,
XP changes while TAB is held, live values already matching a newly desired
value, slot/connection/controller replacement, disconnect, map/config reset,
spawn refresh, different players and retry after failure in each notification.
These tests exercise the notification decision with an in-memory field adapter;
they do not emulate CS2's network serializer or prove a client's rendering.

## Pilot and remaining acceptance

Only AWP #8 (`109.195.19.84:27085`) received the candidate. Two live RCON checks
confirmed zero humans/zero bots before the scoped LinuxGSM stop/start. Original
DLL and private evidence are outside autoload under
`/home/cs2_8/private/fakerank-replication-20261109/`. Unrelated plugin DLLs,
CSS JSON configs and Metamod VDFs matched their pre-restart hashes. Native
FakeRanks 1.1.6 and shared RanksApi were retained.

Candidate DLL SHA256:
`739a6d448c8859aa36751ef1261934591489a6d959218252c46d392282e5376d`.

`css_fakerank_status [slot]` is server-console/RCON only and reports API/config
readiness, schema networking flags, desired/live/notified triples, replication
counters and reveals. All three networking flags were true on the pilot.
The live pilot shows repeated engine resets of wins/rating to zero, successful
repairs and zero replication errors. A single real tester reported stable
display. This is evidence for the correction, not multi-player acceptance.

The `live` triple can already be zero again when a console command runs between
repairs; its value alone is not a rendering failure. `drift` counts repairs and
is not an error counter. `failures` counts failed reads/writes/notifications.

**Fleet release gate remains closed:** verify several real clients with distinct
ratings for at least five minutes, including TAB hold/reopen, movement, death,
spectating, reconnect, map change and XP updates. One successful load, bot rows,
or an in-memory unit test cannot replace that check. Compare against the
original behavior; if values still mix, retain the pilot evidence and continue
diagnosis rather than deploying fleet-wide.

Before acceptance, restarts are restricted to empty servers. After acceptance,
the owner authorized necessary restarts of occupied affected servers. Leave #16
offline. BHOP #12 has no managed Ranks_FakeRank or managed rank writer in the
inspected installation and must not receive this DLL.
