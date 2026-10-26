using System.Buffers.Binary;
using AnimPackage = AnimExportCli.Packages.Package;
using AnimExportCli.Meshes;
using MpmPackage = MhoPackageModifier.Package;

namespace MhoExtendedModManager;

/// <summary>
/// A skeletal mesh's mirror table (SkelMirrorTable) made one entry per bone again (Kurt, 2026-10-12: added bones for a
/// loincloth, dress or wings left the table shorter than the skeleton, and mirroring an animation reads past its end: the
/// Array.h:575 crash; CrashCheck finds it). A stock entry is a tagged struct { SourceIndex: the bone whose animation this one
/// takes when mirrored (itself for a centre bone, its left / right partner for a paired one), BoneFlipAxis: axis_none in all
/// 161 of Rogue Modern's }. The table keeps the entries it has for the bones it has; an added bone gets its left / right
/// partner by name when it has one, else itself, with the first entry's flip axis; a table longer than the skeleton is cut,
/// and entries pointing past the skeleton point at themselves. Only the mesh's export changes (PackageWriter, verified).
/// </summary>
static class MirrorTables
{
    /// <summary>The package with every skeletal mesh's mirror table matching its bones, and what changed; null when nothing
    /// needed it (or the package has no mirror tables).</summary>
    public static (byte[] Bytes, List<string> Changes)? Fix(string path)
    {
        var ap = AnimPackage.Open(path);
        byte[] bytes = File.ReadAllBytes(path);
        var changes = new List<string>();
        foreach (int i in ap.FindExportsOfClass(SkeletalMeshReader.ClassName))
        {
            SkeletalMesh? sm;
            try { sm = SkeletalMeshReader.TryRead(ap, i); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { sm = null; }
            if (sm == null) continue;
            var mp = MpmPackage.FromBytes(bytes);
            if (i >= mp.Exports.Length || !mp.Exports[i].ObjectName.Equals(ap.GetExportName(i), StringComparison.OrdinalIgnoreCase)) continue;
            var t = new Fx.FxTables(mp);
            var props = Fx.FxProps.Find(mp.Body, t, t.Exports[i])?.Props;
            if (props?.FirstOrDefault(p => p.Name.Equals("SkelMirrorTable", StringComparison.OrdinalIgnoreCase)) is not { } mt) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(mp.Body.AsSpan(mt.ValueAt));
            var names = sm.Bones.Select(b => b.Name).ToList();
            int n = names.Count;
            if (count == n || count <= 0) continue;
            var elements = Elements(mp, mt.ValueAt + 4, mt.ValueAt + mt.Size, count);
            if (elements == null) { changes.Add($"{ap.GetExportName(i)}: its mirror table couldn't be read, left as it is"); continue; }
            var (template, srcAt) = elements[0];
            var fresh = new List<byte[]>();
            for (int b = 0; b < n; b++)
            {
                byte[] e = b < elements.Count ? (byte[])elements[b].Bytes.Clone() : (byte[])template.Clone();
                int at = b < elements.Count ? elements[b].SourceAt : srcAt;
                int src = BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(at));
                if (b >= elements.Count) src = Partner(names, b);   // an added bone: its partner, else itself
                if (src < 0 || src >= n) src = b;                    // (past the skeleton: itself)
                BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(at), src);
                fresh.Add(e);
            }
            // the property again: its tag (the size field updated), the count, the entries
            int valueSize = 4 + fresh.Sum(e => e.Length);
            var prop = new List<byte>();
            byte[] tag = mp.Body.AsSpan(mt.TagAt, mt.ValueAt - mt.TagAt).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(mt.SizeAt - mt.TagAt), valueSize);
            prop.AddRange(tag);
            prop.AddRange(BitConverter.GetBytes(n));
            foreach (var e in fresh) prop.AddRange(e);
            var ex0 = mp.Exports[i];
            byte[] old = mp.Body.AsSpan(ex0.SerialOffset, ex0.SerialSize).ToArray();
            int cutFrom = mt.TagAt - ex0.SerialOffset, cutTo = mt.ValueAt + mt.Size - ex0.SerialOffset;
            byte[] export = [.. old.AsSpan(0, cutFrom), .. prop, .. old.AsSpan(cutTo)];
            byte[] written = MhoPackageModifier.PackageWriter.ReplaceExport(mp, i, _ => export);
            var problems = MhoPackageModifier.PackageWriter.Verify(mp, MpmPackage.FromBytes(written), i, export);
            if (problems.Count > 0) throw new InvalidDataException($"{ap.GetExportName(i)}: the fixed mirror table didn't verify ({string.Join("; ", problems)})");
            bytes = written;
            changes.Add($"{ap.GetExportName(i)}: mirror table {count} → {n} entries ({(n > count ? $"{n - count} added bone(s) given their own entry" : $"{count - n} entries past the skeleton cut")})");
        }
        return changes.Any(c => c.Contains('→')) ? (bytes, changes) : null;
    }

    /// <summary>The table's entries (each a tagged struct ending in None) and where each one's SourceIndex value is; null when
    /// they don't read as expected.</summary>
    static List<(byte[] Bytes, int SourceAt)>? Elements(MpmPackage p, int start, int end, int count)
    {
        var list = new List<(byte[], int)>();
        int at = start;
        string Name(int pos) { int k = BinaryPrimitives.ReadInt32LittleEndian(p.Body.AsSpan(pos)); return k >= 0 && k < p.Names.Length ? p.Names[k] : ""; }
        for (int e = 0; e < count; e++)
        {
            int begin = at, srcAt = -1;
            for (int guard = 0; guard < 16; guard++)
            {
                if (at + 8 > end) return null;
                string name = Name(at);
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { at += 8; break; }
                if (at + 24 > end) return null;
                string type = Name(at + 8);
                int size = BinaryPrimitives.ReadInt32LittleEndian(p.Body.AsSpan(at + 16));
                int value = at + 24 + (type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase) || type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) ? 8 : 0)
                    + (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? 0 : 0);
                if (name.Equals("SourceIndex", StringComparison.OrdinalIgnoreCase) && type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase)) srcAt = value - begin;
                at = value + (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? 1 : size);
            }
            if (srcAt < 0 || at > end) return null;
            list.Add((p.Body.AsSpan(begin, at - begin).ToArray(), srcAt));
        }
        return at == end ? list : null;
    }

    /// <summary>A bone's left / right partner by name (g_l_wing1 ↔ g_r_wing1, …_l ↔ …_r, Left ↔ Right), else itself.</summary>
    static int Partner(List<string> names, int b)
    {
        string n = names[b];
        foreach (var (a, z) in new[] { ("_l_", "_r_"), ("_r_", "_l_"), ("left", "right"), ("right", "left"), ("Left", "Right"), ("Right", "Left") })
            if (n.Contains(a, StringComparison.Ordinal)) { int k = names.FindIndex(x => x.Equals(n.Replace(a, z), StringComparison.OrdinalIgnoreCase)); if (k >= 0) return k; }
        foreach (var (a, z) in new[] { ("_l", "_r"), ("_r", "_l"), ("_L", "_R"), ("_R", "_L") })
            if (n.EndsWith(a, StringComparison.Ordinal)) { int k = names.FindIndex(x => x.Equals(n[..^a.Length] + z, StringComparison.OrdinalIgnoreCase)); if (k >= 0) return k; }
        return b;
    }

    /// <summary>
    /// The mod's packages with their mirror tables fixed, saved like an editor save (the old folder to the discarded copies; place,
    /// on/off, tags kept). Returns the packages changed, or null with <paramref name="error"/>.
    /// </summary>
    public static List<string>? Apply(ModLibrary lib, Mod m, List<string> log, out string? error)
    {
        error = null;
        string work = Path.Combine(lib.DataFolder, "mirror-fix-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            var d = ModDraft.From(m);
            var done = new List<string>();
            foreach (string f in m.Manifest.UpkReplacements.Where(f => f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)))
            {
                string src = Path.Combine(m.Folder, f);
                if (!File.Exists(src) || Fix(src) is not { } fixedPkg) continue;
                string dst = Path.Combine(work, f);
                File.WriteAllBytes(dst, fixedPkg.Bytes);
                int k = d.Packages.FindIndex(x => x.Item1.Equals(f, StringComparison.OrdinalIgnoreCase));
                if (k >= 0) d.Packages[k] = (f, dst);
                log.AddRange(fixedPkg.Changes.Select(c => $"{f}: {c}"));
                done.Add(f);
            }
            if (done.Count == 0) { error = "No mirror table needed fixing."; return null; }
            if (ModWriter.Save(lib, d, m, out error, "Fix Mirror Tables (Crash Risk)") == null) return null;
            return done;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException) { error = ex.Message; return null; }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }
}
