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

    /// <summary>Nexus's own "Full description" template (Kurt, 2026-11-17): Description, Installation instructions, Main
    /// features, Requirements, Shout outs; then the changelog (a spoiler) and the tags.</summary>
    public static string Nexus(Source s)
    {
        var f = Gather(s);
        var b = new StringBuilder();
        void Heading(string t) { if (b.Length > 0) b.AppendLine(); b.AppendLine($"[b]{t}[/b]"); }

        Heading("Description");
        b.AppendLine(f.Description.Length > 0 ? f.Description
            : $"{f.Name}" + (f.Version.Length > 0 ? $" v{f.Version.TrimStart('v', 'V')}" : "") + (f.Author.Length > 0 ? $" by {f.Author}." : "."));
        if (Heroes(f).Length > 0) b.AppendLine($"Hero: {Heroes(f)}");

        Heading("Installation instructions");
        // one manager or the other, not two steps (Kurt)
        b.AppendLine("Close the game, then install it with either manager:");
        b.AppendLine("[list]");
        b.AppendLine("[*][b]MHO Extended Mod Manager:[/b] Install Mod (or drop the zip on the window), tick the mod, then Apply Changes (Ctrl+Enter).");
        b.AppendLine("[*][b]or MHModManager 1.0.1:[/b] Install the zip, enable the mod, then Apply.");
        b.AppendLine("[/list]");
        if (f.Replaces.Count > 0 || f.Changes.Count > 0)
            b.AppendLine("If another mod changes the same costume or files, the one higher in the mod list wins.");

        if (f.Replaces.Count > 0 || f.Changes.Count > 0 || f.Latest != null)
        {
            Heading("Main features");
            b.AppendLine("[list]");
            foreach (var r in f.Replaces) b.AppendLine($"[*]Replaces {r}");
            foreach (var c in f.Changes) b.AppendLine($"[*]{char.ToUpperInvariant(c[0])}{c[1..]}");
            b.AppendLine("[/list]");
            if (f.Latest != null)
            {
                b.AppendLine($"[b]What's new in v{f.Latest.Version.TrimStart('v', 'V')}[/b]");
                b.AppendLine(f.Latest.Changes.Trim());
            }
        }

        Heading("Requirements");
        b.AppendLine("[list]");
        b.AppendLine($"[*][url={ManagerUrl}]MHO Extended Mod Manager[/url] (recommended), or MHModManager 1.0.1");
        if (f.UsesExtension) b.AppendLine("[*]The images in other icon packages need MHO Extended Mod Manager (MHModManager installs the rest and skips those)");
        b.AppendLine("[/list]");

        Heading("Shout outs");
        b.AppendLine($"Made with [url={ManagerUrl}]MHO Extended Mod Manager[/url]. Thanks to everyone who inspired or helped with this mod.");

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

    // ---- Nexus images (Kurt's templates, 2026-11-17: Assets\Post)

    /// <summary>The Marvel Heroes Omega banner for the top of a Nexus page (Assets\Post\nexus_banner.png, 1300 x 372).</summary>
    public static string BannerFile => Path.Combine(AppContext.BaseDirectory, "Assets", "Post", "nexus_banner.png");
    public const string BannerName = "Marvel Heroes Omega Banner.png";
    static string BlankFile => Path.Combine(AppContext.BaseDirectory, "Assets", "Post", "showcase_blank.jpg");

    static string VisualUpdateBlankFile => Path.Combine(AppContext.BaseDirectory, "Assets", "Post", "visual_update_blank.jpg");

    /// <summary>Where the comic cover goes on the 1920 x 1080 showcase image (right of the middle, nearly full height).</summary>
    public static readonly System.Drawing.Rectangle CoverSlot = new(935, 40, 650, 1000);
    /// <summary>The Visual Update template's cover slot: the open area right of the two panels.</summary>
    public static readonly System.Drawing.Rectangle VisualUpdateCoverSlot = new(1160, 40, 650, 1000);
    /// <summary>The Visual Update template's panels (1920 x 1080): under LAUNCH and VISUAL UPDATE, between the titles and the logos.</summary>
    public static readonly System.Drawing.Rectangle LaunchPanel = new(102, 100, 344, 800), VisualUpdatePanel = new(566, 100, 468, 800);

    /// <summary>The kinds of Nexus picture: the file name ends in its suffix (" showcase", " visual update"), which also picks the cover slot.</summary>
    public const string ShowcaseSuffix = " showcase", VisualUpdateSuffix = " visual update";
    public static System.Drawing.Rectangle CoverSlotFor(string file) =>
        Path.GetFileNameWithoutExtension(file).EndsWith(VisualUpdateSuffix, StringComparison.OrdinalIgnoreCase) ? VisualUpdateCoverSlot : CoverSlot;

    /// <summary>A .dds (or any picture) as a Bitmap; null when it can't be read.</summary>
    public static System.Drawing.Bitmap? Picture(string file)
    {
        try
        {
            if (!file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return ImageFiles.Load(file);
            var d = MhoPackageModifier.TextureDecode.ReadDds(file, out _);
            var bgra = d is { } x ? MhoPackageModifier.TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
            return bgra == null ? null : MhoPackageModifier.TextureDecode.ToBitmap(bgra, d!.Value.W, d.Value.H);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// The Visual Update template (Kurt, 2026-11-17): the game's look under LAUNCH, the mod's under VISUAL UPDATE (each fitted
    /// into its panel, standing on its bottom), the right side left for a cover. Writes a 1920 x 1080 PNG.
    /// </summary>
    public static System.Drawing.Bitmap VisualUpdateImage(System.Drawing.Image? before, System.Drawing.Image? after, string? cover = null)
    {
        var bmp = new System.Drawing.Bitmap(1920, 1080, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        if (File.Exists(VisualUpdateBlankFile)) using (var back = System.Drawing.Image.FromFile(VisualUpdateBlankFile)) g.DrawImage(back, 0, 0, 1920, 1080);
        // both at the same height (a fair before / after), centered in their panel and cut at its edges
        void Fit(System.Drawing.Image? img, System.Drawing.Rectangle r)
        {
            if (img == null) return;
            int h = Math.Min(680, r.Height), w = img.Width * h / Math.Max(1, img.Height);
            g.SetClip(r);
            g.DrawImage(img, new System.Drawing.Rectangle(r.X + (r.Width - w) / 2, r.Bottom - h, w, h));
            g.ResetClip();
        }
        Fit(before, LaunchPanel);
        Fit(after, VisualUpdatePanel);
        if (cover != null) DrawCover(g, cover, VisualUpdateCoverSlot);
        return bmp;
    }

    /// <summary>Copies the banner into a folder of exported images (Export Images, Export ZIP's post folder).</summary>
    public static void AddBanner(string folder)
    {
        if (File.Exists(BannerFile)) File.Copy(BannerFile, Path.Combine(folder, BannerName), true);
    }

    /// <summary>
    /// A 1920 x 1080 showcase image (Kurt's blank Marvel Heroes Omega backdrop): the mod's store image on the left, the
    /// right side left for a comic cover (<paramref name="cover"/>, or dropped in later in an image editor). Null when the
    /// mod has no store image; else the file written (PNG).
    /// </summary>
    public static string? Showcase(Source s, string modFolder, string outFile, string? cover = null)
    {
        var store = s.Manifest.StoreReplacements.FirstOrDefault();
        if (store == null || !File.Exists(BlankFile)) return null;
        var d = MhoPackageModifier.TextureDecode.ReadDds(Path.Combine(modFolder, store.DdsFileName), out _);
        var bgra = d is { } x ? MhoPackageModifier.TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
        if (bgra == null) return null;
        using var art = MhoPackageModifier.TextureDecode.ToBitmap(bgra, d!.Value.W, d.Value.H);
        using var bmp = new System.Drawing.Bitmap(1920, 1080, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using (var back = System.Drawing.Image.FromFile(BlankFile)) g.DrawImage(back, 0, 0, 1920, 1080);
            // the character: as tall as the cover, centered in the left part (like Kurt's example)
            int h = 900, w = art.Width * h / Math.Max(1, art.Height);
            g.DrawImage(art, new System.Drawing.Rectangle(470 - w / 2, 1030 - h, w, h));
            if (cover != null) DrawCover(g, cover, CoverSlot);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        bmp.Save(outFile, System.Drawing.Imaging.ImageFormat.Png);
        return outFile;
    }

    /// <summary>Puts a cover picture into an existing showcase image's cover slot (fitted, its own shape kept).</summary>
    public static void PutCover(string showcaseFile, string cover)
    {
        System.Drawing.Bitmap bmp;
        using (var raw = System.Drawing.Image.FromFile(showcaseFile)) bmp = new System.Drawing.Bitmap(raw);
        using (bmp)
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                // the slot scaled to this image's size (the same layout at another size)
                g.ScaleTransform(bmp.Width / 1920f, bmp.Height / 1080f);
                DrawCover(g, cover, CoverSlotFor(showcaseFile));
            }
            bmp.Save(showcaseFile, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    static void DrawCover(System.Drawing.Graphics g, string cover, System.Drawing.Rectangle r)
    {
        using var c = ImageFiles.Load(cover);
        float k = Math.Min(r.Width / (float)c.Width, r.Height / (float)c.Height);
        int w = (int)(c.Width * k), h = (int)(c.Height * k);
        g.DrawImage(c, new System.Drawing.Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h));
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
