# EchoRelay.Headless

A console host for `EchoRelay.Core` with no WinForms. It runs EchoRelay.App's startup sequence (storage open, initial
deployment from the game folder, server start), logging lines in place of forms. Every feature beyond that is off by
default.

```
EchoRelay.Headless [--port 777] [--storage-dir DIR] [--game-dir DIR] [--serverdb-api-key KEY] [--redeploy]
  [--invites true] [--invite-max-per-user 3]                      invite codes, see invites.md
  [--early-quit true] [--early-quit-mode shadow|enforce]          early quit, see protocol/early-quit.md
    [--early-quit-policy FILE.json] [--early-quit-config FILE.json] [--early-quit-detect true]
    [--early-quit-grace N] [--early-quit-flags 0xNN]
  [--early-quit-selftest]                                         run the policy self-test and exit
  [--lobby-settings FILE.json]                                    send SNSLobbySettingsResponse to clients
  [--control-port N]                                              localhost-only JSON control API
  [--test-start-session-level NAME|0xHEX] [...]                   test-only session automation (see Program.cs)
```

## Control API

- **Off by default.** `--control-port N` turns it on.
- **Localhost only:** `http://127.0.0.1:N/`. Never expose it: it has no authentication.
- **Routes** are listed at the top of `Control.cs`:
  - `GET /servers`, `/peers`, `/health`;
  - session start / kick / accept / lock;
  - lobby-settings push;
  - operator invite routes;
  - `/early-quit/*`;
  - `POST /accounts/{user}/developer` to set `disable_afk_timeout` on a profile.

## Environment switches (Core)

| variable | effect |
|---|---|
| `ECHORELAY_DIAG=1` | log each start-session message with its wire bytes, and what each join resolved to. The session settings can carry user-entered values, so keep these logs private. |
| `ECHORELAY_AI_JOIN_TEAM=off\|N` | disable, or change, the team handed to team-Any joiners of AI sessions. Default 6. |

## Build

```
dotnet test EchoRelay.Core.Test
dotnet publish EchoRelay.Headless -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true
```

`linux-x64` and `win-x64` work the same way. `deploy/` has a systemd unit for a Raspberry Pi.
