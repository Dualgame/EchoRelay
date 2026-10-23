# Invite codes (EchoRelay.Headless --invites true)

The final Echo VR builds ship a CREATE GAME / JOIN VIA CODE wizard (`ui_mnu_deeplinking_*`) that the retail services
never backed. The headless host serves short invite codes for it on the **services port** (LAN-reachable), through
`Server.ExtraApiHandler`. It is ACL-gated like every other services request. A client-side plugin
(companion client-features repository) connects the wizard to these routes.

| route | body / query | result |
|---|---|---|
| `POST /invites` | `{"user_id", "gametype", "level"?, "region"?, "password"?, "expires_s"?, "player_limit"?, "settings"?}` | `{"code","link","gametype","level","expires_at","password":bool}`; 403 not a logged-in peer, 429 per-user cap, 503 no idle game server |
| `GET /invites/{code}` | `?password=` when set | `{"code","link","gametype","level","expires_at"}`; 404 unknown, 403 wrong password, 410 expired / revoked |
| `POST /invites/{code}/revoke` | `{"user_id"}` | creator only (403 otherwise) |

- Codes are 5 characters with no `0 O 1 I`, so they can be read aloud and typed on the in-game keyboard.
- An invite **leases** one idle game server and starts a private session on it, plus a link alias, so a
  `LobbyJoinSessionRequestv7` with the link reaches it. When the session ends (empty-server timeout) it is started
  again until the invite expires or is revoked.
- Passwords are stored as SHA-256 of `code\npassword` and compared in fixed time. Over plain HTTP they still travel
  in clear, and the game's own create metric carries the password too. **Keep host logs private.**

## Known limits

- **No auth.** A request is accepted for any logged-in `user_id`, and the id is not bound to the caller's login
  session. Fine on a LAN; not for an internet-facing host.
- **Memory only.** Invites are lost on restart.
- **Revoke** waits for the empty-session timeout instead of ending the session at once.
