using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MhoMffImporter;

/// <summary>
/// The importer's output (Kurt, 2026-09-30): a Mod Manager mod, never a write into the game. A folder with the
/// MHModManager 1.0.1 manifest (Type 2 = package mod; the fields the old manager reads, in its order, so it stays
/// installable there too) and the package(s). Install it with the Mod Manager's Install (a folder or a .zip), then Apply.
/// </summary>
static class ModOut
{
    public static string Write(string outDir, string name, string author, string version, IReadOnlyList<string> packages, string? notes = null)
    {
        Protected.CheckWrite(outDir);
        Directory.CreateDirectory(outDir);
        foreach (var p in packages)
        {
            string dst = Path.Combine(outDir, Path.GetFileName(p));
            Protected.CheckWrite(dst);
            File.Copy(p, dst, true);
            if (!File.ReadAllBytes(dst).AsSpan().SequenceEqual(File.ReadAllBytes(p))) throw new IOException($"{dst} doesn't match {p} after copying");
        }
        var manifest = new Dictionary<string, object?>
        {
            ["Name"] = name,
            ["Author"] = author,
            ["Version"] = version,
            ["Replacements"] = Array.Empty<object>(),
            ["AchievementReplacements"] = Array.Empty<object>(),
            ["StoreReplacements"] = Array.Empty<object>(),
            ["Languages"] = Array.Empty<string>(),
            ["UpkReplacements"] = packages.Select(Path.GetFileName).ToArray(),
            ["AudioPacks"] = Array.Empty<string>(),
            ["HasTextures"] = false,
            ["HasUpkReplacements"] = true,
            ["Type"] = 2,
            ["TextureReplacementCount"] = 0,
        };
        if (notes != null) manifest["Notes"] = notes;   // Ext Mod Manager extension (ignored by MHModManager)
        string path = Path.Combine(outDir, "manifest.json");
        Protected.CheckWrite(path);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return path;
    }

    /// <summary>
    /// The mod folder as a .zip next to it (Kurt, 2026-09-30: the Mod Manager installs from a zip), named as the Mod
    /// Manager's own exports are: "&lt;name&gt; - v&lt;version&gt;.zip"; manifest.json and the packages at the top level.
    /// Read back after writing: every entry present with its exact bytes.
    /// </summary>
    public static string Zip(string modDir, string name, string version)
    {
        string safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        string zip = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(modDir))!, $"{safe} - v{version.TrimStart('v', 'V')}.zip");
        Protected.CheckWrite(zip);
        string tmp = zip + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        var files = Directory.GetFiles(modDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
            foreach (var f in files) z.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
        using (var z = ZipFile.OpenRead(tmp))
            foreach (var f in files)
            {
                var e = z.GetEntry(Path.GetFileName(f)) ?? throw new IOException($"{Path.GetFileName(f)} missing from the zip");
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                if (!ms.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(f))) throw new IOException($"{Path.GetFileName(f)} differs in the zip");
            }
        File.Move(tmp, zip, true);
        return zip;
    }
}
