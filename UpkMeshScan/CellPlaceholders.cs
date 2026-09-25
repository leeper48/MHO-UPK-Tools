using System.Buffers.Binary;
using System.Numerics;

namespace UpkMeshScan;

/// <summary>
/// --add-cell-placeholders ("option A"): placeholder buildings as one placed object per map cell, each with
/// MinDrawDistance, so cells near the camera (loaded, where the game fades real buildings) show none.
///
/// Built from the package's .bak (the stock original) every time, so it can be re-run with other settings:
/// every export the live file has changed relative to the .bak (e.g. fog edits) is carried over as is; exports
/// this command manages (the sky mesh and the collection actor) are rebuilt, and anything appended by earlier
/// runs is dropped and made again. Added, all at the end of the export table:
///   - a flat-grey copy of the sky material instance (as --add-sky-placeholders makes);
///   - per cell: a StaticMesh (copy of the sky sphere's entry, geometry in world units, no BodySetup/collision)
///     and a StaticMeshComponent (copy of the sky's component: new mesh, grey material, no Scale3D, MinDrawDistance),
///     appended to the StaticMeshCollectionActor's StaticMeshComponents list (the cooked level lists only actors,
///     and this actor stores nothing but that list and a tag — checked on MidTown_Static);
///   - the name MinDrawDistance if the package lacks it (the engine defines PrimitiveComponent.MinDrawDistance).
/// The sky sphere keeps its own section plus, optionally, a ground plane section (under the real ground, so it
/// never needs to fade).
/// </summary>
static class CellPlaceholders
{
    public static int Run(string upkPath, string fbxPath, float minDrawDistance, float cellSize, float gray, bool dryRun,
        IReadOnlyList<float[]> excludeBoxes, float? groundZ, float groundMargin, float shrink, IReadOnlyList<string> addFbx,
        string meshName = "sm_skysphere", string micName = "m_procedural_sky_daytime")
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        string bakPath = upkPath + ".bak";
        var live = Package.Open(upkPath);
        var bak = File.Exists(bakPath) ? Package.Open(bakPath) : live;
        Console.WriteLine($"Cell placeholders: {Path.GetFileName(fbxPath)} -> {Path.GetFileName(upkPath)} (built from {(File.Exists(bakPath) ? "its .bak" : "itself, no .bak yet")}), MinDrawDistance {minDrawDistance}{(dryRun ? "  [dry run]" : "")}");

        // The .bak's exports must be the live file's first exports (this tool only ever appends).
        int n0 = bak.Exports.Length;
        if (live.Exports.Length < n0 || Enumerable.Range(0, n0).Any(i => live.Exports[i] with { SerialSize = 0, SerialOffset = 0 } != bak.Exports[i] with { SerialSize = 0, SerialOffset = 0 }))
        { Console.WriteLine("  the live package's exports don't start with the .bak's; not safe to rebuild from the .bak"); return 1; }

        int skyMesh = Find(bak, meshName, "StaticMesh"), skyMic = Find(bak, micName, "MaterialInstanceConstant");
        int skyComp = -1;
        ComponentTransform? placement = null;
        for (int i = 0; i < n0 && skyMesh >= 0; i++)
            if (bak.ClassOf(bak.Exports[i]).Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)
                && ComponentTransform.Read(bak, bak.ReadExportBytes(bak.Exports[i])) is { } c && c.MeshRef == skyMesh + 1) { skyComp = i; placement = c; break; }
        if (skyMesh < 0 || skyMic < 0 || skyComp < 0) { Console.WriteLine("  sky sphere mesh / material / component not found"); return 1; }
        int actor = bak.Exports[skyComp].OuterIndex - 1;
        if (actor < 0 || !bak.ClassOf(bak.Exports[actor]).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("  the sky component isn't in a StaticMeshCollectionActor"); return 1; }
        float skyScale = placement!.Scale.X;

        // Carry over everything the live file changed (fog, lights…), except what this command rebuilds.
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var carried = new List<string>();
        for (int i = 0; i < n0; i++)
        {
            if (i == skyMesh || i == actor) continue;
            byte[] l = live.ReadExportBytes(live.Exports[i]);
            if (l.AsSpan().SequenceEqual(bak.ReadExportBytes(bak.Exports[i]))) continue;
            replace[i] = _ => l;
            carried.Add(bak.Exports[i].ObjectName);
        }
        if (live.Exports.Length > n0) Console.WriteLine($"  dropping {live.Exports.Length - n0} export(s) added by earlier runs (made again below)");
        Console.WriteLine($"  carried over from the live file: {(carried.Count == 0 ? "nothing" : string.Join(", ", carried))}");

        // Placeholder geometry in world units.
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var idx = new List<int>();
        void Load(string path)
        {
            foreach (var s in FbxMeshReader.Read(path, 1))
            {
                int b = pos.Count;
                pos.AddRange(s.Positions); nrm.AddRange(s.Normals); idx.AddRange(s.Indices.Select(i => i + b));
            }
        }
        Load(fbxPath);
        if (excludeBoxes.Count > 0) SkyPlaceholders.RemoveIslandsPublic(pos, nrm, idx, excludeBoxes);
        if (shrink != 1f) SkyPlaceholders.ShrinkIslandsPublic(pos, idx, shrink);
        foreach (string extra in addFbx) { int before = idx.Count; Load(extra); Console.WriteLine($"  added {(idx.Count - before) / 3:N0} tris from {Path.GetFileName(extra)}"); }

        // One group per map cell (by each connected piece's centre).
        var root = SkyPlaceholders.IslandsPublic(pos, idx);
        var centre = new Dictionary<int, (Vector2 Sum, int Count)>();
        for (int i = 0; i < pos.Count; i++) { int r = root(i); var v = centre.GetValueOrDefault(r); centre[r] = (v.Sum + new Vector2(pos[i].X, pos[i].Y), v.Count + 1); }
        var cellOf = centre.ToDictionary(kv => kv.Key, kv => { Vector2 c = kv.Value.Sum / kv.Value.Count; return ((int)MathF.Round(c.X / cellSize), (int)MathF.Round(c.Y / cellSize)); });
        var cells = new SortedDictionary<(int, int), (List<Vector3> P, List<Vector3> N, List<int> I, Dictionary<int, int> Map)>();
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            var key = cellOf[root(idx[t])];
            if (!cells.TryGetValue(key, out var g)) cells[key] = g = (new(), new(), new(), new());
            for (int k = 0; k < 3; k++)
            {
                int v = idx[t + k];
                if (!g.Map.TryGetValue(v, out int nv)) { nv = g.P.Count; g.Map[v] = nv; g.P.Add(pos[v]); g.N.Add(nrm[v]); }
                g.I.Add(nv);
            }
        }
        Console.WriteLine($"  {idx.Count / 3:N0} placeholder tris in {centre.Count} piece(s) -> {cells.Count} cell object(s) ({cellSize}-unit cells): " +
            string.Join(" ", cells.Select(c => $"X{c.Key.Item1}Y{c.Key.Item2}:{c.Value.I.Count / 3}")));

        // New exports (appended after the .bak's): grey material, then mesh + component per cell.
        var names = bak.Names.Any(n => n.Equals("MinDrawDistance", StringComparison.OrdinalIgnoreCase)) ? new List<string>() : new List<string> { "MinDrawDistance" };
        var tw = new TagWriter(bak, names);
        var add = new List<NewExport>();
        int micRef = n0 + 1;
        byte[] micBytes = SkyPlaceholders.BuildFlatMicPublic(bak, skyMic, gray);
        add.Add(new NewExport(skyMic, NextNumber(bak, skyMic, 0), _ => micBytes));
        var skyTemplate = StaticMesh.Read(bak, bak.Exports[skyMesh]);
        if (skyTemplate.Sections.Length != 1) { Console.WriteLine("  the .bak's sky sphere doesn't have exactly one section"); return 1; }
        var compRefs = new List<int>();
        var cellMeshes = new List<(string Cell, int MeshRef, BuiltMesh Mesh)>();
        int k2 = 0;
        foreach (var (key, g) in cells)
        {
            var mesh = StaticMeshBuilder.BuildGeometry(skyTemplate, g.P, g.N, g.I, micRef, "placeholder");
            int meshRef = n0 + add.Count + 1;
            add.Add(new NewExport(skyMesh, NextNumber(bak, skyMesh, k2), off => StaticMeshBuilder.Serialize(skyTemplate, mesh, off, dropBodySetup: true)));
            int compRef = n0 + add.Count + 1;
            byte[] comp = BuildComponent(bak, skyComp, tw, meshRef, micRef, minDrawDistance);
            add.Add(new NewExport(skyComp, NextNumber(bak, skyComp, k2), _ => comp));
            compRefs.Add(compRef);
            cellMeshes.Add(($"X{key.Item1}Y{key.Item2}", meshRef, mesh));
            k2++;
        }

        // Collection actor: the sky component plus the new ones.
        byte[] actorBytes = BuildActor(bak, actor, tw, compRefs);
        replace[actor] = _ => actorBytes;

        // Sky sphere: its own section, plus the ground plane if asked for.
        BuiltMesh? skyBuilt = null;
        if (groundZ is float gz)
        {
            Vector3 min = pos.Aggregate(Vector3.Min), max = pos.Aggregate(Vector3.Max);
            float x0 = min.X - groundMargin, y0 = min.Y - groundMargin, x1 = max.X + groundMargin, y1 = max.Y + groundMargin;
            var gp = new[] { new Vector3(x0, y0, gz), new Vector3(x1, y0, gz), new Vector3(x1, y1, gz), new Vector3(x0, y1, gz) }.Select(v => v / skyScale).ToList();
            var gi = new List<int>();
            foreach (var (a, b, c) in new[] { (0, 1, 2), (0, 2, 3) })
                gi.AddRange(Vector3.Cross(gp[b] - gp[a], gp[c] - gp[a]).Z > 0 ? [a, c, b] : [a, b, c]);   // engine winding
            skyBuilt = StaticMeshBuilder.AddSection(skyTemplate, gp, Enumerable.Repeat(Vector3.UnitZ, 4).ToList(), gi, micRef, "placeholder");
            var sb = skyBuilt;
            replace[skyMesh] = off => StaticMeshBuilder.Serialize(skyTemplate, sb, off);
            Console.WriteLine($"  ground plane in the sky sphere: z = {gz}, x {x0:0}..{x1:0}, y {y0:0}..{y1:0}");
        }

        byte[] output = PackageRebuilder.Rebuild(bak, replace, add, out var written, names);
        var problems = PackageRebuilder.Verify(bak, output, replace.Keys.ToList(), add, written, names);
        problems.AddRange(Check(output, n0, actor, compRefs, cellMeshes, minDrawDistance, skyMesh, skyTemplate, skyBuilt));
        Console.WriteLine($"  package: {live.RawFile.Length:N0} -> {output.Length:N0} bytes; exports {n0} (.bak) + {add.Count} = {n0 + add.Count}{(names.Count > 0 ? "; name added: MinDrawDistance" : "")}");
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(p => Console.WriteLine($"    - {p}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS (tables = .bak's + additions, untouched exports byte-identical to the .bak, carried-over edits as in the live file, {cells.Count} cell meshes/components read back as built)");

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
            var p = PackageRebuilder.Verify(bak, onDisk, replace.Keys.ToList(), add, written, names);
            p.AddRange(Check(onDisk, n0, actor, compRefs, cellMeshes, minDrawDistance, skyMesh, skyTemplate, skyBuilt));
            return p;
        }) ? 0 : 1;
    }

    /// <summary>Copy of the sky's component: new StaticMesh, Materials = [grey], Scale3D dropped (1), MinDrawDistance added.</summary>
    static byte[] BuildComponent(Package pkg, int template, TagWriter tw, int meshRef, int micRef, float minDraw)
    {
        byte[] src = pkg.ReadExportBytes(pkg.Exports[template]);
        var tags = TagWalker.Walk(pkg, src, 8) ?? throw new InvalidDataException("sky component's properties don't parse (expected MHO layout, properties from byte 8)");
        using var ms = new MemoryStream();
        ms.Write(src, 0, 8);                                                  // MHO component preamble: int32 + NetIndex
        foreach (var t in tags)
        {
            switch (t.Name.ToLowerInvariant())
            {
                case "staticmesh": ms.Write(tw.Tag("StaticMesh", "ObjectProperty", null, BitConverter.GetBytes(meshRef))); break;
                case "materials": ms.Write(tw.Tag("Materials", "ArrayProperty", null, [.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(micRef)])); break;
                case "scale3d": break;                                        // placeholders are in world units: scale 1 (default)
                case "mindrawdistance": break;                                // re-added below
                default: ms.Write(src, t.Start, t.End - t.Start); break;
            }
        }
        ms.Write(tw.Tag("MinDrawDistance", "FloatProperty", null, BitConverter.GetBytes(minDraw)));
        ms.Write(src, tags.NoneAt, src.Length - tags.NoneAt);                // None + native (an empty lighting record)
        return ms.ToArray();
    }

    /// <summary>The collection actor with its StaticMeshComponents list extended.</summary>
    static byte[] BuildActor(Package pkg, int actor, TagWriter tw, IReadOnlyList<int> newComponents)
    {
        byte[] src = pkg.ReadExportBytes(pkg.Exports[actor]);
        var tags = TagWalker.Walk(pkg, src, 4) ?? throw new InvalidDataException("collection actor's properties don't parse");
        var list = tags.FirstOrDefault(t => t.Name.Equals("StaticMeshComponents", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("collection actor has no StaticMeshComponents");
        int count = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(list.ValueAt));
        var refs = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(list.ValueAt + 4 + 4 * i))).Concat(newComponents).ToList();
        using var ms = new MemoryStream();
        ms.Write(src, 0, 4);
        foreach (var t in tags)
        {
            if (t == list) ms.Write(tw.Tag("StaticMeshComponents", "ArrayProperty", null, [.. BitConverter.GetBytes(refs.Count), .. refs.SelectMany(BitConverter.GetBytes)]));
            else ms.Write(src, t.Start, t.End - t.Start);
        }
        ms.Write(src, tags.NoneAt, src.Length - tags.NoneAt);
        return ms.ToArray();
    }

    static List<string> Check(byte[] bytes, int n0, int actor, List<int> compRefs, List<(string Cell, int MeshRef, BuiltMesh Mesh)> meshes, float minDraw,
        int skyMesh, StaticMesh skyTemplate, BuiltMesh? skyBuilt)
    {
        var problems = new List<string>();
        var w = Package.FromBytes(bytes);
        var actorTags = TagWalker.Walk(w, w.ReadExportBytes(w.Exports[actor]), 4);
        var list = actorTags?.FirstOrDefault(t => t.Name.Equals("StaticMeshComponents", StringComparison.OrdinalIgnoreCase));
        if (list == null) problems.Add("collection actor doesn't parse");
        else
        {
            byte[] a = w.ReadExportBytes(w.Exports[actor]);
            var refs = Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(list.ValueAt))).Select(i => BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(list.ValueAt + 4 + 4 * i))).ToList();
            if (!refs.Skip(refs.Count - compRefs.Count).SequenceEqual(compRefs)) problems.Add("collection actor doesn't list the new components");
        }
        for (int i = 0; i < compRefs.Count; i++)
        {
            var c = ComponentTransform.Read(w, w.ReadExportBytes(w.Exports[compRefs[i] - 1]));
            if (c == null || c.MeshRef != meshes[i].MeshRef || c.Scale != Vector3.One || c.Translation != Vector3.Zero) problems.Add($"component {meshes[i].Cell} doesn't read back right");
            var props = PropertyEdit.ReadProperties(w, compRefs[i] - 1);
            var md = props?.FirstOrDefault(p => p.Name.Equals("MinDrawDistance", StringComparison.OrdinalIgnoreCase));
            if (md == null || md.Value != minDraw.ToString(System.Globalization.CultureInfo.InvariantCulture)) problems.Add($"component {meshes[i].Cell}: MinDrawDistance missing or wrong");
            var e = w.Exports[meshes[i].MeshRef - 1];
            var m = StaticMesh.Parse(w, e.ObjectName, w.ReadExportBytes(e), e.SerialOffset);
            if (!m.Positions.SequenceEqual(meshes[i].Mesh.Positions) || !m.Indices.SequenceEqual(meshes[i].Mesh.Indices)) problems.Add($"mesh {meshes[i].Cell} doesn't read back as built");
        }
        var sky = w.Exports[skyMesh];
        var s = StaticMesh.Parse(w, sky.ObjectName, w.ReadExportBytes(sky), sky.SerialOffset);
        if (!s.Positions.AsSpan(0, skyTemplate.Positions.Length).SequenceEqual(skyTemplate.Positions) || s.Sections[0] with { MaterialName = "" } != skyTemplate.Sections[0] with { MaterialName = "" })
            problems.Add("sky sphere's own section changed");
        return problems;
    }

    static int Find(Package pkg, string name, string cls)
    {
        var hits = Enumerable.Range(0, pkg.Exports.Length).Where(i => pkg.ClassOf(pkg.Exports[i]).Equals(cls, StringComparison.OrdinalIgnoreCase)
            && (pkg.Exports[i].ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(pkg.Exports[i]).Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();
        return hits.Count == 1 ? hits[0] : -1;
    }

    /// <summary>The k-th name instance number (from 1) not used by any export with the template's outer and name.</summary>
    static int NextNumber(Package pkg, int template, int k)
    {
        byte[] body = pkg.Body;
        int at = pkg.ExportEntryStart[template];
        int nameIndex = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at + 12));
        var used = new HashSet<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (pkg.Exports[i].OuterIndex == pkg.Exports[template].OuterIndex && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pkg.ExportEntryStart[i] + 12)) == nameIndex)
                used.Add(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pkg.ExportEntryStart[i] + 16)));
        int n = 1, found = -1;
        while (found < k) { if (!used.Contains(n)) found++; if (found < k) n++; }
        return n;
    }
}
