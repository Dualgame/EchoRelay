# EchoRelay.Headless

A console host for `EchoRelay.Core` with no WinForms. It runs EchoRelay.App's startup sequence (storage open, initial
deployment from the game folder, server start), logging lines in place of forms. Every feature beyond that is off by
default.

## Quick start

Build it (see Build below), then, on a host on your LAN:

```
EchoRelay.Headless --port 777 --storage-dir ./storage --game-dir "<Echo VR install>/bin/win10" --invites true --early-quit true
```

- `--port`: the one TCP port every service listens on (default 777).
- `--storage-dir`: where accounts, configs, the access control list and the invite and early-quit stores live
  (default `./storage`).
- `--game-dir`: used only for the initial deployment, which runs when the storage holds none of its critical files
  (access control list, channel info, login settings, symbol cache). The symbol cache is then filled from the files in
  that folder (top level only). It is optional and is not read on later starts. If only some of the critical files are
  missing, Headless stops and asks for `--redeploy`, which clears all storage except accounts.
- `--invites true`: the `/invites` routes the client invite mod calls ([protocol/invites.md](protocol/invites.md)).
- `--early-quit true`: the early-quit messages, in shadow mode by default ([protocol/early-quit.md](protocol/early-quit.md)).

## Client config

Each client's `_local/config.json` is filled in by the user, with this host's addresses:

| key | value |
|---|---|
| `apiservice_host` | `http://HOST:PORT/api` |
| `configservice_host` | `ws://HOST:PORT/config` |
| `loginservice_host` | `ws://HOST:PORT/login?auth=AccountPassword&displayname=AccountName` |
| `matchingservice_host` | `ws://HOST:PORT/matching` |
| `serverdb_host` | `ws://HOST:PORT/serverdb` (game servers only; with `?api_key=KEY` when `--serverdb-api-key` is set) |
| `transactionservice_host` | `ws://HOST:PORT/transaction` |
| `publisher_lock` | `rad15_live` |

`HOST` must be an address the client can reach on your LAN. The client mods' install pages say where the file goes on
PC and on Quest.

## All options

```
EchoRelay.Headless [--port 777] [--storage-dir DIR] [--game-dir DIR] [--serverdb-api-key KEY] [--redeploy]
  [--invites true] [--invite-max-per-user 3]                      invite codes, see protocol/invites.md
  [--invite-store FILE|off]                                       default <storage-dir>/invites.json
  [--early-quit true] [--early-quit-mode shadow|enforce]          early quit, see protocol/early-quit.md
    [--early-quit-policy FILE.json] [--early-quit-config FILE.json] [--early-quit-detect false]
    [--early-quit-grace N] [--early-quit-flags 0xNN]
  [--early-quit-selftest]                                         run the policy self-test and exit
  [--lobby-settings FILE.json]                                    send SNSLobbySettingsResponse to clients
  [--control-port N]                                              localhost-only JSON control API
  [--test-start-session-level NAME|0xHEX] [...]                   test-only session automation (see Program.cs)
```

## Network exposure

This host is for a LAN. Out of the box it is reachable more widely than that:

- **The services port is open to every network the host is on.** `deploy/install_linux.sh` runs
  `ufw allow "$PORT/tcp"`, which accepts any source address. Limit it to your LAN instead, for example:

  ```
  sudo ufw delete allow 777/tcp
  sudo ufw allow from 192.168.1.0/24 to any port 777 proto tcp
  ```

  (replace the network and the port with yours).
- **The host calls out to the internet once at start.** Core asks `https://ipinfo.io/ip` for its public IP address
  (`IPAddressUtils.GetExternalIPAddress`, upstream behaviour). The result is logged (`public IP …`) and is handed to
  clients as the external address of any game server that registered from a private address; clients also receive the
  internal address. Without internet access the lookup fails quietly and the log reads `public IP unknown`. This is
  read from the code; its effect on a LAN without internet access has not been tested separately.
- **The access control list allows everyone.** The initial deployment writes `<storage-dir>/access_control_list.json`
  with allow `*` and no deny rules. Edit that file to restrict which accounts may log in.
- **The control API** (`--control-port`) listens on `127.0.0.1` only and has no authentication. Do not forward it.

## Logs are secrets

Treat the host's console log, journal and storage as secrets. Headless prints every service message it receives and
sends, in full. That includes invite codes, and the game's remote-log messages, whose create metric carries the match
password in clear. All traffic is plain HTTP and WebSocket, so passwords and codes also cross the network in clear:
keep the host on a LAN you trust, and do not paste its logs in public.

## Control API

- **Off by default.** `--control-port N` turns it on.
- **Localhost only:** `http://127.0.0.1:N/`. Never expose it: it has no authentication.
- **Routes:** [protocol/control-api.md](protocol/control-api.md) (the list at the top of `Control.cs` is the implementation):
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
