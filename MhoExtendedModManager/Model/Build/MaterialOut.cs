using System.Buffers.Binary;
using System.Drawing;
using System.Reflection;
using System.Text.RegularExpressions;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>One MFF material to turn into an MHO material instance.</summary>
sealed record MffMaterial(string Name, string? Colour, string? Spec, string? Normal, bool SpecGenerated = false)
{
    /// <summary>An MHO packed spec map (R shine, G power, B skin, A reflectivity), put in as it is; a spec color map.</summary>
    public string? SpecMho { get; init; }
    public string? SpecColor { get; init; }
    /// <summary>A glow map (a glow color on black) for the template's emissive slot; GuessGlow: none of its own, so the color
    /// map's near-white / cyan spots glow (the automatic Metal + Glow choice).</summary>
    public string? Glow { get; init; }
    public bool GuessGlow { get; init; }
}

/// <summary>
/// Phase 4: MHO materials for the MFF sections, in the base package (in memory, verified at every step):
/// the game can't compile shaders, so each MFF material is a renamed copy of a material instance the package already has
/// (its parent and static switches, so its compiled shaders exist) with its texture parameters pointed at new textures.
/// Slots (Punisher Classic's new_punisher_mat, a chbasematerial instance): diffusetex = the MFF colour map;
/// normaltex = the generated normal map (DirectX green); specmultrimmaskreflection = the MFF _sp map (Rogue's MFF ports put
/// it there); every other texture slot (speccolortex, emissivespecpow …) = a small neutral texture (the stock ones are
/// laid out for the stock UVs). New textures: MPM's TextureImport builder (inline mips, NeverStream; its template's
/// LODGroup), DXT1 from MPM's encoder; added with PackageRebuilder; the instance copies with ExportCopy (renamed, its
/// texture references replaced). MPM's texture builder is private to TextureImport: called by reflection (vendored code
/// is never edited here).
/// </summary>
static class MaterialOut
{
    public sealed record Result(byte[] Package, Dictionary<string, int> MaterialRef, List<string> Notes);

    static readonly MethodInfo BuilderMethod = typeof(TextureImport).GetMethod("Builder", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MPM's TextureImport.Builder not found (vendored code changed?)");
    static readonly MethodInfo CheckMethod = typeof(TextureImport).GetMethod("CheckTexture", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("MPM's TextureImport.CheckTexture not found (vendored code changed?)");

    public static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    /// <summary>The start of every object name a build adds ("mff_", or "mff2_", "mff3_" … when the package already holds an
    /// earlier build's: <see cref="PrefixFor"/>). Set by <see cref="Build"/> for the length of the call.</summary>
    [ThreadStatic] static string? prefix;
    static string P => prefix ?? "mff_";

    /// <summary>
    /// The name prefix for a build into <paramref name="pkg"/>: "mff_" for a package without a Model-tab build, else the
    /// first free "mff&lt;n&gt;_" (2026-10-07, a user: building onto a mod's package that already held a Model build, made in
    /// another session or by the mod's author, failed: 'mff_template_mat' already exists). The earlier build's objects stay
    /// in the package unused; the mesh is replaced as always.
    /// </summary>
    public static string PrefixFor(Package pkg)
    {
        bool Taken(string p) => pkg.Exports.Any(e => e.ObjectName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (!Taken("mff_")) return "mff_";
        for (int n = 2; ; n++) if (!Taken($"mff{n}_")) return $"mff{n}_";
    }

    /// <summary>Texture parameters of a material instance: slot name → texture export index (-1 when it's an import).</summary>
    public static Dictionary<string, int> TextureSlots(Package pkg, int mic)
    {
        byte[] d = pkg.ReadExportBytes(pkg.Exports[mic]);
        int at = 4;
        var tp = TaggedProps.Read(d, ref at, r => NameOf(pkg, r));
        var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var arr = tp.Find("textureparametervalues");
        if (arr == null) return slots;
        foreach (var e in tp.StructArray(arr))
        {
            var pn = e.Find("parametername"); var pv = e.Find("parametervalue");
            if (pn == null || pv == null) continue;
            string name = NameOf(pkg, BinaryPrimitives.ReadInt64LittleEndian(pn.Value));
            int r = BinaryPrimitives.ReadInt32LittleEndian(pv.Value);
            slots[name] = r > 0 ? r - 1 : -1;
        }
        return slots;
    }

    /// <summary>Mean colour (R, G, B 0-255) of a texture export's largest mip (from the game's .tfc when streamed), or null.</summary>
    static (float R, float G, float B)? MeanOf(Package pkg, int tex)
    {
        if (tex < 0) return null;
        var mip = TextureExport.ReadBestMip(pkg, tex, out _, Settings.Current.CookedFolder);
        if (mip == null) return null;
        var bgra = TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _);
        if (bgra == null || bgra.Length < 4) return null;
        double r = 0, g = 0, b = 0; int n = bgra.Length / 4;
        for (int i = 0; i < n; i++) { b += bgra[4 * i]; g += bgra[4 * i + 1]; r += bgra[4 * i + 2]; }
        return ((float)(r / n), (float)(g / n), (float)(b / n));
    }

    static string NameOf(Package pkg, long r)
    {
        int idx = (int)(r & 0xFFFFFFFF), num = (int)(r >> 32);
        string n = idx >= 0 && idx < pkg.Names.Length ? pkg.Names[idx] : $"#{idx}";
        return num > 0 ? $"{n}_{num - 1}" : n;
    }

    /// <summary>The template's own stock maps (0.7.0, the metal test): its rim mask level (Punisher Classic 238, Mark 43 133),
    /// whether its stock material reflects (packed B mean over 64: Mark 43 195, Punisher Classic 4), its spec colour (Mark 43
    /// near white, Punisher Classic blue) and spec power. They set the stand-ins and the v1 packing.</summary>
    sealed record StockProfile(int Rim, bool Reflects, (float R, float G, float B)? SpecColour, (float R, float G, float B)? EmissiveSpecPow);

    static StockProfile ReadStock(Package pkg, Dictionary<string, int> slots, List<string> notes)
    {
        var stockSpec = slots.TryGetValue("specmultrimmaskreflection", out int ssx) ? MeanOf(pkg, ssx) : null;
        var stockCol = slots.TryGetValue("speccolortex", out int scx) ? MeanOf(pkg, scx) : null;
        var stockEsp = slots.TryGetValue("emissivespecpow", out int sex) ? MeanOf(pkg, sex) : null;
        int rim = stockSpec is { } st0 ? (int)MathF.Round(st0.G) : 238;
        bool reflects = stockSpec is { } st1 && st1.B > 64;
        notes.Add($"stock maps of the template: packed spec {(stockSpec is { } a0 ? $"R {a0.R:0} G {a0.G:0} B {a0.B:0}" : "unread")}, spec colour {(stockCol is { } a1 ? $"{a1.R:0} {a1.G:0} {a1.B:0}" : "unread")}, emissive/spec power {(stockEsp is { } a2 ? $"{a2.R:0} {a2.G:0} {a2.B:0}" : "unread")} → rim {rim}, reflection {(reflects ? "from the MFF metal mask (_sp blue)" : "off")}");
        return new StockProfile(rim, reflects, stockCol, stockEsp);
    }

    /// <summary>The shared flat stand-ins: no spec (low spec, the stock rim, no reflection; black for matte), grey, no glow
    /// (emissivespecpow R 0, G / B as the stock spec power), the stock spec colour, a flat normal.</summary>
    sealed record StandIns(string NoSpec, string Grey, string NoGlow, string SpecColour, string FlatNormal);

    static StandIns MakeStandIns(MaterialMaps maps, StockProfile stock)
    {
        string noSpec = maps.Flat("mff_neutral_spec", Color.FromArgb(255, 32, stock.Rim, 0));
        if (maps.Matte) noSpec = maps.Flat("mff_neutral_matte", Color.FromArgb(255, 0, 0, 0));
        maps.NoteReflection();
        string grey = maps.Flat("mff_neutral_grey", Color.FromArgb(255, 128, 128, 128));
        string noGlow = maps.Flat("mff_neutral_noglow", stock.EmissiveSpecPow is { } e0 ? Color.FromArgb(255, 0, (int)e0.G, (int)e0.B) : Color.FromArgb(255, 0, 128, 0));
        string specCol = stock.SpecColour is { } c0 && !maps.Matte ? maps.Flat("mff_neutral_speccolor", Color.FromArgb(255, (int)c0.R, (int)c0.G, (int)c0.B)) : grey;
        string flatNormal = maps.Flat("mff_neutral_normal", Color.FromArgb(255, 128, 128, 255));
        return new StandIns(noSpec, grey, noGlow, specCol, flatNormal);
    }

    /// <summary>What kind of template it is.
    /// MetalStyle (0.7.3): a v2 template with its own reflection image and spec colour (Angela's armour): metal packing and a
    /// spec colour per material. GlowSlot (0.7.5): a plain "emissive" slot of its own (Angela's weapons_1602_mtl): a glow map
    /// per material. DiffuseTex: a slot sharing the diffuse's texture follows the diffuse.</summary>
    sealed record TemplateKind(bool MetalStyle, bool GlowSlot, int GlowTex, int DiffuseTex);

    static TemplateKind KindOf(Dictionary<string, int> slots)
    {
        bool metalStyle = slots.ContainsKey("reflectiontex") && slots.ContainsKey("specmult_specpow_skinmask_reflectivity")
                          && slots.TryGetValue("speccolortex", out int sct) && sct != (slots.TryGetValue("diffusetex", out int dt0) ? dt0 : -2);
        bool glowSlot = slots.TryGetValue("emissive", out int emx) && !slots.Any(kv => kv.Value == emx && !kv.Key.Equals("emissive", StringComparison.OrdinalIgnoreCase));
        return new TemplateKind(metalStyle, glowSlot, emx, slots.TryGetValue("diffusetex", out int dtx) ? dtx : -2);
    }

    /// <summary>One new texture: the slot it fills, its material (null = shared), its name, the stock texture it's built
    /// from (the template's, for format details), the image, and DXT5 (alpha) or DXT1.</summary>
    /// <param name="AlphaIsData">The alpha is a value, not transparency (a packed spec map's reflectivity): encoded plain, since
    /// refine fits DXT5 colors only where the alpha isn't 0 (measured on Angela's map: color error 1.3 refined, 0.2 plain).</param>
    sealed record PlannedTexture(string Slot, string? Material, string NewName, int Template, string Image, bool Dxt5, bool AlphaIsData = false);

    /// <summary>Builds the materials into <paramref name="packageBytes"/> (a stock base package). Returns the new package
    /// and each MFF material's instance reference (export index + 1). <paramref name="spec"/> / <paramref name="reflect"/>:
    /// the A/B switches (see <see cref="ImportOptions"/>).</summary>
    public static Result Build(byte[] packageBytes, string templateMic, IReadOnlyList<MffMaterial> mats, string workDir, string? spec = null, string? reflect = null, string namePrefix = "mff_")
    {
        prefix = namePrefix;
        try { return BuildNamed(packageBytes, templateMic, mats, workDir, spec, reflect); }
        finally { prefix = null; }
    }

    static Result BuildNamed(byte[] packageBytes, string templateMic, IReadOnlyList<MffMaterial> mats, string workDir, string? spec, string? reflect)
    {
        var notes = new List<string>();
        var pkg = Package.FromBytes(packageBytes);
        int mic = Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals(templateMic, StringComparison.OrdinalIgnoreCase)
                                                    && pkg.ClassOf(e).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase));
        if (mic < 0) throw new InvalidDataException($"no material instance {templateMic} in the package");
        var slots = TextureSlots(pkg, mic);
        if (slots.Values.Any(v => v < 0)) throw new InvalidDataException($"{templateMic} uses an imported texture; pick an instance whose textures are in the package");
        notes.Add($"template {pkg.PathOf(pkg.Exports[mic])} ({pkg.ClassOf(pkg.Exports[mic])}), texture slots: {string.Join(", ", slots.Keys)}");

        Directory.CreateDirectory(workDir);
        var stock = ReadStock(pkg, slots, notes);
        var maps = new MaterialMaps(workDir, notes, spec, reflect, stock.Rim, stock.Reflects);
        var stand = MakeStandIns(maps, stock);
        var kind = KindOf(slots);
        if (kind.MetalStyle) notes.Add("metal style (template with its own reflection image and spec colour): A = MFF metal mask (_sp blue) → ~130, R = _sp red × 3.5, G 45 → 115 on metal, B 0, spec colour = colour map × 0.85 → 1.25 on metal");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var plan = Plan(slots, mats, maps, stand, kind, notes, out var shared);
        long tPlan = clock.ElapsedMilliseconds; clock.Restart();
        byte[] withTextures = AddTextures(pkg, plan);
        long tAdd = clock.ElapsedMilliseconds; clock.Restart();
        notes.Add($"{plan.Count} textures added ({plan.Count(p => p.Material == null)} shared neutral), verified; package {packageBytes.Length:N0} → {withTextures.Length:N0} bytes");
        var (current, refs) = CopyInstances(pkg, mic, templateMic, slots, mats, shared, kind, withTextures);
        notes.Add($"{mats.Count} material instance(s) copied from {templateMic}, verified");
        notes.Add($"timing: maps {tPlan} ms, textures encoded and added {tAdd} ms, instances {clock.ElapsedMilliseconds} ms");
        return new Result(current, refs, notes);
    }

    /// <summary>Which image every slot gets: shared stand-ins first (slots MFF has no map for), then per material.</summary>
    static List<PlannedTexture> Plan(Dictionary<string, int> slots, IReadOnlyList<MffMaterial> mats, MaterialMaps maps, StandIns stand,
                                     TemplateKind kind, List<string> notes, out Dictionary<string, string> shared)
    {
        var plan = new List<PlannedTexture>();
        shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // slot → shared texture name
        foreach (var (slot, tex) in slots)
        {
            if (Is(slot, "diffusetex") || Is(slot, "normaltex") || Is(slot, "specmultrimmaskreflection") || Is(slot, "specmult_specpow_skinmask_reflectivity")) continue;
            // A slot sharing the diffuse's texture (Punisher Modern VU's speccolortex = its diffuse) gets the MFF colour map
            // with it: the copy replaces references by texture.
            if (tex == kind.DiffuseTex) { notes.Add($"{slot} uses the diffuse texture: gets the colour map"); continue; }
            if (Is(slot, "reflectiontex")) { notes.Add("reflectiontex: the stock environment image is kept"); continue; }
            if (kind.MetalStyle && Is(slot, "speccolortex")) continue;   // per material
            if (kind.GlowSlot && Is(slot, "emissive")) continue;   // per material
            string n = $"{P}{Safe(slot)}_neutral";
            shared[slot] = n;
            plan.Add(new(slot, null, n, tex, Is(slot, "emissivespecpow") ? stand.NoGlow
                : Is(slot, "speccolortex") ? (maps.Matte ? stand.NoSpec : stand.SpecColour) : stand.Grey, false));
        }
        foreach (var m in mats)
        {
            string b = P + Safe(m.Name);
            if (slots.TryGetValue("diffusetex", out int dt)) plan.Add(new("diffusetex", m.Name, b + "_diff", dt, m.Colour ?? stand.Grey, false));
            if (slots.TryGetValue("normaltex", out int nt)) plan.Add(new("normaltex", m.Name, b + "_norm", nt, m.Normal ?? stand.FlatNormal, false));
            if (slots.TryGetValue("specmultrimmaskreflection", out int st))
                plan.Add(new("specmultrimmaskreflection", m.Name, b + "_spec", st, m.Spec != null ? maps.PackedV1(m.Spec, b + "_spec_packed", stand.NoSpec, m.SpecGenerated ? 1f : 2.5f) : stand.NoSpec, false));
            if (slots.TryGetValue("specmult_specpow_skinmask_reflectivity", out int sv))
                plan.Add(new("specmult_specpow_skinmask_reflectivity", m.Name, b + "_spec", sv,
                    m.SpecMho ?? (m.Spec != null ? (kind.MetalStyle ? maps.PackedMetal(m.Spec, b + "_spec_packed") : maps.PackedV2(m.Spec, b + "_spec_packed", stand.NoSpec, 15, 0)) : stand.NoSpec),
                    kind.MetalStyle || m.SpecMho != null, AlphaIsData: true));   // (an MHO map as it is: DXT5 keeps its reflectivity alpha)
            if (kind.GlowSlot)
                plan.Add(new("emissive", m.Name, b + "_glow", kind.GlowTex, m.Glow ?? (m.GuessGlow && m.Colour != null ? maps.GlowMap(m.Colour, b + "_glow") : maps.Flat("mff_neutral_black", Color.Black)), false));
            if (m.Glow != null) notes.Add($"{m.Name}: glow map {Path.GetFileName(m.Glow)}{(kind.GlowSlot ? "" : ": this template has no glow slot (pick Automatic or Metal + Glow)")}");
            if (kind.MetalStyle && slots.TryGetValue("speccolortex", out int scv))
                plan.Add(new("speccolortex", m.Name, b + "_speccol", scv, m.SpecColor ?? (m.Colour != null ? maps.SpecColour(m.Colour, m.Spec, b + "_speccol") : stand.Grey), false));
            if (m.SpecMho != null) notes.Add($"{m.Name}: MHO spec map put in as it is ({Path.GetFileName(m.SpecMho)}){(slots.ContainsKey("specmult_specpow_skinmask_reflectivity") ? "" : ": this template has no slot for it")}");
            if (m.SpecColor != null && !kind.MetalStyle) notes.Add($"{m.Name}: the spec color map needs a template with its own spec color slot (Metal); this one tints by the color map");
            if (m.Colour == null) notes.Add($"{m.Name}: no colour map found (grey)");
            if (m.Spec == null && m.SpecMho == null) notes.Add($"{m.Name}: no _sp map (low spec, no reflection)");
            else if (m.SpecGenerated) notes.Add($"{m.Name}: spec map generated from the color map");
        }
        return plan;
    }

    static bool Is(string slot, string name) => slot.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every planned texture encoded (MPM's encoder, refine) and added in one PackageRebuilder rebuild, each built from
    /// its template texture by MPM's TextureImport builder; the rebuild and every new texture verified.</summary>
    static byte[] AddTextures(Package pkg, List<PlannedTexture> plan)
    {
        var addNames = new List<string>();
        void Need(string n) { if (!pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) && !addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n); }
        // the encoding (most of a build's time: Angela's 12 textures took 8 s one after another) runs in parallel; each texture
        // depends only on its own image, and the results keep the plan's order
        var encoded = new TextureImport.DdsImage[plan.Count];
        var po = new ParallelOptions { MaxDegreeOfParallelism = Environment.GetEnvironmentVariable("MHO_SERIAL_ENCODE") == "1" ? 1 : -1 };   // (1: one at a time, to compare)
        Parallel.For(0, plan.Count, po, i =>
        {
            var p = plan[i];
            var enc = TextureEncode.FromImage(p.Image, p.Dxt5 ? "dxt5" : "dxt1", 85, 1f, false, 0, refine: !p.AlphaIsData);
            encoded[i] = TextureImport.ParseDds(TextureImport.WriteDds(enc), out string? err) ?? throw new InvalidDataException($"{p.Image}: {err}");
        });
        var images = new List<TextureImport.DdsImage>();
        foreach (var (p, img) in plan.Zip(encoded))
        {
            images.Add(img);
            Need(p.NewName);
            foreach (var n in new[] { "NeverStream", "BoolProperty", "IntProperty", "ByteProperty", "EPixelFormat", img.Format }) Need(n);
        }
        var tw = new TagWriter(pkg, addNames);
        var add = new List<NewExport>();
        for (int i = 0; i < plan.Count; i++)
        {
            object?[] args = [pkg, plan[i].Template, images[i], tw, false, null];
            var build = (Func<long, byte[]>?)BuilderMethod.Invoke(null, args) ?? throw new InvalidDataException($"texture {plan[i].NewName}: {args[5]}");
            int t = plan[i].Template;
            byte[] entry = pkg.Body.AsSpan(pkg.ExportEntryStart[t], pkg.ExportEntryEnd[t] - pkg.ExportEntryStart[t]).ToArray();
            int nameIndex = Array.FindIndex(pkg.Names, x => x.Equals(plan[i].NewName, StringComparison.OrdinalIgnoreCase));
            if (nameIndex < 0) nameIndex = pkg.Names.Length + addNames.FindIndex(x => x.Equals(plan[i].NewName, StringComparison.OrdinalIgnoreCase));
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), nameIndex);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 0);
            add.Add(new NewExport(t, 0, build) { Entry = entry });
        }
        byte[] withTextures = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), add, out var written, addNames);
        var problems = PackageRebuilder.Verify(pkg, withTextures, Array.Empty<int>(), add, written, addNames);
        var p1 = Package.FromBytes(withTextures);
        for (int i = 0; i < plan.Count; i++)
        {
            var t = pkg.Exports[plan[i].Template];
            string outer = t.OuterIndex > 0 ? pkg.PathOf(pkg.Exports[t.OuterIndex - 1]) + "." : "";
            problems.AddRange((List<string>)CheckMethod.Invoke(null, [p1, pkg.Exports.Length + i, outer + plan[i].NewName, images[i]])!);
        }
        if (problems.Count > 0) throw new InvalidDataException("adding the textures failed its check: " + string.Join("; ", problems.Take(5)));
        return withTextures;
    }

    /// <summary>One material instance per MFF material: a renamed copy of the template ("mff_&lt;material&gt;_mat", ExportCopy)
    /// with its texture references replaced by that material's new textures (the reflection image stays the stock one).</summary>
    static (byte[] Package, Dictionary<string, int> Refs) CopyInstances(Package pkg, int mic, string templateMic, Dictionary<string, int> slots,
        IReadOnlyList<MffMaterial> mats, Dictionary<string, string> shared, TemplateKind kind, byte[] withTextures)
    {
        string PathOfNew(Package p, string newName) =>
            p.PathOf(p.Exports.First(e => e.ObjectName.Equals(newName, StringComparison.OrdinalIgnoreCase) && p.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)));
        byte[] current = withTextures;
        var refs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mats)
        {
            var cur = Package.FromBytes(current);
            int src = Array.FindIndex(cur.Exports, e => cur.PathOf(e).Equals(pkg.PathOf(pkg.Exports[mic]), StringComparison.OrdinalIgnoreCase));
            var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (slot, tex) in slots)
            {
                if (tex == kind.DiffuseTex && !Is(slot, "diffusetex")) continue;   // follows the diffuse
                if (Is(slot, "reflectiontex")) continue;   // stock image kept
                string newName = shared.TryGetValue(slot, out var sn) ? sn
                    : Is(slot, "diffusetex") ? $"{P}{Safe(m.Name)}_diff"
                    : Is(slot, "normaltex") ? $"{P}{Safe(m.Name)}_norm"
                    : Is(slot, "speccolortex") && kind.MetalStyle ? $"{P}{Safe(m.Name)}_speccol"
                    : Is(slot, "emissive") && kind.GlowSlot ? $"{P}{Safe(m.Name)}_glow"
                    : $"{P}{Safe(m.Name)}_spec";
                replace[pkg.PathOf(pkg.Exports[tex])] = PathOfNew(cur, newName);
            }
            string micName = $"{P}{Safe(m.Name)}_mat";
            var copy = MaterialChoice.Explained(() => ExportCopy.Copy(cur, src, cur, Array.Empty<string>(), micName, replace), out string why)
                ?? throw new InvalidDataException($"copying {templateMic} as {micName} failed: {why}");
            var check = copy.Check(copy.Output);
            if (check.Count > 0) throw new InvalidDataException($"{micName}: " + string.Join("; ", check.Take(5)));
            current = copy.Output;
            refs[m.Name] = copy.RootRef;
        }
        return (current, refs);
    }
}
