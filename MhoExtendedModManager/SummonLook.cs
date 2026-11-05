using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// A summon's look from another summon (Kurt, 2026-11-05: summon-to-summon borrowing; prototype Doctor Doom's Unthinkable orb
/// as Squirrel Girl's squirrel). The target summon's own package (its class, GUID and settings: what it does stays the
/// game's) with the donor's model copied in (CrossMove.Build: mesh, materials, the component pointed at it) and the donor's
/// animations copied under the names the target plays (AnimSwap.Build: a set of its own, appended to the component's list),
/// plus every donor animation under its own name. Objects keep their paths or get new ones, so other packages sharing the
/// target's model (Doom's orb mesh is also Cable's Greymalkin's and the Lightning Orb affix's) are unaffected.
/// </summary>
static class SummonLook
{
    /// <summary>
    /// The target summon package with the donor's look, verified. <paramref name="slots"/>: target animation name → donor
    /// animation name. Throws InvalidDataException with the reason.
    /// </summary>
    public static byte[] Build(string targetPackage, string targetClass, string donorPackage, string donorMesh, IReadOnlyDictionary<string, string> slots, List<string> log)
    {
        byte[] withMesh = CrossMove.Build(donorPackage, donorMesh, targetPackage, targetClass, null, log, sounds: false);
        string temp = Path.Combine(Path.GetTempPath(), $"summonlook_{Guid.NewGuid():N}_{Path.GetFileName(targetPackage)}");
        File.WriteAllBytes(temp, withMesh);
        try
        {
            var donor = CostumeAnims.Read(donorPackage, Path.GetFileName(donorPackage), _ => null)
                ?? throw new InvalidDataException($"{Path.GetFileName(donorPackage)} has no animation list");
            var own = donor.Anims.Where(a => a.From.Kind == CostumeAnims.Source.Costume && a.From.File != null).ToList();
            if (own.Count == 0) throw new InvalidDataException($"{Path.GetFileName(donorPackage)} has no animations of its own");
            var byName = own.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
            var swaps = new List<AnimSwap.Swap>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (slot, from) in slots)
            {
                if (!byName.TryGetValue(from, out var a)) { log.Add($"anims: {slot} ← {from}: the donor has no such animation"); continue; }
                swaps.Add(new AnimSwap.Swap(slot, a.From.File!, a.From.Export, a.Export));
                taken.Add(slot);
            }
            // the donor's own names too (the target may play names of its own kind: idle, spawn, movement …)
            foreach (var a in own.Where(a => !taken.Contains(a.Name)))
                swaps.Add(new AnimSwap.Swap(a.Name, a.From.File!, a.From.Export, a.Export));
            log.Add($"anims: {string.Join(", ", swaps.Select(s => s.Slot))}");
            return AnimSwap.Build(temp, targetClass, swaps, log);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
}
