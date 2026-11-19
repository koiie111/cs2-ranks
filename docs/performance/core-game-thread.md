# Ranks Core game-thread performance

Reference revision: `3b6bea2`. The November 18 server #5 capture predates that revision. It is useful for identifying candidates, but is not the before side of an engine-level A/B.

The rank calculation benchmark uses the original algorithm and the new table with identical inputs, 25 configured ranks and 1,000 simulated ticks. It exercises the rank calculation only, outside CS2.

| Inputs per tick | Legacy time → new time | Legacy allocations → new allocations |
| ---: | ---: | ---: |
| 0 | <0.01 ms → <0.01 ms | 0 → 0 B |
| 1 | 9.16 ms → 0.018 ms | 23,104,552 → 0 B |
| 24 | 235.94 ms → 0.445 ms | 548,829,584 → 0 B |
| 48 | 453.10 ms → 0.861 ms | 1,094,081,472 → 0 B |

CounterStrikeSharp work removed by inspection: rule-proxy enumeration per combat update, repeated player enumeration in the same tick, rank sorting per update, and Tag notifications when the clan already matches. Core persistence runs one background drain per active SteamID and coalesces adjacent pending saves. Load and reset delimit coalescing. Unload publishes outstanding drain tasks so the next plugin instance can wait for the corresponding player off the game thread. ExStats Hits uses the same ordering with a separate handoff.

```sh
dotnet build Ranks/Ranks.sln -c Release
dotnet run -c Release --project Ranks/Ranks/tests/CoreChecks.csproj
dotnet run -c Release --project Ranks/Modules/Ranks_FakeRank/tests/FakeRankChecks.csproj
```

The optional CoreChecks argument `--database` requires an isolated MariaDB instance on loopback port 43316 with a disposable `ranks_perf_check` database and passwordless local test root. The check also holds one player's row lock while a replacement queue requests their stats, verifies a different SteamID can load, and checks the final XP and combined playtime after releasing the lock. Queue handoff is tested with replacement instances outside the engine. The server #34 A/B, including its timer numbers, applies to the original tested PR artifact `404237a`.

A playtime write whose submission outcome is uncertain is not replayed. A failure before SQL submission is carried into the next write for that row in the same plugin lifetime. Existing `shoots` and `hits` values are retained. New fire events increment `shoots` and accepted damage events increment `hits`, so historical totals may contain both conventions.

## Persistence and handler review

Round-end awards share the player list used for the population check. The no-message combat path creates no XP-message closure. Per-player ordering is retained, with up to four concurrent snapshot writes per module instance. Login reads use separate lanes from other players' writes. Under normal load the five-second timer still submits about 4.8 saves/s with 24 loaded players and 9.6 saves/s with 48. The limit controls bursts and the index reduces work per save.

New Core tables have a unique SteamID key and an XP index. Returning players require one SELECT. Concurrent first joins use the unique key and re-read the winning row. Existing installations can apply [the SQL migration](../../php/migrations/sql/20261119_ranks_indexes_and_hit_totals.sql) to their Ranks database. Set the first variable to the configured core table name. Existing duplicate SteamIDs cause unique index creation to fail and require operator review. The script also adds a nullable total to existing Hits tables. Old hit rows use the sum of recorded body parts. Generic and unknown groups from earlier sessions cannot be reconstructed. After migration the next saved total includes these groups going forward.

Round-trip checks cover fresh, legacy and migrated Hits schemas, 24 simultaneous authorizations for one new SteamID, an unavailable write slot, and 48 pending disconnect saves. Core tracks whole elapsed seconds from the beginning of each session. The first acknowledged save after a reset replaces playtime with elapsed time since the reset and confirms it to the player. A subsequent save can safely repeat the absolute reset after an uncertain outcome. Configuration changes to the database or table require reloading the plugins so active sessions finish against their original destination.

In a local MariaDB 11.8 comparison, two disposable 20,000-row tables had identical SteamID, XP and playtime data. Each sample issued 48 sequential autocommit updates to different SteamIDs, incrementing XP and playtime over one loopback connection. After one warm-up, 11 samples per schema with alternating run order gave medians of **167.99 ms without indexes and 25.18 ms with both indexes**. The unindexed lookup examined all 20,000 rows. EXPLAIN for the indexed update estimated one row. This measures SQL on synthetic data. The one-client game-thread measurements in PR #4 apply to commit `404237a`; full-server frame time and CPU on the current head still require an engine profile.

Deploy Core, Hits and GeoIP with the MySqlConnector 2.6.1 dependency and corresponding publish metadata from this revision. The historical release archives predate this build. Pending work lives in memory across in-process plugin reloads. A process kill or a database outage that lasts through the final disconnect can lose unsaved stats.
