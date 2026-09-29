namespace MhoExtendedModManager;

/// <summary>
/// The game's costume definitions (Data\Game\Calligraphy.sip, Entity/Items/Costumes/Prototypes/&lt;Hero&gt;/&lt;Costume&gt;.prototype),
/// read only: the Unreal class that renders the costume (CostumeUnrealClass: "MarvelPlayer_Thor_Classic", so the package is
/// UC__MarvelPlayer_Thor_Classic_SF), its icons (IconPath "MarvelUIIcons.CostumeThor_Classic", PortraitIconPath
/// "MarvelUIIcons.HeroHor_Thor_Classic", StoreIconPath "MarvelUIIcons_Store.Store_Thor_Classic"), its name (DisplayName, a
/// string ID) and the hero (UsableBy). Checked 2026-09-29 on Thor Classic / AgeOfUltronMovie with MPM's cally.py.
/// Asset fields hold asset IDs, named in the type files (Entity/Types/UnrealClass.type, EntityIconPathType.type:
/// 4-byte header, u16 count, then id u64, guid u64, flags u8, name).
/// </summary>
sealed record Costume(string Prototype, string Class, string? Icon, string? Portrait, string? Store, string? PartyPortrait, ulong DisplayName, string? Hero)
{
    /// <summary>"Thor/Classic" from the prototype path.</summary>
    public string Short => Prototype.Replace('\\', '/').Split('/') is var p && p.Length >= 2 ? p[^2] + "/" + p[^1] : Prototype;
    /// <summary>The hero's default costume (its avatar's StartingCostume).</summary>
    public bool IsDefault { get; init; }
    /// <summary>The costume's own text fields (DisplayName, and any others such as a description): field → string ID.</summary>
    public IReadOnlyDictionary<string, ulong> Texts { get; init; } = new Dictionary<string, ulong>();

    /// <summary>"Age of Ultron Movie" from Thor/AgeOfUltronMovie.prototype (the words of the prototype's name).</summary>
    public string Title
    {
        get
        {
            string n = Path.GetFileNameWithoutExtension(Prototype.Replace('\\', '/').Split('/')[^1]);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n.Length; i++)
            {
                char c = n[i];
                bool wordStart = i > 0 && (char.IsUpper(c) && (char.IsLower(n[i - 1]) || (i + 1 < n.Length && char.IsLower(n[i + 1]) && char.IsUpper(n[i - 1])))
                                           || char.IsDigit(c) && !char.IsDigit(n[i - 1]));
                if (wordStart) sb.Append(' ');
                sb.Append(c);
            }
            string[] small = ["Of", "And", "The", "In", "On", "A"];
            return string.Join(' ', sb.ToString().Split(' ').Select((w, k) => k > 0 && small.Contains(w) ? w.ToLowerInvariant() : w));
        }
    }

    /// <summary>The package that holds the class: UC__&lt;Class&gt;_SF.upk.</summary>
    public string Package => $"UC__{Class}_SF.upk";

    /// <summary>An icon path "MarvelUIIcons.CostumeThor_Classic" as (icon package file, texture name in lower case).</summary>
    public static (string Package, string Texture)? IconTexture(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int dot = path.IndexOf('.');
        if (dot <= 0) return null;
        return ($"ICO__{path[..dot]}_SF.upk", path[(dot + 1)..].ToLowerInvariant());
    }

    static List<Costume>? cached;
    static string? cachedKey;
    static readonly object gate = new();

    /// <summary>Every costume the game defines, or null if the data can't be read. Built once per session.</summary>
    public static List<Costume>? All(string gameRoot)
    {
        string sip = Path.Combine(gameRoot, "Data", "Game", "Calligraphy.sip");
        if (!File.Exists(sip)) return null;
        string key = sip + "|" + new FileInfo(sip).Length + "|" + File.GetLastWriteTimeUtc(sip).Ticks;
        lock (gate)
        {
            if (cached != null && cachedKey == key) return cached;
            try { cached = Build(sip); cachedKey = key; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { cached = null; }
            return cached;
        }
    }

    static List<Costume> Build(string sipPath)
    {
        using var sip = new StringUsage.Sip(sipPath);
        byte[] Dir(string name) => sip.Read("Calligraphy/" + name + ".directory");
        var blueprints = new Dictionary<ulong, string>();
        var r = new StringUsage.Reader(Dir("Blueprint"), 4);
        for (int n = r.I32(), i = 0; i < n; i++) { ulong id = r.U64(); r.U64(); r.U8(); blueprints[id] = r.S16(); }
        var protoNames = new Dictionary<ulong, string>();
        var costumes = new List<string>();
        var avatars = new List<string>();
        r = new StringUsage.Reader(Dir("Prototype"), 4);
        for (int n = r.I32(), i = 0; i < n; i++)
        {
            ulong id = r.U64(); r.U64(); r.U64(); r.U8(); string path = r.S16();
            protoNames[id] = path;
            if (path.Replace('\\', '/').StartsWith("Entity/Items/Costumes/Prototypes/", StringComparison.OrdinalIgnoreCase)) costumes.Add(path);
            else if (path.Replace('\\', '/').StartsWith("Entity/Characters/Avatars/", StringComparison.OrdinalIgnoreCase)) avatars.Add(path);
        }
        var assets = new Dictionary<ulong, string>();
        foreach (string type in new[] { "Entity/Types/UnrealClass.type", "Entity/Types/EntityIconPathType.type" })
        {
            var tr = new StringUsage.Reader(sip.Read("Calligraphy/" + type), 4);
            for (int n = tr.U16(), i = 0; i < n; i++) { ulong id = tr.U64(); tr.U64(); tr.U8(); assets[id] = tr.S16(); }
        }

        var fieldNames = new Dictionary<ulong, Dictionary<ulong, string>>();
        Dictionary<ulong, string> Fields(ulong bid)
        {
            if (fieldNames.TryGetValue(bid, out var f)) return f;
            f = [];
            // Names only: a blueprint that can't be read just leaves its fields unnamed (as StringUsage does).
            try
            {
                if (blueprints.TryGetValue(bid, out string? file))
                {
                    var br = new StringUsage.Reader(sip.Read("Calligraphy/" + file), 4);
                    br.S16(); br.U64();
                    for (int n = br.U16(), i = 0; i < n; i++) { br.U64(); br.U8(); }
                    for (int n = br.U16(), i = 0; i < n; i++) { br.U64(); br.U8(); }
                    for (int n = br.U16(), i = 0; i < n; i++)
                    {
                        ulong fid = br.U64(); string name = br.S16(); char baseType = (char)br.U8(); br.U8();
                        if ("ACPR".Contains(baseType)) br.U64();   // not T (--blueprint-check)
                        f[fid] = name;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { }
            fieldNames[bid] = f;
            return f;
        }

        // Each hero's default costume: the costume prototype inside its avatar's StartingCostume struct (Thor:
        // Thor/Modern.prototype, checked 2026-09-29).
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string av in avatars)
        {
            void WalkAvatar(StringUsage.Reader pr, bool inStart)
            {
                byte flags = pr.U8();
                if ((flags & 1) != 0) pr.U64();
                if ((flags & 2) == 0) return;
                for (int g = pr.U16(), gi = 0; gi < g; gi++)
                {
                    var names = Fields(pr.U64()); pr.U8();
                    for (int n = pr.U16(), i = 0; i < n; i++)
                    {
                        ulong fid = pr.U64(); char t = (char)pr.U8();
                        bool start = inStart || (names.TryGetValue(fid, out var nm) && nm.Equals("StartingCostume", StringComparison.OrdinalIgnoreCase));
                        if (t == 'R') { WalkAvatar(pr, start); continue; }
                        ulong v = pr.U64();
                        if (inStart && t == 'P' && protoNames.TryGetValue(v, out var cp) && cp.Replace('\\', '/').StartsWith("Entity/Items/Costumes/Prototypes/", StringComparison.OrdinalIgnoreCase)) defaults.Add(cp);
                    }
                    for (int n = pr.U16(), i = 0; i < n; i++)
                    {
                        pr.U64(); char t = (char)pr.U8(); int count = pr.U16();
                        for (int k = 0; k < count; k++) { if (t == 'R') WalkAvatar(pr, inStart); else pr.U64(); }
                    }
                }
            }
            try { WalkAvatar(new StringUsage.Reader(sip.Read("Calligraphy/" + av), 4), false); }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { }
        }

        var result = new List<Costume>();
        foreach (string proto in costumes)
        {
            // Only the costume's own top-level fields (nested structs such as Icons are skipped over).
            var values = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            var texts = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            void Walk(StringUsage.Reader pr, bool top)
            {
                byte flags = pr.U8();
                if ((flags & 1) != 0) pr.U64();
                if ((flags & 2) == 0) return;
                for (int g = pr.U16(), gi = 0; gi < g; gi++)
                {
                    var names = Fields(pr.U64()); pr.U8();
                    for (int n = pr.U16(), i = 0; i < n; i++)
                    {
                        ulong fid = pr.U64(); char t = (char)pr.U8();
                        if (t == 'R') { Walk(pr, false); continue; }
                        ulong v = pr.U64();
                        if (top && names.TryGetValue(fid, out var nm)) { values.TryAdd(nm, v); if (t == 'S' && v != 0) texts.TryAdd(nm, v); }
                    }
                    for (int n = pr.U16(), i = 0; i < n; i++)
                    {
                        pr.U64(); char t = (char)pr.U8(); int count = pr.U16();
                        for (int k = 0; k < count; k++) { if (t == 'R') Walk(pr, false); else pr.U64(); }
                    }
                }
            }
            try { Walk(new StringUsage.Reader(sip.Read("Calligraphy/" + proto), 4), true); }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { continue; }
            string? Asset(string field) => values.TryGetValue(field, out ulong v) && assets.TryGetValue(v, out var s) ? s : null;
            string? cls = Asset("CostumeUnrealClass");
            if (cls == null) continue;
            string? hero = values.TryGetValue("UsableBy", out ulong h) && protoNames.TryGetValue(h, out var hp) ? hp : null;
            result.Add(new Costume(proto, cls, Asset("IconPath"), Asset("PortraitIconPath"), Asset("StoreIconPath"), Asset("PartyPortraitIconPath"),
                values.TryGetValue("DisplayName", out ulong dn) ? dn : 0, hero) { IsDefault = defaults.Contains(proto), Texts = texts });
        }
        return result;
    }
}
