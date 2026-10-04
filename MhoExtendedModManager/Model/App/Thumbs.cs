using System.Collections.Concurrent;
using System.Drawing.Imaging;
using MhoPackageModifier;

namespace MhoMffImporter;

/// <summary>
/// Thumbnails for the window's lists (Kurt, 0.9.1: "thumbnails next to source and targets"), made on one background thread
/// and kept in <c>data\thumbs</c>: an MFF model = a small front render of its default parts; a base hero package = the
/// game's own portrait from the stock icons package (herohor_&lt;hero&gt;_&lt;costume&gt;, its costume icon, the hero's default
/// portrait; team-ups herohor_teamup_&lt;x&gt;), picked like the Mod Manager's card pictures. <see cref="Ready"/> fires (on the
/// worker thread) when one is made.
/// </summary>
static class Thumbs
{
    static string Dir => Path.Combine(Settings.Home, "thumbs");
    static readonly ConcurrentDictionary<string, Image?> memory = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, bool> queued = new(StringComparer.OrdinalIgnoreCase);
    // Newest first (Kurt, 0.9.3: scrolling fast, the rows he stopped on waited behind every row he flew past), on several
    // workers; a request whose row hasn't been painted for a moment (scrolled past) is dropped and comes back when painted.
    static readonly BlockingCollection<(string Key, Func<string, bool> Make)> work = new(new ConcurrentStack<(string, Func<string, bool>)>());
    static readonly ConcurrentDictionary<string, long> wanted = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<Thread> workers = new();
    static int busy;
    static readonly int Workers = int.TryParse(Environment.GetEnvironmentVariable("MFF_THUMB_WORKERS"), out int tw) && tw > 0 ? tw : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    const long StaleMs = 1500;

    /// <summary>Thumbnails still being made (checks wait for them).</summary>
    public static int Pending => work.Count + busy;

    /// <summary>Thumbnails made since the app started (timing checks).</summary>
    public static int Made => made;
    static int made;

    /// <summary>A thumbnail was made (its key); repaint the row.</summary>
    public static event Action<string>? Ready;

    public static string ModelKey(string folder) => "mff_" + folder;
    // "pkg2_" since 0.10.16 (portraits from the game data): thumbnails made by name matching before are made again
    public static string PackageKey(string file) => "pkg2_" + Path.GetFileNameWithoutExtension(file);

    /// <summary>The thumbnail if it's made (memory or disk); else null, and it's queued.</summary>
    public static Image? Model(string folder) => Get(ModelKey(folder), png => MakeModel(folder, png));
    public static Image? BaseHero(string file) => Get(PackageKey(file), png => MakePortrait(file, png));

    /// <summary>An FBX source's thumbnail: its first material's color map (Kurt, 2026-10-04), made again when the FBX or its folder changes.</summary>
    public static Image? Fbx(string file)
    {
        var fi = new FileInfo(file);
        // (the folder's date too: a color map put beside the FBX later makes a new thumbnail, not the old "none")
        long dir = fi.Directory?.LastWriteTimeUtc.Ticks ?? 0;
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{dir}")))[..16];
        return Get("fbx_" + id, png => MakeColorMap(file, png));
    }

    // --- an FBX: its color map, shrunk to the row ----------------------------------------------------------------------------
    static bool MakeColorMap(string fbx, string png)
    {
        if (MhoMffImporter.FbxReimport.FirstColorMap(fbx) is not string map) return false;
        using var src = LoadCopy(map);
        using var thumb = new Bitmap(128, 128);
        using (var g = Graphics.FromImage(thumb))
        {
            // the whole sheet, fitted (its shape kept) on the row's dark
            g.Clear(Color.FromArgb(30, 32, 40));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            float k = Math.Min(128f / src.Width, 128f / src.Height);
            float w = src.Width * k, h = src.Height * k;
            g.DrawImage(src, (128 - w) / 2, (128 - h) / 2, w, h);
        }
        thumb.Save(png, ImageFormat.Png);
        return true;
    }

    static Image? Get(string key, Func<string, bool> make)
    {
        if (memory.TryGetValue(key, out var img)) return img;
        wanted[key] = Environment.TickCount64;   // painted now: still on screen
        string png = Path.Combine(Dir, key + ".png"), none = Path.Combine(Dir, key + ".none");
        if (File.Exists(png))
        {
            // Read failures (a file another program holds, a broken one) = not ready yet: try again on the next paint. The
            // worker only ever renames a finished file into place, so a half-written one is never read (Kurt's crash, 0.9.1).
            try { return memory[key] = LoadCopy(png); }
            catch (Exception) { return null; }
        }
        if (File.Exists(none)) return memory[key] = null;
        if (queued.TryAdd(key, true))
        {
            work.Add((key, make));
            lock (workers)
                while (workers.Count < Workers)
                {
                    var t = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "thumbs " + workers.Count };
                    workers.Add(t); t.Start();
                }
        }
        return null;
    }

    static void Run()
    {
        foreach (var (key, make) in work.GetConsumingEnumerable())
        {
            if (Environment.TickCount64 - wanted.GetValueOrDefault(key) > StaleMs) { queued.TryRemove(key, out _); continue; }   // scrolled past
            Interlocked.Increment(ref busy);
            Directory.CreateDirectory(Dir);
            string png = Path.Combine(Dir, key + ".png"), tmp = Path.Combine(Dir, key + ".making.png");
            bool ok;
            try
            {
                ok = make(tmp) && File.Exists(tmp);
                if (ok) { File.Move(tmp, png, true); }   // in place only when complete
            }
            catch (Exception) { ok = false; }   // any failure (an FBX Assimp can't read …) = no thumbnail; never end the app from this thread
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            Image? made = null;
            if (ok) try { made = LoadCopy(png); } catch (Exception) { ok = false; }
            if (ok) { memory[key] = made; Interlocked.Increment(ref Thumbs.made); }
            else if (!File.Exists(png)) { memory[key] = null; try { File.WriteAllText(Path.Combine(Dir, key + ".none"), ""); } catch (Exception) { } }
            Interlocked.Decrement(ref busy);
            try { Ready?.Invoke(key); } catch (Exception) { }   // a window closing meanwhile
        }
    }

    /// <summary>An image read into memory (the file isn't kept open).</summary>
    static Image LoadCopy(string png)
    {
        using var s = new MemoryStream(File.ReadAllBytes(png));
        using var src = Image.FromStream(s);
        return new Bitmap(src);
    }

    /// <summary>Makes the model thumbnails of <paramref name="folders"/> with the workers, as if all were on screen (bench).</summary>
    public static TimeSpan Bench(IReadOnlyList<string> folders)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            int left = 0;
            foreach (var f in folders) if (!memory.ContainsKey(ModelKey(f))) { left++; Model(f); }
            if (left == 0) break;
            Thread.Sleep(50);
        }
        return sw.Elapsed;
    }

    // --- an MFF model: its default parts, front view ---------------------------------------------------------------------
    static bool MakeModel(string folder, string png)
    {
        var m = MffModel.Load(Source.ResolveModelFile(folder));
        var meshes = m.Selected(null).SelectMany(p => p.Sections).Select(s => new RMesh(s.Pos, s.Tris, s.Uv, s.Tex.Diffuse)).ToList();
        if (meshes.Count == 0) return false;
        string tmp = png + ".sheet.png";
        // head and shoulders, like the game's portraits (a full figure was too small to tell apart in a list row)
        Snapshot.Sheet(tmp, [new RPanel("", "", meshes, null, "front")], 160, 160, [0.86f, 0.3f]);
        using (var sheet = LoadCopy(tmp))
        using (var crop = new Bitmap(128, 128))
        using (var g = Graphics.FromImage(crop))
        {
            g.DrawImage(sheet, new Rectangle(0, 0, 128, 128), new Rectangle(16, 22, 128, 128), GraphicsUnit.Pixel);
            crop.Save(png, ImageFormat.Png);
        }
        File.Delete(tmp);
        return true;
    }

    // --- a base hero package: the game's portrait --------------------------------------------------------------------------
    static Package? icons;
    static Dictionary<string, int>? iconNames;
    static bool iconsTried;

    static readonly object iconGate = new();

    static bool MakePortrait(string file, string png)
    {
        lock (iconGate) return MakePortraitLocked(file, png);
    }

    static bool MakePortraitLocked(string file, string png)
    {
        // the portrait the game names for this package (any icon package), else the old name matching in MarvelUIIcons
        Package? pkg = null; int idx = -1;
        if (GamePortraits.For(file) is { } gp && OpenIcons(gp.Package) is { } other && other.Names.TryGetValue(gp.Texture, out int gi)) { pkg = other.Pkg; idx = gi; }
        else
        {
            if (!LoadIcons()) return false;
            string? tex = PortraitFor(Path.GetFileNameWithoutExtension(file));
            if (tex == null || !iconNames!.TryGetValue(tex, out idx)) return false;
            pkg = icons;
        }
        var mip = TextureExport.ReadBestMip(pkg!, idx, out _, Settings.Current.CookedFolder);
        if (mip == null) return false;
        var bgra = TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _);
        if (bgra == null) return false;
        using var bmp = TextureDecode.ToBitmap(bgra, mip.Width, mip.Height);
        bmp.Save(png, ImageFormat.Png);
        return true;
    }

    static readonly Dictionary<string, (Package Pkg, Dictionary<string, int> Names)?> iconPackages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An icon package (stock folder first, else the game) with its textures by name; null if missing.</summary>
    static (Package Pkg, Dictionary<string, int> Names)? OpenIcons(string file)
    {
        if (iconPackages.TryGetValue(file, out var have)) return have;
        (Package, Dictionary<string, int>)? found = null;
        foreach (var dir in new[] { Settings.Current.StockFolder, Settings.Current.CookedFolder })
        {
            string f = dir == null ? "" : Path.Combine(dir, file);
            if (!File.Exists(f)) continue;
            var p = Package.Open(f);
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < p.Exports.Length; i++)
                if (p.ClassOf(p.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) names[p.Exports[i].ObjectName] = i;
            found = (p, names);
            break;
        }
        return iconPackages[file] = found;
    }

    /// <summary>Where a base package's portrait comes from (--portraits): "game ICO__…/texture", "name texture", or "none".</summary>
    public static string PortraitSource(string file)
    {
        lock (iconGate)
        {
            if (GamePortraits.For(file) is { } gp)
                return $"game {gp.Package}/{gp.Texture}{(OpenIcons(gp.Package) is { } o && o.Names.ContainsKey(gp.Texture) ? "" : " (MISSING)")}";
            return LoadIcons() && PortraitFor(Path.GetFileNameWithoutExtension(file)) is string t ? "name " + t : "none";
        }
    }

    static bool LoadIcons()
    {
        if (iconsTried) return icons != null;
        iconsTried = true;
        foreach (var dir in new[] { Settings.Current.StockFolder, Settings.Current.CookedFolder })
        {
            string f = dir == null ? "" : Path.Combine(dir, "ICO__MarvelUIIcons_SF.upk");
            if (!File.Exists(f)) continue;
            icons = Package.Open(f);
            iconNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < icons.Exports.Length; i++)
                if (icons.ClassOf(icons.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) iconNames[icons.Exports[i].ObjectName] = i;
            return true;
        }
        return false;
    }

    /// <summary>The portrait texture for UC__MarvelPlayer_&lt;Hero&gt;[_&lt;Costume&gt;]_SF / UC__MarvelTeamUp_&lt;X&gt;_SF, or null.</summary>
    static string? PortraitFor(string stem)
    {
        bool Has(string n) => iconNames!.ContainsKey(n);
        bool teamUp = stem.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase);
        string rest = stem[(teamUp ? "UC__MarvelTeamUp_".Length : "UC__MarvelPlayer_".Length)..];
        if (rest.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) rest = rest[..^3];
        int us = rest.IndexOf('_');
        string hero = (us < 0 ? rest : rest[..us]).ToLowerInvariant(), costume = us < 0 ? "" : rest[(us + 1)..].ToLowerInvariant();
        string? HeroDefault(string prefix)
        {
            if (hero == "angela" && Has(prefix + "angela_aa")) return prefix + "angela_aa";   // Angela's default costume is "aa" (Kurt)
            foreach (string end in new[] { "", "_original", "_classic", "_default", "_modern" })
                if (Has(prefix + hero + end)) return prefix + hero + end;
            return iconNames!.Keys.Where(n => n.StartsWith(prefix + hero + "_", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).FirstOrDefault();
        }
        if (teamUp) return HeroDefault("herohor_teamup_") ?? HeroDefault("herohor_");
        if (costume.Length > 0)
        {
            foreach (string p in new[] { "herohor_", "costume_", "costume" })
                if (Has($"{p}{hero}_{costume}")) return $"{p}{hero}_{costume}";
            if (iconNames!.Keys.FirstOrDefault(n => n.StartsWith("herohor_" + hero + "_", StringComparison.OrdinalIgnoreCase) && n.Contains(costume, StringComparison.OrdinalIgnoreCase)) is string fuzzy) return fuzzy;
        }
        return HeroDefault("herohor_");
    }
}
