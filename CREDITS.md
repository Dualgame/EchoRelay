# Credits

- **Original work:** [EchoRelay](https://github.com/Xenomega/EchoRelay) by [@Xenomega](https://github.com/Xenomega).
  Everything up to tag `upstream-2023-11`, and every line not listed below, is theirs. Upstream ships without a
  license file, and so does this repository (see [docs/LICENSES.md](docs/LICENSES.md)).
- **Changes after `upstream-2023-11`:** by [Dualgame](https://github.com/Dualgame). One
  commit per change, so each can be read, reverted or cherry-picked on its own:
  - build: .NET 10 retarget
  - Core: HTTP API routes the game calls on the services port; accept-loop fault isolation
  - Core: extension points (`ExtraApiHandler`, `MatchingGate`, `RegionFilter`) used by the headless host
  - Core: game-server registration region read fix and region preference
  - Core: AI-teammates session join fix
  - GameServer: lobby-settings relay
  - `EchoRelay.Headless`: a console host with invite codes, early-quit standing and a local control API

This repository is for research and educational purposes; see [docs/LICENSES.md](docs/LICENSES.md).
