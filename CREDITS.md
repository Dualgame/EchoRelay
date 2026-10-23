# Credits

- **Original work:** [EchoRelay](https://github.com/Xenomega/EchoRelay) by [@Xenomega](https://github.com/Xenomega).
  Everything on `main`, and every line on this branch not listed below, is theirs. Upstream ships without a
  license file; nothing on this branch adds a license to upstream code.
- **This branch (`relay-community`):** changes by [Dualgame](https://github.com/Dualgame). One
  commit per change, so each can be read, reverted or cherry-picked on its own:
  - build: .NET 10 retarget
  - Core: HTTP API routes the game calls on the services port; accept-loop fault isolation
  - Core: extension points (`ExtraApiHandler`, `MatchingGate`, `RegionFilter`) used by the headless host
  - Core: game-server registration region read fix and region preference
  - Core: AI-teammates session join fix
  - GameServer: lobby-settings relay
  - `EchoRelay.Headless`: a console host with invite codes, early-quit standing and a local control API

New files added on this branch carry their own header. Their license is stated in [docs/LICENSES.md](docs/LICENSES.md).
