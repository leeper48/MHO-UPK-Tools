using System.Buffers.Binary;
using System.Text.RegularExpressions;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>A power's colour change (the editor's Powers tab), kept in the manifest (PowerColors) so it can be changed again:
/// the power (its prototype path), its name, the colour, and the packages built for it (recoloured stock power packages, in
/// the mod's packages).</summary>
/// <summary>A power's look borrowed from another hero's power (the Powers tab's Borrow Look; PowerBorrow).</summary>
sealed class PowerBorrowEntry
{
    /// <summary>The power that takes the look (its prototype), and its name.</summary>
    public string Power { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The power the look comes from, and its name ("Human Torch: Burning Volley").</summary>
    public string Donor { get; set; } = "";
    /// <summary>The power's name the borrow put in (Kurt, 2026-10-23: Rename This Power): its string ID, so Game's Look takes
    /// it off again. Null: the name wasn't changed.</summary>
    public ulong? NameId { get; set; }
    /// <summary>The icons the borrow put in (Use Its Icon): "package|texture", taken off by Game's Look.</summary>
    public List<string>? Icons { get; set; }
    public string DonorName { get; set; } = "";
    /// <summary>Keep the target's components and timing, take only what they draw (powers built differently: a beam from a
    /// channeled beam); false: the donor's components, animation and projectile look.</summary>
    public bool KeepTiming { get; set; }
    /// <summary>The donor's animation played at the rate that fits it to the target's own (whole-power borrows).</summary>
    public bool Fit { get; set; } = true;
    /// <summary>Keep This Hero's Weapons (Kurt, 2026-10-23: Cap's shield vanished during Hammer of Storms): the weapons this
    /// hero always holds stay shown while the borrowed power runs (they're hidden by default: the other hero's animation
    /// isn't made to hold them).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepWeapons { get; set; }
    /// <summary>The hero parts of the two heroes' base packages (UC__MarvelPlayer_&lt;hero&gt;_SF): not always the prototype
    /// folder's name (Captain Marvel's is MsMarvel). Null: worked out at the build.</summary>
    public string? Hero { get; set; }
    public string? DonorHero { get; set; }
    /// <summary>The packages it built (power, projectile, the hero's base package for its animations).</summary>
    public List<string> Packages { get; set; } = [];
    /// <summary>Vehicle Fit (a borrowed ride's vehicle, Kurt 2026-10-21): the vehicle's size (1 = as made; null: the two
    /// heroes' heights decide), and the rider moved forward (+) / back and up (+) / down in game units and leaned forward (+)
    /// / back in degrees.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? VehicleScale { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float RiderForward { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float RiderUp { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float RiderLean { get; set; }
    /// <summary>Set by the last build (not saved): the vehicle's automatic size when this borrow brings a vehicle, else null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float? VehicleAuto { get; set; }
    /// <summary>Prop Fit (Kurt, 2026-10-21): each brought prop's (vehicle, mount or weapon) size, move and turn, by its class.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PropFit>? Props { get; set; }
    /// <summary>Set by the last build (not saved): the props this borrow brings (class, vehicle, automatic size); null: not built.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<PowerBorrow.SpawnedProp>? Spawned { get; set; }
}

/// <summary>A brought prop's fit: its size (null: automatic: a vehicle sized for the hero, else as made), moved along its
/// model's X / Y / Z (game units) and turned about them (degrees).</summary>
/// <summary>
/// A prop added to a costume from another character (Kurt, 2026-10-23: Thor's hammer in Captain America's hand for his idles):
/// its attachment class copied into the costume's package and added to the costume's own props (mAttachmentClasses), fitted
/// like a borrowed power's prop. Manifest AddedProps (an extension; a legacy export leaves it out, the package keeps it).
/// </summary>
sealed class AddedPropEntry
{
    /// <summary>The costume package it's added to.</summary>
    public string Package { get; set; } = "";
    /// <summary>The package it comes from (a game package file name, UC__MarvelPlayer_Thor_SF.upk).</summary>
    public string From { get; set; } = "";
    /// <summary>Its attachment class (marvelattachment_thorhammer).</summary>
    public string Class { get; set; } = "";
    /// <summary>"Thor: Thorhammer", as the Animations tab lists it.</summary>
    public string Name { get; set; } = "";
    /// <summary>The weapon slot it's held in (its WeaponSlot: righthand), so a borrowed power that shows that slot uses it.</summary>
    public string? Slot { get; set; }
    /// <summary>Its fit (size, move, turn; Class unused).</summary>
    public PropFit Fit { get; set; } = new();
    /// <summary>A model of its own in place of its character's (Kurt, 2026-10-27: built on the Model tab): the package the
    /// Model tab built it into, relative to the mod's Model folder (added_models\…); null: its character's own.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelFrom { get; set; }
    /// <summary>The character bone it's held on in place of its own (Kurt, 2026-10-27: the Model tab's Held On); null: as its
    /// character holds it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Bone { get; set; }
    /// <summary>A prop of the user's own (Kurt, 2026-10-27: Add a Prop → From an FBX): its class only carries it, its model is
    /// built as made (its size and shape kept, not laid over the carrier's model).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepShape { get; set; }
    /// <summary>Left off the costume for now (unticked in the Model tab's list), its settings kept; ticked, it goes back on.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Off { get; set; }
    /// <summary>Per animation, a further move and turn (Kurt, 2026-10-23: a pose that wants the prop elsewhere), baked into
    /// that animation only (PropHold, with Pivot on the Hand); null when none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<PropAnimFit>? Anims { get; set; }
}

/// <summary>
/// One of a costume's own props held on another bone (Kurt, 2026-10-27: the Model tab's Held On): its attachment class
/// default's AttachmentBones set in the package. Manifest PropBones (an extension; the package keeps it).
/// </summary>
sealed class PropBoneEntry
{
    public string Package { get; set; } = "";
    /// <summary>Its attachment class (marvelattachment_thorhammer_ageofultron).</summary>
    public string Class { get; set; } = "";
    public string Bone { get; set; } = "";
    /// <summary>The class default's own AttachmentBones before (empty: none of its own, its parent class's).</summary>
    public List<string> Original { get; set; } = [];
}

/// <summary>An added prop's adjustment in one animation (its slot), on top of its fit: moved along the character's axes
/// (game units: the 3D view's axis gizmo, X the way the character faces, Z up) and turned about them around the bone it
/// hangs on (degrees), the same in every frame (Kurt, 2026-10-23: about the bone's own axes, the swinging hammer's, they
/// moved unpredictably).</summary>
sealed class PropAnimFit
{
    public string Slot { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float X { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float Y { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float Z { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float TurnX { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float TurnY { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float TurnZ { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsZero => X == 0 && Y == 0 && Z == 0 && TurnX == 0 && TurnY == 0 && TurnZ == 0;
    /// <summary>The turn, about the character's axes.</summary>
    public System.Numerics.Matrix4x4 Turn()
    {
        static int Units(float deg) => (int)MathF.Round(deg / 360f * 65536f);
        return ModMeshes.Rotator(Units(TurnY), Units(TurnZ), Units(TurnX));
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Numerics.Vector3 Move => new(X, Y, Z);
}

sealed class PropFit
{
    public string Class { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? Scale { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float X { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float Y { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float Z { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float TurnX { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float TurnY { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public float TurnZ { get; set; }
    /// <summary>Pivot on the Hand (Kurt, 2026-10-23): the borrowed animations stop moving the bone the prop hangs on (Thor's
    /// Hammer of Storms moves g_r_palm out along the strap), so the prop turns about the hand; Move places it from there.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PinPivot { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsIdentity => Scale == null && X == 0 && Y == 0 && Z == 0 && TurnX == 0 && TurnY == 0 && TurnZ == 0 && !PinPivot;
}

sealed class PowerColorEntry
{
    public string Power { get; set; } = "";
    public string Name { get; set; } = "";
    public float Hue { get; set; }
    public float Saturation { get; set; } = 1;
    public float Brightness { get; set; } = 1;
    /// <summary>An NPC's or enemy's power (Fx.AgentPowers): the mod's package of that character; null for a hero's.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Owner { get; set; }
    /// <summary>The Opacity slider (1 = as the game has it; 0 = the effects off).</summary>
    public float Opacity { get; set; } = 1;
    public List<string> Packages { get; set; } = [];
    /// <summary>Single colors replaced (hex "#RRGGBB" → "#RRGGBB", tolerance 0–1); null when none (Kurt, 2026-10-03).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ColorMapEntry>? Maps { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public PowerColor Color => new(Hue, Saturation, Brightness)
    {
        Opacity = Opacity,
        Maps = Maps?.Select(m => ColorMap.FromHex(m.From) is { } f && ColorMap.FromHex(m.To) is { } t ? new ColorMap(f, t, m.Tolerance) : null).OfType<ColorMap>().ToList() ?? [],
    };
}

/// <summary>A single color replaced in a power (PowerColorEntry.Maps).</summary>
sealed class ColorMapEntry
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public float Tolerance { get; set; } = 0.3f;
}

/// <summary>A voice line turned off in the editor, kept in the manifest (VoiceOff) so it can be turned on again.</summary>
sealed class VoiceOffEntry
{
    public string Package { get; set; } = "";
    /// <summary>Where the event reference sits in the voice set's data (it doesn't move: only 4-byte values change).</summary>
    public int Offset { get; set; }
    /// <summary>The sound event (AkEvent) it played, by path, e.g. spidermanvo.EthanTheHuman_4013.</summary>
    public string Event { get; set; } = "";
    /// <summary>A situation the costume's voice (moved from another hero or a team-up) has no line for, set to none so the
    /// target hero's own line doesn't play (Kurt, 2026-10-02: Rescue on Iron Man still said Iron Man's "on cooldown"). Event
    /// is the hero's line it replaces; it can't be turned on (that event isn't in the package). Null when not such a line.</summary>
    public bool? Missing { get; set; }
    public string? Situation { get; set; }
    public string? Detail { get; set; }
}

/// <summary>One line of a voice set: the situation, extra detail (banter target, mission …), the event, whether it's off.</summary>
/// <remarks>Sound is false for an entry that names something other than a sound event: banter targets name a character class
/// (Carnage's only entry is marvelplayer_carnage), Storm's emotes an animation. Nothing to play or shift there.</remarks>
sealed record VoiceLine(string Package, int Offset, string Situation, string Detail, string Event, bool Off, bool Missing = false, bool Sound = true);

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
                && (path.StartsWith("marvelgamecontent.default__marvelplayer_", StringComparison.OrdinalIgnoreCase) || path.StartsWith("marvelgamecontent.default__marvelplayeraudio_", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("marvelgamecontent.default__marvelteamup_", StringComparison.OrdinalIgnoreCase))   // (team-ups: the same component, Kurt 2026-10-16)
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
        var offAt = off.Where(o => o.Package.Equals(packageFile, StringComparison.OrdinalIgnoreCase)).GroupBy(o => o.Offset).ToDictionary(g => g.Key, g => g.First());
        void Add(int at, string situation, string detail)
        {
            int r = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
            if (r > 0 && r <= pkg.Exports.Length)
                lines.Add(new VoiceLine(packageFile, at, situation, detail, pkg.PathOf(pkg.Exports[r - 1]), false, Sound: pkg.ClassOf(pkg.Exports[r - 1]).Equals("AkEvent", StringComparison.OrdinalIgnoreCase)));
            else if (r == 0 && offAt.TryGetValue(at, out var o)) lines.Add(new VoiceLine(packageFile, at, situation, detail, o.Event, true, o.Missing == true));
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
        // Lines the moved voice has no entry for, in a list it left empty (not a reference here: listed from the manifest).
        foreach (var o in offAt.Values.Where(o => o.Missing == true && !lines.Any(l => l.Offset == o.Offset)))
            lines.Add(new VoiceLine(packageFile, o.Offset, o.Situation ?? "", o.Detail ?? "", o.Event, true, true));
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
    public sealed record Source(string Title, string Hero, string File, bool Alternate)
    {
        /// <summary>A team-up's voice (UC__MarvelTeamUp_&lt;Name&gt;_SF: Angel, Jessica Jones …; a user asked, 2026-10-16).</summary>
        public bool TeamUp { get; init; }
    }

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
                // Only sets with sound lines (Carnage's base package holds just a banter target: his voice is the
                // MarvelPlayerAudio_Carnage_Default class his Classic costume extends).
                try { if (Find(Package.Open(f)) < 0 || !Read(name, f, []).Any(l => l.Sound)) continue; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
                if (mb.Success) list.Add(new Source(HeroName(mb.Groups[1].Value), mb.Groups[1].Value, f, false));
                else
                {
                    string hero = ma.Groups[1].Value, voice = ma.Groups[2].Value;
                    bool own = voice.Equals("Default", StringComparison.OrdinalIgnoreCase);
                    list.Add(new Source(HeroName(hero) + " · " + (own ? "Default" : VoiceName(voice)), hero, f, !own));
                }
            }
            // Costumes with a voice of their own (Kurt, 2026-10-30: Captain America's American Dream, whose package is named
            // CapnAmerica, didn't show): an alternate voice of their hero
            var costumeRx = new Regex(@"^UC__MarvelPlayer_([A-Za-z0-9]+)_([A-Za-z0-9_]+)_SF\.upk$", RegexOptions.IgnoreCase);
            var found = new System.Collections.Concurrent.ConcurrentBag<Source>();
            Parallel.ForEach(Directory.EnumerateFiles(cooked, "UC__MarvelPlayer_*_SF.upk"), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, f =>
            {
                string name = Path.GetFileName(f);
                var mc = costumeRx.Match(name);
                if (!mc.Success || name.Contains("bak", StringComparison.OrdinalIgnoreCase) || name.Contains("copy", StringComparison.OrdinalIgnoreCase)) return;
                string stock = StockFiles.For(cooked, name);   // (the game's own: a costume a mod changed isn't offered with the mod's voice)
                try { if (Find(Package.Open(stock)) < 0 || !Read(name, stock, []).Any(l => l.Sound)) return; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return; }
                string hero = HeroAlias(mc.Groups[1].Value);
                found.Add(new Source(HeroName(hero) + " · " + string.Join(" ", mc.Groups[2].Value.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(VoiceName)), hero, stock, true));
            });
            list.AddRange(found);
            // Team-ups: the same voice set in their class default (Angel: 52 lines: activation, attack, boast, banter with
            // the heroes they team up with …)
            var teamRx = new Regex(@"^UC__MarvelTeamUp_([A-Za-z0-9]+)_SF\.upk$", RegexOptions.IgnoreCase);
            foreach (string f in Directory.EnumerateFiles(cooked, "UC__MarvelTeamUp_*_SF.upk"))
            {
                string name = Path.GetFileName(f);
                var mt = teamRx.Match(name);
                if (!mt.Success || name.Contains("bak", StringComparison.OrdinalIgnoreCase) || name.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                try { if (Find(Package.Open(f)) < 0 || !Read(name, f, []).Any(l => l.Sound)) continue; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
                list.Add(new Source(HeroName(mt.Groups[1].Value) + " (Team-Up)", mt.Groups[1].Value, f, false) { TeamUp = true });
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
    /// <summary>A package's hero name as its hero package spells it (American Dream's package says CapnAmerica).</summary>
    public static string HeroAlias(string token) => token.ToLowerInvariant() switch
    {
        "capnamerica" => "CaptainAmerica",
        _ => token,
    };

    static string HeroName(string token) => AutoTags.DisplayName(token) ?? SplitWords(token);

    /// <summary>
    /// Where a hero's voice comes from in the game when a mod's packages have none (Kurt, 2026-10-02: Scream on Carnage's
    /// base package showed one entry). The hero's default costume (its avatar's StartingCostume) and the voice set it plays:
    /// a costume class extending a MarvelPlayerAudio_&lt;Hero&gt;_&lt;Voice&gt; class plays that voice package's set (Carnage Classic →
    /// Carnage · Default), else the hero's base package's. Null when the default costume has no package of its own (its class
    /// is the hero's base class) or no voice is found.
    /// </summary>
    public static (string CostumeFile, string CostumeName, Source Voice)? HeroVoice(string gameRoot, string cooked, string hero)
    {
        var costume = (Costume.All(gameRoot) ?? []).FirstOrDefault(c => c.IsDefault && c.Hero != null
            && Path.GetFileNameWithoutExtension(c.Hero).Equals(hero, StringComparison.OrdinalIgnoreCase));
        if (costume == null) return null;
        string file = "UC__" + costume.Class + "_SF.upk";
        if (file.Equals($"UC__MarvelPlayer_{hero}_SF.upk", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(cooked, file))) return null;
        var all = Sources(cooked);
        Source? voice = null;
        try
        {
            var pkg = Package.Open(StockFiles.For(cooked, file));
            string? audio = pkg.Names.FirstOrDefault(n => n.StartsWith("marvelplayeraudio_", StringComparison.OrdinalIgnoreCase));
            if (audio != null) voice = all.FirstOrDefault(v => Path.GetFileName(v.File).Equals("UC__" + audio + "_SF.upk", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return null; }
        voice ??= all.FirstOrDefault(v => Path.GetFileName(v.File).Equals($"UC__MarvelPlayer_{hero}_SF.upk", StringComparison.OrdinalIgnoreCase));
        if (voice == null) return null;
        string name = costume.Prototype.Split('\\', '/')[^1];
        if (name.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)) name = name[..^10];
        return (file, Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " "), voice);
    }

    /// <summary>A team-up package's name ("UC__MarvelTeamUp_Angel_SF.upk" → Angel), or null.</summary>
    public static string? TeamUpOf(string packageFile)
    {
        var m = Regex.Match(packageFile, @"^UC__MarvelTeamUp_([A-Za-z0-9]+)_", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The hero a costume package is for ("UC__MarvelPlayer_Storm_Modern_SF.upk" → Storm), or null.</summary>
    public static string? HeroOf(string packageFile)
    {
        var m = Regex.Match(packageFile, @"^UC__MarvelPlayer_([A-Za-z0-9]+)_", RegexOptions.IgnoreCase);
        return m.Success ? HeroAlias(m.Groups[1].Value) : null;
    }

    /// <summary>An export's archetype reference (the export table entry's last int).</summary>
    static int EntryArchetype(Package pkg, int export) => BinaryPrimitives.ReadInt32LittleEndian(pkg.Body.AsSpan(pkg.ExportEntryStart[export] + 20));

    /// <summary>
    /// The spare voice components an earlier Use Another Voice left (soundscomponent_voice…, under the costume's class
    /// default) whose archetype is an import other than the costume's own soundscomponent's: pointed at that one, in place
    /// (the export table entry's archetype int, same size; checked by reading back). Before 0.60.243 such a copy from an audio
    /// package kept its hero's class default component as the archetype and the game asserted while loading the costume; a
    /// voice set back to the mod's own kept the spare, so the crash stayed. Returns the bytes as they were when there's nothing.
    /// </summary>
    public static byte[] RepairSpares(byte[] file, List<string> log)
    {
        var pkg = Package.FromBytes(file);
        int own = Slot(pkg);
        if (own < 0) return file;
        int want = EntryArchetype(pkg, own);
        if (want == 0) return file;
        if (pkg.Body.Length != pkg.RawFile.Length) return file;   // (a compressed package: never one we wrote)
        string ownPath = pkg.PathOf(pkg.Exports[own]);
        string parent = ownPath[..ownPath.LastIndexOf('.')];
        byte[]? o = null;
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            string path = pkg.PathOf(pkg.Exports[i]);
            if (!path.StartsWith(parent + ".soundscomponent_voice", StringComparison.OrdinalIgnoreCase)) continue;
            int arch = EntryArchetype(pkg, i);
            if (arch >= 0 || arch == want) continue;
            o ??= (byte[])file.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(pkg.ExportEntryStart[i] + 20), want);
            log.Add($"voice: {path} made from the costume's own component (it was {ModMeshes.ImportPath(pkg, arch)}, another hero's: it crashed the game on load)");
        }
        if (o == null) return file;
        // the other hero's class-default imports nothing uses any more (the spare's old archetype and its outer): made copies
        // of the Core import, so nothing is left that names a package the game doesn't load for this costume
        // (again until nothing changes: an import whose only user was one made harmless, its outer, goes next)
        for (bool changed = true; changed;)
        {
        changed = false;
        var mid = Package.FromBytes(o);
        int size = mid.Imports.Length == 0 ? 0 : (mid.ExportOffset - mid.ImportOffset) / mid.Imports.Length;
        int core = Array.FindIndex(mid.Imports, im => im.OuterIndex == 0 && im.ObjectName.Equals("Core", StringComparison.OrdinalIgnoreCase));
        if (size == 28 && core >= 0)
        {
            HashSet<int>? used = new();
            for (int i = 0; i < mid.Exports.Length; i++)
            {
                // (an object the parser can't read, a stock one of a class it doesn't know, existed before any voice copy:
                // its table entry is checked, its data can't point at the other hero's imports the copy brought in)
                var refs = ExportCopy.ReferencesOf(mid, i) ?? [.. new[] { 0, 4, 8, 20 }.Select(at => BinaryPrimitives.ReadInt32LittleEndian(mid.Body.AsSpan(mid.ExportEntryStart[i] + at)))];
                foreach (int r in refs) if (r < 0) used.Add(r);
            }
            if (used != null)
            {
                foreach (var im in mid.Imports) if (im.OuterIndex < 0) used.Add(im.OuterIndex);
                // (the costume's own hero's class default, the one its own component is made from, is never touched)
                string ownArch = want < 0 ? ModMeshes.ImportPath(mid, want) : mid.PathOf(mid.Exports[want - 1]);
                string ownHero = ownArch.Contains('.') ? ownArch[..ownArch.LastIndexOf('.')] : ownArch;
                for (int k = 0; k < mid.Imports.Length; k++)
                {
                    string ip = ModMeshes.ImportPath(mid, -(k + 1));
                    if (used.Contains(-(k + 1)) || !ip.StartsWith("marvelgamecontent.default__marvelplayer_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ip.Equals(ownHero, StringComparison.OrdinalIgnoreCase) || ip.StartsWith(ownHero + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    Array.Copy(o, mid.ImportOffset + core * size, o, mid.ImportOffset + k * size, size);
                    log.Add($"voice: the unused import {ip} (another hero's) made harmless");
                    changed = true;
                }
            }
        }
        }
        var back = Package.FromBytes(o);
        for (int i = 0; i < back.Exports.Length; i++)
            if (back.PathOf(back.Exports[i]).StartsWith(parent + ".soundscomponent_voice", StringComparison.OrdinalIgnoreCase) && EntryArchetype(back, i) < 0 && EntryArchetype(back, i) != want)
                throw new InvalidDataException("the voice components didn't read back as written");
        return o;
    }

    /// <summary>A costume package's class-default soundscomponent (empty or not), or -1.</summary>
    public static int Slot(Package pkg) => Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals("soundscomponent", StringComparison.OrdinalIgnoreCase)
        && pkg.ClassOf(e).Equals("MarvelEntityCompSounds", StringComparison.OrdinalIgnoreCase)
        && (pkg.PathOf(e).StartsWith("marvelgamecontent.default__marvelplayer_", StringComparison.OrdinalIgnoreCase)
            && !pkg.PathOf(e).StartsWith("marvelgamecontent.default__marvelplayeraudio_", StringComparison.OrdinalIgnoreCase)
            || pkg.PathOf(e).StartsWith("marvelgamecontent.default__marvelteamup_", StringComparison.OrdinalIgnoreCase)));   // (a team-up's too)

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
        var vcut = new List<string>();   // list entries pointing outside the source's tables (Symbiote Spider-Man's bantertargets[0] = 122)
        byte[] vd = vp.ReadExportBytes(vp.Exports[vc]).ToArray();
        if (TagWalker.Walk(vp, vd, 16) is { } vtags)
            foreach (var t in vtags.Where(t => t.Name is var n && (n.Equals("AudioEmotes", StringComparison.OrdinalIgnoreCase) || n.Equals("BanterTargets", StringComparison.OrdinalIgnoreCase))))
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(vd.AsSpan(t.ValueAt));
                if (t.Size != 4 + n * 4) continue;
                for (int k = 0; k < n; k++)
                {
                    int r = BinaryPrimitives.ReadInt32LittleEndian(vd.AsSpan(t.ValueAt + 4 + 4 * k));
                    if (r > vp.Exports.Length || -r > vp.Imports.Length) { vcut.Add($"{t.Name}[{k}]".ToLowerInvariant()); log.Add($"voice: {t.Name}[{k}] pointed at no object ({r}); set to none"); continue; }
                    if (r > 0 && r <= vp.Exports.Length && !vp.ClassOf(vp.Exports[r - 1]).Equals("AkEvent", StringComparison.OrdinalIgnoreCase))
                    {
                        // the voice's own character (Cable's bantertargets name his default object) becomes the target's,
                        // like the component itself (setting it to none cut the copy's outer, and verify failed)
                        if (vp.PathOf(vp.Exports[r - 1]).Equals(defName, StringComparison.OrdinalIgnoreCase)) { log.Add($"voice: {t.Name}[{k}] pointed at its own character; now {targetDefault}"); continue; }
                        vreplace[vp.PathOf(vp.Exports[r - 1])] = "none"; log.Add($"voice: {t.Name}[{k}] pointed at {vp.PathOf(vp.Exports[r - 1])}; set to none");
                    }
                }
            }
        // The copy's archetype: the costume's own soundscomponent's (Kurt, 2026-10-28, two users' crashes on load, "Assertion
        // failed: i>=0 && (i<ArrayNum…)", after Use Another Voice with Thing Fear Itself and Juggernaut Fear Itself): a voice
        // from an audio package (UC__MarvelPlayerAudio_<Hero>_<Voice>_SF) is instanced from its hero's class default,
        // default__marvelplayer_<hero>.soundscomponent, in that hero's own package, which the game doesn't load for this
        // costume; the copied component kept that archetype as an import the game couldn't resolve.
        int srcArch = EntryArchetype(vp, vc);
        int own = Slot(pkg);
        if (srcArch < 0 && own >= 0 && EntryArchetype(pkg, own) is int tgtArch && tgtArch != 0)
        {
            string from = ModMeshes.ImportPath(vp, srcArch), to = tgtArch < 0 ? ModMeshes.ImportPath(pkg, tgtArch) : pkg.PathOf(pkg.Exports[tgtArch - 1]);
            if (!from.Equals(to, StringComparison.OrdinalIgnoreCase)) { vreplace[from] = to; log.Add($"voice: its component made from {to} (the costume's own), not {from} (another hero's package)"); }
        }
        // The copy is a spare export beside the costume's own soundscomponent (its data then goes into that one), under a
        // name the package doesn't have yet (a costume can be given a voice more than once).
        string spare = "soundscomponent_voice";
        for (int k = 2; pkg.Exports.Any(e => pkg.PathOf(e).Equals(targetDefault + "." + spare, StringComparison.OrdinalIgnoreCase)); k++) spare = "soundscomponent_voice" + k;
        var vcopy = CrossMove.Quiet(() => ExportCopy.Copy(vp, vc, pkg, vcut, spare, vreplace), out string vsaid);
        if (vcopy == null)
        {
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_DEBUG") == "1") log.Add(vsaid);
            log.Add("voice not copied: " + string.Join(" ", vsaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("already") || l.TrimStart().StartsWith("- ")).Select(l => l.Trim()).Take(8)));
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
        // spare components an earlier copy left with another hero's archetype put right first (they stay in the package)
        var pkg = Package.FromBytes(RepairSpares(File.ReadAllBytes(costumePackage), log));
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

    /// <summary>
    /// The situations the target hero's own voice set has that a copied set doesn't (top-level properties by name), added
    /// to the copied data as none (a single line) or an empty list, so the hero's lines don't come through for them (an
    /// absent property inherits the hero's). Returns the new data; <paramref name="autoOff"/> gets one entry per hero line
    /// silenced (Missing), for the Voice tab. <paramref name="nameIdx"/> gives (or adds) a name's index in the package.
    /// </summary>
    public static byte[] AddMissing(Package pkg, byte[] data, string heroVoicePackage, string packageFile, Func<string, int> nameIdx,
        List<VoiceOffEntry> autoOff, List<string> log)
    {
        Package hp;
        try { hp = Package.Open(heroVoicePackage); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return data; }
        int hv = Find(hp);
        if (hv < 0) return data;
        byte[] hd = hp.ReadExportBytes(hp.Exports[hv]).ToArray();
        var heroTags = TagWalker.Walk(hp, hd, 16);
        var mine = TagWalker.Walk(pkg, data, 16);
        if (heroTags == null || mine == null) return data;
        var have = mine.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var heroLines = Read(Path.GetFileName(heroVoicePackage), heroVoicePackage, []);
        var add = new List<byte>();
        int at = mine.NoneAt, pseudo = -1;
        var missingNames = new List<string>();
        foreach (var t in heroTags)
        {
            if (have.Contains(t.Name)) continue;
            string type = t.Type.ToLowerInvariant();
            bool single = type == "objectproperty" && t.Size == 4;
            if (!single && type != "arrayproperty") continue;
            var tag = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), nameIdx(t.Name));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), nameIdx(single ? "ObjectProperty" : "ArrayProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);      // size; then index 0, then the value: none / count 0
            int valueAt = at + add.Count + 24;
            add.AddRange(tag);
            string sit = Situation(t.Name);
            foreach (var l in heroLines.Where(l => l.Situation == sit))
                autoOff.Add(new VoiceOffEntry { Package = packageFile, Offset = single ? valueAt : pseudo--, Event = l.Event, Missing = true, Situation = l.Situation, Detail = l.Detail });
            missingNames.Add(sit);
        }
        if (add.Count == 0) return data;
        log.Add($"voice: {missingNames.Count} situation(s) the moved voice has no line for, set to none so {Path.GetFileNameWithoutExtension(heroVoicePackage)}'s don't play: {string.Join(", ", missingNames)}");
        return [.. data.AsSpan(0, at), .. add, .. data.AsSpan(at)];
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
