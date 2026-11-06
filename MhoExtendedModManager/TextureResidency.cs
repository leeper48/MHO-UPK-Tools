using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Textures with no .tfc behind them kept loaded (NeverStream) in packages for characters that aren't the player's hero
/// (Kurt, 2026-11-06: Luna Snow, a mod by Miike79, drew flat lilac / grey as a vanity pet, textured as the Iceman costume).
/// Her textures hold every mip inside the package (TextureFileCacheName none) but say NeverStream = false; Storm AvX's
/// (WIzzer's, textured as a pet) say true. A hero's own textures are always resident, so the flag doesn't matter there; a
/// pet's or team-up's are streamed, and these have nothing to stream from. Setting the flag on the pet copy (nothing else
/// changed) made her textured in game. Full mip chains alone didn't (tested first).
/// </summary>
static class TextureResidency
{
    /// <summary>The package with NeverStream set on every Texture2D that has no texture cache; null when none needed it.</summary>
    public static byte[]? KeepLoaded(byte[] package, List<string> log)
    {
        var pkg = Package.FromBytes(package);
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return pkg.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var changed = new List<string>();
        for (int e = 0; e < pkg.Exports.Length; e++)
        {
            var ex = pkg.Exports[e];
            if (!pkg.ClassOf(ex).Equals("Texture2D", StringComparison.OrdinalIgnoreCase) || ex.SerialSize <= 0) continue;
            byte[] d = pkg.ReadExportBytes(ex).ToArray();
            var tags = TagWalker.Walk(pkg, d, 4);
            if (tags == null) continue;
            // A cache name other than None: its big mips live in a .tfc and stream from there (stock textures).
            if (tags.FirstOrDefault(t => t.Name.Equals("TextureFileCacheName", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } cache
                && !TagWalker.NameAt(pkg, d, cache.ValueAt).Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (tags.FirstOrDefault(t => t.Name.Equals("NeverStream", StringComparison.OrdinalIgnoreCase)) is { } ns)
            {
                if (d[ns.ValueAt - 1] != 0) continue;   // already true (the bool's value byte sits just before ValueAt)
                d[ns.ValueAt - 1] = 1;
            }
            else
            {
                // name (8), type (8), size 0 (4), array index 0 (4), value 1 (1): before None.
                var tag = new byte[25];
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("NeverStream"));
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("BoolProperty"));
                tag[24] = 1;
                d = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
            }
            byte[] nd = d;
            replace[e] = _ => nd;
            changed.Add(ex.ObjectName);
        }
        if (replace.Count == 0) return null;
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("textures kept loaded didn't verify: " + string.Join("; ", problems.Take(2)));
        log.Add($"textures kept loaded (no texture cache; a pet or team-up streams its textures): {changed.Count} ({string.Join(", ", changed.Take(8))}{(changed.Count > 8 ? " …" : "")})");
        return output;
    }
}
