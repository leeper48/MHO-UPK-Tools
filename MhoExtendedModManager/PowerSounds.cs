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

/// <summary>Volume and filters (a user asked, 2026-10-31): a sound's, or a hero's whole set of sounds'.</summary>
class SoundShape
{
    /// <summary>How loud it plays (1 = as it is; null: not set).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Volume { get; set; }
    // The Voice tab's filters (VoiceEffects): 0 = off to 1.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Reverb { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Echo { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Distortion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float LoFi { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Chorus { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Robot { get; set; }

    /// <summary>The volume and filters, as VoiceShift.Process takes them.</summary>
    [JsonIgnore]
    public VoiceShiftEntry Filters => new() { Volume = Volume, Reverb = Reverb, Echo = Echo, Distortion = Distortion, LoFi = LoFi, Chorus = Chorus, Robot = Robot };
    /// <summary>Volume and filters as one value: two with the same one sound the same.</summary>
    [JsonIgnore]
    public string Shape => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Volume ?? 1:0.00} {Reverb:0.00} {Echo:0.00} {Distortion:0.00} {LoFi:0.00} {Chorus:0.00} {Robot:0.00}");
    [JsonIgnore]
    public bool Shaped => Filters.Any;
    public void ClearShape() { Volume = null; Reverb = Echo = Distortion = LoFi = Chorus = Robot = 0; }
    public void CopyShape(SoundShape o) { Volume = o.Volume; Reverb = o.Reverb; Echo = o.Echo; Distortion = o.Distortion; LoFi = o.LoFi; Chorus = o.Chorus; Robot = o.Robot; }
}

/// <summary>A hero's whole set of sounds (manifest PowerSoundSets; extension). Its settings are also on every sound that follows
/// it (PowerSoundEntry.InSet), so a build needs only the entries.</summary>
sealed class PowerSoundSet : SoundShape
{
    public string Hero { get; set; } = "";
}

/// <summary>A sound of a power changed by the mod (manifest PowerSounds; extension, MHModManager ignores it).</summary>
sealed class PowerSoundEntry : SoundShape
{
    public string Package { get; set; } = "";
    public string Component { get; set; } = "";
    public int Index { get; set; }
    /// <summary>The sound's own event, so it can be put back.</summary>
    public string Event { get; set; } = "";
    /// <summary>The event that ends it (a looping sound): copied too, so the copy of the loop stops.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Stop { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Off { get; set; }
    /// <summary>Plays the mod's own audio (in its PowerSounds.mhsfx).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Replaced { get; set; }
    /// <summary>The package came into the mod for this (it leaves again when no sound of it is changed).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AddedPackage { get; set; }
    /// <summary>Follows the hero's whole set; false with a set in place: this row has its own settings (none = plays as it is).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InSet { get; set; }
}

/// <summary>
/// A power's sounds (Kurt, 2026-10-31): components of class …fxsound in its packages (PowerFxSound, ProjectileFxSound,
/// ConditionFxSound, EntityFxSound) hold Events, a list of { ActivateEvent, DeactivateEvent }: the AkEvent that starts the sound
/// and the one that stops it, set for looping sounds. Off = ActivateEvent none. Replaced goes the way Voice Shift's lines do:
/// the event copied in the package (…_mhosnd, same bank), the component pointed at it, and a sound pack that adds that event, so
/// MHModManager 1.0.1 applies it too. A looping sound's stop event stops the sound object it names (70 of 73 on seven heroes
/// name the sound they play), so its copy needs a copied stop: a second patch with the same sound_id, whose action then targets
/// the new sound, with DeactivateEvent pointed at it (MHModManager takes it; both .pck byte-identical; heard in game on Vision,
/// 2026-11-01). The bank then holds two sounds with that ID and the game plays the first. Applied last at Save (PowerColorBuild rebuilds power packages from the game's), in place and
/// reversibly; the manifest holds what's changed. Volume and filters go the same way, on the game's own sounds too (every one-shot
/// on four heroes checked is 22 kHz mono Vorbis, what we write, 2026-11-01). The imported audio stays in the pack as
/// &lt;event&gt;.source.wav and each change renders from it, so moving a slider never re-encodes an encoded sound. A clone keeps one
/// sound, so an event that picks among variations (4-18 % of a hero's) plays only its first once changed; the row says so.
/// </summary>
static class PowerSounds
{
    public const string Suffix = "_mhosnd";
    public const string PackName = "PowerSounds.mhsfx";
    /// <summary>A replaced sound's audio as imported, in the pack beside its .wem.</summary>
    public const string SourceEnd = ".source.wav";

    /// <summary>The sound's event name without its path, or the copy's suffix.</summary>
    public static string Leaf(string eventPath)
    {
        string p = eventPath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? eventPath[..^Suffix.Length] : eventPath;
        return p[(p.LastIndexOf('.') + 1)..];
    }

    /// <summary>The imported audio kept in a pack: entry name → WAV (a pack from before volume and filters has none).</summary>
    public static Dictionary<string, byte[]> Sources(string? pack)
    {
        var all = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (pack == null || !File.Exists(pack)) return all;
        using var z = ZipFile.OpenRead(pack);
        var entries = z.Entries.Where(e => e.FullName.EndsWith(SourceEnd, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Sum(e => e.Length) > 1L << 30) throw new InvalidDataException($"{Path.GetFileName(pack)}: more than 1 GB of sound");   // (as SoundPack.Load)
        foreach (var e in entries)
        {
            using var ms = new MemoryStream(); e.Open().CopyTo(ms); all[e.FullName] = ms.ToArray();
        }
        return all;
    }

    /// <summary>A saved replacement's audio as imported: from the mod's pack, else (an older pack) its .wem decoded.</summary>
    public static byte[] Saved(string leaf, IEnumerable<string> soundPacks)
    {
        string nleaf = leaf + Suffix;
        string pack = soundPacks.FirstOrDefault(p => Path.GetFileName(p).Equals(PackName, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{leaf}: its replacement isn't in the mod's sound pack");
        if (Sources(pack).TryGetValue(nleaf + SourceEnd, out var source)) return source;
        if (SoundPack.Load(pack).Wems.TryGetValue(nleaf + ".wem", out var wem)) return VoiceAudio.ToWav(wem);
        throw new InvalidDataException($"{leaf}: its replacement isn't in the mod's sound pack");
    }

    /// <summary>Audio with the filters and volume: a WAV in the same format.</summary>
    public static byte[] Render(byte[] source, SoundShape e) => VoiceShift.Process(source, e.Filters);

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

    /// <summary>The game's stop event of a changed sound (the copy's name taken off), or null when it has none.</summary>
    static string? StopOf(Package pkg, PowerSoundEntry e)
    {
        int x = Array.FindIndex(pkg.Exports, y => pkg.PathOf(y).Equals(e.Component, StringComparison.OrdinalIgnoreCase));
        if (x < 0 || Elements(pkg, x) is not { } el || e.Index >= el.Elements.Count) return null;
        int r = el.Elements[e.Index].FirstOrDefault(f => f.Name.Equals("deactivateevent", StringComparison.OrdinalIgnoreCase)) is { Size: 4 } f ? BinaryPrimitives.ReadInt32LittleEndian(el.Bytes.AsSpan(f.ValueAt)) : 0;
        if (r <= 0 || r > pkg.Exports.Length || !pkg.ClassOf(pkg.Exports[r - 1]).Equals("AkEvent", StringComparison.OrdinalIgnoreCase)) return null;
        string path = pkg.PathOf(pkg.Exports[r - 1]);
        return path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? path[..^Suffix.Length] : path;
    }

    /// <summary>The components' events set (null: none), as verified bytes: every other byte kept.</summary>
    static byte[] Write(Package pkg, IEnumerable<(string Component, int Index, string? Event, string? Stop)> changes)
    {
        var rep = new Dictionary<int, Func<long, byte[]>>();
        int Ref(string ev)
        {
            int i = Array.FindIndex(pkg.Exports, y => pkg.PathOf(y).Equals(ev, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(y).Equals("AkEvent", StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i + 1 : throw new InvalidDataException($"the sound event {ev} isn't in the package");
        }
        foreach (var g in changes.GroupBy(c => c.Component, StringComparer.OrdinalIgnoreCase))
        {
            int x = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (x < 0 || Elements(pkg, x) is not { } e) throw new InvalidDataException($"{g.Key} isn't a sound component in the package");
            byte[] d = e.Bytes;
            foreach (var (_, index, ev, stop) in g)
            {
                if (index >= e.Elements.Count || e.Elements[index].FirstOrDefault(f => f.Name.Equals("activateevent", StringComparison.OrdinalIgnoreCase)) is not { Size: 4 } tag)
                    throw new InvalidDataException($"{g.Key} has no sound {index}");
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(tag.ValueAt), ev != null ? Ref(ev) : 0);
                if (stop != null)
                    BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan((e.Elements[index].FirstOrDefault(f => f.Name.Equals("deactivateevent", StringComparison.OrdinalIgnoreCase) && f.Size == 4) ?? throw new InvalidDataException($"{g.Key} sound {index} has no stop event")).ValueAt), Ref(stop));
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
        var kept = Sources(earlierPath);
        var others = draft.SoundPacks.Where(p => !Path.GetFileName(p).Equals(PackName, StringComparison.OrdinalIgnoreCase)).ToList();
        var sources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var renders = new List<(string Wem, byte[] Audio, PowerSoundEntry Entry)>();
        var patches = new List<Dictionary<string, string>>();
        var wems = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stopFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            var changes = new List<(string, int, string?, string?)>();
            foreach (var e in back.Where(e => e.Package.Equals(file, StringComparison.OrdinalIgnoreCase))) changes.Add((e.Component, e.Index, e.Event, StopOf(pkg, e)));
            foreach (var e in mine)
            {
                e.Stop = StopOf(pkg, e);
                if (!e.Replaced && (e.Off || !e.Shaped)) { changes.Add((e.Component, e.Index, e.Off ? null : e.Event, e.Stop)); continue; }
                string leaf = Leaf(e.Event), nleaf = leaf + Suffix, group = e.Event[..e.Event.LastIndexOf('.')];
                byte[]? stock = null;
                if (!e.Replaced)
                    // (a whole set reaches sounds that can't take it: they stay as they are)
                    try { stock = VoiceAudio.ToWav(VoiceAudio.Wem(e.Event, others, game.Cooked).Wem); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException) { log.Add($"{leaf}: left as it is ({ex.Message})"); changes.Add((e.Component, e.Index, e.Event, e.Stop)); continue; }
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
                // a loop: its stop event copied too, and pointed at the same new sound (patch below), or the copy would never end
                string? nstop = null;
                if (e.Stop != null)
                {
                    string sleaf = Leaf(e.Stop);
                    nstop = sleaf + Suffix;
                    // (two loops ending by one stop event: it can follow only one copy)
                    if (stopFor.TryGetValue(nstop, out string? other) && !other.Equals(nleaf, StringComparison.OrdinalIgnoreCase))
                    {
                        log.Add($"{leaf}: left as it is (its stop event also ends {Leaf(other)})");
                        changes.Add((e.Component, e.Index, e.Off ? null : e.Event, e.Stop));
                        continue;
                    }
                    stopFor[nstop] = nleaf;
                    if (!index.TryGetValue(SoundPack.Fnv(sleaf), out var swhere) || swhere.Bank != where.Bank) throw new InvalidDataException($"{sleaf}: its event isn't in the loop's sound bank");
                    int si = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(e.Stop, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("AkEvent", StringComparison.OrdinalIgnoreCase));
                    if (si < 0) throw new InvalidDataException($"{file}: the sound event {e.Stop} isn't in the package");
                    if (!pkg.Exports.Any(x => x.ObjectName.Equals(nstop, StringComparison.OrdinalIgnoreCase)))
                    {
                        var cp = CrossMove.Quiet(() => ExportCopy.Copy(pkg, si, pkg, [], nstop), out string said) ?? throw new InvalidDataException($"{sleaf}: the event couldn't be copied ({said.Trim()})");
                        pkg = Package.FromBytes(cp.Output);
                    }
                }
                changes.Add((e.Component, e.Index, e.Off ? null : group + "." + nleaf, nstop != null ? e.Stop![..e.Stop!.LastIndexOf('.')] + "." + nstop : null));
                if (!claimed.Add(nleaf + ".wem")) continue;   // (the same sound in another package: one new event)
                byte[]? fresh = newLines.GetValueOrDefault(leaf), audio = fresh ?? kept.GetValueOrDefault(nleaf + SourceEnd), wem = earlier?.GetValueOrDefault(nleaf + ".wem");
                if (fresh == null && wem != null && was.Any(w => w.Replaced == e.Replaced && w.Shape == e.Shape && Leaf(w.Event).Equals(leaf, StringComparison.OrdinalIgnoreCase)))
                    wems[nleaf + ".wem"] = wem;   // (not encoded again: it'd lose quality each time)
                else if (stock != null) renders.Add((nleaf + ".wem", stock, e));
                else
                {
                    audio ??= wem != null ? VoiceAudio.ToWav(wem) : throw new InvalidDataException($"{leaf}: its replacement isn't in the mod's sound pack");   // (a pack from before volume and filters)
                    renders.Add((nleaf + ".wem", audio, e));
                }
                if (e.Replaced && audio != null) sources[nleaf + SourceEnd] = audio;
                patches.Add(new Dictionary<string, string>
                {
                    ["type"] = "new_event", ["original_event_name"] = leaf, ["event_name"] = nleaf, ["event_hash"] = $"0x{SoundPack.Fnv(nleaf):X8}",
                    ["action_id"] = $"0x{SoundPack.Fnv(nleaf + "_action"):X8}", ["sound_id"] = $"0x{SoundPack.Fnv(nleaf + "_sound"):X8}", ["source_id"] = $"0x{SoundPack.Fnv(nleaf + "_source"):X8}",
                    ["wem_file"] = nleaf + ".wem", ["bank_name"] = bank, ["pck_file"] = where.Pck,
                });
                if (nstop != null)
                    patches.Add(new Dictionary<string, string>
                    {
                        // (the same sound_id: the copied stop action targets the loop's new sound; MHModManager takes this too, 2026-11-01)
                        ["type"] = "new_event", ["original_event_name"] = Leaf(e.Stop!), ["event_name"] = nstop, ["event_hash"] = $"0x{SoundPack.Fnv(nstop):X8}",
                        ["action_id"] = $"0x{SoundPack.Fnv(nstop + "_action"):X8}", ["sound_id"] = $"0x{SoundPack.Fnv(nleaf + "_sound"):X8}", ["source_id"] = $"0x{SoundPack.Fnv(nstop + "_source"):X8}",
                        ["wem_file"] = nleaf + ".wem", ["bank_name"] = bank, ["pck_file"] = where.Pck,
                    });
            }
            string dir = Directory.CreateDirectory(Path.Combine(work, Guid.NewGuid().ToString("N")[..6])).FullName, outFile = Path.Combine(dir, file);
            File.WriteAllBytes(outFile, Write(pkg, changes));
            if (k >= 0) draft.Packages[k] = (file, outFile); else draft.Packages.Add((file, outFile));
        }
        // (side by side: a whole set is a hundred sounds or more, and Save waits for it)
        var made = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(renders, r =>
        {
            byte[] wav = r.Entry.Shaped ? Render(r.Audio, r.Entry) : r.Audio;
            made[r.Wem] = WwiseVorbisWrite.ToWem(VorbisEncode.Encode(wav, 0.4f), WavPcm.Read(wav).Channels[0].Length);
        });
        foreach (var (n, b) in made) wems[n] = b;
        draft.SoundPacks.RemoveAll(p => Path.GetFileName(p).Equals(PackName, StringComparison.OrdinalIgnoreCase));
        if (patches.Count > 0)
        {
            string pack = Path.Combine(work, PackName);
            using (var z = ZipFile.Open(pack, ZipArchiveMode.Create))
            {
                using (var sw = new StreamWriter(z.CreateEntry("mod.json").Open()))
                    sw.Write(JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = "Power sounds", ["patches"] = patches }, MhoExtendedModManager.JsonOpts.Indented));
                foreach (var (n, b) in wems.Concat(sources)) using (var e = z.CreateEntry(n).Open()) e.Write(b);
            }
            draft.SoundPacks.Add(pack);
        }
        log.Add($"Power sounds: {draft.PowerSounds.Count(e => e.Off)} off, {patches.Count} changed");
        return work;
    }
}
