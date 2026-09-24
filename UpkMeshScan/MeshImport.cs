using System.Numerics;
using System.Security.Cryptography;

namespace UpkMeshScan;

/// <summary>
/// FBX -> StaticMesh import, --revert, and the --verify-import-roundtrip self-test.
/// Game files are only written by --import-fbx without --dry-run, following CLAUDE.md rule 1:
/// .upk.bak created once (never overwritten) and verified, new package built and verified in a
/// temp file first, then moved over the live file. --revert copies the .bak back and keeps it.
/// </summary>
static class MeshImport
{
    static readonly DateTime StockDate = new(2024, 3, 14);

    sealed record Prepared(Package Package, int ExportIndex, StaticMesh Original, BuiltMesh Built, byte[] PackageBytes, byte[] ExportBytes, List<string> Problems);

    static Prepared Prepare(string upkPath, string meshName, string fbxPath)
    {
        var pkg = Package.Open(upkPath);
        int index = FindStaticMesh(pkg, meshName);
        var original = StaticMesh.Read(pkg, pkg.Exports[index]);
        var imported = FbxMeshReader.Read(fbxPath, original.NumTexCoords, original.Name, out var used);
        Console.WriteLine($"  FBX objects used: {(used.Count == 0 ? "(none)" : string.Join(", ", used))}");
        var built = StaticMeshBuilder.Build(original, imported);
        return Finish(pkg, index, original, built);
    }

    static Prepared Finish(Package pkg, int index, StaticMesh original, BuiltMesh built)
    {
        byte[]? exportBytes = null;
        byte[] packageBytes = PackageWriter.ReplaceExport(pkg, index, offset => exportBytes = StaticMeshBuilder.Serialize(original, built, offset));

        var problems = new List<string>();
        Package written = Package.FromBytes(packageBytes);
        problems.AddRange(PackageWriter.Verify(pkg, written, index, exportBytes!));

        var entry = written.Exports[index];
        StaticMesh reread = StaticMesh.Parse(written, entry.ObjectName, written.ReadExportBytes(entry), entry.SerialOffset);
        void Check(bool ok, string what) { if (!ok) problems.Add(what); }
        Check(reread.Positions.SequenceEqual(built.Positions), "re-read positions differ from built");
        Check(reread.Indices.SequenceEqual(built.Indices), "re-read indices differ from built");
        Check(reread.TangentX.SequenceEqual(built.TangentX) && reread.TangentZ.SequenceEqual(built.TangentZ), "re-read tangents differ from built");
        Check(reread.Adjacency.SequenceEqual(built.Adjacency), "re-read adjacency differs from built");
        Check(reread.Sections.Length == built.Sections.Length && reread.Sections.Zip(built.Sections).All(p => p.First with { MaterialName = "" } == p.Second with { MaterialName = "" }), "re-read sections differ from built");
        for (int c = 0; c < built.TexCoords.Length; c++)
            Check(reread.TexCoords[c].Zip(built.TexCoords[c]).All(p => Vector2.Distance(p.First, p.Second) < 0.002f), $"re-read UV channel {c} differs beyond half precision");
        Check(reread.KdopTriangleCount == 0, "collision tree not empty");
        Check(reread.BoundsOrigin == built.BoundsOrigin && reread.BoundsExtent == built.BoundsExtent && reread.BoundsRadius == built.BoundsRadius, "bounds differ");
        Check(!reread.Notes.Any(n => n.Contains("bulk-data offset")), "LOD bulk-data offset doesn't point at itself");
        return new Prepared(pkg, index, original, built, packageBytes, exportBytes!, problems);
    }

    static int FindStaticMesh(Package pkg, string meshName)
    {
        var matches = Enumerable.Range(0, pkg.Exports.Length)
            .Where(i => pkg.ClassOf(pkg.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
            .Where(i => pkg.Exports[i].ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(pkg.Exports[i]).Equals(meshName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1) throw new InvalidDataException(matches.Count == 0 ? $"no StaticMesh named '{meshName}'" : $"'{meshName}' matches {matches.Count} StaticMeshes; pass the full path");
        return matches[0];
    }

    static void Summarize(Prepared p)
    {
        var o = p.Original; var b = p.Built;
        Console.WriteLine($"  original: {o.Positions.Length,7:N0} verts {o.Indices.Length / 3,7:N0} tris  bounds {V(o.BoundsOrigin - o.BoundsExtent)}..{V(o.BoundsOrigin + o.BoundsExtent)}  collision tris {o.KdopTriangleCount:N0}");
        Console.WriteLine($"  imported: {b.Positions.Length,7:N0} verts {b.Indices.Length / 3,7:N0} tris  bounds {V(b.BoundsOrigin - b.BoundsExtent)}..{V(b.BoundsOrigin + b.BoundsExtent)}  collision tris 0 (empty)");
        foreach (var s in b.Sections) Console.WriteLine($"    section {s.NumTriangles,7:N0} tris  verts {s.MinVertexIndex}-{s.MaxVertexIndex}  {s.MaterialName}");
        Console.WriteLine($"  export: {o.Layout.Original.Length:N0} -> {p.ExportBytes.Length:N0} bytes; package: {p.Package.RawFile.Length:N0} -> {p.PackageBytes.Length:N0} bytes (uncompressed)");
        if (p.Problems.Count == 0) Console.WriteLine("  verify: PASS (re-read package: tables identical, all other exports byte-identical, new mesh decodes to exactly what was built)");
        else { Console.WriteLine($"  verify: FAIL ({p.Problems.Count})"); foreach (var x in p.Problems) Console.WriteLine($"    - {x}"); }
    }

    static string V(Vector3 v) => $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})";

    // ------------------------------------------------------------------ commands

    public static int Import(string upkPath, string meshName, string fbxPath, bool dryRun, string? outDir)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to import into a .bak/copy file."); return 2; }
        Console.WriteLine($"Import {Path.GetFileName(fbxPath)} -> {Path.GetFileName(upkPath)} :: {meshName}{(dryRun ? "  [dry run]" : "")}");

        Prepared p;
        try { p = Prepare(upkPath, meshName, fbxPath); }
        catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IOException or Assimp.AssimpException)
        { Console.WriteLine($"  cannot import: {ex.Message}"); return 1; }
        Summarize(p);
        if (p.Problems.Count > 0) { Console.WriteLine("  Nothing written."); return 1; }

        if (dryRun)
        {
            string dir = outDir ?? Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(upkPath));
            if (string.Equals(Path.GetFullPath(target), upkPath, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  --out points at the game file; refusing."); return 2; }
            File.WriteAllBytes(target, p.PackageBytes);
            Console.WriteLine($"  dry run: wrote {target} (game folder untouched)");
            return 0;
        }

        // Live import. 0) file not locked; 1) back up once; 2) write + re-verify temp from disk; 3) replace.
        if (Locked(upkPath)) return 1;
        string bak = upkPath + ".bak";
        if (!File.Exists(bak))
        {
            if (File.GetLastWriteTime(upkPath).Date != StockDate)
                Console.WriteLine($"  warning: {Path.GetFileName(upkPath)} is dated {File.GetLastWriteTime(upkPath):yyyy-MM-dd}, not the 2024-03-14 stock date; the backup will be of an already-modified file.");
            File.Copy(upkPath, bak, overwrite: false);
            if (!SameBytes(upkPath, bak)) { Console.WriteLine("  backup copy doesn't match the original; stopping."); return 1; }
            Console.WriteLine($"  backup: created {Path.GetFileName(bak)} (verified identical)");
        }
        else Console.WriteLine($"  backup: {Path.GetFileName(bak)} already exists, left as is (it's the original)");

        string temp = upkPath + ".importtmp";
        File.WriteAllBytes(temp, p.PackageBytes);
        byte[] onDisk = File.ReadAllBytes(temp);
        if (!onDisk.AsSpan().SequenceEqual(p.PackageBytes)) { File.Delete(temp); Console.WriteLine("  temp file didn't read back identically; live file untouched."); return 1; }
        var fromDisk = Package.FromBytes(onDisk);
        var problems = PackageWriter.Verify(p.Package, fromDisk, p.ExportIndex, p.ExportBytes);
        if (problems.Count > 0) { File.Delete(temp); Console.WriteLine($"  temp file failed verification ({string.Join("; ", problems)}); live file untouched."); return 1; }

        if (!TryReplace(temp, upkPath)) return 1;
        if (!SameHash(File.ReadAllBytes(upkPath), p.PackageBytes)) { Console.WriteLine("  WARNING: live file doesn't match what was written. Run --revert."); return 1; }
        Console.WriteLine($"  imported: {Path.GetFileName(upkPath)} replaced and verified. Undo with --revert \"{upkPath}\"");
        return 0;
    }

    public static int Revert(string upkPath)
    {
        upkPath = Path.GetFullPath(upkPath);
        string bak = upkPath + ".bak";
        if (!File.Exists(bak)) { Console.WriteLine($"No {Path.GetFileName(bak)} to revert from."); return 1; }
        if (File.Exists(upkPath) && SameBytes(upkPath, bak)) { Console.WriteLine($"{Path.GetFileName(upkPath)} already matches its .bak; nothing to do."); return 0; }
        if (Locked(upkPath)) return 1;
        string temp = upkPath + ".reverttmp";
        File.Copy(bak, temp, overwrite: true);
        if (!SameBytes(temp, bak)) { File.Delete(temp); Console.WriteLine("Copy of the .bak didn't verify; live file untouched."); return 1; }
        if (!TryReplace(temp, upkPath)) return 1;
        bool ok = SameBytes(upkPath, bak);
        Console.WriteLine(ok ? $"Reverted {Path.GetFileName(upkPath)} from {Path.GetFileName(bak)} (verified identical; .bak kept)." : "WARNING: live file doesn't match the .bak after revert.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Self-test on an unmodified mesh, writing nothing to the game folder: export to a temp FBX,
    /// import it back, and compare with the original decode. Also checks the builder's tangent,
    /// adjacency and bounds rules against the original's stored data, and runs the package writer
    /// + verifier in memory.
    /// </summary>
    public static int VerifyRoundTrip(string upkPath, string meshName)
    {
        var pkg = Package.Open(upkPath);
        int index = FindStaticMesh(pkg, meshName);
        var o = StaticMesh.Read(pkg, pkg.Exports[index]);
        Console.WriteLine($"Round trip: {pkg.PathOf(pkg.Exports[index])} ({o.Positions.Length:N0} verts, {o.Indices.Length / 3:N0} tris)");

        // Rules vs the original's stored data.
        var (tx, tz) = StaticMeshBuilder.Tangents(o.Positions, o.Normals, o.TexCoords[0], o.Indices);
        int tAgree = 0, sAgree = 0;
        for (int v = 0; v < o.Positions.Length; v++)
        {
            if (Vector3.Dot(Unpack(tx[v]), Unpack(o.TangentX[v])) > 0.9f) tAgree++;
            if ((tz[v] >> 24) == (o.TangentZ[v] >> 24)) sAgree++;
        }
        Console.WriteLine($"  tangent rule vs stored: direction {Pct(tAgree, o.Positions.Length)}, binormal sign {Pct(sAgree, o.Positions.Length)}");
        var adj = StaticMeshBuilder.Adjacency(o.Positions, o.TexCoords[0], o.Indices);
        int edgeSame = 0, domSame = 0, tris = o.Indices.Length / 3;
        for (int t = 0; t < tris; t++)
        {
            for (int k = 3; k < 9; k++) if (adj[12 * t + k] == o.Adjacency[12 * t + k]) edgeSame++;
            for (int k = 9; k < 12; k++) if (adj[12 * t + k] == o.Adjacency[12 * t + k]) domSame++;
        }
        Console.WriteLine($"  adjacency rule vs stored: edge entries {Pct(edgeSame, tris * 6)}, dominant corners {Pct(domSame, tris * 3)}");
        Vector3 mn = o.Positions.Aggregate(Vector3.Min), mx = o.Positions.Aggregate(Vector3.Max), org = (mn + mx) * 0.5f;
        float rad = MathF.Sqrt(o.Positions.Max(p => Vector3.DistanceSquared(p, org)));
        Console.WriteLine($"  bounds rule vs stored: origin diff {Vector3.Distance(org, o.BoundsOrigin):0.####}, extent diff {Vector3.Distance((mx - mn) * 0.5f, o.BoundsExtent):0.####}, radius {rad:0.###} vs {o.BoundsRadius:0.###}");

        // FBX round trip.
        string tempDir = Path.Combine(Path.GetTempPath(), "UpkMeshScan_roundtrip");
        Directory.CreateDirectory(tempDir);
        if (StaticMeshExport.Run(upkPath, pkg.PathOf(pkg.Exports[index]), tempDir, quiet: true) != 0) return 1;
        string fbx = Path.Combine(tempDir, $"{o.Name}.fbx");
        var built = StaticMeshBuilder.Build(o, FbxMeshReader.Read(fbx, o.NumTexCoords));

        var original = CornerMap(o.Positions, o.Normals, o.TexCoords, o.Indices, o.Sections);
        var roundTrip = CornerMap(built.Positions, built.Normals, built.TexCoords, built.Indices, built.Sections);
        int missing = 0; float maxNormalDeg = 0, maxUv = 0;
        foreach (var (key, list) in original)
        {
            if (!roundTrip.TryGetValue(key, out var other) || other.Count != list.Count) { missing += list.Count; continue; }
            for (int i = 0; i < list.Count; i++)
                for (int c = 0; c < 3; c++)
                {
                    float dot = Math.Clamp(Vector3.Dot(Vector3.Normalize(list[i].N[c]), Vector3.Normalize(other[i].N[c])), -1f, 1f);
                    maxNormalDeg = MathF.Max(maxNormalDeg, MathF.Acos(dot) * 180f / MathF.PI);
                    for (int ch = 0; ch < list[i].Uv[c].Length; ch++) maxUv = MathF.Max(maxUv, Vector2.Distance(list[i].Uv[c][ch], other[i].Uv[c][ch]));
                }
        }
        int extra = roundTrip.Sum(kv => kv.Value.Count) - original.Sum(kv => kv.Value.Count);
        if (missing > 0)
        {
            // Classify: same triangle with reversed winding? different section? count mismatch on duplicates?
            var rtNoSection = roundTrip.Keys.Select(k => k[(k.IndexOf('|') + 1)..]).ToHashSet();
            int reversed = 0, otherSection = 0, duplicateCount = 0, absent = 0;
            foreach (var (key, list) in original)
            {
                if (roundTrip.TryGetValue(key, out var other)) { if (other.Count != list.Count) duplicateCount += list.Count; continue; }
                var parts = key.Split('|');
                string rev = $"{parts[1]}|{parts[3]}|{parts[2]}";
                var revCanon = new[] { $"{parts[1]}|{parts[3]}|{parts[2]}", $"{parts[3]}|{parts[2]}|{parts[1]}", $"{parts[2]}|{parts[1]}|{parts[3]}" }.Order(StringComparer.Ordinal).First();
                bool degenerate = parts[1] == parts[2] || parts[2] == parts[3] || parts[1] == parts[3];
                if (rtNoSection.Contains($"{parts[1]}|{parts[2]}|{parts[3]}")) otherSection += list.Count;
                else if (rtNoSection.Any(k => k == rev || k == revCanon)) reversed += list.Count;
                else { absent += list.Count; if (absent <= 3) Console.WriteLine($"    absent: {key}{(degenerate ? "  (degenerate)" : "")}"); }
            }
            Console.WriteLine($"    missing breakdown: reversed winding {reversed}, other section {otherSection}, duplicate-count mismatch {duplicateCount}, absent {absent}");
        }
        Console.WriteLine($"  FBX round trip: {built.Positions.Length:N0} verts (original {o.Positions.Length:N0}), {built.Indices.Length / 3:N0} tris; " +
                          $"triangles not found with same positions+winding+section: {missing}, extra: {extra}; max normal error {maxNormalDeg:0.###}°, max UV error {maxUv:0.#####}");

        var prepared = Finish(pkg, index, o, built);
        Summarize(prepared);
        bool pass = missing == 0 && extra == 0 && prepared.Problems.Count == 0;
        Console.WriteLine(pass ? "ROUND TRIP: PASS" : "ROUND TRIP: FAIL");
        return pass ? 0 : 1;
    }

    sealed record Corner(Vector3[] N, Vector2[][] Uv);

    /// <summary>Triangles keyed by section + positions in winding order (rotated to a canonical start).</summary>
    static Dictionary<string, List<Corner>> CornerMap(Vector3[] p, Vector3[] n, Vector2[][] uv, ushort[] idx, StaticMeshSection[] sections)
    {
        var map = new Dictionary<string, List<Corner>>();
        foreach (var s in sections)
            for (int i = s.FirstIndex; i < s.FirstIndex + s.NumTriangles * 3; i += 3)
            {
                int[] c = [idx[i], idx[i + 1], idx[i + 2]];
                string K(int v) => $"{MathF.Round(p[v].X, 2) + 0f},{MathF.Round(p[v].Y, 2) + 0f},{MathF.Round(p[v].Z, 2) + 0f}";   // + 0f folds -0 into 0
                int start = Enumerable.Range(0, 3).OrderBy(k => K(c[k]), StringComparer.Ordinal).First();
                int[] r = [c[start], c[(start + 1) % 3], c[(start + 2) % 3]];
                string key = $"{s.MaterialName}|{K(r[0])}|{K(r[1])}|{K(r[2])}";
                var corner = new Corner(r.Select(v => n[v]).ToArray(), r.Select(v => uv.Select(ch => ch[v]).ToArray()).ToArray());
                if (!map.TryGetValue(key, out var list)) map[key] = list = new();
                list.Add(corner);
            }
        return map;
    }

    /// <summary>True (with a message) if something — usually the running game — holds the file open.</summary>
    static bool Locked(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  {Path.GetFileName(path)} is in use (is Marvel Heroes running?). Close the game and try again. Nothing was changed.");
            return true;
        }
    }

    /// <summary>Moves temp over target; on failure deletes temp, reports, and leaves target untouched.</summary>
    static bool TryReplace(string temp, string target)
    {
        try { File.Move(temp, target, overwrite: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { }
            Console.WriteLine($"  Couldn't replace {Path.GetFileName(target)} ({ex.Message}). Is the game running? The file was left as it was.");
            return false;
        }
    }

    static Vector3 Unpack(uint packed) => new((packed & 0xFF) / 127.5f - 1f, ((packed >> 8) & 0xFF) / 127.5f - 1f, ((packed >> 16) & 0xFF) / 127.5f - 1f);
    static string Pct(int a, int b) => b == 0 ? "n/a" : $"{a:N0}/{b:N0} ({100.0 * a / b:0.00}%)";
    static bool SameBytes(string a, string b) => new FileInfo(a).Length == new FileInfo(b).Length && SameHash(File.ReadAllBytes(a), File.ReadAllBytes(b));
    static bool SameHash(byte[] a, byte[] b) => SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
}
