using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MhoPackageModifier;
using AnimPackage = AnimExportCli.Packages.Package;
using AnimExportCli.Meshes;

namespace MhoExtendedModManager;

/// <summary>
/// Copying a skeletal mesh into another package (cross-hero costume move, phase 1, 2026-09-29): MPM's ExportCopy maps every
/// name and object reference of what it copies, but doesn't know a SkeletalMesh's native data; this supplies it
/// (ExportCopy.NativeHook), using AnimExportCli's reader to find where the levels of detail end.
///
/// SkeletalMesh native data (after the properties), checked on 12 meshes of 8 heroes: bounds (28 bytes); materials (count,
/// object references); origin and rotation origin (24 bytes); bones (count × 52 bytes, each starting with its name); skeletal
/// depth (4); the levels of detail (geometry only: no names or references); the NameIndexMap (count = bones, then name + int
/// per bone); then 44 bytes: three empty arrays (12 zero bytes), 1, 0, 4, two floats, 12 zero bytes. Anything else stops the
/// copy. Inline bulk-data "offset in file" fields inside the LODs are left stale, as in other tools' working packages.
/// </summary>
static class MeshCopy
{
    static readonly ConditionalWeakTable<Package, AnimPackage> anim = new();

    /// <summary>Makes ExportCopy able to copy skeletal meshes (idempotent).</summary>
    public static void Register() => ExportCopy.NativeHook ??= Native;

    static List<ExportCopy.NativeRef>? Native(Package pkg, byte[] d, int p, string cls)
    {
        try { return NativeRefs(pkg, d, p, cls); }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("MHO_EXTMM_DEBUG") == "1") { Console.WriteLine(ex); throw; }
    }

    static List<ExportCopy.NativeRef>? NativeRefs(Package pkg, byte[] d, int p, string cls)
    {
        if (cls != "skeletalmesh") return null;
        var ap = anim.GetValue(pkg, x => AnimPackage.Read(x.RawFile));
        int index = -1;
        foreach (int i in ap.FindExportsOfClass(SkeletalMeshReader.ClassName))
            if (ap.GetExportData(i).SequenceEqual(d)) { index = i; break; }
        if (index < 0) throw new InvalidDataException("skeletal mesh data not found in its package");
        string why = "";
        var mesh = SkeletalMeshReader.TryRead(ap, index, e => why = e) ?? throw new InvalidDataException("skeletal mesh doesn't read: " + why);

        var refs = new List<ExportCopy.NativeRef>();
        int at = p + MeshBounds.ByteSize;
        int materials = I32(d, at); at += 4;
        if (materials < 0 || materials > 256) throw new InvalidDataException($"{materials} materials");
        for (int k = 0; k < materials; k++, at += 4) refs.Add(new(at, false, $"native.materials[{k}]"));
        at += 24;                                                     // origin, rotation origin
        int bones = I32(d, at); at += 4;
        if (bones != mesh.Bones.Count) throw new InvalidDataException($"bone count {bones}, the reader found {mesh.Bones.Count}");
        for (int k = 0; k < bones; k++, at += 52) refs.Add(new(at, true, $"native.bones[{k}].name"));
        int map = mesh.LodsEnd;
        if (I32(d, map) != bones) throw new InvalidDataException($"name map count {I32(d, map)}, expected {bones}");
        int m = map + 4;
        for (int k = 0; k < bones; k++, m += 12) refs.Add(new(m, true, $"native.namemap[{k}]"));
        // The 44-byte end: 3 empty arrays, 1, 0, 4, a float, 16 zero bytes.
        // (two floats at +24 and +28: the second is 0 in most meshes, not in Beast, Dr Doom, Punisher Modern.)
        if (d.Length - m != 44 || d.AsSpan(m, 12).ContainsAnyExcept((byte)0) || I32(d, m + 12) != 1 || I32(d, m + 16) != 0 || I32(d, m + 20) != 4
            || d.AsSpan(m + 32, 12).ContainsAnyExcept((byte)0))
            throw new InvalidDataException($"skeletal mesh end isn't the known 44-byte layout ({d.Length - m} bytes)");
        return refs;
    }

    static int I32(byte[] d, int p) => p + 4 <= d.Length ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)) : throw new InvalidDataException("read past the end");

    /// <summary>
    /// Test: a mesh copied from <paramref name="srcPath"/> into <paramref name="dstPath"/> (renamed), written to
    /// <paramref name="outPath"/> and read back: same bones, geometry and material paths as the source. Returns problems.
    /// </summary>
    public static List<string> Test(string srcPath, string meshName, string dstPath, string outPath)
    {
        Register();
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_DEBUG") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => { if (e.Exception is ArgumentOutOfRangeException) Console.WriteLine("FIRST CHANCE: " + e.Exception + Environment.NewLine + Environment.StackTrace); };
        var problems = new List<string>();
        var src = Package.Open(srcPath);
        var dst = Package.Open(dstPath);
        int root = Array.FindIndex(src.Exports, e => e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && src.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        if (root < 0) return [$"no skeletal mesh '{meshName}'"];
        var result = ExportCopy.Copy(src, root, dst, [], meshName + "_copytest");
        if (result == null) return ["copy refused (see above)"];
        File.WriteAllBytes(outPath, result.Output);

        var sa = AnimPackage.Open(srcPath);
        var ta = AnimPackage.Read(result.Output);
        int si = sa.FindExportsOfClass(SkeletalMeshReader.ClassName).First(i => sa.GetExportName(i).Equals(meshName, StringComparison.OrdinalIgnoreCase));
        int ti = result.RootRef - 1;
        var a = SkeletalMeshReader.TryRead(sa, si);
        var b = SkeletalMeshReader.TryRead(ta, ti, e => problems.Add("copy doesn't read: " + e));
        if (a == null || b == null) return problems;
        if (!a.Bones.Select(x => x.Name).SequenceEqual(b.Bones.Select(x => x.Name))) problems.Add("bone names differ");
        if (a.Lods.Count != b.Lods.Count) problems.Add("LOD count differs");
        for (int l = 0; l < Math.Min(a.Lods.Count, b.Lods.Count); l++)
        {
            if (!a.Lods[l].Positions.SequenceEqual(b.Lods[l].Positions)) problems.Add($"LOD {l} positions differ");
            if (!a.Lods[l].Indices.SequenceEqual(b.Lods[l].Indices)) problems.Add($"LOD {l} indices differ");
        }
        // Materials: the same paths (the copy's own, or ones the target already had).
        var mp = Package.FromBytes(result.Output);
        static List<string> Paths(AnimPackage ap, int i, Package p) =>
            [.. ModMeshes.MaterialRefs(ap, i).Select(r => r > 0 ? p.PathOf(p.Exports[r - 1]) : r < 0 ? "import " + p.RefName(r) : "none")];
        var sm = Paths(sa, si, src);
        var tm = Paths(ta, ti, mp);
        if (!sm.SequenceEqual(tm)) problems.Add($"materials differ: {string.Join(", ", sm)} vs {string.Join(", ", tm)}");
        return problems;
    }
}
