using System.Buffers.Binary;
using System.Numerics;

namespace MhoPackageModifier;

/// <summary>
/// --add-cloud-dome: a second sky dome just inside the zone's own, for a cloud layer with its own tiling. The sky
/// material's cloud layer and its photo read the same UV0 (the Brood skydome material), so the clouds can't be made
/// bigger on the photo dome without blurring the photo. This adds a copy of the sky sphere's own section as a new
/// StaticMesh (positions x --shrink, so it sits inside; UV0 x --uv fu,fv) and a copy of the sky component that places
/// it with --material (e.g. the sky material copied with a black photo: clouds only, if the material adds its layers),
/// in the same collection actor. The copy carries an empty lighting record. Same .bak / verify / swap.
/// </summary>
static class CloudDome
{
    static readonly byte[] EmptyLighting = [1, 0, 0, 0, .. new byte[17]];

    public static int Run(string upkPath, string componentPath, string materialPath, Vector2 uvScale, float shrink, bool dryRun,
        float planarScale = 0f, float planarAngle = 0f, float horizonDeg = 3f, Vector2? horizonFade = null, int? sortPriority = null)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        Console.WriteLine($"Cloud dome: {componentPath} in {Path.GetFileName(upkPath)}, material {materialPath}, UV0 x({uvScale.X}, {uvScale.Y}), size x{shrink}{(dryRun ? "  [dry run]" : "")}");
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(componentPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0 || !pkg.ClassOf(pkg.Exports[comp]).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  no StaticMeshComponent with that path"); return 2; }
        int actor = pkg.Exports[comp].OuterIndex - 1;
        if (actor < 0 || !pkg.ClassOf(pkg.Exports[actor]).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  the component isn't in a StaticMeshCollectionActor"); return 1; }
        int mat = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(materialPath, StringComparison.OrdinalIgnoreCase)
            && pkg.ClassOf(e).StartsWith("Material", StringComparison.OrdinalIgnoreCase));
        if (mat < 0) { Console.WriteLine($"  no material '{materialPath}' among the package's exports"); return 2; }

        byte[] src = pkg.ReadExportBytes(pkg.Exports[comp]);
        var c0 = ComponentTransform.Read(pkg, src);
        if (c0 == null || c0.MeshRef <= 0) { Console.WriteLine("  the component doesn't place a mesh from this package"); return 1; }
        int meshIndex = c0.MeshRef - 1;
        var full = StaticMesh.Read(pkg, pkg.Exports[meshIndex]);
        var sky = full.Sections.Length > 1 ? SkyPlaceholders.SkyOnlyPublic(full) : full;   // the sky's own section only

        var uv = sky.TexCoords.Select(ch => ch.ToArray()).ToArray();
        if (planarScale > 0)
        {
            // --planar S[,angle]: UV0 as if the clouds were painted on a flat ceiling seen through the dome (direction d:
            // uv = rotate(d.xy / |d.z|) * S), so they shrink and flatten toward the horizon like a real cloud layer, with no
            // wrap seam. |d.z| is softened to sqrt(z^2 + sin(--horizon)^2), where the projection would run off to infinity (a
            // hard floor gave the 0 and 5.6 degree vertex rows the same UVs: vertical streaks, 2026-09-26). The lower
            // half mirrors the upper (it's below the ground). uvScale then scales the result (u, v).
            float zMin = MathF.Sin(horizonDeg * MathF.PI / 180f), ca = MathF.Cos(planarAngle * MathF.PI / 180f), sa = MathF.Sin(planarAngle * MathF.PI / 180f);
            for (int v = 0; v < uv[0].Length; v++)
            {
                var d = Vector3.Normalize(sky.Positions[v]);
                float z = MathF.Sqrt(d.Z * d.Z + zMin * zMin);                 // soft floor: rows near the horizon stay distinct
                var p = new Vector2(d.X / z, d.Y / z) * planarScale;
                uv[0][v] = new Vector2(p.X * ca - p.Y * sa, p.X * sa + p.Y * ca) * uvScale;
            }
            Console.WriteLine($"  planar UV0: scale {planarScale}, turned {planarAngle} degrees, held above {horizonDeg} degrees; u {uv[0].Min(t => t.X):0.#}..{uv[0].Max(t => t.X):0.#}, v {uv[0].Min(t => t.Y):0.#}..{uv[0].Max(t => t.Y):0.#}");
        }
        else
            for (int v = 0; v < uv[0].Length; v++) uv[0][v] *= uvScale;
        // --horizon-fade lo,hi: vertex alpha 0 at lo degrees above the horizon (and below), 255 at hi, smoothstep between;
        // colour white. Works if the material multiplies its opacity by vertex alpha (a translucent VFX dome usually does).
        byte[]? colors = null;
        if (horizonFade is { } hf)
        {
            colors = new byte[sky.Positions.Length * 4];
            for (int v = 0; v < sky.Positions.Length; v++)
            {
                var d = Vector3.Normalize(sky.Positions[v]);
                float el = MathF.Asin(Math.Clamp(d.Z, -1f, 1f)) * 180f / MathF.PI;
                float t = Math.Clamp((el - hf.X) / (hf.Y - hf.X), 0f, 1f); t = t * t * (3 - 2 * t);
                colors[v * 4] = colors[v * 4 + 1] = colors[v * 4 + 2] = 255; colors[v * 4 + 3] = (byte)MathF.Round(t * 255);
            }
            Console.WriteLine($"  horizon fade: vertex alpha 0 at {hf.X} degrees, 255 at {hf.Y} and up");
        }
        var built = new BuiltMesh
        {
            ColorsBgra = colors,
            Positions = sky.Positions.Select(p => p * shrink).ToArray(), Normals = sky.Normals, TexCoords = uv,
            TangentX = sky.TangentX, TangentZ = sky.TangentZ, Indices = sky.Indices, Adjacency = sky.Adjacency, Sections = sky.Sections,
            BoundsOrigin = sky.BoundsOrigin * shrink, BoundsExtent = sky.BoundsExtent * shrink, BoundsRadius = sky.BoundsRadius * shrink,
        };
        var add = new List<NewExport> { new(meshIndex, CellPlaceholders.NextNumber(pkg, meshIndex, 0), off => StaticMeshBuilder.Serialize(sky, built, off, dropBodySetup: true)) };
        int newMeshRef = pkg.Exports.Length + 1;

        // The component copy: StaticMesh -> the new mesh, Materials[0] -> the cloud material; everything else as the sky's.
        var tags = TagWalker.Walk(pkg, src, 8) ?? throw new InvalidDataException("component properties don't parse (MHO layout, from byte 8)");
        var meshTag = tags.First(t => t.Name.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
        var matsTag = tags.FirstOrDefault(t => t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
        if (matsTag == null || BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(matsTag.ValueAt)) < 1) { Console.WriteLine("  the component has no Materials entry to replace"); return 1; }
        // --sort-priority N: TranslucencySortPriority on the copy, so a translucent dome draws in a fixed order against other
        // translucent things at a similar distance (the water in the sky mesh flickered through the city dome's lower half).
        var addNames = new List<string>();
        foreach (string n in new[] { "TranslucencySortPriority", "IntProperty" })
            if (sortPriority != null && !pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) addNames.Add(n);
        var tw = new TagWriter(pkg, addNames);
        using var ms = new MemoryStream();
        ms.Write(src, 0, 8);
        foreach (var t in tags)
        {
            byte[] tag = src[t.Start..t.End];
            if (t == meshTag) BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(t.ValueAt - t.Start), newMeshRef);
            if (t == matsTag) BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(t.ValueAt - t.Start + 4), mat + 1);
            if (sortPriority != null && t.Name.Equals("TranslucencySortPriority", StringComparison.OrdinalIgnoreCase)) continue;
            ms.Write(tag);
        }
        if (sortPriority is int sp) ms.Write(tw.Tag("TranslucencySortPriority", "IntProperty", null, BitConverter.GetBytes(sp)));
        ms.Write(src, tags.NoneAt, 8);
        ms.Write(EmptyLighting);
        byte[] compBytes = ms.ToArray();
        int newCompRef = pkg.Exports.Length + 2;
        add.Add(new NewExport(comp, CellPlaceholders.NextNumber(pkg, comp, 0), _ => compBytes));

        byte[] actorBytes = CellPlaceholders.BuildActor(pkg, actor, tw, [newCompRef]);
        var replace = new Dictionary<int, Func<long, byte[]>> { [actor] = _ => actorBytes };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, add, out var written, addNames);

        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [actor], add, written, addNames);
            var w = Package.FromBytes(bytes);
            var m = StaticMesh.Parse(w, w.Exports[newMeshRef - 1].ObjectName, w.ReadExportBytes(w.Exports[newMeshRef - 1]), w.Exports[newMeshRef - 1].SerialOffset);
            if (m.Positions.Length != built.Positions.Length || m.Indices.Length != built.Indices.Length) problems.Add("cloud mesh doesn't read back with the sky section's geometry");
            else
                for (int v = 0; v < m.Positions.Length; v++)
                    if (Vector3.Distance(m.Positions[v], built.Positions[v]) > 0.01f || Vector2.Distance(m.TexCoords[0][v], built.TexCoords[0][v]) > 2e-3f + 2e-3f * built.TexCoords[0][v].Length())
                    { problems.Add($"cloud mesh vertex {v} reads back differently"); break; }
            if (colors != null && (m.ColorsBgra == null || !m.ColorsBgra.AsSpan().SequenceEqual(colors))) problems.Add("cloud mesh's vertex colours don't read back as written");
            byte[] cb = w.ReadExportBytes(w.Exports[newCompRef - 1]);
            var c = ComponentTransform.Read(w, cb);
            if (c == null || c.MeshRef != newMeshRef || c.Scale != c0.Scale || c.Translation != c0.Translation) problems.Add("cloud component doesn't place the new mesh like the sky");
            var mt = TagWalker.Walk(w, cb, 8)?.FirstOrDefault(t => t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
            if (mt == null || BinaryPrimitives.ReadInt32LittleEndian(cb.AsSpan(mt.ValueAt + 4)) != mat + 1) problems.Add("cloud component's material isn't the cloud material");
            if (sortPriority is int want)
            {
                var sp = TagWalker.Walk(w, cb, 8)?.FirstOrDefault(t => t.Name.Equals("TranslucencySortPriority", StringComparison.OrdinalIgnoreCase));
                if (sp == null || BinaryPrimitives.ReadInt32LittleEndian(cb.AsSpan(sp.ValueAt)) != want) problems.Add("cloud component's TranslucencySortPriority doesn't read back");
            }
            return problems;
        }
        var problems = Check(output);
        Console.WriteLine($"  new mesh #{newMeshRef} ({built.Positions.Length:N0} verts, {built.Indices.Length / 3:N0} tris), new component #{newCompRef} in {pkg.Exports[actor].ObjectName}");
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine("  verify: PASS (mesh = sky section scaled, UV0 scaled; component = the sky's with the new mesh and material; actor lists it; every other export identical)");
        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(upkPath)), output);
            return 0;
        }
        return MeshImport.WriteLive(upkPath, output, Check) ? 0 : 1;
    }
}
