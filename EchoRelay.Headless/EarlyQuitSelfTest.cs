using Newtonsoft.Json.Linq;
// Offline self-test of the early-quit policy and store on synthetic timelines. Run() prints one
// line per case and returns the number of failures.
static class EqSelfTest
{
    const long Now = 1_800_000_000, Day = 86400;
    static readonly Guid S = Guid.Parse("11111111-2222-3333-4444-555555555555");
    static readonly EqPolicy P = EqPolicy.Default;
    static readonly EqOutcome Live = new(null, null, true);
    static readonly EqEntry[] None = Array.Empty<EqEntry>();
    static readonly EqRemoval[] NoOthers = Array.Empty<EqRemoval>();

    static EqRemoval Rm(string user, long removed = Now, int team = 0, long? accepted = null, long? loginClosed = null) =>
        new(user, 7, S, team, accepted ?? removed - 300, removed, loginClosed);
    static EqEntry E(string cls, long t, string? session = null, string? reason = null, long? expiry = null) =>
        new() { T = t, Cls = cls, Session = session, Reason = reason, Expiry = expiry };
    static List<EqEntry> Many(string cls, int n, long t0, long step = 600) =>
        Enumerable.Range(0, n).Select(i => E(cls, t0 + i * step, $"s{cls}{t0 + i * step}")).ToList();

    public static int Run(Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        int fail = 0, n = 0;
        void Check(string name, bool ok, string detail = "")
        {
            n++;
            if (!ok) fail++;
            log($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail == "" ? "" : "  -- " + detail)}");
        }
        void Cls(string name, (string Cls, string Reason, string[] Would) got, string cls, string? reason = null, params string[] would)
        {
            bool ok = got.Cls == cls && (reason == null || got.Reason == reason) && got.Would.SequenceEqual(would);
            Check(name, ok, $"got {got.Cls}/{got.Reason} [{string.Join(",", got.Would)}]");
        }
        try { Classifier(Cls); Standing(Check); Fixes(Check); Pendings(Check, Cls); Store(Check); PolicyFile(Check); }
        catch (Exception e) { Check("no exception", false, e.ToString()); }
        log($"early-quit self-test: {n - fail}/{n} pass");
        return fail;
    }

    delegate void ClsCheck(string name, (string Cls, string Reason, string[] Would) got, string cls, string? reason = null,
                           params string[] would);

    static void Classifier(ClsCheck Cls)
    {
        var c = (EqRemoval r, EqOutcome o, IReadOnlyList<EqRemoval>? same, IReadOnlyList<EqEntry>? h) =>
            EqClassifier.Classify(r, o, same ?? NoOthers, h ?? None, P);

        Cls("class spectator (team 2)", c(Rm("u", team: 2), Live, null, null), "spectator", "team_spectator");
        Cls("class spectator (moderator 4)", c(Rm("u", team: 4), Live, null, null), "spectator", "team_moderator");
        Cls("class spectator beats server", c(Rm("u", team: 2), new(null, Now + 5, false), null, null), "spectator");
        Cls("class server (unregistered in grace, also ended)", c(Rm("u"), new(Now + 10, Now + 30, false), null, null),
            "server", "server_unregistered");
        Cls("class server unregistered after grace -> quit", c(Rm("u"), new(null, Now + 61, true), null, null), "quit");
        Cls("class complete (end 10 s after removal)", c(Rm("u"), new(Now + 10, null, false), null, null), "complete", "end_burst");
        Cls("class complete (end at +15 boundary)", c(Rm("u"), new(Now + 15, null, false), null, null), "complete");
        Cls("class complete (removed after end)", c(Rm("u"), new(Now - 3, null, false), null, null), "complete");
        Cls("class abandoned (end at +16)", c(Rm("u"), new(Now + 16, null, false), null, null), "abandoned", "ended_in_grace");
        Cls("class abandoned (end at +60)", c(Rm("u"), new(Now + 60, null, false), null, null), "abandoned");
        Cls("class end after grace -> quit", c(Rm("u"), new(Now + 61, null, true), null, null), "quit");
        Cls("class not live, no event -> abandoned", c(Rm("u"), new(null, null, false), null, null), "abandoned", "not_live");
        Cls("class complete beats start", c(Rm("u", accepted: Now - 5), new(Now + 5, null, false), null, null), "complete");
        Cls("class start (20 s after accept)", c(Rm("u", accepted: Now - 20), Live, null, null), "start", "start_grace");
        Cls("class start (30 s boundary)", c(Rm("u", accepted: Now - 30), Live, null, null), "start");
        Cls("class quit at 31 s", c(Rm("u", accepted: Now - 31), Live, null, null), "quit", "live_after_grace");
        var starts1 = new[] { E("start", Now - 3000) };
        Cls("class 2nd start in hour stays start", c(Rm("u", accepted: Now - 10), Live, null, starts1), "start");
        var starts2 = new[] { E("start", Now - 3000), E("start", Now - 1200) };
        Cls("class start_cap (3rd in sliding hour)", c(Rm("u", accepted: Now - 10), Live, null, starts2), "quit", "start_cap");
        var startsOld = new[] { E("start", Now - 3700), E("start", Now - 1200) };
        Cls("class start_cap slides (one start > 1 h old)", c(Rm("u", accepted: Now - 10), Live, null, startsOld), "start");
        var capped = new[] { E("start", Now - 1200), E("quit", Now - 600, reason: "start_cap") };
        Cls("class start_cap counts earlier capped quits", c(Rm("u", accepted: Now - 10), Live, null, capped), "quit", "start_cap");
        Cls("class quit", c(Rm("u"), Live, null, null), "quit", "live_after_grace");

        Cls("would_exit (login closed before removal)", c(Rm("u", loginClosed: Now - 14), Live, null, null), "quit", null, "would_exit");
        Cls("would_exit (login closed +5 s)", c(Rm("u", loginClosed: Now + 5), Live, null, null), "quit", null, "would_exit");
        Cls("no would_exit (login closed +6 s)", c(Rm("u", loginClosed: Now + 6), Live, null, null), "quit");
        Cls("no would_exit (login closed 1 s before accept)", c(Rm("u", accepted: Now - 300, loginClosed: Now - 301), Live, null, null), "quit");
        Cls("would_exit (login closed at accept)", c(Rm("u", accepted: Now - 300, loginClosed: Now - 300), Live, null, null), "quit", null, "would_exit");
        Cls("class server (unregistered at +60 boundary)", c(Rm("u"), new(null, Now + 60, true), null, null), "server");
        Cls("class start row exactly 1 h old not counted", c(Rm("u", accepted: Now - 10), Live, null,
            new[] { E("start", Now - 3600), E("start", Now - 1200) }), "start");
        Cls("no would-flags on non-quit", c(Rm("u", loginClosed: Now - 1), new(Now + 5, null, false), null, null), "complete");

        // cascade timeline: A 0, B 5 (A's group), C 30, D 200, spectator X 25, start leave Y 150, Z 160
        long t0 = Now;
        var A = Rm("A", t0); var B = Rm("B", t0 + 5); var C = Rm("C", t0 + 30); var D = Rm("D", t0 + 200);
        var X = Rm("X", t0 + 25, team: 2); var Y = Rm("Y", t0 + 150, accepted: t0 + 140); var Z = Rm("Z", t0 + 160);
        var all = new[] { A, B, C, D, X, Y, Z };
        Cls("cascade: earliest quitter never", c(A, Live, all, null), "quit");
        Cls("cascade: together (5 s) group not exempt", c(B, Live, all, null), "quit");
        Cls("cascade: 30 s after earlier quit", c(C, Live, all, null), "quit", null, "would_cascade");
        Cls("cascade: list without self", c(C, Live, new[] { A, B }, null), "quit", null, "would_cascade");
        Cls("cascade: 160 s after A, 130 after C -> no", c(Z, Live, new[] { A, B, C, X, Y }, null), "quit");
        Cls("cascade: Z within 120 of C (with D gone)", c(Rm("Z", t0 + 140), Live, new[] { A, C }, null), "quit", null, "would_cascade");
        Cls("cascade: after spectator / start leave only -> no", c(Rm("W", t0 + 40), Live, new[] { X, Rm("Y2", t0 + 20, accepted: t0 + 10) }, null), "quit");
        var chain = new[] { Rm("P", t0), Rm("Q", t0 + 9), Rm("R", t0 + 18) };
        Cls("cascade: 9 s chain is one group", c(chain[2], Live, chain, null), "quit");
        Cls("cascade: gap exactly 10 s = new group -> flagged", c(Rm("G", t0 + 10), Live, new[] { Rm("F", t0) }, null), "quit", null, "would_cascade");
        Cls("cascade: exactly 120 s after -> flagged", c(Rm("G", t0 + 120), Live, new[] { Rm("F", t0) }, null), "quit", null, "would_cascade");
        Cls("cascade: 121 s after -> no", c(Rm("G", t0 + 121), Live, new[] { Rm("F", t0) }, null), "quit");
        var sim = new[] { Rm("S1", t0), Rm("S2", t0), Rm("S3", t0) };
        Cls("cascade: simultaneous leaves all pay", c(sim[1], Live, sim, null), "quit");
        Cls("cascade: exit + cascade both logged", c(Rm("C", t0 + 30, loginClosed: t0 + 20), Live, all, null), "quit", null,
            "would_exit", "would_cascade");
    }

    // M2 hold count, M3 rejoin (cancel at most once, then one complete), L8 no seat
    static void Pendings(Action<string, bool, string> Check, ClsCheck Cls)
    {
        EqPending Pd(string user, int team = 0) => new() { R = Rm(user, team: team) };
        var l = new List<EqPending> { Pd("u"), Pd("u", 2), Pd("u", 4), Pd("v") };
        Check("hold count skips spectator / moderator removals", EqPendingOps.HoldCount(l, "u") == 1, "");
        Check("hold: wq 4 + spectator removal pending -> no hold",
              !EqStanding.HoldActive(4, EqPendingOps.HoldCount(new[] { Pd("w", 2) }, "w"), P), "");
        var back = EqPendingOps.Rejoin(l, "u");
        Check("rejoin cancels that user's 3 pending, not v's", back.Count == 3 && back.All(p => p.Rejoined && p.Done == 1) &&
              l[3].Done == 0, "");
        Check("after rejoin: nothing pending for u", EqPendingOps.HoldCount(l, "u") == 0, "");
        Check("grace / end after rejoin records nothing (TryFinish false)", !l[0].TryFinish(), "");
        Check("second rejoin cancels nothing", EqPendingOps.Rejoin(l, "u").Count == 0, "");
        Check("resolve then rejoin: rejoin cancels nothing", l[3].TryFinish() && EqPendingOps.Rejoin(l, "v").Count == 0, "");
        var h = new List<EqEntry> { E(EqClass.Rejoin, Now - 20, "s1"), E(EqClass.Complete, Now, "s1") };
        var s = EqStanding.Compute(h, Now, -1, P);
        Check("rejoin row is neutral: one match, one complete, wq 0", s.MatchesInWindow == 1 && s.CompletedInWindow == 1 &&
              s.Wq == 0 && !h[0].IsMatch, s.ToString());
        Cls("class no seat (accept unknown) -> abandoned", EqClassifier.Classify(Rm("u", accepted: 0), Live, NoOthers, None, P),
            "abandoned", "no_seat");
        Cls("class no seat, session ended in burst -> complete", EqClassifier.Classify(Rm("u", accepted: 0), new(Now + 5, null, false),
            NoOthers, None, P), "complete");
    }

    static void Standing(Action<string, bool, string> Check)
    {
        EqSnapshot S(IReadOnlyList<EqEntry> h, long now = Now, long exp = -1) => EqStanding.Compute(h, now, exp, P);
        string Str(EqSnapshot s) => s.ToString();

        // 10 completes, then quits
        var h = Many("complete", 10, Now - 20 * 3600);
        h.AddRange(Many("quit", 2, Now - 3600));
        var s = S(h);
        Check("standing GOOD with 2 quits", s.State == "good" && s.Wq == 2 && !s.NoticeOwed, Str(s));
        Check("2 quits: no lockout", EqStanding.OnCountedQuit(h, Now, -1, P) == null, "");
        h.Add(E("quit", Now - 1000, "q3"));
        s = S(h);
        Check("standing WARNED with 3 quits, notice owed", s.State == "warned" && s.Wq == 3 && s.NoticeOwed, Str(s));
        h.Add(E("pulse", Now - 900));
        Check("pulse clears notice", !S(h).NoticeOwed, Str(S(h)));
        h.Add(E("quit", Now - 800, "q4"));
        Check("4th quit: warned, owed again, no lockout", S(h).State == "warned" && S(h).NoticeOwed &&
              EqStanding.OnCountedQuit(h, Now, -1, P) == null, Str(S(h)));
        h.Add(E("quit", Now, "q5"));
        long? exp = EqStanding.OnCountedQuit(h, Now, -1, P);
        Check("5th quit locks 300 s", exp == Now + 300, $"{exp - Now}");
        h.Add(E("lockout", Now, expiry: exp));
        s = S(h, Now, exp!.Value);
        Check("LOCKED, notice owed, rung now 1", s.State == "locked" && s.Wq == 5 && s.NoticeOwed && s.Rung == 1, Str(s));
        h.Add(E("pulse", Now + 9));
        Check("pulse after lockout clears notice", !S(h, Now + 9, exp.Value).NoticeOwed, "");
        h.Add(E("quit", Now + 60, "q6"));
        long? exp2 = EqStanding.OnCountedQuit(h, Now + 60, exp.Value, P);
        Check("6th quit inside lockout extends old + 900", exp2 == exp + 900, $"{exp2 - Now}");
        Check("lockout expired: warned, not locked", S(h, Now + 2000, exp2!.Value).State == "warned", "");

        // cap: 4 lockouts this week -> rung 4 -> 86400, extension capped at now + 86400
        var hc = Many("quit", 6, Now - 5 * 3600);
        hc.AddRange(Enumerable.Range(0, 4).Select(i => E("lockout", Now - Day * (i + 1), expiry: Now + 3600)));
        hc.Add(E("quit", Now, "qc"));
        long? capd = EqStanding.OnCountedQuit(hc, Now, Now + 3600, P);
        Check("extension capped at now + extend_cap", capd == Now + 86400, $"{capd - Now}");
        hc.AddRange(Enumerable.Range(0, 3).Select(i => E("lockout", Now - 3600 * (i + 1))));
        Check("rung clamps at ladder top", S(hc).Rung == 4, Str(S(hc)));
        Check("fresh lockout at top rung = 86400", EqStanding.OnCountedQuit(hc, Now, -1, P) == Now + 86400, "");

        // rung decay: one lockout this week, then completes, then a 6th counted quit
        var hr = Many("quit", 5, Now - 10 * 3600);
        hr.Add(E("lockout", Now - 9 * 3600, expiry: Now - 9 * 3600 + 300));
        hr.AddRange(Many("complete", 4, Now - 8 * 3600));
        Check("rung 1 after 4 completes", S(hr).Rung == 1, Str(S(hr)));
        var hr4 = hr.Append(E("quit", Now, "q")).ToList();
        Check("quit after 4 completes: 900 s", EqStanding.OnCountedQuit(hr4, Now, -1, P) == Now + 900, "");
        hr.Add(E("complete", Now - 3600, "c5"));
        Check("rung 0 after 5 completes", S(hr).Rung == 0, Str(S(hr)));
        hr.Add(E("quit", Now, "q"));
        Check("quit after 5 completes: 300 s", EqStanding.OnCountedQuit(hr, Now, -1, P) == Now + 300, "");
        var hw = new List<EqEntry> { E("lockout", Now - 6 * Day) };
        Check("rung 1 with a 6-day-old lockout", S(hw).Rung == 1, "");
        Check("rung 0 after 7 days", S(hw, Now + Day + 1).Rung == 0, Str(S(hw, Now + Day + 1)));

        // window aging
        var ha = Many("quit", 5, Now - 31 * Day);
        Check("quits older than 30 d leave the window -> GOOD", S(ha).State == "good" && S(ha).Wq == 0, Str(S(ha)));
        var hm = Many("quit", 5, Now - 5 * Day);
        hm.AddRange(Many("complete", 16, Now - 4 * Day));
        Check("21 matches: oldest quit drops, wq 4 warned", S(hm).Wq == 4 && S(hm).State == "warned" && S(hm).MatchesInWindow == 20, Str(S(hm)));
        hm.AddRange(Many("complete", 4, Now - 3 * Day));
        Check("20 newer completes -> wq 0 GOOD", S(hm).Wq == 0 && S(hm).State == "good" && S(hm).CompletedInWindow == 20, Str(S(hm)));
        Check("non-match rows ignored by window", S(new[] { E("abandoned", Now), E("start", Now), E("server", Now), E("spectator", Now) }).MatchesInWindow == 0, "");

        // admin rows
        var hf = Many("quit", 5, Now - 3600);
        hf.Add(E("admin_forgive", Now, hf[2].Session));
        Check("admin_forgive by session drops one quit", S(hf).Wq == 4, Str(S(hf)));
        hf.Add(E("admin_forgive", Now));
        Check("admin_forgive without session drops newest quit", S(hf).Wq == 3, Str(S(hf)));
        hf.Add(E("lockout", Now));
        hf.Add(E("admin_clear", Now));
        Check("admin_clear resets window and rung", S(hf).Wq == 0 && S(hf).Rung == 0 && !S(hf).NoticeOwed, Str(S(hf)));

        // hold
        Check("hold: wq 3 + 2 pending", EqStanding.HoldActive(3, 2, P), "");
        Check("hold: chained quits wq 4 + 1 pending", EqStanding.HoldActive(4, 1, P), "");
        Check("no hold: wq 3 + 1 pending", !EqStanding.HoldActive(3, 1, P), "");
        Check("no hold without pending", !EqStanding.HoldActive(6, 0, P), "");

        // steady level
        int L(int completes, int quits)
        {
            var hl = Many("complete", completes, Now - 3 * Day);
            hl.AddRange(Many("quit", quits, Now - Day));
            return S(hl).SteadyLevel;
        }
        Check("steady: 9 matches -> none (wire 0)", L(9, 0) == -1 && S(Many("complete", 9, Now - Day)).SteadyLevelWire == 0, "");
        Check("steady: 10 matches 8 complete (80 %) -> 0", L(8, 2) == 0, $"{L(8, 2)}");
        Check("steady: 10 matches 7 complete (70 %) -> none, wire 0", L(7, 3) == -1 &&
              S(Many("complete", 7, Now - 3 * Day).Concat(Many("quit", 3, Now - Day)).ToList()).SteadyLevelWire == 0, $"{L(7, 3)}");
        Check("steady: 10 quits 0 complete -> none", L(0, 10) == -1, $"{L(0, 10)}");
        Check("steady: 20 matches 16 complete (80 %) -> 0", L(16, 4) == 0, $"{L(16, 4)}");
        Check("steady: 10/10 -> 1", L(10, 0) == 1, $"{L(10, 0)}");
        Check("steady: 19/20 -> 2", L(19, 1) == 2, $"{L(19, 1)}");
        Check("steady: 20/20 -> 3", L(20, 0) == 3, $"{L(20, 0)}");
        Check("steady: 18/20 -> 1", L(18, 2) == 1, $"{L(18, 2)}");

        // clock tolerance
        Check("gate: 5 s left within tolerance -> open", !EqStanding.Locked(Now + 5, Now, P), "");
        Check("gate: 6 s left -> locked", EqStanding.Locked(Now + 6, Now, P), "");
        Check("gate: expired -> open", !EqStanding.Locked(Now - 1, Now, P), "");
        Check("gate: none (-1) -> open", !EqStanding.Locked(-1, Now, P), "");
    }

    // review fixes: extension, forgive -> lockout end, shadow rows and the rung, per-peer notice, boundaries
    static void Fixes(Action<string, bool, string> Check)
    {
        EqSnapshot S(IReadOnlyList<EqEntry> h, long now = Now, long exp = -1) => EqStanding.Compute(h, now, exp, P);

        // extension never shortens
        var hx = Many("quit", 6, Now - 5 * 3600);
        hx.Add(E("quit", Now, "qx"));
        Check("extension: old beyond cap (now + 3 d) is kept", EqStanding.OnCountedQuit(hx, Now, Now + 3 * Day, P) == Now + 3 * Day,
              $"{EqStanding.OnCountedQuit(hx, Now, Now + 3 * Day, P) - Now}");
        Check("extension: old exactly at cap is kept", EqStanding.OnCountedQuit(hx, Now, Now + Day, P) == Now + Day, "");
        Check("extension: old just below cap rises to cap", EqStanding.OnCountedQuit(hx, Now, Now + Day - 100, P) == Now + Day, "");
        Check("lockoutExpiry == now: not locked, fresh lockout", S(hx, Now, Now).State != "locked" &&
              EqStanding.OnCountedQuit(hx, Now, Now, P) == Now + 300, "");

        // window / rung boundaries
        var b30 = new List<EqEntry> { E("quit", Now - 30 * Day), E("quit", Now - 30 * Day - 1) };
        Check("window: row exactly 30 d old counts, 30 d + 1 s does not", S(b30).Wq == 1, S(b30).ToString());
        Check("wq == enable_at - 1 -> good", S(Many("quit", P.EnableAt - 1, Now - Day)).State == "good", "");
        Check("rung: lockout exactly 7 d old not counted", S(new List<EqEntry> { E("lockout", Now - 7 * Day) }).Rung == 0, "");
        Check("rung counts extension lockout rows", S(new List<EqEntry> { E("lockout", Now - Day), E("lockout", Now - 3600) }).Rung == 2, "");
        Check("no hold with negative pending", !EqStanding.HoldActive(10, -1, P), "");

        // shadow lockout rows never raise the rung
        var hs = Many("quit", 5, Now - 10 * 3600);
        hs.Add(E("lockout", Now - 9 * 3600, reason: "shadow", expiry: Now - 9 * 3600 + 300));
        hs.Add(E("lockout", Now - 8 * 3600, reason: "shadow", expiry: Now - 8 * 3600 + 900));
        Check("rung: shadow lockouts not counted", S(hs).Rung == 0, S(hs).ToString());
        hs.Add(E("lockout", Now - 7 * 3600, expiry: Now - 7 * 3600 + 300));
        Check("rung: real lockout counted beside shadow ones", S(hs).Rung == 1, S(hs).ToString());
        var hs2 = Many("quit", 5, Now - 10 * 3600);
        hs2.Add(E("lockout", Now - 9 * 3600, reason: "shadow", expiry: Now - 9 * 3600 + 300));
        hs2.Add(E("quit", Now, "qs"));
        Check("quit after a shadow lockout: 300 s, not 900", EqStanding.OnCountedQuit(hs2, Now, -1, P) == Now + 300, "");

        // forgive the quit that produced the running lockout -> ends it
        var hf = Many("quit", 5, Now - 3600, 60);
        hf.Add(E("lockout", hf[^1].T, expiry: Now + 300));
        string q5 = hf[4].Session!, q2 = hf[1].Session!;
        Check("forgive lockout-producing quit ends the lockout",
              EqStanding.ForgiveEndsLockout(hf, E("admin_forgive", Now, q5), Now, Now + 300, out int row) && row == 5, $"row {row}");
        Check("forgive without session (newest quit) ends it", EqStanding.ForgiveEndsLockout(hf, E("admin_forgive", Now), Now, Now + 300, out _), "");
        Check("forgive an earlier quit: lockout stays", !EqStanding.ForgiveEndsLockout(hf, E("admin_forgive", Now, q2), Now, Now + 300, out _), "");
        Check("forgive unknown session: lockout stays", !EqStanding.ForgiveEndsLockout(hf, E("admin_forgive", Now, "nope"), Now, Now + 300, out _), "");
        Check("forgive after the lockout ran out: nothing to end", !EqStanding.ForgiveEndsLockout(hf, E("admin_forgive", Now + 400, q5), Now + 400, -1, out _), "");
        Check("forgive while an admin lockout (other expiry) runs: stays", !EqStanding.ForgiveEndsLockout(hf, E("admin_forgive", Now, q5), Now, Now + 9999, out _), "");
        var he = new List<EqEntry>(hf) { E("quit", Now + 10, "q6"), E("lockout", Now + 10, expiry: Now + 1200) };
        Check("forgive the extending quit while the first lockout still runs: stays",
              !EqStanding.ForgiveEndsLockout(he, E("admin_forgive", Now + 20, "q6"), Now + 20, Now + 1200, out _), "");
        Check("forgive the extending quit after the first lockout ran out: ends",
              EqStanding.ForgiveEndsLockout(he, E("admin_forgive", Now + 400, "q6"), Now + 400, Now + 1200, out int r6) && r6 == 7, "");
        var hcl = new List<EqEntry>(hf) { E("admin_clear", Now + 1) };
        Check("forgive after admin_clear: nothing to end", !EqStanding.ForgiveEndsLockout(hcl, E("admin_forgive", Now + 2, q5), Now + 2, Now + 300, out _), "");

        // notice owed per login peer: a relogin while locked re-pulses on lobby arrival
        var hn = Many("quit", 5, Now - 3600, 60);
        hn.Add(E("lockout", Now, expiry: Now + 300));
        hn.Add(E("pulse", Now + 9));
        bool hist = S(hn, Now + 60, Now + 300).NoticeOwed;
        Check("relogin while locked: history alone owes nothing", !hist, "");
        Check("relogin while locked: new peer (never shown) owed", EqStanding.OwedTo(hist, true, null, Now + 300), "");
        Check("same peer already shown this lockout: not owed", !EqStanding.OwedTo(hist, true, Now + 300, Now + 300), "");
        Check("same peer, lockout extended since: owed", EqStanding.OwedTo(hist, true, Now + 300, Now + 1200), "");
        Check("not locked, nothing in history: not owed", !EqStanding.OwedTo(false, false, null, -1), "");
        Check("admin-only lockout (no row): history owes nothing, new peer owed",
              !S(Many("complete", 3, Now - Day), Now, Now + 600).NoticeOwed && EqStanding.OwedTo(false, true, null, Now + 600), "");
    }

    static void Store(Action<string, bool, string> Check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqselftest-" + Guid.NewGuid().ToString("n"));
        try
        {
            var st = new EqStore(dir);
            Check("file name keeps plain ids", EqStore.FileName("OVR-ORG-123_x") == "OVR-ORG-123_x.json", EqStore.FileName("OVR-ORG-123_x"));
            Check("file name escapes path chars", EqStore.FileName("../a/b:c") == "%2e%2e%2fa%2fb%3ac.json", EqStore.FileName("../a/b:c"));
            Check("missing user -> fresh state", st.Load("nobody").History.Count == 0 && st.Load("nobody").PenaltyTsSent == -1, "");

            var s = new EqState { LockoutExpiry = Now + 300, PenaltyTsSent = Now - 1 };
            s.History.Add(new EqEntry { T = Now, Cls = "quit", Session = S.ToString(), Reason = "live_after_grace", Would = new[] { "would_exit" } });
            s.History.Add(new EqEntry { T = Now, Cls = "lockout", Expiry = Now + 300 });
            st.Save("OVR-ORG-1", s);
            var r = st.Load("OVR-ORG-1");
            Check("store round-trip", r.History.Count == 2 && r.History[0] == s.History[0] with { Would = r.History[0].Would } &&
                  r.History[0].Would!.SequenceEqual(new[] { "would_exit" }) && r.History[1].Expiry == Now + 300 &&
                  r.LockoutExpiry == Now + 300 && r.PenaltyTsSent == Now - 1, File.ReadAllText(st.PathOf("OVR-ORG-1")));
            Check("store JSON is snake_case", File.ReadAllText(st.PathOf("OVR-ORG-1")).Contains("\"penalty_ts_sent\""), "");
            st.Save("ex", new EqState { Exempt = true });
            Check("exempt round-trips, default off", st.Load("ex").Exempt && !st.Load("OVR-ORG-1").Exempt, "");

            long a = r.NextPenaltyTs(Now - 10), b = r.NextPenaltyTs(Now), c = r.NextPenaltyTs(Now);
            Check("penaltyts strictly grows", a == Now && b == Now + 1 && c == Now + 2, $"{a - Now} {b - Now} {c - Now}");

            var big = new EqState();
            big.History.AddRange(Enumerable.Range(0, 600).Select(i => new EqEntry { T = Now - 600 + i, Cls = "complete" }));
            st.Save("big", big, Now);
            var rb = st.Load("big");
            Check("history capped at 500 newest when all are matches", rb.History.Count == 500 && rb.History[0].T == Now - 500 &&
                  rb.History[^1].T == Now - 1, $"{rb.History.Count}");

            // trim by class: 5 quits then 600 spectator rows -> every quit survives
            var sp = new EqState();
            sp.History.AddRange(Many("quit", 5, Now - 3 * Day));
            sp.History.AddRange(Enumerable.Range(0, 600).Select(i => E("spectator", Now - 2 * Day + i)));
            st.Save("spec", sp, Now);
            var rs = st.Load("spec");
            Check("trim drops neutral rows first, quits kept", rs.History.Count == 500 &&
                  rs.History.Count(e => e.Cls == "quit") == 5 && EqStanding.Compute(rs.History, Now, -1, P).Wq == 5,
                  $"{rs.History.Count} rows, {rs.History.Count(e => e.Cls == "quit")} quits");
            var pr = new List<EqEntry>();
            pr.AddRange(Many("complete", 450, Now - 2 * Day, 10));
            pr.AddRange(Enumerable.Range(0, 20).Select(i => E("pulse", Now - Day + i)));
            pr.Add(E("admin_forgive", Now - Day + 50)); pr.Add(E("admin_clear", Now - Day + 51));
            pr.AddRange(Enumerable.Range(0, 100).Select(i => E("start", Now - Day + 100 + i)));
            EqState.Trim(pr, Now, 30);
            Check("trim over cap keeps pulse / admin rows, drops oldest starts", pr.Count == 500 && pr.Count(e => e.Cls == "pulse") == 20 &&
                  pr.Count(e => e.Cls.StartsWith("admin_")) == 2 && pr.Count(e => e.Cls == "start") == 28 &&
                  pr.Where(e => e.Cls == "start").Min(e => e.T) == Now - Day + 172, $"{pr.Count}");
            var old = new List<EqEntry> { E("lockout", Now - 40 * Day, expiry: 1), E("quit", Now - 40 * Day), E("lockout", Now - 35 * Day, expiry: 2),
                                          E("quit", Now - 31 * Day), E("complete", Now - 31 * Day - 1), E("quit", Now - Day) };
            EqState.Trim(old, Now, 30);
            Check("trim by age: > 31 d gone, newest lockout kept, 31 d boundary kept", old.Count == 3 && old[0].Expiry == 2 &&
                  old[1].T == Now - 31 * Day && old[2].T == Now - Day, string.Join(" ", old.Select(e => $"{e.Cls}@{(e.T - Now) / Day}")));

            // concurrent locked Load -> append -> Save: no lost update
            const int tasks = 8, each = 20;
            Task.WaitAll(Enumerable.Range(0, tasks).Select(k => Task.Run(async () =>
            {
                for (int i = 0; i < each; i++)
                {
                    var sem = st.Lock("conc");
                    await sem.WaitAsync();
                    try
                    {
                        var x = st.Load("conc");
                        x.History.Add(new EqEntry { T = Now - 10000 + k * 1000 + i, Cls = "complete" });
                        x.NextPenaltyTs(Now);
                        st.Save("conc", x, Now);
                    }
                    finally { sem.Release(); }
                }
            })).ToArray());
            var rc = st.Load("conc");
            Check("concurrent locked saves keep every row", rc.History.Count == tasks * each && rc.PenaltyTsSent == Now + tasks * each - 1,
                  $"{rc.History.Count} rows, ts +{rc.PenaltyTsSent - Now}");

            // unlocked parallel saves still leave a whole file (atomic rename) and no tmp files
            Task.WaitAll(Enumerable.Range(0, 8).Select(k => Task.Run(() =>
            {
                for (int i = 0; i < 20; i++) { var x = new EqState(); x.History.Add(new EqEntry { T = Now - k, Cls = "quit" }); st.Save("race", x, Now); }
            })).ToArray());
            var rr = st.Load("race");
            Check("unlocked parallel saves: file whole, no tmp left", rr.History.Count == 1 &&
                  !Directory.EnumerateFiles(dir, "*.tmp").Any(), "");

            File.WriteAllText(st.PathOf("bad"), "{not json");
            var rbad = st.Load("bad");
            Check("corrupt file moved aside, fresh state", rbad.History.Count == 0 && !File.Exists(st.PathOf("bad")) &&
                  Directory.EnumerateFiles(dir, "bad.json.*.bad").Any(), "");

            var logs = new List<string>();
            var sl = new EqStore(dir, 30, logs.Add);
            File.WriteAllText(sl.PathOf("empty"), "");
            File.WriteAllText(sl.PathOf("jnull"), "null");
            bool fresh = sl.Load("empty").History.Count == 0 && sl.Load("jnull").History.Count == 0;
            Check("empty / null file -> .bad + logged, not silently fresh", fresh && !File.Exists(sl.PathOf("empty")) &&
                  Directory.EnumerateFiles(dir, "empty.json.*.bad").Any() && Directory.EnumerateFiles(dir, "jnull.json.*.bad").Any() &&
                  logs.Count == 2 && logs.All(l => l.Contains("unreadable")), string.Join(" | ", logs));
            File.WriteAllText(sl.PathOf("bad"), "{");
            sl.Load("bad");
            Check("two .bad files never overwrite each other", Directory.EnumerateFiles(dir, "bad.json.*.bad").Count() == 2, "");
            File.WriteAllText(sl.PathOf("nulls"), @"{""history"":[null,{""t"":1800000000,""cls"":""quit""},null],""lockout_expiry"":5}");
            var rn = sl.Load("nulls");
            Check("null history rows skipped on load", rn.History.Count == 1 && rn.LockoutExpiry == 5 &&
                  EqStanding.Compute(rn.History, Now, -1, P).Wq == 1, $"{rn.History.Count}");
            Check("is_match not serialized", !File.ReadAllText(st.PathOf("OVR-ORG-1")).Contains("is_match"), File.ReadAllText(st.PathOf("OVR-ORG-1")));
            var mx = new EqState { PenaltyTsSent = long.MaxValue };
            Check("penaltyts saturates at long.MaxValue", mx.NextPenaltyTs(Now) == long.MaxValue && mx.NextPenaltyTs(Now) == long.MaxValue, "");

            // file names: long / unicode / reserved / empty
            string id100 = new string('a', 100), id101 = new string('a', 101);
            string longA = new string('x', 60) + new string('a', 240), longB = new string('x', 60) + new string('b', 240);
            Check("file name: 100 chars kept, 101 hashed to 60 + _ + 16 hex", EqStore.FileName(id100) == id100 + ".json" &&
                  EqStore.FileName(id101).Length == 77 + 5 && EqStore.FileName(id101).StartsWith(new string('a', 60) + "_"), EqStore.FileName(id101));
            Check("file name: long ids with a shared 60-char prefix differ (file and lock)", EqStore.FileName(longA) != EqStore.FileName(longB) &&
                  !ReferenceEquals(st.Lock(longA), st.Lock(longB)) && ReferenceEquals(st.Lock(longA), st.Lock(new string(longA))), "");
            string uni = string.Concat(Enumerable.Repeat("\u00e9\u4e2d", 60));
            uni = System.Text.RegularExpressions.Regex.Unescape(uni);
            st.Save(longA, new EqState { LockoutExpiry = 7 }, Now); st.Save(uni, new EqState { LockoutExpiry = 8 }, Now);
            Check("300-char and 120-unicode-char ids round-trip", st.Load(longA).LockoutExpiry == 7 && st.Load(uni).LockoutExpiry == 8 &&
                  st.Load(longB).LockoutExpiry == -1, EqStore.FileName(uni));
            Check("file name: Windows device names prefixed", EqStore.FileName("CON") == "%_CON.json" && EqStore.FileName("nul") == "%_nul.json" &&
                  EqStore.FileName("COM9") == "%_COM9.json" && EqStore.FileName("LPT1") == "%_LPT1.json" && EqStore.FileName("COM10") == "COM10.json" &&
                  EqStore.FileName("CONX") == "CONX.json", EqStore.FileName("CON"));
            int argEx = 0;
            foreach (var bad in new string?[] { null, "" })
            {
                try { st.Load(bad!); } catch (ArgumentException) { argEx++; }
                try { st.Lock(bad!); } catch (ArgumentException) { argEx++; }
                try { st.Save(bad!, new EqState()); } catch (ArgumentException) { argEx++; }
            }
            Check("null / empty id -> ArgumentException (Load, Lock, Save)", argEx == 6, $"{argEx}/6");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void PolicyFile(Action<string, bool, string> Check)
    {
        var d = EqPolicy.Default;
        Check("defaults", d.WindowMatches == 20 && d.MaxAgeDays == 30 && d.EnableAt == 3 && d.LockoutAt == 5 &&
              d.LadderSec.SequenceEqual(new long[] { 300, 900, 3600, 21600, 86400 }) && d.SteadyLevels.Count == 4 &&
              d.SteadyLevels[3].MinRatio == 0.98 && d.StartCapPerHour == 3 && d.ClockToleranceSec == 5, "");
        string f = Path.Combine(Path.GetTempPath(), "eqpolicy-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            File.WriteAllText(f, @"{""lockout_at"": 7, ""ladder_sec"": [60, 120],
                ""steady_levels"": [{""level"": 0, ""min_matches"": 1, ""min_ratio"": 0.5}]}");
            var p = EqPolicy.Load(f);
            Check("policy file overrides, keeps the rest", p.LockoutAt == 7 && p.LadderSec.Length == 2 && p.SteadyLevels.Count == 1 &&
                  p.SteadyLevels[0].MinRatio == 0.5 && p.WindowMatches == 20 && p.GraceSec == 60, p.ToJson());
            Check("policy JSON keys are snake_case", d.ToJson().Contains("\"window_matches\"") && d.ToJson().Contains("\"min_ratio\""), "");
            Check("Load(null) = defaults", EqPolicy.Load(null).ToJson() == d.ToJson(), "");
            bool Rejects(string json)
            {
                File.WriteAllText(f, json);
                try { EqPolicy.Load(f); return false; } catch (InvalidDataException) { return true; }
            }
            Check("empty ladder rejected", Rejects(@"{""ladder_sec"": []}"), "");
            Check("ladder step 0 / negative rejected", Rejects(@"{""ladder_sec"": [300, 0]}") && Rejects(@"{""ladder_sec"": [-5]}"), "");
            Check("max_age_days 0 rejected", Rejects(@"{""max_age_days"": 0}"), "");
            Check("window_matches 0 rejected", Rejects(@"{""window_matches"": 0}"), "");
            Check("enable_at > lockout_at rejected, == accepted", Rejects(@"{""enable_at"": 6}") && !Rejects(@"{""enable_at"": 5}"), "");
            Check("negative grace rejected", Rejects(@"{""grace_sec"": -1}") && Rejects(@"{""start_grace_sec"": -1}"), "");
            File.WriteAllText(f, @"{""steady_levels"": [null, {""level"": 2, ""min_matches"": 1, ""min_ratio"": 0.5}]}");
            var pn = EqPolicy.Load(f);
            Check("null steady level skipped", pn.SteadyLevels.Count == 1 &&
                  EqStanding.Compute(Many("complete", 1, Now - Day), Now, -1, pn).SteadyLevel == 2, "");
            var cfg0 = (JArray)EarlyQuit.DefaultConfig["steady_player_levels"]!;
            Check("client config steady levels = policy defaults (level 0 at 80 %)", cfg0.Count == d.SteadyLevels.Count &&
                  cfg0.Zip(d.SteadyLevels).All(z => (int)z.First["steady_player_level"]! == z.Second.Level &&
                      (int)z.First["min_num_matches"]! == z.Second.MinMatches && (double)z.First["min_steady_ratio"]! == z.Second.MinRatio) &&
                  d.SteadyLevels[0].MinRatio == 0.80, cfg0.ToString(Newtonsoft.Json.Formatting.None));
        }
        finally { File.Delete(f); }
    }
}
