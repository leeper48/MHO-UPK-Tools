using MpmPackage = MhoPackageModifier.Package;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Writes a copy of a package with one export replaced, using MHO Package Modifier's PackageWriter (proven in game:
/// uncompressed, the new export appended, only its table entry rewritten, every other byte in place) and its Verify
/// (reads the result back: tables unchanged, every other export byte-identical, the replaced one as built).
/// The MPM code is vendored (Vendor\MhoPackageModifier), compiled into this app.
/// Output never goes into the game folder, the stock backup or the MFF source (<see cref="Protected"/>).
/// </summary>
static class PackageOut
{
    public static byte[] ReplaceExport(MpmPackage pkg, int exportIndex, byte[] export) =>
        MhoPackageModifier.PackageWriter.ReplaceExport(pkg, exportIndex, _ => export);

    public static List<string> Verify(MpmPackage original, MpmPackage written, int exportIndex, byte[] expected) =>
        MhoPackageModifier.PackageWriter.Verify(original, written, exportIndex, expected);

    /// <summary>Replaces the export named <paramref name="exportName"/> (class SkeletalMesh) in a copy of
    /// <paramref name="source"/> written to <paramref name="outFile"/>, then verifies it. Returns problems (empty = ok).</summary>
    public static List<string> WriteMesh(string source, string exportName, byte[] export, string outFile) =>
        WriteMesh(File.ReadAllBytes(source), Path.GetFileName(source), exportName, export, outFile);

    /// <summary>As above, from package bytes in memory (e.g. the base package with the new materials added).</summary>
    public static List<string> WriteMesh(byte[] sourceBytes, string sourceName, string exportName, byte[] export, string outFile)
    {
        Protected.CheckWrite(outFile);
        var src = MpmPackage.FromBytes(sourceBytes);
        string source = sourceName;
        int idx = Array.FindIndex(src.Exports, e => e.ObjectName.Equals(exportName, StringComparison.OrdinalIgnoreCase)
                                                    && src.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return [$"no SkeletalMesh '{exportName}' in {Path.GetFileName(source)}"];
        var bytes = ReplaceExport(src, idx, export);
        var written = MpmPackage.FromBytes(bytes);
        var problems = Verify(src, written, idx, export);
        if (problems.Count > 0) return problems;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        string tmp = outFile + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (!File.ReadAllBytes(tmp).AsSpan().SequenceEqual(bytes)) { File.Delete(tmp); return ["the written file doesn't read back as written"]; }
        File.Move(tmp, outFile, true);
        return problems;
    }
}
