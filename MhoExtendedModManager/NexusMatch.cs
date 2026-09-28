using System.Text.Json;
using System.Text.RegularExpressions;

namespace MhoExtendedModManager;

/// <summary>
/// Finding installed mods on Nexus by name (Kurt: scan the mod names, list likely Nexus pages, link after confirmation).
/// The Nexus v1 API has no search, so the game's whole mod list comes from the public v2 GraphQL API (no key; 343 mods
/// on 2026-09-27, 100 per request) and is matched here: words of the names (without filler such as "costume" or
/// "visual update", with "VU" read as "visual update", without version numbers), Dice overlap, plus a bonus when the
/// author matches the Nexus author or uploader ("Wlzzer" ~ "WIzzerMH").
/// </summary>
static class NexusMatch
{
    public sealed record NexusMod(int ModId, string Name, string Author, string Uploader, string Version, DateTime Updated, string Summary);
    public sealed record Candidate(NexusMod Mod, double Score);


    /// <summary>Every Marvel Heroes Omega mod on Nexus (MHO_EXTMM_NEXUS_API: graphql_mods.json in that folder, for tests).</summary>
    public static async Task<List<NexusMod>> AllMods(IProgress<string>? progress = null)
    {
        var list = new List<NexusMod>();
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_API") is string fake)
        {
            foreach (var n in JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fake, "graphql_mods.json"))).RootElement.EnumerateArray()) list.Add(Read(n));
            return list;
        }
        const string query = "query($f: ModsFilter, $o: Int){ mods(filter:$f, count:100, offset:$o){ totalCount nodes { modId name version author uploader { name } summary updatedAt } } }";
        // Pages can hold fewer than asked for: step by what arrived (stepping by 100 missed 60 of 343 mods).
        for (int offset = 0, total = int.MaxValue; offset < total; )
        {
            progress?.Report($"Reading the Nexus Mod List ({offset}…)");
            var data = await Nexus.GraphQl(query, new { f = new { gameDomainName = new[] { new { value = Nexus.Game, op = "EQUALS" } } }, o = offset });
            var mods = data.GetProperty("mods");
            total = mods.GetProperty("totalCount").GetInt32();
            int got = 0;
            foreach (var n in mods.GetProperty("nodes").EnumerateArray()) { list.Add(Read(n)); got++; }
            if (got == 0) break;
            offset += got;
        }
        return list;
    }

    static NexusMod Read(JsonElement n)
    {
        string S(string p) => n.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string uploader = n.TryGetProperty("uploader", out var u) && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("name", out var un) ? un.GetString() ?? "" : "";
        return new NexusMod(n.GetProperty("modId").GetInt32(), S("name"), S("author"), uploader, S("version"),
                            DateTime.TryParse(S("updatedAt"), out var d) ? d : DateTime.MinValue, S("summary"));
    }

    static readonly HashSet<string> Filler = ["the", "a", "an", "of", "and", "for", "by", "mod", "mods", "costume", "costumes", "visual", "update", "updated", "version", "replace", "replacement", "replacer", "with"];

    /// <summary>A name's meaningful words: lower case, "VU" as visual update, no filler, no version numbers.</summary>
    public static HashSet<string> Words(string name)
    {
        string s = Regex.Replace(name.ToLowerInvariant().Replace("'", ""), @"[^a-z0-9.]+", " ");
        var words = new HashSet<string>();
        foreach (string raw in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string w = raw.Trim('.');
            if (w.Length == 0 || w == "vu" || Filler.Contains(w)) continue;
            if (Regex.IsMatch(w, @"^v?\d+(\.\d+)+$") || Regex.IsMatch(w, @"^v\d+$")) continue;   // 1.2.1, v2 (keeps 97, 1602, 90s)
            words.Add(w);
        }
        return words;
    }

    static string Person(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "").Replace("l", "i");   // Wlzzer ~ WIzzer (l / I look alike)

    public static bool SameAuthor(string? local, NexusMod n)
    {
        string a = Person(local ?? "");
        if (a.Length < 3) return false;
        return new[] { n.Author, n.Uploader }.Select(Person).Any(b => b.Length >= 3 && (a == b || a.StartsWith(b) || b.StartsWith(a)));
    }

    /// <summary>How alike two mod names are, 0–1 (1 = the same words).</summary>
    public static double NameScore(string local, string nexus)
    {
        var a = Words(local); var b = Words(nexus);
        if (a.Count == 0 || b.Count == 0) return 0;
        if (a.SetEquals(b)) return 1;
        return 2.0 * a.Intersect(b).Count() / (a.Count + b.Count);
    }

    /// <summary>The likely Nexus pages for a mod, best first (score at least 0.35; at most <paramref name="max"/>).</summary>
    public static List<Candidate> Candidates(Mod m, IEnumerable<NexusMod> all, int max = 5)
    {
        // The mod's characters: from its content (automatic tags) and its name. A Nexus title naming only other characters
        // is a different mod ("Starlord Infinity War" isn't "Capitan America Infinity war").
        var mine = m.AutoTags.Where(t => AutoTags.Classify(t) == AutoTags.TagClass.Character).Select(t => t.ToLowerInvariant()).Concat(AutoTags.CharactersIn(m.Name)).ToHashSet();
        double Score(NexusMod n)
        {
            double s = NameScore(m.Name, n.Name) + (SameAuthor(m.Manifest.Author, n) ? 0.15 : 0);
            var theirs = AutoTags.CharactersIn(n.Name);
            if (mine.Count > 0 && theirs.Count > 0 && !theirs.Overlaps(mine)) s -= 0.4;
            return Math.Min(1, s);
        }
        return all.Select(n => new Candidate(n, Score(n))).Where(c => c.Score >= 0.35)
                  .OrderByDescending(c => c.Score).ThenByDescending(c => c.Mod.Updated).Take(max).ToList();
    }

    /// <summary>Strong enough to tick in advance: a high score, clearly ahead of the next one.</summary>
    public static bool Confident(List<Candidate> c) => c.Count > 0 && c[0].Score >= 0.75 && (c.Count == 1 || c[0].Score - c[1].Score >= 0.1);
}
