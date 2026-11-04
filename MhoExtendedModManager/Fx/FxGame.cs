using System.Text;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// The game files the power-effect code reads (ported from the MHO Hero Creator, which keeps its own copy; no reference
/// to it): packages by name, a mod's own copy first (a recolored power shows as the mod has it), then the game's; the
/// packages the game always has loaded (MarvelGame.upk: vfx_shared_textures / materials; Startup.upk: basematerials),
/// opened only when an import needs them; and the class → packages table.
/// </summary>
sealed class FxGame
{
    public readonly string Cooked;
    readonly Dictionary<string, string> modFiles = new(StringComparer.OrdinalIgnoreCase);   // "uc__powerthor_x_sf" → path
    AssetPackageCache? apc;
    List<FxPkg>? always;

    public FxGame(string cooked, IEnumerable<string> modPackageFiles)
    {
        Cooked = cooked;
        foreach (string f in modPackageFiles) if (File.Exists(f)) modFiles[Path.GetFileNameWithoutExtension(f)] = f;
    }

    /// <summary>A package file by name (without .upk): the mod's copy, else the game's; null if neither exists.</summary>
    public string? FileFor(string packageName)
    {
        string n = packageName.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? packageName[..^4] : packageName;
        if (modFiles.TryGetValue(n, out var m)) return m;
        string f = Path.Combine(Cooked, n + ".upk");
        return File.Exists(f) ? StockFiles.For(Cooked, n + ".upk") : null;   // the game's stock copy, not one a mod changed
    }

    public FxPkg? Open(string packageName) => FileFor(packageName) is { } f ? FxPkg.Open(f) : null;

    /// <summary>The mod's packages (paths).</summary>
    public IEnumerable<string> ModFiles => modFiles.Values;

    public AssetPackageCache Apc => apc ??= File.Exists(Path.Combine(Cooked, "AssetPackageCache.bin"))
        ? AssetPackageCache.Parse(File.ReadAllBytes(Path.Combine(Cooked, "AssetPackageCache.bin"))) : new AssetPackageCache();

    /// <summary>The packages a class lives in (the cache's list, else its own UC__&lt;class&gt;_SF).</summary>
    public List<string> PackagesOf(string cls)
    {
        var list = Apc.Packages.FirstOrDefault(x => x.Path.Equals("marvelgamecontent." + cls, StringComparison.OrdinalIgnoreCase)).Packages?.ToList() ?? [];
        if (list.Count == 0) list.Add("uc__" + cls + "_sf");
        return list;
    }

    public List<FxPkg> AlwaysLoaded()
    {
        if (always != null) return always;
        var list = new List<FxPkg>();
        foreach (var name in new[] { "MarvelGame", "Startup" })
            try { if (Open(name) is { } p) list.Add(p); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
        return always = list;
    }

    readonly Dictionary<string, List<string>> chains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A class and the game classes it extends (PowerGhostRider_RideBike → PowerShared_RideBike): its components count as
    /// the class's. Found through the class export's super reference (entry +4) in the class's packages.
    /// </summary>
    public List<string> ClassChain(string cls)
    {
        if (chains.TryGetValue(cls, out var hit)) return hit;
        var list = new List<string> { cls };
        string cur = cls;
        for (int guard = 0; guard < 6; guard++)
        {
            string? super = null;
            foreach (var pkgName in PackagesOf(cur).Append("uc__" + cur + "_sf"))
            {
                FxPkg? p;
                try { p = Open(pkgName); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
                if (p == null) continue;
                int ci = p.Find("marvelgamecontent." + cur);
                if (ci < 0) continue;
                int sr = BitConverter.ToInt32(p.Bytes, p.T.Exports[ci].EntryStart + 4);
                if (sr == 0) break;
                string sp = p.T.PathOf(sr);
                if (sp.StartsWith("marvelgamecontent.", StringComparison.OrdinalIgnoreCase)) super = sp["marvelgamecontent.".Length..];
                break;
            }
            if (super == null || list.Contains(super, StringComparer.OrdinalIgnoreCase)) break;
            list.Add(super); cur = super;
        }
        return chains[cls] = list;
    }

    readonly Dictionary<string, string> defaultPoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When a power component with no ActivationPoint of its own starts: its class's default in MarvelGame.upk, else its
    /// parent class's (Default__PowerFXParticle: POWER_AT_CONTACT_TIME; Default__PowerFXAnimation: POWER_ON_START), else
    /// power_on_start. UE3 leaves a value equal to the default out (2026-11-03, --fx-placement against the game: Thor's
    /// Hammer Smash impact, no ActivationPoint, started at +0.41 s in game, at once in the 3D view). Lower case, no enum prefix.
    /// </summary>
    public string DefaultActivationPoint(string componentClass)
    {
        if (defaultPoints.TryGetValue(componentClass, out var hit)) return hit;
        string result = "power_on_start";
        FxPkg? mg = null;
        try { mg = Open("MarvelGame"); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
        if (mg != null)
        {
            string cur = componentClass;
            for (int guard = 0; guard < 8; guard++)
            {
                // (MarvelGame.upk's own objects have no outer: Default__PowerFXParticle, PowerFXParticle)
                int d = mg.Find("default__" + cur);
                if (d >= 0 && FxProps.Find(mg.Bytes, mg.T, mg.T.Exports[d])?.Props.FirstOrDefault(x => x.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase)) is { Value: { } v })
                { result = v[(v.LastIndexOf('.') + 1)..].ToLowerInvariant(); break; }
                int ci = mg.Find(cur);
                if (ci < 0) break;
                int sr = BitConverter.ToInt32(mg.Bytes, mg.T.Exports[ci].EntryStart + 4);
                if (sr <= 0) break;   // (a parent in another package: Engine's)
                string sp = mg.T.PathOf(sr);
                cur = sp.Contains('.') ? sp[(sp.LastIndexOf('.') + 1)..] : sp;
            }
        }
        return defaultPoints[componentClass] = result;
    }

    /// <summary>Whether a component's path belongs to one of the chain's class defaults.</summary>
    public static bool OfChain(string path, List<string> chain) => chain.Any(c => path.Contains("default__" + c + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>An array of tagged structs, each as its NameProperty values (SpawnSockets: SocketName …).</summary>
    public static List<Dictionary<string, string>> StructArray(byte[] b, FxTables t, int at, int size)
    {
        var list = new List<Dictionary<string, string>>();
        int end = at + size, count = BitConverter.ToInt32(b, at), p = at + 4;
        string N(int q) => t.Names[BitConverter.ToInt32(b, q)].Text;
        for (int k = 0; k < count && p < end; k++)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (p + 8 <= end)
            {
                string name = N(p);
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { p += 8; break; }
                string type = N(p + 8); int sz = BitConverter.ToInt32(b, p + 16); p += 24;
                if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase)) { p += 1; continue; }
                if (type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) || type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase)) p += 8;
                if (type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase)) d[name] = N(p);
                p += sz;
            }
            list.Add(d);
        }
        return list;
    }
}

/// <summary>
/// UnrealEngine3\MarvelGame\CookedPCConsole\AssetPackageCache.bin: which package files hold each Unreal class the game
/// data names (the MHO Hero Creator's reading). u32 count; entries: u64 id, FString path — section 1; u32 count;
/// entries: FString path, u32 n, n × FString package file name (lower case) — section 2; two u32 counts (0); u32 count;
/// entries: FString level, u32 n, n × FString package — section 3. FString = i32 length including the null, Latin-1.
/// </summary>
sealed class AssetPackageCache
{
    public readonly List<(ulong Id, string Path)> Ids = new();
    public readonly List<(string Path, List<string> Packages)> Packages = new();
    public readonly List<(string Level, List<string> Packages)> Levels = new();

    public static AssetPackageCache Parse(byte[] b)
    {
        var c = new AssetPackageCache();
        int pos = 0;
        int I32() { int v = BitConverter.ToInt32(b, pos); pos += 4; return v; }
        ulong U64() { ulong v = BitConverter.ToUInt64(b, pos); pos += 8; return v; }
        string S() { int n = I32(); if (n <= 0) return ""; var s = Encoding.Latin1.GetString(b, pos, n - 1); pos += n; return s; }
        for (int n = I32(), i = 0; i < n; i++) { ulong id = U64(); c.Ids.Add((id, S())); }
        for (int n = I32(), i = 0; i < n; i++)
        {
            string p = S(); var list = new List<string>();
            for (int k = I32(), j = 0; j < k; j++) list.Add(S());
            c.Packages.Add((p, list));
        }
        if (I32() != 0 || I32() != 0) throw new InvalidDataException("AssetPackageCache: unknown non-empty lists");
        for (int n = I32(), i = 0; i < n; i++)
        {
            string p = S(); var list = new List<string>();
            for (int k = I32(), j = 0; j < k; j++) list.Add(S());
            c.Levels.Add((p, list));
        }
        if (pos != b.Length) throw new InvalidDataException($"{b.Length - pos} bytes left after AssetPackageCache");
        return c;
    }
}
