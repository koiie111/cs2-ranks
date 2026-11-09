# Stable FakeRank display

Branch: `codex/fix-fakerank-replication`. Module: `v1.0.3-replication.3`.

## Cause and correction

Both pre-O2 and O2 managed FakeRank implementations sent
`CCSUsrMsg_ServerRankRevealAll` every tick while any player held TAB. The fleet
also has native FakeRanks-RevealAll 1.1.6, which reveals on a detected TAB press.
Repeated managed disclosure caused client-side rank-number flicker and mixing
between rows, even when each controller's rank stayed constant.

Remove managed reveal messages. The native module remains the sole disclosure
owner; managed code assigns and synchronizes wins/type/rank. Mark changed fields
with `Utilities.SetStateChanged`, track last successful notification separately
from desired values, and repair observed live field drift. Cache identity
includes slot, SteamID, UserID and controller handle. Disconnect/map/config
reload invalidate caches; spawn forces notification. Failed notifications retry.
Unavailable player data is withheld. Wins=111, projections, modes, RanksApi,
JSON configuration and databases remain unchanged.

Candidate 3 also reconciles cached fields on every tick while TAB is closed.
The engine can clear wins/rating between 0.25-second API polls. Waiting for the
scoreboard button to reach the server permits a stale first local draw; cached
field recovery therefore must run before that input, without another reveal.
Authoritative API polling remains at 0.25 seconds. Unchanged live values still
produce no writes/notifications.

All inspected managed installations already have native FakeRanks-RevealAll.
This release depends on that division of responsibilities. The native binary
was reviewed and retained; it sends reveal rather than assigning rank values.
Native 1.1.6 polls buttons every twelfth tick, so short presses can be missed.

## Validation

```
dotnet run --project Ranks/Modules/Ranks_FakeRank/tests/FakeRankChecks.csproj -c Release
dotnet build Ranks/Modules/Ranks_FakeRank/Ranks_FakeRank.csproj -c Release
git diff --check
```

The regression suite covers both projections, the 99999 ceiling, unloaded data,
640 unchanged ticks, all seven combinations of live field drift, XP changes,
already matching live memory with an outdated notification cache, slot and
controller replacement, lifecycle reset, forced refresh, player isolation, and
retry after each notification failure. Build: zero errors/warnings.

Live AWP #8 test used one real client and three stationary bots with distinct
in-memory ranks (1234, 12345, 54321). No SteamID spoofing or synthetic player XP
writes were used. The notification-only candidate reproduced the original
flicker despite stable bot fields. Blocking every reveal hid ranks; allowing
one per second changed flicker to once per second. Removing the managed sender
retained native press-triggered reveal and stable display.

The owner confirmed stable values in their own rows, then correct isolated
updates of FR_B between 12345 and 22345 every two seconds while other rows stayed
fixed. Switching bots to competitive type with rank IDs 1, 9 and 18 also produced
three stable icons. All test bots retained wins=111: unique wins were unnecessary.
The main post-change probe ran for over 6 minutes, with zero replication failures.
The temporary probe is manually loaded from disabled and removed after testing.

These visual results cover candidate 2. The owner subsequently reproduced brief
row mixing on the initial TAB draw/new-player entry, which settles while held.
A single native reveal and a reversible unique-wins test did not fix that case.
Candidate 3 passed regression tests and maintains correct live values on #8
while TAB is closed. With two real players already connected, the owner then
confirmed smooth initial TAB display after connecting and repeated opening.
This is the accepted final candidate. Native twelve-tick input polling was not
changed. Unit tests do not emulate CS2 rendering; no full active-load or
multiple-observer performance claim is made.

## Artifact and operations

Candidate 2 frozen DLL SHA256:
`00e1247ff38b8fc13588c4659e2565597fcffb9a4cbc3ed21d0055bc0fd8ccc5`.

Candidate 3 pilot DLL SHA256:
`325456d84cc4933ad17f2c4d6304238d8c5854bf4baa7d94cb3e1e9918d92913`.

`css_fakerank_status [slot]` is server-console/RCON only. It reports readiness,
networked schema flags, desired/live/notified values, failures/repairs and
`reveal=external-metamod`. A zero live value between repairs is not by itself a
client rendering failure; `drift` counts repairs and `failures` counts errors.

Pilot private originals and evidence:
`/home/cs2_8/private/fakerank-replication-20261109/`.
Fleet backups: `/root/fakerank-replication-20261109/backup/<number>/` on each host.
Install only the module DLL, retain native/other plugins/configs and player data,
and verify hash, loaded version, API/native readiness and fresh logs per instance.
VIPCore runtime cookie saves during shutdown are retained. Leave #16 offline;
BHOP #12 has no managed Ranks_FakeRank and must not receive this DLL.
