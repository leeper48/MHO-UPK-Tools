using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The character's mesh component on biped_lib.pc_at_nocape when cape bones that move by animation (an added rig, or a
/// moved costume's cape given motion) hang from a tree that blends them apart: pc_at_v2 and pc_at_v2_walkblend (Startup.upk)
/// start per-bone blends at g_cape1 and g_l/r_cape1-3, second child at full weight (Kurt, 2026-11-20: Vision's cape on Ultron,
/// moved to his main package, moved in idle, fidgets and flight but hung still in the walk, although the walk's cape tracks
/// swing it 70°; Ultron's tree is pc_at_v2, Vision's pc_at_nocape).
///
/// pc_at_nocape is otherwise the same tree except: its upper-body split starts at the shoulders (pc_at_v2 also at g_spine02 /
/// g_spine03), it has no g_gun_backpack branch, and no add_lean / add_pivot additives (no pc_at_v2 hero checked plays those:
/// Ultron, Colossus, Daredevil, Hulk, Spider-Man, Punisher). Hair and sash bones are branched in both trees, so a hair or sash
/// rig doesn't change the tree. Other trees are left as they are.
/// </summary>
public static class RigTree
{
    static readonly HashSet<string> CapeBranchBones = new(["g_cape1", "g_l_cape1", "g_l_cape2", "g_l_cape3", "g_r_cape1", "g_r_cape2", "g_r_cape3"], StringComparer.OrdinalIgnoreCase);
    static readonly string[] BranchTrees = ["pc_at_v2", "pc_at_v2_walkblend"];
    const string Target = "pc_at_nocape";

    /// <summary>The package with every character component on such a tree moved to pc_at_nocape when <paramref name="moving"/>
    /// (the bones given motion) holds one of the branched cape bones; the package unchanged otherwise.</summary>
    /// <param name="heroBase">The hero's base package (UC__MarvelPlayer_&lt;Hero&gt;_SF): a costume component without a tree of its
    /// own plays the base component's (Daredevil Modern: pc_at_v2), and then gets its own AnimTreeTemplate.</param>
    public static byte[] Apply(byte[] packageBytes, IEnumerable<string> moving, Action<string> log, string? heroBase = null)
    {
        if (!moving.Any(CapeBranchBones.Contains)) return packageBytes;
        var inherited = heroBase != null && File.Exists(heroBase) ? BaseTree(heroBase) : null;
        var src = Package.FromBytes(packageBytes);
        for (int i = 0; i < src.Exports.Length; i++)
        {
            var e = src.Exports[i];
            if (!src.ClassOf(e).Equals("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            string path = src.PathOf(e);
            if (!path.EndsWith(".initialskeletalmesh", StringComparison.OrdinalIgnoreCase) || !path.Contains("default__marvel", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = src.ReadExportBytes(e).ToArray();
            if (TagWalker.Walk(src, d, 16) is not { } tags) { log($"tree:    {path}: properties not read, its tree left as it is"); continue; }
            var at = tags.FirstOrDefault(t => t.Name.Equals("AnimTreeTemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4);
            string outer, from;
            if (at != null)
            {
                int r = BitConverter.ToInt32(d, at.ValueAt);
                if (r >= 0) { log($"tree:    {path}: {(r == 0 ? "no tree (none)" : "its own tree")}: left as it is"); continue; }
                var im = src.Imports[-r - 1];
                if (!BranchTrees.Contains(im.ObjectName, StringComparer.OrdinalIgnoreCase) || im.OuterIndex >= 0) { if (!im.ObjectName.Equals(Target, StringComparison.OrdinalIgnoreCase)) log($"tree:    {path}: {im.ObjectName} doesn't blend the cape apart: left as it is"); continue; }
                outer = src.Imports[-im.OuterIndex - 1].ObjectName; from = im.ObjectName;
            }
            else if (inherited is { } it && path.Contains("default__marvelplayer_", StringComparison.OrdinalIgnoreCase)) { outer = it.Outer; from = it.Name + " (the hero's)"; }
            else { log($"tree:    {path}: no tree of its own{(heroBase == null ? "" : inherited == null ? $" and {Path.GetFileName(heroBase)}'s isn't one that blends the cape apart" : "")}: left as it is"); continue; }
            var addNames = new List<string>();
            var addImports = new List<NewImport>();
            int NameIdx(string n)
            {
                int k = Array.FindIndex(src.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (k >= 0) return k;
                if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
                return src.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            }
            int outerAt = Array.FindIndex(src.Imports, x => x.ObjectName.Equals(outer, StringComparison.OrdinalIgnoreCase) && x.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase) && x.OuterIndex == 0);
            int outerRef;
            if (outerAt >= 0) outerRef = -1 - outerAt;
            else
            {
                foreach (string n in new[] { "Core", "Package", outer }) NameIdx(n);
                addImports.Add(new NewImport("Core", "Package", 0, outer));
                outerRef = -(src.Imports.Length + addImports.Count);
            }
            int found = Array.FindIndex(src.Imports, x => x.ObjectName.Equals(Target, StringComparison.OrdinalIgnoreCase) && x.ClassName.Equals("AnimTree", StringComparison.OrdinalIgnoreCase) && x.OuterIndex == outerRef);
            int treeRef;
            if (found >= 0) treeRef = -1 - found;
            else
            {
                foreach (string n in new[] { "Engine", "AnimTree", Target }) NameIdx(n);
                addImports.Add(new NewImport("Engine", "AnimTree", outerRef, Target));
                treeRef = -(src.Imports.Length + addImports.Count);
            }
            if (at != null) BitConverter.TryWriteBytes(d.AsSpan(at.ValueAt), treeRef);
            else
            {
                var tag = new byte[28];
                BitConverter.TryWriteBytes(tag.AsSpan(0), NameIdx("AnimTreeTemplate"));
                BitConverter.TryWriteBytes(tag.AsSpan(8), NameIdx("ObjectProperty"));
                BitConverter.TryWriteBytes(tag.AsSpan(16), 4);
                BitConverter.TryWriteBytes(tag.AsSpan(24), treeRef);
                d = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
            }
            byte[] dd = d;
            byte[] bytes = PackageRebuilder.Rebuild(src, new Dictionary<int, Func<long, byte[]>> { [i] = _ => dd }, [], out var written, addNames, addImports);
            var problems = PackageRebuilder.Verify(src, bytes, [i], [], written, addNames, addImports);
            if (problems.Count > 0) { foreach (var x in problems.Take(3)) log("tree:    PACKAGE PROBLEM: " + x); continue; }
            log($"tree:    {path}: animation tree {outer}.{from} → {outer}.{Target} (it blends the cape bones apart from the animation while moving, so the cape's motion didn't play in the walk)");
            packageBytes = bytes;
            src = Package.FromBytes(bytes);
        }
        return packageBytes;
    }

    /// <summary>The tree the hero's base component plays when it's a cape-blending shared one (package import, tree name).</summary>
    static (string Outer, string Name)? BaseTree(string heroBase)
    {
        try
        {
            var p = Package.Open(heroBase);
            for (int i = 0; i < p.Exports.Length; i++)
            {
                var e = p.Exports[i];
                if (!p.ClassOf(e).Equals("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
                string path = p.PathOf(e);
                if (!path.EndsWith(".initialskeletalmesh", StringComparison.OrdinalIgnoreCase) || !path.Contains("default__marvelplayer_", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] d = p.ReadExportBytes(e).ToArray();
                if (TagWalker.Walk(p, d, 16)?.FirstOrDefault(t => t.Name.Equals("AnimTreeTemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } at) continue;
                int r = BitConverter.ToInt32(d, at.ValueAt);
                if (r >= 0) return null;
                var im = p.Imports[-r - 1];
                if (!BranchTrees.Contains(im.ObjectName, StringComparer.OrdinalIgnoreCase) || im.OuterIndex >= 0) return null;
                return (p.Imports[-im.OuterIndex - 1].ObjectName, im.ObjectName);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return null;
    }
}
