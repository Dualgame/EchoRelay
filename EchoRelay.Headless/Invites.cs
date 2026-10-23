// Invite codes on the SERVICES port (LAN-reachable, unlike the loopback control API), for the
// shipped-but-stubbed CREATE GAME / JOIN VIA CODE wizard and for launchers. Design:
// docs/invites.md. Served through Server.ExtraApiHandler;
// matches with or without the /api prefix (the game drops it). JSON in/out, errors {"error": "..."}.
//   POST /invites                 {"user_id":"OVR-ORG-…", "gametype":NAME|0xHEX, "level":NAME|0xHEX (optional),
//                                  "region":LABEL|CODE|0xHEX (optional; "US WEST"|"usw", Regions.cs; falls back to "default"), "password":"…" (optional), "expires_s":21600,
//                                  "player_limit":16, "settings":{SessionSettings} (optional)}
//                                 -> {"code":"X7F9A", "link":GUID, "gametype", "level", "expires_at", "password":bool}
//                                 403 user_id is not a logged-in peer, 429 too many live invites, 503 no free server
//   GET  /invites/{code}[?password=…]  -> {"code", "link", "gametype", "level", "expires_at"}
//                                 404 unknown, 403 wrong/missing password, 410 expired/revoked/server gone (1 h)
//   POST /invites/{code}/revoke   {"user_id":…} -> {"revoked":code}; creator only (403)
// The loopback control API adds operator routes (no login check, no per-user cap): POST /invites (creator = the
// body's user_id or "control") and POST /invites/{code}/revoke, for test tooling and a future bot/launcher backend.
// An invite LEASES one idle game server: the host starts a private session on it and adds a /links alias (shared with
// Control.cs), so LobbyJoinSessionRequestv7(lobby=link) reaches it. A started private session is skipped by
// every find/create filter (FilterGameServers matches only Unassigned/Public there), so no lock is needed. When the
// session ends (empty-server timeout after 60 s) the host starts it again until the invite expires or is revoked.
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services;
using EchoRelay.Core.Server.Services.ServerDB;
using Newtonsoft.Json.Linq;
using HttpError = ControlApi.HttpError;
using LobbyType = EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession.LobbyType;

static class Invites
{
    sealed record Invite(string Code, Guid Link, ulong ServerId, string Creator, string GameType, string? Level,
        long GameTypeSym, long? LevelSym, ERGameServerStartSession.SessionSettings? Settings, byte PlayerLimit,
        byte[]? PasswordHash, DateTime Created, DateTime Expires);

    // No 0/O/1/I: codes are read aloud and typed on the in-game keyboard (5 characters max on page 34).
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    const int CodeLength = 5, DefaultExpiresS = 6 * 3600, MaxExpiresS = 24 * 3600;
    static readonly ConcurrentDictionary<string, Invite> ByCode = new();
    // Dropped codes answer 410 with the reason for an hour (and are not reissued meanwhile), instead of a bare 404.
    static readonly ConcurrentDictionary<string, (string Why, DateTime At)> Gone = new();
    static readonly SemaphoreSlim CreateLock = new(1, 1);
    static Server server = null!;
    static bool enabled;
    static Func<string, long> sym = null!;
    static Action<string> log = null!;
    static int maxPerUser;

    public static void Enable(Server s, Func<string, long> symbol, Action<string> logger, int maxLivePerUser)
    {
        server = s; sym = symbol; log = logger; maxPerUser = maxLivePerUser; enabled = true;
        s.ExtraApiHandler = ctx =>
        {
            string[] seg = Segments(ctx);
            if (seg.Length == 0 || seg[0] != "invites") return false;
            _ = Task.Run(() => Handle(ctx, seg));
            return true;
        };
        var reg = s.ServerDBService.Registry;
        reg.OnGameServerRegistered += gs => gs.OnSessionStateChanged += KeepSession;
        reg.OnGameServerUnregistered += gs =>
        {
            foreach (var inv in ByCode.Values.Where(i => i.ServerId == gs.ServerId).ToArray())
                Drop(inv, "game server unregistered");
        };
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(5000);
                foreach (var inv in ByCode.Values.Where(i => i.Expires <= DateTime.UtcNow).ToArray())
                    Drop(inv, "expired");
                foreach (var kv in Gone.Where(kv => kv.Value.At < DateTime.UtcNow.AddHours(-1)).ToArray())
                    Gone.TryRemove(kv.Key, out _);
            }
        });
        log($"[INVITES] enabled on the services port: /invites (max {maxPerUser} live per user)");
    }

    // The control API's read-only mirror (GET /invites on the loopback port).
    static void NeedEnabled() { if (!enabled) throw new HttpError(404, "invites are off (host --invites true)"); }

    public static JToken List() { NeedEnabled(); return ListAll(); }
    static JToken ListAll() => new JArray(ByCode.Values.OrderBy(i => i.Created).Select(i =>
    {
        var g = server.ServerDBService.Registry.GetGameServer(i.ServerId);
        return new JObject
        {
            ["code"] = i.Code, ["link"] = i.Link.ToString(), ["server"] = i.ServerId.ToString(), ["creator"] = i.Creator,
            ["gametype"] = i.GameType, ["level"] = i.Level, ["password"] = i.PasswordHash != null,
            ["created_at"] = i.Created.ToString("o"), ["expires_at"] = i.Expires.ToString("o"),
            ["session_id"] = g?.SessionId?.ToString(), ["live"] = g != null,
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
            JObject body = new();
            if (method == "POST")
            {
                string text = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                try { if (!string.IsNullOrWhiteSpace(text)) body = JObject.Parse(text); }
                catch (Exception e) { throw new HttpError(400, $"bad JSON body: {e.Message}"); }
            }
            result = (method, seg) switch
            {
                ("POST", ["invites"]) => await Create(body, null),
                ("GET", ["invites", var c]) => Resolve(c, ctx.Request.QueryString["password"]),
                ("POST", ["invites", var c, "revoke"]) => Revoke(c, LoggedInUser((string?)body["user_id"])),
                _ => throw new HttpError(404, $"no route {method} /{string.Join('/', seg)}"),
            };
        }
        catch (HttpError e) { code = e.Code; result = new JObject { ["error"] = e.Message }; }
        catch (Exception e) { code = 500; result = new JObject { ["error"] = e.Message }; }
        await ControlApi.Respond(ctx, code, result);
    }

    static string LoggedInUser(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) throw new HttpError(400, "need user_id");
        // P0 auth: the id must belong to a peer that is logged in to the login service right now (LAN testbed only;
        // anyone who knows a logged-in id can act for it).
        lock (server.LoginService.PeersLock)
            if (!server.LoginService.Peers.Any(p => p.UserId?.ToString() == userId))
                throw new HttpError(403, $"{userId} is not logged in");
        return userId;
    }

    public static Task<JToken> CreateAsOperator(JObject b) { NeedEnabled(); return Create(b, (string?)b["user_id"] ?? "control"); }
    public static JToken RevokeAsOperator(string c) { NeedEnabled(); return Revoke(c, null); }

    // operatorUser != null: a control-API create (trusted loopback caller) -> no login check, no per-user cap.
    static async Task<JToken> Create(JObject b, string? operatorUser)
    {
        string user = operatorUser ?? LoggedInUser((string?)b["user_id"]);
        string gameType = (string?)b["gametype"] ?? throw new HttpError(400, "need gametype");
        string? level = (string?)b["level"], region = (string?)b["region"], password = (string?)b["password"];
        int expiresS = (int?)b["expires_s"] ?? DefaultExpiresS;
        if (expiresS <= 0 || expiresS > MaxExpiresS) throw new HttpError(400, $"expires_s must be 1..{MaxExpiresS}");
        byte limit = (byte?)b["player_limit"] ?? 16;
        var settings = b["settings"] is JObject so ? so.ToObject<ERGameServerStartSession.SessionSettings>() : null;
        long gameTypeSym = sym(gameType);
        long? levelSym = level == null ? null : sym(level), regionSym = region == null ? null : Regions.Symbol(region);

        await CreateLock.WaitAsync();
        try
        {
            if (operatorUser == null && ByCode.Values.Count(i => i.Creator == user) >= maxPerUser)
                throw new HttpError(429, $"{user} already has {maxPerUser} live invite(s)");
            var leased = ByCode.Values.Select(i => i.ServerId).ToHashSet();
            var free = server.ServerDBService.Registry.RegisteredGameServers.Values
                .Where(x => x.SessionId == null && !leased.Contains(x.ServerId)).OrderBy(x => x.ServerId).ToList();
            // A region pick prefers servers registered in it, then servers in "default" (an unconfigured server).
            var g = (regionSym == null ? free.FirstOrDefault()
                     : free.FirstOrDefault(x => x.RegionSymbol == regionSym) ?? free.FirstOrDefault(x => x.RegionSymbol == Regions.Default))
                ?? throw new HttpError(503, region == null ? "no free game server" : $"no free game server in region {region} or default");
            string code;
            do code = string.Concat(Enumerable.Range(0, CodeLength).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]));
            while (ByCode.ContainsKey(code) || Gone.ContainsKey(code));
            var now = DateTime.UtcNow;
            var inv = new Invite(code, Guid.NewGuid(), g.ServerId, user, gameType, level, gameTypeSym, levelSym, settings,
                limit, password == null ? null : Hash(code, password), now, now.AddSeconds(expiresS));
            ByCode[code] = inv;
            await g.StartSession(new EchoRelay.Core.Game.XPlatformId(), LobbyType.Private, new Guid(), gameTypeSym, levelSym, settings, limit);
            // A join by the link that finds the server idle (between sessions) starts the invite's own session.
            ControlApi.AliasLink(server, g, inv.Link, () => ByCode.ContainsKey(inv.Code)
                ? new GameServerRegistry.SessionStartParameters(LobbyType.Private, new Guid(), inv.GameTypeSym, inv.LevelSym, inv.Settings, inv.PlayerLimit)
                : null);
            log($"[INVITES] {code} by {user}{(operatorUser != null ? " (control API)" : "")}: game server {g.ServerId} session {g.SessionId} link {inv.Link} " +
                $"gametype {gameType} level {level ?? "(default)"} region {Regions.Name(g.RegionSymbol)}{(region != null ? $" (asked {region})" : "")} expires {inv.Expires:o}{(password != null ? " password" : "")}");
            return new JObject
            {
                ["code"] = code, ["link"] = inv.Link.ToString(), ["gametype"] = gameType, ["level"] = level,
                ["expires_at"] = inv.Expires.ToString("o"), ["password"] = password != null,
                ["region"] = Regions.Name(g.RegionSymbol),
            };
        }
        finally { CreateLock.Release(); }
    }

    static Invite Lookup(string c)
    {
        string code = c.ToUpperInvariant();
        if (!ByCode.TryGetValue(code, out var inv))
            throw Gone.TryGetValue(code, out var gone) ? new HttpError(410, $"invite {code} {gone.Why}")
                                                        : new HttpError(404, $"no invite {code}");
        if (inv.Expires <= DateTime.UtcNow) { Drop(inv, "expired"); throw new HttpError(410, $"invite {code} expired"); }
        return inv;
    }

    static JToken Resolve(string c, string? password)
    {
        var inv = Lookup(c);
        if (inv.PasswordHash != null &&
            (password == null || !CryptographicOperations.FixedTimeEquals(inv.PasswordHash, Hash(inv.Code, password))))
            throw new HttpError(403, password == null ? $"invite {inv.Code} needs a password" : "wrong password");
        log($"[INVITES] {inv.Code} resolved -> link {inv.Link}");
        return new JObject
        {
            ["code"] = inv.Code, ["link"] = inv.Link.ToString(), ["gametype"] = inv.GameType, ["level"] = inv.Level,
            ["expires_at"] = inv.Expires.ToString("o"),
        };
    }

    // user == null: operator revoke from the control API.
    static JToken Revoke(string c, string? user)
    {
        var inv = Lookup(c);
        if (user != null && user != inv.Creator) throw new HttpError(403, "only the creator can revoke");
        Drop(inv, "revoked");
        return new JObject { ["revoked"] = inv.Code };
    }

    static void Drop(Invite inv, string why)
    {
        if (!ByCode.TryRemove(inv.Code, out _)) return;
        Gone[inv.Code] = (why, DateTime.UtcNow);
        ControlApi.UnaliasLink(server, inv.Link);
        log($"[INVITES] {inv.Code} dropped ({why}); link {inv.Link} removed, game server {inv.ServerId} released");
    }

    static byte[] Hash(string code, string password) => SHA256.HashData(Encoding.UTF8.GetBytes($"{code}\n{password}"));

    // Lease keep-alive: an invite's session that ends is started again on the same server (same settings).
    static readonly ConcurrentDictionary<ulong, byte> Restarting = new();
    static void KeepSession(RegisteredGameServer g)
    {
        if (g.SessionId != null) return;
        var inv = ByCode.Values.FirstOrDefault(i => i.ServerId == g.ServerId);
        if (inv == null || !Restarting.TryAdd(g.ServerId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                if (g.SessionId != null || !ByCode.ContainsKey(inv.Code)) return;
                await g.StartSession(new EchoRelay.Core.Game.XPlatformId(), LobbyType.Private, new Guid(), inv.GameTypeSym, inv.LevelSym, inv.Settings, inv.PlayerLimit);
                log($"[INVITES] {inv.Code}: session restarted on game server {g.ServerId}, session id {g.SessionId}");
            }
            catch (Exception e) { log($"[INVITES] {inv.Code}: session restart failed: {e.Message}"); }
            finally { Restarting.TryRemove(g.ServerId, out _); }
        });
    }
}
