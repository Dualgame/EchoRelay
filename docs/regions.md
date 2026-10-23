# Server regions

- **Game server side.** A game server registers `CSymbol64(server_region)`. That comes from its `_local/config.json`
  key `server_region` or `-serverregion <code>` on its command line, else `"default"`.
- **Client side.** A client create carries `CSymbol64(-region <code>)` in `LobbyCreateSessionRequestv9.Unk0`.

**The fix on this branch.** Core used to read the region 8 bytes early. The game server's
`ERLobbyRegistrationRequest` is naturally aligned, so `regionId` is at +24. The old read put the region into
`VersionLock`.

**Matching** prefers exact region matches, then `default` servers, then any (`MatchingService.PreferRegion`;
replace the policy through `MatchingService.RegionFilter`).

**Codes.** `EchoRelay.Headless/Regions.cs` maps the wizard's labels to codes. The codes are the shipped
`clanforge_<code>` datacenter literals:

| label | code |
|---|---|
| US WEST | `usw` |
| US CENTRAL SOUTH | `usc` |
| US CENTRAL NORTH | `uscn` |
| US EAST | `use` |
| EU WEST | `euw` |
| EU CENTRAL | `euc` |
| ASIA SOUTH | `sin` |
| JAPAN | `jp` (no shipped code; ours) |
| AUSTRALIA | `aus` |
| LATIN AMERICA | `lat` |
