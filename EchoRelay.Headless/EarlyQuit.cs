// The services half of Echo VR's early-quit system, which the shipped client only displays (docs/protocol/early-quit.md).
// Off unless --early-quit true.
//   * State: the v2 store (EarlyQuitStore.cs, <storage-dir>/earlyquit_v2/<user>.json): history, lockout expiry, last
//     penaltyts, exempt. Policy = EarlyQuitPolicy.cs (--early-quit-policy FILE.json, else its defaults). The SERVER
//     PROFILE only mirrors what the client reads: "earlyquit" {penaltyts, numearlyquits (lifetime quits),
//     numsteadymatches / numsteadyearlyquits (completes / quits in the 20-match window), penaltylevel (locked: the rung
//     that lockout used + 1, else 0), steadyplayerlevel} plus "earlyquit_lockout" (Unix s; the client never reads it).
//     An account with only the old profile block starts with an empty v2 history (its penaltyts and a running
//     lockout carry over; logged once).
//   * Login: after LoginSettings the client gets SNSEarlyQuitConfig (1 byte + u32 + zstd JSON), FeatureFlags
//     0x02 (bit 1; stock clients keep & 0xdd) and an Update. Every Update bumps penaltyts (EqState.NextPenaltyTs, saved
//     first): the client ignores a record whose ts is not newer. The store's per-user SemaphoreSlim serialises every
//     state change and its sends; each peer send has its own try / catch.
//   * --early-quit-mode shadow (default): detect, record, log ("shadow would lockout / refuse / pulse"), push the login
//     sequence and Updates; no pulses, no refusals, and a would-be lockout is only a "lockout" row (reason shadow).
//     enforce: the lockout is real, the gate refuses and the notice pulses.
//   * Detection (--early-quit-detect true): public arena / combat sessions only. Accept time and TeamIndex per
//     (server, session, user) at OnPlayersAdded (the team comes from the user's last matching request, seen by the
//     gate: Core clears the matching session before the accept); login-socket close times per user. A removal waits
//     the grace, then EqClassifier.Classify on grace expiry / EndSession / server unregister; players still seated at
//     EndSession are removals at that moment (complete). Pending removals live in memory (a restart forgives them).
//     A re-add to the same session while a removal is pending cancels it (one neutral "rejoin" row). Per-session maps
//     are dropped at session end / unregister / a grace that finds the session gone; seats are kept only with detect.
//     Completion is inferred from the session end (end_burst / grace, policy-tunable), unverified until device test
//     T11: enforce only after T11.
//   * Gate (Core MatchingService.MatchingGate): public arena / combat FIND / JOIN by lobby id (registry lookup) / CREATE
//     (a public create would bypass the lockout) are refused (ServerIsLocked + "early quit lockout mm:ss") while locked
//     (clock tolerance) or on the provisional hold (a pending non-spectator removal could reach lockout_at). Spectator /
//     moderator requests, social, private and AI (gametype or ai_difficulty) pass. It fails open on any error.
//   * Every Core-invoked handler catches and logs ("[EARLYQUIT] <handler> failed") and never throws into Core.
//   * Pulse (enforce, no --early-quit-flags): Flags 0x22 + Update, later 0x02, plus a "pulse" row. The podium picks
//     the notice only when its home page is ENABLED (device T2, 2026-10-22: a pulse while home is up shows nothing; the
//     home page is enabled during the lobby load). So a pulse is sent only while the user is ARRIVING in a social lobby
//     (Social_2.0 / _Private accept, until the client's LOAD_STATS remote log for it): at the accept when a notice is
//     owed (a quit newer than the last pulse, or a lockout not yet shown to that login peer), or when a quit is counted
//     or a lockout set during the load. Bit 5 holds until pulse_hold after LOAD_STATS (at most arrival_max after the
//     accept). Outside an arrival nothing is pulsed and the notice stays owed to the next arrival.
//     --early-quit-flags 0xNN fixes one value instead (older behaviour: login, and every change while locked out).
//   * Expiry: the client never leaves the LOCKOUT NOTICE by itself, so when a lockout runs out the host
//     pushes FeatureFlags without bit 5 and an Update with no expiry (action "expire").
//   * Control API (Control.cs): GET /early-quit/stats, /early-quit/{user}; POST /early-quit/{user}/quit|complete
//     (synthetic rows) |clear|expire|forgive {"session"}|exempt {"on"}|lockout {"seconds": N}. Unknown account = 404
//     (stats excepted). A forgive of the quit that set the running lockout ends it (expire path) if nothing else holds.
//   Update +0x20 (lockout expiry) is ignored by stock clients; a client patch can make the podium show it.
using System.Collections.Concurrent;
using EchoRelay.Core.Game;
using EchoRelay.Core.Server;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Matching;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services;
using EchoRelay.Core.Server.Services.Matching;
using EchoRelay.Core.Server.Services.ServerDB;
using EchoRelay.Core.Server.Storage.Types;
using EchoRelay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static class EarlyQuit
{
    const long SymConfig = 0x080495a43a6b7251, SymFlags = unchecked((long)0xd9a955895caccac3UL),
               SymUpdate = 0x1f81b54c35788eaa;
    static Server server = null!;
    static Action<string> log = _ => { };
    static JObject config = null!;
    static EqPolicy policy = null!;
    static EqStore store = null!;
    static string storeDir = null!;
    static bool enforce, detect;
    const byte FlagsRest = 0x02, FlagsPulse = 0x22;
    static byte? flagsOverride;   // --early-quit-flags: one fixed value instead of the rest / pulse schedule
    static HashSet<long> publicTypes = null!;
    static long socialType, socialPrivateType;   // both lobbies have the podium (device T2: a private lobby got no pulse)

    sealed class Pending : EqPending { public required XPlatformId Id; }
    // (server id, session id) -> that public session's removals, resolved or not (Classify reads them all)
    static readonly ConcurrentDictionary<(ulong, Guid), List<Pending>> removals = new();
    // (server id, session id, user id) -> accept time + team, from OnPlayersAdded
    static readonly ConcurrentDictionary<(ulong, Guid, string), (long At, int Team, XPlatformId Id)> seats = new();
    // user id -> TeamIndex of the last matching request; user id -> last login-socket close (Unix s)
    static readonly ConcurrentDictionary<string, int> lastTeam = new();
    static readonly ConcurrentDictionary<string, long> loginClosed = new();
    // login peer -> lockout expiry pulsed to it ("shown this login"); user id -> scheduled expiry push; pulse generation
    static readonly ConcurrentDictionary<Peer, long> shown = new();
    static readonly ConcurrentDictionary<string, long> expiring = new();
    static readonly ConcurrentDictionary<string, long> pulseGen = new();
    static readonly ConcurrentDictionary<string, long> arriving = new();   // user id -> social accept, until its LOAD_STATS
    static readonly ConcurrentDictionary<string, byte> migrated = new();
    static int teamMissLogged;

    public static readonly JObject DefaultConfig = JObject.Parse(@"{
      ""penalty_levels"": [
        {""penalty_level"":0,""min_earlyquits"":0,""max_earlyquits"":4,""mm_lockout_sec"":0,""spawnlock"":0,""autoreport"":0,""cnvpe_reactivated"":0},
        {""penalty_level"":1,""min_earlyquits"":5,""max_earlyquits"":5,""mm_lockout_sec"":300,""spawnlock"":1,""autoreport"":0,""cnvpe_reactivated"":0},
        {""penalty_level"":2,""min_earlyquits"":6,""max_earlyquits"":6,""mm_lockout_sec"":900,""spawnlock"":1,""autoreport"":0,""cnvpe_reactivated"":0},
        {""penalty_level"":3,""min_earlyquits"":7,""max_earlyquits"":7,""mm_lockout_sec"":3600,""spawnlock"":1,""autoreport"":0,""cnvpe_reactivated"":0},
        {""penalty_level"":4,""min_earlyquits"":8,""max_earlyquits"":8,""mm_lockout_sec"":21600,""spawnlock"":1,""autoreport"":0,""cnvpe_reactivated"":0},
        {""penalty_level"":5,""min_earlyquits"":9,""max_earlyquits"":4294967295,""mm_lockout_sec"":86400,""spawnlock"":1,""autoreport"":0,""cnvpe_reactivated"":0}],
      ""steady_player_levels"": [
        {""steady_player_level"":0,""min_num_matches"":10,""min_steady_ratio"":0.80},
        {""steady_player_level"":1,""min_num_matches"":10,""min_steady_ratio"":0.90},
        {""steady_player_level"":2,""min_num_matches"":20,""min_steady_ratio"":0.95},
        {""steady_player_level"":3,""min_num_matches"":20,""min_steady_ratio"":0.98}]}");
    // penalty_levels mirror the lockout ladder; steady_player_levels: 4 levels over a 20-match window;
    // level 0 = Steadfast (>= 80 %). Below it = none, which the Update sends as 0 too (EqSnapshot.SteadyLevelWire).

    public static bool On => store != null;

    public static void Enable(Server srv, Func<string, long> sym, Action<string> logger, string? configFile, byte? featureFlags,
                              bool detect, string mode, string? policyFile, int? grace, string dir)
    {
        enforce = mode switch { "enforce" => true, "shadow" => false,
                                _ => throw new ArgumentException($"--early-quit-mode {mode}: shadow|enforce") };
        server = srv; log = logger; flagsOverride = featureFlags; EarlyQuit.detect = detect;
        policy = EqPolicy.Load(policyFile);
        if (grace is int g) policy.GraceSec = g;
        config = configFile == null ? (JObject)DefaultConfig.DeepClone() : JObject.Parse(File.ReadAllText(configFile));
        if (configFile == null)   // the steady levels the Update reports come from the policy
            config["steady_player_levels"] = new JArray(policy.SteadyLevels.Select(l => new JObject
                { ["steady_player_level"] = l.Level, ["min_num_matches"] = l.MinMatches, ["min_steady_ratio"] = l.MinRatio }));
        publicTypes = new HashSet<long> { sym("echo_arena"), sym("echo_combat") };
        socialType = sym("social_2.0"); socialPrivateType = sym("social_2.0_private");
        storeDir = dir; store = new EqStore(dir, policy.MaxAgeDays, log);
        server.MatchingService.MatchingGate = Gate;
        server.OnServicePacketSent += (svc, peer, packet) => Guard("OnPacketSent", () =>
        {
            if (svc != server.LoginService || !packet.Any(m => m is EchoRelay.Core.Server.Messages.Login.LoginSettings)) return;
            _ = Task.Run(async () => { try { await SendAll(peer); } catch (Exception e) { log($"[EARLYQUIT] login push failed: {e.Message}"); } });
        });
        server.OnServicePacketReceived += (svc, peer, packet) => Guard("OnPacketReceived", () =>
        {
            if (svc != server.LoginService || peer.UserId is not XPlatformId u) return;
            foreach (var m in packet.OfType<EchoRelay.Core.Server.Messages.Login.RemoteLogSetv3>())
                foreach (var l in m.Logs ?? Array.Empty<string>())
                    if (l.Contains("\"LOAD_STATS\"") && LoadedSocial(l)) Loaded(u);
        });
        server.OnServicePeerDisconnected += (svc, peer) => Guard("OnPeerDisconnected", () =>
        {
            if (svc != server.LoginService) return;
            shown.TryRemove(peer, out _);
            if (peer.UserId is XPlatformId u) loginClosed[u.ToString()] = Now;
            if (loginClosed.Count > 4096)   // only the last grace + exit window matters: drop day-old closes
                foreach (var kv in loginClosed.Where(kv => kv.Value < Now - 86400).ToArray()) loginClosed.TryRemove(kv.Key, out _);
        });
        server.ServerDBService.Registry.OnGameServerRegistered += gs => Guard("OnGameServerRegistered", () =>
        {
            gs.OnPlayersAdded += (g, ps) => Guard("OnPlayersAdded", () => OnPlayersAdded(g, ps));
            if (!detect) return;
            gs.OnPlayerRemoved += (g, s, p) => Guard("OnPlayerRemoved", () => OnPlayerRemoved(g, s, p));
            gs.OnSessionStateChanged += g => Guard("OnSessionStateChanged", () => OnSessionChanged(g));
        });
        if (detect) server.ServerDBService.Registry.OnGameServerUnregistered += gs => Guard("OnGameServerUnregistered", () => OnServerUnregistered(gs));
        log($"[EARLYQUIT] on: mode {mode}, flags {(flagsOverride is byte fo ? $"0x{fo:x2} (fixed)" : $"0x{FlagsRest:x2} / pulse 0x{FlagsPulse:x2} {policy.PulseHoldSec} s")}, detect {detect} (grace {policy.GraceSec} s), window {policy.WindowMatches} enable {policy.EnableAt} lockout {policy.LockoutAt}, policy {policyFile ?? "defaults"}, store {dir}");
    }

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Core raises these on a peer's receive loop: a throw would close that socket (a game server: every match on it)
    static void Guard(string handler, Action a)
    {
        try { a(); }
        catch (Exception e) { try { log($"[EARLYQUIT] {handler} failed: {e.GetType().Name}: {e.Message}"); } catch { } }
    }

    // a Task.Delay that never throws (negative / > ~24.8 d values clamp)
    static Task Sleep(double sec) => Task.Delay(TimeSpan.FromSeconds(Math.Clamp(sec, 0, int.MaxValue / 1000.0)));

    static async Task<T> Locked<T>(XPlatformId user, Func<Task<T>> f)
    {
        var sem = store.Lock(user.ToString());
        await sem.WaitAsync();
        try { return await f(); } finally { sem.Release(); }
    }

    // ---- state
    static AccountResource? Account(XPlatformId user) => server.Storage.Accounts.Get(user);

    // a user with no v2 file but an old profile block: empty history, its penaltyts and a running lockout carry over
    static EqState LoadState(XPlatformId user)
    {
        string id = user.ToString();
        bool fresh = !File.Exists(store.PathOf(id));
        var st = store.Load(id);
        if (Account(user) is not AccountResource a ||
            !a.Profile.Server.AdditionalData.TryGetValue("earlyquit", out var t) || t is not JObject old) return st;
        // always (also after a corrupt file went to .bad): penaltyts never goes below what the mirror last carried
        st.PenaltyTsSent = Math.Max(st.PenaltyTsSent, old["penaltyts"]?.Type == JTokenType.Integer ? (long)old["penaltyts"]! : -1);
        if (!fresh) return st;
        if (a.Profile.Server.AdditionalData.TryGetValue("earlyquit_lockout", out var lt) && lt.Type == JTokenType.Integer &&
            (long)lt > Now) st.LockoutExpiry = (long)lt;
        if (migrated.TryAdd(id, 0))
            log($"[EARLYQUIT] {id}: old profile block {old.ToString(Formatting.None)} -> v2 store, empty history (penaltyts {st.PenaltyTsSent}, lockout {st.LockoutExpiry})");
        return st;
    }

    // enforce: the real lockout. shadow: also the newest would-be lockout row, so standing and the gate log what would be
    static long ViewExpiry(EqState st)
    {
        if (enforce) return st.LockoutExpiry;
        var row = EqStanding.Effective(st.History).LastOrDefault(e => e.Cls == EqClass.Lockout);
        return Math.Max(st.LockoutExpiry, row?.Expiry ?? -1);
    }

    static EqSnapshot Snap(EqState st, long now) => EqStanding.Compute(st.History, now, ViewExpiry(st), policy);

    static int PendingCount(string id) =>
        removals.Values.Sum(l => { lock (l) return EqPendingOps.HoldCount(l, id); });

    // the six client keys; bumps penaltyts (the caller saves before sending)
    static JObject Wire(EqState st, long now)
    {
        var s = EqStanding.Compute(st.History, now, st.LockoutExpiry, policy);
        int level = 0;
        if (st.LockoutExpiry > now)
        {
            // the rung that lockout used: history before its row, minus the quit that set it
            int row = st.History.FindLastIndex(e => e.Cls == EqClass.Lockout);
            var before = (row < 0 ? st.History : st.History.Take(row)).ToList();
            int q = before.FindLastIndex(e => e.Cls == EqClass.Quit);
            if (q >= 0) before.RemoveAt(q);
            level = Math.Min(EqStanding.Compute(before, now, -1, policy).Rung + 1, policy.LadderSec.Length);
        }
        return new JObject { ["penaltyts"] = st.NextPenaltyTs(now),
                             ["numearlyquits"] = EqStanding.Effective(st.History).Count(e => e.Cls == EqClass.Quit),
                             ["numsteadymatches"] = s.CompletedInWindow, ["numsteadyearlyquits"] = s.Wq,
                             ["penaltylevel"] = level, ["steadyplayerlevel"] = s.SteadyLevelWire };
    }

    // Core has no lock around account / profile updates (LoginService profile merge, Storage.Accounts, LRUFileCache), so:
    // write only on a change, on a fresh copy swapped in by one reference assignment (never mutate the shared dictionary).
    // Residual race: a Core profile merge between our Get and Set can still drop this write until the next Push.
    static void Mirror(XPlatformId user, JObject w, long lockout)
    {
        if (Account(user) is not AccountResource a) return;
        var cur = a.Profile.Server.AdditionalData;
        JToken lo = lockout;
        if (cur.TryGetValue("earlyquit", out var o) && JToken.DeepEquals(o, w) &&
            cur.TryGetValue("earlyquit_lockout", out var l) && JToken.DeepEquals(l, lo)) return;
        var copy = new Dictionary<string, JToken>(cur) { ["earlyquit"] = w.DeepClone(), ["earlyquit_lockout"] = lo };
        a.Profile.Server.AdditionalData = copy;
        server.Storage.Accounts.Set(a);
    }

    static JToken Json(object o) => JToken.Parse(JsonConvert.SerializeObject(o, EqPolicy.Json));

    static JObject Describe(XPlatformId user, EqState st, long now)
    {
        var r = new JObject { ["user_id"] = user.ToString(), ["mode"] = enforce ? "enforce" : "shadow", ["exempt"] = st.Exempt,
                              ["lockout_expiry"] = st.LockoutExpiry, ["lockout_left_sec"] = Math.Max(0, st.LockoutExpiry - now),
                              ["penaltyts_sent"] = st.PenaltyTsSent, ["pending"] = PendingCount(user.ToString()),
                              ["snapshot"] = Json(Snap(st, now)), ["history_total"] = st.History.Count,
                              ["history"] = Json(st.History.TakeLast(50)) };
        if (!enforce) r["shadow_lockout_expiry"] = ViewExpiry(st);
        return r;
    }

    // accounts per state, locked, active = an entry in the last 30 d; entry classes and would-flags in those 30 d
    public static JObject Stats()
    {
        long now = Now, since = now - 30 * 86400L;
        var states = new JObject { [EqStanding.Good] = 0, [EqStanding.Warned] = 0, [EqStanding.StateLocked] = 0 };
        var cls = new JObject();
        var would = new JObject();
        int accounts = 0, active = 0, locked = 0, activeLocked = 0, activeWarned = 0, exempt = 0;
        foreach (var f in Directory.EnumerateFiles(storeDir).Where(f => f.EndsWith(".json")))
        {
            EqState? st;
            try { st = EqState.Parse(File.ReadAllText(f)); }
            catch (Exception) { continue; }   // mid-rename or a bad file
            if (st == null) continue;
            if (st.Exempt) { exempt++; continue; }   // reported apart, never in states / shares
            accounts++;
            var s = Snap(st, now);
            states[s.State] = (int)states[s.State]! + 1;
            bool isLocked = EqStanding.Locked(ViewExpiry(st), now, policy);
            if (isLocked) locked++;
            var recent = st.History.Where(e => e.T >= since).ToList();
            if (recent.Count == 0) continue;
            active++;
            if (isLocked) activeLocked++;
            if (s.State == EqStanding.Warned) activeWarned++;
            foreach (var e in recent)
            {
                cls[e.Cls] = ((int?)cls[e.Cls] ?? 0) + 1;
                foreach (var w in e.Would ?? Array.Empty<string>()) would[w] = ((int?)would[w] ?? 0) + 1;
            }
        }
        return new JObject { ["mode"] = enforce ? "enforce" : "shadow", ["accounts"] = accounts, ["exempt"] = exempt, ["states"] = states,
                             ["locked"] = locked, ["active_30d"] = active, ["active_locked"] = activeLocked,
                             ["active_warned"] = activeWarned,
                             ["locked_share"] = active == 0 ? 0.0 : Math.Round((double)activeLocked / active, 4),
                             ["warned_share"] = active == 0 ? 0.0 : Math.Round((double)activeWarned / active, 4),
                             ["entries_30d"] = cls, ["would_30d"] = would,
                             ["pending"] = removals.Values.Sum(l => { lock (l) return l.Count(p => p.Done == 0); }) };
    }

    // ---- what changes the state (all under the user's lock). Control API: an unknown account is a 404
    public static Task<JObject> Apply(XPlatformId user, string action, JObject body) =>
        Account(user) == null ? throw new KeyNotFoundException($"no account {user}")
                              : Locked(user, () => ApplyLocked(user, action, body));

    static async Task<JObject> ApplyLocked(XPlatformId user, string action, JObject body)
    {
        var st = LoadState(user);
        long now = Now;
        string? session = (string?)body["session"];
        switch (action)
        {
            case "show":
                return Describe(user, st, now);
            case "quit": case "complete":
                return await AddEntry(user, st, new EqEntry { T = now, Cls = action, Session = session, Reason = "admin" });
            case "forgive":
            {
                // the forgiven quit produced the running lockout and nothing else would hold it: end it now (expire path)
                var f = new EqEntry { T = now, Cls = EqClass.AdminForgive, Session = session, Reason = (string?)body["reason"] };
                bool ends = EqStanding.ForgiveEndsLockout(st.History, f, now, ViewExpiry(st), out int row);
                st.History.Add(f);
                if (!ends) break;
                st.History[row] = st.History[row] with { Expiry = now };
                if (st.LockoutExpiry > now) st.LockoutExpiry = -1;
                return await Push(user, st, now, "admin forgive, lockout ended", flags: (byte)((flagsOverride ?? FlagsRest) & ~0x20));
            }
            case "exempt":
                st.Exempt = (bool?)body["on"] ?? throw new ArgumentException("need {\"on\": true|false}");
                break;
            case "lockout":
                long s = (long?)body["seconds"] ?? throw new ArgumentException("need seconds");
                st.LockoutExpiry = s <= 0 ? -1 : now + s;
                var lr = await Push(user, st, now, $"admin lockout {s} s", pulse: st.LockoutExpiry > now);
                if (!enforce) lr["note"] = "shadow: sent to the client, but the gate does not refuse and no pulse is sent";
                return lr;
            case "expire":
                if (st.LockoutExpiry > now) throw new ArgumentException($"lockout still running ({st.LockoutExpiry - now} s); use lockout {{\"seconds\":0}}");
                st.LockoutExpiry = -1;
                // bit 5 off after an expiry: the podium can go home
                return await Push(user, st, now, "expire", flags: (byte)((flagsOverride ?? FlagsRest) & ~0x20));
            case "clear":
                st.History.Add(new EqEntry { T = now, Cls = EqClass.AdminClear });
                st.LockoutExpiry = -1;
                break;
            default:
                throw new ArgumentException($"action {action}: quit|complete|lockout|clear|expire|forgive|exempt");
        }
        return await Push(user, st, now, $"admin {action}");
    }

    // one quit / complete / neutral row, then the standing: a counted quit may set or extend the lockout, and pulses
    // while warned / locked
    static async Task<JObject> AddEntry(XPlatformId user, EqState st, EqEntry e)
    {
        long now = Now;
        st.History.Add(e);
        string note = "";
        if (e.Cls == EqClass.Quit && EqStanding.OnCountedQuit(st.History, now, ViewExpiry(st), policy) is long exp)
        {
            st.History.Add(new EqEntry { T = now, Cls = EqClass.Lockout, Session = e.Session, Expiry = exp, Reason = enforce ? null : "shadow" });
            if (enforce) st.LockoutExpiry = exp;
            note = $", {(enforce ? "" : "shadow would ")}lockout until {exp} ({exp - now} s left)";
        }
        var s = Snap(st, now);
        string would = e.Would is { Length: > 0 } w ? " " + string.Join(",", w) : "";
        log($"[EARLYQUIT] {user} {e.Cls} ({e.Reason}{would}) session {e.Session ?? "-"}: wq {s.Wq}/{s.MatchesInWindow}, {s.State}, rung {s.Rung}{note}");
        return await Push(user, st, now, e.Cls, pulse: e.Cls == EqClass.Quit && s.State != EqStanding.Good);
    }

    // bumps penaltyts, saves the store and the profile mirror, then sends [Flags] + Update to the login peers (all, or
    // `to`). pulse: enforce only (shadow logs it), skipped with --early-quit-flags; a "pulse" row only when a peer got
    // it, so a notice stays owed to a user who is offline (e.g. quit by closing the game).
    static async Task<JObject> Push(XPlatformId user, EqState st, long now, string why, bool pulse = false,
                                    byte? flags = null, Peer[]? to = null)
    {
        bool arrive = Arriving(user.ToString(), now);
        bool doPulse = pulse && enforce && flagsOverride == null && arrive;
        if (pulse && !enforce) log($"[EARLYQUIT] shadow would pulse {user} ({why}{(arrive ? "" : ", not arriving: owed")})");
        else if (pulse && !doPulse && flagsOverride == null) log($"[EARLYQUIT] {user} {why}: not arriving in a lobby, the notice stays owed");
        var w = Wire(st, now);
        store.Save(user.ToString(), st);
        Mirror(user, w, st.LockoutExpiry);
        byte? f = doPulse ? FlagsPulse : flags ?? (st.LockoutExpiry > now ? flagsOverride : null);
        int sent = 0;
        foreach (var p in to ?? LoginPeers(user))
            if (await TrySend(user, p, f is byte fb ? new[] { FlagsMsg(fb), Update(w, st.LockoutExpiry) } : new[] { Update(w, st.LockoutExpiry) }))
            {
                sent++;
                if (doPulse && LoginPeers(user).Contains(p)) shown[p] = st.LockoutExpiry;   // not after its disconnect
            }
        if (doPulse && sent > 0)
        {
            st.History.Add(new EqEntry { T = now, Cls = EqClass.Pulse, Reason = why });
            store.Save(user.ToString(), st);
            // LOAD_STATS may land between the Arriving check and here: then the short hold
            SchedulePulseEnd(user, arriving.TryGetValue(user.ToString(), out var a) ? Math.Max(policy.PulseHoldSec, a + policy.ArrivalMaxSec - now) : policy.PulseHoldSec);
        }
        log($"[EARLYQUIT] {user} {why}: {w.ToString(Formatting.None)} lockout {st.LockoutExpiry}{(f is byte fl ? $", flags 0x{fl:x2}{(doPulse ? " (pulse until the lobby load)" : "")}" : "")} -> {sent} login peer(s)");
        ScheduleExpiry(user, st.LockoutExpiry);
        var r = Describe(user, st, now);
        r["sent"] = sent;
        return r;
    }

    // one delayed "expire" per (user, expiry): fires 1 s after the expiry unless the lockout changed meanwhile
    static void ScheduleExpiry(XPlatformId user, long exp)
    {
        string id = user.ToString();
        if (exp <= Now || (expiring.TryGetValue(id, out var cur) && cur == exp)) return;
        expiring[id] = exp;
        _ = Task.Run(async () =>
        {
            try
            {
                await Sleep(exp - Now + 1);
                await Locked(user, async () =>
                {
                    if (!expiring.TryGetValue(id, out var e) || e != exp) return 0;
                    expiring.TryRemove(id, out _);
                    if (LoadState(user).LockoutExpiry != exp) return 0;
                    await ApplyLocked(user, "expire", new JObject());
                    return 0;
                });
            }
            catch (Exception ex) { log($"[EARLYQUIT] expire failed: {ex.Message}"); }
        });
    }

    // end of a pulse: Flags 0x02 after hold s, skipped when a newer pulse or a newer end started meanwhile
    static void SchedulePulseEnd(XPlatformId user, double hold)
    {
        string id = user.ToString();
        long gen = pulseGen.AddOrUpdate(id, 1, (_, g) => g + 1);
        _ = Task.Run(async () =>
        {
            try
            {
                await Sleep(hold);
                await Locked(user, async () =>
                {
                    if (!pulseGen.TryGetValue(id, out var g) || g != gen) return 0;
                    arriving.TryRemove(id, out _);
                    pulseGen.TryRemove(KeyValuePair.Create(id, gen));
                    int sent = 0;
                    foreach (var p in LoginPeers(user))
                        if (await TrySend(user, p, FlagsMsg(FlagsRest))) sent++;
                    log($"[EARLYQUIT] {user} pulse end: flags 0x{FlagsRest:x2} -> {sent} login peer(s)");
                    return 0;
                });
            }
            catch (Exception ex) { log($"[EARLYQUIT] pulse end failed: {ex.Message}"); }
        });
    }

    // ---- matching gate (Core MatchingService.MatchingGate); also records the request's TeamIndex for the accept
    static bool IsPublic(RegisteredGameServer gs) =>
        gs.SessionLobbyType == ERGameServerStartSession.LobbyType.Public && !gs.SessionIsAi &&
        gs.SessionGameTypeSymbol is long gt && publicTypes.Contains(gt);

    // a store error (bad id, I/O) never fails the matching request: open, logged
    static (LobbySessionFailureErrorCode, string)? Gate(Peer peer, MatchingSession m)
    {
        try { return GateCore(m); }
        catch (Exception e) { log($"[EARLYQUIT] MatchingGate failed (open) for {m.UserId}: {e.Message}"); return null; }
    }

    static (LobbySessionFailureErrorCode, string)? GateCore(MatchingSession m)
    {
        string id = m.UserId.ToString(), what;
        lastTeam[id] = (int)m.TeamIndex;
        if (m.LobbyId is Guid lid)
        {
            if (server.ServerDBService.Registry.GetGameServer(lid) is not RegisteredGameServer gs || !IsPublic(gs)) return null;
            what = $"join {lid}";
        }
        else if ((m.GameTypeSymbol ?? m.SessionSettings?.GameType) is long gt && publicTypes.Contains(gt) &&
                 m.NewSessionLobbyType == ERGameServerStartSession.LobbyType.Public &&
                 !(m.SessionSettings?.AdditionalData?.ContainsKey("ai_difficulty") ?? false))   // VS AI stays open
            what = $"find / create 0x{gt:x16}";
        else return null;
        if (m.TeamIndex is TeamIndex.Spectator or TeamIndex.Moderator) return null;
        var st = LoadState(m.UserId);
        if (st.Exempt) return null;
        long now = Now, exp = ViewExpiry(st), left = Math.Max(0, exp - now);
        int pend = PendingCount(id);
        bool locked = EqStanding.Locked(exp, now, policy);
        if (!locked && !EqStanding.HoldActive(Snap(st, now).Wq, pend, policy)) return null;
        string why = locked ? $"lockout {left} s left" : $"hold, {pend} removal(s) pending";
        if (!enforce) { log($"[EARLYQUIT] shadow would refuse {id} public {what}: {why}"); return null; }
        log($"[EARLYQUIT] refused {id} public {what}: {why}");
        return (LobbySessionFailureErrorCode.ServerIsLocked, locked ? $"Early quit lockout {left / 60:00}:{left % 60:00}" : "Early quit check pending");
    }

    // ---- detection from the game servers (Core calls these through Guard)
    static void OnPlayersAdded(RegisteredGameServer gs, (Guid playerSession, Peer? peer)[] players)
    {
        long now = Now;
        foreach (var (_, peer) in players)
        {
            if (peer?.UserId is not XPlatformId user) continue;
            string id = user.ToString();
            bool hadTeam = lastTeam.TryRemove(id, out var t);   // consumed by this accept (refreshed by the next gate call)
            if (gs.SessionGameTypeSymbol == socialType || gs.SessionGameTypeSymbol == socialPrivateType)
            {
                arriving[id] = now;
                LobbyArrival(user);
                continue;
            }
            if (!detect || gs.SessionId is not Guid sid || !IsPublic(gs)) continue;   // M1: seats only with detect
            int? team = peer.GetSessionData<MatchingSession>() is MatchingSession m ? (int)m.TeamIndex
                      : hadTeam ? t : null;
            if (team == null && Interlocked.Exchange(ref teamMissLogged, 1) == 0)
                log($"[EARLYQUIT] {user}: no matching request seen before the accept, team -1 (logged once)");
            long at = now;
            // M3: back in the same session while a removal is pending: cancel it, keep the first accept time, one rejoin row
            if (removals.TryGetValue((gs.ServerId, sid), out var list))
            {
                List<EqPending> back;
                lock (list) back = EqPendingOps.Rejoin(list, id);
                if (back.Count > 0)
                {
                    at = back.Min(p => p.R.AcceptedAt > 0 ? p.R.AcceptedAt : now);
                    RecordRejoin(user, sid, now - back.Max(p => p.R.RemovedAt));
                }
            }
            seats[(gs.ServerId, sid, id)] = (at, team ?? -1, user);
        }
    }

    static void RecordRejoin(XPlatformId user, Guid sid, long afterSec) => _ = Task.Run(async () =>
    {
        try
        {
            await Locked(user, async () =>
            {
                var st = LoadState(user);
                if (st.Exempt) return 0;
                await AddEntry(user, st, new EqEntry { T = Now, Cls = EqClass.Rejoin, Session = sid.ToString(),
                                                       Reason = $"back after {afterSec} s" });
                return 0;
            });
        }
        catch (Exception e) { log($"[EARLYQUIT] rejoin {user} failed: {e.Message}"); }
    });

    static void OnPlayerRemoved(RegisteredGameServer gs, Guid playerSession, Peer? peer)
    {
        if (peer?.UserId is not XPlatformId user || gs.SessionId is not Guid sid || !IsPublic(gs)) return;
        var p = Remove(gs.ServerId, sid, user, Now, out var list);
        log($"[EARLYQUIT] {user} removed from public session {sid} (server {gs.ServerId}, team {p.R.Team}, {(p.R.AcceptedAt > 0 ? $"{p.R.RemovedAt - p.R.AcceptedAt} s" : "?")} after accept): grace {policy.GraceSec} s");
        _ = Task.Run(async () =>
        {
            try
            {
                await Sleep(policy.GraceSec);
                bool live = gs.SessionId == sid && Registered(gs);
                Resolve(p, new EqOutcome(null, null, live), list);
                if (!live) { DropSession(gs.ServerId, sid, new EqOutcome(null, null, false), "gone after grace"); return; }
                // live: keep only what Classify can still use (cascade window), so a long session does not grow
                long keep = Now - policy.CascadeWindowSec - policy.GraceSec;
                lock (list) list.RemoveAll(x => x.Done == 1 && x.R.RemovedAt < keep);
            }
            catch (Exception e) { log($"[EARLYQUIT] grace {user} failed: {e.Message}"); }
        });
    }

    static Pending Remove(ulong srv, Guid sid, XPlatformId user, long now, out List<Pending> list)
    {
        bool seated = seats.TryRemove((srv, sid, user.ToString()), out var seat);
        var p = new Pending { Id = user, R = new EqRemoval(user.ToString(), srv, sid, seated ? seat.Team : -1,
                                                           seated ? seat.At : 0, now) };
        list = removals.GetOrAdd((srv, sid), _ => new());
        lock (list) list.Add(p);
        return p;
    }

    static void Resolve(Pending p, EqOutcome o, List<Pending> list)
    {
        if (!p.TryFinish()) return;   // resolved already, or cancelled by a rejoin
        EqRemoval[] same;
        lock (list) same = list.Where(x => !x.Rejoined).Select(x => x.R).ToArray();
        // U4: the login socket from join time closed before the removal or within exit_window after it (would_exit)
        var r = loginClosed.TryGetValue(p.R.User, out var lc) && lc >= (p.R.AcceptedAt > 0 ? p.R.AcceptedAt : p.R.RemovedAt - policy.GraceSec)
            ? p.R with { LoginClosedAt = lc } : p.R;
        _ = Task.Run(async () =>
        {
            try
            {
                await Locked(p.Id, async () =>
                {
                    var st = LoadState(p.Id);
                    var (cls, reason, would) = EqClassifier.Classify(r, o, same, st.History, policy);
                    if (st.Exempt) { log($"[EARLYQUIT] {r.User} {cls} ({reason}) session {r.Session}: exempt, not recorded"); return 0; }
                    await AddEntry(p.Id, st, new EqEntry { T = r.RemovedAt, Cls = cls, Session = r.Session.ToString(), Reason = reason,
                                                           Would = would.Length > 0 ? would : null });
                    return 0;
                });
            }
            catch (Exception e) { log($"[EARLYQUIT] record {r.User} failed: {e.Message}"); }
        });
    }

    // end of a session: everyone still seated is removed now (complete); pending removals resolve with the end time.
    // L9: every session of this server other than the current one has ended (a fast new session can already be set).
    static void OnSessionChanged(RegisteredGameServer gs)
    {
        Guid? cur = gs.SessionId;
        long now = Now;
        foreach (var sid in seats.Keys.Where(k => k.Item1 == gs.ServerId && k.Item2 != cur).Select(k => k.Item2)
                                 .Concat(removals.Keys.Where(k => k.Item1 == gs.ServerId && k.Item2 != cur).Select(k => k.Item2))
                                 .Distinct().ToArray())
            DropSession(gs.ServerId, sid, new EqOutcome(now, null, false), "session end", seatedComplete: true);
    }

    static bool Registered(RegisteredGameServer gs) =>
        server.ServerDBService.Registry.RegisteredGameServers.TryGetValue(gs.ServerId, out var cur) && ReferenceEquals(cur, gs);

    // a crashed / shut-down server keeps its SessionId and fires no EndSession: its pending removals resolve as server
    static void OnServerUnregistered(RegisteredGameServer gs)
    {
        foreach (var k in seats.Keys.Where(k => k.Item1 == gs.ServerId).ToArray()) seats.TryRemove(k, out _);
        int n = 0;
        foreach (var sid in removals.Keys.Where(k => k.Item1 == gs.ServerId).Select(k => k.Item2).ToArray())
            n += DropSession(gs.ServerId, sid, new EqOutcome(null, Now, false), null);
        log($"[EARLYQUIT] server {gs.ServerId} unregistered: {n} pending removal(s) resolved");
    }

    // forget one (server, session): seats (removed now when seatedComplete, else dropped) and its removals, the
    // unresolved ones resolved with o. Returns how many resolved.
    static int DropSession(ulong srv, Guid sid, EqOutcome o, string? why, bool seatedComplete = false)
    {
        foreach (var k in seats.Keys.Where(k => k.Item1 == srv && k.Item2 == sid).ToArray())
            if (seatedComplete && seats.TryGetValue(k, out var seat)) Remove(srv, sid, seat.Id, o.SessionEndedAt ?? Now, out _);
            else seats.TryRemove(k, out _);
        int n = 0;
        if (removals.TryRemove((srv, sid), out var list))
        {
            Pending[] ps;
            lock (list) ps = list.Where(p => p.Done == 0).ToArray();
            foreach (var p in ps) { n++; Resolve(p, o, list); }
        }
        if (n > 0 && why != null) log($"[EARLYQUIT] server {srv} session {sid} {why}: {n} pending removal(s) resolved");
        return n;
    }

    // a LOAD_STATS remote log whose destination is a social lobby (the podium lobby)
    static bool LoadedSocial(string log)
    {
        try { return (string?)JObject.Parse(log)["[destination][match_type]"] is string t
                     && (t.Equals("Social_2.0", StringComparison.OrdinalIgnoreCase) || t.Equals("Social_2.0_Private", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception) { return false; }
    }

    // arriving: accepted into a social lobby less than arrival_max ago and its LOAD_STATS not seen yet
    static bool Arriving(string id, long now) => arriving.TryGetValue(id, out var a) && now - a <= policy.ArrivalMaxSec;

    // the lobby finished loading: end a running arrival pulse pulse_hold later (the podium page is up by now)
    static void Loaded(XPlatformId user)
    {
        string id = user.ToString();
        if (!arriving.TryRemove(id, out _) || !pulseGen.ContainsKey(id)) return;
        log($"[EARLYQUIT] {user} lobby loaded: pulse ends in {policy.PulseHoldSec} s");
        SchedulePulseEnd(user, policy.PulseHoldSec);
    }

    // social accept: pulse at once the login peers a notice is owed to; the podium home page is enabled during the load
    static void LobbyArrival(XPlatformId user) => _ = Task.Run(async () =>
    {
        try
        {
            await Locked(user, async () =>
            {
                var st = LoadState(user);
                long now = Now;
                var s = Snap(st, now);
                bool locked = ViewExpiry(st) > now;
                var owed = LoginPeers(user).Where(p => EqStanding.OwedTo(s.NoticeOwed, locked,
                               shown.TryGetValue(p, out var e) ? e : null, st.LockoutExpiry)).ToArray();
                if (owed.Length == 0 || flagsOverride != null) return 0;
                if (!enforce) { log($"[EARLYQUIT] shadow would pulse {user} (lobby arrival, {s.State})"); return 0; }
                await Push(user, st, now, "lobby arrival", pulse: true, to: owed);
                return 0;
            });
        }
        catch (Exception e) { log($"[EARLYQUIT] lobby pulse failed: {e.Message}"); }
    });

    // ---- messages
    static Peer[] LoginPeers(XPlatformId user)
    {
        lock (server.LoginService.PeersLock)
            return server.LoginService.Peers.Where(p => p.UserId?.ToString() == user.ToString()).ToArray();
    }

    static async Task<bool> TrySend(XPlatformId user, Peer p, params Message[] ms)
    {
        // a wedged socket must not hold the user's lock: stop waiting after 10 s (Core's send itself carries on)
        try { foreach (var m in ms) await p.Send(m).WaitAsync(TimeSpan.FromSeconds(10)); return true; }
        catch (Exception e) { log($"[EARLYQUIT] send to {user} peer failed: {e.Message}"); return false; }
    }

    static Message FlagsMsg(byte f) => new UnimplementedMessage(SymFlags) { Data = new[] { f } };

    static async Task SendAll(Peer peer)
    {
        if (peer.UserId is not XPlatformId user) return;
        await Locked(user, async () =>
        {
            var st = LoadState(user);
            long now = Now;
            byte f = flagsOverride ?? FlagsRest;
            var w = Wire(st, now);
            store.Save(user.ToString(), st);
            Mirror(user, w, st.LockoutExpiry);
            bool ok = await TrySend(user, peer, Config(), FlagsMsg(f), Update(w, st.LockoutExpiry));
            log($"[EARLYQUIT] login push to {user}: config, flags 0x{f:x2}, update (ts {w["penaltyts"]}, lockout {st.LockoutExpiry}){(ok ? "" : " FAILED")}");
            ScheduleExpiry(user, st.LockoutExpiry);
            return 0;
        });
    }

    static Message Config()
    {
        var io = new StreamIO(ByteOrder.LittleEndian, StreamMode.Write);
        io.Write((byte)0);   // the 1-byte struct: without it the client reads the length one byte late and dies
        io.WriteJSON(config, true, JSONCompressionMode.Zstd);
        byte[] b = io.ToArray();
        io.Close();
        return new UnimplementedMessage(SymConfig) { Data = b };
    }

    static Message Update(JObject st, long lockout)
    {
        var b = new byte[56];
        BitConverter.TryWriteBytes(b.AsSpan(0x18), (long)st["penaltyts"]!);
        BitConverter.TryWriteBytes(b.AsSpan(0x20), lockout);
        BitConverter.TryWriteBytes(b.AsSpan(0x28), (uint)(int)st["numearlyquits"]!);
        BitConverter.TryWriteBytes(b.AsSpan(0x2c), (uint)(int)st["numsteadymatches"]!);
        BitConverter.TryWriteBytes(b.AsSpan(0x30), (uint)(int)st["numsteadyearlyquits"]!);
        b[0x34] = (byte)(int)st["penaltylevel"]!;
        b[0x35] = (byte)(int)st["steadyplayerlevel"]!;
        return new UnimplementedMessage(SymUpdate) { Data = b };
    }
}
