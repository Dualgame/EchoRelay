# Invite codes (EchoRelay.Headless --invites true)

The final Echo VR builds ship a CREATE GAME / JOIN VIA CODE wizard (`ui_mnu_deeplinking_*`) that the retail services
never backed. The headless host serves short invite codes for it on the **services port** (LAN-reachable), through
`Server.ExtraApiHandler`. It is ACL-gated like every other services request. A client-side plugin
(companion client-features repository) connects the wizard to these routes.

| route | body / query | result |
|---|---|---|
| `POST /invites` | `{"user_id", "gametype", "level"?, "region"?, "password"?, "expires_s"?, "player_limit"?, "settings"?}` | `{"code","link","gametype","level","expires_at","password":bool}`; 403 `user_id` not logged in, or not from the caller's address; 429 per-user cap, 503 no idle game server |
| `GET /invites/{code}` | `?password=` when set | `{"code","link","gametype","level","expires_at"}`; 404 unknown, 403 wrong password, 410 expired / revoked, 503 unleased and no idle game server to re-lease (retry) |
| `POST /invites/{code}/revoke` | `{"user_id"}` | `{"revoked","kicked":N}`; creator only (403 otherwise, nobody kicked), same caller check as create |

The loopback control API (`--control-port`) keeps its operator routes unchanged: `POST /invites` (no login check, no
cap), `POST /invites/{code}/revoke` (any invite; the POST needs a Content-Length, e.g. curl
`-d '{}'`, or HttpListener answers 411) and `GET /invites` (adds `leased` and `players`).

- Codes are 5 characters with no `0 O 1 I`, so they can be read aloud and typed on the in-game keyboard.
- An invite **leases** one idle game server and starts a private session on it, plus a link alias, so a
  `LobbyJoinSessionRequestv7` with the link reaches it. When the session ends (empty-server timeout) it is started
  again until the invite expires or is revoked.
- Passwords are stored as SHA-256 of `code\npassword` and compared in fixed time. Over plain HTTP they still travel
  in clear, and the game's own create metric carries the password too. **Keep host logs private.**

## Caller binding (create, revoke)

A services create or revoke names a `user_id`. The host accepts it only when that id has a live login session (a
peer on the login service right now) **and** the HTTP request comes from that login peer's remote address. The game's
own wizard calls from the machine that holds the login websocket, so it passes; another machine that only knows the
id gets 403 `not logged in from this address`. Loopback matches loopback (`127.0.0.1`, `::1`, `::ffff:127.0.0.1`), so a
local client and local tooling count as one host. Every refusal is logged as `[INVITES] refused <id> from <addr>`.

This binds an id to a host address, not to a person: two players behind one NAT address, or every caller behind a
reverse proxy (which presents its own address), are not told apart. That is enough for a LAN or a NAT'd home host;
an internet-facing host still wants a token from the login session.

## Persistence

Invites are written to `<storage-dir>/invites.json` (`--invite-store FILE` for another path, `--invite-store off` for
memory only) on every create, revoke and drop, atomically (temp file, fsync, rename). The file also keeps the codes
dropped in the last hour, so a revoked code still answers 410 after a restart and is not reissued. At start the
host loads it, drops invites that expired meanwhile, and moves an unreadable file aside to `invites.json.bad`.

**What a restored invite means.** The code, link, creator, password, settings and expiry survive; the **lease does
not**. A restart forgets every game server (they must register again), so a restored invite starts unleased. The
next `GET /invites/{code}` with the right password leases an idle server again: it starts the invite's private
session and re-adds the link alias, then answers 200 as before. With no idle server it answers 503 and the code stays
valid. So a launcher that resolves the code first (the normal path) works across a restart, while a link saved from
before the restart reaches nothing until someone resolves the code. Unleased invites still count toward the
per-user cap.

The same holds at run time: when an invite's game server unregisters (it crashed, or the host is shutting down), the
invite is unleased, not dropped, and the next resolve leases another idle server.

**The file is private.** It holds creator ids and the password hashes (SHA-256 of `code\npassword`, unsalted
beyond the code). Treat it like the rest of the storage directory: not world-readable, not committed, not shared.

## Revoke

A revoke (by the creator, or by the operator over the control API) drops the invite, removes its link and kicks the
private session's players at once (`ERGameServerPlayersRejected`, the control API's `kick`). The empty session then
ends on the game server and is not restarted. The answer says how many were kicked. An expired invite is not
kicked: its players finish, and the session is not restarted after it ends.

## Known limits

- **Auth is address-bound** (above), not token-bound.
- **Links of restored invites** need a resolve before they route again.
