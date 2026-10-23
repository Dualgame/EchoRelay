# SNSLobbySettingsResponse relay (GameServer)

ServerDB can send a game server `SNSLobbySettingsResponse` (TCP form `0x5B71B22A4483BDA5`, payload = u64 decompressed
length + zlib JSON). A dedicated server registers only the **local** listener for this message (local form
`0x4CD5B97B2B97D9A3`), so without help the TCP form is dropped.

`EchoRelay.GameServer` on this branch:

1. inflates the JSON with the game's own zlib uncompress (RVA `0x152A910`, PC `6323983201049540`);
2. raises the local event (8-byte zero item + plain JSON), as the retail handler does;
3. broadcasts it to all peers;
4. re-broadcasts it every 2 s, up to 10 times, after the entrant count changes. Settings usually arrive with the
   session-success message, before the client has joined the broadcaster.

The cache is cleared on session start and end, so one session's settings never reach the next session on that server.
The listener is unlistened on unload like the others. The RVA ties this to PC build `6323983201049540`.
