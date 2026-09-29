using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// A mod's release post, built from the mod itself (Kurt, 2026-09-27): a Nexus description in BBCode and a Discord
/// message in Markdown (under Discord's 2,000 characters). Both carry the name, version and author, the hero and team,
/// what it changes (costume slots from the package names, icons, store images, strings, sounds), the description, this
/// version's changes, requirements and install steps for both managers. The window lets the user edit before copying.
/// </summary>
static class PostWriter
{
    public const string ManagerUrl = "https://github.com/leeper48/MHO-UPK-Tools/releases/latest";
    public const int DiscordLimit = 2000;

    /// <summary>What a post is made from: the manifest (a saved mod's, or a draft's preview) and the string count.</summary>
    public sealed record Source(ModManifest Manifest, int StringCount, IReadOnlyCollection<string> Languages);

    public static Source From(Mod m) => new(m.Manifest, m.Strings.Count, m.Strings.Select(s => s.Language).Distinct().ToList());
    public static Source From(ModDraft d) => new(d.Preview(), d.Strings.Count, d.Strings.Select(s => s.Language).Distinct().ToList());

    sealed record Facts(string Name, string Version, string Author, List<string> Characters, List<string> Teams, List<string> Kinds,
                        List<string> Changes, List<string> Replaces, string Description, ChangelogEntry? Latest, List<ChangelogEntry> Older,
                        bool UsesExtension, List<string> ModTags);

    static Facts Gather(Source s)
    {
        var m = s.Manifest;
        var auto = AutoTags.For(m);
        var chars = auto.Where(t => AutoTags.Classify(t) == AutoTags.TagClass.Character).ToList();
        var teams = auto.Where(t => AutoTags.Classify(t) == AutoTags.TagClass.Team).ToList();
        var kinds = auto.Where(t => AutoTags.Classify(t) == AutoTags.TagClass.Content).ToList();

        // Costume and team-up slots from the package names: UC__MarvelPlayer_<Hero>_<Costume>_SF, UC__MarvelTeamUp_<Name>_SF.
        var replaces = new List<string>();
        foreach (string f in m.UpkReplacements)
        {
            var p = Path.GetFileNameWithoutExtension(f).Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 3 && p[0].Equals("UC", StringComparison.OrdinalIgnoreCase) && p[1].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase))
            {
                string hero = AutoTags.DisplayName(p[2]) ?? Spaced(p[2]);
                var costume = p.Skip(3).Where(x => !x.Equals("SF", StringComparison.OrdinalIgnoreCase)).ToList();
                string line = costume.Count > 0 ? $"{hero}'s {Spaced(string.Join(" ", costume))} costume" : $"{hero}'s shared files (used by every costume)";
                if (!replaces.Contains(line)) replaces.Add(line);
            }
            else if (p.Length >= 3 && p[1].Equals("MarvelTeamUp", StringComparison.OrdinalIgnoreCase))
            {
                string line = $"the {AutoTags.DisplayName(p[2]) ?? Spaced(p[2])} team-up";
                if (!replaces.Contains(line)) replaces.Add(line);
            }
        }

        var changes = new List<string>();
        int n(IEnumerable<TextureReplacement> l, string prefix) => l.Count(r => (r.TextureName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (m.UpkReplacements.Count > 0) changes.Add(Count(m.UpkReplacements.Count, "game package"));
        int costumeIcons = n(m.Replacements, "costume"), powerIcons = n(m.Replacements, "power_"), portraits = n(m.Replacements, "herohor_");
        int otherIcons = m.Replacements.Count - costumeIcons - powerIcons - portraits;
        if (costumeIcons > 0) changes.Add(Count(costumeIcons, "costume icon"));
        if (portraits > 0) changes.Add(Count(portraits, "hero portrait"));
        if (powerIcons > 0) changes.Add(Count(powerIcons, "power icon"));
        if (otherIcons > 0) changes.Add(Count(otherIcons, "other icon"));
        if (m.StoreReplacements.Count > 0) changes.Add(Count(m.StoreReplacements.Count, "store image"));
        if (m.AchievementReplacements.Count > 0) changes.Add(Count(m.AchievementReplacements.Count, "achievement icon"));
        if (m.Extra.Any()) changes.Add(Count(m.Extra.Count(), "image") + " in other icon packages");
        if (s.StringCount > 0) changes.Add(Count(s.StringCount, "game text string") + (s.Languages.Count > 0 ? $" ({string.Join(", ", s.Languages)})" : ""));
        if (m.AudioPacks.Count > 0) changes.Add(Count(m.AudioPacks.Count, "sound pack") + " (new voice lines / sounds)");

        var log = m.Changelog ?? [];
        var latest = log.FirstOrDefault(e => e.Version.Trim().Equals((m.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        return new Facts(m.Name, m.Version ?? "", m.Author ?? "", chars, teams, kinds, changes, replaces, (m.Description ?? "").Trim(),
                         latest, log.Where(e => e != latest).ToList(), m.Extra.Any(), m.Tags ?? []);
    }

    static string Count(int n, string what) => $"{n:N0} {what}{(n == 1 ? "" : "s")}";

    /// <summary>"BlackWidow" → "Black Widow", "90sXMen" → "90s X Men".</summary>
    static string Spaced(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]) || i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static string Heroes(Facts f) => f.Characters.Count > 0 ? string.Join(", ", f.Characters) + (f.Teams.Count > 0 ? $" ({string.Join(", ", f.Teams)})" : "") : "";

    // ---- Nexus (BBCode)

    public static string Nexus(Source s)
    {
        var f = Gather(s);
        var b = new StringBuilder();
        b.AppendLine($"[size=5][b]{f.Name}[/b][/size]" + (f.Version.Length > 0 ? $"  [size=3]v{f.Version.TrimStart('v', 'V')}[/size]" : ""));
        if (f.Author.Length > 0) b.AppendLine($"by [b]{f.Author}[/b]");
        if (Heroes(f).Length > 0) b.AppendLine($"[b]Hero:[/b] {Heroes(f)}");
        b.AppendLine();
        if (f.Description.Length > 0) { b.AppendLine(f.Description); b.AppendLine(); }
        if (f.Replaces.Count > 0)
        {
            b.AppendLine("[b]Replaces[/b]");
            b.AppendLine("[list]"); foreach (var r in f.Replaces) b.AppendLine($"[*]{r}"); b.AppendLine("[/list]");
        }
        if (f.Changes.Count > 0)
        {
            b.AppendLine("[b]What It Changes[/b]");
            b.AppendLine("[list]"); foreach (var c in f.Changes) b.AppendLine($"[*]{c}"); b.AppendLine("[/list]");
        }
        if (f.Latest != null)
        {
            b.AppendLine($"[b]What's New in v{f.Latest.Version.TrimStart('v', 'V')}[/b]");
            b.AppendLine(f.Latest.Changes.Trim());
            b.AppendLine();
        }
        b.AppendLine("[b]Requirements[/b]");
        b.AppendLine("[list]");
        b.AppendLine($"[*][url={ManagerUrl}]MHO Extended Mod Manager[/url] (recommended), or MHModManager 1.0.1");
        if (f.UsesExtension) b.AppendLine("[*]The images in other icon packages need MHO Extended Mod Manager (MHModManager installs the rest and skips those)");
        b.AppendLine("[/list]");
        b.AppendLine("[b]Installation[/b]");
        b.AppendLine("[list=1]");
        b.AppendLine("[*]Close the game.");
        b.AppendLine("[*][b]MHO Extended Mod Manager:[/b] Install Mod (or drop the zip on the window), tick the mod, then Apply Changes (Ctrl+Enter).");
        b.AppendLine("[*][b]MHModManager:[/b] Install the zip, enable the mod, then Apply.");
        b.AppendLine("[/list]");
        if (f.Replaces.Count > 0 || f.Changes.Count > 0)
            b.AppendLine("If another mod changes the same costume or files, the one higher in the mod list wins.");
        if (f.Older.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("[spoiler=Changelog]");
            foreach (var e in new[] { f.Latest }.Concat(f.Older).OfType<ChangelogEntry>())
            {
                b.AppendLine($"[b]v{e.Version.TrimStart('v', 'V')}[/b]");
                b.AppendLine(e.Changes.Trim());
            }
            b.AppendLine("[/spoiler]");
        }
        var tags = f.ModTags.Concat(f.Kinds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tags.Count > 0) { b.AppendLine(); b.AppendLine($"[size=2]Tags: {string.Join(", ", tags)}[/size]"); }
        return b.ToString().TrimEnd();
    }

    // ---- Discord (Markdown, at most 2,000 characters)

    public static string Discord(Source s)
    {
        var f = Gather(s);
        string Build(string description, bool withChanges)
        {
            var b = new StringBuilder();
            b.AppendLine($"**{f.Name}**" + (f.Version.Length > 0 ? $" v{f.Version.TrimStart('v', 'V')}" : "") + (f.Author.Length > 0 ? $" by {f.Author}" : ""));
            if (Heroes(f).Length > 0) b.AppendLine($"Hero: {Heroes(f)}");
            if (description.Length > 0) { b.AppendLine(); b.AppendLine(description); }
            if (f.Replaces.Count > 0) { b.AppendLine(); b.AppendLine("**Replaces**"); foreach (var r in f.Replaces) b.AppendLine($"• {r}"); }
            if (f.Changes.Count > 0) { b.AppendLine(); b.AppendLine("**What It Changes**"); foreach (var c in f.Changes) b.AppendLine($"• {c}"); }
            if (withChanges && f.Latest != null) { b.AppendLine(); b.AppendLine($"**What's New in v{f.Latest.Version.TrimStart('v', 'V')}**"); b.AppendLine(f.Latest.Changes.Trim()); }
            b.AppendLine();
            if (s.Manifest.NexusModId is int nid) b.AppendLine($"Download: <{MhoExtendedModManager.Nexus.SiteMods}{nid}>");
            b.AppendLine($"Install with MHO Extended Mod Manager (<{ManagerUrl}>) or MHModManager: install the zip, turn it on, Apply. Close the game first.");
            if (f.UsesExtension) b.AppendLine("The images in other icon packages need MHO Extended Mod Manager.");
            return b.ToString().TrimEnd();
        }
        string text = Build(f.Description, true);
        if (text.Length <= DiscordLimit) return text;
        // Too long: shorten the description first, then drop the changes.
        for (int keep = f.Description.Length - 100; keep > 0; keep -= 100)
        {
            text = Build(f.Description[..keep].TrimEnd() + "…", true);
            if (text.Length <= DiscordLimit) return text;
        }
        text = Build("", false);
        return text.Length <= DiscordLimit ? text : text[..(DiscordLimit - 1)] + "…";
    }

    /// <summary>The mod's store images, costume icons and hero portraits as PNG, for uploading with the post. Returns the files written.</summary>
    public static List<string> SaveImages(Source s, string modFolder, string outFolder)
    {
        Directory.CreateDirectory(outFolder);
        var m = s.Manifest;
        var picks = m.StoreReplacements.Concat(m.Replacements.Where(r => (r.TextureName ?? "").StartsWith("costume", StringComparison.OrdinalIgnoreCase)
                                                                      || (r.TextureName ?? "").StartsWith("herohor_", StringComparison.OrdinalIgnoreCase)));
        var written = new List<string>();
        foreach (var r in picks)
        {
            string dds = Path.Combine(modFolder, r.DdsFileName);
            var d = MhoPackageModifier.TextureDecode.ReadDds(dds, out _);
            var bgra = d is { } x ? MhoPackageModifier.TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
            if (bgra == null) continue;
            using var bmp = MhoPackageModifier.TextureDecode.ToBitmap(bgra, d!.Value.W, d.Value.H);
            string png = Path.Combine(outFolder, ModInstaller.Sanitise(r.TextureName ?? Path.GetFileNameWithoutExtension(dds)) + ".png");
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            written.Add(png);
        }
        return written;
    }
}
