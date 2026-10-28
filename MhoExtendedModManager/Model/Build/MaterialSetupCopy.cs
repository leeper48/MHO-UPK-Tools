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

    /// <summary>The game's packed spec layout a texture slot reads (v1 specmultrimmaskreflection, v2
    /// specmult_specpow_skinmask_reflectivity, …: SpecLayouts), or null for any other slot.</summary>
    static SpecLayouts.Layout? Packed(string slot) => SpecLayouts.All.FirstOrDefault(l => l.Parameter.Equals(slot, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a spec conversion would do, in words (the popup offers it), or null when there's nothing to convert: the source's
    /// material reads a packed spec map the target hasn't got, and the target has a packed spec map of another layout (Kurt,
    /// 2026-10-28: first old into new, the Skrull's v1 map for Modern's v2 material; then new into old too, v2 for a v1 one).
    /// </summary>
    public static string? SpecConversion(byte[] target, string targetFile, string meshName, int slot, string sourceFile, string sourceMic)
    {
        try
        {
            var tgt = Package.FromBytes(target);
            var old = Slots(target, targetFile, meshName)[slot];
            var src = Package.Open(sourceFile);
            int mic = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(sourceMic, StringComparison.OrdinalIgnoreCase));
            if (mic < 0 || !old.Instance) return null;
            var mine = MaterialOut.TextureRefs(tgt, old.Export);
            bool Has(string p) => mine.TryGetValue(p, out int r) && r != 0;
            var wanted = MaterialOut.TextureRefs(src, mic).Keys.Where(k => Packed(k) != null && !Has(k)).ToList();
            var have = SpecLayouts.All.Where(l => Has(l.Parameter)).ToList();
            if (wanted.Count == 0 || have.Count == 0) return null;
            return $"this costume has {string.Join(" and ", have.Select(l => $"{l.Parameter} ({l.Label})"))}; the source's material reads {string.Join(" and ", wanted.Select(k => $"{k} ({Packed(k)!.Label})"))}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// The target's packed spec maps made into the layout of <paramref name="want"/> (a slot the source's material reads), as
    /// a PNG in <paramref name="dir"/>, and whether it uses alpha; null when none can be read. The maps are first read into one
    /// set of channels (shine, power, skin mask, reflectivity: SpecLayouts.ToAngela; spec power from a second map when the
    /// first has none, a v1 emissivespecpow's G), then laid out as <paramref name="want"/> reads them; a channel with no
    /// counterpart gets the game's usual value (a v1 rim mask 238, no glow).
    /// </summary>
    /// <remarks>The shine and reflectivity are scaled first (Kurt, 2026-10-28: "look exactly the same"): the shine halfway
    /// (geometric mean) between the costume's own strength and the source map's level, reflectivity never above the costume's
    /// own. Measured on Luke Cage: Modern's map averages shine 34 and reflectivity 255 under spec 18 / reflection 10, the
    /// Skrull's v1 map shine 58 and reflection 27 under 0.8 / 3; kept at the Skrull's own strength (×0.044) it was as dull as
    /// no spec, at Modern's level (×0.59) the gauntlets and pants went white, between them (×0.16) it looked like Modern in the
    /// preview; a DXT1 map's A reads 255 everywhere, which isn't a level. When the source's map can't be read, the strengths'
    /// ratio is used (the costume shines as it did).</remarks>
    static (string File, bool Alpha)? ConvertedSpec(Package tgt, string targetFile, Dictionary<string, int> mine, string dir, List<string> lines, MaterialInfo? was, MaterialInfo? now, Package src, int srcRef, string want, string sourceFile)
    {
        if (Packed(want) is not { } lw) return null;
        int[] off = [2, 1, 0, 3];
        byte[]? px = null; int w = 0, h = 0; bool havePower = false;
        foreach (var l in SpecLayouts.All)
        {
            if (!mine.TryGetValue(l.Parameter, out int r) || r == 0) continue;
            string png = Path.Combine(dir, MaterialOut.Safe(l.Parameter) + "_original.png");
            if (!File.Exists(png) && !DecodeTexture(tgt, r, targetFile, png)) { lines.Add($"  {l.Parameter}: can't be read, left out of the conversion"); continue; }
            string conv = SpecLayouts.ToAngela(png, l);
            if (px == null)
            {
                (w, h, px) = ImagePixels.ReadBgra(conv);
                havePower = l.FromChannel[1] >= 0;
                lines.Add($"  {want}: made from the costume's {l.Parameter} ({l.Label})");
            }
            else if (!havePower && l.FromChannel[1] >= 0)
            {
                // a second map with a spec power (v1 emissivespecpow G): its power goes into G
                var (_, _, p2) = ImagePixels.ReadBgra(conv, w, h);
                for (int i = 0; i < w * h; i++) px[4 * i + off[1]] = p2[4 * i + off[1]];
                havePower = true;
                lines.Add($"  {want}: spec power from the costume's {l.Parameter}");
            }
        }
        if (px == null) return null;
        double Mean(byte[] b, int c) { double t = 0; for (int i = 0; i < b.Length / 4; i++) t += b[4 * i + off[c]]; return t / Math.Max(1, b.Length / 4); }
        float S(MaterialInfo? m, string n, float d) => m != null && m.Scalars.TryGetValue(n, out float v) ? v : d;
        // a material's spec strength: v2 specmult, v1 specmult1 × totalspecmult
        float Strength(MaterialInfo? m) => m != null && m.Scalars.ContainsKey("specmult") ? S(m, "specmult", 1) : S(m, "specmult1", 1) * S(m, "totalspecmult", 1);
        float shineWas = Strength(was), shineNow = Strength(now);
        float kStrength = shineNow > 0 ? Math.Clamp(shineWas / shineNow, 0, 4) : 1;   // (the costume as bright as with its own material)
        float ks, kr; string why;
        string srcPng = Path.Combine(dir, MaterialOut.Safe(want) + "_source.png");
        if (srcRef != 0 && DecodeTexture(src, srcRef, Path.GetFileName(sourceFile), srcPng))
        {
            var (_, _, sp) = ImagePixels.ReadBgra(SpecLayouts.ToAngela(srcPng, lw));
            double sr = Mean(sp, 0), sa = Mean(sp, 3), cr = Mean(px, 0), ca = Mean(px, 3);
            float kLevel = cr > 0.5 ? (float)Math.Clamp(sr / cr, 0, 4) : 1;
            ks = MathF.Sqrt(kLevel * kStrength);
            kr = ca > 0.5 ? (float)Math.Clamp(sa / ca, 0, 1) : 1;
            why = $"between this costume's own strength (×{kStrength:0.###}: spec {shineWas:0.##} then, {shineNow:0.##} now) and the source map's level (×{kLevel:0.##}: its shine averages {sr:0}, this costume's {cr:0})";
        }
        else
        {
            float reflWas = was?.Switch("usereflection", true) == false ? 0 : S(was, "reflectionmult", 1), reflNow = S(now, "reflectionmult", 1);
            ks = kStrength; kr = reflNow > 0 ? Math.Clamp(reflWas / reflNow, 0, 4) : 1;
            why = $"as strong as with its own material (spec {shineWas:0.##} then, {shineNow:0.##} now; reflection {reflWas:0.##}, {reflNow:0.##})";
        }
        for (int i = 0; i < w * h; i++)
        {
            px[4 * i + off[0]] = (byte)Math.Min(255, MathF.Round(px[4 * i + off[0]] * ks));
            px[4 * i + off[3]] = (byte)Math.Min(255, MathF.Round(px[4 * i + off[3]] * kr));
        }
        lines.Add($"  {want}: shine × {ks:0.##}, reflectivity × {kr:0.##}, {why}");
        // laid out as the wanted slot reads it: each of its channels from the one it holds (lw.FromChannel), else the usual value
        byte[] o = px;
        if (lw.Id != "v2skin")
        {
            o = new byte[px.Length];
            byte Usual(string channel) => channel switch { "rim mask" => 238, "ambient" or "height" => 128, _ => 0 };
            for (int c = 0; c < 4; c++)
            {
                int a = Array.IndexOf(lw.FromChannel, c);
                for (int i = 0; i < w * h; i++) o[4 * i + off[c]] = a >= 0 ? px[4 * i + off[a]] : Usual(lw.Channels[c]);
            }
            lines.Add($"  {want}: laid out as {lw.Label} ({string.Join(", ", lw.Channels.Select((ch, c) => ch == "—" ? null : Array.IndexOf(lw.FromChannel, c) >= 0 ? $"{ch} from the costume's maps" : $"{ch} {Usual(ch)}").OfType<string>())})");
        }
        bool alpha = lw.Channels[3] != "—";
        string outFile = Path.Combine(dir, MaterialOut.Safe(want) + "_converted.png");
        Protected.CheckWrite(outFile);
        ImagePixels.Save(w, h, o, outFile);
        return (outFile, alpha);
    }

    /// <summary>
    /// A material's color map (its diffusetex, else its first texture) as a small picture, for the material lists (Kurt,
    /// 2026-10-28: names like mat_body don't say which is which); null when it can't be read.
    /// </summary>
    public static Image? ColorMap(string packagePath, int materialExport, int size)
    {
        try
        {
            var pkg = Package.Open(packagePath);
            var refs = MaterialOut.TextureRefs(pkg, materialExport);
            int r = refs.TryGetValue("diffusetex", out int d) && d != 0 ? d : refs.Values.FirstOrDefault(v => v != 0);
            if (r == 0) return null;
            string png = Path.Combine(Path.GetTempPath(), "MHO_ExtMM_matthumbs", Guid.NewGuid().ToString("N") + ".png");
            if (!DecodeTexture(pkg, r, Path.GetFileName(packagePath), png)) return null;
            try
            {
                var (w, h, bgra) = ImagePixels.ReadBgra(png);
                return MhoExtendedModManager.Gui.Ui.Thumb(bgra, w, h, size);
            }
            finally { try { File.Delete(png); } catch (IOException) { } }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException or IndexOutOfRangeException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A texture of the target (an export, or an import found in the hero's base package) decoded to a PNG.</summary>
    static bool DecodeTexture(Package tgt, int r, string targetFile, string png)
    {
        Package pkg = tgt; int idx;
        if (r > 0) idx = r - 1;
        else
        {
            // an import: the hero's base package (UC__MarvelPlayer_<Hero>_SF), the object by its path
            string path = ModMeshes.ImportPath(tgt, r);
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(targetFile), @"^(UC__MarvelPlayer_[^_]+)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || Settings.Current.CookedFolder is not string cooked) return false;
            string bp = StockFiles.For(cooked, m.Groups[1].Value + "_SF.upk");
            if (!File.Exists(bp)) return false;
            pkg = Package.Open(bp);
            idx = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase) || path.EndsWith("." + pkg.PathOf(e), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
        }
        var mip = TextureExport.ReadBestMip(pkg, idx, out _, Settings.Current.CookedFolder);
        if (mip == null || TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is not byte[] bgra) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        Protected.CheckWrite(png);
        ImagePixels.Save(mip.Width, mip.Height, bgra, png);
        return true;
    }

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
    /// <param name="convertSpec">A v2 packed spec slot the target has no map for is made from its older packed spec maps
    /// (<see cref="ConvertedSpec"/>) instead of a flat stand-in.</param>
    public static Result Copy(byte[] target, string targetFile, string meshName, int slot, string sourceFile, string sourceMic, string tag, string workDir, bool convertSpec = false)
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

        // each source map: the target's own of that slot, a stand-in, or (the environment image, which doesn't follow the UVs)
        // the source's. A source map in another package is never kept as it is (Kurt, 2026-10-28, the Skrull's material onto
        // Captain America: its normal, spec and reflection maps are in Luke Cage's base package, which the game doesn't load for
        // Cap, so they'd have shown the engine's default textures): only one in a package that's always loaded stays a reference.
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var standIns = new List<(string Slot, string From)>();
        var envCopies = new List<(string Slot, int Ref, string From)>();
        var how = new List<string>();
        foreach (var (name, r) in MaterialOut.TextureRefs(src, mic))
        {
            if (r == 0) continue;
            string from = r > 0 ? src.PathOf(src.Exports[r - 1]) : ModMeshes.ImportPath(src, r);
            if (replace.ContainsKey(from) || standIns.Any(x => x.From.Equals(from, StringComparison.OrdinalIgnoreCase)) || envCopies.Any(x => x.From.Equals(from, StringComparison.OrdinalIgnoreCase)))
            { how.Add($"{name}: shares {from} with another slot"); continue; }
            if (name.Equals("reflectiontex", StringComparison.OrdinalIgnoreCase))
            {
                if (r > 0) how.Add($"{name}: the source's environment image ({from}), copied with the material");
                else if (AlwaysLoaded(from)) how.Add($"{name}: the source's environment image ({from}, in a package the game always loads)");
                else envCopies.Add((name, r, from));
                continue;
            }
            if (tgtSlots.TryGetValue(name, out int tt) && tt != 0)
            {
                // (the target's own texture, or one it takes from its hero's base package: the Skrull's normal map)
                replace[from] = tt > 0 ? tgt.PathOf(tgt.Exports[tt - 1]) : ModMeshes.ImportPath(tgt, tt);
                how.Add($"{name}: the target's {(tt > 0 ? tgt.Exports[tt - 1].ObjectName : replace[from] + " (from its hero's package)")}");
            }
            else standIns.Add((name, from));
        }
        string copyName = MaterialOut.Safe(src.Exports[mic].ObjectName + "_" + tag).ToLowerInvariant();

        // flat stand-ins for the maps the target hasn't got, built on one of its own textures (format details)
        byte[] current = target;
        if (standIns.Count > 0 || envCopies.Count > 0)
        {
            int template = TemplateTexture(tgt, tgtSlots.Values.Where(v => v > 0).Select(v => v - 1));
            if (template < 0) throw new InvalidDataException($"{targetFile} has no texture of a known texture group to build the new maps on");
            lines.Add($"new maps built on {tgt.Exports[template].ObjectName} ({LodGroup(tgt, template) ?? "no texture group: the default"})");
            Directory.CreateDirectory(workDir);
            var plan = new List<MaterialOut.PlannedTexture>();
            // the source's environment image from a package the target's character doesn't load: its pixels copied in
            var envNames = new List<(string From, string Name)>();
            foreach (var (name, r, from) in envCopies)
            {
                string png = Path.Combine(workDir, MaterialOut.Safe(name) + "_source.png");
                if (!DecodeTexture(src, r, Path.GetFileName(sourceFile), png)) { how.Add($"{name}: the source's environment image {from} can't be read, so it's left as a reference into its package (it may show the default image)"); continue; }
                string texName = UniqueExport(tgt, MaterialOut.Safe($"{copyName}_{name}"));
                plan.Add(new MaterialOut.PlannedTexture(name, null, texName, template, png, false));
                envNames.Add((from, texName));
                how.Add($"{name}: the source's environment image ({from}), copied into this package (its own package isn't loaded with this character)");
            }
            foreach (var (name, _) in standIns)
            {
                string texName = UniqueExport(tgt, MaterialOut.Safe($"{copyName}_{name}"));
                // the v2 packed spec made from the costume's own older packed maps (the shine kept)
                if (convertSpec && Packed(name) != null && ConvertedSpec(tgt, targetFile, tgtSlots, workDir, lines, Info(tgt, old.Export), Info(src, mic), src, MaterialOut.TextureRefs(src, mic).TryGetValue(name, out int sv) ? sv : 0, name, sourceFile) is var (conv, alpha))
                {
                    plan.Add(new MaterialOut.PlannedTexture(name, null, texName, template, conv, alpha, AlphaIsData: alpha));
                    how.Add($"{name}: made from the costume's own spec maps (converted to this layout)");
                    continue;
                }
                var (color, dxt5, what) = StandIn(name);
                string png = Path.Combine(workDir, MaterialOut.Safe(name) + "_standin.png");
                Protected.CheckWrite(png);
                using (var b = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b)) g.Clear(color);
                    ImagePixels.SavePng(b, png);
                }
                plan.Add(new MaterialOut.PlannedTexture(name, null, texName, template, png, dxt5, AlphaIsData: dxt5));
                how.Add($"{name}: a stand-in ({what}): the target has no map for it");
            }
            current = MaterialOut.AddTextures(tgt, plan);
            var withTex = Package.FromBytes(current);
            string Added(string n) => withTex.PathOf(withTex.Exports.First(e => e.ObjectName.Equals(n, StringComparison.OrdinalIgnoreCase)));
            foreach (var (from, n) in envNames) replace[from] = Added(n);
            foreach (var (p, (name, from)) in plan.Skip(envNames.Count).Zip(standIns)) replace[from] = Added(p.NewName);
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

    /// <summary>
    /// The texture new maps are built on (their format details and texture group come from it): one of the material's own,
    /// else any in the package, whose texture group the game knows (TEXTUREGROUP_…, or none: the default). Kurt, 2026-10-28,
    /// a user's crash ("CreateTexture … SizeX=1, SizeY=1, DXT1 … D3DERR_INVALIDCALL"): a Spider-Woman mod's textures, made
    /// with MHMaterialEditor, have the group "textures" (no mipmaps, so they load); the reflection image built on one got that
    /// group with a full mip chain, and the game, with no settings for that group, made it a 1×1 DXT1. -1 when there's none.
    /// </summary>
    static int TemplateTexture(Package pkg, IEnumerable<int> preferred)
    {
        bool Known(int i) => pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)
            && (LodGroup(pkg, i) is not string g || g.StartsWith("texturegroup_", StringComparison.OrdinalIgnoreCase));
        foreach (int i in preferred) if (i >= 0 && i < pkg.Exports.Length && Known(i)) return i;
        for (int i = 0; i < pkg.Exports.Length; i++) if (Known(i)) return i;
        return -1;
    }

    /// <summary>A texture's LODGroup property (its texture group, lower case), or null when it has none (the default).</summary>
    static string? LodGroup(Package pkg, int texture)
    {
        try
        {
            byte[] d = pkg.ReadExportBytes(pkg.Exports[texture]).ToArray();
            var t = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(x => x.Name.Equals("LODGroup", StringComparison.OrdinalIgnoreCase));
            return t == null ? null : TagWalker.NameAt(pkg, d, t.ValueAt + (t.Size == 16 ? 8 : 0)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    /// <summary>Whether an imported object is in a package the game always loads (MarvelGame.upk, Startup.upk), so a reference to
    /// it works in any character's package.</summary>
    static bool AlwaysLoaded(string path)
    {
        if (Settings.Current.CookedFolder is not string cooked) return false;
        lock (alwaysLoaded)
        {
            if (alwaysLoaded.Count == 0)
                foreach (string f in new[] { "MarvelGame.upk", "Startup.upk" })
                    try
                    {
                        string p = StockFiles.For(cooked, f);
                        if (!File.Exists(p)) continue;
                        var pkg = Package.Open(p);
                        foreach (var e in pkg.Exports) alwaysLoaded.Add(pkg.PathOf(e));
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
            return alwaysLoaded.Contains(path) || alwaysLoaded.Any(x => path.EndsWith("." + x, StringComparison.OrdinalIgnoreCase));
        }
    }
    static readonly HashSet<string> alwaysLoaded = new(StringComparer.OrdinalIgnoreCase);

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

    static MaterialInfo? Info(Package pkg, int export)
    {
        try { return ModMaterials.Read(pkg, export + 1); }
        catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { return null; }
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
