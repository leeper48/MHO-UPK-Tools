using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Mods in the older MH Texture Manager format (Kurt, 2026-10-06: "Psylocke Jim Lee" on Nexus): a <c>&lt;Name&gt;.json</c>
/// listing textures (Head.TextureName = the texture's path, Original = its mips in the game's .tfc, Updated = the new mips
/// in <c>&lt;Updated.TextureFileName&gt;.tfc</c>) and that .tfc, which holds each mip as a UE3 compressed chunk. That tool
/// wrote into the game's shared texture caches. Install converts such a mod into an ordinary package mod instead: every game
/// package that exports the texture (Psylocke's: her Classic VU costume and her NPC) gets it with the new mips inline (no
/// .tfc), built from the stock copy, so the mod installs, turns on and off, and moves like any other, and stays installable
/// in MHModManager.
/// </summary>
static class TextureManagerMod
{
    sealed record Map(long Offset, int Size);
    sealed record Item(string Texture, string Guid, string Cache, List<Map> Maps);

    /// <summary>The texture-manager mods under <paramref name="root"/> (up to 2 folders deep): each .json of that shape with
    /// its .tfc beside it.</summary>
    public static List<string> Find(string root) =>
        Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(root, f).Count(c => c == Path.DirectorySeparatorChar) <= 2)
            .Where(f => TryRead(f, out var items) && items.Count > 0 && items.All(i => File.Exists(TfcOf(f, i))))
            .OrderBy(f => f).ToList();

    static string TfcOf(string json, Item i) => Path.Combine(Path.GetDirectoryName(json)!, i.Cache + ".tfc");

    static bool TryRead(string json, out List<Item> items)
    {
        items = [];
        try
        {
            if (new FileInfo(json).Length > 4 << 20) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("Head", out var head) || !e.TryGetProperty("Updated", out var upd)) return false;
                string? tex = head.TryGetProperty("TextureName", out var t) ? t.GetString() : null;
                string guid = head.TryGetProperty("TextureGuid", out var g) ? g.GetString() ?? "" : "";
                string? cache = upd.TryGetProperty("TextureFileName", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(tex) || string.IsNullOrWhiteSpace(cache) || !upd.TryGetProperty("Maps", out var maps)) return false;
                // a cache name, not a path (the mod's file names are untrusted: ModSafety)
                if (cache.IndexOfAny(['/', '\\', ':']) >= 0 || cache.Contains("..")) return false;
                var list = maps.EnumerateArray().Select(m => new Map(m.GetProperty("Offset").GetInt64(), m.GetProperty("Size").GetInt32())).ToList();
                if (list.Count == 0 || list.Any(m => m.Offset < 0 || m.Size <= 0)) return false;
                items.Add(new Item(tex, guid, cache, list));
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException or FormatException) { return false; }
    }

    /// <summary>
    /// Converts the texture-manager mod <paramref name="json"/> into a package mod in <paramref name="outDir"/> (manifest.json
    /// plus the built packages, each verified by reading it back). <paramref name="source"/> (the archive or folder installed)
    /// names the mod and its version when it's a Nexus download. False with the reason in the log when it can't be converted.
    /// </summary>
    public static bool Convert(string json, string source, string cooked, string outDir, List<string> log)
    {
        string label = Path.GetFileName(json);
        if (!TryRead(json, out var items) || items.Count == 0) { log.Add($"{label}: not a texture-manager mod."); return false; }
        // package → its texture replacements
        var perPackage = new Dictionary<string, List<TextureImport.Replacement>>(StringComparer.OrdinalIgnoreCase);
        var tfcBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            var hits = PackagesWith(cooked, it.Texture);
            if (hits.Count == 0) { log.Add($"{label}: no game package has the texture {it.Texture}."); return false; }
            string tfc = TfcOf(json, it);
            if (!tfcBytes.TryGetValue(tfc, out var data)) tfcBytes[tfc] = data = File.ReadAllBytes(tfc);
            var (file, stock, export) = hits[0];
            var pkg = Package.Open(stock);
            var info = TextureInfo.Read(pkg, pkg.Exports[export]);
            byte[]? dds = BuildDds(pkg, info, it, data, out string? why);
            if (dds == null) { log.Add($"{label}: {it.Texture}: {why}"); return false; }
            foreach (var (f, _, _) in hits)
            {
                if (!perPackage.TryGetValue(f, out var list)) perPackage[f] = list = [];
                list.Add(new TextureImport.Replacement(it.Texture, dds, $"{Path.GetFileName(tfc)} ({it.Texture})"));
            }
            log.Add($"  {it.Texture}: {info.SizeX}×{info.SizeY} {info.Format}, into {string.Join(", ", hits.Select(h => h.File))}");
        }
        Directory.CreateDirectory(outDir);
        foreach (var (file, reps) in perPackage)
        {
            var pkg = Package.Open(StockFiles.For(cooked, file));
            byte[]? built = TextureImport.ReplaceMany(pkg, reps, out var problems, out var verify);
            if (built == null) { log.Add($"{label}: {file}: {string.Join("; ", problems)}"); return false; }
            var bad = verify(built);
            if (bad.Count > 0) { log.Add($"{label}: {file} didn't read back right: {string.Join("; ", bad.Take(3))}"); return false; }
            File.WriteAllBytes(Path.Combine(outDir, file), built);
        }
        // a Nexus download's name (Name-<id>-<version>-<time>), the archive's or the unpacked folder's
        var dl = Nexus.FromFileName(source) ?? (Directory.Exists(source) ? Nexus.FromFileName(Path.TrimEndingDirectorySeparator(source) + ".zip") : null);
        string name = dl?.Title is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(json);
        // the author from the mod's Nexus page (public information; only the page's id is sent)
        string author = AuthorFromNexus(dl, log) ?? "Unknown";
        var manifest = new ModManifest
        {
            Name = name,
            Author = author,
            Version = dl?.Version ?? "1.0",
            UpkReplacements = [.. perPackage.Keys.Order(StringComparer.OrdinalIgnoreCase)],
            HasUpkReplacements = true,
            Type = ModType.Upk,
            NexusModId = dl?.ModId,
            Notes = $"Converted from an MH Texture Manager mod ({Path.GetFileName(json)} and its .tfc): the textures are in the packages, the game's texture caches are left alone.",
        };
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, ModManifest.Json));
        log.Add($"{label}: converted from the MH Texture Manager format: {items.Count} texture(s) into {perPackage.Count} package(s).");
        return true;
    }

    /// <summary>The author named on the mod's Nexus page (null without a Nexus id, offline, or when the page names none).</summary>
    static string? AuthorFromNexus(Nexus.DownloadName? dl, List<string> log)
    {
        if (dl is not { ModId: > 0 } d) return null;
        try
        {
            var t = Task.Run(() => Nexus.Author(d.ModId));
            if (!t.Wait(TimeSpan.FromSeconds(20))) { log.Add($"  author: Nexus didn't answer in time (mod {d.ModId}); left as Unknown"); return null; }
            if (t.Result is string a) { log.Add($"  author: {a} (from its Nexus page, mod {d.ModId})"); return a; }
            log.Add($"  author: its Nexus page (mod {d.ModId}) names none; left as Unknown");
        }
        catch (Exception ex) { log.Add($"  author: couldn't read its Nexus page ({(ex is AggregateException ae ? ae.InnerException?.Message : ex.Message)}); left as Unknown"); }
        return null;
    }

    /// <summary>The game packages (file, stock copy, export index) that export the texture at <paramref name="path"/>
    /// (group.name). Looked for in the packages named after the group's first word (Psylocke_ClassicVU → *Psylocke*), the
    /// texture's own name checked in the name table first; bak / copy files left out.</summary>
    static List<(string File, string Stock, int Export)> PackagesWith(string cooked, string path)
    {
        var result = new List<(string, string, int)>();
        string leaf = path[(path.LastIndexOf('.') + 1)..];
        string word = path.Split('.', '_')[0];
        if (word.Length < 3) return result;
        foreach (string live in Directory.EnumerateFiles(cooked, "*.upk").Where(f => Path.GetFileName(f).Contains(word, StringComparison.OrdinalIgnoreCase)).Order())
        {
            string file = Path.GetFileName(live);
            if (file.Contains("bak", StringComparison.OrdinalIgnoreCase) || file.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string stock = StockFiles.For(cooked, file);
                var pkg = Package.Open(stock);
                if (!pkg.Names.Any(n => n.Equals(leaf, StringComparison.OrdinalIgnoreCase))) continue;
                for (int i = 0; i < pkg.Exports.Length; i++)
                    if (pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase) && pkg.PathOf(pkg.Exports[i]).Equals(path, StringComparison.OrdinalIgnoreCase))
                    { result.Add((file, stock, i)); break; }
            }
            catch (Exception ex) when (ex is IOException or PackageFormatException or InvalidDataException) { }
        }
        return result;
    }

    /// <summary>
    /// The texture as a DDS: the mod's mips (each a compressed chunk in its .tfc), then the stock texture's own smaller mips
    /// the .tfc doesn't have (the texture manager only replaced the streamed ones; the small ones stayed the game's). The
    /// mod's mips must match the stock texture's sizes in its format (DXT1 / DXT5), or be all scaled alike (a bigger or
    /// smaller texture: then only the mod's mips).
    /// </summary>
    static byte[]? BuildDds(Package pkg, TextureInfo info, Item it, byte[] tfc, out string? why)
    {
        why = null;
        int bpb = info.Format.ToUpperInvariant() switch { "PF_DXT1" => 8, "PF_DXT5" => 16, _ => 0 };
        if (bpb == 0) { why = $"the game's texture is {info.Format}; only DXT1 / DXT5 can be converted"; return null; }
        static int Bytes(int w, int h, int bpb) => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * bpb;
        var levels = new List<(int W, int H, byte[] Pixels)>();
        var mods = new List<byte[]>();
        foreach (var m in it.Maps)
        {
            if (m.Offset + m.Size > tfc.Length) { why = "its .tfc is shorter than the .json says"; return null; }
            var block = tfc.AsSpan((int)m.Offset, m.Size).ToArray();
            if (BitConverter.ToUInt32(block, 0) != 0x9E2A83C1) { why = "a mip in its .tfc isn't a compressed chunk"; return null; }
            int unpacked = BitConverter.ToInt32(block, 12);
            try { mods.Add(TextureExport.DecompressChunk(block, unpacked)); }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { why = "a mip in its .tfc couldn't be unpacked: " + ex.Message; return null; }
        }
        // the stock mips in order, largest first; the mod's first mip matches one of them, or is a scaled one
        var stockMips = info.Mips.Where(m => !m.Unused && m.Width > 0).ToList();
        int start = stockMips.FindIndex(m => Bytes(m.Width, m.Height, bpb) == mods[0].Length);
        if (start >= 0 && Enumerable.Range(0, mods.Count).All(k => start + k < stockMips.Count && Bytes(stockMips[start + k].Width, stockMips[start + k].Height, bpb) == mods[k].Length))
        {
            for (int k = 0; k < mods.Count; k++) levels.Add((stockMips[start + k].Width, stockMips[start + k].Height, mods[k]));
            foreach (var m in stockMips.Skip(start + mods.Count))
            {
                byte[]? px = MipBytes(pkg, info, m, bpb);
                if (px == null) break;   // the rest of the chain isn't in the package: the mod's mips are enough
                levels.Add((m.Width, m.Height, px));
            }
        }
        else
        {
            // a resized texture: the size from the first mip, keeping the stock aspect
            double scale = Math.Sqrt(mods[0].Length / (double)Bytes(info.SizeX, info.SizeY, bpb));
            int w = (int)Math.Round(info.SizeX * scale), h = (int)Math.Round(info.SizeY * scale);
            for (int k = 0; k < mods.Count; k++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            {
                if (Bytes(w, h, bpb) != mods[k].Length) { why = $"its mip {k + 1} ({mods[k].Length} bytes) doesn't fit a {w}×{h} {info.Format} texture"; return null; }
                levels.Add((w, h, mods[k]));
            }
        }
        return Dds(levels, info.Format[3..].ToUpperInvariant());
    }

    /// <summary>A mip's pixels when the package holds them (inline, LZO or not); null for one in a .tfc.</summary>
    static byte[]? MipBytes(Package pkg, TextureInfo info, TextureMip m, int bpb)
    {
        if (!m.Inline) return null;
        var raw = info.Data.AsSpan(m.InlineAt, m.Size).ToArray();
        int expected = Math.Max(1, (m.Width + 3) / 4) * Math.Max(1, (m.Height + 3) / 4) * bpb;
        if (m.Lzo) { try { var px = TextureExport.DecompressChunk(raw, m.Count); return px.Length == expected ? px : null; } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; } }
        return raw.Length == expected ? raw : null;
    }

    /// <summary>A DDS file: the 128-byte header (FourCC, mip count) and the levels.</summary>
    static byte[] Dds(List<(int W, int H, byte[] Pixels)> levels, string fourCC)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x20534444);                                   // "DDS "
        w.Write(124);
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);  // caps, height, width, pixel format, mip count, linear size
        w.Write(levels[0].H); w.Write(levels[0].W);
        w.Write(levels[0].Pixels.Length);
        w.Write(0);                                            // depth
        w.Write(levels.Count);
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32); w.Write(0x4);                             // pixel format: FourCC
        w.Write(System.Text.Encoding.ASCII.GetBytes(fourCC.PadRight(4)[..4]));
        for (int i = 0; i < 5; i++) w.Write(0);
        w.Write(0x1000 | 0x8 | 0x400000);                      // texture, complex, mipmap
        for (int i = 0; i < 4; i++) w.Write(0);
        foreach (var l in levels) w.Write(l.Pixels);
        return ms.ToArray();
    }
}
