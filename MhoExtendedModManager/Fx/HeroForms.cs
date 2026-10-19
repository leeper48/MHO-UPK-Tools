using MhoPackageModifier;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// A hero's forms (Kurt, 2026-10-19, a user's report: Jean Grey's Phoenix form didn't show in the preview, and her Phoenix
/// powers played the old standing versions). A form is a power (a talent: Jean's "Unleash the Phoenix", PhoenixFormSpec)
/// that applies a condition whose ConditionFxAnimation adds an animation-set alias (AnimSetAliasToAdd "phoenixas"); the hero's
/// class default names the set for it (AnimationSetAliases: phoenixas → jeangrey_anim.jeangrey_phoenixform_as, her flying
/// versions, which replace the standing ones of the same name). The same condition shows and hides weapon slots (her wings,
/// her cape) and plays effects on her sockets (flames). The talent's MapPower entries swap powers for the form's versions
/// (Psychokinetic Barrier: ForcePushJean → ForcePushPhoenixNew). Read only.
/// </summary>
static class HeroForms
{
    /// <summary>A talent that swaps powers (its MapPower entries: original → mapped power prototype paths).</summary>
    public sealed record Mapping(string Spec, Dictionary<string, string> Mapped);

    /// <summary>A form: the power that applies it, its name, the animation-set alias its condition adds, the condition packages
    /// (file names) and the powers it swaps.</summary>
    public sealed record Form(string Spec, string Name, string Alias, List<string> ConditionFiles, Dictionary<string, string> Mapped);

    /// <summary>
    /// A transform mode (Kurt, 2026-10-19: Rogue's Final Form, Rocket's mech, Loki's Frost Giant): the hero becomes another
    /// character class for a while, with abilities of its own. <paramref name="Spec"/> is the power that turns it on (the
    /// mode's UseRankOfPower, else its EnterTransformModePower); <paramref name="Model"/> the transformed class
    /// (MarvelPlayer_Rogue_UltimateForm), <paramref name="ModelFor"/> a costume's own (incoming class → transformed, lower case).
    /// </summary>
    public sealed record Transform(string Mode, string Spec, string? Enter, string Model, Dictionary<string, string> ModelFor, List<string> Abilities)
    {
        /// <summary>The transformed class for a costume class (marvelplayer_rogue_classic90s → MarvelPlayer_Rogue_UltimateForm_Classic90s).</summary>
        public string ModelOf(string? costumeClass) => costumeClass != null && ModelFor.TryGetValue(costumeClass, out var m) ? m : Model;
    }

    static readonly System.Text.RegularExpressions.Regex FieldLine = new(@"^\s*(\w+) \((\w)\) = (\S+)");

    /// <summary>The hero's transform modes (those its avatar names: Entity\Characters\Avatars\TransformModes\…).</summary>
    public static List<Transform> Transforms(GameData db, string hero)
    {
        var result = new List<Transform>();
        // the modes the avatar names, and those the hero's powers enter (Loki's avatar doesn't name his: his Ultimate's combo does)
        var ids = PowerList.AvatarPowers(db, hero);
        string folder = $"Powers\\Player\\{hero}\\";
        foreach (var (pid, pe) in db.Prototypes)
        {
            if (!pe.Path.Replace('/', '\\').StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                foreach (string l in db.Dump(db.Prototype(pid).Data, 8))
                    if (FieldLine.Match(l) is { Success: true } fm && fm.Groups[1].Value == "TransformMode" && db.Find(fm.Groups[3].Value) is { } te) ids.Add(te.Id);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException) { }
        }
        foreach (ulong id in ids)
        {
            if (!db.Prototypes.TryGetValue(id, out var e) || !e.Path.Replace('/', '\\').Contains("\\TransformModes\\", StringComparison.OrdinalIgnoreCase)) continue;
            List<string> lines;
            try { lines = db.Dump(db.Prototype(id).Data, 60).ToList(); }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException) { continue; }
            string? model = null, rank = null, enter = null, transformed = null;
            var modelFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var abilities = new List<string>();
            foreach (string l in lines)
            {
                var m = FieldLine.Match(l);
                if (!m.Success) continue;
                string f = m.Groups[1].Value, v = m.Groups[3].Value;
                switch (f)
                {
                    case "UnrealClass": model ??= v; break;
                    case "UseRankOfPower": rank ??= v; break;
                    case "EnterTransformModePower": enter ??= v; break;
                    case "TransformedUnrealClass": transformed = v; break;
                    case "IncomingUnrealClass": if (transformed != null) { modelFor.TryAdd(v, transformed); transformed = null; } break;
                    case "Ability": if (v.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase) && !abilities.Contains(v, StringComparer.OrdinalIgnoreCase)) abilities.Add(v); break;
                }
            }
            if (model == null || (rank ?? enter) is not string spec) continue;
            result.Add(new Transform(e.Path.Replace('/', '\\'), spec, enter, model, modelFor, abilities));
        }
        return result;
    }

    static readonly System.Text.RegularExpressions.Regex ParentLine = new(@"<parent (\S+\.prototype)>");

    /// <summary>Every prototype under Powers\Player\&lt;hero&gt;\ with MapPower entries (a talent's power swaps).</summary>
    public static List<Mapping> Mappings(GameData db, string hero)
    {
        string folder = $"Powers\\Player\\{hero}\\";
        var result = new List<Mapping>();
        foreach (var (id, e) in db.Prototypes)
        {
            string path = e.Path.Replace('/', '\\');
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
            List<string> lines;
            try { lines = db.Dump(db.Prototype(id).Data, 60).ToList(); }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException) { continue; }
            var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in lines.Select(l => ParentLine.Match(l)).Where(m => m.Success))
            {
                string parent = m.Groups[1].Value;
                if (parent.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                var (o, of) = PowerList.Field(db, parent, "OriginalPower", 'P');
                var (n, nf) = PowerList.Field(db, parent, "MappedPower", 'P');
                if (!of || !nf || !db.Prototypes.TryGetValue(o, out var oe) || !db.Prototypes.TryGetValue(n, out var ne)) continue;
                mapped.TryAdd(oe.Path.Replace('/', '\\'), ne.Path.Replace('/', '\\'));
            }
            if (mapped.Count > 0) result.Add(new Mapping(path, mapped));
        }
        return result;
    }

    /// <summary>Every MapPower prototype's mapped power under Powers\Player\&lt;hero&gt;\ (used or not).</summary>
    public static HashSet<string> AllMappedTargets(GameData db, string hero)
    {
        string folder = $"Powers\\Player\\{hero}\\";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, e) in db.Prototypes)
        {
            string path = e.Path.Replace('/', '\\');
            if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
            var (n, nf) = PowerList.Field(db, path, "MappedPower", 'P');
            if (nf && db.Prototypes.TryGetValue(n, out var ne)) set.Add(ne.Path.Replace('/', '\\'));
        }
        return set;
    }

    /// <summary>The animation-set alias a power's own conditions add (their packages' ConditionFxAnimation AnimSetAliasToAdd),
    /// with those packages; null when it adds none. A mod's own copy of a condition package is read first.</summary>
    public static (string Alias, List<string> Files)? AliasOf(GameData db, string power, string cooked, IEnumerable<string> modFiles)
    {
        var mods = modFiles.ToList();
        var files = new List<string>();
        string? alias = null;
        foreach (string c in Model.ConditionSwaps.ConditionsOf(db, power))
        {
            string file = $"UC__{c}_SF.upk";
            string path = mods.FirstOrDefault(m => Path.GetFileName(m).Equals(file, StringComparison.OrdinalIgnoreCase)) ?? StockFiles.For(cooked, file);
            if (!File.Exists(path)) continue;
            try
            {
                var pkg = Package.Open(path);
                for (int i = 0; i < pkg.Exports.Length; i++)
                {
                    if (!pkg.ClassOf(pkg.Exports[i]).Equals("ConditionFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
                    var d = pkg.ReadExportBytes(pkg.Exports[i]);
                    if (TagWalker.Walk(pkg, d, 16) is not { } tags || tags.FirstOrDefault(t => t.Name.Equals("AnimSetAliasToAdd", StringComparison.OrdinalIgnoreCase)) is not { } t) continue;
                    if (BitConverter.ToInt32(d, t.ValueAt) > 0) alias ??= TagWalker.NameAt(pkg, d, t.ValueAt + 4);
                }
                files.Add(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException) { }
        }
        return alias != null ? (alias, files) : null;
    }

    /// <summary>
    /// The hero's forms: each power the avatar names (or a talent mapping powers) whose condition adds an animation alias,
    /// with the powers its talent swaps (an alias talent with no swaps of its own takes those of a talent applying the same
    /// condition). Reads the condition packages (a few, once per hero).
    /// </summary>
    public static List<Form> For(GameData db, string hero, string cooked, IEnumerable<string> modFiles, IReadOnlyDictionary<string, string> names)
    {
        var mappings = Mappings(db, hero);
        var candidates = mappings.Select(m => m.Spec).ToList();
        var result = new List<Form>();
        foreach (string spec in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AliasOf(db, spec, cooked, modFiles) is not { } a) continue;
            var mapped = mappings.First(m => m.Spec.Equals(spec, StringComparison.OrdinalIgnoreCase)).Mapped;
            string name = names.TryGetValue(spec, out var n) ? n : PowerPreviewName(spec);
            result.Add(new Form(spec, name, a.Alias, a.Files, mapped));
        }
        return result;
    }

    static string PowerPreviewName(string spec) => Gui.PowerPreview.SplitWords(Path.GetFileNameWithoutExtension(spec));

    /// <summary>
    /// The AnimSet an alias names in a character class default's AnimationSetAliases (an array of { Alias, AnimSet }), looked
    /// for in <paramref name="packages"/> (the costume's own, then the hero's base package): the package and the AnimSet's
    /// export index (0-based), or null.
    /// </summary>
    public static (string Path, int Export)? AnimSetFor(string alias, IEnumerable<string> packages)
    {
        foreach (string path in packages)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var pkg = Package.Open(path);
                for (int i = 0; i < pkg.Exports.Length; i++)
                {
                    var e = pkg.Exports[i];
                    if (!e.ObjectName.StartsWith("default__marvelplayer_", StringComparison.OrdinalIgnoreCase)) continue;   // (a character class's default)
                    var d = pkg.ReadExportBytes(e);
                    if (TagWalker.Walk(pkg, d, 4) is not { } tags || tags.FirstOrDefault(t => t.Name.Equals("AnimationSetAliases", StringComparison.OrdinalIgnoreCase)) is not { } arr) continue;
                    int count = BitConverter.ToInt32(d, arr.ValueAt), at = arr.ValueAt + 4;
                    for (int k = 0; k < count && at < arr.End; k++)
                    {
                        if (TagWalker.Walk(pkg, d, at) is not { } st) break;
                        string? al = st.FirstOrDefault(x => x.Name.Equals("Alias", StringComparison.OrdinalIgnoreCase)) is { } an ? TagWalker.NameAt(pkg, d, an.ValueAt) : null;
                        int set = st.FirstOrDefault(x => x.Name.Equals("AnimSet", StringComparison.OrdinalIgnoreCase)) is { } sp ? BitConverter.ToInt32(d, sp.ValueAt) : 0;
                        if (al != null && al.Equals(alias, StringComparison.OrdinalIgnoreCase) && set > 0) return (path, set - 1);
                        at = st.NoneAt + 8;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException or IndexOutOfRangeException) { }
        }
        return null;
    }
}
