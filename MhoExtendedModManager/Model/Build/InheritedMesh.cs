using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// A costume with no model of its own (Kurt, 2026-10-08: "a target without a skeleton inside that references another base
/// character's skeleton"): 20 stock player costumes have no SkeletalMesh export, and their class default's mesh component sets
/// no SkeletalMesh, so it inherits the hero base component's (Captain America Avengers, Carnage Classic, Venom Classic, Thing
/// Classic, Rogue Modern, Spider-Man Big Time …; --mesh-refs). Every costume of the hero shows that one base mesh, so it can't be
/// built on in place. The costume package gets a copy of it under its own name (&lt;mesh&gt;_&lt;costume&gt;: an object with the
/// base mesh's path would replace it for every costume loaded after), with its materials, and its component names the copy;
/// the Model tab then builds onto that like any costume's own model. Only the costume's package changes; its GUID stays.
/// </summary>
static class InheritedMesh
{
    /// <summary>The hero base package whose mesh the costume shows, when <paramref name="package"/> is a player costume with
    /// no skeletal mesh and a component that sets none; else null.</summary>
    public static string? BaseFor(string package)
    {
        var (cls, hero, _) = Parse(package);
        if (cls == null) return null;
        try
        {
            var mp = Package.Open(package);
            if (mp.Exports.Any(e => mp.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))) return null;
            int comp = Component(mp, cls);
            if (comp < 0) return null;
            byte[] d = mp.ReadExportBytes(mp.Exports[comp]).ToArray();
            var tags = TagWalker.Walk(mp, d, 16);
            if (tags == null || tags.Any(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))) return null;
            string basePath = BasePackage.Resolve($"UC__MarvelPlayer_{hero}_SF.upk", true);
            return File.Exists(basePath) ? basePath : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or FileNotFoundException) { return null; }
    }

    /// <summary>(class, hero, costume part) of UC__MarvelPlayer_&lt;Hero&gt;_&lt;Costume&gt;_SF; nulls for anything else.</summary>
    static (string? Class, string Hero, string Costume) Parse(string package)
    {
        string stem = Path.GetFileNameWithoutExtension(package);
        if (!stem.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || !stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) return (null, "", "");
        string cls = stem[4..^3];
        var p = cls.Split('_');
        if (p.Length < 3) return (null, "", "");   // a hero's base package
        return (cls, p[1], string.Join('_', p.Skip(2)));
    }

    static int Component(Package p, string cls)
    {
        string path = $"marvelgamecontent.default__{cls.ToLowerInvariant()}.initialskeletalmesh";
        return Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The package to start from: <paramref name="package"/> itself, or, for a costume that inherits its hero's model,
    /// a copy with that model in it (made once per version of the two files, kept in data\model\inherit).</summary>
    public static string Start(string package, Action<string>? log = null)
    {
        if (BaseFor(package) is not string basePath) return package;
        var a = new FileInfo(package); var b = new FileInfo(basePath);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{a.FullName}|{a.Length}|{a.LastWriteTimeUtc.Ticks}|{b.FullName}|{b.Length}|{b.LastWriteTimeUtc.Ticks}")))[..16];
        string dir = Path.Combine(Settings.Home, "inherit", key);
        string outFile = Path.Combine(dir, a.Name);
        if (File.Exists(outFile)) return outFile;
        var lines = new List<string>();
        byte[] bytes = Build(package, basePath, lines);
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        string tmp = outFile + ".making";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, outFile, true);
        foreach (var l in lines) log?.Invoke(l);
        return outFile;
    }

    /// <summary>The costume package with its hero's model copied in under the costume's name and its component naming it.</summary>
    public static byte[] Build(string package, string basePath, List<string> log)
    {
        var (cls, hero, costume) = Parse(package);
        if (cls == null) throw new InvalidDataException($"{Path.GetFileName(package)} isn't a costume package");
        MeshCopy.Register();
        var dst = Package.Open(package);
        var bas = Package.Open(basePath);
        string meshName = MhoSkeleton.ComponentMesh(basePath) ?? MhoSkeleton.Load(basePath, null).Name;
        int mi = Array.FindIndex(bas.Exports, e => e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && bas.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        if (mi < 0) throw new InvalidDataException($"no skeletal mesh {meshName} in {Path.GetFileName(basePath)}");
        string newName = $"{meshName}_{costume}".ToLowerInvariant();
        var copy = CrossMove.Quiet(() => ExportCopy.Copy(bas, mi, dst, [], newName), out string said)
                   ?? throw new InvalidDataException($"{hero}'s model {meshName} couldn't be copied into {Path.GetFileName(package)}: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not supported")).Select(l => l.Trim())));
        var pkg = Package.FromBytes(copy.Output);
        log.Add($"inherit: {Path.GetFileName(package)} has no model of its own (it shows {hero}'s {meshName} from {Path.GetFileName(basePath)}): a copy, {newName}, is put into it as the costume's own model; the other {hero} costumes keep the base one");

        // the costume's component: a SkeletalMesh property naming the copy, before its None
        int comp = Component(pkg, cls);
        if (comp < 0) throw new InvalidDataException($"no mesh component for {cls} in {Path.GetFileName(package)}");
        byte[] d = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var tags = TagWalker.Walk(pkg, d, 16) ?? throw new InvalidDataException("the costume's mesh component doesn't read");
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return pkg.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var tag = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("SkeletalMesh"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ObjectProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(24), copy.RootRef);
        byte[] nd = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
        var replace = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("pointing the costume at its model: " + string.Join("; ", problems.Take(3)));
        // read back: the component names the copy, and the GUID is still the costume's (the game checks it)
        var back = Package.FromBytes(output);
        if (MeshOf(back, cls) is not string got || !got.Equals(newName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("the costume's component doesn't name its new model");
        if (!File.ReadAllBytes(package).AsSpan(dst.GenerationsAt - 16, 16).SequenceEqual(output.AsSpan(back.GenerationsAt - 16, 16))) throw new InvalidDataException("the package's GUID changed");
        log.Add($"inherit: component {cls.ToLowerInvariant()}.initialskeletalmesh → {newName}; package verified ({copy.Output.Length:N0} bytes)");
        return output;
    }

    /// <summary>The name of the skeletal mesh export the costume's component names, or null.</summary>
    static string? MeshOf(Package p, string cls)
    {
        int comp = Component(p, cls);
        if (comp < 0) return null;
        byte[] d = p.ReadExportBytes(p.Exports[comp]).ToArray();
        var t = TagWalker.Walk(p, d, 16)?.FirstOrDefault(x => x.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && x.Size == 4);
        int r = t == null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt));
        return r > 0 ? p.Exports[r - 1].ObjectName : null;
    }
}
