using System.Text.RegularExpressions;
using AnimExportCli.Animation;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Motion for the parts a costume brings to another hero that only its own hero's animations move (Kurt, 2026-11-20: "this
/// should help future costume moves with the missing rigs, not just this one cape"). The moved model keeps those bones (a
/// cape, a coat, hair, a tail …), but the new hero's animations have no keys for them, so they hang still (Doctor Strange's
/// cape on Daredevil, Vision's on Ultron). Here each such part is a rig in place, matched from the source hero's own
/// animations: every frame of the new hero's animations takes the part's pose from the source hero's frame whose body moves
/// most like it (CapeMatch, as the Model tab's Add Rigs, with the source hero as the donor), written into copies of the new
/// hero's animation sets (HairAnims). Nothing in the mesh changes. A cape hanging from a tree that blends its bones apart is
/// put on the tree caped heroes use (RigTree).
/// </summary>
static class PartMotion
{
    /// <summary>The parts that hang still on the target: driven only by the source's animations.</summary>
    public static List<CrossMove.ExtraPart> Still(IEnumerable<CrossMove.ExtraPart> parts) => parts.Where(p => p.Driver == "animations" && !p.Moves).ToList();

    /// <summary>The moved package (<paramref name="file"/>, the game's <paramref name="packageFile"/>) with the parts' motion in
    /// its animations; null (logged) when nothing could be added.</summary>
    public static byte[]? Apply(string file, string packageFile, string meshName, Mod mod, string srcFile, string sourceName, IReadOnlyList<CrossMove.ExtraPart> parts, string? cooked, Action<string> log, string? heroBase = null)
    {
        var still = Still(parts);
        if (still.Count == 0) return null;
        string modPkg = Path.Combine(mod.Folder, srcFile);
        string mesh = meshName.Split('.')[^1];
        var donor = MhoSkeleton.Load(modPkg, mesh);
        var sk = MhoSkeleton.Load(file, mesh);
        var mods = mod.Manifest.UpkReplacements.Select(f => (f, Path.Combine(mod.Folder, f))).ToList();
        var anims = ModAnimations.For(new MeshRef(srcFile, modPkg, mesh, 0), donor.Bones, mods, cooked).Select(ModAnimations.Load).OfType<BoneAnimation>().ToList();
        if (anims.Count == 0) { log($"parts:   {sourceName}'s animations couldn't be read: the parts stay as they are"); return null; }
        var rigs = new List<BorrowedRig>();
        foreach (var part in still)
        {
            string kind = part.Kind.ToString().ToLowerInvariant();
            var rx = new Regex("^(" + string.Join("|", part.Names.Select(Regex.Escape)) + ")$", RegexOptions.IgnoreCase);
            if (sk.AdoptRig(donor, rx, out var corrections) is not { } adopted) { log($"parts:   the {kind}'s bones aren't the same in the moved model: left as it is"); continue; }
            var rigBones = Enumerable.Range(0, donor.Bones.Count).Where(i => rx.IsMatch(donor.Bones[i].Name)).ToList();
            var lib = CapeMatch.Build(donor.Bones, rigBones, anims);
            if (lib.Frames == 0) { log($"parts:   {sourceName}'s animations don't move the {kind}: left as it is"); continue; }
            rigs.Add(new BorrowedRig { Part = part.Kind, Label = $"{sourceName}'s {kind}", Library = lib, Corrections = corrections, Bones = rx });
            sk = adopted;
            log($"parts:   {kind} ({part.Bones} bones): motion matched from {lib.Frames} frames of {sourceName}'s animations{(lib.Unkeyed > 0 ? $" ({lib.Unkeyed} animations that hold it still left out)" : "")}");
        }
        if (rigs.Count == 0) return null;
        var res = HairAnims.Add(file, packageFile, sk.Bones, sk.Borrowed, rigs, log);
        log($"parts:   {res.Sets} animation set(s), {res.Sequences} animations written with the {string.Join(" and ", rigs.Select(r => r.Part.ToString().ToLowerInvariant()))}'s motion");
        return RigTree.Apply(res.Package, sk.Borrowed, log, heroBase);
    }
}
