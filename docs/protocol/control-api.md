# Headless host control API

A small HTTP API on `EchoRelay.Headless` for test tooling: list servers and peers, start and steer sessions, push
messages, and inspect invites and early-quit state. Tools outside this repository should pin this page (and a tag
of this repository), not the source file. The route list in `EchoRelay.Headless/Control.cs` is the implementation
and must agree with this page.

- **Off by default.** `--control-port N` turns it on.
- **Localhost only:** it binds `http://127.0.0.1:N/`. It has no authentication. Never expose it.
- JSON in and out. Errors are `{"error": "..."}` with status 400, 404 or 500.
- `{id}` is a server id: decimal, `0x` hex, or a session GUID.
- Every action writes a `[CONTROL] ...` line to the host log.

## Servers and peers

| route | body → answer |
|---|---|
| `GET /health` | `{"ok":true,"port":<services port>,"servers":n}` |
| `GET /servers`, `GET /servers/{id}` | per server: `id` (decimal, as in the host log), `id_hex` (the game log's "registered as"), `peer`, internal / external address and port, `region`, `version_lock`, `session_id` (lower case, with dashes, the `/join_session` spelling), `level` / `gametype` as `{sym, name}`, `lobby_type`, `started`, `locked`, `player_limit`, `players` `[{session, peer, user_id, name}]` |
| `GET /peers` | connected peers per service: `[{peer, user_id, name, path}]` |

## Sessions

| route | body → answer |
|---|---|
| `POST /servers/{id}/start-session` | `{"level": NAME\|0xHEX, "gametype": "social_2.0", "lobby_type": "public"\|"private", "settings": {SessionSettings}, "player_limit": 16}` → `{"session_id": ...}` |
| `POST /servers/{id}/kick` | `{"player_session": GUID\|"all"}` → `{"kicked": [...]}` (sends `ERGameServerPlayersRejected`) |
| `POST /servers/{id}/accept` | `{"player_sessions": [GUID, ...]}` (sends `ERGameServerPlayersAccepted`) |
| `POST /servers/{id}/lock` | `{"locked": true\|false}`: registry state only; matching skips locked sessions |

## Messages

| route | body → effect |
|---|---|
| `POST /lobby-settings` | `{"json": {...}, "target": "clients"\|"servers"\|"all"\|"peer", "peer": "ADDR:PORT"}`: sends `SNSLobbySettingsResponse` now. `clients` = login and matching peers. See [lobby-settings.md](lobby-settings.md) |
| `POST /push` | `{"sym": NAME\|0xHEX, "hex": "..." \| "json": {...}, "compression": "none"\|"zlib"\|"zstd", "null_terminated": true, "prefix_hex": "", "target", "peer"}`: sends any message now. The payload is `prefix_hex` + (`hex`, or the JSON framed as Core's StreamJSON). `target` / `peer` as for `/lobby-settings` |

## Links and invites

| route | body → answer |
|---|---|
| `GET /links` | `[{link, server, session_id, live}]` |
| `POST /links` | `{"server": {id}, "link": GUID}` (link optional, new if absent) → `{"link": ...}`. A fixed join id: the link GUID becomes an extra key of the registry's session lookup, so `LobbyJoinSessionRequestv7(lobby=link)` (for example a client started with `-lobbyid <link>`) matches that server's current session, across session restarts |
| `POST /links/{link}/remove` | drops the alias |
| `GET /invites` | `[{code, link, server, creator, gametype, level, expires_at, live, ...}]`: the invite codes of [invites.md](invites.md), mirrored from the services-port routes |
| `POST /invites` | operator create (no login check, no per-user cap): same body and answer as the services route; `creator` = the body's `user_id`, or `"control"` |
| `POST /invites/{code}/revoke` | operator revoke |

## Early quit (host `--early-quit true`)

| route | body → answer |
|---|---|
| `GET /early-quit/stats` | accounts per state, locked, active (an entry in 30 days) and shares, 30-day entry classes and would-flags, pending removals |
| `GET /early-quit/{user}` | the v2 state: history (last 50), snapshot, lockout, exempt, pending |
| `POST /early-quit/{user}/{action}` | `action` = `quit` \| `complete` (synthetic rows) \| `clear` \| `expire` \| `lockout {"seconds": N}` \| `forgive {"session"}` \| `exempt {"on": bool}`. Updates the store and the profile mirror, and pushes an update to the user's login peers. See [early-quit.md](early-quit.md) |

## Accounts

| route | body → answer |
|---|---|
| `GET /accounts/{user}/developer` | the server profile's developer block, JSON key `dev` (`null` = none) |
| `POST /accounts/{user}/developer` | `{"disable_afk_timeout": true\|false}`: sets `profile.server.dev {disable_afk_timeout, xplatformid = user}`; `false` removes the block. The game server reads it at the next join, so a `-noovr` client (no headset heartbeat) is not kicked for inactivity |
