using MhoPackageModifier;
using MhoExtendedModManager.Fx;

namespace MhoExtendedModManager;

/// <summary>
/// Dark power colors for additive effects (Kurt, 2026-10-15: Dark worked on Nova's Magneton Pulsar, a translucent field, but
/// made Nova Beam invisible: 7 of its 9 materials are additive, and an additive effect adds its light, so a dark color adds
/// nothing). For a dark color, each additive sprite / beam / trail emitter of the package gets a translucent material: a
/// copy of an existing game material instance whose shaders are compiled (Vision's vfx_vision_b.atomicfootdive.mat_ring1:
/// parent vfx_shared_materials_a.mat_masters.mat_greytrans_, translucent, depth-biased alpha off; that parent is made for
/// sprites, beams, trails and sub-images, not mesh particles), renamed, with a texture of its own: the emitter's texture's
/// shape (its brightness, times its alpha where that varies, times the opacity) in every channel, so whichever channel the
/// material reads gives the shape. The particle colors then draw it dark. Tested in game on Nova's Magneton Pulsar (test
/// I: the swap renders; dim until the shape is strong enough). Mesh emitters (their meshes' own materials) and distortion
/// effects are left as they are.
/// </summary>
static class DarkSwap
{
    public const string TemplatePackage = "UC__PowerVision_AtomicFootDive_SF.upk";
    const string TemplateMic = "vfx_vision_b.atomicfootdive.mat_ring1";
    const string TemplateTex = "vfx_shared_textures.textures.tex_ring_faded_clamp";

    /// <summary>A color dark enough that additive effects would vanish (they're swapped then).</summary>
    public static bool Wanted(PowerColor c) => c.Brightness <= 0.25f;

    /// <summary>The package with its additive emitters drawn translucent (see the class); null when there's nothing to swap.</summary>
    public static byte[]? Apply(byte[] package, string file, string cooked, float opacity, Action<string> log)
    {
        string? templateFile = StockFiles.For(cooked, TemplatePackage);
        if (templateFile == null || !File.Exists(templateFile)) { log($"dark: {TemplatePackage} isn't in the game folder; additive effects stay additive"); return null; }
        var src = Package.Open(templateFile);
        int micIdx = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(TemplateMic, StringComparison.OrdinalIgnoreCase));
        int texIdx = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(TemplateTex, StringComparison.OrdinalIgnoreCase));
        if (micIdx < 0 || texIdx < 0) { log("dark: the template material isn't in its package; additive effects stay additive"); return null; }

        var pkg = Package.FromBytes(package);
        var t = new FxTables(pkg);
        var fx = new FxPkg(file, pkg.Body, t);
        // the packages that hold what this one imports (materials and textures of the shared effect packages), so their
        // textures can be found and read
        var exportIx = ImportIndex.Exported(UniqueNames.Folder(cooked));
        var extra = new List<FxPkg> { fx };
        foreach (var im in pkg.Imports)
        {
            string c = im.ClassName.ToLowerInvariant();
            if (c is not ("texture2d" or "material" or "materialinstanceconstant")) continue;
            if (!exportIx.TryGetValue($"{ImportIndex.PathOf(pkg, im)}|{c}", out var owner) || extra.Any(x => x.Name.Equals(owner, StringComparison.OrdinalIgnoreCase))) continue;
            if (StockFiles.For(cooked, owner) is string of && File.Exists(of))
                try { extra.Add(FxPkg.Open(of)); } catch (Exception e) when (e is IOException or InvalidDataException or PackageFormatException) { }
        }
        // the always-loaded packages too: the shared effect materials and textures (mat_glow_b, tex_cloud_basic …) are in
        // MarvelGame.upk, which every power package imports from
        foreach (string always in new[] { "MarvelGame.upk", "Startup.upk" })
            if (StockFiles.For(cooked, always) is string af && File.Exists(af))
                try { extra.Add(FxPkg.Open(af)); } catch (Exception e) when (e is IOException or InvalidDataException or PackageFormatException) { }
        var tex = new FxTextures(extra, cooked);
        // each LOD level's required module and type data (a mesh emitter draws its meshes' own materials)
        var typeOf = new Dictionary<int, string>();
        for (int i = 0; i < t.Exports.Count; i++)
        {
            if (!t.ClassOf(t.Exports[i]).Equals("ParticleLODLevel", StringComparison.OrdinalIgnoreCase)) continue;
            var props = FxProps.Find(pkg.Body, t, t.Exports[i])?.Props ?? [];
            int Ref(string n) => props.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && x.Size == 4) is { } p ? BitConverter.ToInt32(pkg.Body, p.ValueAt) : 0;
            int req = Ref("RequiredModule"), td = Ref("TypeDataModule");
            if (req > 0) typeOf[req - 1] = td > 0 ? t.ClassOf(t.Exports[td - 1]) : td < 0 ? t.Imports[-td - 1].ObjectName : "";
        }
        // the required modules' materials (several emitters can share one)
        var byMaterial = new Dictionary<int, List<(int Export, int ValueAt, bool Sub)>>();
        var beamMaterials = new HashSet<int>();
        var skipped = new List<string>();
        for (int i = 0; i < t.Exports.Count; i++)
        {
            if (!t.ClassOf(t.Exports[i]).Equals("ParticleModuleRequired", StringComparison.OrdinalIgnoreCase)) continue;
            if (typeOf.TryGetValue(i, out var td) && td.Length > 0 && !td.Contains("Beam", StringComparison.OrdinalIgnoreCase) && !td.Contains("Trail", StringComparison.OrdinalIgnoreCase))
            { skipped.Add($"{t.PathOf(i + 1).Split('.')[^1]} ({td})"); continue; }
            var props = FxProps.Find(pkg.Body, t, t.Exports[i])?.Props ?? [];
            if (props.FirstOrDefault(x => x.Name.Equals("Material", StringComparison.OrdinalIgnoreCase) && x.Size == 4) is not { } mp) continue;
            int mref = BitConverter.ToInt32(pkg.Body, mp.ValueAt);
            if (mref == 0) continue;
            if (Environment.GetEnvironmentVariable("MHO_DARK_DEBUG") == "1")
                log($"dark debug: {t.PathOf(i + 1)} [{(typeOf.TryGetValue(i, out var tdd) ? tdd : "?")}] material {t.PathOf(mref)}");
            bool sub = props.Any(x => (x.Name.Equals("SubImages_Horizontal", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("SubImages_Vertical", StringComparison.OrdinalIgnoreCase)) && x.Value is not ("1" or "0"));
            if (typeOf.TryGetValue(i, out var tdb) && (tdb.Contains("Beam", StringComparison.OrdinalIgnoreCase) || tdb.Contains("Trail", StringComparison.OrdinalIgnoreCase))) beamMaterials.Add(mref);
            if (!byMaterial.TryGetValue(mref, out var l)) byMaterial[mref] = l = [];
            l.Add((i, mp.ValueAt - t.Exports[i].SerialOffset, sub));
        }
        // which are additive, and their shapes
        var swaps = new List<(int Material, string Name, byte[] Dds, List<(int Export, int At)> Users)>();
        string temp = Path.Combine(Path.GetTempPath(), "mhoextmm_dark_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var (mref, users) in byMaterial)
            {
                var pick = tex.ParticleTexture(fx, mref, users.Any(u => u.Sub), out bool additive);
                if (!additive)
                    try { additive = AddBlend(tex, fx, pkg, mref, cooked); }
                    catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException or PackageFormatException)
                    { if (Environment.GetEnvironmentVariable("MHO_DARK_DEBUG") == "1") log($"dark debug: {t.PathOf(mref)}: add-blend check failed: {ex.Message}"); }
                if (Environment.GetEnvironmentVariable("MHO_DARK_DEBUG") == "1") log($"dark debug: {t.PathOf(mref)}: additive {additive}, texture {(pick == null ? "none" : pick.Value.P.T.PathOf(pick.Value.Export + 1))}");
                if (!additive) continue;
                string mname = t.PathOf(mref).Split('.')[^1].ToLowerInvariant();
                if (mname.Contains("distort") || mname.Contains("warp") || mname.Contains("refract")) { skipped.Add($"{mname} (distortion)"); continue; }
                // no shape texture (a solid beam: mat_beam_generic_solid only has a noise map, which isn't its shape): a solid shape
                // beams and trails: a solid shape too (Kurt, 2026-10-15: Nova Beam's solid beam showed dark, Minor Beam's textured
                // ones (an energy texture, a gradient its material pans along the beam) stayed invisible: the replacement
                // samples the texture without the original's panning and tiling)
                var d = pick == null ? (new byte[64 * 64 * 4].Select(_ => (byte)255).ToArray(), 64, 64) : tex.Decoded(pick.Value.P, pick.Value.Export);
                if (d == null) { log($"dark: {t.PathOf(mref)}: its texture can't be read; left additive"); continue; }
                string png = Path.Combine(temp, $"shape{swaps.Count}.png");
                WriteShape(d.Value.Bgra, d.Value.W, d.Value.H, opacity, png, crossSection: beamMaterials.Contains(mref) && pick != null);
                var enc = TextureEncode.FromImage(png, "dxt5", 85, 1f);
                string name = t.PathOf(mref).Split('.')[^1];
                swaps.Add((mref, name, TextureImport.WriteDds(enc), [.. users.Select(u => (u.Export, u.ValueAt))]));
            }
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
        if (skipped.Count > 0) log($"dark: left as they are: {string.Join(", ", skipped.Distinct())}");
        if (swaps.Count == 0) return null;

        // copy the template once per swapped material: its texture (renamed, then given the shape), then the instance
        // (renamed, pointing at that texture)
        byte[] cur = package;
        var newRefs = new List<int>();
        string texOuter = TemplateTex[..TemplateTex.LastIndexOf('.')];
        for (int k = 0; k < swaps.Count; k++)
        {
            string texName = $"tex_dark_{k}_{swaps[k].Name}", micName = $"mic_dark_{k}_{swaps[k].Name}";
            var dst = Package.FromBytes(cur);
            var tc = ExportCopy.Copy(src, texIdx, dst, [], rename: texName) ?? throw new InvalidDataException($"dark: the texture copy for {swaps[k].Name} failed");
            string texPath = texOuter + "." + texName;
            cur = TextureImport.ReplaceMany(Package.FromBytes(tc.Output), [new TextureImport.Replacement(texPath, swaps[k].Dds, texName)], out var problems, out _)
                ?? throw new InvalidDataException("dark: " + string.Join("; ", problems));
            var mc = ExportCopy.Copy(src, micIdx, Package.FromBytes(cur), [], rename: micName, replaceRefs: new Dictionary<string, string> { [TemplateTex] = texPath })
                ?? throw new InvalidDataException($"dark: the material copy for {swaps[k].Name} failed");
            cur = mc.Output;
            newRefs.Add(mc.RootRef);
        }
        // the emitters to their new materials (their exports keep their indices: copies are appended)
        var final = Package.FromBytes(cur);
        var changed = new Dictionary<int, Func<long, byte[]>>();
        for (int k = 0; k < swaps.Count; k++)
            foreach (var (exp, at) in swaps[k].Users)
            {
                byte[] d = changed.TryGetValue(exp, out var f) ? f(0) : final.ReadExportBytes(final.Exports[exp]).ToArray();
                BitConverter.GetBytes(newRefs[k]).CopyTo(d, at);
                var dd = d; changed[exp] = _ => dd;
            }
        log($"dark: {swaps.Count} additive material(s) drawn translucent instead ({swaps.Sum(s => s.Users.Count)} emitter(s)): {string.Join(", ", swaps.Select(s => s.Name))}");
        return PackageRebuilder.Rebuild(final, changed, [], out _);
    }

    /// <summary>
    /// A translucent material that adds anyway (Kurt, 2026-10-15: Minor Beam's beam stayed invisible): its base material
    /// runs its color through the material function matfunc_addblend (Iron Man's repulsor beam material on Nova), and when
    /// the base has an "addblend" static switch, the instance turns it on (mat_greytrans_'s "_ab" instances, such as
    /// mat_flashstarsoft_ab_dba). A dark color makes such an effect see-through, like an additive one.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> addBlendSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For the 3D preview (Kurt, 2026-10-15: Minor Beam's repulsor beam looked faint there): whether a particle material
    /// adds its light although it's translucent (see <see cref="AddBlend"/>), read from the game's files, remembered.</summary>
    public static bool AddBlendLook(FxTextures tex, FxPkg p, int mref, string cooked)
    {
        if (mref == 0) return false;
        string key = p.Name + "|" + p.T.PathOf(mref);
        if (addBlendSeen.TryGetValue(key, out bool hit)) return hit;
        bool r;
        try { r = AddBlend(tex, p, null, mref, cooked); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException or PackageFormatException or IOException)
        { r = false; if (Environment.GetEnvironmentVariable("MHO_DARK_DEBUG") == "1") Console.WriteLine($"    preview: {key}: {ex.Message}"); }
        if (r && Environment.GetEnvironmentVariable("MHO_DARK_DEBUG") == "1") Console.WriteLine($"    preview: {key} adds (add-blend)");
        return addBlendSeen[key] = r;
    }

    static bool AddBlend(FxTextures tex, FxPkg fx, Package? pkg, int mref, string cooked)
    {
        bool? switchOn = null;
        var at = tex.Resolve(fx, mref);
        for (int depth = 0; depth < 8 && at != null; depth++)
        {
            var (q, i) = at.Value;
            var e = q.T.Exports[i];
            string cls = q.T.ClassOf(e);
            Package? qp = q == fx && pkg != null ? pkg : StockFiles.For(cooked, q.Name) is string qf && File.Exists(qf) ? TryOpen(qf) : null;
            if (cls.StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase))
            {
                if (switchOn == null && qp != null)
                    foreach (string sw in ExportCopy.StaticSwitches(qp, qp.ReadExportBytes(qp.Exports[i]).ToArray()))
                        if (sw.Contains(": addblend =", StringComparison.OrdinalIgnoreCase)) { switchOn = sw.Contains("= true", StringComparison.OrdinalIgnoreCase); break; }
                var props = FxProps.Find(q.Bytes, q.T, e)?.Props ?? [];
                var parent = props.FirstOrDefault(x => x.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase));
                at = parent == null ? null : tex.Resolve(q, BitConverter.ToInt32(q.Bytes, parent.ValueAt));
                continue;
            }
            // the base material: does it call matfunc_addblend, and has it an addblend switch?
            bool calls = false, hasSwitch = false;
            // (a cooked material lists the functions it calls in MaterialFunctionInfos: { StateId, Function } each)
            var own = FxProps.Find(q.Bytes, q.T, e)?.Props ?? [];
            if (own.FirstOrDefault(x => x.Name.Equals("MaterialFunctionInfos", StringComparison.OrdinalIgnoreCase)) is { } mfi)
                for (int o = mfi.ValueAt; o + 4 <= mfi.ValueAt + mfi.Size && !calls; o++)
                {
                    int r = BitConverter.ToInt32(q.Bytes, o);
                    if (r == 0 || r == int.MinValue || r > q.T.Exports.Count || -r > q.T.Imports.Count) continue;
                    if ((r > 0 ? q.T.ClassOf(q.T.Exports[r - 1]) : q.T.Imports[-r - 1].ClassName).Equals("MaterialFunction", StringComparison.OrdinalIgnoreCase)
                        && q.T.PathOf(r).Contains("addblend", StringComparison.OrdinalIgnoreCase)) calls = true;
                }
            for (int k = 0; k < q.T.Exports.Count; k++)
            {
                if (q.T.Exports[k].Outer != i + 1) continue;
                string ec = q.T.ClassOf(q.T.Exports[k]);
                var props = FxProps.Find(q.Bytes, q.T, q.T.Exports[k])?.Props ?? [];
                if (ec.Equals("MaterialExpressionMaterialFunctionCall", StringComparison.OrdinalIgnoreCase)
                    && props.FirstOrDefault(x => x.Name.Equals("MaterialFunction", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("Function", StringComparison.OrdinalIgnoreCase)) is { Size: 4 } fp
                    && q.T.PathOf(BitConverter.ToInt32(q.Bytes, fp.ValueAt)).Contains("addblend", StringComparison.OrdinalIgnoreCase)) calls = true;
                if (ec.Equals("MaterialExpressionStaticSwitchParameter", StringComparison.OrdinalIgnoreCase)
                    && props.FirstOrDefault(x => x.Name.Equals("ParameterName", StringComparison.OrdinalIgnoreCase))?.Value.Equals("addblend", StringComparison.OrdinalIgnoreCase) == true) hasSwitch = true;
            }
            return calls && (!hasSwitch || switchOn == true);
        }
        return false;
    }

    static Package? TryOpen(string f) { try { return Package.Open(f); } catch (Exception e) when (e is IOException or InvalidDataException or PackageFormatException) { return null; } }

    /// <summary>The shape of an additive texture (its brightness, times its alpha where that varies, times the opacity) in
    /// every channel.</summary>
    /// <param name="crossSection">A beam or trail (Kurt, 2026-10-15: a solid shape filled the whole beam sheet): the shape's
    /// profile across the beam (each row averaged along its length, the brightest row at full strength), the same all along it,
    /// so it keeps the narrow core without the scrolling the original material gives its texture.</param>
    static void WriteShape(byte[] bgra, int w, int h, float opacity, string png, bool crossSection = false)
    {
        var shape = Shape(bgra, w, h, opacity, crossSection);
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var outp = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) outp[i * 4] = outp[i * 4 + 1] = outp[i * 4 + 2] = outp[i * 4 + 3] = shape[i];
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(outp, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        bmp.UnlockBits(bd);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>The dark shape of an additive texture, one byte per pixel (shared with the 3D preview, Kurt 2026-10-15: render
    /// the swapped materials there too): its brightness, times its alpha where that really varies, times the opacity; for a
    /// beam or trail the profile across it (see <see cref="WriteShape"/>).</summary>
    public static byte[] Shape(byte[] bgra, int w, int h, float opacity, bool crossSection)
    {
        // alpha is a mask only when it really varies: additive textures often leave it nearly empty (tex_beam_01: 0-3,
        // tex_energy_a_low: 0-17), which multiplied every shape to nothing (the preview ignores such alpha too)
        int amin = 255, amax = 0;
        for (int k = 3; k < bgra.Length; k += 4) { amin = Math.Min(amin, bgra[k]); amax = Math.Max(amax, bgra[k]); }
        bool alphaVaries = amax - amin > 32;
        var shape = new byte[w * h];
        float boost = Math.Max(1f, opacity);
        float Raw(int i) { int o = i * 4; return Math.Max(bgra[o], Math.Max(bgra[o + 1], bgra[o + 2])) / 255f * (alphaVaries ? bgra[o + 3] / 255f : 1f); }
        float[]? rows = null;
        if (crossSection)
        {
            rows = new float[h];
            for (int y = 0; y < h; y++) { float sum = 0; for (int x = 0; x < w; x++) sum += Raw(y * w + x); rows[y] = sum / w; }
            float top = rows.Max();
            // steeper toward the edges (the 4th power) and not boosted: the particles' alpha is already multiplied by the
            // opacity, so a soft profile filled most of the sheet (Kurt, 2026-10-15: Minor Beam was still a wide plane;
            // measured under 5x alpha: tex_energy_a_low 85 % of the width, tex_grad_jet_clamped 74 %; now 35 % and 42 %)
            if (top > 1e-4f) for (int y = 0; y < h; y++) rows[y] = MathF.Pow(rows[y] / top, 4);
        }
        for (int i = 0; i < w * h; i++)
        {
            float v = rows != null ? rows[i / w] : Raw(i) * boost;
            shape[i] = (byte)Math.Clamp((int)(v * 255 + 0.5f), 0, 255);
        }
        return shape;
    }
}
