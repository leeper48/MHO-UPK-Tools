using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// The cloth event a caped costume needs on another hero (Kurt, 2026-09-30). Doctor Strange's cape stayed pinned on Colossus
/// and Daredevil while the same graft moved on his own Fear Itself costume, and Punisher S2's coat moved on Daredevil. His
/// animations carry AnimNotify_ClothingMaxDistanceScale events (idle, idle_flying, idle_combat_flying, movement_run_flying:
/// scale 1 → 1; the high-flying ones: 0 → 0), which set how far his APEX cape may move from the body; other heroes'
/// animations (Punisher, Colossus, Daredevil …) have none. Hypothesis: his cape asset needs that event to be released.
/// This adds the source hero's idle event to the target hero's idle sequence(s): the notify object is copied (its outer
/// mapped onto the target sequence) and a Notifies entry (Time 0, the event, Duration = the target idle's length) is
/// appended. The target is the hero's base package (its animations are shared by all its costumes; a scale-1 event
/// changes nothing for a costume without cloth).
/// </summary>
static class ClothNotify
{
    const string NotifyClass = "AnimNotify_ClothingMaxDistanceScale";

    static string? SequenceName(Package p, byte[] d, TagWalker tags) =>
        tags.FirstOrDefault(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } t ? TagWalker.NameAt(p, d, t.ValueAt) : null;

    /// <summary>The source's cloth event on its idle: (the sequence, the notify export), or null.</summary>
    static (int Seq, int Notify)? SourceEvent(Package src)
    {
        for (int i = 0; i < src.Exports.Length; i++)
        {
            if (!src.ClassOf(src.Exports[i]).Equals("AnimSequence", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = src.ReadExportBytes(src.Exports[i]).ToArray();
            var tags = TagWalker.Walk(src, d, 4);
            if (tags == null || !string.Equals(SequenceName(src, d, tags), "idle", StringComparison.OrdinalIgnoreCase)) continue;
            if (tags.FirstOrDefault(t => t.Name.Equals("Notifies", StringComparison.OrdinalIgnoreCase)) is not { } nt) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nt.ValueAt)), q = nt.ValueAt + 4;
            for (int k = 0; k < count; k++)
            {
                var el = TagWalker.Walk(src, d, q);
                if (el == null) break;
                if (el.FirstOrDefault(t => t.Name.Equals("Notify", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } n
                    && BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(n.ValueAt)) is int r && r > 0
                    && src.ClassOf(src.Exports[r - 1]).Equals(NotifyClass, StringComparison.OrdinalIgnoreCase))
                    return (i, r - 1);
                q = el.NoneAt + 8;
            }
        }
        return null;
    }

    /// <summary>
    /// The target base package with the source's idle cloth event added to its idle sequence(s), verified; null when the
    /// source has no such event or the target has no idle (reason in the log). Throws when the build doesn't verify.
    /// </summary>
    public static byte[]? Build(string sourceBase, string targetBase, List<string> log)
    {
        var src = Package.Open(sourceBase);
        if (SourceEvent(src) is not { } ev) { log.Add($"cloth event: none on {Path.GetFileName(sourceBase)}'s idle"); return null; }
        var pkg = Package.Open(targetBase);
        var targets = new List<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            if (!pkg.ClassOf(pkg.Exports[i]).Equals("AnimSequence", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = pkg.ReadExportBytes(pkg.Exports[i]).ToArray();
            if (TagWalker.Walk(pkg, d, 4) is { } tags && string.Equals(SequenceName(pkg, d, tags), "idle", StringComparison.OrdinalIgnoreCase)) targets.Add(i);
        }
        if (targets.Count == 0) { log.Add($"cloth event: {Path.GetFileName(targetBase)} has no idle"); return null; }

        // Copy the event once per target idle, its outer mapped onto that sequence.
        var notifyRef = new Dictionary<int, int>();
        foreach (int t in targets)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [src.PathOf(src.Exports[ev.Seq])] = pkg.PathOf(pkg.Exports[t]) };
            var c = ExportCopy.Copy(src, ev.Notify, pkg, [], null, map) ?? throw new InvalidDataException("the cloth event couldn't be copied");
            pkg = Package.FromBytes(c.Output);
            notifyRef[t] = c.RootRef;
        }

        var addNames = new List<string>();
        foreach (string n in new[] { "Notifies", "ArrayProperty", "Time", "FloatProperty", "Notify", "ObjectProperty", "Duration", "None" })
            if (!pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) && !addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
        var tw = new TagWriter(pkg, addNames);
        var replaceData = new Dictionary<int, Func<long, byte[]>>();
        foreach (int t in targets)
        {
            byte[] d = pkg.ReadExportBytes(pkg.Exports[t]).ToArray();
            var tags = TagWalker.Walk(pkg, d, 4) ?? throw new InvalidDataException("the idle sequence doesn't read");
            float length = tags.FirstOrDefault(x => x.Name.Equals("SequenceLength", StringComparison.OrdinalIgnoreCase) && x.Size == 4) is { } sl
                ? BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(sl.ValueAt)) : 1f;
            byte[] element = [.. tw.Tag("Time", "FloatProperty", null, BitConverter.GetBytes(0f)),
                              .. tw.Tag("Notify", "ObjectProperty", null, BitConverter.GetBytes(notifyRef[t])),
                              .. tw.Tag("Duration", "FloatProperty", null, BitConverter.GetBytes(length)),
                              .. tw.NameRef("None")];
            byte[] nd;
            if (tags.FirstOrDefault(x => x.Name.Equals("Notifies", StringComparison.OrdinalIgnoreCase)) is { } nt)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nt.ValueAt));
                nd = [.. d.AsSpan(0, nt.End), .. element, .. d.AsSpan(nt.End)];
                BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(nt.ValueAt), count + 1);
                BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(nt.Start + 16), nt.Size + element.Length);
            }
            else
            {
                byte[] arr = tw.StructArray("Notifies", [element]);
                nd = [.. d.AsSpan(0, tags.NoneAt), .. arr, .. d.AsSpan(tags.NoneAt)];
            }
            replaceData[t] = _ => nd;
            log.Add($"cloth event: added to {pkg.PathOf(pkg.Exports[t])} (idle, {length:0.##} s) → #{notifyRef[t]}");
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, replaceData, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(pkg, output, replaceData.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        return output;
    }
}
