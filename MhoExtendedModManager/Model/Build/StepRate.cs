using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Steps that match the Size in Game (Kurt, 2026-10-05: "change the timing of the run animations so the feet move in sync with
/// the bigger or smaller size"). The game moves a character at its own speed whatever its size, so a character twice as big
/// covers the ground in half as many of its (twice as long) strides: its movement animations play at 1 / size. The run is one
/// sync group in the hero animation tree (biped_lib.pc_at_v2: movement_run leads, the arm / female / heavy layers follow it),
/// so every movement_* and add_movement_* animation the costume plays gets the same RateScale (UE3's AnimSequence play-rate
/// multiplier; no stock animation sets one, so it's the class default 1 everywhere).
///
/// Only the costume's package changes, as the Animations tab's swaps do: animations it gets from elsewhere (its hero's sets)
/// are copied into a set of its own (AnimSwap.Build) with the rate; ones already in its own swap sets (an earlier size, or a
/// swap from the Animations tab) get the rate in place, so sizing again replaces the rate instead of adding copies.
/// </summary>
static class StepRate
{
    /// <summary>A step cycle or one of its layers: movement_* / add_movement_*, but not a landing or jump (one-off moves, not
    /// steps: slowing them only drags them out).</summary>
    public static bool IsMovement(string name) =>
        (name.StartsWith("movement_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("add_movement_", StringComparison.OrdinalIgnoreCase))
        && !name.Contains("landing", StringComparison.OrdinalIgnoreCase) && !name.Contains("jump", StringComparison.OrdinalIgnoreCase);

    /// <summary>The package with its movement animations at 1 / <paramref name="size"/>; null when there was nothing to do or it
    /// couldn't be done (the log says why).</summary>
    public static byte[]? Apply(string packagePath, string packageFile, float size, string? cooked, Action<string> log)
    {
        float rate = 1f / size;
        var mine = CostumeAnims.Read(packagePath, packageFile, CostumeAnims.FilesFor(null, cooked));
        if (mine == null) { log($"steps:   {packageFile}: no character mesh component, so no animations to time"); return null; }
        var moves = mine.Anims.Where(a => IsMovement(a.Name) && a.From.File != null && a.Export >= 0).ToList();
        if (moves.Count == 0) { log($"steps:   {packageFile} plays no movement animations"); return null; }
        string full = Path.GetFullPath(packagePath);
        bool Own(CostumeAnims.Anim a) => Path.GetFullPath(a.From.File!).Equals(full, StringComparison.OrdinalIgnoreCase) && a.From.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase);
        var own = moves.Where(Own).ToList();
        var copy = moves.Where(a => !Own(a)).ToList();
        if (copy.Count == 0 && own.Count == 0) return null;
        if (Math.Abs(rate - 1) < 1e-4 && copy.Count > 0 && own.Count == 0) return null;   // 100 %: nothing of ours to put back

        byte[] bytes = File.ReadAllBytes(packagePath);
        if (own.Count > 0)
        {
            bytes = SetRates(Package.FromBytes(bytes), own.Select(a => a.Export).ToList(), rate);
            log($"steps:   {own.Count} movement animation(s) already in the costume's own sets now play at {rate * 100:0} %");
        }
        if (copy.Count > 0 && Math.Abs(rate - 1) > 1e-4)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"mho_steps_{Guid.NewGuid():N}_{packageFile}");
            try
            {
                File.WriteAllBytes(tmp, bytes);
                var swaps = copy.Select(a => new AnimSwap.Swap(a.Name, a.From.File!, a.From.Export, a.Export)).ToList();
                var slog = new List<string>();
                bytes = AnimSwap.Build(tmp, mine.Class, swaps, slog, mine.Inherited ? [.. mine.Sets.Select(s => s.Path)] : null, rate);
                log($"steps:   {copy.Count} movement animation(s) ({string.Join(", ", copy.Select(a => a.Name).Take(6))}{(copy.Count > 6 ? " …" : "")}) copied into the costume's own set at {rate * 100:0} % speed");
            }
            catch (InvalidDataException ex) { log("steps:   the movement animations weren't timed: " + ex.Message); return own.Count > 0 ? bytes : null; }
            finally { try { File.Delete(tmp); } catch (IOException) { } }
        }
        return bytes;
    }

    /// <summary>RateScale set on these AnimSequence exports (0-based), replaced or added before None; verified.</summary>
    public static byte[] SetRates(Package pkg, IReadOnlyList<int> exports, float rate)
    {
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return pkg.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var replace = new Dictionary<int, Func<long, byte[]>>();
        foreach (int e in exports.Distinct())
        {
            byte[] d = pkg.ReadExportBytes(pkg.Exports[e]).ToArray();
            byte[] nd = WithRate(pkg, d, rate, NameIdx);
            replace[e] = _ => nd;
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the timed package didn't verify: " + string.Join("; ", problems.Take(3)));
        return output;
    }

    /// <summary>An AnimSequence's bytes with RateScale = <paramref name="rate"/> (its tag replaced, or added before None).</summary>
    public static byte[] WithRate(Package pkg, byte[] d, float rate, Func<string, int> nameIdx)
    {
        var tags = TagWalker.Walk(pkg, d, 4) ?? throw new InvalidDataException("an animation's properties don't read");
        var t = tags.FirstOrDefault(x => x.Name.Equals("RateScale", StringComparison.OrdinalIgnoreCase) && x.Size == 4);
        if (t != null) { var c = (byte[])d.Clone(); BinaryPrimitives.WriteSingleLittleEndian(c.AsSpan(t.ValueAt), rate); return c; }
        var tag = new byte[28];   // name, type, size 4, array index 0, the value
        BinaryPrimitives.WriteInt32LittleEndian(tag, nameIdx("RateScale"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), nameIdx("FloatProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
        BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(24), rate);
        return [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
    }
}
