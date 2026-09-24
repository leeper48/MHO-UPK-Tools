using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace UpkMeshScan;

/// <summary>
/// --add-sky-placeholders: puts low-poly placeholder buildings (an FBX in world space, e.g. from
/// --zone-placeholders) into a zone's always-loaded sky sphere mesh as a second section, drawn with a new
/// flat-grey copy of the sky's material instance. The sky sphere component is scaled (500 in Midtown) and sits
/// at the origin, so world positions are divided by its scale. The package is rebuilt (PackageRebuilder) to
/// hold the new material; everything else stays byte-identical. Dry run or the usual .bak / verify / swap.
/// </summary>
static class SkyPlaceholders
{
    public static int Run(string upkPath, string fbxPath, string meshName, string micName, float gray, bool dryRun,
        IReadOnlyList<float[]> excludeBoxes, float? groundZ, float groundMargin, float shrink = 1f, IReadOnlyList<string>? addFbx = null)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        Console.WriteLine($"Sky placeholders: {Path.GetFileName(fbxPath)} -> {Path.GetFileName(upkPath)} :: {meshName}{(dryRun ? "  [dry run]" : "")}");

        int meshIndex = Find(pkg, meshName, "StaticMesh");
        int micIndex = Find(pkg, micName, "MaterialInstanceConstant");
        if (meshIndex < 0 || micIndex < 0) { Console.WriteLine($"  couldn't find {(meshIndex < 0 ? meshName : micName)}"); return 1; }

        // Where the sky sphere is placed: its component's scale; translation and rotation must be zero.
        ComponentTransform? placement = null;
        foreach (var e in pkg.Exports)
            if (pkg.ClassOf(e).Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)
                && ComponentTransform.Read(pkg, pkg.ReadExportBytes(e)) is { } c && c.MeshRef == meshIndex + 1) { placement = c; break; }
        if (placement is null) { Console.WriteLine("  no component places this mesh"); return 1; }
        if (placement.Translation != Vector3.Zero || placement.Rotation != Vector3.Zero || placement.Scale.X != placement.Scale.Y || placement.Scale.Y != placement.Scale.Z)
        { Console.WriteLine($"  the component isn't at the origin with a uniform scale ({placement}); not supported"); return 1; }
        float scale = placement.Scale.X;

        var original = StaticMesh.Read(pkg, pkg.Exports[meshIndex]);
        // Placeholders added before (a second section using a copy of the sky material): replace just that section
        // and reuse its material, so later edits to this package (e.g. fog) are kept.
        int existingMic = -1;
        if (original.Sections.Length == 2 && original.Sections[1].MaterialRef > 0
            && pkg.Exports[original.Sections[1].MaterialRef - 1].ObjectName.StartsWith(pkg.Exports[micIndex].ObjectName + "_", StringComparison.OrdinalIgnoreCase))
        {
            existingMic = original.Sections[1].MaterialRef - 1;
            original = SkyOnly(original);
            Console.WriteLine($"  placeholders already present: replacing them, keeping material '{pkg.Exports[existingMic].ObjectName}' (its grey stays as it is)");
        }
        else if (original.Sections.Length != 1) { Console.WriteLine($"  the mesh has {original.Sections.Length} sections; expected the sky sphere (1) or sky + placeholders (2)."); return 1; }

        // Placeholder geometry: every FBX triangle, world -> the sky mesh's local space.
        var sections = FbxMeshReader.Read(fbxPath, 1);
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var idx = new List<int>();
        foreach (var s in sections)
        {
            int b = pos.Count;
            pos.AddRange(s.Positions.Select(p => p / scale));
            nrm.AddRange(s.Normals);
            idx.AddRange(s.Indices.Select(i => i + b));
        }
        if (idx.Count == 0) { Console.WriteLine("  the FBX has no triangles"); return 1; }
        if (excludeBoxes.Count > 0) RemoveIslands(pos, nrm, idx, scale, excludeBoxes);
        if (shrink != 1f) ShrinkIslands(pos, idx, shrink);
        foreach (string extra in addFbx ?? [])
        {
            // Extra placeholder FBX files, merged as they are (not shrunk): e.g. freshly generated pieces that replace excluded ones.
            int before = idx.Count;
            foreach (var s in FbxMeshReader.Read(extra, 1))
            {
                int b = pos.Count;
                pos.AddRange(s.Positions.Select(p => p / scale));
                nrm.AddRange(s.Normals);
                idx.AddRange(s.Indices.Select(i => i + b));
            }
            Console.WriteLine($"  added {(idx.Count - before) / 3:N0} tris from {Path.GetFileName(extra)}");
        }
        if (groundZ is float gz) AddGround(pos, nrm, idx, scale, gz, groundMargin);
        var worldPos = pos.Select(p => p * scale).ToList();
        Vector3 wmin = worldPos.Aggregate(Vector3.Min), wmax = worldPos.Aggregate(Vector3.Max);
        Console.WriteLine($"  placeholders: {pos.Count:N0} verts, {idx.Count / 3:N0} tris, world ({wmin.X:0}, {wmin.Y:0}, {wmin.Z:0})..({wmax.X:0}, {wmax.Y:0}, {wmax.Z:0}); sky component scale {scale} -> mesh space /{scale}");

        // Material: the one added before, or a new flat-grey copy of the sky's material instance.
        var add = new List<NewExport>();
        int newIndex;
        string newName;
        if (existingMic >= 0) { newIndex = existingMic; newName = pkg.Exports[existingMic].ObjectName; }
        else
        {
            int number = NextNameNumber(pkg, micIndex);
            newIndex = pkg.Exports.Length;                                 // 0-based index of the added export
            byte[] micBytes = BuildFlatMic(pkg, micIndex, gray);
            newName = number > 0 ? $"{pkg.Exports[micIndex].ObjectName}_{number - 1}" : pkg.Exports[micIndex].ObjectName;
            Console.WriteLine($"  new material: {pkg.PathOf(pkg.Exports[micIndex])} copied as '{newName}' (export #{newIndex + 1}), grey {gray:0.###}");
            add.Add(new NewExport(micIndex, number, _ => micBytes));
        }

        var built = StaticMeshBuilder.AddSection(original, pos, nrm, idx, newIndex + 1, newName);
        var replace = new Dictionary<int, Func<long, byte[]>> { [meshIndex] = off => StaticMeshBuilder.Serialize(original, built, off) };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, add, out var written);

        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), add, written);
        problems.AddRange(CheckResult(output, meshIndex, newIndex, original, built, gray));
        Console.WriteLine($"  sky mesh: {original.Positions.Length:N0} -> {built.Positions.Length:N0} verts, sections: sky (unchanged) + placeholders ({idx.Count / 3:N0} tris)");
        Console.WriteLine($"  package: {pkg.RawFile.Length:N0} -> {output.Length:N0} bytes, {pkg.Exports.Length} -> {pkg.Exports.Length + add.Count} exports");
        if (problems.Count > 0) { Console.WriteLine($"  verify: FAIL"); problems.ForEach(p => Console.WriteLine($"    - {p}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine("  verify: PASS (names/imports identical, every other export byte-identical, sky section unchanged, new material and section read back as built)");

        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(upkPath));
            File.WriteAllBytes(target, output);
            Console.WriteLine($"  dry run: wrote {target} (game folder untouched)");
            return 0;
        }
        return MeshImport.WriteLive(upkPath, output, onDisk =>
        {
            var p = PackageRebuilder.Verify(pkg, onDisk, replace.Keys.ToList(), add, written);
            p.AddRange(CheckResult(onDisk, meshIndex, newIndex, original, built, gray));
            return p;
        }) ? 0 : 1;
    }

    /// <summary>The sky sphere part of a mesh that already has placeholders: section 0 and its vertices/triangles (added first).</summary>
    static StaticMesh SkyOnly(StaticMesh m)
    {
        var sky = m.Sections[0];
        if (sky.FirstIndex != 0 || sky.MinVertexIndex != 0 || m.Sections[1].MinVertexIndex != sky.MaxVertexIndex + 1)
            throw new InvalidDataException("sky section isn't the first block of vertices/triangles; can't separate it from the placeholders");
        int nv = sky.MaxVertexIndex + 1, ni = sky.NumTriangles * 3;
        return new StaticMesh
        {
            Name = m.Name, InternalVersion = m.InternalVersion, LodCount = m.LodCount, NumTexCoords = m.NumTexCoords,
            Positions = m.Positions[..nv], Normals = m.Normals[..nv], TexCoords = m.TexCoords.Select(c => c[..nv]).ToArray(),
            Indices = m.Indices[..ni], Sections = [sky], Notes = m.Notes, Facts = m.Facts,
            TangentX = m.TangentX[..nv], TangentZ = m.TangentZ[..nv], ColorStride = m.ColorStride, Layout = m.Layout,
            FullPrecisionUVs = m.FullPrecisionUVs, HasVertexColors = m.HasVertexColors, Adjacency = m.Adjacency,
            KdopTriangleCount = m.KdopTriangleCount, BoundsOrigin = m.BoundsOrigin, BoundsExtent = m.BoundsExtent, BoundsRadius = m.BoundsRadius,
        };
    }

    /// <summary>
    /// Drops every connected piece (triangles joined through shared positions) whose centre, in world XY, falls
    /// inside one of the boxes (minX, minY, maxX, maxY). Whole pieces, so merged buildings aren't cut in half.
    /// </summary>
    /// <summary>Connected piece of every vertex: triangles joined through shared corners or identical positions.</summary>
    static Func<int, int> Islands(List<Vector3> pos, List<int> idx)
    {
        var byPos = new Dictionary<Vector3, int>();
        var parent = new int[pos.Count];
        for (int i = 0; i < pos.Count; i++) parent[i] = i;
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Join(int a, int b) { a = Root(a); b = Root(b); if (a != b) parent[a] = b; }
        for (int i = 0; i < pos.Count; i++) { if (byPos.TryGetValue(pos[i], out int j)) Join(i, j); else byPos[pos[i]] = i; }
        for (int t = 0; t + 2 < idx.Count; t += 3) { Join(idx[t], idx[t + 1]); Join(idx[t], idx[t + 2]); }
        return Root;
    }

    /// <summary>
    /// Scales each connected piece in place: sideways toward the piece's centre, and its height down from its
    /// base. 0.8/0.9 turns 90% placeholders into 80% ones without regenerating (hand merges are kept).
    /// </summary>
    static void ShrinkIslands(List<Vector3> pos, List<int> idx, float factor)
    {
        var root = Islands(pos, idx);
        var stats = new Dictionary<int, (Vector2 Sum, int Count, float MinZ)>();
        for (int i = 0; i < pos.Count; i++)
        {
            int r = root(i);
            var v = stats.TryGetValue(r, out var x) ? x : (Vector2.Zero, 0, float.MaxValue);
            stats[r] = (v.Item1 + new Vector2(pos[i].X, pos[i].Y), v.Item2 + 1, MathF.Min(v.Item3, pos[i].Z));
        }
        for (int i = 0; i < pos.Count; i++)
        {
            var st = stats[root(i)];
            Vector2 c = st.Sum / st.Count;
            Vector3 p = pos[i];
            pos[i] = new Vector3(c.X + (p.X - c.X) * factor, c.Y + (p.Y - c.Y) * factor, st.MinZ + (p.Z - st.MinZ) * factor);
        }
        Console.WriteLine($"  shrunk: {stats.Count} piece(s) by {factor:0.###} (sideways toward each centre, height from each base)");
    }

    static void RemoveIslands(List<Vector3> pos, List<Vector3> nrm, List<int> idx, float scale, IReadOnlyList<float[]> boxes)
    {
        var Root = Islands(pos, idx);

        var sum = new Dictionary<int, (Vector3 Sum, int Count)>();
        for (int i = 0; i < pos.Count; i++) { int r = Root(i); var v = sum.GetValueOrDefault(r); sum[r] = (v.Sum + pos[i] * scale, v.Count + 1); }
        var drop = sum.Where(kv =>
        {
            Vector3 c = kv.Value.Sum / kv.Value.Count;
            return boxes.Any(b => c.X >= b[0] && c.Y >= b[1] && c.X <= b[2] && c.Y <= b[3]);
        }).Select(kv => kv.Key).ToHashSet();

        var keptIdx = new List<int>();
        for (int t = 0; t + 2 < idx.Count; t += 3)
            if (!drop.Contains(Root(idx[t]))) keptIdx.AddRange([idx[t], idx[t + 1], idx[t + 2]]);
        // Compact the vertex list to what's still used.
        var remap = new Dictionary<int, int>();
        var newPos = new List<Vector3>(); var newNrm = new List<Vector3>();
        foreach (int i in keptIdx) if (!remap.ContainsKey(i)) { remap[i] = newPos.Count; newPos.Add(pos[i]); newNrm.Add(nrm[i]); }
        Console.WriteLine($"  excluded: {drop.Count} piece(s), {(idx.Count - keptIdx.Count) / 3:N0} tris inside {string.Join(" ", boxes.Select(b => $"[{b[0]:0},{b[1]:0}..{b[2]:0},{b[3]:0}]"))}");
        var newIdx = keptIdx.Select(i => remap[i]).ToList();
        pos.Clear(); pos.AddRange(newPos); nrm.Clear(); nrm.AddRange(newNrm); idx.Clear(); idx.AddRange(newIdx);
    }

    /// <summary>A flat ground quad at world height z under everything, margin past the placeholders on every side.</summary>
    static void AddGround(List<Vector3> pos, List<Vector3> nrm, List<int> idx, float scale, float z, float margin)
    {
        Vector3 min = pos.Aggregate(Vector3.Min) * scale, max = pos.Aggregate(Vector3.Max) * scale;
        float x0 = min.X - margin, y0 = min.Y - margin, x1 = max.X + margin, y1 = max.Y + margin;
        int b = pos.Count;
        foreach (var v in new[] { new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z) })
        { pos.Add(v / scale); nrm.Add(Vector3.UnitZ); }
        // Engine winding: cross(v1-v0, v2-v0) points against the normal (checked on stock meshes).
        foreach (var (i0, i1, i2) in new[] { (0, 1, 2), (0, 2, 3) })
        {
            Vector3 c = Vector3.Cross(pos[b + i1] - pos[b + i0], pos[b + i2] - pos[b + i0]);
            if (c.Z > 0) idx.AddRange([b + i0, b + i2, b + i1]); else idx.AddRange([b + i0, b + i1, b + i2]);
        }
        Console.WriteLine($"  ground plane: z = {z}, x {x0:0}..{x1:0}, y {y0:0}..{y1:0}");
    }

    /// <summary>Reads the rebuilt package back: sky section and geometry unchanged, placeholder section present, new material's values.</summary>
    static List<string> CheckResult(byte[] bytes, int meshIndex, int newIndex, StaticMesh original, BuiltMesh built, float gray)
    {
        var problems = new List<string>();
        var w = Package.FromBytes(bytes);
        var e = w.Exports[meshIndex];
        var m = StaticMesh.Parse(w, e.ObjectName, w.ReadExportBytes(e), e.SerialOffset);
        if (m.Sections.Length != 2) problems.Add($"mesh has {m.Sections.Length} sections");
        else
        {
            if (m.Sections[0] with { MaterialName = "" } != original.Sections[0] with { MaterialName = "" }) problems.Add("sky section changed");
            if (m.Sections[1].MaterialRef != newIndex + 1) problems.Add("placeholder section doesn't use the new material");
        }
        int n = original.Positions.Length;
        if (!m.Positions.AsSpan(0, n).SequenceEqual(original.Positions)) problems.Add("sky vertices changed");
        if (!m.Indices.AsSpan(0, original.Indices.Length).SequenceEqual(original.Indices)) problems.Add("sky triangles changed");
        if (!m.Positions.SequenceEqual(built.Positions) || !m.Indices.SequenceEqual(built.Indices)) problems.Add("mesh doesn't read back as built");
        if (m.Notes.Any(x => x.Contains("bulk-data offset"))) problems.Add("mesh bulk-data offset doesn't point at itself");
        var props = PropertyEdit.ReadProperties(w, newIndex);
        if (props == null) problems.Add("new material's properties don't parse");
        return problems;
    }

    static int Find(Package pkg, string name, string cls)
    {
        var hits = Enumerable.Range(0, pkg.Exports.Length).Where(i => pkg.ClassOf(pkg.Exports[i]).Equals(cls, StringComparison.OrdinalIgnoreCase)
            && (pkg.Exports[i].ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(pkg.Exports[i]).Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
        return hits.Count == 1 ? hits[0] : -1;
    }

    /// <summary>A name instance number no export with the same outer and name uses yet (stored number = displayed _N + 1).</summary>
    static int NextNameNumber(Package pkg, int template)
    {
        var t = pkg.Exports[template];
        byte[] body = pkg.Body;
        int nameIndex = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pkg.ExportEntryStart[template] + 12));
        var used = new HashSet<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            int at = pkg.ExportEntryStart[i];
            if (pkg.Exports[i].OuterIndex == t.OuterIndex && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at + 12)) == nameIndex)
                used.Add(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at + 16)));
        }
        int n = 1;
        while (used.Contains(n)) n++;
        return n;
    }

    // ------------------------------------------------------------------ flat material

    /// <summary>
    /// A copy of the sky material instance with its scalar and vector parameter arrays replaced: horizon = zenith =
    /// grey (no gradient), sun / rim / star colours 0, clouds and rim off. Every other byte (texture parameters,
    /// Parent, static-permutation flag, native data) is copied. Parameter GUIDs come from the master's expressions.
    /// </summary>
    static byte[] BuildFlatMic(Package pkg, int micIndex, float gray)
    {
        byte[] src = pkg.ReadExportBytes(pkg.Exports[micIndex]);
        var tags = TagWalker.Walk(pkg, src, 4) ?? throw new InvalidDataException("sky material's properties don't parse");
        int parent = tags.FirstOrDefault(t => t.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase)) is { } pt ? BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(pt.ValueAt)) : 0;
        if (parent <= 0) throw new InvalidDataException("sky material's parent isn't in this package");

        // Parameter name -> ExpressionGUID from the master material's expressions.
        var guids = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (e.OuterIndex != parent || !pkg.ClassOf(e).StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = pkg.ReadExportBytes(e);
            var et = TagWalker.Walk(pkg, d, 4);
            if (et == null) continue;
            var pn = et.FirstOrDefault(t => t.Name.Equals("ParameterName", StringComparison.OrdinalIgnoreCase));
            var eg = et.FirstOrDefault(t => t.Name.Equals("ExpressionGUID", StringComparison.OrdinalIgnoreCase));
            if (pn != null && eg != null && eg.Size == 16) guids[TagWalker.NameAt(pkg, d, pn.ValueAt)] = d.AsSpan(eg.ValueAt, 16).ToArray();
        }

        var scalars = new List<(string, float)> { ("speed", 0), ("cloudbrightness", 0), ("clouddarkness", 0), ("cloudopacity", 0), ("rimbrightness", 0), ("skybrightness", 1) };
        var vectors = new List<(string, Vector4)>
        {
            ("horizoncolor", new(gray, gray, gray, 1)), ("zenithcolor", new(gray, gray, gray, 1)),
            ("sun", new(0, 0, 0, 1)), ("rimcolor", new(0, 0, 0, 1)), ("starcolor", new(0, 0, 0, 1)),
        };
        scalars.RemoveAll(s => !guids.ContainsKey(s.Item1));
        vectors.RemoveAll(v => !guids.ContainsKey(v.Item1));

        var w = new TagWriter(pkg);
        using var ms = new MemoryStream();
        ms.Write(src, 0, 4);                                                  // NetIndex
        foreach (var t in tags)
        {
            if (t.Name.Equals("ScalarParameterValues", StringComparison.OrdinalIgnoreCase))
                ms.Write(w.StructArray("ScalarParameterValues", scalars.Select(s => w.Element(s.Item1, "FloatProperty", null, BitConverter.GetBytes(s.Item2), guids[s.Item1]))));
            else if (t.Name.Equals("VectorParameterValues", StringComparison.OrdinalIgnoreCase))
                ms.Write(w.StructArray("VectorParameterValues", vectors.Select(v => w.Element(v.Item1, "StructProperty", "LinearColor", Floats(v.Item2), guids[v.Item1]))));
            else ms.Write(src, t.Start, t.End - t.Start);                   // everything else as it was
        }
        ms.Write(src, tags.NoneAt, src.Length - tags.NoneAt);                // "None" and the native data
        return ms.ToArray();
    }

    static byte[] Floats(Vector4 v)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), v.Z); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), v.W);
        return b;
    }

    /// <summary>--test-rebuild: rebuild a package with nothing changed (and optionally one copied export) and verify. Writes nothing.</summary>
    public static int TestRebuild(string upkPath, string? cloneName)
    {
        var pkg = Package.Open(upkPath);
        var add = new List<NewExport>();
        if (cloneName != null)
        {
            int i = Enumerable.Range(0, pkg.Exports.Length).FirstOrDefault(k => pkg.Exports[k].ObjectName.Equals(cloneName, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(pkg.Exports[k]).Equals(cloneName, StringComparison.OrdinalIgnoreCase), -1);
            if (i < 0) { Console.WriteLine($"no export {cloneName}"); return 1; }
            byte[] data = pkg.ReadExportBytes(pkg.Exports[i]);
            add.Add(new NewExport(i, NextNameNumber(pkg, i), _ => data));
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), add, out var written);
        var problems = PackageRebuilder.Verify(pkg, output, Array.Empty<int>(), add, written);
        Console.WriteLine($"{Path.GetFileName(upkPath)}: {pkg.RawFile.Length:N0} -> {output.Length:N0} bytes, exports {pkg.Exports.Length} -> {pkg.Exports.Length + add.Count}: {(problems.Count == 0 ? "PASS" : "FAIL")}");
        problems.ForEach(p => Console.WriteLine($"  - {p}"));
        if (add.Count > 0) { var w = Package.FromBytes(output); Console.WriteLine($"  added: #{w.Exports.Length} {w.ClassOf(w.Exports[^1])} {w.PathOf(w.Exports[^1])}"); }
        return problems.Count == 0 ? 0 : 1;
    }
}

/// <summary>Top-level tagged properties of export bytes, with their byte ranges.</summary>
sealed class TagWalker : List<TagWalker.Tag>
{
    public sealed record Tag(string Name, string Type, int Start, int ValueAt, int Size, int End);
    public int NoneAt { get; private set; }

    public static TagWalker? Walk(Package pkg, byte[] d, int start)
    {
        var list = new TagWalker();
        int p = start;
        try
        {
            for (int guard = 0; guard < 4096; guard++)
            {
                int tagStart = p;
                string name = Name(pkg, d, ref p);
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { list.NoneAt = tagStart; return list; }
                string type = Name(pkg, d, ref p);
                int size = BitConverter.ToInt32(d, p); p += 8;
                if (type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) || type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase)) Name(pkg, d, ref p);
                if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase)) p += 1;
                if (size < 0 || p + size > d.Length) return null;
                list.Add(new Tag(name, type, tagStart, p, size, p + size));
                p += size;
            }
        }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { }
        return null;
    }

    public static string NameAt(Package pkg, byte[] d, int at) { int p = at; return Name(pkg, d, ref p); }

    static string Name(Package pkg, byte[] d, ref int p)
    {
        if (p + 8 > d.Length) throw new PackageFormatException("past end");
        int idx = BitConverter.ToInt32(d, p), num = BitConverter.ToInt32(d, p + 4); p += 8;
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException("bad name");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }
}

/// <summary>Writes tagged properties using names that already exist in the package's name table.</summary>
sealed class TagWriter(Package pkg)
{
    byte[] NameRef(string name)
    {
        int i = Array.FindIndex(pkg.Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new InvalidDataException($"name '{name}' isn't in the package's name table");
        var b = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(b, i);
        return b;
    }

    public byte[] Tag(string name, string type, string? structName, byte[] value)
    {
        using var ms = new MemoryStream();
        ms.Write(NameRef(name)); ms.Write(NameRef(type));
        ms.Write(BitConverter.GetBytes(value.Length)); ms.Write(BitConverter.GetBytes(0));
        if (structName != null) ms.Write(NameRef(structName));
        ms.Write(value);
        return ms.ToArray();
    }

    /// <summary>One element of ScalarParameterValues / VectorParameterValues: ParameterName, ParameterValue, ExpressionGUID, None.</summary>
    public byte[] Element(string parameter, string valueType, string? valueStruct, byte[] value, byte[] guid)
    {
        using var ms = new MemoryStream();
        ms.Write(Tag("ParameterName", "NameProperty", null, NameRef(parameter)));
        ms.Write(Tag("ParameterValue", valueType, valueStruct, value));
        ms.Write(Tag("ExpressionGUID", "StructProperty", "Guid", guid));
        ms.Write(NameRef("None"));
        return ms.ToArray();
    }

    public byte[] StructArray(string name, IEnumerable<byte[]> elements)
    {
        var list = elements.ToList();
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(list.Count));
        foreach (var e in list) ms.Write(e);
        return Tag(name, "ArrayProperty", null, ms.ToArray());
    }
}
