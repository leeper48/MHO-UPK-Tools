namespace MhoExtendedModManager.Fx;

/// <summary>
/// Which power an animation belongs to (power effects in the 3D preview). A power's class has PowerFxAnimation
/// components naming its animations (AnimName; the MHO Hero Creator's PowerAnims); the class lives in its package
/// UC__Power&lt;Hero&gt;_&lt;Power&gt;_SF (39 for Doctor Strange). So a hero's power packages are scanned once and indexed by
/// animation name. A mod's own copy of a power package (a recolored one) is read instead of the game's: the effects show
/// as the mod has them. Read only.
/// </summary>
static class PowerIndex
{
    /// <summary>A power that plays an animation: its class (e.g. powerthor_lightningstrike) and the package file holding it.</summary>
    public sealed record PowerRef(string Class, string File, string Component);

    static readonly Dictionary<string, Dictionary<string, List<PowerRef>>> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The hero's power packages (the mod's copies first, by file name; then the game's), indexed by animation name.
    /// <paramref name="hero"/> is the class's hero part (MarvelPlayer_DoctorStrange_Classic → DoctorStrange).
    /// </summary>
    /// <param name="extra">More packages to index (HeroClassFiles: those of the hero's power classes not named after him).</param>
    public static Dictionary<string, List<PowerRef>> For(string hero, string? cooked, IEnumerable<string> modFiles, IEnumerable<string>? extra = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string prefix = $"UC__Power{hero}_";
        // The hero's power packages: UC__Power<Hero>_… and also those naming the hero as a word elsewhere
        // (UC__PowerChanneledEnergyBeam_MsMarvel_SF: Captain Marvel's Photonic Devastation was missing).
        bool Mine(string name) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(name).Split('_').Contains(hero, StringComparer.OrdinalIgnoreCase);
        foreach (string f in modFiles) if (Mine(Path.GetFileName(f)) && File.Exists(f)) files[Path.GetFileName(f)] = f;
        if (cooked != null && Directory.Exists(cooked))
            foreach (string f in Directory.EnumerateFiles(cooked, "UC__Power*_SF.upk"))
                if (Mine(Path.GetFileName(f)) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase))
                    files.TryAdd(Path.GetFileName(f), StockFiles.For(cooked, Path.GetFileName(f)));
        foreach (string f in extra ?? []) files.TryAdd(Path.GetFileName(f), f);
        string key = hero + "|" + string.Join("|", files.Values.Select(f => f + File.GetLastWriteTimeUtc(f).Ticks));
        lock (cache) if (cache.TryGetValue(key, out var hit)) return hit;

        var index = new Dictionary<string, List<PowerRef>>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files.Values.Order(StringComparer.OrdinalIgnoreCase))
        {
            FxPkg p;
            try { p = FxPkg.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
            // (only what the power's default lists: a borrowed look leaves the power's old animation in the package, unused;
            // Angela's Heavenly Charge ← Blade's Stake kept its lunge listed first, so the power button played it: 0.60.5)
            var unlisted = PowerEffects.Unlisted(p);
            for (int i = 0; i < p.T.Exports.Count; i++)
            {
                if (!p.T.ClassOf(p.T.Exports[i]).StartsWith("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
                string path = p.T.PathOf(i + 1);   // marvelgamecontent.default__powerthor_x.animfoo
                if (unlisted.Contains(path)) continue;
                int d = path.IndexOf(".default__", StringComparison.OrdinalIgnoreCase);
                if (d < 0) continue;
                string rest = path[(d + ".default__".Length)..];
                string cls = rest.Split('.')[0];
                var props = FxProps.Find(p.Bytes, p.T, p.T.Exports[i])?.Props ?? [];
                // only for some characters (EntityRequired: a borrow keeps the stock animation for Kate Bishop only): left
                // out, like such effects (Kurt, 2026-10-22: Hawkeye's preview played his Sky-Cycle ride again)
                if (props.Any(x => x.Name.Equals("EntityRequired", StringComparison.OrdinalIgnoreCase))) continue;
                // no animations of its own: its template's, when the template is in this package (a borrowed ride's anim, whose
                // template powershared_ridebike.anim the borrow copied in: 0.60.7)
                if (!props.Any(x => x.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase))
                    && BitConverter.ToInt32(p.Bytes, p.T.Exports[i].EntryStart + 20) is int arch && arch > 0 && arch <= p.T.Exports.Count)
                    props = FxProps.Find(p.Bytes, p.T, p.T.Exports[arch - 1])?.Props ?? props;
                var names = props.Where(x => x.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToList();
                // The looping kind lists its animations (PowerAnims: an array of names; a channelled beam's start, loop, end).
                foreach (var arr in props.Where(x => x.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase)))
                {
                    int n = BitConverter.ToInt32(p.Bytes, arr.ValueAt);
                    for (int k = 0; k < n && arr.ValueAt + 4 + 8 * (k + 1) <= arr.ValueAt + arr.Size; k++)
                    {
                        int idx = BitConverter.ToInt32(p.Bytes, arr.ValueAt + 4 + 8 * k), num = BitConverter.ToInt32(p.Bytes, arr.ValueAt + 8 + 8 * k);
                        if (idx >= 0 && idx < p.T.Names.Count) names.Add(num > 0 ? $"{p.T.Names[idx].Text}_{num - 1}" : p.T.Names[idx].Text);
                    }
                }
                foreach (string anim in names)
                {
                    if (anim.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!index.TryGetValue(anim, out var list)) index[anim] = list = [];
                    if (!list.Any(x => x.Class.Equals(cls, StringComparison.OrdinalIgnoreCase))) list.Add(new PowerRef(cls, f, p.T.Exports[i].ObjectName));
                }
            }
        }
        lock (cache) cache[key] = index;
        return index;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dictionary<string, List<PowerRef>>, Dictionary<string, List<PowerRef>>> withTravel = new();
    static readonly Dictionary<(GameData, string), List<string>> heroClassNames = [];

    /// <summary>
    /// The packages of the hero's power classes that aren't named after the hero (Kurt, 2026-10-23: Thor's Hammer of Storms is
    /// class PowerFlyMjolnir in UC__PowerFlyMjolnir_SF, so its throw was missing and it couldn't be borrowed): every prototype
    /// under Powers\Player\&lt;Hero&gt;\ names its PowerUnrealClass; its package UC__&lt;Class&gt;_SF, the mod's copy first.
    /// </summary>
    public static List<string> HeroClassFiles(GameData db, string hero, string? cooked, IReadOnlyCollection<string> modFiles)
    {
        if (cooked == null) return [];
        List<string>? names;
        lock (heroClassNames) heroClassNames.TryGetValue((db, hero), out names);
        if (names == null)
        {
            names = [];
            string folder = $@"Powers\Player\{hero}\";
            var byClass = PrototypesByClass(db);
            foreach (var e in db.Prototypes.Values)
            {
                if (!e.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                string? cls;
                try { cls = PowerEffects.UnrealClassOf(db, e.Path); } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException) { continue; }
                if (cls == null) continue;
                // only a class of this hero's alone (Colossus's Fastball Special sets off Wolverine's Slashing Leap; Deadpool's
                // allies use Cable's Pulse Bolt): another hero's power isn't one of his buttons
                if (byClass.TryGetValue(cls, out var users) && users.Any(u => !u.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) continue;
                string file = $"UC__{cls}_SF.upk";
                if (!names.Contains(file, StringComparer.OrdinalIgnoreCase)) names.Add(file);
            }
            lock (heroClassNames) heroClassNames[(db, hero)] = names;
        }
        var result = new List<string>();
        foreach (string n in names)
        {
            string? mine = modFiles.FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase) && File.Exists(f));
            string path = mine ?? StockFiles.For(cooked, n);
            if (File.Exists(path)) result.Add(path);
        }
        return result;
    }

    /// <summary>
    /// <see cref="For(string, string?, IEnumerable{string})"/> plus the hero's travel power (the avatar's TravelPower): its
    /// ride animations sit on a shared class (PowerWolverine_RideBike extends powershared_ridebike) in packages named after
    /// neither, so they're read from the class chain's packages and filed under the hero's own class. Every package of the
    /// chain is a ref, so its prop rules (the bike) and effects count.
    /// </summary>
    public static Dictionary<string, List<PowerRef>> For(string hero, string? cooked, IEnumerable<string> modFiles, GameData db)
    {
        var mods = modFiles.ToList();
        var idx = For(hero, cooked, mods, HeroClassFiles(db, hero, cooked, mods));
        lock (withTravel) if (withTravel.TryGetValue(idx, out var hit)) return hit;
        var merged = idx;
        if (cooked != null && PowerList.TravelPowerOf(db, hero) is string travel && PowerList.TravelClassOf(db, travel) is string cls)
        {
            var fg = new FxGame(cooked, mods);
            var chain = fg.ClassChain(cls);
            List<string> Files(IEnumerable<string> classes) => classes.SelectMany(c => fg.PackagesOf(c)).Distinct(StringComparer.OrdinalIgnoreCase).Select(n => fg.FileFor(n)).OfType<string>().ToList();
            var anims = Files(chain).SelectMany(f => AnimationsIn(f, chain)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Refs: the class chain's packages and those of every class the power sets off with theirs (the ride's condition
            // and its shared parent, which shows the bike).
            var files = Files(chain.Concat(PowerClosure.Of(db, travel).Select(x => x.Class).Where(c => !string.IsNullOrEmpty(c)).SelectMany(c => fg.ClassChain(c))));
            if (anims.Count > 0)
            {
                merged = idx.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (string a in anims)
                {
                    if (!merged.TryGetValue(a, out var list)) merged[a] = list = [];
                    foreach (string f in files)
                        if (!list.Any(x => x.Class.Equals(cls, StringComparison.OrdinalIgnoreCase) && x.File.Equals(f, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new PowerRef(cls, f, "travel"));
                }
            }
        }
        lock (withTravel) withTravel.AddOrUpdate(idx, merged);
        return merged;
    }

    /// <summary>The animation names a power package's PowerFxAnimation components play (AnimName, and the looping kind's
    /// PowerAnims list), e.g. a travel power's (the Sky-Cycle's, a bike's).</summary>
    public static List<string> AnimationsIn(string file, List<string>? chain = null)
    {
        var names = new List<string>();
        FxPkg p;
        try { p = FxPkg.Open(file); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { return names; }
        for (int i = 0; i < p.T.Exports.Count; i++)
        {
            if (!p.T.ClassOf(p.T.Exports[i]).StartsWith("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
            if (chain != null && !FxGame.OfChain(p.T.PathOf(i + 1), chain)) continue;
            var props = FxProps.Find(p.Bytes, p.T, p.T.Exports[i])?.Props ?? [];
            if (props.Any(x => x.Name.Equals("EntityRequired", StringComparison.OrdinalIgnoreCase))) continue;   // (only for some characters)
            names.AddRange(props.Where(x => x.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value));
            foreach (var arr in props.Where(x => x.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase)))
            {
                int n = BitConverter.ToInt32(p.Bytes, arr.ValueAt);
                for (int k = 0; k < n && arr.ValueAt + 4 + 8 * (k + 1) <= arr.ValueAt + arr.Size; k++)
                {
                    int idx = BitConverter.ToInt32(p.Bytes, arr.ValueAt + 4 + 8 * k), num = BitConverter.ToInt32(p.Bytes, arr.ValueAt + 8 + 8 * k);
                    if (idx >= 0 && idx < p.T.Names.Count) names.Add(num > 0 ? $"{p.T.Names[idx].Text}_{num - 1}" : p.T.Names[idx].Text);
                }
            }
        }
        return [.. names.Where(n => !n.Equals("None", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    static readonly Dictionary<GameData, Dictionary<string, List<string>>> protoCache = [];

    /// <summary>
    /// Power prototypes by their Unreal class (powerthor_shockwave → Powers\Player\Thor\Rework\Shockwave.prototype),
    /// over every player power (Powers\Player\…), read once per game data. An animation's power is found through its
    /// class (the packages' PowerFxAnimation components), then its prototype: the effects need the prototype (what it
    /// sets off, its contact time).
    /// </summary>
    public static Dictionary<string, List<string>> PrototypesByClass(GameData db)
    {
        lock (protoCache) if (protoCache.TryGetValue(db, out var hit)) return hit;
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in db.Prototypes.Values)
        {
            if (!e.Path.StartsWith(@"Powers\Player\", StringComparison.OrdinalIgnoreCase)) continue;
            string? cls;
            try { cls = PowerEffects.UnrealClassOf(db, e.Path); } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException) { continue; }
            if (cls == null) continue;
            if (!map.TryGetValue(cls, out var list)) map[cls] = list = [];
            list.Add(e.Path);
        }
        lock (protoCache) protoCache[db] = map;
        return map;
    }

    /// <summary>The hero part of a costume / hero class name (MarvelPlayer_DoctorStrange_Classic → DoctorStrange), or null.</summary>
    public static string? HeroOf(string costumeClass)
    {
        var parts = costumeClass.Split('_');
        return parts.Length >= 2 && parts[0].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }
}
