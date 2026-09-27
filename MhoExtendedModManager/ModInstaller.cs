using System.IO.Compression;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace MhoExtendedModManager;

/// <summary>
/// Library housekeeping: a new empty library, installing mods (.zip / .7z / .rar or a folder, the formats MHModManager
/// exports and mod authors share: manifest.json plus its files, at the top or one folder down; an archive may hold
/// several mods), exporting a mod as .zip, removing one (to the Recycle Bin), and moving the library. None of this
/// touches the game folder: Apply does that.
/// </summary>
static class ModInstaller
{
    /// <summary>The stock package checksum list shipped next to the exe (see StockData\README.txt).</summary>
    public static string ShippedStockList => Path.Combine(AppContext.BaseDirectory, "StockData", "upk_checksums.json");

    public static void CreateEmptyLibrary(string library)
    {
        Directory.CreateDirectory(Path.Combine(library, "mods"));
        string state = Path.Combine(library, "state.json");
        if (!File.Exists(state)) File.WriteAllText(state, JsonSerializer.Serialize(new ModState(), ModManifest.Json));
        string list = Path.Combine(library, "upk_checksums.json");
        if (!File.Exists(list) && File.Exists(ShippedStockList)) File.Copy(ShippedStockList, list);
    }

    /// <summary>Installs every mod in the archive or folder. New mods go at the top of the order, disabled. Returns the installed folder names.</summary>
    public static List<string> Install(string source, ModLibrary lib, List<string> log)
    {
        var installed = new List<string>();
        string temp = Path.Combine(Path.GetTempPath(), "MhoExtMM_install_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string root;
            if (Directory.Exists(source)) root = source;
            else
            {
                Directory.CreateDirectory(temp);
                Extract(source, temp);
                root = temp;
            }
            var manifests = Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories)
                .Where(m => Path.GetRelativePath(root, m).Count(c => c == Path.DirectorySeparatorChar) <= 2).OrderBy(m => m).ToList();
            if (manifests.Count == 0) { log.Add("No manifest.json in it: not a mod in MHModManager's format."); return installed; }
            foreach (string manifestPath in manifests)
            {
                string dir = Path.GetDirectoryName(manifestPath)!;
                ModManifest m;
                try { m = ModManifest.Load(manifestPath); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { log.Add($"{Path.GetRelativePath(root, manifestPath)}: invalid manifest ({ex.Message})"); continue; }
                string name = Sanitise(string.IsNullOrWhiteSpace(m.Name) ? Path.GetFileName(dir) : m.Name);
                var probe = new Mod { Folder = dir, FolderName = name, Manifest = m };
                var missing = probe.MissingFiles().ToList();
                if (missing.Count > 0) { log.Add($"{name}: files missing from the archive: {string.Join(", ", missing)}"); continue; }
                string target = Path.Combine(lib.DataFolder, "mods", name);
                if (Directory.Exists(target)) { log.Add($"{name}: a mod with this name is already installed (remove it first to replace it)."); continue; }
                CopyDirectory(dir, target + ".tmp");
                Directory.Move(target + ".tmp", target);
                installed.Add(name);
                log.Add($"Installed '{name}' by {m.Author ?? "?"}, version {m.Version ?? "?"} (disabled, top of the list).");
            }
            if (installed.Count > 0)
            {
                // Top of the order, disabled: enable it and Apply when ready.
                var order = installed.Concat(lib.Mods.OrderBy(x => x.Priority).Select(x => x.FolderName)).ToList();
                lib.State.ModOrder = order;
                lib.State.EnabledMods = lib.Mods.Where(x => x.Enabled).OrderBy(x => x.Priority).Select(x => x.FolderName).ToList();
                File.WriteAllText(Path.Combine(lib.DataFolder, "state.json"), JsonSerializer.Serialize(lib.State, ModManifest.Json));
            }
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        return installed;
    }

    /// <summary>Unpacks an archive, refusing entries that would land outside the target folder.</summary>
    static void Extract(string archive, string to)
    {
        string full = Path.GetFullPath(to) + Path.DirectorySeparatorChar;
        using var a = ArchiveFactory.Open(archive);
        foreach (var e in a.Entries.Where(e => !e.IsDirectory))
        {
            string dest = Path.GetFullPath(Path.Combine(to, e.Key!.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"archive entry '{e.Key}' points outside the folder");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.WriteToFile(dest, new ExtractionOptions { Overwrite = true });
        }
    }

    /// <summary>The mod's folder as a .zip with manifest.json at the top (installable here and in MHModManager).</summary>
    public static void Export(Mod mod, string zipPath)
    {
        string temp = zipPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        ZipFile.CreateFromDirectory(mod.Folder, temp, CompressionLevel.Optimal, includeBaseDirectory: false);
        using (var z = ZipFile.OpenRead(temp))
            if (z.GetEntry("manifest.json") == null || z.Entries.Count != Directory.GetFiles(mod.Folder, "*", SearchOption.AllDirectories).Length)
                throw new IOException("the zip doesn't read back complete");
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>
    /// Removes a mod from the library (its folder goes to the Recycle Bin). Only when it's disabled and Apply has taken
    /// it out of the game, so nothing of it is left live that the library no longer knows about.
    /// </summary>
    public static string? Remove(Mod mod, ModLibrary lib, GameState? game)
    {
        if (mod.Enabled) return "Disable it and Apply first.";
        if (game != null)
        {
            var plan = Applier.MakePlan(lib, game, new Originals(lib.DataFolder, game));
            if (plan.Steps.Count > 0) return "Apply first: the game doesn't match the list yet, and this mod may still be in it.";
        }
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(mod.Folder, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        lib.State.ModOrder.RemoveAll(n => n.Equals(mod.FolderName, StringComparison.OrdinalIgnoreCase));
        lib.State.EnabledMods.RemoveAll(n => n.Equals(mod.FolderName, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(lib.DataFolder, "state.json"), JsonSerializer.Serialize(lib.State, ModManifest.Json));
        return null;
    }

    /// <summary>Moves the whole library (copy, verify every file's size, then delete the old one).</summary>
    public static void MoveLibrary(string from, string to)
    {
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) throw new IOException($"{to} isn't empty");
        if (Path.GetFullPath(to).StartsWith(Path.GetFullPath(from), StringComparison.OrdinalIgnoreCase)) throw new IOException("can't move the library into itself");
        try
        {
            CopyDirectory(from, to);
            foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var t = new FileInfo(Path.Combine(to, Path.GetRelativePath(from, f)));
                if (!t.Exists || t.Length != new FileInfo(f).Length) throw new IOException($"copy of {f} didn't verify; the old library is untouched");
            }
        }
        catch
        {
            if (Directory.Exists(to)) Directory.Delete(to, true);   // no half copy left behind
            throw;
        }
        Directory.Delete(from, true);
    }

    public static string Sanitise(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        string s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "mod" : s;
    }

    static void CopyDirectory(string from, string to)
    {
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }
}
