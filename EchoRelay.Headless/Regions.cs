// Server-region map (docs/regions.md).
// A game server registers CSymbol64(server_region) - its _local/config.json "server_region" key, or "-serverregion
// <code>" on its command line; "default" when neither is set (echovr.exe PC 6323983201049540). A client create carries
// CSymbol64(-region <code>) (LobbyCreateSessionRequestv9.Unk0). The p40 deep-link wizard sends the LABEL ("US WEST").
// Codes are the shipped clanforge_<code> datacenter literals (echovr.exe / pnsrad.dll / libr15.so, final builds);
// JAPAN has no shipped code ("jp" is ours). Accepts a label, a code, "clanforge_<code>", a decimal or a 0x symbol.
static class Regions
{
    public sealed record Region(string Code, string Label);

    // Order = the p40 language table's labels; codes from the shipped clanforge_* literals.
    public static readonly Region[] All =
    {
        new("usw", "US WEST"), new("usc", "US CENTRAL SOUTH"), new("uscn", "US CENTRAL NORTH"), new("use", "US EAST"),
        new("euw", "EU WEST"), new("euc", "EU CENTRAL"), new("sin", "ASIA SOUTH"), new("jp", "JAPAN"),
        new("aus", "AUSTRALIA"), new("lat", "LATIN AMERICA"), new("default", "DEFAULT"),
    };

    static Func<string, long> sym = null!;
    public static void Init(Func<string, long> symbol) => sym = symbol;

    public static Region? Find(string s)
    {
        string t = s.Trim();
        if (t.StartsWith("clanforge_", StringComparison.OrdinalIgnoreCase)) t = t[10..];
        return All.FirstOrDefault(r => string.Equals(r.Code, t, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(r.Label, t, StringComparison.OrdinalIgnoreCase));
    }

    // NAME|LABEL|0xHEX -> the symbol a game server registers. Unknown names hash as-is (a custom -serverregion).
    public static long Symbol(string s) => Find(s) is { } r ? sym(r.Code) : sym(s.Trim());

    public static string Name(long symbol) =>
        All.FirstOrDefault(r => sym(r.Code) == symbol)?.Code ?? (symbol == -1 ? "(none)" : $"0x{symbol:x16}");

    public static long Default => sym("default");
}
