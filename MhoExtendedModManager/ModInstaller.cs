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
    /// <param name="replace">Asked when a mod with the same name is installed already (existing, incoming): true replaces
    /// it, keeping its folder, so its place, on/off, lock, tags and note stay. Null: refuse, as before.</param>
    /// <param name="into">Update this mod with the archive's (single) mod, whatever its name ("Update from a file…").</param>
    public static List<string> Install(string source, ModLibrary lib, List<string> log, Func<Mod, ModManifest, bool>? replace = null, Mod? into = null)
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
            if (into != null && manifests.Count != 1) { log.Add($"It holds {manifests.Count} mods: to update '{into.Name}' it has to hold exactly one."); return installed; }
            var updated = new List<string>();
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
                var existing = into ?? lib.Mods.FirstOrDefault(x => x.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing == null && Directory.Exists(target)) { log.Add($"{name}: a folder with this name is in the library already."); continue; }
                if (existing != null)
                {
                    if (replace == null || !replace(existing, m)) { log.Add($"{name}: already installed, left as it was."); continue; }
                    // Update in place: the new files next to the old, checked, then the old folder to the Recycle Bin and the
                    // new one in its place. Same folder name, so state.json (order, on/off, lock, tags, note) still applies.
                    string fresh = existing.Folder + ".new";
                    if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
                    CopyDirectory(dir, fresh);
                    var want = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(dir, f), new FileInfo(f).Length)).Order().ToList();
                    var got = Directory.GetFiles(fresh, "*", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(fresh, f), new FileInfo(f).Length)).Order().ToList();
                    if (!want.SequenceEqual(got)) { Directory.Delete(fresh, true); log.Add($"{name}: the copy didn't check out, nothing changed."); continue; }
                    string oldPost = Path.Combine(existing.Folder, ModPost.Folder), newPost = Path.Combine(fresh, ModPost.Folder);
                    if (Directory.Exists(oldPost) && !Directory.Exists(newPost)) CopyDirectory(oldPost, newPost);   // keep the saved post
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(existing.Folder, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    Directory.Move(fresh, existing.Folder);
                    updated.Add(existing.FolderName);
                    installed.Add(existing.FolderName);
                    log.Add($"Updated '{existing.Name}' ({existing.Manifest.Version ?? "?"} → {m.Version ?? "?"}): kept its place, on/off, lock, tags and note; the old files are in the Recycle Bin." +
                            (existing.Enabled ? " It's on: Apply Changes puts the new version in the game." : ""));
                    continue;
                }
                CopyDirectory(dir, target + ".tmp");
                Directory.Move(target + ".tmp", target);
                installed.Add(name);
                log.Add($"Installed '{name}' by {m.Author ?? "?"}, version {m.Version ?? "?"} (disabled, top of the list).");
            }
            if (installed.Count > updated.Count)
            {
                // Top of the order, disabled: enable it and Apply when ready.
                var order = installed.Except(updated).Concat(lib.Mods.OrderBy(x => x.Priority).Select(x => x.FolderName)).ToList();
                lib.State.ModOrder = order;
                lib.State.EnabledMods = lib.Mods.Where(x => x.Enabled).OrderBy(x => x.Priority).Select(x => x.FolderName).ToList();
                lib.State.ApplyLocks();   // locked mods keep their place at the top / bottom
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

    /// <summary>
    /// The mod's folder as a .zip with manifest.json at the top, installable here and in MHModManager (which ignores the
    /// ExtraIconReplacements extension). <paramref name="legacy"/>: leave the extension out entirely (its field and the
    /// .dds files only it uses), for a strictly MHModManager-format mod.
    /// </summary>
    /// <summary>The export's file name: "&lt;name&gt; - v&lt;version&gt;.zip" (no version: just the name), " (legacy)" for a legacy copy.</summary>
    public static string ZipName(Mod mod, bool legacy = false)
    {
        string v = (mod.Manifest.Version ?? "").Trim().TrimStart('v', 'V').Trim();
        return Sanitise(mod.Name + (v.Length > 0 ? " - v" + v : "")) + (legacy ? " (legacy)" : "") + ".zip";
    }

    public static void Export(Mod mod, string zipPath, bool legacy = false, IEnumerable<string>? addTags = null, string? note = null,
        string? previewPick = null, Dictionary<string, float[]>? views = null, float? light = null)
    {
        string temp = zipPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        var files = Directory.GetFiles(mod.Folder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(mod.Folder, f).StartsWith(ModPost.Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();   // the post goes beside the zip
        byte[]? manifest = null;
        var extraTags = (addTags ?? []).Where(t => !mod.ModTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!legacy && (extraTags.Count > 0 || note != null || previewPick != null || views is { Count: > 0 } || light != null))
        {
            // The user's tags / note / preview choice / 3D views / light go into the exported copy (the library's manifest isn't changed).
            var m = ModManifest.Load(Path.Combine(mod.Folder, "manifest.json"));
            if (extraTags.Count > 0) m.Tags = [.. m.Tags ?? [], .. extraTags];
            if (note != null) m.Notes = note.Length > 0 ? note : null;
            if (previewPick != null) m.PreviewImage = previewPick;
            if (views is { Count: > 0 }) { m.PreviewViews ??= []; foreach (var (k, v) in views) m.PreviewViews[k] = v; }
            if (light is float lv) m.PreviewLight = Math.Abs(lv - 1f) < 1e-4 ? null : MathF.Round(lv, 2);
            manifest = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, ModManifest.Json));
        }
        if (legacy && (mod.Manifest.Extra.Any() || mod.Manifest.Tags != null || mod.Manifest.Notes != null || mod.Manifest.Description != null || mod.Manifest.Changelog != null || mod.Manifest.NexusModId != null || mod.Manifest.PreviewImage != null || mod.Manifest.PreviewViews != null || mod.Manifest.PreviewLight != null || mod.Manifest.VoiceOff != null))
        {
            var m = ModManifest.Load(Path.Combine(mod.Folder, "manifest.json"));
            m.Tags = null; m.Notes = null; m.Description = null; m.Changelog = null; m.NexusModId = null; m.PreviewImage = null; m.PreviewViews = null; m.PreviewLight = null; m.VoiceOff = null;   // extensions: a legacy copy is MHModManager's format only
            var keep = m.Replacements.Concat(m.AchievementReplacements).Concat(m.StoreReplacements).Select(r => r.DdsFileName).Concat(m.UpkReplacements).Concat(m.AudioPacks).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropOnly = m.Extra.Select(r => r.DdsFileName).Where(f => !keep.Contains(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            files.RemoveAll(f => dropOnly.Contains(Path.GetRelativePath(mod.Folder, f)));
            m.ExtraIconReplacements = null;
            if (!m.HasTextures && m.Replacements.Count + m.AchievementReplacements.Count + m.StoreReplacements.Count == 0 && m.Type == ModType.Texture)
                m.Type = m.HasUpkReplacements ? ModType.Upk : m.HasStrings ? ModType.String : m.HasAudio ? ModType.Audio : ModType.Texture;
            manifest = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, ModManifest.Json));
        }
        using (var z = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (string f in files)
            {
                string name = Path.GetRelativePath(mod.Folder, f).Replace('\\', '/');
                if (manifest != null && name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    using var s = z.CreateEntry(name, CompressionLevel.Optimal).Open();
                    s.Write(manifest);
                }
                else z.CreateEntryFromFile(f, name, CompressionLevel.Optimal);
            }
        }
        using (var z = ZipFile.OpenRead(temp))
            if (z.GetEntry("manifest.json") == null || z.Entries.Count != files.Count)
                throw new IOException("the zip doesn't read back complete");
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>
    /// Removes a mod from the library (its folder goes to the Recycle Bin). Only when it's disabled and Apply has taken
    /// it out of the game, so nothing of it is left live that the library no longer knows about.
    /// </summary>
    /// <summary>
    /// The game files only this mod names, so Apply stops looking after them once it's gone: its packages, its other icon
    /// packages and the sound packages its packs patch, less those another mod names too. (The three classic icon
    /// packages, strings and Icons.tfc are always rebuilt from their originals, with or without the mod.)
    /// </summary>
    public static HashSet<string> OnlyItsFiles(Mod mod, ModLibrary lib)
    {
        static IEnumerable<string> Files(Mod m)
        {
            foreach (string f in m.Manifest.UpkReplacements) yield return f;
            foreach (var r in m.Manifest.Extra) yield return r.Package;
            foreach (string a in m.Manifest.AudioPacks)
            {
                List<string> pcks = [];
                try { string p = Path.Combine(m.Folder, a); if (File.Exists(p)) pcks = SoundPack.Load(p).Patches.Select(x => x.PckFile).ToList(); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or FormatException) { }
                foreach (string pck in pcks) yield return pck;
            }
        }
        var mine = Files(mod).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in lib.Mods.Where(o => o != mod)) mine.ExceptWith(Files(other));
        return mine;
    }

    public static string? Remove(Mod mod, ModLibrary lib, GameState? game, bool dryRun = false)
    {
        if (mod.Enabled) return "Turn it off and Apply Changes first.";
        if (game != null)
        {
            // Only this mod's own files matter: once it's removed, Apply no longer puts their originals back. Pending
            // changes to anything else (other mods, icons, strings) don't stop a removal; the next Apply does them.
            var mine = OnlyItsFiles(mod, lib);
            var plan = Applier.MakePlan(lib, game, new Originals(lib.DataFolder, game));
            var live = plan.Steps.Where(s => mine.Contains(s.File)).Select(s => s.File).ToList();
            if (live.Count > 0)
                return $"Apply Changes first: {live.Count} of its file(s) are still modded in the game, and after removing the mod nothing would put the originals back:\n" +
                       string.Join("\n", live.Take(8).Select(f => "  " + f)) + (live.Count > 8 ? $"\n  … and {live.Count - 8} more" : "");
            var stuck = plan.Problems.Where(p => mine.Any(f => p.StartsWith(f + ":", StringComparison.OrdinalIgnoreCase))).ToList();
            if (stuck.Count > 0)
                return "Some of its files can't be checked, so it may still be in the game:\n" + string.Join("\n", stuck.Take(8).Select(p => "  " + p));
        }
        if (dryRun) return null;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(mod.Folder, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        lib.State.ModOrder.RemoveAll(n => n.Equals(mod.FolderName, StringComparison.OrdinalIgnoreCase));
        lib.State.EnabledMods.RemoveAll(n => n.Equals(mod.FolderName, StringComparison.OrdinalIgnoreCase));
        lib.State.ForgetMod(mod.FolderName);
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
