// Optional localhost-only HTTP control API for EchoRelay.Headless, for test tooling. Off unless
// --control-port N is given; binds http://127.0.0.1:N/ only. JSON in/out; errors are {"error": "..."} with 400/404/500.
//   GET  /health                          {"ok":true,"port":<services port>,"servers":n}
//   GET  /servers  |  /servers/{id}       id (decimal string, the host log's id) + id_hex (the game log's "registered as"),
//                                         peer, internal/external/port, region, version_lock, session_id (lower case,
//                                         dashes = the /join_session spelling), level/gametype {sym,name}, lobby_type,
//                                         started, locked, player_limit, players [{session, peer, user_id, name}]
//   GET  /peers                           connected peers per service [{peer, user_id, name, path}]
//   POST /servers/{id}/start-session      {"level":NAME|0xHEX, "gametype":"social_2.0", "lobby_type":"public|private",
//                                          "settings":{SessionSettings}, "player_limit":16} -> {"session_id":...}
//   POST /servers/{id}/kick               {"player_session":GUID|"all"} -> {"kicked":[...]} (ERGameServerPlayersRejected)
//   POST /servers/{id}/accept             {"player_sessions":[GUID,...]}  (ERGameServerPlayersAccepted)
//   POST /servers/{id}/lock               {"locked":true|false}  (registry state only: matching skips locked sessions)
//   POST /lobby-settings                  {"json":{...}, "target":"clients|servers|all|peer", "peer":"ADDR:PORT"}
//                                         sends SNSLobbySettingsResponse now; clients = login + matching peers.
//   POST /push                            {"sym":NAME|0xHEX, "hex":"..." | "json":{...}, "compression":"none|zlib|zstd",
//                                          "null_terminated":true, "prefix_hex":"", "target", "peer"} sends ANY message
//                                         now (early-quit tests); target/peer as /lobby-settings.
//                                         The payload is prefix_hex + (hex | the JSON framed as Core's StreamJSON).
//   GET  /links                           [{link, server, session_id, live}]
//   POST /links                           {"server":{id}, "link":GUID (optional, new if absent)} -> {"link":...}: a fixed
//                                         join id (a deep-link stand-in). The link GUID is added as an extra key of the
//                                         registry's session lookup, so LobbyJoinSessionRequestv7(lobby=link), e.g. a
//                                         client started with -lobbyid <link>, matches that server's CURRENT session, and
//                                         keeps matching across session restarts. No Core patch.
//   POST /links/{link}/remove             drops the alias
//   GET  /invites                         [{code, link, server, creator, gametype, level, expires_at, live, ...}]: the
//                                         invite codes of Invites.cs (their routes are on the
//                                         services port; mirrored here for test tooling)
//   POST /invites                         operator create (no login check, no cap): same body/answer as the services
//                                         route; creator = body user_id or "control"
//   POST /invites/{code}/revoke           operator revoke
//   GET  /early-quit/stats                accounts per state, locked, active (an entry in 30 d) + shares, 30-d entry
//                                         classes and would-flags, pending removals (host --early-quit true)
//   GET  /early-quit/{user}               v2 state: history (last 50), snapshot, lockout, exempt, pending
//   POST /early-quit/{user}/{action}      action = quit | complete (synthetic rows) | clear | expire | lockout
//                                         {"seconds": N} | forgive {"session"} | exempt {"on": bool}: updates the v2
//                                         store + profile mirror, pushes an Update to the user's login peers (EarlyQuit.cs)
//   GET  /accounts/{user}/developer       the server profile's developer block, JSON key `dev` (null = none)
//   POST /accounts/{user}/developer       {"disable_afk_timeout":true|false}: sets
//                                         profile.server.dev {disable_afk_timeout, xplatformid = user}; false
//                                         removes the block. The game server reads it at the next join, so a -noovr
//                                         client (no headset heartbeat) is not kicked for inactivity.
// {id} = decimal, 0x hex, or a session GUID. Every action logs a "[CONTROL] ..." line in the host log.
using System.Net;
using System.Text;
using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services;
using EchoRelay.Core.Server.Services.ServerDB;
using EchoRelay.Core.Server.Storage.Types;
using Newtonsoft.Json.Linq;

static class ControlApi
{
    public static void Start(Server server, int port, Func<string, long> sym, Action<string> log)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        log($"[CONTROL] listening on http://127.0.0.1:{port}/ (localhost only)");
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { break; }
                _ = Task.Run(() => Handle(server, ctx, sym, log));
            }
        });
    }

    internal sealed class HttpError : Exception { public int Code; public HttpError(int code, string msg) : base(msg) { Code = code; } }

    static async Task Handle(Server server, HttpListenerContext ctx, Func<string, long> sym, Action<string> log)
    {
        int code = 200;
        JToken result;
        try
        {
            if (!IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address)) throw new HttpError(403, "localhost only");
            string method = ctx.Request.HttpMethod, path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/');
            JObject body = new();
            if (method == "POST")
            {
                string text = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                try { if (!string.IsNullOrWhiteSpace(text)) body = JObject.Parse(text); }
                catch (Exception e) { throw new HttpError(400, $"bad JSON body: {e.Message}"); }
            }
            string[] seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            result = (method, seg) switch
            {
                ("GET", ["health"]) => new JObject { ["ok"] = true, ["port"] = server.Settings.Port,
                                                     ["servers"] = server.ServerDBService.Registry.RegisteredGameServers.Count },
                ("GET", ["servers"]) => new JArray(await Task.WhenAll(server.ServerDBService.Registry.RegisteredGameServers.Values
                                            .OrderBy(g => g.ServerId).Select(g => Describe(server, g)))),
                ("GET", ["servers", var id]) => await Describe(server, Find(server, id)),
                ("GET", ["peers"]) => Peers(server),
                ("POST", ["servers", var id, "start-session"]) => await StartSession(Find(server, id), body, sym, log),
                ("POST", ["servers", var id, "kick"]) => await Kick(Find(server, id), body, log),
                ("POST", ["servers", var id, "accept"]) => await Accept(Find(server, id), body, log),
                ("POST", ["servers", var id, "lock"]) => Lock(Find(server, id), body, log),
                ("POST", ["lobby-settings"]) => await LobbySettings(server, body, log),
                ("POST", ["push"]) => await Push(server, body, sym, log),
                ("GET", ["links"]) => ListLinks(server),
                ("POST", ["links"]) => AddLink(server, body, log),
                ("POST", ["links", var l, "remove"]) => RemoveLink(server, l, log),
                ("GET", ["invites"]) => Invites.List(),
                ("POST", ["invites"]) => await Invites.CreateAsOperator(body),
                ("POST", ["invites", var c, "revoke"]) => Invites.RevokeAsOperator(c),
                ("GET", ["early-quit", "stats"]) => EarlyQuit.On ? EarlyQuit.Stats() : throw new HttpError(409, "early quit is off (host --early-quit true)"),
                ("GET", ["early-quit", var u]) => await EarlyQuitCall(u, "show", body),
                ("POST", ["early-quit", var u, var act]) => await EarlyQuitCall(u, act, body),
                ("GET", ["accounts", var u, "developer"]) => Developer(server, u, null, log),
                ("POST", ["accounts", var u, "developer"]) => Developer(server, u, body, log),
                _ => throw new HttpError(404, $"no route {method} {path}"),
            };
        }
        catch (HttpError e) { code = e.Code; result = new JObject { ["error"] = e.Message }; }
        catch (Exception e) { code = 500; result = new JObject { ["error"] = e.ToString() }; }
        await Respond(ctx, code, result);
    }

    static async Task<JToken> EarlyQuitCall(string user, string action, JObject body)
    {
        var id = EchoRelay.Core.Game.XPlatformId.Parse(user) ?? throw new HttpError(400, $"bad user id {user} (e.g. DMO-123)");
        if (!EarlyQuit.On) throw new HttpError(409, "early quit is off (host --early-quit true)");
        try { return await EarlyQuit.Apply(id, action, body); }
        catch (KeyNotFoundException e) { throw new HttpError(404, e.Message); }
        catch (ArgumentException e) { throw new HttpError(400, e.Message); }
    }

    static JToken Developer(Server server, string user, JObject? body, Action<string> log)
    {
        var id = EchoRelay.Core.Game.XPlatformId.Parse(user) ?? throw new HttpError(400, $"bad user id {user} (e.g. DMO-123)");
        var a = server.Storage.Accounts.Get(id) ?? throw new HttpError(404, $"no account {user}");
        if (body != null)
        {
            if (body["disable_afk_timeout"]?.Type != JTokenType.Boolean) throw new HttpError(400, "body: {\"disable_afk_timeout\": true|false}");
            a.Profile.Server.Developer = (bool)body["disable_afk_timeout"]!
                ? new AccountResource.AccountServerProfile.DeveloperSettings(id, true) : null;
            server.Storage.Accounts.Set(a);
            log($"[CONTROL] {id} developer: {(a.Profile.Server.Developer == null ? "removed" : "disable_afk_timeout on")}");
        }
        return new JObject { ["user_id"] = id.ToString(),
                             ["developer"] = a.Profile.Server.Developer == null ? JValue.CreateNull() : JObject.FromObject(a.Profile.Server.Developer) };
    }

    internal static async Task Respond(HttpListenerContext ctx, int code, JToken result)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(result.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception) { }
    }

    internal static RegisteredGameServer Find(Server server, string id)
    {
        var reg = server.ServerDBService.Registry;
        RegisteredGameServer? g = null;
        if (Guid.TryParse(id, out Guid sid)) g = reg.GetGameServer(sid);
        else if (id.StartsWith("0x") && ulong.TryParse(id[2..], System.Globalization.NumberStyles.HexNumber, null, out ulong h)) g = reg.GetGameServer(h);
        else if (ulong.TryParse(id, out ulong d)) g = reg.GetGameServer(d);
        else throw new HttpError(400, $"bad server id {id} (decimal, 0x hex or session GUID)");
        return g ?? throw new HttpError(404, $"no registered game server {id}");
    }

    static JObject SymJ(Server server, long? s) => s == null ? new JObject { ["sym"] = null, ["name"] = null }
        : new JObject { ["sym"] = $"0x{s.Value:x16}", ["name"] = server.SymbolCache.GetName(s.Value) };

    static string Ep(Peer? p) => p == null ? "" : $"{p.Address}:{p.Port}";

    static async Task<JToken> Describe(Server server, RegisteredGameServer g)
    {
        var players = new JArray((await g.GetPlayers()).Select(p => new JObject
        {
            ["session"] = p.PlayerSession.ToString(), ["peer"] = Ep(p.Peer),
            ["user_id"] = p.Peer?.UserId?.ToString(), ["name"] = p.Peer?.UserDisplayName,
        }));
        return new JObject
        {
            ["id"] = g.ServerId.ToString(), ["id_hex"] = $"0x{g.ServerId:x16}", ["peer"] = Ep(g.Peer),
            ["internal"] = g.InternalAddress.ToString(), ["external"] = g.ExternalAddress.ToString(), ["port"] = g.Port,
            ["region"] = SymJ(server, g.RegionSymbol), ["version_lock"] = $"0x{g.VersionLock:x16}",
            ["session_id"] = g.SessionId?.ToString(), ["level"] = SymJ(server, g.SessionLevelSymbol),
            ["gametype"] = SymJ(server, g.SessionGameTypeSymbol), ["lobby_type"] = g.SessionLobbyType.ToString().ToLowerInvariant(),
            ["started"] = g.SessionStarted, ["locked"] = g.SessionLocked, ["player_limit"] = g.SessionPlayerLimits.TotalPlayerLimit,
            ["player_count"] = g.SessionPlayerCount, ["players"] = players,
        };
    }

    static JObject Peers(Server server)
    {
        var o = new JObject();
        foreach (Service svc in new Service[] { server.LoginService, server.ConfigService, server.MatchingService,
                                                server.ServerDBService, server.TransactionService })
            lock (svc.PeersLock)
                o[svc.Name] = new JArray(svc.Peers.Select(p => new JObject
                {
                    ["peer"] = Ep(p), ["user_id"] = p.UserId?.ToString(), ["name"] = p.UserDisplayName,
                    ["path"] = p.RequestUri?.AbsolutePath,
                }));
        return o;
    }

    static async Task<JToken> StartSession(RegisteredGameServer g, JObject b, Func<string, long> sym, Action<string> log)
    {
        string? levelName = (string?)b["level"];
        long? level = levelName == null ? null : sym(levelName);
        long gameType = sym((string?)b["gametype"] ?? "social_2.0");
        var lobbyType = ((string?)b["lobby_type"] ?? "public") == "private"
            ? ERGameServerStartSession.LobbyType.Private : ERGameServerStartSession.LobbyType.Public;
        var settings = b["settings"] is JObject so ? so.ToObject<ERGameServerStartSession.SessionSettings>() : null;
        byte limit = (byte?)b["player_limit"] ?? 16;
        log($"[CONTROL] start-session on game server {g.ServerId}: level {levelName}=0x{level ?? 0:x16} gametype 0x{gameType:x16} lobby {lobbyType}");
        await g.StartSession(new EchoRelay.Core.Game.XPlatformId(), lobbyType, new Guid(), gameType, level, settings, limit);
        log($"[CONTROL] start-session sent to game server {g.ServerId}, session id {g.SessionId}");
        return new JObject { ["id"] = g.ServerId.ToString(), ["session_id"] = g.SessionId?.ToString() };
    }

    static async Task<JToken> Kick(RegisteredGameServer g, JObject b, Action<string> log)
    {
        string who = (string?)b["player_session"] ?? throw new HttpError(400, "need player_session (GUID or \"all\")");
        Guid[] targets = who == "all" ? (await g.GetPlayers()).Select(p => p.PlayerSession).ToArray()
            : new[] { Guid.TryParse(who, out Guid x) ? x : throw new HttpError(400, $"bad player_session {who}") };
        foreach (Guid t in targets)
        {
            log($"[CONTROL] kicking player session {t} on game server {g.ServerId}");
            await g.KickPlayer(t);
        }
        return new JObject { ["kicked"] = new JArray(targets.Select(t => t.ToString())) };
    }

    static async Task<JToken> Accept(RegisteredGameServer g, JObject b, Action<string> log)
    {
        var ids = (b["player_sessions"] as JArray ?? throw new HttpError(400, "need player_sessions [GUID,...]"))
            .Select(t => Guid.TryParse((string?)t, out Guid x) ? x : throw new HttpError(400, $"bad GUID {t}")).ToArray();
        log($"[CONTROL] accepting {ids.Length} player session(s) on game server {g.ServerId}");
        await g.AddPlayers(ids);
        return new JObject { ["accepted"] = new JArray(ids.Select(t => t.ToString())) };
    }

    static JToken Lock(RegisteredGameServer g, JObject b, Action<string> log)
    {
        bool locked = (bool?)b["locked"] ?? throw new HttpError(400, "need locked true|false");
        g.SetLockedStatus(locked);
        log($"[CONTROL] game server {g.ServerId} locked={locked}");
        return new JObject { ["id"] = g.ServerId.ToString(), ["locked"] = g.SessionLocked };
    }

    static async Task<JToken> LobbySettings(Server server, JObject b, Action<string> log)
    {
        var json = b["json"] as JObject ?? throw new HttpError(400, "need json {...}");
        var sent = await SendTo(server, b, () => new SNSLobbySettingsResponse(json));
        log($"[CONTROL] SNSLobbySettingsResponse sent to {sent.Count} peer(s) ({(string?)b["target"] ?? "clients"}): {json.ToString(Newtonsoft.Json.Formatting.None)}");
        return new JObject { ["sent"] = sent };
    }

    static async Task<JToken> Push(Server server, JObject b, Func<string, long> sym, Action<string> log)
    {
        string name = (string?)b["sym"] ?? throw new HttpError(400, "need sym (name or 0xHEX)");
        long symbol = sym(name);
        byte[] payload;
        try
        {
            var io = new EchoRelay.Core.Utils.StreamIO(EchoRelay.Core.Utils.ByteOrder.LittleEndian, EchoRelay.Core.Utils.StreamMode.Write);
            string prefix = (string?)b["prefix_hex"] ?? "";
            if (prefix != "") io.Write(Convert.FromHexString(prefix));
            if (b["hex"] != null) io.Write(Convert.FromHexString((string)b["hex"]!));
            else if (b["json"] is JToken j)
            {
                var mode = ((string?)b["compression"] ?? "none") switch
                {
                    "none" => EchoRelay.Core.Utils.JSONCompressionMode.None,
                    "zlib" => EchoRelay.Core.Utils.JSONCompressionMode.Zlib,
                    "zstd" => EchoRelay.Core.Utils.JSONCompressionMode.Zstd,
                    var m => throw new HttpError(400, $"compression {m}: none|zlib|zstd"),
                };
                io.WriteJSON(j, (bool?)b["null_terminated"] ?? true, mode);
            }
            else if (prefix == "") throw new HttpError(400, "need hex, json or prefix_hex");
            payload = io.ToArray();
            io.Close();
        }
        catch (FormatException e) { throw new HttpError(400, $"bad hex: {e.Message}"); }
        var sent = await SendTo(server, b, () => new EchoRelay.Core.Server.Messages.UnimplementedMessage(symbol) { Data = payload });
        log($"[CONTROL] push 0x{symbol:x16} ({server.SymbolCache.GetName(symbol) ?? name}) {payload.Length} B to {sent.Count} peer(s) ({(string?)b["target"] ?? "clients"}): {Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 64)))}");
        return new JObject { ["sent"] = sent, ["sym"] = $"0x{symbol:x16}", ["bytes"] = payload.Length, ["hex"] = Convert.ToHexString(payload).ToLowerInvariant() };
    }

    // "target": clients (login + matching peers, default) | servers | all | peer (+ "peer": ADDR:PORT, any service).
    static async Task<JArray> SendTo(Server server, JObject b, Func<EchoRelay.Core.Server.Messages.Message> make)
    {
        string target = (string?)b["target"] ?? "clients", peer = (string?)b["peer"] ?? "";
        var services = target switch
        {
            "clients" => new Service[] { server.LoginService, server.MatchingService },
            "login" => new Service[] { server.LoginService },
            "matching" => new Service[] { server.MatchingService },
            "servers" => new Service[] { server.ServerDBService },
            "all" or "peer" => new Service[] { server.LoginService, server.MatchingService, server.ServerDBService },
            _ => throw new HttpError(400, "target = clients|login|matching|servers|all|peer"),
        };
        if (target == "peer" && peer == "") throw new HttpError(400, "target peer needs \"peer\": \"ADDR:PORT\"");
        var sent = new JArray();
        foreach (Service svc in services)
        {
            Peer[] peers;
            lock (svc.PeersLock) peers = svc.Peers.Where(p => target != "peer" || Ep(p) == peer).ToArray();
            foreach (Peer p in peers)
            {
                await p.Send(make());
                sent.Add($"{svc.Name} {Ep(p)}");
            }
        }
        return sent;
    }

    // Shared with Invites.cs. Links are Core session aliases (GameServerRegistry.SessionAliases): they resolve to the
    // game server by server id, across its sessions, and are not removed when a session ends (the session's id is).
    internal static void AliasLink(Server server, RegisteredGameServer g, Guid link,
        Func<GameServerRegistry.SessionStartParameters?>? idleStart = null)
    {
        if (!server.ServerDBService.Registry.AddSessionAlias(link, g.ServerId, idleStart))
            throw new HttpError(400, $"{link} is a live session id of another game server");
    }
    internal static bool UnaliasLink(Server server, Guid link) => server.ServerDBService.Registry.RemoveSessionAlias(link);

    static JToken ListLinks(Server server)
    {
        var reg = server.ServerDBService.Registry;
        return new JArray(reg.SessionAliases.OrderBy(kv => kv.Key).Select(kv =>
        {
            var g = reg.GetGameServer(kv.Value.ServerId);
            return new JObject
            {
                ["link"] = kv.Key.ToString(), ["server"] = kv.Value.ServerId.ToString(),
                ["session_id"] = g?.SessionId?.ToString(), ["live"] = g != null,
            };
        }));
    }

    static JToken AddLink(Server server, JObject b, Action<string> log)
    {
        var g = Find(server, (string?)b["server"] ?? throw new HttpError(400, "need server {id}"));
        string? given = (string?)b["link"];
        Guid link = given == null ? Guid.NewGuid()
            : Guid.TryParse(given, out Guid x) ? x : throw new HttpError(400, $"bad link GUID {given}");
        AliasLink(server, g, link);
        log($"[CONTROL] link {link} -> game server {g.ServerId} (current session {g.SessionId})");
        return new JObject { ["link"] = link.ToString(), ["server"] = g.ServerId.ToString(), ["session_id"] = g.SessionId?.ToString() };
    }

    static JToken RemoveLink(Server server, string l, Action<string> log)
    {
        Guid link = Guid.TryParse(l, out Guid x) ? x : throw new HttpError(400, $"bad link GUID {l}");
        if (!UnaliasLink(server, link)) throw new HttpError(404, $"no link {link}");
        log($"[CONTROL] link {link} removed");
        return new JObject { ["removed"] = link.ToString() };
    }
}
