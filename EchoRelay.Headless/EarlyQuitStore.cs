// The v2 early-quit store, one JSON file per user, kept out of the profile (the profile is
// served to other clients). Writes are atomic (tmp + fsync + rename). Lock(user) is the per-user SemaphoreSlim that must
// wrap every Load -> change -> Save -> send; it is keyed by the file name, so the lock and the file always agree.
// A null / empty user id is an ArgumentException (callers catch it).
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

sealed class EqState
{
    public const int MaxHistory = 500;
    public List<EqEntry> History { get; set; } = new();   // decision order, oldest first
    public long LockoutExpiry { get; set; } = -1;          // Unix s, -1 none (mirrors the profile's earlyquit_lockout)
    public long PenaltyTsSent { get; set; } = -1;          // last penaltyts sent; every Update must go above it
    public bool Exempt { get; set; }                      // admin /exempt: detected leaves are not recorded, never refused

    // the next penaltyts: strictly above the last one sent (caller saves before sending); saturates at long.MaxValue
    public long NextPenaltyTs(long now) =>
        PenaltyTsSent = Math.Max(now, PenaltyTsSent == long.MaxValue ? long.MaxValue : PenaltyTsSent + 1);

    // in place, never drops a row that matters: rows older than max_age_days + 1 go, except the newest lockout row; then,
    // over the cap, neutral rows (not quit / complete / lockout / pulse / admin_forgive / admin_clear) oldest first, and
    // only then the oldest of the rest (the newest lockout row still kept)
    public static void Trim(List<EqEntry> h, long now, int maxAgeDays, int cap = MaxHistory)
    {
        var keep = h.LastOrDefault(e => e.Cls == EqClass.Lockout);
        long cut = now - (maxAgeDays + 1) * 86400L;
        h.RemoveAll(e => e.T < cut && !ReferenceEquals(e, keep));
        bool Protected(EqEntry e) => e.IsMatch || e.Cls is EqClass.Lockout or EqClass.Pulse or EqClass.AdminForgive or EqClass.AdminClear;
        for (int i = 0; h.Count > cap && i < h.Count;)
            if (!Protected(h[i])) h.RemoveAt(i); else i++;
        for (int i = 0; h.Count > cap && i < h.Count;)
            if (!ReferenceEquals(h[i], keep)) h.RemoveAt(i); else i++;
    }

    // null = empty / "null" / not an object (corrupt); null rows are skipped
    public static EqState? Parse(string json)
    {
        var s = JsonConvert.DeserializeObject<EqState>(json, EqPolicy.Json);
        if (s == null) return null;
        s.History ??= new();
        s.History.RemoveAll(e => e == null);
        return s;
    }
}

sealed class EqStore
{
    readonly string dir;
    readonly int maxAgeDays;
    readonly Action<string> log;
    readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    public EqStore(string dir, int maxAgeDays = 30, Action<string>? log = null)
    {
        this.dir = dir; this.maxAgeDays = maxAgeDays; this.log = log ?? (_ => { });
        Directory.CreateDirectory(dir);
    }

    public SemaphoreSlim Lock(string user) => locks.GetOrAdd(FileName(user), _ => new SemaphoreSlim(1, 1));

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // letters, digits, '-' and '_' kept; anything else as %XX of its UTF-8 bytes, so distinct ids never collide. Escaped
    // names over 100 chars: first 60 + "_" + 16 hex of SHA-256(id). Windows device names get a "%_" prefix (escaping
    // never yields a '%' without two hex digits, so the prefix cannot collide).
    public static string FileName(string user)
    {
        if (string.IsNullOrEmpty(user)) throw new ArgumentException("early-quit store: empty user id");
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(user))
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'_')
                sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("x2"));
        string n = sb.ToString();
        if (n.Length > 100)
            n = n[..60] + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16].ToLowerInvariant();
        if (Reserved.Contains(n)) n = "%_" + n;
        return n + ".json";
    }

    public string PathOf(string user) => Path.Combine(dir, FileName(user));

    // missing file = fresh state; an empty or unreadable file is moved aside (.bad), logged, and a fresh state returned
    public EqState Load(string user)
    {
        string path = PathOf(user);
        if (!File.Exists(path)) return new EqState();
        string why;
        try
        {
            if (EqState.Parse(File.ReadAllText(path)) is EqState s) return s;
            why = "empty / null";
        }
        catch (JsonException e) { why = e.Message; }
        string bad = $"{path}.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{Guid.NewGuid().ToString("n")[..8]}.bad";
        try { File.Move(path, bad, true); } catch (IOException e) { bad = $"(move failed: {e.Message})"; }
        log($"[EARLYQUIT] store: {user} file unreadable ({why}) -> {bad}, fresh state");
        return new EqState();
    }

    // trims History (in place, EqState.Trim), then tmp (fsynced) + rename
    public void Save(string user, EqState s, long? now = null)
    {
        EqState.Trim(s.History, now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), maxAgeDays);
        string path = PathOf(user), tmp = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(s, Formatting.None, EqPolicy.Json));
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(b);
                fs.Flush(true);
            }
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
