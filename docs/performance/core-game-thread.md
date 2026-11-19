# Ranks Core game-thread performance

Reference revision: `3b6bea2`. The November 18 server #5 capture predates that revision. It is useful for identifying candidates, but is not the before side of an engine-level A/B.

The rank calculation benchmark uses the original algorithm and the new table with identical inputs, 25 configured ranks and 1,000 simulated ticks. It exercises the rank calculation only, outside CS2.

| Inputs per tick | Legacy time → new time | Legacy allocations → new allocations |
| ---: | ---: | ---: |
| 0 | <0.01 ms → <0.01 ms | 0 → 0 B |
| 1 | 9.16 ms → 0.018 ms | 23,104,552 → 0 B |
| 24 | 235.94 ms → 0.445 ms | 548,829,584 → 0 B |
| 48 | 453.10 ms → 0.861 ms | 1,094,081,472 → 0 B |

CounterStrikeSharp work removed by inspection: rule-proxy enumeration per combat update, repeated player enumeration in the same tick, rank sorting per update, periodic per-player task dispatch, and Tag notifications when the clan already matches. Timing for full callbacks and timer bursts requires a CS2 capture.

```sh
dotnet build Ranks/Ranks.sln -c Release
dotnet run -c Release --project Ranks/Ranks/tests/CoreChecks.csproj
dotnet run -c Release --project Ranks/Modules/Ranks_FakeRank/tests/FakeRankChecks.csproj
```

The optional CoreChecks argument `--database` requires an isolated MariaDB instance on loopback port 43316 with a disposable `ranks_perf_check` database and passwordless local test root. The check uses a disposable table, delays a save with a write lock, checks consecutive playtime increments, resets the row, and reads it back after another save. It exercises SQL, not the engine callbacks.

A playtime write whose submission outcome is uncertain is not replayed. A failure before SQL submission is carried into the next write for that row in the same plugin lifetime. Existing `shoots` and `hits` values are retained. New fire events increment `shoots` and accepted damage events increment `hits`, so historical totals may contain both conventions.
