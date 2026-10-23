// Invite codes on the SERVICES port (LAN-reachable, unlike the loopback control API), for the
// shipped-but-stubbed CREATE GAME / JOIN VIA CODE wizard and for launchers. Design:
// docs/invites.md. Served through Server.ExtraApiHandler;
// matches with or without the /api prefix (the game drops it). JSON in/out, errors {"error": "..."}.
//   POST /invites                 {"user_id":"OVR-ORG-…", "gametype":NAME|0xHEX, "level":NAME|0xHEX (optional),
//                                  "region":LABEL|CODE|0xHEX (optional; "US WEST"|"usw", Regions.cs; falls back to "default"), "password":"…" (optional), "expires_s":21600,
//                                  "player_limit":16, "settings":{SessionSettings} (optional)}
//                                 -> {"code":"X7F9A", "link":GUID, "gametype", "level", "expires_at", "password":bool}
//                                 403 user_id is not logged in, or not from the caller's address; 429 too many live
//                                 invites, 503 no free server
//   GET  /invites/{code}[?password=…]  -> {"code", "link", "gametype", "level", "expires_at"}
//                                 404 unknown, 403 wrong/missing password, 410 expired/revoked (1 h),
//                                 503 unleased (host restarted or its server left) and no idle server to re-lease
//   POST /invites/{code}/revoke   {"user_id":…} -> {"revoked":code, "kicked":N}; creator only (403), same caller
//                                 binding as create; kicks the session's players at once
// The loopback control API adds operator routes (no login check, no per-user cap): POST /invites (creator = the
// body's user_id or "control") and POST /invites/{code}/revoke, for test tooling and a future bot/launcher backend.
// An invite LEASES one idle game server: the host starts a private session on it and adds a /links alias (shared with
// Control.cs), so LobbyJoinSessionRequestv7(lobby=link) reaches it. A started private session is skipped by
// every find/create filter (FilterGameServers matches only Unassigned/Public there), so no lock is needed. When the
// session ends (empty-server timeout after 60 s) the host starts it again until the invite expires or is revoked.
// Invites persist in <storage-dir>/invites.json (InviteBook.cs; --invite-store FILE|off). The lease does not: a
// restored invite, or one whose game server unregistered, is UNLEASED until the next resolve of its code leases an
// idle server again (same code, link, password, expiry).
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services.ServerDB;
using Newtonsoft.Json.Linq;
using HttpError = ControlApi.HttpError;
using LobbyType = EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession.LobbyType;

static class Invites
{
    // No 0/O/1/I: codes are read aloud and typed on the in-game keyboard (5 characters max on page 34).
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    const int CodeLength = 5, DefaultExpiresS = 6 * 3600, MaxExpiresS = 24 * 3600;
    static InviteBook book = new(null);
    static readonly SemaphoreSlim LeaseLock = new(1, 1);
    static Server server = null!;
    static bool enabled;
    static Func<string, long> sym = null!;
    static Action<string> log = null!;
    static int maxPerUser;

    public static void Enable(Server s, Func<string, long> symbol, Action<string> logger, int maxLivePerUser, string? storePath)
    {
        server = s; sym = symbol; log = logger; maxPerUser = maxLivePerUser;
        book = new InviteBook(storePath, null, logger)
        {
            OnDropped = (inv, why) =>
            {
                ControlApi.UnaliasLink(server, inv.Link);
                log($"[INVITES] {inv.Code} dropped ({why}); link {inv.Link} removed" +
                    (inv.ServerId is ulong id ? $", game server {id} released" : ""));
            },
            Kick = KickAll,
        };
        book.Load();
        enabled = true;
        s.ExtraApiHandler = ctx =>
        {
            string[] seg = Segments(ctx);
            if (seg.Length == 0 || seg[0] != "invites") return false;
            _ = Task.Run(() => Handle(ctx, seg));
            return true;
        };
        var reg = s.ServerDBService.Registry;
        reg.OnGameServerRegistered += gs => gs.OnSessionStateChanged += KeepSession;
        // A server that leaves (crash, or the host's own shutdown) unleases its invite instead of dropping it: the code
        // stays valid and the next resolve leases another idle server.
        reg.OnGameServerUnregistered += gs =>
        {
            foreach (var inv in book.ByCode.Values.Where(i => i.ServerId == gs.ServerId).ToArray())
            {
                book.SetLease(inv.Code, null);
                ControlApi.UnaliasLink(server, inv.Link);
                log($"[INVITES] {inv.Code} unleased (game server {gs.ServerId} unregistered); the next resolve re-leases");
            }
        };
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(5000);
                book.Sweep();
            }
        });
        log($"[INVITES] enabled on the services port: /invites (max {maxPerUser} live per user, store {storePath ?? "off"})");
    }

    // The control API's read-only mirror (GET /invites on the loopback port).
    static void NeedEnabled() { if (!enabled) throw new HttpError(404, "invites are off (host --invites true)"); }

    public static JToken List() { NeedEnabled(); return ListAll(); }
    static JToken ListAll() => new JArray(book.ByCode.Values.OrderBy(i => i.Created).Select(i =>
    {
        var g = i.ServerId is ulong id ? server.ServerDBService.Registry.GetGameServer(id) : null;
        return new JObject
        {
            ["code"] = i.Code, ["link"] = i.Link.ToString(), ["server"] = i.ServerId?.ToString(), ["creator"] = i.Creator,
            ["gametype"] = i.GameType, ["level"] = i.Level, ["password"] = i.PasswordHash != null,
            ["created_at"] = i.Created.ToString("o"), ["expires_at"] = i.Expires.ToString("o"),
            ["session_id"] = g?.SessionId?.ToString(), ["live"] = g != null, ["leased"] = i.ServerId != null,
            ["players"] = g?.SessionPlayerCount ?? 0,
        };
    }));

    static string[] Segments(HttpListenerContext ctx)
    {
        var seg = (ctx.Request.Url?.AbsolutePath ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length > 0 && seg[0].Equals("api", StringComparison.OrdinalIgnoreCase)) seg = seg[1..];
        if (seg.Length > 0) seg[0] = seg[0].ToLowerInvariant();
        return seg;
    }

    static async Task Handle(HttpListenerContext ctx, string[] seg)
    {
        int code = 200;
        JToken result;
        try
        {
            string method = ctx.Request.HttpMethod;
            IPAddress caller = ctx.Request.RemoteEndPoint.Address;
            JObject body = new();
            if (method == "POST")
            {
                string text = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                try { if (!string.IsNullOrWhiteSpace(text)) body = JObject.Parse(text); }
                catch (Exception e) { throw new HttpError(400, $"bad JSON body: {e.Message}"); }
            }
            result = (method, seg) switch
            {
                ("POST", ["invites"]) => await Create(body, LoggedInCaller((string?)body["user_id"], caller), false),
                ("GET", ["invites", var c]) => await Resolve(c, ctx.Request.QueryString["password"]),
                ("POST", ["invites", var c, "revoke"]) => await Revoke(c, LoggedInCaller((string?)body["user_id"], caller)),
                _ => throw new HttpError(404, $"no route {method} /{string.Join('/', seg)}"),
            };
        }
        catch (HttpError e) { code = e.Code; result = new JObject { ["error"] = e.Message }; }
        catch (Exception e) { code = 500; result = new JObject { ["error"] = e.Message }; }
        await ControlApi.Respond(ctx, code, result);
    }

    // H-4: the user_id must have a live login session from the caller's own address (InviteAuth).
    static string LoggedInCaller(string? userId, IPAddress caller)
    {
        if (string.IsNullOrEmpty(userId)) throw new HttpError(400, "need user_id");
        List<(string?, IPAddress)> logins;
        lock (server.LoginService.PeersLock)
            logins = server.LoginService.Peers.Select(p => (p.UserId?.ToString(), p.Address)).ToList();
        if (InviteAuth.Refusal(userId, caller, logins) is string why)
        {
            log($"[INVITES] refused {userId} from {caller}: {why}");
            throw new HttpError(403, why);
        }
        return userId;
    }

    public static Task<JToken> CreateAsOperator(JObject b) { NeedEnabled(); return Create(b, (string?)b["user_id"] ?? "control", true); }
    public static Task<JToken> RevokeAsOperator(string c) { NeedEnabled(); return Revoke(c, null); }

    // asOperator: a control-API create (trusted loopback caller) -> no login check, no per-user cap.
    static async Task<JToken> Create(JObject b, string user, bool asOperator)
    {
        string gameType = (string?)b["gametype"] ?? throw new HttpError(400, "need gametype");
        string? level = (string?)b["level"], region = (string?)b["region"], password = (string?)b["password"];
        int expiresS = (int?)b["expires_s"] ?? DefaultExpiresS;
        if (expiresS <= 0 || expiresS > MaxExpiresS) throw new HttpError(400, $"expires_s must be 1..{MaxExpiresS}");
        byte limit = (byte?)b["player_limit"] ?? 16;
        var settings = b["settings"] is JObject so ? so.ToObject<ERGameServerStartSession.SessionSettings>() : null;
        long gameTypeSym = sym(gameType);
        long? levelSym = level == null ? null : sym(level), regionSym = region == null ? null : Regions.Symbol(region);

        await LeaseLock.WaitAsync();
        try
        {
            if (!asOperator && book.ByCode.Values.Count(i => i.Creator == user) >= maxPerUser)
                throw new HttpError(429, $"{user} already has {maxPerUser} live invite(s)");
            var g = FreeServer(regionSym)
                ?? throw new HttpError(503, region == null ? "no free game server" : $"no free game server in region {region} or default");
            string code;
            do code = string.Concat(Enumerable.Range(0, CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
            while (book.Taken(code));
            var now = book.Now;
            var inv = new Invite(code, Guid.NewGuid(), user, gameType, level, region, gameTypeSym, levelSym, regionSym, settings,
                limit, password == null ? null : Hash(code, password), now, now.AddSeconds(expiresS)) { ServerId = g.ServerId };
            book.Add(inv);
            try { await StartOn(inv, g); }
            catch { book.Drop(inv, "session start failed"); throw; }   // no stored invite without a session
            log($"[INVITES] {code} by {user}{(asOperator ? " (control API)" : "")}: game server {g.ServerId} session {g.SessionId} link {inv.Link} " +
                $"gametype {gameType} level {level ?? "(default)"} region {Regions.Name(g.RegionSymbol)}{(region != null ? $" (asked {region})" : "")} expires {inv.Expires:o}{(password != null ? " password" : "")}");
            return new JObject
            {
                ["code"] = code, ["link"] = inv.Link.ToString(), ["gametype"] = gameType, ["level"] = level,
                ["expires_at"] = inv.Expires.ToString("o"), ["password"] = password != null,
                ["region"] = Regions.Name(g.RegionSymbol),
            };
        }
        finally { LeaseLock.Release(); }
    }

    // An idle server no invite holds. A region pick prefers servers registered in it, then servers in "default".
    static RegisteredGameServer? FreeServer(long? regionSym)
    {
        var leased = book.ByCode.Values.Where(i => i.ServerId != null).Select(i => i.ServerId!.Value).ToHashSet();
        var free = server.ServerDBService.Registry.RegisteredGameServers.Values
            .Where(x => x.SessionId == null && !leased.Contains(x.ServerId)).OrderBy(x => x.ServerId).ToList();
        return regionSym == null ? free.FirstOrDefault()
            : free.FirstOrDefault(x => x.RegionSymbol == regionSym) ?? free.FirstOrDefault(x => x.RegionSymbol == Regions.Default);
    }

    static async Task StartOn(Invite inv, RegisteredGameServer g)
    {
        await g.StartSession(new EchoRelay.Core.Game.XPlatformId(), LobbyType.Private, new Guid(), inv.GameTypeSym, inv.LevelSym, inv.Settings, inv.PlayerLimit);
        // A join by the link that finds the server idle (between sessions) starts the invite's own session.
        ControlApi.AliasLink(server, g, inv.Link, () => book.ByCode.ContainsKey(inv.Code)
            ? new GameServerRegistry.SessionStartParameters(LobbyType.Private, new Guid(), inv.GameTypeSym, inv.LevelSym, inv.Settings, inv.PlayerLimit)
            : null);
    }

    // A restored (or orphaned) invite leases an idle server on resolve, so the link a launcher joins next has a session.
    static async Task Release(string code)
    {
        await LeaseLock.WaitAsync();
        try
        {
            if (!book.ByCode.TryGetValue(code, out var inv) || inv.ServerId != null) return;
            var g = FreeServer(inv.RegionSym)
                ?? throw new HttpError(503, $"invite {code} has no game server right now (host restarted or its server left); the code stays valid, retry");
            inv = book.SetLease(code, g.ServerId) ?? throw new HttpError(410, $"invite {code} dropped");
            await StartOn(inv, g);
            log($"[INVITES] {code} re-leased: game server {g.ServerId} session {g.SessionId} link {inv.Link}");
        }
        finally { LeaseLock.Release(); }
    }

    static async Task<JToken> Resolve(string c, string? password)
    {
        var inv = book.Lookup(c);
        if (inv.PasswordHash != null &&
            (password == null || !CryptographicOperations.FixedTimeEquals(inv.PasswordHash, Hash(inv.Code, password))))
            throw new HttpError(403, password == null ? $"invite {inv.Code} needs a password" : "wrong password");
        if (inv.ServerId == null) await Release(inv.Code);
        log($"[INVITES] {inv.Code} resolved -> link {inv.Link}");
        return new JObject
        {
            ["code"] = inv.Code, ["link"] = inv.Link.ToString(), ["gametype"] = inv.GameType, ["level"] = inv.Level,
            ["expires_at"] = inv.Expires.ToString("o"),
        };
    }

    // user == null: operator revoke from the control API.
    static async Task<JToken> Revoke(string c, string? user)
    {
        var (inv, kicked) = await book.Revoke(c, user);
        return new JObject { ["revoked"] = inv.Code, ["kicked"] = kicked };
    }

    // H-2: a revoke ends the private session now, with the control API's kick (ERGameServerPlayersRejected per player
    // session); the then-empty session ends on the server and KeepSession no longer restarts it.
    static async Task<int> KickAll(Invite inv)
    {
        var g = inv.ServerId is ulong id ? server.ServerDBService.Registry.GetGameServer(id) : null;
        if (g == null) return 0;
        var players = await g.GetPlayers();
        foreach (var p in players)
        {
            try { await g.KickPlayer(p.PlayerSession); }
            catch (Exception e) { log($"[INVITES] {inv.Code}: kick {p.PlayerSession} failed: {e.Message}"); }
        }
        log($"[INVITES] {inv.Code} revoked: kicked {players.Length} player(s) on game server {g.ServerId}");
        return players.Length;
    }

    static byte[] Hash(string code, string password) => SHA256.HashData(Encoding.UTF8.GetBytes($"{code}\n{password}"));

    // Lease keep-alive: an invite's session that ends is started again on the same server (same settings).
    static readonly ConcurrentDictionary<ulong, byte> Restarting = new();
    static void KeepSession(RegisteredGameServer g)
    {
        if (g.SessionId != null) return;
        var inv = book.ByCode.Values.FirstOrDefault(i => i.ServerId == g.ServerId);
        if (inv == null || !Restarting.TryAdd(g.ServerId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                if (g.SessionId != null || !book.ByCode.TryGetValue(inv.Code, out var cur) || cur.ServerId != g.ServerId) return;
                await g.StartSession(new EchoRelay.Core.Game.XPlatformId(), LobbyType.Private, new Guid(), inv.GameTypeSym, inv.LevelSym, inv.Settings, inv.PlayerLimit);
                log($"[INVITES] {inv.Code}: session restarted on game server {g.ServerId}, session id {g.SessionId}");
            }
            catch (Exception e) { log($"[INVITES] {inv.Code}: session restart failed: {e.Message}"); }
            finally { Restarting.TryRemove(g.ServerId, out _); }
        });
    }
}
