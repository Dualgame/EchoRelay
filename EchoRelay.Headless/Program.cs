// Headless host for EchoRelay.Core. Repeats EchoRelay.App's MainWindow startup sequence
// (storage Open -> initial deployment if critical resources are missing -> Server -> Start) with log lines instead
// of forms. Usage: EchoRelay.Headless [--port 777] [--storage-dir DIR] [--game-dir DIR] [--serverdb-api-key KEY]
//                                     [--redeploy]
// TEST-ONLY (all off by default): [--test-start-session-level NAME|0xHEX] [--test-start-session-gametype NAME|0xHEX]
//   [--test-session-settings FILE.json] [--test-start-session-delay-ms 2000] [--test-start-session-lobby-type public|private]
//   When --test-start-session-level is given, every game server that registers is sent ERGameServerStartSession for that
//   level after the delay, with no client in the loop. FILE.json is a SessionSettings object: appid/gametype/level are
//   honoured (the flags override them) and every other top-level key is forwarded verbatim in the session JSON.
//   [--test-lobby-settings FILE.json]: also send SNSLobbySettingsResponse (wire symbol CSymbol64 0x5b71b22a4483bda5,
//   payload = u64 length + zlib JSON, the SNSLoginSettings layout) with FILE.json to the game server, once before and
//   once after the start-session. The game's handler 0x17dee0 (retail 6323983201049540) feeds "[NETGAME] Lobby settings".
// [--lobby-settings FILE.json] (off by default): send SNSLobbySettingsResponse with FILE.json to every peer right
//   after the server sends it LoginSettings (login) or LobbySessionSuccess (lobby join). That is where a client needs
//   it. A dedicated server ignores it.
// [--test-session-sequence LEVEL[@GAMETYPE],...] [--test-sequence-hold-ms 30000] (off by default): move the
//   client on from the --test-start-session-level session to the next levels (kick, then restart the session).
// [--test-start-session-skip N] (off by default = 0): the first N game servers to register are left idle, so a
//   client's own create request starts their session (the lobby); later servers get the auto start-session. Lets one host
//   run "S1 = client's lobby, S2 = --test-start-session-level", then POST /join_session {"session_id": S2} on the client.
// [--test-start-session-keep true] (off by default; ignored with --test-session-sequence): when an auto-started
//   session ends (e.g. "Dedicated: timeout waiting for first player" after 60 s), start it again on that server, so a
//   later /join_session finds the test level instead of making matching start an EMPTY session (server default level).
// [--control-port N] (off by default): localhost-only JSON control API on http://127.0.0.1:N/ (Control.cs: GET
//   /servers /peers /health; POST /servers/{id}/start-session|kick|accept|lock, /lobby-settings). Test tooling conventionally uses 7779.
// [--invites true] [--invite-max-per-user 3] (off by default): invite codes on the SERVICES port
//   (Invites.cs: POST /invites, GET /invites/{code}, POST /invites/{code}/revoke; uses Core Server.ExtraApiHandler). Each invite
//   leases an idle game server for a private session plus a /links alias; the control API mirrors GET /invites.
// [--early-quit-selftest] (run the early-quit policy cases and exit)
// [--early-quit true] [--early-quit-mode shadow|enforce] [--early-quit-policy FILE.json] [--early-quit-config FILE.json]
//   [--early-quit-flags 0xNN] [--early-quit-detect false] [--early-quit-grace N] (off by default;
//   uses Core MatchingService.MatchingGate): the services half of early quit (EarlyQuit.cs). Login push of Config / Flags / Update, v2
//   store <storage-dir>/earlyquit_v2/, detection from game-server player removals (classified by EarlyQuitPolicy.cs),
//   control API /early-quit/... Mode shadow (default) records and logs only; enforce refuses public arena / combat finds
//   and joins while locked out or on hold, and pulses the notice (0x22 for pulse_hold, else 0x02). The policy file holds
//   window / thresholds / ladder / grace / pulse timings; --early-quit-grace overrides its grace; --early-quit-flags
//   fixes one Flags value instead of the schedule.
using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Storage;
using EchoRelay.Core.Server.Storage.Filesystem;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services.ServerDB;

var opt = new Dictionary<string, string?>();
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) { Console.Error.WriteLine($"unexpected argument: {args[i]}"); return 2; }
    string key = args[i][2..];
    opt[key] = key is "redeploy" or "early-quit-selftest" ? "true" : (i + 1 < args.Length ? args[++i] : null);
}
// --early-quit-selftest: run the pure early-quit policy / store cases (EarlyQuitSelfTest.cs) and exit; needs no storage
if (opt.ContainsKey("early-quit-selftest")) { int f = EqSelfTest.Run(Console.WriteLine); Console.WriteLine(f == 0 ? "ALL PASS" : $"{f} FAILED"); return f == 0 ? 0 : 1; }
ushort port = ushort.Parse(opt.GetValueOrDefault("port") ?? "777");
string storageDir = Path.GetFullPath(opt.GetValueOrDefault("storage-dir") ?? "storage");
string? gameDir = opt.GetValueOrDefault("game-dir");
string? apiKey = opt.GetValueOrDefault("serverdb-api-key");
if (string.IsNullOrEmpty(apiKey)) apiKey = null; // App default: no ServerDB API key

void Log(string msg) => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {msg}");

ServerStorage storage = new FilesystemServerStorage(storageDir);
storage.Open();
bool allCritical = storage.AccessControlList.Exists() && storage.ChannelInfo.Exists() && storage.LoginSettings.Exists() && storage.SymbolCache.Exists();
bool anyCritical = storage.AccessControlList.Exists() || storage.ChannelInfo.Exists() || storage.LoginSettings.Exists() || storage.SymbolCache.Exists();
if (!allCritical && anyCritical && !opt.ContainsKey("redeploy"))
{
    // The App asks with a MessageBox here; redeploying clears all storage except accounts, so require the flag.
    Log($"[HOST] critical resources missing from non-empty storage {storageDir}; rerun with --redeploy");
    return 3;
}
if (!allCritical)
{
    Log($"[HOST] initial deployment into {storageDir}");
    InitialDeployment.PerformInitialDeployment(storage, gameDir, false);
}

var server = new Server(storage, new ServerSettings(port: port, serverDbApiKey: apiKey));
server.OnServerStarted += s => Log($"[SERVER] started on :{port}, public IP {s.PublicIPAddress?.ToString() ?? "unknown"}");
server.OnServerStopped += s => Log("[SERVER] stopped");
server.OnAuthorizationResult += (s, client, ok) => { if (!ok) Log($"[SERVER] client({client.Address}:{client.Port}) failed authorization"); };
server.OnServicePeerConnected += (svc, peer) => Log($"[{svc.Name}] client({peer.Address}:{peer.Port}) connected");
server.OnServicePeerDisconnected += (svc, peer) => Log($"[{svc.Name}] client({peer.Address}:{peer.Port}) disconnected");
server.OnServicePeerAuthenticated += (svc, peer, userId) => Log($"[{svc.Name}] client({peer.Address}:{peer.Port}) authenticated as {userId}");
server.OnServicePacketReceived += (svc, peer, packet) => { foreach (var m in packet) Log($"[{svc.Name}] client({peer.Address}:{peer.Port})->server: {m}"); };
server.OnServicePacketSent += (svc, peer, packet) => { foreach (var m in packet) Log($"[{svc.Name}] server->client({peer.Address}:{peer.Port}): {m}"); };
Regions.Init(Sym);
server.ServerDBService.Registry.OnGameServerRegistered += gs => Log($"[SERVERDB] game server registered: id {gs.ServerId} internal {gs.InternalAddress} external {gs.ExternalAddress}:{gs.Port} region {Regions.Name(gs.RegionSymbol)} (0x{gs.RegionSymbol:x16}) version {gs.VersionLock}");
server.ServerDBService.Registry.OnGameServerUnregistered += gs => Log($"[SERVERDB] game server unregistered: id {gs.ServerId}");

string? clientLobbySettingsFile = opt.GetValueOrDefault("lobby-settings");
if (clientLobbySettingsFile != null)
{
    var clientLobbySettings = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(clientLobbySettingsFile));
    Log($"[HOST] lobby settings armed: {clientLobbySettings.ToString(Newtonsoft.Json.Formatting.None)}");
    server.OnServicePacketSent += (svc, peer, packet) =>
    {
        if (!packet.Any(m => m is EchoRelay.Core.Server.Messages.Login.LoginSettings
                             || m is EchoRelay.Core.Server.Messages.Matching.LobbySessionSuccessv4
                             || m is EchoRelay.Core.Server.Messages.Matching.LobbySessionSuccessv5))
            return;
        // Send outside the event (it fires inside Peer.Send).
        _ = Task.Run(async () =>
        {
            try { await peer.Send(new SNSLobbySettingsResponse(clientLobbySettings)); }
            catch (Exception e) { Log($"[HOST] lobby settings send to {peer.Address}:{peer.Port} failed: {e.Message}"); }
        });
    };
}

string? testLevel = opt.GetValueOrDefault("test-start-session-level");
if (testLevel != null)
{
    var testSettings = new ERGameServerStartSession.SessionSettings();
    string? settingsFile = opt.GetValueOrDefault("test-session-settings");
    if (settingsFile != null)
        testSettings = Newtonsoft.Json.JsonConvert.DeserializeObject<ERGameServerStartSession.SessionSettings>(File.ReadAllText(settingsFile))
                       ?? throw new InvalidDataException($"empty settings file {settingsFile}");
    string? lobbySettingsFile = opt.GetValueOrDefault("test-lobby-settings");
    Newtonsoft.Json.Linq.JObject? lobbySettings = lobbySettingsFile == null ? null
        : Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(lobbySettingsFile));
    long level = Sym(testLevel);
    long gameType = Sym(opt.GetValueOrDefault("test-start-session-gametype") ?? "social_2.0");
    int delayMs = int.Parse(opt.GetValueOrDefault("test-start-session-delay-ms") ?? "2000");
    var lobbyType = (opt.GetValueOrDefault("test-start-session-lobby-type") ?? "public") == "private"
        ? ERGameServerStartSession.LobbyType.Private : ERGameServerStartSession.LobbyType.Public;
    string extra = Newtonsoft.Json.JsonConvert.SerializeObject(testSettings.AdditionalData);
    Log($"[TEST] auto start-session armed: level {testLevel}=0x{level:x16} gametype=0x{gameType:x16} lobby {lobbyType} delay {delayMs} ms extra {extra}");
    int skip = int.Parse(opt.GetValueOrDefault("test-start-session-skip") ?? "0"), seen = 0;
    bool keep = opt.ContainsKey("test-start-session-keep") && opt.GetValueOrDefault("test-session-sequence") == null;
    server.ServerDBService.Registry.OnGameServerRegistered += gs => _ = Task.Run(async () =>
    {
        try
        {
            if (Interlocked.Increment(ref seen) <= skip) { Log($"[TEST] game server {gs.ServerId} left idle (skip {skip})"); return; }
            await Task.Delay(delayMs);
            if (lobbySettings != null)
            {
                Log($"[TEST] sending SNSLobbySettingsResponse (pre) {lobbySettings.ToString(Newtonsoft.Json.Formatting.None)}");
                await gs.Peer.Send(new SNSLobbySettingsResponse(lobbySettings));
            }
            Log($"[TEST] sending ERGameServerStartSession to game server {gs.ServerId}: level 0x{level:x16} gametype 0x{gameType:x16} extra {extra}");
            await gs.StartSession(new EchoRelay.Core.Game.XPlatformId(), lobbyType, new Guid(), gameType, level, testSettings);
            Log($"[TEST] start-session sent to game server {gs.ServerId}, session id {gs.SessionId}");
            if (keep)
                gs.OnSessionStateChanged += g =>
                {
                    if (g.SessionId != null) return;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(delayMs);
                        if (g.SessionId != null) return;
                        await g.StartSession(new EchoRelay.Core.Game.XPlatformId(), lobbyType, new Guid(), gameType, level, testSettings);
                        Log($"[TEST] start-session re-sent (keep) to game server {g.ServerId}, session id {g.SessionId}");
                    });
                };
            if (lobbySettings != null)
            {
                await Task.Delay(delayMs);
                Log("[TEST] sending SNSLobbySettingsResponse (post)");
                await gs.Peer.Send(new SNSLobbySettingsResponse(lobbySettings));
            }
        }
        catch (Exception e) { Log($"[TEST] start-session failed: {e}"); }
    });

    // [--test-session-sequence LEVEL[@GAMETYPE],... --test-sequence-hold-ms 30000] (off by default): once a player
    // has been in a test session for the hold time, kick every player; when the game server reports that session
    // ended, start the next level on it (gametype default social_2.0, so a client's social re-find matches it).
    string? sequence = opt.GetValueOrDefault("test-session-sequence");
    if (sequence != null)
    {
        var steps = new Queue<(string Name, long Level, long GameType)>(sequence.Split(',').Select(s =>
        {
            var p = s.Split('@');
            return (s, Sym(p[0]), Sym(p.Length > 1 ? p[1] : "social_2.0"));
        }));
        int holdMs = int.Parse(opt.GetValueOrDefault("test-sequence-hold-ms") ?? "30000");
        Log($"[TEST] session sequence armed: {sequence}, hold {holdMs} ms");
        server.ServerDBService.Registry.OnGameServerRegistered += gs =>
        {
            bool kicking = false;
            gs.OnPlayersAdded += (g, players) =>
            {
                Log($"[TEST] players added to session {g.SessionId}: {players.Length}");
                if (kicking || steps.Count == 0) return;
                kicking = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(holdMs);
                    foreach (var (playerSession, _) in await g.GetPlayers())
                    {
                        Log($"[TEST] kicking player session {playerSession}");
                        await g.KickPlayer(playerSession);
                    }
                });
            };
            gs.OnPlayerRemoved += (g, playerSession, _) => Log($"[TEST] player session {playerSession} removed");
            gs.OnSessionStateChanged += g =>
            {
                Log($"[TEST] session state: id {g.SessionId?.ToString() ?? "none"} level 0x{g.SessionLevelSymbol ?? 0:x16}");
                if (!kicking || g.SessionId != null || steps.Count == 0) return;
                kicking = false;
                var next = steps.Dequeue();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);
                        Log($"[TEST] sequence: starting {next.Name} = level 0x{next.Level:x16} gametype 0x{next.GameType:x16}");
                        await g.StartSession(new EchoRelay.Core.Game.XPlatformId(), lobbyType, new Guid(), next.GameType, next.Level, testSettings);
                    }
                    catch (Exception e) { Log($"[TEST] sequence start failed: {e}"); }
                });
            };
        };
    }
}

string? controlPort = opt.GetValueOrDefault("control-port");
if (controlPort != null && controlPort != "0")
    server.OnServerStarted += _ =>
    {
        try { ControlApi.Start(server, int.Parse(controlPort), Sym, Log); }
        catch (Exception e) { Log($"[CONTROL] failed to listen on 127.0.0.1:{controlPort}: {e.Message}"); }
    };

if (opt.GetValueOrDefault("invites") is "true" or "on" or "1")
    Invites.Enable(server, Sym, Log, int.Parse(opt.GetValueOrDefault("invite-max-per-user") ?? "3"));

if (opt.GetValueOrDefault("early-quit") is "true" or "on" or "1")
    EarlyQuit.Enable(server, Sym, Log, opt.GetValueOrDefault("early-quit-config"),
                     opt.GetValueOrDefault("early-quit-flags") is string ef ? Convert.ToByte(ef, 16) : null,
                     opt.GetValueOrDefault("early-quit-detect") is not ("false" or "off" or "0"), // on by default: shadow records from it
                     opt.GetValueOrDefault("early-quit-mode") ?? "shadow", opt.GetValueOrDefault("early-quit-policy"),
                     opt.GetValueOrDefault("early-quit-grace") is string eg ? int.Parse(eg) : null,
                     Path.Combine(storageDir, "earlyquit_v2"));

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (server.Running) server.Stop(); };
try { await server.Start(cts); }
catch (OperationCanceledException) { }
return 0;


// NAME -> CSymbol64 (lowercase-folded CRC64 "jones", poly 0x95AC9329AC4BC9B5, init -1);
// "0x..." hex or a decimal integer is taken as the symbol itself.
static long Sym(string s)
{
    if (s.StartsWith("0x")) return unchecked((long)Convert.ToUInt64(s[2..], 16));
    if (long.TryParse(s, out long v)) return v;
    const ulong poly = 0x95AC9329AC4BC9B5UL;
    var seeds = new ulong[256];
    for (int i = 0; i < 256; i++)
    {
        ulong x = (i & 0x80) != 0 ? 0x2B5926535897936AUL : 0UL;
        if ((i & 0x40) != 0) x ^= poly;
        for (int sh = 0x20; sh != 0; sh >>= 1) { x <<= 1; if ((i & sh) != 0) x ^= poly; }
        seeds[i] = x << 1;
    }
    ulong r = ulong.MaxValue;
    foreach (byte b in System.Text.Encoding.ASCII.GetBytes(s))
    {
        byte c = b is >= 0x41 and <= 0x5A ? (byte)(b + 32) : b;
        r = (r << 8) ^ seeds[(r >> 56) & 0xFF] ^ c;
    }
    return unchecked((long)r);
}

// SNSLobbySettingsResponse, services -> game. Not in upstream EchoRelay.Core. Layout = SNSLoginSettings: u64 decompressed
// length + zlib JSON. The retail TCP handler 0x17dee0 reads the u64, inflates via 0x152a910 (the same routine the
// login-settings handler uses, at 0x60c501), parses the JSON into game+0x63260, and logs "[NETGAME] Lobby settings
// received" (from disassembly of PC build 6323983201049540). Only a services-connected CLIENT listens for it.
class SNSLobbySettingsResponse : EchoRelay.Core.Server.Messages.Message
{
    public override long MessageTypeSymbol => unchecked((long)0x5b71b22a4483bda5UL);
    public Newtonsoft.Json.Linq.JObject Settings;
    public SNSLobbySettingsResponse() { Settings = new(); }
    public SNSLobbySettingsResponse(Newtonsoft.Json.Linq.JObject settings) { Settings = settings; }
    public override void Stream(EchoRelay.Core.Utils.StreamIO io) =>
        io.StreamJSON(ref Settings, false, EchoRelay.Core.Utils.JSONCompressionMode.Zlib);
    public override string ToString() => $"SNSLobbySettingsResponse({Settings.ToString(Newtonsoft.Json.Formatting.None)})";
}
