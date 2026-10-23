// The invite table behind Invites.cs, without the Server: codes, the 410 "gone" list, the JSON store, the creator
// check on revoke, and the caller binding of a services user_id (InviteAuth). Kept apart so EchoRelay.Core.Test can
// drive it with a fake clock, a temp store and fake login peers. Design: docs/invites.md.
using System.Collections.Concurrent;
using System.Net;
using EchoRelay.Core.Server.Messages.ServerDB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HttpError = ControlApi.HttpError;

// One invite. ServerId is the current LEASE (runtime only, never stored): null after a host restart or after its game
// server left, until the next resolve leases an idle server again (same code, link, password, expiry).
sealed record Invite(string Code, Guid Link, string Creator, string GameType, string? Level, string? Region,
    long GameTypeSym, long? LevelSym, long? RegionSym, ERGameServerStartSession.SessionSettings? Settings, byte PlayerLimit,
    byte[]? PasswordHash, DateTime Created, DateTime Expires)
{
    [JsonIgnore] public ulong? ServerId { get; init; }
}

sealed class InviteBook
{
    public const int StoreVersion = 1;
    public static readonly TimeSpan GoneFor = TimeSpan.FromHours(1);
    public readonly ConcurrentDictionary<string, Invite> ByCode = new();
    // Dropped codes answer 410 with the reason for an hour (and are not reissued meanwhile), instead of a bare 404.
    public readonly ConcurrentDictionary<string, (string Why, DateTime At)> Gone = new();
    readonly string? storePath;
    readonly Func<DateTime> now;
    readonly Action<string> log;
    readonly object saveLock = new();
    // Called after an invite left the table (expired / revoked / dropped): Invites.cs removes its link alias here.
    public Action<Invite, string>? OnDropped;
    // Called by Revoke for a leased invite: ends the private session now (Invites.cs kicks its players); returns the
    // number of players kicked.
    public Func<Invite, Task<int>>? Kick;

    public InviteBook(string? storePath, Func<DateTime>? clock = null, Action<string>? logger = null)
    {
        this.storePath = storePath; now = clock ?? (() => DateTime.UtcNow); log = logger ?? (_ => { });
    }

    public DateTime Now => now();

    // Restore the store: expired invites move to the gone list, gone entries older than an hour are forgotten, and
    // every restored invite starts UNLEASED (the registry is empty after a restart). A file that does not parse is
    // moved aside to <file>.bad and the table starts empty. Returns the number of live invites restored.
    public int Load()
    {
        if (storePath == null || !File.Exists(storePath)) return 0;
        JObject root;
        try { root = JObject.Parse(File.ReadAllText(storePath)); }
        catch (Exception e)
        {
            string bad = storePath + ".bad";
            try { File.Move(storePath, bad, true); } catch (IOException m) { bad = $"(move failed: {m.Message})"; }
            log($"[INVITES] store {storePath} unreadable ({e.Message}); moved to {bad}, starting empty");
            return 0;
        }
        DateTime t = now();
        int expired = 0;
        foreach (var g in root["gone"] as JArray ?? new JArray())
            if ((string?)g["code"] is string c && (DateTime?)g["at"] is DateTime at && at > t - GoneFor)
                Gone[c] = ((string?)g["why"] ?? "dropped", at.ToUniversalTime());
        foreach (var j in root["invites"] as JArray ?? new JArray())
        {
            var inv = j.ToObject<Invite>();
            if (inv == null || string.IsNullOrEmpty(inv.Code)) continue;
            if (inv.Expires <= t) { expired++; if (inv.Expires > t - GoneFor) Gone[inv.Code] = ("expired", inv.Expires); continue; }
            ByCode[inv.Code] = inv with { ServerId = null };
        }
        log($"[INVITES] store {storePath}: restored {ByCode.Count} invite(s) unleased, {expired} expired, {Gone.Count} gone code(s)");
        if (expired > 0) Save();
        return ByCode.Count;
    }

    // Atomic: tmp + fsync + rename, so a crash leaves the old file or the new one, never half of one.
    public void Save()
    {
        if (storePath == null) return;
        lock (saveLock)
        {
            var root = new JObject
            {
                ["version"] = StoreVersion,
                ["invites"] = new JArray(ByCode.Values.OrderBy(i => i.Created).Select(i => JObject.FromObject(i))),
                ["gone"] = new JArray(Gone.OrderBy(kv => kv.Value.At).Select(kv =>
                    new JObject { ["code"] = kv.Key, ["why"] = kv.Value.Why, ["at"] = kv.Value.At })),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
            string tmp = storePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new StreamWriter(fs))
            {
                w.Write(root.ToString(Formatting.Indented));
                w.Flush();
                fs.Flush(true);
            }
            File.Move(tmp, storePath, true);
        }
    }

    public bool Taken(string code) => ByCode.ContainsKey(code) || Gone.ContainsKey(code);

    public void Add(Invite inv) { ByCode[inv.Code] = inv; Save(); }

    // The lease is runtime state: no save.
    public Invite? SetLease(string code, ulong? serverId)
    {
        while (ByCode.TryGetValue(code, out var cur))
            if (ByCode.TryUpdate(code, cur with { ServerId = serverId }, cur)) return ByCode[code];
        return null;
    }

    public Invite Lookup(string c)
    {
        string code = c.ToUpperInvariant();
        if (!ByCode.TryGetValue(code, out var inv))
            throw Gone.TryGetValue(code, out var gone) ? new HttpError(410, $"invite {code} {gone.Why}")
                                                        : new HttpError(404, $"no invite {code}");
        if (inv.Expires <= now()) { Drop(inv, "expired"); throw new HttpError(410, $"invite {code} expired"); }
        return inv;
    }

    // user == null: operator revoke (control API). Anyone else but the creator: 403, nothing dropped, nobody kicked.
    public async Task<(Invite Invite, int Kicked)> Revoke(string c, string? user)
    {
        var inv = Lookup(c);
        if (user != null && user != inv.Creator) throw new HttpError(403, "only the creator can revoke");
        var dropped = Drop(inv, "revoked");
        if (dropped?.ServerId == null || Kick == null) return (dropped ?? inv, 0);
        return (dropped, await Kick(dropped));
    }

    // Returns the removed record (with its lease), or null when another caller dropped it first.
    public Invite? Drop(Invite inv, string why)
    {
        if (!ByCode.TryRemove(inv.Code, out var cur)) return null;
        Gone[inv.Code] = (why, now());
        Save();
        OnDropped?.Invoke(cur, why);
        return cur;
    }

    // The 5 s sweep: expire invites, forget gone codes after an hour.
    public void Sweep()
    {
        DateTime t = now();
        foreach (var inv in ByCode.Values.Where(i => i.Expires <= t).ToArray()) Drop(inv, "expired");
        foreach (var kv in Gone.Where(kv => kv.Value.At < t - GoneFor).ToArray()) Gone.TryRemove(kv.Key, out _);
    }
}

// H-4: a services create / revoke names a user_id; accept it only when that id has a live login session (a peer on
// the login service right now) whose remote address is the HTTP caller's. The game's own wizard calls from the same
// machine that holds the login websocket, so this costs it nothing; another LAN machine that only knows the id is
// refused. Loopback equals loopback (127.0.0.1 / ::1 / ::ffff:127.0.0.1). Behind a reverse proxy every caller has the
// proxy's address, so the binding is only as good as the proxy (docs/invites.md).
static class InviteAuth
{
    public static string? Refusal(string userId, IPAddress caller, IEnumerable<(string? UserId, IPAddress Address)> logins)
    {
        var mine = logins.Where(l => l.UserId == userId).ToList();
        if (mine.Count == 0) return $"{userId} is not logged in";
        if (!mine.Any(l => SameHost(l.Address, caller))) return $"{userId} is not logged in from this address";
        return null;
    }

    public static bool SameHost(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return (IPAddress.IsLoopback(a) && IPAddress.IsLoopback(b)) || a.Equals(b);
    }
}
