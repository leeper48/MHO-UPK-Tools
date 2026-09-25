using System.Security.Cryptography;
using System.Text;

namespace UpkMeshScan;

/// <summary>
/// Undo / redo for game-file writes. Before every write through MeshImport.WriteLive and every --revert, the live
/// file as it was is saved as a snapshot, OUTSIDE the game folder (%LOCALAPPDATA%\UpkMeshScan\history\&lt;package&gt;),
/// with the command that changed it and the SHA-256 of the file before and after. --undo puts the previous version
/// back (the current one goes on the redo list), --redo re-applies it; a new write clears the redo list. Both refuse
/// if the live file isn't the version the history expects (changed by another tool or copied over by hand), unless
/// --force. Restores use the same verified path as every write: copy to a temp file next to the live file, check its
/// hash, swap, check again. The .bak (the stock original) is never touched. The last MaxEntries snapshots are kept.
/// </summary>
static class History
{
    public const int MaxEntries = 20;

    /// <summary>What the next recorded change is called (CLI: the command line; GUI: the action's title).</summary>
    public static string Label { get; set; } = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(a => Path.IsPathRooted(a) ? Path.GetFileName(a) : a));

    sealed record Entry(int Id, DateTime Time, string Before, string After, string Label);

    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpkMeshScan", "history");

    /// <summary>One folder per live file: its name plus a short hash of its full path (two game folders don't mix).</summary>
    static string Folder(string upkPath)
    {
        string full = Path.GetFullPath(upkPath);
        string h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8];
        return Path.Combine(Root, $"{Path.GetFileName(full)}_{h}");
    }

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    static (List<Entry> Undo, List<Entry> Redo) Load(string dir)
    {
        var undo = new List<Entry>(); var redo = new List<Entry>();
        string f = Path.Combine(dir, "history.txt");
        if (!File.Exists(f)) return (undo, redo);
        foreach (string line in File.ReadAllLines(f))
        {
            string[] p = line.Split('\t', 6);
            if (p.Length < 6) continue;
            var e = new Entry(int.Parse(p[1]), DateTime.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), p[3], p[4], p[5]);
            (p[0] == "U" ? undo : redo).Add(e);
        }
        return (undo, redo);
    }

    static void Save(string dir, List<Entry> undo, List<Entry> redo)
    {
        Directory.CreateDirectory(dir);
        var lines = undo.Select(e => Line("U", e)).Concat(redo.Select(e => Line("R", e)));
        File.WriteAllLines(Path.Combine(dir, "history.txt"), lines);
        static string Line(string k, Entry e) => $"{k}\t{e.Id}\t{e.Time.ToString("s", System.Globalization.CultureInfo.InvariantCulture)}\t{e.Before}\t{e.After}\t{e.Label.Replace('\t', ' ').Replace('\n', ' ')}";
    }

    static string Snap(string dir, Entry e) => Path.Combine(dir, $"{e.Id:D5}.upk");

    /// <summary>
    /// Called just before the live file is replaced with `after`: snapshots the current live file (the undo point) and
    /// clears the redo list. Never stops a write: a snapshot failure is reported and the write goes ahead.
    /// </summary>
    public static void Record(string upkPath, byte[] after)
    {
        try
        {
            if (!File.Exists(upkPath)) return;
            string dir = Folder(upkPath);
            Directory.CreateDirectory(dir);
            var (undo, redo) = Load(dir);
            byte[] before = File.ReadAllBytes(upkPath);
            int id = undo.Concat(redo).Select(e => e.Id).DefaultIfEmpty(0).Max() + 1;
            var entry = new Entry(id, DateTime.Now, Hash(before), Hash(after), Label);
            File.WriteAllBytes(Snap(dir, entry), before);
            foreach (var r in redo) TryDelete(Snap(dir, r));
            redo.Clear();
            undo.Add(entry);
            while (undo.Count > MaxEntries) { TryDelete(Snap(dir, undo[0])); undo.RemoveAt(0); }
            Save(dir, undo, redo);
            Console.WriteLine($"  history: saved the previous version (undo with --undo \"{upkPath}\"; {undo.Count} step(s) kept)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  history: couldn't save an undo snapshot ({ex.Message}); the write goes ahead, --revert still works");
        }
    }

    public static int Undo(string upkPath, bool force) => Step(upkPath, force, undoing: true);
    public static int Redo(string upkPath, bool force) => Step(upkPath, force, undoing: false);

    static int Step(string upkPath, bool force, bool undoing)
    {
        upkPath = Path.GetFullPath(upkPath);
        string verb = undoing ? "Undo" : "Redo";
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        string dir = Folder(upkPath);
        var (undo, redo) = Load(dir);
        var from = undoing ? undo : redo;
        if (from.Count == 0) { Console.WriteLine($"{verb}: nothing to {verb.ToLowerInvariant()} for {Path.GetFileName(upkPath)}."); return 1; }
        var e = from[^1];
        byte[] current = File.ReadAllBytes(upkPath);
        // The live file must be the version this step starts from: after the change (undo) or before it (redo).
        string expected = undoing ? e.After : e.Before;
        if (Hash(current) != expected && !force)
        {
            Console.WriteLine($"{verb}: {Path.GetFileName(upkPath)} isn't the version the history expects (changed outside this tool?). Nothing changed; --force to {verb.ToLowerInvariant()} anyway.");
            return 1;
        }
        string snapFile = Snap(dir, e);
        if (!File.Exists(snapFile)) { Console.WriteLine($"{verb}: snapshot {snapFile} is missing."); return 1; }
        byte[] target = File.ReadAllBytes(snapFile);
        if (Hash(target) != (undoing ? e.Before : e.After)) { Console.WriteLine($"{verb}: the snapshot doesn't match its recorded hash; nothing changed."); return 1; }
        Console.WriteLine($"{verb}: {Path.GetFileName(upkPath)} -> {(undoing ? "before" : "after")} \"{e.Label}\" ({e.Time:yyyy-MM-dd HH:mm})");
        if (!MeshImport.RestoreBytes(upkPath, target)) return 1;

        // Move the step to the other list; its snapshot now holds the version we just replaced.
        from.RemoveAt(from.Count - 1);
        File.WriteAllBytes(snapFile, current);
        (undoing ? redo : undo).Add(e);
        Save(dir, undo, redo);
        Console.WriteLine($"  {undo.Count} undo / {redo.Count} redo step(s) left");
        return 0;
    }

    public static int List(string upkPath)
    {
        upkPath = Path.GetFullPath(upkPath);
        var (undo, redo) = Load(Folder(upkPath));
        string liveHash = File.Exists(upkPath) ? Hash(File.ReadAllBytes(upkPath)) : "";
        Console.WriteLine($"History of {Path.GetFileName(upkPath)} ({Folder(upkPath)}):");
        if (undo.Count + redo.Count == 0) { Console.WriteLine("  (none)"); return 0; }
        foreach (var e in undo) Console.WriteLine($"  undo  {e.Time:yyyy-MM-dd HH:mm}  {e.Label}{(e == undo[^1] && e.After == liveHash ? "   <- current" : "")}");
        foreach (var e in Enumerable.Reverse(redo)) Console.WriteLine($"  redo  {e.Time:yyyy-MM-dd HH:mm}  {e.Label}");
        if (undo.Count > 0 && undo[^1].After != liveHash) Console.WriteLine("  note: the live file isn't the version after the last recorded change (changed outside this tool?)");
        return 0;
    }

    /// <summary>(undo steps, redo steps) for a package, for the GUI.</summary>
    public static (int Undo, int Redo) Counts(string upkPath)
    {
        try { var (u, r) = Load(Folder(upkPath)); return (u.Count, r.Count); }
        catch (Exception ex) when (ex is IOException or FormatException) { return (0, 0); }
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch (IOException) { } }
}
