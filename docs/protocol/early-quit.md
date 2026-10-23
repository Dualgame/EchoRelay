# Early quit: the three service messages

Echo VR's final builds (PC `6323983201049540`, Quest `6528386917200980`) contain the client half of an early-quit
system: a notice, two INFO pages, a lockout timer, a main-menu card, social-lobby podium pages and scoreboard loyalty
ribbons. The services half was never public. `EchoRelay.Headless --early-quit true` implements it
(`EchoRelay.Headless/EarlyQuit*.cs`). Byte layouts below are from disassembly of those builds and were checked
against a running PC client of `6323983201049540`.

All three messages go to the client on the **login** socket and are receive-only: the client never sends early-quit
messages.

| message | CSymbol64 | payload |
|---|---|---|
| `SNSEarlyQuitConfig` | `0x080495a43a6b7251` | 1-byte struct + u32 LE length + zstd(JSON + NUL) |
| `SNSEarlyQuitFeatureFlags` | `0xd9a955895caccac3` | 1 byte |
| `SNSEarlyQuitUpdateNotification` | `0x1f81b54c35788eaa` | 56-byte struct |

## SNSEarlyQuitConfig

```
u8  0x00                     the 1-byte struct; the raw tail follows it
u32 LE decompressed length   must equal the real size of the JSON + NUL
zstd frame of (JSON + NUL)
```

The leading byte is load-bearing. Without it the client reads the length one byte late, asks its stack allocator for
hundreds of MiB and exits with `Stack allocator ran out of memory: N`. It is zstd, not the u64 + zlib framing of
`SNSLobbySettingsResponse`.

| table | row keys |
|---|---|
| `penalty_levels` | `penalty_level` u8, `min_earlyquits` u32, `max_earlyquits` u32, `mm_lockout_sec` u32, `spawnlock` u32 (PC only), `autoreport` u32, `cnvpe_reactivated` u32 |
| `steady_player_levels` | `steady_player_level` u8, `min_num_matches` u32, `min_steady_ratio` f32 |

Both tables must parse, or the client keeps its old ones.

## SNSEarlyQuitFeatureFlags

One byte, stored as `byte & 0xdd`: **a stock client clears bits 1 and 5**. Bit 5 gates the EARLY QUIT card, the quit
notice, the 16 podium pages, scoreboard ribbons and the post-match display; bit 1 gates the lockout timer. So on an
unmodified client only the INFO 2/2 page can show this data, and no stock menu route reaches it. Showing the rest
needs a client patch that keeps those bits; see the companion client-features repository.

## SNSEarlyQuitUpdateNotification (56 bytes)

| off | type | field | note |
|---|---|---|---|
| 0x00–0x17 | — | — | not read |
| 0x18 | i64 | `penaltyts` | must be greater than the stored value (initially -1), or the record is ignored |
| 0x20 | i64 | lockout expiry | ignored by stock clients |
| 0x28 | u32 | `numearlyquits` | |
| 0x2c | u32 | `numsteadymatches` | completed matches |
| 0x30 | u32 | `numsteadyearlyquits` | quit matches |
| 0x34 | u8 | `penaltylevel` | |
| 0x35 | u8 | `steadyplayerlevel` | loyalty tier index |

## What the client does not do

There is no client-side lockout. The client never writes a lockout expiry and its `lockoutcountdownactive` output is
fixed at 0. `mm_lockout_sec` gates no client code. A lockout is the services refusing matchmaking: here,
`MatchingService.MatchingGate` refuses public FIND / JOIN / CREATE with `ServerIsLocked`. Stock clients show their
own fixed text for that code, not the message string.

## Host behaviour (summary)

- **Start it** with `--early-quit true`. It is off without that flag.
- **Shadow mode is the default** (`--early-quit-mode shadow`). It detects quits, records them and logs what enforce would
  do. It refuses nothing and shows no notices.
- **Enforce** (`--early-quit-mode enforce`, opt-in) makes the lockout real.
- **Detection** feeds both modes and is on by default. `--early-quit-detect false` turns it off, so nothing is
  recorded.
- **Arrival pulse (enforce only):** login sends Flags `0x02`. Bit 5 (`0x22`) is held only while a player arrives in
  a social lobby with a notice owed, until 2 s (`pulse_hold_sec`) after the client's LOAD_STATS, capped at
  `arrival_max_sec` (120 s). The stock podium shows the notice only during that load.
- **Quits are counted** over the last 20 public arena / combat matches (30-day cap). Warnings start at 3 quits and a
  lockout at 5. Lockouts run 5 min → 15 min → 1 h → 6 h → 24 h. Social, private, AI and spectating are never gated.
- **Match completion is inferred** from the session end, not reported by the game server. Tune `grace` (default
  60 s) / `end_burst` (default 15 s) to your post-match timing before you use enforce.
- **Every value is policy:** pass `--early-quit-policy FILE.json`.
- **Self-test:** `--early-quit-selftest`.
