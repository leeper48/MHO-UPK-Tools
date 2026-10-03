using System.Buffers.Binary;
using System.Globalization;
using MpmPackage = MhoPackageModifier.Package;

namespace MhoMffImporter;

/// <summary>
/// Which stock material instance the MFF materials copy, and bringing it into the base package.
/// Static switches are baked into the game's shaders, so a material can only be a copy of an instance that exists; its
/// scalar / vector values are plain numbers and can change.
/// <list type="bullet">
/// <item>Default (0.6.7, Kurt): Punisher Modern VU's instance (chbasematerial_v2; looked right in game where the base mesh's own
/// v1 Punisher Classic material gave a sheen).</item>
/// <item>Metal (0.7.4): Angela's armour instance when over half of the model's _sp texels are metal (Iron Man S01 86 %, Gamora
/// S02 21 %, Punisher S07 6 %).</item>
/// <item>Metal with glow spots (0.7.7): Angela's weapons_1602_mtl (glow switch and slot) with her armour instance's values.</item>
/// </list>
/// </summary>
static class MaterialChoice
{
    public const string Default = "UC__MarvelPlayer_Punisher_ModernVU_SF:punisher_modernvu_testmat";
    public const string Metal = "UC__MarvelPlayer_Angela_SF:angela_std_v2-1";
    public const string Glow = "UC__MarvelPlayer_Angela_SF:weapons_1602_mtl";

    public static bool IsAutomatic(string donor) => donor is Default or Metal or Glow;

    /// <summary>The instance to copy: the requested one ("base" = null = the base mesh's own), else by metal / glow share.</summary>
    public static string? Donor(string? requested, float metalShare, float glowShare) => requested is { Length: > 0 } v
        ? (v.Equals("base", StringComparison.OrdinalIgnoreCase) ? null : v)
        : metalShare > 0.5f ? (glowShare > 0.001f ? Glow : Metal) : Default;

    /// <summary>Share of the _sp texels MFF marks as metal (blue over 85, i.e. (blue − 60) / 50 over a half). Older _sp maps
    /// (0.10.15; Spider-Man, Storm, Colossus, Black Cat S01) keep blue at 160-255 everywhere, a different layout with no metal
    /// mask: they count as no metal (they read as 100 % and every such model got the metal material).</summary>
    public static float MetalShare(IEnumerable<string> spMaps)
    {
        long all = 0, metal = 0;
        foreach (var f in spMaps)
        {
            using var b = new System.Drawing.Bitmap(f);
            bool old = OldLayout(b);
            for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) { all++; if (!old && b.GetPixel(x, y).B > 85) metal++; }
        }
        return all > 0 ? (float)metal / all : 0;
    }

    /// <summary>The older _sp layout: blue's low end (5th percentile, sampled) above 140 (old maps: 160-180; metal-mask maps: 0).</summary>
    public static bool OldLayout(System.Drawing.Bitmap b)
    {
        var vals = new List<int>();
        int sx = Math.Max(1, b.Width / 64), sy = Math.Max(1, b.Height / 64);
        for (int y = 0; y < b.Height; y += sy) for (int x = 0; x < b.Width; x += sx) vals.Add(b.GetPixel(x, y).B);
        vals.Sort();
        return vals.Count > 0 && vals[vals.Count / 20] > 140;
    }

    /// <summary>Share of the colour-map texels painted near-white or bright cyan (MaterialOut's glow test; Iron Man S01 0.6 %).</summary>
    public static float GlowShare(IEnumerable<string> colourMaps)
    {
        long all = 0, lit = 0;
        foreach (var f in colourMaps)
        {
            using var b = new System.Drawing.Bitmap(f);
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    var c = b.GetPixel(x, y); all++;
                    if ((c.R + c.G + c.B) / 3f > 200 || (c.B > 180 && c.G > 170 && c.R < 160)) lit++;
                }
        }
        return all > 0 ? (float)lit / all : 0;
    }

    /// <summary>
    /// Copies the donor instance into the base package as "mff_template_mat" (with its parent import and textures, verified)
    /// and returns the package. Values from another instance (0.7.6, Kurt: the glow build's chest lost reflection, and its glow
    /// was subtle): the template keeps its own switches (Angela's weapons_1602_mtl: glow on) and takes another instance's
    /// values (her armour's angela_std_v2-1: reflectionmult 20 not 10, specularpowermask 25 not 255, phong / lambert diffuse
    /// 20 / 3 not 3 / 1 ...), only from an instance with the same parent (the expression GUIDs in the entries are the
    /// parent's). Entries only the template has are kept; emissivemultiplier = <paramref name="glow"/> (the weapon's is 1).
    /// </summary>
    public static byte[] CopyDonor(MpmPackage target, string donor, string? valuesFrom, float glow, Action<string> log)
    {
        var dparts = donor.Split(':', 2);
        string dfile = dparts[0].EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? dparts[0] : dparts[0] + ".upk";
        var donorPkg = MpmPackage.Open(BasePackage.Resolve(dfile));
        int di = Array.FindIndex(donorPkg.Exports, x => donorPkg.ClassOf(x).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase)
                                                && (dparts.Length < 2 || x.ObjectName.Equals(dparts[1], StringComparison.OrdinalIgnoreCase)));
        if (di < 0) throw new InvalidDataException($"no material instance {(dparts.Length > 1 ? dparts[1] : "")} in {dfile}");
        log($"material: template from {dfile}: {donorPkg.PathOf(donorPkg.Exports[di])} ({donorPkg.ClassOf(donorPkg.Exports[di])})");
        valuesFrom ??= donorPkg.Exports[di].ObjectName.Equals("weapons_1602_mtl", StringComparison.OrdinalIgnoreCase) ? "angela_std_v2-1" : null;
        if (!string.IsNullOrWhiteSpace(valuesFrom))
            donorPkg = MergeValues(donorPkg, di, valuesFrom, dfile, glow, log);
        var dc = MhoPackageModifier.ExportCopy.Copy(donorPkg, di, target, Array.Empty<string>(), "mff_template_mat")
            ?? throw new InvalidDataException($"copying {donorPkg.Exports[di].ObjectName} from {dfile} failed (see above)");
        var dcheck = dc.Check(dc.Output);
        if (dcheck.Count > 0) throw new InvalidDataException("template copy: " + string.Join("; ", dcheck.Take(5)));
        return dc.Output;
    }

    /// <summary>The donor package with instance <paramref name="di"/>'s scalar / vector values taken from <paramref name="valuesFrom"/>.</summary>
    static MpmPackage MergeValues(MpmPackage dk, int di, string valuesFrom, string dfile, float glow, Action<string> log)
    {
        int vi = Array.FindIndex(dk.Exports, x => dk.ClassOf(x).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase) && x.ObjectName.Equals(valuesFrom, StringComparison.OrdinalIgnoreCase));
        if (vi < 0) throw new InvalidDataException($"MFF_VALUES_FROM: no material instance {valuesFrom} in {dfile}");
        string Nm(long r) => TaggedProps.NameOf(dk.Names, r);
        byte[] td = dk.ReadExportBytes(dk.Exports[di]); int ta = 4; var tp = TaggedProps.Read(td, ref ta, Nm); var tail = td[ta..];
        byte[] sd = dk.ReadExportBytes(dk.Exports[vi]); int sa = 4; var sp = TaggedProps.Read(sd, ref sa, Nm);
        if (!(tp.Find("parent")?.Value ?? []).AsSpan().SequenceEqual(sp.Find("parent")?.Value ?? []))
            throw new InvalidDataException($"MFF_VALUES_FROM: {valuesFrom} has another parent than {dk.Exports[di].ObjectName}");
        string PN(TaggedProps el) => Nm(BinaryPrimitives.ReadInt64LittleEndian(el.Find("parametername")!.Value));
        foreach (var arr in new[] { "scalarparametervalues", "vectorparametervalues" })
        {
            var tt = tp.Find(arr); var st = sp.Find(arr);
            if (tt == null || st == null) continue;
            var tEl = tp.StructArray(tt); var sEl = sp.StructArray(st);
            var merged = sEl.ToList();
            merged.AddRange(tEl.Where(el => !sEl.Any(x => PN(x).Equals(PN(el), StringComparison.OrdinalIgnoreCase))));
            foreach (var el in merged.Where(q => PN(q).Equals("emissivemultiplier", StringComparison.OrdinalIgnoreCase)))
                BinaryPrimitives.WriteSingleLittleEndian(el.Find("parametervalue")!.Value, glow);
            log($"material: {arr}: {sEl.Count} from {valuesFrom}, {merged.Count - sEl.Count} kept from {dk.Exports[di].ObjectName}: " + string.Join(", ", merged.Select(el => PN(el) + (arr.StartsWith("scalar") ? "=" + BinaryPrimitives.ReadSingleLittleEndian(el.Find("parametervalue")!.Value).ToString("0.###", CultureInfo.InvariantCulture) : ""))));
            tt.Value = TaggedProps.StructArrayValue(merged);
        }
        byte[] nb = [.. td[..4], .. tp.Write(), .. tail];
        var merged2 = MpmPackage.FromBytes(PackageOut.ReplaceExport(dk, di, nb));
        int ra = 4; TaggedProps.Read(merged2.ReadExportBytes(merged2.Exports[di]), ref ra, r => TaggedProps.NameOf(merged2.Names, r));
        return merged2;
    }
}
