namespace MhoExtendedModManager.Fx;

// Ported from the MHO Hero Creator's GameData.cs (2026-09-30; power effects in the 3D preview); kept in step with it.
/// <summary>A loaded Calligraphy.sip: directories, names, blueprints (cached) and prototypes (parsed on demand).</summary>
sealed class GameData
{
    public readonly SipArchive Sip;
    public readonly Calligraphy.Directory PrototypeDirectory, BlueprintDirectory;
    public readonly Dictionary<ulong, Calligraphy.DirEntry> Prototypes = new();
    public readonly Dictionary<ulong, Calligraphy.DirEntry> Blueprints = new();
    readonly Dictionary<string, ulong> protoByPath = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<ulong, Calligraphy.Blueprint> bpCache = new();
    readonly Dictionary<ulong, string> otherNames = new();

    public GameData(SipArchive sip)
    {
        Sip = sip;
        PrototypeDirectory = Calligraphy.Directory.Parse(sip.Read("Calligraphy/Prototype.directory"), true);
        BlueprintDirectory = Calligraphy.Directory.Parse(sip.Read("Calligraphy/Blueprint.directory"), false);
        foreach (var e in PrototypeDirectory.Entries) { Prototypes[e.Id] = e; protoByPath[Norm(e.Path)] = e.Id; }
        foreach (var e in BlueprintDirectory.Entries) Blueprints[e.Id] = e;
        // Asset types, curves: names only (for dumps).
        TypeDirectory = Calligraphy.Directory.Parse(sip.Read("Calligraphy/Type.directory"), false);
        foreach (var e in TypeDirectory.Entries) otherNames.TryAdd(e.Id, e.Path);
        foreach (var e in Calligraphy.Directory.Parse(sip.Read("Calligraphy/Curve.directory"), false).Entries) otherNames.TryAdd(e.Id, e.Path);
    }

    public readonly Calligraphy.Directory TypeDirectory;
    Dictionary<ulong, (string Type, Calligraphy.AssetValue Asset)>? assets;

    /// <summary>Every asset by id: its type file and entry (loaded on first use).</summary>
    public IReadOnlyDictionary<ulong, (string Type, Calligraphy.AssetValue Asset)> Assets
    {
        get
        {
            if (assets != null) return assets;
            var d = new Dictionary<ulong, (string, Calligraphy.AssetValue)>();
            foreach (var t in TypeDirectory.Entries)
                foreach (var a in Calligraphy.AssetTypeFile.Parse(Sip.Read("Calligraphy/" + t.Path.Replace('\\', '/'))).Assets)
                    d.TryAdd(a.Id, (t.Path, a));
            return assets = d;
        }
    }

    public string AssetName(ulong id) => Assets.TryGetValue(id, out var a) ? $"{a.Asset.Name}  [{Path.GetFileNameWithoutExtension(a.Type)}]" : $"asset #{id}";

    static string Norm(string p) => p.Replace('/', '\\');

    public Calligraphy.DirEntry? Find(string pathOrId)
    {
        if (ulong.TryParse(pathOrId, out ulong id) && Prototypes.TryGetValue(id, out var e)) return e;
        return protoByPath.TryGetValue(Norm(pathOrId), out id) ? Prototypes[id] : null;
    }

    public ulong BlueprintByPath(string path) =>
        BlueprintDirectory.Entries.First(e => Norm(e.Path).Equals(Norm(path), StringComparison.OrdinalIgnoreCase)).Id;

    public string Name(ulong id)
    {
        if (id == 0) return "none";
        if (Prototypes.TryGetValue(id, out var p)) return p.Path;
        if (Blueprints.TryGetValue(id, out var b)) return b.Path;
        return otherNames.TryGetValue(id, out var n) ? n : $"#{id:X16}";
    }

    public Calligraphy.PrototypeFile Prototype(ulong id) =>
        Calligraphy.PrototypeFile.Parse(Sip.Read("Calligraphy/" + Prototypes[id].Path.Replace('\\', '/')));

    public Calligraphy.Blueprint Blueprint(ulong id)
    {
        lock (bpCache)
        {
            if (!bpCache.TryGetValue(id, out var bp))
                bpCache[id] = bp = Calligraphy.Blueprint.Parse(Sip.Read("Calligraphy/" + Blueprints[id].Path.Replace('\\', '/')));
            return bp;
        }
    }

    /// <summary>True when <paramref name="bp"/> is <paramref name="ancestor"/> or inherits from it.</summary>
    public bool IsBlueprintOrChild(ulong bp, ulong ancestor)
    {
        var seen = new HashSet<ulong>();
        var stack = new Stack<ulong>([bp]);
        while (stack.Count > 0)
        {
            ulong b = stack.Pop();
            if (b == ancestor) return true;
            if (!seen.Add(b) || !Blueprints.ContainsKey(b)) continue;
            foreach (var p in Blueprint(b).Parents) stack.Push(p);
        }
        return false;
    }

    public string FieldName(ulong blueprint, ulong field) =>
        Blueprints.ContainsKey(blueprint) && Blueprint(blueprint).Fields.TryGetValue(field, out var f) ? f.Name : $"#{field:X16}";

    /// <summary>Where <paramref name="d"/> names <paramref name="id"/>: as parent, or as the value of a P field (nested too).</summary>
    public IEnumerable<string> References(Calligraphy.Data d, ulong id, string path = "", char type = 'P')
    {
        if (type == 'P' && d.HasParent && d.Parent == id) yield return (path == "" ? "" : path + ".") + "<parent>";
        foreach (var g in d.Groups)
        {
            foreach (var f in g.Simple)
            {
                string n = (path == "" ? "" : path + ".") + FieldName(g.Blueprint, f.Id);
                if (f.Type == 'R') { foreach (var x in References(f.Value.Struct!, id, n, type)) yield return x; }
                else if (f.Type == type && f.Value.Raw == id) yield return n;
            }
            foreach (var f in g.Lists)
            {
                string n = (path == "" ? "" : path + ".") + FieldName(g.Blueprint, f.Id);
                for (int i = 0; i < f.Values.Count; i++)
                {
                    if (f.Type == 'R') { foreach (var x in References(f.Values[i].Struct!, id, $"{n}[{i}]", type)) yield return x; }
                    else if (f.Type == type && f.Values[i].Raw == id) yield return $"{n}[{i}]";
                }
            }
        }
    }

    public IEnumerable<string> Dump(Calligraphy.Data d, int maxList = 50, int depth = 0)
    {
        string pad = new(' ', depth * 2);
        if (d.HasParent) yield return $"{pad}<parent {Name(d.Parent)}>";
        foreach (var g in d.Groups)
        {
            yield return $"{pad}[{Name(g.Blueprint)}{(g.Copy != 0 ? " #" + g.Copy : "")}]";
            foreach (var f in g.Simple)
            {
                string n = FieldName(g.Blueprint, f.Id);
                if (f.Type == 'R')
                {
                    yield return $"{pad}  {n} (R):";
                    foreach (var l in Dump(f.Value.Struct!, maxList, depth + 2)) yield return l;
                }
                else yield return $"{pad}  {n} ({f.Type}) = {Show(f.Type, f.Value.Raw)}";
            }
            foreach (var f in g.Lists)
            {
                string n = FieldName(g.Blueprint, f.Id);
                yield return $"{pad}  {n} ({f.Type}) list[{f.Values.Count}]:";
                foreach (var v in f.Values.Take(maxList))
                {
                    if (f.Type == 'R') foreach (var l in Dump(v.Struct!, maxList, depth + 3)) yield return l;
                    else yield return $"{pad}      {Show(f.Type, v.Raw)}";
                }
                if (f.Values.Count > maxList) yield return $"{pad}      … {f.Values.Count - maxList} more";
            }
        }
    }

    string Show(char t, ulong v) => t switch
    {
        'P' or 'T' or 'C' => Name(v),
        'D' => BitConverter.Int64BitsToDouble((long)v).ToString("R"),
        'L' => ((long)v).ToString(),
        'B' => v != 0 ? "true" : "false",
        'S' => $"string {v}",
        'A' => AssetName(v),
        _ => v.ToString(),
    };
}
