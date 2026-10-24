# Continuing the work

Open items, known limits and where to start for the host side of this fork: the headless host, the invite service,
early quit, matching and the game-server plugin. Client-side items (the mod loader, the client plugins, the Quest APK
builder, the menu cards) live with the client repository. This is a research / educational project for LAN use; it does
not host public matches.

Every item has an **entry point** (a file and function in this repository) and an **acceptance test**. Measurements
name the game build they ran on: PC `6323983201049540`, Quest `6528386917200980` (version code 4987566).

## Known limits

- **Invite auth is address-bound, not token-bound.** A create or revoke must come from the address of a live login for
  that `user_id`. Two players behind one NAT address, or every caller behind a reverse proxy, are not told apart
  ([docs/protocol/invites.md](docs/protocol/invites.md)). Enough for a LAN or a NAT'd home host, not for the internet.
- **Early quit runs in shadow mode by default:** it detects and records quits and refuses nothing. Enforce mode is not
  supported until the post-match timing is measured (item 2).
- **Servers per host.** On PC `6323983201049540` (12 logical CPUs), one dedicated server in a match with one client
  uses about 58 % of one core at the default `-timestep 120`, and scales near-linearly with the tick rate (about 33 %
  at 60, 79 % at 180). A loaded match with no client costs 25–37 %. Each server holds about 1.45 GB of private memory.
- **Start game servers one at a time.** Four or more servers initialising at once fail at the broadcaster port
  (`Broadcaster port (6794) initialization failed`); started one by one, each waiting until it has registered, 4 and 8
  register. The cause is unknown.
- **`-timestep` is the server tick rate**, set by `EchoRelay.Patch` (see its README), not a stock game option. Gameplay
  at a lower rate has not been checked.
- **`-headless` holds:** no graphics or audio module loads; leaving it off costs about 2.4 GB per server.

## Open items

| # | item | entry point | acceptance |
|---|---|---|---|
| 1 | **Token-bound invite auth.** Required before any internet-facing host. | `EchoRelay.Headless/Invites.cs`: `LoggedInUser`, `Create`, `Revoke` and the `Resolve` password path; a per-login token issued at login and sent by the client plugin. | Two clients behind one address cannot create or revoke for each other; the normal create / join / revoke flows still pass. |
| 2 | **Early quit: enforce mode and the post-match timing.** Private, social and AI sessions are exempt from detection by design, so the timing must be measured in a **public** arena. In a private match the post-match phase keeps the session live while any player stays; whether a winner-screen leave in a public match reads as a quit is open. | `EchoRelay.Headless/EarlyQuit.cs`: `OnSessionChanged`, `DropSession`, `GateCore`; `EarlyQuitPolicy.cs`: `grace` (60 s) and `end_burst` (15 s). | Measure the public post-match phase in arena and combat over many matches, tune the two values, then show that completed matches are never counted and a real quit is. In enforce mode a real quit locks matchmaking at the policy's threshold. Update [docs/protocol/early-quit.md](docs/protocol/early-quit.md) and drop the "not supported" line above. |
| 3 | **Early quit: remaining device tests.** A counted quit while warned, on Quest; exit signatures (login close vs removal); a server crash during the grace; everyone leaves; arrival-pulse robustness; the penalty timestamp across a relogin. | `EarlyQuit.cs`: `OnPlayerRemoved`, `OnServerUnregistered`, `Loaded`. `--early-quit-selftest` covers only the policy. | Each case has a PASS / FAIL with host and client logs, on PC, and on Quest where it applies. |
| 4 | **SNSFriend\* and SNSParty\* messages.** 18 friend and 40 party messages exist in the final client and none is implemented. They are never initialised on late clients, and the client's party is the platform's social object. | New message classes under `EchoRelay.Core/Server/Messages` and services under `EchoRelay.Core/Server/Services`. Start by capturing what a client with a platform social object sends. | A client sends a friend-list request and gets an answer a panel can show. Each message's layout documented. |
| 5 | **Game-server waves 2–6.** *Wave 2*: a game server reconnects and re-registers after a host restart. *Wave 3*, matching integrity: reserve idle servers so two simultaneous creates cannot collide, pending player slots time out, re-match if the session changed after the filter ran. *Wave 4*, operability: a terminate command, a start acknowledgement, sends outside the lock. *Wave 5*, parity: the start-session player array, team and owner-data sync, a version-lock check. *Wave 6*, on demand: half-open probe, auth plus a reverse proxy, A2S. | Core: `RegisteredGameServer.cs`, `GameServerRegistry.cs`, `MatchingService.cs`. Plugin: `EchoRelay.GameServer/gameserver.cpp` (`RequestRegistration`, `OnTcpMessageStartSession`, `OnTcpMsgPlayersAccepted`, `EndSession`). | Wave 2: kill and restart the host; the server re-registers by itself. Wave 3: two simultaneous creates for different levels each land on their own level (or the second fails cleanly); a client killed before connecting frees its slot within the timeout plus a few seconds. Wave 4: terminate stops a server in under 5 s. Each wave leaves the earlier flows passing. |
| 6 | **Server density.** | A per-process snapshot (CPU, memory, threads, loaded modules) with N servers started one at a time. | A servers-per-host figure at a stated build with two clients per server (the per-player slope); gameplay checked at 60–90 Hz before any `-timestep` default changes; the cause of the concurrent-initialisation limit. |

## How to prove a change

- `dotnet test EchoRelay.Core.Test` and `EchoRelay.Headless --early-quit-selftest` before touching the host; both CI
  jobs in `.github/workflows/build.yml` must pass.
- Run a create / join / revoke pass against a local host and keep the logs private: host logs can carry invite codes
  and passwords.
- State the build in every result. A PC result and a Quest result are different claims.
