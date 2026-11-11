using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Texture mips listed smaller than 4 × 4 (Kurt, 2026-11-10: "Rendering thread exception: CreateTexture … SizeX=1, SizeY=1,
/// Format=DXT1 … D3DERR_INVALIDCALL" on a costume whose copied material had an 8 × 8 glow stand-in with mips 8, 4, 2, 1).
/// The game's own textures list their last mips as 4 × 4 (one DXT block; normalplaceholder: 4, 4, 4), and when it drops the
/// top mips of a small texture it creates one from the smallest it keeps: a 1 × 1 DXT1 one, which Direct3D refuses. MPM's
/// writer lists them as 4 × 4 since 2026-11-10; this fixes packages written before: only the mips' size fields change (the
/// data is one block already), so the package keeps its length.
/// </summary>
static class SmallMips
{
    /// <summary>The largest texture fixed (the long side).</summary>
    public const int MaxSize = 16;

    /// <summary>The package with every inline mip's size at least 4 × 4, or null when none is smaller; <paramref name="fixedTextures"/>
    /// names the textures changed.</summary>
    public static byte[]? Fix(Package pkg, out List<string> fixedTextures)
    {
        fixedTextures = [];
        var replace = new Dictionary<int, Func<long, byte[]>>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            if (!pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
            TextureInfo ti;
            try { ti = TextureInfo.Read(pkg, pkg.Exports[i]); }
            catch (PackageFormatException) { continue; }
            // (only tiny textures, 16 or less: the game drops up to about three top mips, so from 8 x 8 it reached 1 x 1; bigger
            // ones never get down to their tail, and hundreds of recolored and imported ones with 2 x 2 / 1 x 1 tails work in game)
            if (Math.Max(ti.SizeX, ti.SizeY) > MaxSize) continue;
            // (DXT only: its data is 4 x 4 blocks; uncompressed A8R8G8B8 ones list 2 x 2 and 1 x 1 mips in the game's own files)
            if (!ti.Format.Contains("DXT", StringComparison.OrdinalIgnoreCase)) continue;
            byte[]? d = null;
            foreach (var m in ti.Mips.Where(m => m.Inline && (m.Width < 4 || m.Height < 4)))
            {
                d ??= (byte[])ti.Data.Clone();
                int at = m.InlineAt + m.Size;
                BitConverter.TryWriteBytes(d.AsSpan(at), Math.Max(4, m.Width));
                BitConverter.TryWriteBytes(d.AsSpan(at + 4), Math.Max(4, m.Height));
            }
            if (d == null) continue;
            byte[] nd = d;
            replace[i] = _ => nd;
            fixedTextures.Add(pkg.Exports[i].ObjectName);
        }
        byte[]? output = null;
        if (replace.Count > 0)
        {
            output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);
            var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written);
            if (problems.Count > 0) throw new InvalidDataException("the fixed package doesn't check out: " + string.Join("; ", problems.Take(3)));
            pkg = Package.FromBytes(output);
        }
        // a texture marked TMGS_NoMipMaps with more than one level (Kurt, 2026-11-10: HEAD D, single-mip in the costume, got a
        // full chain from the Materials tab and the game crashed creating a 1 x 1 DXT1 texture): back to its top level, its
        // pixels as they are (TextureImport keeps every other setting)
        int noMips = Array.FindIndex(pkg.Names, n => n.Equals("TMGS_NoMipMaps", StringComparison.OrdinalIgnoreCase));   // (none: nothing marked)
        var single = new List<TextureImport.Replacement>();
        if (noMips >= 0)
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                if (!pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
                TextureInfo ti;
                try { ti = TextureInfo.Read(pkg, pkg.Exports[i]); }
                catch (PackageFormatException) { continue; }
                if (ti.Mips.Count <= 1 || !ti.Mips[0].Inline || !ti.Format.Contains("DXT", StringComparison.OrdinalIgnoreCase)) continue;
                // (only with a texture group the game doesn't have, as HEAD D's "Textures" from another tool: hundreds of recolored
                // effect textures marked No Mipmaps carry a chain under TEXTUREGROUP_Effects and work in game)
                var (mipGen, group) = Settings(pkg, ti.Data);
                if (!mipGen.Equals("TMGS_NoMipMaps", StringComparison.OrdinalIgnoreCase) || group.StartsWith("TEXTUREGROUP_", StringComparison.OrdinalIgnoreCase)) continue;
                var top = ti.Mips[0];
                var level = (top.Width, top.Height, ti.Data.AsSpan(top.InlineAt, top.Size).ToArray());
                string fourCC = ti.Format.Contains("DXT5", StringComparison.OrdinalIgnoreCase) ? "DXT5" : "DXT1";
                single.Add(new TextureImport.Replacement(pkg.PathOf(pkg.Exports[i]), TextureImport.WriteDds(new TextureEncode.Result(fourCC, top.Width, top.Height, [level])), "its own top level"));
                fixedTextures.Add(pkg.Exports[i].ObjectName + " (one level, as marked)");
            }
        if (single.Count > 0)
        {
            output = TextureImport.ReplaceMany(pkg, single, out var problems2, out var verify)
                ?? throw new InvalidDataException("one level: " + string.Join("; ", problems2.Take(3)));
            var check = verify(output);
            if (check.Count > 0) throw new InvalidDataException("one level doesn't check out: " + string.Join("; ", check.Take(3)));
        }
        return output;
    }

    /// <summary>A texture's MipGenSettings and LODGroup values ("" when not set), from its tagged properties.</summary>
    static (string MipGen, string Group) Settings(Package pkg, byte[] d)
    {
        string mipGen = "", group = "";
        string Name(ref int at) { int idx = BitConverter.ToInt32(d, at), num = BitConverter.ToInt32(d, at + 4); at += 8; return (uint)idx < (uint)pkg.Names.Length ? (num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx]) : "?"; }
        int p = 4;
        for (int guard = 0; guard < 64 && p + 24 <= d.Length; guard++)
        {
            string name = Name(ref p);
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase) || name == "?") break;
            string type = Name(ref p).ToLowerInvariant();
            int size = BitConverter.ToInt32(d, p); p += 8;
            if (type is "structproperty" or "byteproperty") Name(ref p);
            if (type == "boolproperty") p += 1;
            if (type == "byteproperty" && size == 8)
            {
                int q = p;
                if (name.Equals("MipGenSettings", StringComparison.OrdinalIgnoreCase)) mipGen = Name(ref q);
                else if (name.Equals("LODGroup", StringComparison.OrdinalIgnoreCase)) group = Name(ref q);
            }
            p += size;
        }
        return (mipGen, group);
    }
}
