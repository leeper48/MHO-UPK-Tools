using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// An added prop shown only while chosen powers run (Monarch, 2026-11: link a prop's visibility to powers), the game's own way
/// (stock evidence, Punisher):
/// - the prop's class default: WeaponSlot (an array of names) and VisibilityPoint = visible_on_demand
///   (marvelattachment_punisher_shotgun: 3 slots, on demand);
/// - the power's class default lists a PowerFxMeshAttachment component that shows that slot while the power runs
///   (powerpunisher_magnum.attachmentgunleft: WhileActivatedShowWeaponSlot magnum, HideAttachments hide_current: the
///   pistols go away while the magnum shows).
/// The prop gets a slot of its own (memm_&lt;class&gt;), so the power packages, which every costume of the hero shares, show
/// nothing on the costumes that don't hold it. Hypothesis until seen in game: the client matches a slot by its name only
/// (AMarvelAttachmentMeshBase::PassesWeaponSlotFilter), so a name no stock prop uses works.
/// </summary>
static partial class PowerBorrow
{
    /// <summary>The stock component the show components are copied from (its form; its properties are replaced).</summary>
    const string ShowTemplateFile = "UC__PowerPunisher_Magnum_SF.upk",
                 ShowTemplateDefault = "marvelgamecontent.default__powerpunisher_magnum",
                 ShowTemplate = ShowTemplateDefault + ".attachmentgunleft";

    /// <summary>Show components this app adds start with this (all of them are taken off again before the current ones go in).</summary>
    public const string ShowPrefix = "memm_show_";

    /// <summary>The slot an added prop gets when it's shown only during powers: its class name (own to this costume) as a slot.</summary>
    public static string ShowSlot(string propClass) => "memm_" + propClass.ToLowerInvariant().Replace("marvelattachment_", "");

    /// <summary>
    /// The prop class default (marvelgamecontent.default__&lt;class&gt;) shown on demand in <paramref name="slot"/> only: WeaponSlot
    /// = [slot], VisibilityPoint = visible_on_demand. Applying it again gives the same package. Verified.
    /// </summary>
    public static byte[] PropOnDemand(byte[] package, string classDefault, string slot, List<string> log)
    {
        var p = Package.FromBytes(package);
        int d = Find(p, classDefault);
        if (d < 0) throw new InvalidDataException($"{classDefault} isn't in the package");
        byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
        var tags = TagWalker.Walk(p, b, 4) ?? throw new InvalidDataException($"{classDefault}: its properties don't read");
        var names = new NameAdder(p);
        // the tags as wanted
        byte[] slotTag = names.Tag("WeaponSlot", "ArrayProperty", [.. BitConverter.GetBytes(1), .. names.Value(slot)]);
        byte[] visTag = names.ByteTag("VisibilityPoint", "EVisibilityPoint", "VISIBLE_ON_DEMAND");
        var m = new MemoryStream();
        m.Write(b, 0, 4);
        foreach (var t in tags)
            if (!t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase) && !t.Name.Equals("VisibilityPoint", StringComparison.OrdinalIgnoreCase))
                m.Write(b, t.Start, t.End - t.Start);
        m.Write(slotTag); m.Write(visTag);
        m.Write(b, tags.NoneAt, b.Length - tags.NoneAt);   // None and anything after it
        byte[] nb = m.ToArray();
        if (nb.AsSpan().SequenceEqual(b)) return package;
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => nb };
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, names.Added);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, names.Added);
        if (problems.Count > 0) throw new InvalidDataException($"{p.Exports[d].ObjectName}: couldn't be set to show only during powers: " + string.Join("; ", problems.Take(2)));
        var back = Package.FromBytes(output);
        byte[] bb = back.ReadExportBytes(back.Exports[d]).ToArray();
        var bt = TagWalker.Walk(back, bb, 4);
        if (bt?.FirstOrDefault(t => t.Name.Equals("VisibilityPoint", StringComparison.OrdinalIgnoreCase)) is not { } vp || !TagWalker.NameAt(back, bb, vp.ValueAt).Contains("on_demand", StringComparison.OrdinalIgnoreCase)
            || bt.FirstOrDefault(t => t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase)) is not { } ws || !TagWalker.NameAt(back, bb, ws.ValueAt + 4).Equals(slot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{p.Exports[d].ObjectName}: the on-demand setting didn't read back");
        log.Add($"{p.Exports[d].ObjectName}: shown only while a power shows its slot {slot}");
        return output;
    }

    /// <summary>
    /// A power's class default (<paramref name="powerDefault"/>) with every show component this app added taken off its list
    /// (ShowPrefix; the exports stay, unused), then one per slot in <paramref name="slots"/> added: it shows that slot while
    /// the power runs (until its end), and with <paramref name="hideOthers"/> hides the props shown before (as Punisher's
    /// Magnum hides his pistols). The package unchanged when that's what it has. Verified.
    /// </summary>
    public static byte[] SetPowerShows(byte[] package, string powerDefault, IReadOnlyList<(string Slot, bool HideOthers)> slots, string cooked, List<string> log)
    {
        var p = Package.FromBytes(package);
        int td = Find(p, powerDefault);
        if (td < 0) throw new InvalidDataException($"{powerDefault} isn't in its package");
        var comps = Components(p, td, out _, out _);
        bool Ours(int r) => r > 0 && p.Exports[r - 1].ObjectName.StartsWith(ShowPrefix, StringComparison.OrdinalIgnoreCase);
        // already as wanted: nothing to do (Save runs this every time)
        var have = comps.Where(Ours).Select(r => p.Exports[r - 1].ObjectName).ToList();
        var want = slots.Select(s => ShowName(s.Slot, s.HideOthers)).ToList();
        if (have.SequenceEqual(want, StringComparer.OrdinalIgnoreCase)) return package;
        // the list without ours
        var list = comps.Where(r => !Ours(r)).ToList();
        if (list.Count != comps.Count) p = Package.FromBytes(WithComponents(p, td, list));
        foreach (var (slot, hide) in slots)
        {
            string name = ShowName(slot, hide);
            int have1 = Find(p, powerDefault + "." + name);
            if (have1 < 0)
            {
                string tplFile = StockFiles.For(cooked, ShowTemplateFile);
                if (!File.Exists(tplFile)) throw new InvalidDataException($"the show component's template package ({ShowTemplateFile}) isn't found");
                var tp = Package.Open(tplFile);
                int ti = Find(tp, ShowTemplate);
                if (ti < 0) throw new InvalidDataException("the show component's template isn't in " + ShowTemplateFile);
                var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ShowTemplateDefault] = powerDefault };
                var cc = CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, p, [], name, replace), out string said)
                    ?? throw new InvalidDataException("the show component couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3)));
                p = Package.FromBytes(cc.Output);
                have1 = cc.RootRef - 1;
            }
            // its properties: the slot shown while the power runs, and the others hidden (hide_current) if asked
            byte[] cd = p.ReadExportBytes(p.Exports[have1]).ToArray();
            var ctags = TagWalker.Walk(p, cd, 16) ?? throw new InvalidDataException($"{name}: its properties don't read");
            var names = new NameAdder(p);
            var m = new MemoryStream();
            m.Write(cd, 0, 16);
            m.Write(names.Tag("WhileActivatedShowWeaponSlot", "NameProperty", names.Value(slot)));
            if (hide) m.Write(names.ByteTag("HideAttachments", "EHide", "HIDE_CURRENT"));
            m.Write(cd, ctags.NoneAt, cd.Length - ctags.NoneAt);
            byte[] nc = m.ToArray();
            var rep = new Dictionary<int, Func<long, byte[]>> { [have1] = _ => nc };
            byte[] o1 = PackageRebuilder.Rebuild(p, rep, [], out var w1, names.Added);
            var pr1 = PackageRebuilder.Verify(p, o1, rep.Keys.ToList(), [], w1, names.Added);
            if (pr1.Count > 0) throw new InvalidDataException($"{name}: " + string.Join("; ", pr1.Take(2)));
            p = Package.FromBytes(o1);
            td = Find(p, powerDefault);
            var now = Components(p, td, out _, out _);
            if (!now.Contains(have1 + 1)) p = Package.FromBytes(WithComponents(p, td, [.. now, have1 + 1]));
        }
        log.Add($"{powerDefault.Replace("marvelgamecontent.default__", "")}: {(slots.Count == 0 ? "shows no added prop" : "shows " + string.Join(", ", slots.Select(s => s.Slot + (s.HideOthers ? " (the others hidden)" : ""))))} while it runs");
        // read back: exactly the wanted ones on the list
        int bd = Find(p, powerDefault);
        var got = Components(p, bd, out _, out _).Where(r => r > 0 && p.Exports[r - 1].ObjectName.StartsWith(ShowPrefix, StringComparison.OrdinalIgnoreCase)).Select(r => p.Exports[r - 1].ObjectName).ToList();
        if (!got.SequenceEqual(want, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"{powerDefault}: the show components didn't read back ({string.Join(", ", got)})");
        return p.RawFile;
    }

    static string ShowName(string slot, bool hide)
    {
        string s = slot.ToLowerInvariant();
        if (s.StartsWith("memm_")) s = s[5..];
        return ShowPrefix + s + (hide ? "_only" : "");
    }

    /// <summary>The class default's Components list set to <paramref name="list"/>; the whole package (verified).</summary>
    static byte[] WithComponents(Package p, int export, List<int> list)
    {
        Components(p, export, out var tag, out byte[] d);
        var value = new byte[4 + 4 * list.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
        for (int k = 0; k < list.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), list[k]);
        byte[] nd = [.. d.AsSpan(0, tag.ValueAt), .. value, .. d.AsSpan(tag.ValueAt + tag.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
        var rep = new Dictionary<int, Func<long, byte[]>> { [export] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException($"{p.Exports[export].ObjectName}'s components: " + string.Join("; ", problems.Take(2)));
        return output;
    }

    /// <summary>Names for new property tags: the package's own (any case), else added.</summary>
    sealed class NameAdder(Package p)
    {
        public readonly List<string> Added = [];
        public int Index(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int a = Added.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (a < 0) { Added.Add(n); a = Added.Count - 1; }
            return p.Names.Length + a;
        }
        public byte[] Value(string n) { var v = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(v, Index(n)); return v; }
        public byte[] Tag(string name, string type, byte[] value)
        {
            var t = new byte[24 + value.Length];
            BinaryPrimitives.WriteInt32LittleEndian(t, Index(name));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(8), Index(type));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(16), value.Length);
            value.CopyTo(t, 24);
            return t;
        }
        /// <summary>An enum ByteProperty: the tag, its enum's name, then the value's name (size 8).</summary>
        public byte[] ByteTag(string name, string enumName, string value)
        {
            var t = new byte[24 + 8 + 8];
            BinaryPrimitives.WriteInt32LittleEndian(t, Index(name));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(8), Index("ByteProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(16), 8);
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(24), Index(enumName));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(32), Index(value));
            return t;
        }
    }
}
