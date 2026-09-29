using System.Buffers.Binary;
using System.Text.RegularExpressions;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>A voice line turned off in the editor, kept in the manifest (VoiceOff) so it can be turned on again.</summary>
sealed class VoiceOffEntry
{
    public string Package { get; set; } = "";
    /// <summary>Where the event reference sits in the voice set's data (it doesn't move: only 4-byte values change).</summary>
    public int Offset { get; set; }
    /// <summary>The sound event (AkEvent) it played, by path, e.g. spidermanvo.EthanTheHuman_4013.</summary>
    public string Event { get; set; } = "";
}

/// <summary>One line of a voice set: the situation, extra detail (banter target, mission …), the event, whether it's off.</summary>
sealed record VoiceLine(string Package, int Offset, string Situation, string Detail, string Event, bool Off);

/// <summary>
/// A costume's voice set (Kurt, 2026-09-29: turn lines off, e.g. a donor voice naming its own team): the class default's
/// soundscomponent (MarvelEntityCompSounds; header = owner ref, TemplateName, NetIndex; properties from byte 16), each
/// situation naming a sound event (AkEvent) by reference: single properties (deathvo, levelupvo …), voiceoverlist entries
/// (votype, vo, vofirst), banterlist (type, target, banterevent), missionbanterlist (missionbantername, banterevent),
/// audioemotes and bantertargets (reference lists). A line is off when its reference is none; the editor keeps what it
/// turned off in the manifest so it can come back. Only costume packages with a filled set (more than the empty 24
/// bytes) have lines to edit: a voice mod's, or a costume moved to another hero.
/// </summary>
static class VoiceSet
{
    /// <summary>The package's voice set export (the costume class default's soundscomponent with content), or -1.</summary>
    public static int Find(Package pkg)
    {
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (e.SerialSize > 64 && pkg.ClassOf(e).Equals("MarvelEntityCompSounds", StringComparison.OrdinalIgnoreCase)
                && pkg.PathOf(e) is var path
                && (path.StartsWith("marvelgamecontent.default__marvelplayer_", StringComparison.OrdinalIgnoreCase) || path.StartsWith("marvelgamecontent.default__marvelplayeraudio_", StringComparison.OrdinalIgnoreCase))
                && e.ObjectName.Equals("soundscomponent", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>The lines of a package's voice set (empty when it has none); <paramref name="off"/> names lines turned off before.</summary>
    public static List<VoiceLine> Read(string packageFile, string path, IEnumerable<VoiceOffEntry> off)
    {
        var lines = new List<VoiceLine>();
        Package pkg;
        try { pkg = Package.Open(path); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return lines; }
        int vs = Find(pkg);
        if (vs < 0) return lines;
        byte[] d = pkg.ReadExportBytes(pkg.Exports[vs]).ToArray();
        var offAt = off.Where(o => o.Package.Equals(packageFile, StringComparison.OrdinalIgnoreCase)).ToDictionary(o => o.Offset, o => o.Event);
        void Add(int at, string situation, string detail)
        {
            int r = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
            if (r > 0 && r <= pkg.Exports.Length) lines.Add(new VoiceLine(packageFile, at, situation, detail, pkg.PathOf(pkg.Exports[r - 1]), false));
            else if (r == 0 && offAt.TryGetValue(at, out var ev)) lines.Add(new VoiceLine(packageFile, at, situation, detail, ev, true));
        }
        if (TagWalker.Walk(pkg, d, 16) is not { } tags) return lines;
        foreach (var t in tags)
        {
            string type = t.Type.ToLowerInvariant();
            if (type == "objectproperty" && t.Size == 4) { Add(t.ValueAt, Situation(t.Name), ""); continue; }
            if (type != "arrayproperty") continue;
            int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt));
            if (n <= 0) continue;
            // A list of structs (each its own properties, ending in None) …
            int pos = t.ValueAt + 4;
            var elements = new List<TagWalker>();
            for (int k = 0; k < n && pos < t.End; k++)
            {
                if (TagWalker.Walk(pkg, d, pos) is not { } el) { elements.Clear(); break; }
                elements.Add(el); pos = el.NoneAt + 8;
            }
            if (elements.Count == n && pos == t.End)
            {
                for (int k = 0; k < n; k++)
                {
                    var el = elements[k];
                    var detail = new List<string>();
                    foreach (var f in el)
                    {
                        string ft = f.Type.ToLowerInvariant();
                        if (ft == "byteproperty" && f.Size == 8) detail.Add(Pretty(TagWalker.NameAt(pkg, d, f.ValueAt)));
                        else if (ft == "nameproperty") detail.Add(Pretty(TagWalker.NameAt(pkg, d, f.ValueAt)));
                        else if (ft == "intproperty" && f.Size == 4) detail.Add($"target {BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(f.ValueAt))}");
                    }
                    foreach (var f in el.Where(f => f.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) && f.Size == 4))
                        Add(f.ValueAt, Situation(t.Name), string.Join(" · ", detail) + (f.Name.EndsWith("first", StringComparison.OrdinalIgnoreCase) ? " (first time)" : ""));
                }
                continue;
            }
            // … or a list of references (audioemotes, bantertargets).
            if (t.Size == 4 + n * 4)
                for (int k = 0; k < n; k++) Add(t.ValueAt + 4 + 4 * k, Situation(t.Name), $"#{k + 1}");
        }
        return lines;
    }

    /// <summary>
    /// The package with the given lines turned off (reference none) or on again (their event's reference), as verified bytes
    /// (PackageRebuilder: every other byte kept). Null when nothing changes.
    /// </summary>
    public static byte[]? Write(string path, IReadOnlyCollection<(int Offset, string? Event)> changes)
    {
        var pkg = Package.Open(path);
        int vs = Find(pkg);
        if (vs < 0 || changes.Count == 0) return null;
        byte[] d = pkg.ReadExportBytes(pkg.Exports[vs]).ToArray();
        foreach (var (at, ev) in changes)
        {
            int r = 0;
            if (ev != null)
            {
                int i = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(ev, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(e).Equals("AkEvent", StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw new InvalidDataException($"the sound event {ev} isn't in the package any more");
                r = i + 1;
            }
            if (at < 16 || at + 4 > d.Length) throw new InvalidDataException($"line at {at} is outside the voice set");
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at), r);
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>> { [vs] = _ => d }, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, [vs], [], written);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        return output;
    }

    /// <summary>A stock voice set to copy into a costume: the hero's own (base package) or a voice package's (Lady Deadpool …).</summary>
    public sealed record Source(string Title, string Hero, string File, bool Alternate);

    static (string Cooked, List<Source> List)? sources;
    static readonly object sourcesGate = new();

    /// <summary>
    /// Every stock voice set (read once per game folder): each hero's base package (UC__MarvelPlayer_&lt;Hero&gt;_SF: its class
    /// default's soundscomponent, e.g. Storm's 144 events) and the voice packages (UC__MarvelPlayerAudio_&lt;Hero&gt;_&lt;Voice&gt;_SF,
    /// 40: Spider-Man's own voice is Spiderman_Default, plus Lady Deadpool, Spider-Gwen …). Only sets with lines count (a
    /// stock costume's own is empty: its hero's applies).
    /// </summary>
    public static List<Source> Sources(string cooked)
    {
        lock (sourcesGate)
        {
            if (sources is { } c && c.Cooked == cooked) return c.List;
            var list = new List<Source>();
            var baseRx = new Regex(@"^UC__MarvelPlayer_([A-Za-z0-9]+)_SF\.upk$", RegexOptions.IgnoreCase);
            var audioRx = new Regex(@"^UC__MarvelPlayerAudio_([A-Za-z0-9]+)_([A-Za-z0-9]+)_SF\.upk$", RegexOptions.IgnoreCase);
            foreach (string f in Directory.EnumerateFiles(cooked, "UC__MarvelPlayer*_SF.upk"))
            {
                string name = Path.GetFileName(f);
                var mb = baseRx.Match(name); var ma = audioRx.Match(name);
                if (!mb.Success && !ma.Success) continue;
                try { if (Find(Package.Open(f)) < 0) continue; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
                if (mb.Success) list.Add(new Source(HeroName(mb.Groups[1].Value), mb.Groups[1].Value, f, false));
                else
                {
                    string hero = ma.Groups[1].Value, voice = ma.Groups[2].Value;
                    bool own = voice.Equals("Default", StringComparison.OrdinalIgnoreCase);
                    list.Add(new Source(HeroName(hero) + " · " + (own ? "Default" : VoiceName(voice)), hero, f, !own));
                }
            }
            // A voice package named Default is the hero's own voice when the base package has none (Spider-Man, Deadpool,
            // Thor …); only when both exist does it need telling apart.
            var withBase = list.Where(x => !x.Alternate && !Path.GetFileName(x.File).StartsWith("UC__MarvelPlayerAudio_", StringComparison.OrdinalIgnoreCase)).Select(x => x.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list = [.. list.Select(x => x.Title.EndsWith(" · Default") && !withBase.Contains(x.Title[..^10]) ? x with { Title = x.Title[..^10] } : x)
                .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)];
            sources = (cooked, list);
            return list;
        }
    }

    static string VoiceName(string v) => v.ToLowerInvariant() switch
    {
        "gotgmovie" => "GotG Movie",
        "jackolantern" => "Jack O'Lantern",
        "fearitself" => "Fear Itself",
        "earthx" => "Earth X",
        "spidergwen" => "Spider-Gwen",
        "spidergirl" => "Spider-Girl",
        "spidercarnage" => "Spider-Carnage",
        _ => char.ToUpperInvariant(v[0]) + SplitWords(v)[1..],
    };

    static string SplitWords(string s) => Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");
    static string HeroName(string token) => AutoTags.DisplayName(token) ?? SplitWords(token);

    /// <summary>The hero a costume package is for ("UC__MarvelPlayer_Storm_Modern_SF.upk" → Storm), or null.</summary>
    public static string? HeroOf(string packageFile)
    {
        var m = Regex.Match(packageFile, @"^UC__MarvelPlayer_([A-Za-z0-9]+)_", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>A costume package's class-default soundscomponent (empty or not), or -1.</summary>
    public static int Slot(Package pkg) => Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals("soundscomponent", StringComparison.OrdinalIgnoreCase)
        && pkg.ClassOf(e).Equals("MarvelEntityCompSounds", StringComparison.OrdinalIgnoreCase)
        && pkg.PathOf(e).StartsWith("marvelgamecontent.default__marvelplayer_", StringComparison.OrdinalIgnoreCase)
        && !pkg.PathOf(e).StartsWith("marvelgamecontent.default__marvelplayeraudio_", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Copies a voice set (source package's soundscomponent <paramref name="vc"/>, with its sound events and banks) into
    /// <paramref name="pkg"/> under <paramref name="targetDefault"/> (MPM ExportCopy; objects already there by path and class
    /// are reused). Returns the new package, the target's own soundscomponent and the data to put in it, or null (reason
    /// logged). Reference lists (audioemotes, bantertargets) may hold stale stock values (She-Hulk's bantertargets[1] points at
    /// a property of her anger-meter UI class): only sound events are kept, anything else becomes none.
    /// </summary>
    public static (Package Pkg, int Comp, byte[] Data)? CopyInto(Package pkg, string targetDefault, Package vp, int vc, List<string> log)
    {
        string compPath = vp.PathOf(vp.Exports[vc]);
        string defName = compPath[..compPath.LastIndexOf('.')];
        var vreplace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [defName] = targetDefault };
        byte[] vd = vp.ReadExportBytes(vp.Exports[vc]).ToArray();
        if (TagWalker.Walk(vp, vd, 16) is { } vtags)
            foreach (var t in vtags.Where(t => t.Name is var n && (n.Equals("AudioEmotes", StringComparison.OrdinalIgnoreCase) || n.Equals("BanterTargets", StringComparison.OrdinalIgnoreCase))))
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(vd.AsSpan(t.ValueAt));
                if (t.Size != 4 + n * 4) continue;
                for (int k = 0; k < n; k++)
                {
                    int r = BinaryPrimitives.ReadInt32LittleEndian(vd.AsSpan(t.ValueAt + 4 + 4 * k));
                    if (r > 0 && r <= vp.Exports.Length && !vp.ClassOf(vp.Exports[r - 1]).Equals("AkEvent", StringComparison.OrdinalIgnoreCase))
                    { vreplace[vp.PathOf(vp.Exports[r - 1])] = "none"; log.Add($"voice: {t.Name}[{k}] pointed at {vp.PathOf(vp.Exports[r - 1])}; set to none"); }
                }
            }
        // The copy is a spare export beside the costume's own soundscomponent (its data then goes into that one), under a
        // name the package doesn't have yet (a costume can be given a voice more than once).
        string spare = "soundscomponent_voice";
        for (int k = 2; pkg.Exports.Any(e => pkg.PathOf(e).Equals(targetDefault + "." + spare, StringComparison.OrdinalIgnoreCase)); k++) spare = "soundscomponent_voice" + k;
        var vcopy = CrossMove.Quiet(() => ExportCopy.Copy(vp, vc, pkg, [], spare, vreplace), out string vsaid);
        if (vcopy == null)
        {
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_DEBUG") == "1") log.Add(vsaid);
            log.Add("voice not copied: " + string.Join(" ", vsaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("already")).Select(l => l.Trim())));
            return null;
        }
        var outPkg = Package.FromBytes(vcopy.Output);
        byte[] data = outPkg.ReadExportBytes(outPkg.Exports[vcopy.RootRef - 1]).ToArray();
        int comp = Array.FindIndex(outPkg.Exports, e => outPkg.PathOf(e).Equals(targetDefault + ".soundscomponent", StringComparison.OrdinalIgnoreCase));
        if (comp < 0) { log.Add("voice not copied: the costume has no soundscomponent"); return null; }
        return (outPkg, comp, data);
    }

    /// <summary>
    /// A costume package with another voice set: the source's (a stock voice from Sources) copied in with its sound events,
    /// replacing the costume's own (empty = its hero's). Verified package bytes; throws InvalidDataException with the reason.
    /// </summary>
    public static byte[] Replace(string costumePackage, string sourcePackage, List<string> log)
    {
        var pkg = Package.Open(costumePackage);
        int slot = Slot(pkg);
        if (slot < 0) throw new InvalidDataException($"{Path.GetFileName(costumePackage)} isn't a costume package (no voice slot)");
        string compPath = pkg.PathOf(pkg.Exports[slot]);
        string targetDefault = compPath[..compPath.LastIndexOf('.')];
        var vp = Package.Open(sourcePackage);
        int vc = Find(vp);
        if (vc < 0) throw new InvalidDataException($"{Path.GetFileName(sourcePackage)} has no voice set");
        var got = CopyInto(pkg, targetDefault, vp, vc, log) ?? throw new InvalidDataException(log.LastOrDefault() ?? "the voice couldn't be copied");
        var replaceData = new Dictionary<int, Func<long, byte[]>> { [got.Comp] = _ => got.Data };
        byte[] output = PackageRebuilder.Rebuild(got.Pkg, replaceData, [], out var written);
        var problems = PackageRebuilder.Verify(got.Pkg, output, [got.Comp], [], written);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        log.Add($"voice: {vp.PathOf(vp.Exports[vc])} from {Path.GetFileName(sourcePackage)} ({vp.Exports[vc].SerialSize:N0} bytes) → {compPath}");
        return output;
    }

    /// <summary>"deathvo" → "Death", "lowhealthentervofirst" → "Low Health Enter (first time)" …</summary>
    static string Situation(string property)
    {
        string p = property.ToLowerInvariant();
        return p switch
        {
            "voiceoverlist" => "Voice Over",
            "banterlist" => "Banter",
            "missionbanterlist" => "Mission Banter",
            "audioemotes" => "Emote",
            "bantertargets" => "Banter Target",
            _ => Words(p),
        };
    }

    static readonly string[] Parts = ["environmental", "banter", "lowhealth", "low", "health", "enter", "power", "fail", "no", "endurance", "bad", "target", "other",
        "accept", "resurrect", "death", "activation", "start", "inventory", "full", "crit", "hit", "received", "landed", "damage", "med", "high",
        "victory", "boss", "mob", "defeat", "group", "destroyed", "entity", "idle", "find", "item", "level", "up", "taunted", "slowed", "rooted",
        "stunned", "knocked", "back", "down", "feared", "locked", "unlocked", "heat", "cold", "first", "vo"];

    /// <summary>Splits a run-together property name into words (greedy, longest known word first).</summary>
    static string Words(string p)
    {
        var words = new List<string>();
        bool first = p.EndsWith("first");
        if (first) p = p[..^5];
        if (p.EndsWith("vo")) p = p[..^2];
        int i = 0;
        while (i < p.Length)
        {
            string? w = Parts.Where(x => x != "first" && x != "vo" && p.AsSpan(i).StartsWith(x)).OrderByDescending(x => x.Length).FirstOrDefault();
            if (w == null) { words.Add(p[i..]); break; }
            words.Add(w); i += w.Length;
        }
        string s = string.Join(' ', words.Select(w => w == "lowhealth" ? "Low Health" : char.ToUpperInvariant(w[0]) + w[1..]));
        return first ? s + " (First Time)" : s;
    }

    /// <summary>"msn_stopartifacttheft_approachtablet" → "Msn Stopartifacttheft Approachtablet"; enum values keep their words.</summary>
    static string Pretty(string n)
    {
        int colon = n.IndexOf("::", StringComparison.Ordinal);
        if (colon >= 0) n = n[(colon + 2)..];
        return n.ToLowerInvariant() switch
        {
            "bt_killedother" => "Defeated Enemy",
            "bt_encounter" => "Encounter",
            "bt_interplay" => "Team-Up Talk",
            "vt_taunt" => "Taunt",
            var x when x.StartsWith("vt_") => char.ToUpperInvariant(x[3]) + x[4..].Replace('_', ' '),
            var x when x.StartsWith("bt_") => char.ToUpperInvariant(x[3]) + x[4..].Replace('_', ' '),
            _ => n.Replace('_', ' '),
        };
    }
}
