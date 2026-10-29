using System.Text.RegularExpressions;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// An earlier build's leftovers (Kurt, 2026-10-27: Thor Age of Ultron's package grew from 6.5 to 23 MB): a build that starts
/// from the mod's copy (so another model built there stays) adds its own materials and textures (mff2_, mff3_ …) and leaves the
/// earlier ones in the package, unused. A package's exports can't be taken out (every index would move), so each texture of
/// an earlier build that nothing uses any more is rebuilt as a 4×4 black one in place (as --replace-texture does).
/// "Nothing uses it": not reached from any object that isn't a build's own (every reference read with the copier's exact
/// parser, ExportCopy.ReferencesOf; an object it doesn't read, a costume's class default, is searched for anything that could
/// be a reference to a build's object). If a build's own object doesn't read, nothing is changed.
/// </summary>
static class BuildLeftovers
{
    /// <summary>A build's own objects: mff_…, mff2_…, mff3_m2_… (MaterialOut's name prefixes).</summary>
    static readonly Regex BuildMade = new("^mff[0-9]*_", RegexOptions.IgnoreCase);

    /// <summary>An added prop's model copies (PowerBorrow: its own model "&lt;model&gt;_m12345", a fit "&lt;model&gt;_x100_fit12345"):
    /// each new fit or model leaves the one before in the package (Monarch, 2026-10-29: "1/14+ models after a save").</summary>
    static readonly Regex PropCopy = new(@"(_x\d+(_fit\d+)?|_m\d+)$", RegexOptions.IgnoreCase);

    /// <summary>An object a build or a prop fit made, which nothing may use any more (a model copy's sockets with it).</summary>
    static bool Candidate(Package pkg, int i)
    {
        if (BuildMade.IsMatch(pkg.Exports[i].ObjectName)) return true;
        for (int k = i, guard = 0; k >= 0 && guard < 8; k = pkg.Exports[k].OuterIndex - 1, guard++)
            if (IsModelCopy(pkg, k)) return true;
        return false;
    }

    static bool IsModelCopy(Package pkg, int i) => PropCopy.IsMatch(pkg.Exports[i].ObjectName)
        && pkg.ClassOf(pkg.Exports[i]).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The unused build textures of a package (indices), or null with the reason when references can't all be read.</summary>
    public static List<int>? Unused(Package pkg, out string why)
    {
        if (Reached(pkg, out why) is not { } reached) return null;
        return [.. Enumerable.Range(0, pkg.Exports.Length).Where(i => !reached.Contains(i) && BuildMade.IsMatch(pkg.Exports[i].ObjectName)
            && pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase) && pkg.Exports[i].SerialSize > 512)];
    }

    /// <summary>
    /// The added props' model copies in a package that nothing uses any more (names; empty when there are none or the
    /// references can't all be read), cached by file size and time. The model lists leave them out: a package's objects
    /// can't be taken out, but they needn't be offered (Monarch, 2026-10-29: 14 models after a few saves).
    /// </summary>
    public static IReadOnlySet<string> UnusedModels(string path)
    {
        string key;
        try { var fi = new FileInfo(path); key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"; }
        catch (IOException) { return new HashSet<string>(); }
        lock (unusedModels) if (unusedModels.TryGetValue(key, out var had)) return had;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pkg = Package.Open(path);
            // (most packages have no copies at all: nothing to walk)
            if (Enumerable.Range(0, pkg.Exports.Length).Any(i => IsModelCopy(pkg, i)))
                lock (walkLock)
                    if (Reached(pkg, out _) is { } reached)
                        for (int i = 0; i < pkg.Exports.Length; i++)
                            if (!reached.Contains(i) && IsModelCopy(pkg, i)) found.Add(pkg.Exports[i].ObjectName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException or IndexOutOfRangeException) { }
        lock (unusedModels) unusedModels[key] = found;
        return found;
    }
    static readonly Dictionary<string, IReadOnlySet<string>> unusedModels = new(StringComparer.OrdinalIgnoreCase);
    static readonly object walkLock = new();

    /// <summary>Every object reached from one no build made (null with the reason when one of a build's doesn't read).</summary>
    static HashSet<int>? Reached(Package pkg, out string why)
    {
        why = "";
        MeshCopy.Register();   // (skeletal meshes' native data: their materials)
        var reached = new HashSet<int>();
        int scanned = 0;
        var scannedKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (!Candidate(pkg, i)) { reached.Add(i); queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            var refs = ExportCopy.ReferencesOf(pkg, i);
            if (refs == null)
            {
                // an object the parser doesn't read (a costume's class default): every 4 bytes in it that could be a reference
                // to a build's object count as one (it can only keep more, never drop one in use)
                if (Candidate(pkg, i)) { why = $"{pkg.PathOf(pkg.Exports[i])} ({pkg.ClassOf(pkg.Exports[i])}) doesn't read"; return null; }
                byte[] d = pkg.ReadExportBytes(pkg.Exports[i]);
                refs = [];
                for (int k = 0; k + 4 <= d.Length; k++)
                {
                    int v = BitConverter.ToInt32(d, k);
                    if (v > 0 && v <= pkg.Exports.Length && Candidate(pkg, v - 1)) refs.Add(v);
                }
                scanned++;
                scannedKinds[pkg.ClassOf(pkg.Exports[i])] = scannedKinds.GetValueOrDefault(pkg.ClassOf(pkg.Exports[i])) + 1;
            }
            foreach (int r in refs)
                if (r > 0 && r <= pkg.Exports.Length && reached.Add(r - 1)) queue.Enqueue(r - 1);
        }
        why = scanned > 0 ? $"{scanned} object(s) the parser doesn't read were searched byte by byte ({string.Join(", ", scannedKinds.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Value} {k.Key}"))})" : "";
        return reached;
    }

    /// <summary>The package with its unused build textures made 4×4 (verified), or null when there's nothing to do or it
    /// can't be done (the reason in the log).</summary>
    public static byte[]? Shrink(byte[] package, Action<string> log)
    {
        var pkg = Package.FromBytes(package);
        var unused = Unused(pkg, out string why);
        if (unused == null) { log($"cleanup: skipped ({why}): the earlier builds' textures stay"); return null; }
        if (unused.Count == 0) return null;
        long before = unused.Sum(i => (long)pkg.Exports[i].SerialSize);
        var items = unused.Select(i => new TextureImport.Replacement(pkg.PathOf(pkg.Exports[i]), TinyDds(), "cleanup")).ToList();
        var output = TextureImport.ReplaceMany(pkg, items, out var problems, out var verify);
        if (output == null) { log("cleanup: skipped (" + string.Join("; ", problems.Take(2)) + ")"); return null; }
        var check = verify(output);
        if (check.Count > 0) { log("cleanup: skipped (" + string.Join("; ", check.Take(2)) + ")"); return null; }
        log($"cleanup: {unused.Count} texture(s) of earlier builds nothing uses any more made 4×4 ({before / 1048576.0:0.0} MB); package {package.Length / 1048576.0:0.0} → {output.Length / 1048576.0:0.0} MB");
        return output;
    }

    /// <summary>A 4×4 black DXT1 .dds, one mip.</summary>
    static byte[] TinyDds()
    {
        var d = new byte[128 + 8];
        "DDS "u8.CopyTo(d);
        void W(int at, int v) => BitConverter.GetBytes(v).CopyTo(d, at);
        W(4, 124); W(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); W(12, 4); W(16, 4); W(20, 8); W(28, 1);
        W(76, 32); W(80, 0x4); "DXT1"u8.CopyTo(d.AsSpan(84)); W(108, 0x1000);
        return d;   // (the block's colors 0 and 0: black)
    }
}
