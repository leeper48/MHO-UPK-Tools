using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// What to do with game packages changed by something this app doesn't manage (ApplyCheck.Unexpected; Kurt, 2026-10-01):
/// keep them as a mod (so they show in the list, can be turned off, and Apply manages them), or put the game's originals
/// back. Both only use what's already there: Keep copies the live files into the library; Restore writes the clean
/// original over the live file through Applier.Execute (its .bak made from the original if missing, WriteLive, undo
/// history, the game's date).
/// </summary>
static class GameFiles
{
    /// <summary>Copies <paramref name="files"/> (live packages) into a new mod <paramref name="name"/>, each checked to
    /// read back identical; the mod is turned on, lowest priority (real mods win where they overlap). Null = done, else why not.</summary>
    public static string? KeepAsMod(ModLibrary lib, GameState game, IReadOnlyList<string> files, string name)
    {
        if (files.Count == 0) return "No files chosen.";
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.')) return "That name can't be a folder name.";
        string folder = Path.Combine(lib.DataFolder, "mods", name);
        if (Directory.Exists(folder) || lib.Mods.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return $"A mod named \"{name}\" exists already.";
        string temp = folder + ".tmp";
        if (Directory.Exists(temp)) return $"A leftover folder \"{name}.tmp\" is in the library; remove it first.";
        Directory.CreateDirectory(temp);
        foreach (string f in files)
        {
            string live = Path.Combine(game.Cooked, f), copy = Path.Combine(temp, f);
            if (!File.Exists(live)) { Directory.Delete(temp, true); return $"{f} isn't in the game folder."; }
            File.Copy(live, copy);
            if (game.CrcOf(File.ReadAllBytes(copy)) != game.Crc(live)) { Directory.Delete(temp, true); return $"The copy of {f} didn't read back identical; nothing kept."; }
        }
        var manifest = new ModManifest
        {
            Name = name, Author = "captured from the game", Version = DateTime.Now.ToString("yyyy-MM-dd"), Type = ModType.Upk,
            UpkReplacements = [.. files], HasUpkReplacements = true,
            Notes = $"Game files that were changed outside this app, kept as a mod on {DateTime.Now:yyyy-MM-dd}.",
        };
        File.WriteAllText(Path.Combine(temp, "manifest.json"), JsonSerializer.Serialize(manifest, ModManifest.Json));
        Directory.Move(temp, folder);
        lib.State.ModOrder = lib.Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).Append(name).ToList();
        lib.State.EnabledMods = lib.Mods.Where(m => m.Enabled).Select(m => m.FolderName).Append(name).ToList();
        lib.State.ApplyLocks();
        File.WriteAllText(Path.Combine(lib.DataFolder, "state.json"), JsonSerializer.Serialize(lib.State, ModManifest.Json));
        return null;
    }

    /// <summary>Writes each file's clean original over the live file (Applier.Execute; it prints its log). False = stopped.</summary>
    public static bool Restore(GameState game, Originals originals, string libraryData, IReadOnlyList<string> files)
    {
        var steps = new List<Applier.Step>();
        foreach (string f in files)
        {
            string? original = originals.Find(f);
            if (original == null) { Console.WriteLine($"{f}: no clean original found; left as it is."); continue; }
            steps.Add(new Applier.Step(f, "restore stock original (changed outside this app)", original, null, game.Crc(original)));
        }
        if (steps.Count == 0) return false;
        return Applier.Execute(new Applier.Plan(steps, [], 0, []), game, originals, libraryData);
    }
}
