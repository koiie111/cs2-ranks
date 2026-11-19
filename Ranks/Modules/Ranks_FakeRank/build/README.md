# A module for displaying ranks in a tab.

The archive in this directory contains the managed FakeRank module v1.0.4.1 for
CounterStrikeSharp API 1.0.329. It requires RanksApi and the existing native
FakeRanks-RevealAll module. The packaged DLL has SHA-256
`238ade473c8327dab4703966542218db5d6e9e23624defb0da9e259554491dbd`.

## Performance verification

The offline fixture used 48 slots, 24 human rows, one bot and one HLTV. Timing
used the pinned CounterStrikeSharp managed assembly on Windows/.NET 8 with an
in-memory entity table and stub native calls. Each figure is the median of 11
warmed batches with stable values. Event counts came from a separate callback
replay. Neither measurement includes a running CS2 engine or client.

| Measurement | main (`e38b096`) | Original PR (`8142532`) | v1.0.4.1 |
|---|---:|---:|---:|
| Stable FakeRank OnTick, µs/call | 33.336 | 0.609 | 0.592 |
| Managed allocations, bytes/steady tick | 55,713 | 960 | 960 |
| Slot lookups over 2,879 steady ticks | 138,192 | 0 | 0 |
| API reads for 24 TAB presses in one tick | 576 | 576 | 24 |
| API reads over 64 ticks + 4 timer polls, one unavailable row and 23 ready | 160 | 160 | 96 |
| API reads over 180 timer polls, all 24 ready | 4,320 | 4,320 | 4,320 |

Known ranks are checked every tick, including with TAB closed. Once a full poll
finds no data, a pending row retries on the 250 ms timer, a TAB refresh, spawn,
rank change or renewed authorization. The first TAB poll in a tick refreshes
all rows immediately. A new connection or authorization invalidates that poll.
The timer still catches direct XP changes made through the core API.

The baseline server sample recorded 152.421 ms inside FakeRank OnTick over 45 s
(2,879 calls). That is 0.053 ms/call and 0.34% of elapsed time on one thread.
A paired in-game profile at comparable occupancy and TAB activity is needed
for server CPU, timer cost and tick-tail latency. First-frame rendering after
joining and respawning also needs a real client on this build.

## Config

```json
{
  "Type": 0, // 0 - Premier ranks, 1 - Regular ranks from Match Making (1 - 18) or custom ranks (19, 20, ...).
  "FakeRank": {
    "1": 1,  // "Rank in LR": Rank in Tab
    "2": 2,
    "3": 3,
    "4": 4,
    "5": 5,
    "6": 6,
    "7": 7,
    "8": 8,
    "9": 9,
    "10": 10,
    "11": 11,
    "12": 12,
    "13": 13,
    "14": 14,
    "15": 15,
    "16": 16,
    "17": 17,
    "18": 18
  }
}
```

## Screenshots
Type 0:
![image](https://github.com/user-attachments/assets/012dfe78-15d0-495b-bb93-31ab46c4743d)

Type 1:
![image](https://github.com/user-attachments/assets/68158a07-33f8-4bad-ad7a-0c69ae44c889)

Type 1 custom rank:
![image](https://github.com/user-attachments/assets/8a987025-ceab-4920-b107-612559a3bc37)


