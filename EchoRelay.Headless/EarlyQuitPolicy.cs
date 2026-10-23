// The pure early-quit policy (no I/O but the policy file). Plain types only: string user ids,
// Guid sessions, ulong server ids, Unix seconds, int team index. Everything is derived from the history (§ 9 / review
// § E): the store keeps only the history, the lockout expiry and the last penaltyts sent.
//   * EqClassifier.Classify: one removal + what happened within the grace -> class, reason, would-flags.
//   * EqStanding: the 20-match window, state, derived rung, steady level, notice owed, lockout / hold / gate.
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

sealed class EqLevel
{
    public int Level { get; set; }
    public int MinMatches { get; set; }
    public double MinRatio { get; set; }
}

sealed class EqPolicy
{
    public int WindowMatches { get; set; } = 20;
    public int MaxAgeDays { get; set; } = 30;
    public int EnableAt { get; set; } = 3;
    public int LockoutAt { get; set; } = 5;
    public long[] LadderSec { get; set; } = { 300, 900, 3600, 21600, 86400 };
    public long ExtendCapSec { get; set; } = 86400;
    public int RungWindowDays { get; set; } = 7;
    public int RungDecayCompletes { get; set; } = 5;
    public int GraceSec { get; set; } = 60;
    public int EndBurstSec { get; set; } = 15;
    public int StartGraceSec { get; set; } = 30;
    public int StartCapPerHour { get; set; } = 3;      // the Nth start leave in a sliding hour is a quit
    public int ExitWindowSec { get; set; } = 5;
    public int TogetherSec { get; set; } = 10;
    public int CascadeWindowSec { get; set; } = 120;
    // the arrival pulse holds bit 5 from the social accept to pulse_hold_sec after the client's LOAD_STATS, at most
    // arrival_max_sec (no LOAD_STATS: a crash or a client that does not log). pulse_delay_sec is gone (device T2).
    public int ArrivalMaxSec { get; set; } = 120;
    public int PulseHoldSec { get; set; } = 2;     // after LOAD_STATS; the podium is up ~13 s before it (device T2), OK is a no-op while bit 5 is on
    public int ClockToleranceSec { get; set; } = 5;
    public List<EqLevel> SteadyLevels { get; set; } = new()
    {
        new() { Level = 0, MinMatches = 10, MinRatio = 0.80 }, new() { Level = 1, MinMatches = 10, MinRatio = 0.90 },
        new() { Level = 2, MinMatches = 20, MinRatio = 0.95 }, new() { Level = 3, MinMatches = 20, MinRatio = 0.98 },
    };

    public static readonly JsonSerializerSettings Json = new()
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        ObjectCreationHandling = ObjectCreationHandling.Replace,   // a list in the file replaces the default list
        NullValueHandling = NullValueHandling.Ignore,
    };

    public static EqPolicy Default => new();

    // null path = defaults; keys missing from the file keep their defaults
    public static EqPolicy Load(string? path)
    {
        if (path == null) return Default;
        var p = JsonConvert.DeserializeObject<EqPolicy>(File.ReadAllText(path), Json) ?? Default;
        p.Validate(path);
        return p;
    }

    // a bad policy file is a host startup failure with a clear message
    public void Validate(string what = "defaults")
    {
        void Need(bool ok, string msg) { if (!ok) throw new InvalidDataException($"early-quit policy {what}: {msg}"); }
        Need(LadderSec is { Length: > 0 } && LadderSec.All(d => d > 0), "ladder_sec must be non-empty, every step > 0");
        Need(WindowMatches >= 1 && MaxAgeDays >= 1, "window_matches and max_age_days must be >= 1");
        Need(LockoutAt > 0 && StartCapPerHour > 0 && RungDecayCompletes > 0,
             "lockout_at, start_cap_per_hour and rung_decay_completes must be > 0");
        Need(EnableAt <= LockoutAt, $"enable_at {EnableAt} > lockout_at {LockoutAt}");
        Need(new long[] { GraceSec, EndBurstSec, StartGraceSec, ExitWindowSec, TogetherSec, CascadeWindowSec, ArrivalMaxSec,
                          PulseHoldSec, ClockToleranceSec, ExtendCapSec, RungWindowDays }.All(v => v >= 0),
             "graces, windows and delays must be >= 0");
        SteadyLevels ??= new();
        SteadyLevels.RemoveAll(l => l == null);   // "steady_levels": [null] = skipped
    }

    public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented, Json);
}

// one history row. Cls: quit, complete, abandoned, start, spectator, server, lockout, pulse, admin_forgive, admin_clear
sealed record EqEntry
{
    public long T { get; init; }
    public string Cls { get; init; } = "";
    public string? Session { get; init; }
    public string? Reason { get; init; }
    public long? Expiry { get; init; }       // lockout rows: the expiry it set
    public string[]? Would { get; init; }

    [JsonIgnore] public bool IsMatch => Cls is EqClass.Quit or EqClass.Complete;
}

static class EqClass
{
    public const string Quit = "quit", Complete = "complete", Abandoned = "abandoned", Start = "start",
                        Spectator = "spectator", Server = "server", Lockout = "lockout", Pulse = "pulse",
                        AdminForgive = "admin_forgive", AdminClear = "admin_clear",
                        Rejoin = "rejoin";   // re-added to the same session while a removal was pending: neutral, never a match row
}

sealed record EqRemoval(string User, ulong Server, Guid Session, int Team, long AcceptedAt, long RemovedAt,
                        long? LoginClosedAt = null);

// one removal awaiting its grace. Done 0 -> 1 exactly once (Interlocked): resolved, or cancelled by a rejoin
class EqPending
{
    public required EqRemoval R;
    public int Done;
    public bool Rejoined;
    public bool TryFinish() => Interlocked.Exchange(ref Done, 1) == 0;
}

static class EqPendingOps
{
    // provisional-hold count: unresolved, this user, never a spectator / moderator removal (those can only be spectator)
    public static int HoldCount(IEnumerable<EqPending> ps, string user) =>
        ps.Count(p => p.Done == 0 && p.R.User == user && p.R.Team is not (EqClassifier.TeamSpectator or EqClassifier.TeamModerator));

    // a re-add to the same (server, session): cancels that user's unresolved removals there (the caller locks the list
    // and writes one rejoin row); the grace / end that finds them later sees Done and records nothing for them
    public static List<EqPending> Rejoin(IEnumerable<EqPending> sessionPs, string user)
    {
        var hit = sessionPs.Where(p => p.R.User == user && p.Done == 0).Where(p => p.TryFinish()).ToList();
        foreach (var p in hit) p.Rejoined = true;
        return hit;
    }
}

// what happened within the grace after the removal
sealed record EqOutcome(long? SessionEndedAt, long? ServerUnregisteredAt, bool StillLiveAfterGrace);

static class EqClassifier
{
    public const int TeamSpectator = 2, TeamModerator = 4;

    // first match wins (§ 9): spectator, server, complete, abandoned, start (start_cap -> quit), quit.
    // Would-flags are set on quits only and never change the class.
    public static (string Cls, string Reason, string[] Would) Classify(EqRemoval r, EqOutcome o,
        IReadOnlyList<EqRemoval> sameSessionRemovals, IReadOnlyList<EqEntry> history, EqPolicy p)
    {
        if (r.Team is TeamSpectator or TeamModerator)
            return (EqClass.Spectator, r.Team == TeamSpectator ? "team_spectator" : "team_moderator", Array.Empty<string>());
        if (o.ServerUnregisteredAt is long u && u <= r.RemovedAt + p.GraceSec)
            return (EqClass.Server, "server_unregistered", Array.Empty<string>());
        if (o.SessionEndedAt is long end && end <= r.RemovedAt + p.GraceSec)
            return end - r.RemovedAt <= p.EndBurstSec
                ? (EqClass.Complete, "end_burst", Array.Empty<string>())
                : (EqClass.Abandoned, "ended_in_grace", Array.Empty<string>());
        if (!o.StillLiveAfterGrace)      // gone without an end or unregister event: neutral
            return (EqClass.Abandoned, "not_live", Array.Empty<string>());
        if (r.AcceptedAt <= 0)           // no seat seen (e.g. seated before a host restart): accept time unknown, neutral
            return (EqClass.Abandoned, "no_seat", Array.Empty<string>());

        string cls = EqClass.Quit, reason = "live_after_grace";
        if (IsStartLeave(r, p))
        {
            // start leaves in the sliding hour before this one: start rows plus earlier start_cap quits
            int prior = history.Count(e => e.T > r.RemovedAt - 3600 && e.T <= r.RemovedAt &&
                                           (e.Cls == EqClass.Start || e.Cls == EqClass.Quit && e.Reason == "start_cap"));
            if (prior + 1 < p.StartCapPerHour) return (EqClass.Start, "start_grace", Array.Empty<string>());
            reason = "start_cap";
        }

        var would = new List<string>();
        if (r.LoginClosedAt is long lc && lc >= r.AcceptedAt && lc <= r.RemovedAt + p.ExitWindowSec) would.Add("would_exit");
        if (WouldCascade(r, sameSessionRemovals, p)) would.Add("would_cascade");
        return (cls, reason, would.ToArray());
    }

    static bool IsStartLeave(EqRemoval r, EqPolicy p) => r.RemovedAt - r.AcceptedAt <= p.StartGraceSec;

    // a leave 10-120 s after an EARLIER quit candidate of the same session. Candidates (not spectator, not a start
    // leave) are sorted by removal time and chained into groups (gap < together_sec); no one in the first group is
    // ever flagged, and a group never exempts its own members.
    static bool WouldCascade(EqRemoval r, IReadOnlyList<EqRemoval> same, EqPolicy p)
    {
        var c = same.Where(x => x.Session == r.Session && !(x.User == r.User && x.RemovedAt == r.RemovedAt) &&
                                x.RemovedAt <= r.RemovedAt && x.Team is not (TeamSpectator or TeamModerator) &&
                                !IsStartLeave(x, p))
                    .Select(x => x.RemovedAt).Append(r.RemovedAt).OrderBy(t => t).ToList();
        int group = 0;
        long groupStart = c[0];
        for (int i = 1; i < c.Count; i++)
            if (c[i] - c[i - 1] >= p.TogetherSec) { group++; groupStart = c[i]; }
        // r is last in c (ties sort into its group); its group starts at groupStart
        if (group == 0) return false;
        return c.Any(t => t < groupStart && r.RemovedAt - t <= p.CascadeWindowSec);
    }
}

// SteadyLevel -1 = none. Wire (SteadyLevelWire) = max(0, level): none and level 0 (Steadfast, >= 80 %) both send 0,
// so the client sees no difference between them; levels 1-3 send 1-3.
sealed record EqSnapshot(int Wq, int CompletedInWindow, int MatchesInWindow, string State, int Rung, int SteadyLevel,
                         bool NoticeOwed)
{
    public int SteadyLevelWire => Math.Max(0, SteadyLevel);
}

static class EqStanding
{
    public const string Good = "good", Warned = "warned", StateLocked = "locked";

    // history after the last admin_clear, minus quits an admin_forgive cancelled (same session, else the newest
    // earlier quit). List order is decision order.
    public static List<EqEntry> Effective(IReadOnlyList<EqEntry> h)
    {
        int start = 0;
        for (int i = h.Count - 1; i >= 0; i--) if (h[i].Cls == EqClass.AdminClear) { start = i + 1; break; }
        var keep = Enumerable.Range(start, h.Count - start).ToList();
        var dropped = new HashSet<int>();
        foreach (int i in keep.Where(i => h[i].Cls == EqClass.AdminForgive))
        {
            int hit = keep.Where(j => j < i && !dropped.Contains(j) && h[j].Cls == EqClass.Quit &&
                                      (h[i].Session == null || h[j].Session == h[i].Session))
                          .DefaultIfEmpty(-1).Max();
            if (hit >= 0) dropped.Add(hit);
        }
        return keep.Where(i => !dropped.Contains(i)).Select(i => h[i]).ToList();
    }

    // the last window_matches quit / complete rows no older than max_age_days
    static List<EqEntry> Window(List<EqEntry> eff, long now, EqPolicy p) =>
        eff.Where(e => e.IsMatch && e.T >= now - p.MaxAgeDays * 86400L)
           .OrderBy(e => e.T).TakeLast(p.WindowMatches).ToList();

    // real lockouts (reason "shadow" = would-be, not counted) in the sliding rung window minus floor(completes since the last counted quit / rung_decay_completes)
    static int Rung(List<EqEntry> eff, long now, EqPolicy p)
    {
        int lockouts = eff.Count(e => e.Cls == EqClass.Lockout && e.Reason != "shadow" &&
                                    e.T > now - p.RungWindowDays * 86400L);
        int lastQuit = eff.FindLastIndex(e => e.Cls == EqClass.Quit);
        int completes = eff.Skip(lastQuit + 1).Count(e => e.Cls == EqClass.Complete);
        return Math.Clamp(lockouts - completes / p.RungDecayCompletes, 0, p.LadderSec.Length - 1);
    }

    public static EqSnapshot Compute(IReadOnlyList<EqEntry> h, long now, long lockoutExpiry, EqPolicy p)
    {
        var eff = Effective(h);
        var w = Window(eff, now, p);
        int wq = w.Count(e => e.Cls == EqClass.Quit), done = w.Count - wq;
        string state = lockoutExpiry > now ? StateLocked : wq >= p.EnableAt ? Warned : Good;

        int level = -1;
        foreach (var l in p.SteadyLevels)
            if (w.Count >= l.MinMatches && w.Count > 0 && (double)done / w.Count >= l.MinRatio && l.Level > level)
                level = l.Level;

        int lastPulse = eff.FindLastIndex(e => e.Cls == EqClass.Pulse);
        bool owed = state != Good && eff.FindLastIndex(e => e.Cls == EqClass.Quit) > lastPulse ||
                    state == StateLocked && eff.FindLastIndex(e => e.Cls == EqClass.Lockout) > lastPulse;
        return new EqSnapshot(wq, done, w.Count, state, Rung(eff, now, p), level, owed);
    }

    // h must already hold this counted quit (as its newest quit row). Returns the new lockout expiry, or null when wq
    // stays below lockout_at. The rung is read from the history before this quit, so completes since the previous
    // quit decay it. The caller then appends a lockout row (Expiry = result); extensions are lockout rows too.
    public static long? OnCountedQuit(IReadOnlyList<EqEntry> h, long now, long lockoutExpiry, EqPolicy p)
    {
        var eff = Effective(h);
        if (Window(eff, now, p).Count(e => e.Cls == EqClass.Quit) < p.LockoutAt) return null;
        int self = eff.FindLastIndex(e => e.Cls == EqClass.Quit);
        if (self >= 0) eff.RemoveAt(self);
        long dur = p.LadderSec[Rung(eff, now, p)];
        // an extension never shortens a lockout already beyond the cap (admin / older policy)
        return lockoutExpiry > now ? Math.Max(lockoutExpiry, Math.Min(lockoutExpiry + dur, now + p.ExtendCapSec)) : now + dur;
    }

    // admin_forgive `f` appended to h: true when it cancels the quit that produced the current lockout (the lockout row
    // right after that quit, expiry == currentExpiry, still running) and no other lockout row would still be running.
    // row = that lockout row's index in h.
    public static bool ForgiveEndsLockout(IReadOnlyList<EqEntry> h, EqEntry f, long now, long currentExpiry, out int row)
    {
        row = -1;
        var before = Effective(h);
        var after = Effective(h.Append(f).ToList());
        var gone = before.FirstOrDefault(e => !after.Any(a => ReferenceEquals(a, e)));
        int q = gone == null ? -1 : h.ToList().FindIndex(e => ReferenceEquals(e, gone));
        if (q < 0 || q + 1 >= h.Count || h[q + 1].Cls != EqClass.Lockout) return false;
        var lk = h[q + 1];
        if (lk.Expiry is not long x || x <= now || x != currentExpiry) return false;
        if (after.Any(e => e.Cls == EqClass.Lockout && !ReferenceEquals(e, lk) && e.Expiry > now)) return false;
        row = q + 1;
        return true;
    }

    // lobby arrival, per login peer: a notice from history, or a running lockout not yet pulsed to THIS peer (a relogin is
    // a new peer, so it is owed again)
    public static bool OwedTo(bool historyOwed, bool locked, long? shownExpiry, long lockoutExpiry) =>
        historyOwed || locked && shownExpiry != lockoutExpiry;

    // provisional hold: refuse public FIND / JOIN while a removal is pending and could reach the lockout
    public static bool HoldActive(int wq, int pending, EqPolicy p) => pending > 0 && wq + pending >= p.LockoutAt;

    // gate side: the client counts down on its own clock, so allow clock_tolerance_sec
    public static bool Locked(long expiry, long now, EqPolicy p) => expiry - p.ClockToleranceSec > now;
}
