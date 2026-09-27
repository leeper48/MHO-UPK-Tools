namespace MhoExtendedModManager;

/// <summary>
/// Clean copies of every package a mod replaces, in &lt;library&gt;\originals. A copy is only ever accepted if its CRC-32
/// matches the stock list, so this is the one place that is known to be original (unlike a .bak, which may have been
/// taken from an already-modded file: 20 of Kurt's were, 2026-09-27). Disabling a mod restores from here.
/// </summary>
sealed class Originals(string libraryData, GameState game)
{
    public string Folder { get; } = Path.Combine(libraryData, "originals");

    /// <summary>A verified clean copy of the package, or null.</summary>
    public string? Get(string file)
    {
        string p = Path.Combine(Folder, file);
        return File.Exists(p) && game.MatchesStock(file, p) ? p : null;
    }

    /// <summary>Where a clean copy is, or could be taken from, without copying anything (for dry runs).</summary>
    public string? Find(string file, params string[] extraSources)
    {
        if (Get(file) is string have) return have;
        return Candidates(file, extraSources).FirstOrDefault(p => game.MatchesStock(file, p));
    }

    /// <summary>The live file, its .bak, then &lt;dir&gt;\&lt;file&gt; and &lt;dir&gt;\&lt;file&gt;.bak in each extra folder (MHModManager kept its icon-package backups as ICO__….upk.bak).</summary>
    IEnumerable<string> Candidates(string file, string[] extraSources)
    {
        string live = Path.Combine(game.Cooked, file);
        return new[] { live, live + ".bak" }.Concat(extraSources.SelectMany(d => new[] { Path.Combine(d, file), Path.Combine(d, file + ".bak") }));
    }

    /// <summary>
    /// String files (&lt;lang&gt;.all\&lt;file&gt;.string) have no stock checksums, so their originals can't be verified: they
    /// are MHModManager's backups (legacy\string_backups, byte-identical to the live files for the 6 languages no mod
    /// touches) or, with no backup, the live file as first seen. Kept in originals\strings.
    /// </summary>
    public string? FindString(string rel, string legacy)
    {
        string have = Path.Combine(Folder, "strings", rel);
        if (File.Exists(have)) return have;
        string old = Path.Combine(legacy, "string_backups", rel + ".bak");
        if (File.Exists(old)) return old;
        string live = Path.Combine(game.Loco, rel);
        return File.Exists(live) ? live : null;
    }

    /// <summary>
    /// Sound packages (.pck) have no stock checksums either. Their original: originals\sounds, else MHModManager's backup
    /// (legacy\sound_backups; Kurt's two are stock-dated and identical to live), else the live file only while it still
    /// has the stock date (2024-03-14), i.e. hasn't been rewritten by anything.
    /// </summary>
    public string? FindSound(string pck, string legacy)
    {
        string have = Path.Combine(Folder, "sounds", pck);
        if (File.Exists(have)) return have;
        string old = Path.Combine(legacy, "sound_backups", pck);
        if (File.Exists(old)) return old;
        string live = Path.Combine(game.Cooked, pck);
        return File.Exists(live) && File.GetLastWriteTime(live).Date == GameState.StockDate ? live : null;
    }

    public string? EnsureSound(string pck, string legacy)
    {
        string dst = Path.Combine(Folder, "sounds", pck);
        if (File.Exists(dst)) return dst;
        string? src = FindSound(pck, legacy);
        if (src == null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst + ".tmp", overwrite: true);
        if (game.Crc(dst + ".tmp") != game.Crc(src)) { File.Delete(dst + ".tmp"); return null; }
        File.Move(dst + ".tmp", dst);
        return dst;
    }

    /// <summary>
    /// Texture caches (.tfc) with a kept original: originals\tfc holds &lt;cache&gt;.tfc plus a copy of the game's
    /// TextureFileCacheManifest.bin (so MPM's TfcCache can read original images from there). There are no stock
    /// checksums for .tfc files, so a copy is only accepted with the stock date (File.Copy keeps it) and the live
    /// file's size (MHIconManager and MHModManager 1.0.0 wrote icon images into Icons.tfc in place).
    /// </summary>
    public string TfcFolder => Path.Combine(Folder, "tfc");

    public string? FindTfc(string cache)
    {
        string have = Path.Combine(TfcFolder, cache + ".tfc");
        return File.Exists(have) && File.Exists(Path.Combine(TfcFolder, "TextureFileCacheManifest.bin")) ? have : null;
    }

    /// <summary>Keeps <paramref name="file"/> as the original of &lt;cache&gt;.tfc. Returns why not, or null when done.</summary>
    public string? AddTfc(string file, string cache)
    {
        if (FindTfc(cache) != null) return $"{cache}.tfc already has an original";
        string live = Path.Combine(game.Cooked, cache + ".tfc"), manifest = Path.Combine(game.Cooked, "TextureFileCacheManifest.bin");
        if (!File.Exists(file)) return "file not found";
        if (!File.Exists(live) || !File.Exists(manifest)) return $"{cache}.tfc or the texture cache manifest isn't in the game folder";
        if (File.GetLastWriteTime(file).Date != GameState.StockDate) return $"it's dated {File.GetLastWriteTime(file):yyyy-MM-dd}, not the stock date 2024-03-14, so it can't be trusted as the original";
        if (new FileInfo(file).Length != new FileInfo(live).Length) return "its size differs from the game's file";
        Directory.CreateDirectory(TfcFolder);
        string dst = Path.Combine(TfcFolder, cache + ".tfc");
        File.Copy(file, dst + ".tmp", overwrite: true);
        if (game.Crc(dst + ".tmp") != game.Crc(file)) { File.Delete(dst + ".tmp"); return "the copy didn't verify"; }
        File.Move(dst + ".tmp", dst);
        string man = Path.Combine(TfcFolder, "TextureFileCacheManifest.bin");
        if (!File.Exists(man)) File.Copy(manifest, man);
        return null;
    }

    public string? EnsureString(string rel, string legacy)
    {
        string dst = Path.Combine(Folder, "strings", rel);
        if (File.Exists(dst)) return dst;
        string? src = FindString(rel, legacy);
        if (src == null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        byte[] b = File.ReadAllBytes(src);
        File.WriteAllBytes(dst + ".tmp", b);
        if (!File.ReadAllBytes(dst + ".tmp").AsSpan().SequenceEqual(b)) { File.Delete(dst + ".tmp"); return null; }
        File.Move(dst + ".tmp", dst);
        return dst;
    }

    /// <summary>
    /// Finds a clean copy if there isn't one yet: the live file, its .bak, then any extra folders (MHModManager's
    /// upk_backups during migration). Copies it in and verifies it. Returns the path or null (with the reason).
    /// </summary>
    public string? Ensure(string file, out string? why, params string[] extraSources)
    {
        why = null;
        if (Get(file) is string have) return have;
        if (!game.IsStockName(file)) { why = "not in the stock checksum list, so no original can be verified"; return null; }
        foreach (string src in Candidates(file, extraSources))
        {
            if (!File.Exists(src) || !game.MatchesStock(file, src)) continue;
            Directory.CreateDirectory(Folder);
            string dst = Path.Combine(Folder, file), tmp = dst + ".tmp";
            File.Copy(src, tmp, overwrite: true);
            if (!game.MatchesStock(file, tmp)) { File.Delete(tmp); continue; }
            File.Move(tmp, dst, overwrite: true);
            return dst;
        }
        why = "no stock copy found (live file, .bak" + (extraSources.Length > 0 ? ", old backups" : "") + " are all modified)";
        return null;
    }
}
