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
    public static Dictionary<string, List<PowerRef>> For(string hero, string? cooked, IEnumerable<string> modFiles)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string prefix = $"UC__Power{hero}_";
        foreach (string f in modFiles) if (Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(f)) files[Path.GetFileName(f)] = f;
        if (cooked != null && Directory.Exists(cooked))
            foreach (string f in Directory.EnumerateFiles(cooked, prefix + "*_SF.upk"))
                files.TryAdd(Path.GetFileName(f), f);
        string key = hero + "|" + string.Join("|", files.Values.Select(f => f + File.GetLastWriteTimeUtc(f).Ticks));
        lock (cache) if (cache.TryGetValue(key, out var hit)) return hit;

        var index = new Dictionary<string, List<PowerRef>>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files.Values.Order(StringComparer.OrdinalIgnoreCase))
        {
            FxPkg p;
            try { p = FxPkg.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
            for (int i = 0; i < p.T.Exports.Count; i++)
            {
                if (!p.T.ClassOf(p.T.Exports[i]).StartsWith("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
                string path = p.T.PathOf(i + 1);   // marvelgamecontent.default__powerthor_x.animfoo
                int d = path.IndexOf(".default__", StringComparison.OrdinalIgnoreCase);
                if (d < 0) continue;
                string rest = path[(d + ".default__".Length)..];
                string cls = rest.Split('.')[0];
                var props = FxProps.Find(p.Bytes, p.T, p.T.Exports[i])?.Props ?? [];
                foreach (var pr in props.Where(x => x.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase)))
                {
                    if (pr.Value.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!index.TryGetValue(pr.Value, out var list)) index[pr.Value] = list = [];
                    if (!list.Any(x => x.Class.Equals(cls, StringComparison.OrdinalIgnoreCase))) list.Add(new PowerRef(cls, f, p.T.Exports[i].ObjectName));
                }
            }
        }
        lock (cache) cache[key] = index;
        return index;
    }

    /// <summary>The hero part of a costume / hero class name (MarvelPlayer_DoctorStrange_Classic → DoctorStrange), or null.</summary>
    public static string? HeroOf(string costumeClass)
    {
        var parts = costumeClass.Split('_');
        return parts.Length >= 2 && parts[0].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }
}
