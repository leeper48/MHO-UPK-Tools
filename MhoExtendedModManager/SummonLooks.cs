using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// A hero's summons that show the mod's model (Make Unique keeps it shared with them: Jean Grey's Telepathic Illusions)
/// still draw it with their own settings, made for the game's model (Kurt, 2026-10-19, Ultimate Jean Grey in game):
/// <list type="bullet">
/// <item>Their component's Materials (the illusion's ghost material) cover material slots by number: the base illusion lists
/// one, for slot 0, enough for the game's one-slot All-New X-Men model; the mod's model has 4 slots and uses 3 and 2, so
/// the illusion showed solid. Every slot of the mod's model gets the summon's material (the last one it lists).</item>
/// <item>Their Scale (the illusion's 1.072) is set for the game's character size: the mod's character scale 0.95 made the
/// illusion 13 % bigger than her instead of 7 %. The summon's scale changes by the same factor as the character's.</item>
/// </list>
/// The summon's own package (an existing game file, its name and GUID kept) gets the change in its class default's mesh
/// component, as a package of the mod. Values are worked out from the game's copies each time, so saving again gives the
/// same result; a mod's own copy of the package (recolored powers) is the one edited.
/// </summary>
static class SummonLooks
{
    /// <summary>A component's Materials (paths, null = none) and Scale, where it's set: its own, else the classes it extends.</summary>
    sealed record Settings(List<string?>? Materials, float Scale, bool OwnMaterials, bool OwnScale);

    static string Stem(string file)
    {
        string s = Path.GetFileNameWithoutExtension(file);
        if (s.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        if (s.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) s = s[..^3];
        return s.ToLowerInvariant();
    }

    static string ComponentPath(string cls) => $"marvelgamecontent.default__{cls.ToLowerInvariant()}.initialskeletalmesh";

    /// <summary>The component's tags (from 16: owner, template name, NetIndex first) or null.</summary>
    static (byte[] Data, TagWalker Tags)? Component(Package p, string cls, out int index)
    {
        string path = ComponentPath(cls);
        index = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        byte[] d = p.ReadExportBytes(p.Exports[index]).ToArray();
        return TagWalker.Walk(p, d, 16) is { } t ? (d, t) : null;
    }

    static string RefPath(Package p, int r) => r > 0 ? p.PathOf(p.Exports[r - 1]) : r < 0 ? ImportPath(p, r) : "";

    static string ImportPath(Package p, int r)
    {
        var parts = new List<string>();
        for (int guard = 0; guard < 16 && r < 0; guard++) { var im = p.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
        if (r > 0) parts.Insert(0, p.PathOf(p.Exports[r - 1]));
        return string.Join(".", parts);
    }

    /// <summary>The summon's Materials and Scale as the game sees them: its own component's, else the first class up its
    /// chain whose component sets them (the base illusion's).</summary>
    static Settings Read(Fx.FxGame g, string cls)
    {
        List<string?>? mats = null; float? scale = null; bool ownM = false, ownS = false;
        foreach (string c in g.ClassChain(cls))
        {
            bool own = c.Equals(cls, StringComparison.OrdinalIgnoreCase);
            foreach (string pkgName in g.PackagesOf(c).Append("uc__" + c + "_sf"))
            {
                if (g.FileFor(pkgName) is not string f) continue;
                Package p;
                try { p = Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
                if (Component(p, c, out _) is not { } comp) continue;
                if (mats == null && comp.Tags.FirstOrDefault(t => t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase) && t.Size >= 4) is { } mt)
                {
                    int n = BinaryPrimitives.ReadInt32LittleEndian(comp.Data.AsSpan(mt.ValueAt));
                    mats = [];
                    for (int k = 0; k < n && 4 + 4 * k + 4 <= mt.Size; k++)
                    {
                        int r = BinaryPrimitives.ReadInt32LittleEndian(comp.Data.AsSpan(mt.ValueAt + 4 + 4 * k));
                        mats.Add(r == 0 ? null : RefPath(p, r) + "|" + (r > 0 ? p.ClassOf(p.Exports[r - 1]) : p.Imports[-r - 1].ClassName));
                    }
                    ownM = own;
                }
                if (scale == null && comp.Tags.FirstOrDefault(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } st)
                { scale = BitConverter.ToSingle(comp.Data, st.ValueAt); ownS = own; }
                break;
            }
            if (mats != null && scale != null) break;
        }
        return new(mats, scale ?? 1, ownM, ownS);
    }

    /// <summary>A character's scale as the game sees it (Jean Grey's costumes set none and inherit her base class's 0.95): the
    /// component's own, else the first class up its chain whose component sets one; 1 when none does. <paramref name="ownFile"/>
    /// is read for the class itself (the mod's copy), <paramref name="g"/>'s packages for the classes it extends.</summary>
    public static float ScaleOf(Fx.FxGame g, string cls, string? ownFile = null)
    {
        var chain = g.ClassChain(cls);
        for (int i = 0; i < chain.Count; i++)
        {
            string c = chain[i];
            var files = i == 0 && ownFile != null ? [ownFile] : g.PackagesOf(c).Append("uc__" + c + "_sf").Select(n => g.FileFor(n)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string f in files)
            {
                try
                {
                    var p = Package.Open(f);
                    if (Component(p, c, out _) is not { } comp) continue;
                    if (comp.Tags.FirstOrDefault(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } st) return BitConverter.ToSingle(comp.Data, st.ValueAt);
                    break;   // (this class's component, no scale of its own: up the chain)
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
            }
        }
        return 1;
    }

    /// <summary>The material slots of the skeletal mesh at <paramref name="path"/> in a package (0 when not there).</summary>
    static int Slots(string file, string path)
    {
        var p = Package.Open(file);
        int i = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase) && p.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : ModMeshes.MaterialRefs(AnimExportCli.Packages.Package.Open(file), i).Count;
    }

    /// <summary>The skeletal meshes of the package that the hero's own copies and summons (<paramref name="own"/>) carry
    /// under the same path, with those packages (from the stock index, so packages the mod ships count too).</summary>
    public static List<(string Path, List<string> Own)> SharedWith(string package, string file, Dictionary<string, List<string>> shared, IReadOnlySet<string> own)
    {
        var list = new List<(string, List<string>)>();
        var p = Package.Open(package);
        foreach (var e in p.Exports)
        {
            if (!p.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) continue;
            string path = p.PathOf(e);
            if (!shared.TryGetValue($"{path}|skeletalmesh", out var carriers)) continue;
            var mine = carriers.Where(c => !c.Equals(file, StringComparison.OrdinalIgnoreCase) && own.Contains(c)).ToList();
            if (mine.Count > 0) list.Add((path, mine));
        }
        return list;
    }

    /// <summary>
    /// The summon packages to change for the mod's character package <paramref name="file"/> (as it is now:
    /// <paramref name="modPath"/>; the game's: <paramref name="stockPath"/>), whose model at each of <paramref name="kept"/>
    /// (paths kept shared, with their packages) they show: file → new package bytes (verified). <paramref name="shippedPath"/>
    /// gives the mod's own copy of a package, if any (that's the one changed).
    /// </summary>
    public static Dictionary<string, byte[]> Fix(string file, string modPath, string? stockPath, IEnumerable<(string Path, List<string> Own)> kept,
        string cooked, Func<string, string?> shippedPath, IEnumerable<string> modFiles, List<string> notes)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (stockPath == null || !File.Exists(stockPath)) return result;
        var g = new Fx.FxGame(cooked, []);   // (the game's classes and settings: what the values are worked out from)
        // the character's size change: as the mod has it (its packages: a changed base class counts) over the game's, both
        // inherited (a costume that sets the 0.95 it inherits anyway changes nothing)
        var gMod = new Fx.FxGame(cooked, modFiles.Where(f => !Path.GetFileName(f).Equals(file, StringComparison.OrdinalIgnoreCase)));
        float ratio = ScaleOf(gMod, Stem(file), modPath) / Math.Max(1e-4f, ScaleOf(g, Stem(file)));
        foreach (var (meshPath, own) in kept)
        {
            int slots;
            try { slots = Slots(modPath, meshPath); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
            if (slots == 0) continue;   // (not a skeletal mesh)
            foreach (string summon in own)
            {
                string sumFile = summon.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? summon : $"UC__{summon}_SF.upk";
                if (result.ContainsKey(sumFile)) continue;
                string cls = Stem(sumFile);
                string? source = shippedPath(sumFile) ?? (g.FileFor(Path.GetFileNameWithoutExtension(sumFile)) is string sf ? sf : null);
                if (source == null) continue;
                try
                {
                    var p = Package.Open(source);
                    if (Component(p, cls, out int ci) is not { } comp) continue;   // (a power package: no character of its own)
                    // only a summon showing this very model
                    if (comp.Tags.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } smt
                        && RefPath(p, BinaryPrimitives.ReadInt32LittleEndian(comp.Data.AsSpan(smt.ValueAt))) is string shown && !shown.Equals(meshPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var set = Read(g, cls);
                    bool fillMats = set.Materials is { Count: > 0 } m && m.Count < slots && m.Any(x => x != null);
                    float scale = set.Scale * ratio;
                    // the scale as it should be, written whenever it isn't (a copy saved by 0.50.22 holds a wrong one: that
                    // version read an inherited scale as none)
                    float? have = comp.Tags.FirstOrDefault(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } hs ? BitConverter.ToSingle(comp.Data, hs.ValueAt) : null;
                    bool rescale = Math.Abs(ratio - 1) > 1e-4 ? have == null || Math.Abs(have.Value - scale) > 1e-4 : have != null && Math.Abs(have.Value - scale) > 1e-4;
                    if (!fillMats && !rescale) continue;
                    var mats = set.Materials ?? [];
                    string? last = mats.LastOrDefault(x => x != null);
                    var want = fillMats ? Enumerable.Range(0, slots).Select(k => k < mats.Count && mats[k] != null ? mats[k] : last).ToList() : null;
                    byte[] bytes = Edit(p, ci, comp.Data, comp.Tags, want, rescale ? scale : null);
                    result[sumFile] = bytes;
                    notes.Add($"{sumFile}: shows the mod's {meshPath.Split('.')[^1]}" +
                        (fillMats ? $"; its material ({last?.Split('|')[0].Split('.')[^1]}) on all {slots} of the model's slots (it covered {mats.Count})" : "") +
                        (rescale ? (Math.Abs(ratio - 1) > 1e-4 ? $"; scale {set.Scale:0.###} → {scale:0.###} (the character's {ratio:0.###}×)" : $"; scale back to the game's {scale:0.###}") : ""));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException) { notes.Add($"{sumFile}: left as it is ({ex.Message})"); }
            }
        }
        return result;
    }

    /// <summary>The package with the component's Materials and / or Scale set (tags replaced or added before None).</summary>
    static byte[] Edit(Package pkg, int comp, byte[] d, TagWalker tags, List<string?>? materials, float? scale)
    {
        var addNames = new List<string>();
        void Need(string n) { if (!pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) && !addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n); }
        var addImports = new List<NewImport>();
        int RefOf(string pathAndClass)
        {
            string path = pathAndClass.Split('|')[0], cls = pathAndClass.Split('|')[1];
            int e = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (e >= 0) return e + 1;
            string[] parts = path.Split('.');
            int outer = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                bool lastPart = k == parts.Length - 1;
                string cn = lastPart ? cls : "Package", cp = lastPart ? "Engine" : "Core";
                int found = Array.FindIndex(pkg.Imports, im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals(cn, StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outer);
                if (found >= 0) { outer = -1 - found; continue; }
                int added = addImports.FindIndex(im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.ClassName == cn && im.OuterIndex == outer);
                if (added < 0)
                {
                    foreach (string n in new[] { cp, cn, parts[k] }) Need(n);
                    addImports.Add(new NewImport(cp, cn, outer, parts[k]));
                    added = addImports.Count - 1;
                }
                outer = -(pkg.Imports.Length + added + 1);
            }
            return outer;
        }
        int[]? refs = materials?.Select(m => m == null ? 0 : RefOf(m)).ToArray();
        foreach (string n in new[] { "Materials", "ArrayProperty", "Scale", "FloatProperty", "None" }) Need(n);
        var tw = new TagWriter(pkg, addNames);
        using var ms = new MemoryStream();
        ms.Write(d, 0, 16);
        foreach (var t in tags)
        {
            if (refs != null && t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase)) continue;
            if (scale != null && t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase)) continue;
            ms.Write(d, t.Start, t.End - t.Start);
        }
        if (refs != null)
        {
            var v = new byte[4 + 4 * refs.Length];
            BinaryPrimitives.WriteInt32LittleEndian(v, refs.Length);
            for (int k = 0; k < refs.Length; k++) BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4 + 4 * k), refs[k]);
            ms.Write(tw.Tag("Materials", "ArrayProperty", null, v));
        }
        if (scale != null) ms.Write(tw.Tag("Scale", "FloatProperty", null, BitConverter.GetBytes(scale.Value)));
        ms.Write(tw.NameRef("None"));
        ms.Write(d, tags.NoneAt + 8, d.Length - tags.NoneAt - 8);
        byte[] data = ms.ToArray();
        var replace = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => data };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(pkg, output, [comp], [], written, addNames, addImports);
        var back = Package.FromBytes(output);
        if (!output.AsSpan(back.GenerationsAt - 16, 16).SequenceEqual(pkg.RawFile.AsSpan(pkg.GenerationsAt - 16, 16))) problems.Add("the package's GUID changed");
        // read back: the component's new values
        var bt = TagWalker.Walk(back, back.ReadExportBytes(back.Exports[comp]).ToArray(), 16);
        if (bt == null) problems.Add("the component doesn't read back");
        else
        {
            if (refs != null && !bt.Any(t => t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase) && t.Size == 4 + 4 * refs.Length)) problems.Add("Materials doesn't read back");
            if (scale != null && !bt.Any(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && t.Size == 4)) problems.Add("Scale doesn't read back");
        }
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems.Take(4)));
        return output;
    }
}
