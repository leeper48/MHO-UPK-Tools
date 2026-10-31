using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>One sound of a power: an entry in a sound component's Events list.</summary>
/// <param name="Component">The component's path in the package.</param>
/// <param name="Index">Its place in the component's Events list.</param>
/// <param name="Stop">The event that ends it (a looping sound), or null.</param>
sealed record PowerSound(string Package, string Component, string Part, string When, int Index, string Event, string? Stop);

/// <summary>A sound of a power changed by the mod (manifest PowerSounds; extension, MHModManager ignores it).</summary>
sealed class PowerSoundEntry
{
    public string Package { get; set; } = "";
    public string Component { get; set; } = "";
    public int Index { get; set; }
    /// <summary>The sound's own event, so it can be put back.</summary>
    public string Event { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Off { get; set; }
    /// <summary>Plays the mod's own audio (in its PowerSounds.mhsfx).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Replaced { get; set; }
    /// <summary>The package came into the mod for this (it leaves again when no sound of it is changed).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AddedPackage { get; set; }
}

/// <summary>
/// A power's sounds (Kurt, 2026-10-31): components of class …fxsound in its packages (PowerFxSound, ProjectileFxSound,
/// ConditionFxSound, EntityFxSound) hold Events, a list of { ActivateEvent, DeactivateEvent }: the AkEvent that starts the sound
/// and the one that stops it, set for looping sounds. Off = ActivateEvent none. Replaced goes the way Voice Shift's lines do:
/// the event copied in the package (…_mhosnd, same bank), the component pointed at it, and a sound pack that adds that event, so
/// MHModManager 1.0.1 applies it too. A looping sound can't be replaced: its stop event stops the original sound object, so a
/// clone would never end. Applied last at Save (PowerColorBuild rebuilds power packages from the game's), in place and
/// reversibly; the manifest holds what's changed.
/// </summary>
static class PowerSounds
{
    public const string Suffix = "_mhosnd";
    public const string PackName = "PowerSounds.mhsfx";

    /// <summary>The sound's event name without its path, or the copy's suffix.</summary>
    public static string Leaf(string eventPath)
    {
        string p = eventPath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? eventPath[..^Suffix.Length] : eventPath;
        return p[(p.LastIndexOf('.') + 1)..];
    }

    /// <summary>The events and their tags of a sound component, or null when it has none.</summary>
    static (List<TagWalker> Elements, byte[] Bytes)? Elements(Package pkg, int export)
    {
        byte[] d = pkg.ReadExportBytes(pkg.Exports[export]).ToArray();
        if (TagWalker.Walk(pkg, d, 16) is not { } tags || tags.FirstOrDefault(t => t.Name.Equals("events", StringComparison.OrdinalIgnoreCase)) is not { } ev) return null;
        var list = new List<TagWalker>();
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ev.ValueAt)), pos = ev.ValueAt + 4;
        for (int k = 0; k < n && pos < ev.End; k++)
        {
            if (TagWalker.Walk(pkg, d, pos) is not { } el) break;
            list.Add(el); pos = el.NoneAt + 8;
        }
        return (list, d);
    }

    public static List<PowerSound> Read(string packageFile, string path)
    {
        var list = new List<PowerSound>();
        var pkg = Package.Open(path);
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            if (!pkg.ClassOf(pkg.Exports[i]).EndsWith("fxsound", StringComparison.OrdinalIgnoreCase) || Elements(pkg, i) is not { } e) continue;
            byte[] d = e.Bytes;
            string when = TagWalker.Walk(pkg, d, 16)?.FirstOrDefault(t => t.Name.Equals("activationpoint", StringComparison.OrdinalIgnoreCase)) is { Size: 8 } ap ? TagWalker.NameAt(pkg, d, ap.ValueAt) : "";
            for (int k = 0; k < e.Elements.Count; k++)
            {
                var el = e.Elements[k];
                string? Path(string name)
                {
                    int r = el.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Size: 4 } f ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(f.ValueAt)) : 0;
                    return r > 0 && r <= pkg.Exports.Length && pkg.ClassOf(pkg.Exports[r - 1]).Equals("AkEvent", StringComparison.OrdinalIgnoreCase) ? pkg.PathOf(pkg.Exports[r - 1]) : null;
                }
                if (Path("activateevent") is string start) list.Add(new PowerSound(packageFile, pkg.PathOf(pkg.Exports[i]), pkg.Exports[i].ObjectName, when, k, start, Path("deactivateevent")));
            }
        }
        return list;
    }

    /// <summary>The components' events set (null: none), as verified bytes: every other byte kept.</summary>
    static byte[] Write(Package pkg, IEnumerable<(string Component, int Index, string? Event)> changes)
    {
        var rep = new Dictionary<int, Func<long, byte[]>>();
        foreach (var g in changes.GroupBy(c => c.Component, StringComparer.OrdinalIgnoreCase))
        {
            int x = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (x < 0 || Elements(pkg, x) is not { } e) throw new InvalidDataException($"{g.Key} isn't a sound component in the package");
            byte[] d = e.Bytes;
            foreach (var (_, index, ev) in g)
            {
                if (index >= e.Elements.Count || e.Elements[index].FirstOrDefault(f => f.Name.Equals("activateevent", StringComparison.OrdinalIgnoreCase)) is not { Size: 4 } tag)
                    throw new InvalidDataException($"{g.Key} has no sound {index}");
                int r = 0;
                if (ev != null)
                {
                    int i = Array.FindIndex(pkg.Exports, y => pkg.PathOf(y).Equals(ev, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(y).Equals("AkEvent", StringComparison.OrdinalIgnoreCase));
                    if (i < 0) throw new InvalidDataException($"the sound event {ev} isn't in the package");
                    r = i + 1;
                }
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(tag.ValueAt), r);
            }
            rep[x] = _ => d;
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, rep, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, [.. rep.Keys], [], written);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems.Take(2)));
        return output;
    }

    /// <summary>
    /// Before saving: draft.PowerSounds into the draft's packages and sound pack, sounds no longer changed put back. Returns the
    /// work folder, or null when there's nothing to do. <paramref name="newLines"/>: event name → WAV in the game's format, for
    /// sounds replaced since the last save; the others' audio stays as it is in the mod's pack.
    /// </summary>
    public static string? Apply(ModDraft draft, Mod? editing, ModLibrary lib, GameState game, IReadOnlyDictionary<string, byte[]> newLines, List<string> log)
    {
        var was = editing?.Manifest.PowerSounds ?? [];
        if (draft.PowerSounds.Count == 0 && was.Count == 0) return null;
        static (string, string, int) Key(PowerSoundEntry e) => (e.Package.ToLowerInvariant(), e.Component.ToLowerInvariant(), e.Index);
        var now = draft.PowerSounds.ToDictionary(Key);
        var back = was.Where(e => !now.ContainsKey(Key(e))).ToList();
        string work = Path.Combine(lib.DataFolder, "power-sounds-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var originals = new Originals(lib.DataFolder, game);
        var index = Akpk.EventIndex(game.Cooked);
        string? earlierPath = draft.SoundPacks.FirstOrDefault(p => Path.GetFileName(p).Equals(PackName, StringComparison.OrdinalIgnoreCase));
        var earlier = earlierPath != null && File.Exists(earlierPath) ? SoundPack.Load(earlierPath).Wems : null;
        var patches = new List<Dictionary<string, string>>();
        var wems = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in draft.PowerSounds.Concat(back).Select(e => e.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            int k = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
            var mine = draft.PowerSounds.Where(e => e.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
            // nothing of it changed any more, and it only came in for its sounds: out of the mod
            if (mine.Count == 0 && k >= 0 && back.Where(e => e.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).All(e => e.AddedPackage)
                && !draft.PowerColors.Any(c => c.Packages.Contains(file, StringComparer.OrdinalIgnoreCase)))
            {
                draft.Packages.RemoveAt(k);
                log.Add($"{file}: its sounds are the game's again, so it's out of the mod");
                continue;
            }
            string? source = k >= 0 ? draft.Packages[k].Source : originals.Find(file);
            if (source == null) throw new InvalidDataException($"{file}: no clean original of this power package (the game's copy is changed)");
            if (k < 0) foreach (var e in mine) e.AddedPackage = true;
            var pkg = Package.Open(source);
            var changes = new List<(string, int, string?)>();
            foreach (var e in back.Where(e => e.Package.Equals(file, StringComparison.OrdinalIgnoreCase))) changes.Add((e.Component, e.Index, e.Event));
            foreach (var e in mine)
            {
                if (!e.Replaced) { changes.Add((e.Component, e.Index, e.Off ? null : e.Event)); continue; }
                string leaf = Leaf(e.Event), nleaf = leaf + Suffix, group = e.Event[..e.Event.LastIndexOf('.')];
                int ei = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(e.Event, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("AkEvent", StringComparison.OrdinalIgnoreCase));
                if (ei < 0) throw new InvalidDataException($"{file}: the sound event {e.Event} isn't in the package");
                if (!index.TryGetValue(SoundPack.Fnv(leaf), out var where)) throw new InvalidDataException($"{leaf}: its event isn't in the game's sound banks");
                byte[] ed = pkg.ReadExportBytes(pkg.Exports[ei]).ToArray();
                string? bank = TagWalker.Walk(pkg, ed, 4)?.FirstOrDefault(t => t.Name.Equals("RequiredBank", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } rb
                    ? pkg.RefName(BinaryPrimitives.ReadInt32LittleEndian(ed.AsSpan(rb.ValueAt))) : null;
                if (bank == null || SoundPack.Fnv(bank) != where.Bank) throw new InvalidDataException($"{leaf}: its bank {bank ?? "?"} isn't the one it's in");
                if (!pkg.Exports.Any(x => x.ObjectName.Equals(nleaf, StringComparison.OrdinalIgnoreCase)))
                {
                    var cp = CrossMove.Quiet(() => ExportCopy.Copy(pkg, ei, pkg, [], nleaf), out string said) ?? throw new InvalidDataException($"{leaf}: the event couldn't be copied ({said.Trim()})");
                    pkg = Package.FromBytes(cp.Output);
                }
                changes.Add((e.Component, e.Index, e.Off ? null : group + "." + nleaf));
                if (wems.ContainsKey(nleaf + ".wem")) continue;   // (the same sound in another package: one new event)
                if (newLines.TryGetValue(leaf, out var wav)) wems[nleaf + ".wem"] = WwiseVorbisWrite.ToWem(VorbisEncode.Encode(wav, 0.4f), WavPcm.Read(wav).Channels[0].Length);
                else if (earlier?.TryGetValue(nleaf + ".wem", out var kept) == true) wems[nleaf + ".wem"] = kept;   // (not encoded again: it'd lose quality each time)
                else throw new InvalidDataException($"{leaf}: its replacement isn't in the mod's sound pack");
                patches.Add(new Dictionary<string, string>
                {
                    ["type"] = "new_event", ["original_event_name"] = leaf, ["event_name"] = nleaf, ["event_hash"] = $"0x{SoundPack.Fnv(nleaf):X8}",
                    ["action_id"] = $"0x{SoundPack.Fnv(nleaf + "_action"):X8}", ["sound_id"] = $"0x{SoundPack.Fnv(nleaf + "_sound"):X8}", ["source_id"] = $"0x{SoundPack.Fnv(nleaf + "_source"):X8}",
                    ["wem_file"] = nleaf + ".wem", ["bank_name"] = bank, ["pck_file"] = where.Pck,
                });
            }
            string dir = Directory.CreateDirectory(Path.Combine(work, Guid.NewGuid().ToString("N")[..6])).FullName, outFile = Path.Combine(dir, file);
            File.WriteAllBytes(outFile, Write(pkg, changes));
            if (k >= 0) draft.Packages[k] = (file, outFile); else draft.Packages.Add((file, outFile));
        }
        draft.SoundPacks.RemoveAll(p => Path.GetFileName(p).Equals(PackName, StringComparison.OrdinalIgnoreCase));
        if (patches.Count > 0)
        {
            string pack = Path.Combine(work, PackName);
            using (var z = ZipFile.Open(pack, ZipArchiveMode.Create))
            {
                using (var sw = new StreamWriter(z.CreateEntry("mod.json").Open()))
                    sw.Write(JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = "Power sounds", ["patches"] = patches }, new JsonSerializerOptions { WriteIndented = true }));
                foreach (var (n, b) in wems) using (var e = z.CreateEntry(n).Open()) e.Write(b);
            }
            draft.SoundPacks.Add(pack);
        }
        log.Add($"Power sounds: {draft.PowerSounds.Count(e => e.Off)} off, {patches.Count} replaced");
        return work;
    }
}
