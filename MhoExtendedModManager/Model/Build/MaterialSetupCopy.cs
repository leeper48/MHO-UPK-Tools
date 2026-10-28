using System.Buffers.Binary;
using System.Drawing;
using AnimPackage = AnimExportCli.Packages.Package;
using AnimExportCli.Meshes;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Copy Material Setup (Kurt, 2026-10-28, a user: Luke Cage's Skrull costume with Modern's look; MHMaterialEditor's Clone MIC
/// does it): another character's material instance (its parent, static switches and scalar / vector values, so its compiled
/// shaders exist) copied into the target package under a name of its own, its texture slots pointed at the target's own
/// maps, and the target's material slot pointed at the copy. The maps stay the target's: the two models' UV layouts differ
/// (the Skrull's and Modern's color maps are laid out differently). A slot the target has no map for gets a flat stand-in
/// (MaterialOut's: a flat normal, no spec, no glow), which the Materials tab's own maps can fill later; the source's
/// environment image (reflectiontex) is kept, it doesn't follow the UVs.
/// Copies of one source into one package with the same maps would have the same compiled data (MHMaterialEditor warns one
/// falls back to the default material then), so such a copy is reused instead of made twice.
/// </summary>
static class MaterialSetupCopy
{
    /// <summary>One material slot of a mesh: its index, the material's path (or "none") and whether it's an instance in the
    /// package (only those can be read for their maps).</summary>
    public sealed record SlotInfo(int Index, string Material, int Export, bool Instance);

    public sealed record Result(byte[] Package, string NewMaterial, List<string> Lines);

    /// <summary>The mesh's material slots as the game draws them (the character component's own list wins where it sets one).</summary>
    public static List<SlotInfo> Slots(byte[] package, string file, string meshName)
    {
        var pkg = Package.FromBytes(package);
        var (_, refs, comp) = MeshMaterials(pkg, package, file, meshName);
        var list = new List<SlotInfo>();
        for (int i = 0; i < refs.Count; i++)
        {
            int r = comp != null && i < comp.Count && comp[i] != 0 ? comp[i] : refs[i];
            string path = r == 0 ? "none" : r > 0 ? pkg.PathOf(pkg.Exports[r - 1]) : ModMeshes.ImportPath(pkg, r);
            bool inst = r > 0 && pkg.ClassOf(pkg.Exports[r - 1]).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase);
            list.Add(new SlotInfo(i, path, r > 0 ? r - 1 : -1, inst));
        }
        return list;
    }

    /// <summary>The mesh's export, its material references and the character component's own list (null: none).</summary>
    static (int Mesh, List<int> Refs, List<int>? Component) MeshMaterials(Package pkg, byte[] bytes, string file, string meshName)
    {
        int mesh = Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        if (mesh < 0) throw new InvalidDataException($"no model {meshName} in {file}");
        var ap = AnimPackage.Read(bytes);
        var refs = ModMeshes.MaterialRefs(ap, mesh);
        if (refs.Count == 0) throw new InvalidDataException($"{meshName}: its materials don't read");
        return (mesh, refs, ModMeshes.ComponentMaterials(pkg, file, meshName));
    }

    /// <summary>
    /// Material slot <paramref name="slot"/> of <paramref name="meshName"/> given a copy of <paramref name="sourceMic"/> (a path
    /// in <paramref name="sourceFile"/>) with the target's maps. <paramref name="tag"/> makes the copy's name its own.
    /// </summary>
    public static Result Copy(byte[] target, string targetFile, string meshName, int slot, string sourceFile, string sourceMic, string tag, string workDir)
    {
        var lines = new List<string>();
        var tgt = Package.FromBytes(target);
        var slots = Slots(target, targetFile, meshName);
        if (slot < 0 || slot >= slots.Count) throw new InvalidDataException($"{meshName} has no material slot {slot}");
        var old = slots[slot];
        var src = Package.Open(sourceFile);
        int mic = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(sourceMic, StringComparison.OrdinalIgnoreCase));
        if (mic < 0 || !src.ClassOf(src.Exports[mic]).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{sourceMic} isn't a material instance in {Path.GetFileName(sourceFile)}");
        var srcSlots = MaterialOut.TextureSlots(src, mic);
        var tgtSlots = old.Instance ? MaterialOut.TextureRefs(tgt, old.Export) : [];
        if (!old.Instance || tgtSlots.Count == 0)
            throw new InvalidDataException($"material slot {slot} uses {old.Material}, which isn't a material instance of this package with maps of its own: there are no maps of its own to keep (the copy would have stand-ins only)");
        lines.Add($"source {sourceMic} ({src.ClassOf(src.Exports[mic])}): maps {string.Join(", ", srcSlots.Keys)}");
        lines.Add($"target slot {slot}: {old.Material}: maps {(tgtSlots.Count == 0 ? "none" : string.Join(", ", tgtSlots.Keys))}");

        // each source map: the target's own of that slot, a stand-in, or (an image that doesn't follow the UVs) the source's
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var standIns = new List<(string Slot, int SourceTex)>();
        var how = new List<string>();
        foreach (var (name, tex) in srcSlots)
        {
            string from = tex >= 0 ? src.PathOf(src.Exports[tex]) : "";
            if (name.Equals("reflectiontex", StringComparison.OrdinalIgnoreCase) || tex < 0) { how.Add($"{name}: the source's ({(tex < 0 ? "from another package" : "an environment image")})"); continue; }
            if (replace.ContainsKey(from)) { how.Add($"{name}: shares {from} with another slot"); continue; }
            if (tgtSlots.TryGetValue(name, out int tt) && tt != 0)
            {
                // (the target's own texture, or one it takes from another package: the Skrull's normal map)
                replace[from] = tt > 0 ? tgt.PathOf(tgt.Exports[tt - 1]) : ModMeshes.ImportPath(tgt, tt);
                how.Add($"{name}: the target's {(tt > 0 ? tgt.Exports[tt - 1].ObjectName : replace[from] + " (from another package)")}");
            }
            else standIns.Add((name, tex));
        }
        string copyName = MaterialOut.Safe(src.Exports[mic].ObjectName + "_" + tag).ToLowerInvariant();

        // flat stand-ins for the maps the target hasn't got, built on one of its own textures (format details)
        byte[] current = target;
        if (standIns.Count > 0)
        {
            int template = tgtSlots.Values.Where(v => v > 0).Select(v => v - 1).FirstOrDefault(-1);
            if (template < 0) template = Array.FindIndex(tgt.Exports, e => tgt.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
            if (template < 0) throw new InvalidDataException($"{targetFile} has no texture to build the stand-in maps on");
            Directory.CreateDirectory(workDir);
            var plan = new List<MaterialOut.PlannedTexture>();
            foreach (var (name, _) in standIns)
            {
                var (color, dxt5, what) = StandIn(name);
                string png = Path.Combine(workDir, MaterialOut.Safe(name) + "_standin.png");
                Protected.CheckWrite(png);
                using (var b = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b)) g.Clear(color);
                    ImagePixels.SavePng(b, png);
                }
                string texName = UniqueExport(tgt, MaterialOut.Safe($"{copyName}_{name}"));
                plan.Add(new MaterialOut.PlannedTexture(name, null, texName, template, png, dxt5, AlphaIsData: dxt5));
                how.Add($"{name}: a stand-in ({what}): the target has no map for it");
            }
            current = MaterialOut.AddTextures(tgt, plan);
            var withTex = Package.FromBytes(current);
            foreach (var (p, (name, tex)) in plan.Zip(standIns))
                replace[src.PathOf(src.Exports[tex])] = withTex.PathOf(withTex.Exports.First(e => e.ObjectName.Equals(p.NewName, StringComparison.OrdinalIgnoreCase)));
        }
        lines.AddRange(how.Select(h => "  " + h));

        // a copy of this source with the same maps already in the package: reused (the same compiled data twice collides)
        var cur = Package.FromBytes(current);
        int made = -1;
        for (int i = 0; i < cur.Exports.Length && standIns.Count == 0; i++)
            if (cur.Exports[i].ObjectName.StartsWith(copyName, StringComparison.OrdinalIgnoreCase) && cur.ClassOf(cur.Exports[i]).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase)
                && SameMaps(cur, i, replace)) { made = i; break; }
        if (made >= 0) lines.Add($"{cur.Exports[made].ObjectName} is already in the package with these maps: used again");
        else
        {
            string name = UniqueExport(cur, copyName);
            var copy = MaterialChoice.Explained(() => ExportCopy.Copy(src, mic, cur, Array.Empty<string>(), name, replace), out string why)
                ?? throw new InvalidDataException($"copying {sourceMic} failed: {why}");
            var check = copy.Check(copy.Output);
            if (check.Count > 0) throw new InvalidDataException($"{name}: " + string.Join("; ", check.Take(5)));
            current = copy.Output;
            cur = Package.FromBytes(current);
            made = copy.RootRef - 1;
            lines.Add($"copied as {cur.PathOf(cur.Exports[made])} (its parent, switches and values as the source's)");
        }

        // the slot pointed at the copy: the mesh's list, and the component's where it sets this slot
        current = PointSlot(current, targetFile, meshName, slot, made + 1);
        var back = Slots(current, targetFile, meshName);
        if (back.Count != slots.Count || back[slot].Export != made || back.Where((s, i) => i != slot).Select(s => s.Material).SequenceEqual(slots.Where((s, i) => i != slot).Select(s => s.Material)) == false)
            throw new InvalidDataException("the material slots didn't read back as written");
        lines.Add($"slot {slot} of {meshName} now uses {cur.Exports[made].ObjectName} (was {old.Material}); read back");
        return new Result(current, cur.Exports[made].ObjectName, lines);
    }

    /// <summary>A stand-in's color: a flat normal, no spec (v1 packing as MaterialOut's: spec 32, rim 238), no glow, grey.</summary>
    static (Color Color, bool Dxt5, string What) StandIn(string slot)
    {
        string s = slot.ToLowerInvariant();
        if (s == "normaltex" || s.Contains("normal")) return (Color.FromArgb(255, 128, 128, 255), false, "flat");
        if (s == "specmultrimmaskreflection") return (Color.FromArgb(255, 32, 238, 0), false, "low spec, no reflection");
        if (s.StartsWith("specmult") || s.Contains("specpow") || s.Contains("reflectivity")) return (Color.FromArgb(0, 0, 0, 0), true, "no spec, no reflection");
        if (s == "emissivespecpow") return (Color.FromArgb(255, 0, 128, 0), false, "no glow");
        if (s.Contains("emissive") || s.Contains("glow") || s.Contains("mask")) return (Color.Black, false, "none");
        return (Color.FromArgb(255, 128, 128, 128), false, "grey");
    }

    static bool SameMaps(Package pkg, int mic, Dictionary<string, string> replace)
    {
        var want = replace.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var have = MaterialOut.TextureRefs(pkg, mic).Values.Where(v => v != 0).Select(v => v > 0 ? pkg.PathOf(pkg.Exports[v - 1]) : ModMeshes.ImportPath(pkg, v)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return want.All(have.Contains);
    }

    static string UniqueExport(Package pkg, string name)
    {
        string n = name;
        for (int k = 2; pkg.Exports.Any(e => e.ObjectName.Equals(n, StringComparison.OrdinalIgnoreCase)); k++) n = $"{name}_{k}";
        return n;
    }

    /// <summary>The slot's reference set in place (same size: the mesh's native list, the component's Materials array) in the
    /// uncompressed package the copy wrote.</summary>
    static byte[] PointSlot(byte[] bytes, string file, string meshName, int slot, int newRef)
    {
        var pkg = Package.FromBytes(bytes);
        if (pkg.Body.Length != pkg.RawFile.Length) throw new InvalidDataException("the package isn't uncompressed after the copy");
        var (mesh, _, comp) = MeshMaterials(pkg, bytes, file, meshName);
        byte[] o = (byte[])bytes.Clone();
        var ap = AnimPackage.Read(bytes);
        var props = ap.TryReadProperties(mesh) ?? throw new InvalidDataException($"{meshName}: its properties don't read");
        int at = pkg.Exports[mesh].SerialOffset + props.PayloadOffset + MeshBounds.ByteSize + 4 + 4 * slot;
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(at), newRef);
        if (comp != null && slot < comp.Count && comp[slot] != 0)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) stem = stem[4..];
            if (stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
            int c = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals($"marvelgamecontent.default__{stem.ToLowerInvariant()}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
            byte[] d = pkg.ReadExportBytes(pkg.Exports[c]).ToArray();
            var mt = TagWalker.Walk(pkg, d, 16)!.First(t => t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(pkg.Exports[c].SerialOffset + mt.ValueAt + 4 + 4 * slot), newRef);
        }
        return o;
    }
}
