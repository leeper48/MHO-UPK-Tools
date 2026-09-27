using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// What each game string is attached to (Kurt: in the string editor, "Vision" was the hero's name but also an NPC, items,
/// a stash page and a token). The game's data definitions (prototypes, in Data\Game\Calligraphy.sip) name their text by
/// string ID in fields of type S (DisplayName, Description, ItemSubcategory …); indexing every such field gives, for each
/// string ID, the prototypes and fields that use it. Read only.
///
/// Format (checked 2026-09-27: all 85,186 prototypes read, 70,535 of the 77,089 English strings have a user; the Python
/// decoder in MPM's publish\scans\tools had skipped curve fields' subtype and misread 43% of prototypes):
///   .sip: "KAPG", version, count; entries (u64 hash, u32 name length, name, i32 mod, offset, compressed, uncompressed size);
///         then the body; an entry is stored as is or as one LZ4 block.
///   Calligraphy/Blueprint.directory / Prototype.directory: 4-byte header, count, entries (ids, flags, path).
///   Blueprint: header, runtime binding, default prototype, parents, contributing blueprints, fields (id, name, base type,
///         structure type, and a subtype id for base types A C P R T).
///   Prototype: header, then data: flags (1 = parent reference follows, 2 = field groups follow); groups: blueprint id,
///         copy number, simple fields (id, type, value), list fields (id, type, count, values); value = u64, or nested data
///         for R (a struct).
/// </summary>
sealed class StringUsage
{
    public sealed record Use(string Prototype, string Field);

    readonly Dictionary<ulong, List<Use>> uses;
    StringUsage(Dictionary<ulong, List<Use>> uses) => this.uses = uses;

    public IReadOnlyList<Use> For(ulong id) => uses.TryGetValue(id, out var l) ? l : [];
    public int Count => uses.Count;

    static StringUsage? cached;
    static string? cachedKey;
    static readonly object gate = new();

    /// <summary>The index for a game folder, built once per session (a few seconds), or null if the data can't be read.</summary>
    public static StringUsage? Load(string gameRoot)
    {
        string sip = Path.Combine(gameRoot, "Data", "Game", "Calligraphy.sip");
        if (!File.Exists(sip)) return null;
        string key = sip + "|" + new FileInfo(sip).Length + "|" + File.GetLastWriteTimeUtc(sip).Ticks;
        lock (gate)
        {
            if (cached != null && cachedKey == key) return cached;
            try { cached = Build(sip); cachedKey = key; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { cached = null; }
            return cached;
        }
    }

    // ---- .sip archive

    sealed class Sip : IDisposable
    {
        readonly FileStream f;
        readonly long body;
        public readonly Dictionary<string, (int Offset, int Compressed, int Size)> Entries = new(StringComparer.OrdinalIgnoreCase);

        public Sip(string path)
        {
            f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var r = new BinaryReader(f, Encoding.Latin1, leaveOpen: true);
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "KAPG") throw new InvalidDataException("not a .sip archive");
            r.ReadInt32();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                r.ReadUInt64();
                string name = Encoding.Latin1.GetString(r.ReadBytes(r.ReadInt32()));
                r.ReadInt32();
                int off = r.ReadInt32(), csize = r.ReadInt32(), usize = r.ReadInt32();
                Entries[name.Replace('\\', '/')] = (off, csize, usize);
            }
            body = f.Position;
        }

        public byte[] Read(string name)
        {
            var (off, csize, usize) = Entries[name.Replace('\\', '/')];
            var data = new byte[csize];
            f.Position = body + off;
            f.ReadExactly(data);
            return csize == usize ? data : Lz4Block(data, usize);
        }

        static byte[] Lz4Block(byte[] src, int outLen)
        {
            var dst = new byte[outLen];
            int i = 0, o = 0;
            while (i < src.Length)
            {
                int token = src[i++], lit = token >> 4;
                if (lit == 15) { int b; do { b = src[i++]; lit += b; } while (b == 255); }
                Array.Copy(src, i, dst, o, lit); i += lit; o += lit;
                if (i >= src.Length) break;
                int off = src[i] | src[i + 1] << 8; i += 2;
                int ml = token & 15;
                if (ml == 15) { int b; do { b = src[i++]; ml += b; } while (b == 255); }
                ml += 4;
                for (int k = 0; k < ml; k++, o++) dst[o] = dst[o - off];
            }
            if (o != outLen) throw new InvalidDataException("bad LZ4 block");
            return dst;
        }

        public void Dispose() => f.Dispose();
    }

    sealed class Reader(byte[] b, int p = 0)
    {
        int p = p;
        public byte U8() => b[p++];
        public ushort U16() { var v = BitConverter.ToUInt16(b, p); p += 2; return v; }
        public int I32() { var v = BitConverter.ToInt32(b, p); p += 4; return v; }
        public ulong U64() { var v = BitConverter.ToUInt64(b, p); p += 8; return v; }
        public string S16() { int n = U16(); var s = Encoding.Latin1.GetString(b, p, n); p += n; return s; }
    }

    // ---- prototypes

    static StringUsage Build(string sipPath)
    {
        using var sip = new Sip(sipPath);
        byte[] Dir(string name) => sip.Read("Calligraphy/" + name + ".directory");
        var blueprints = new Dictionary<ulong, string>();
        var r = new Reader(Dir("Blueprint"), 4);
        for (int n = r.I32(), i = 0; i < n; i++) { ulong id = r.U64(); r.U64(); r.U8(); blueprints[id] = r.S16(); }
        var prototypes = new List<string>();
        r = new Reader(Dir("Prototype"), 4);
        for (int n = r.I32(), i = 0; i < n; i++) { r.U64(); r.U64(); r.U64(); r.U8(); prototypes.Add(r.S16()); }

        var fieldNames = new Dictionary<ulong, Dictionary<ulong, string>>();
        Dictionary<ulong, string> Fields(ulong bid)
        {
            if (fieldNames.TryGetValue(bid, out var f)) return f;
            f = [];
            try
            {
                if (blueprints.TryGetValue(bid, out string? file))
                {
                    var br = new Reader(sip.Read("Calligraphy/" + file), 4);
                    br.S16(); br.U64();
                    for (int n = br.U16(), i = 0; i < n; i++) { br.U64(); br.U8(); }
                    for (int n = br.U16(), i = 0; i < n; i++) { br.U64(); br.U8(); }
                    for (int n = br.U16(), i = 0; i < n; i++)
                    {
                        ulong fid = br.U64(); string name = br.S16(); char baseType = (char)br.U8(); br.U8();
                        if ("ACPRT".Contains(baseType)) br.U64();   // subtype
                        f[fid] = name;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { }   // names only: unknown is fine
            fieldNames[bid] = f;
            return f;
        }

        var uses = new Dictionary<ulong, List<Use>>();
        foreach (string proto in prototypes)
        {
            string shortName = proto.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase) ? proto[..^10] : proto;
            void Walk(Reader pr, string trail)
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
                        Value(pr, t, trail + (names.TryGetValue(fid, out var nm) ? nm : "?"));
                    }
                    for (int n = pr.U16(), i = 0; i < n; i++)
                    {
                        ulong fid = pr.U64(); char t = (char)pr.U8(); int count = pr.U16();
                        string nm = names.TryGetValue(fid, out var x) ? x : "?";
                        for (int k = 0; k < count; k++) Value(pr, t, trail + nm);
                    }
                }
            }
            void Value(Reader pr, char t, string field)
            {
                if (t == 'R') { Walk(pr, field + "."); return; }
                ulong v = pr.U64();
                if (t == 'S' && v != 0)
                {
                    if (!uses.TryGetValue(v, out var l)) uses[v] = l = [];
                    l.Add(new Use(shortName, field));
                }
            }
            try { Walk(new Reader(sip.Read("Calligraphy/" + proto), 4), ""); }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { }
        }
        return new StringUsage(uses);
    }

    // ---- plain words

    /// <summary>
    /// A use in plain words: "Hero: Vision · Name", "NPC: Vision · Name", "Item: Unique414 · Item Subcategory",
    /// "Power (Vision): PhasingStrike · Name" …
    /// </summary>
    public static string Describe(Use u)
    {
        var parts = u.Prototype.Split('\\', '/');
        string last = parts[^1];
        string kind =
            Has(parts, "Avatars") && Has(parts, "Shipping") ? "Hero" :
            Has(parts, "Costumes") ? "Costume" :
            Has(parts, "CharacterTokens") ? "Token" :
            Has(parts, "TeamUps") ? "Team-Up" :
            Has(parts, "NPCs") || Has(parts, "Vendors") ? "NPC" :
            parts[0].Equals("Entity", StringComparison.OrdinalIgnoreCase) && Has(parts, "Items") ? "Item" :
            parts[0].Equals("Powers", StringComparison.OrdinalIgnoreCase) ? (parts.Length > 3 && Has(parts, "Player") ? $"Power ({parts[2]})" : "Power") :
            parts[0].Equals("Missions", StringComparison.OrdinalIgnoreCase) ? "Mission" :
            parts[0].Equals("Regions", StringComparison.OrdinalIgnoreCase) ? "Region" :
            parts[0].Equals("Achievements", StringComparison.OrdinalIgnoreCase) ? "Achievement" :
            parts[0].Equals("UI", StringComparison.OrdinalIgnoreCase) ? "Interface" :
            parts[0].Equals("Entity", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? parts[1] : parts[0];
        return $"{kind}: {last} · {Words(u.Field)}";
    }

    /// <summary>Sort key: hero names first, then costumes, powers, other uses.</summary>
    public static int Rank(IReadOnlyList<Use> uses) =>
        uses.Count == 0 ? 9 : uses.Min(u =>
        {
            string d = Describe(u);
            return d.StartsWith("Hero:") ? (u.Field == "DisplayName" ? 0 : 1) : d.StartsWith("Costume:") ? 2 : d.StartsWith("Team-Up:") ? 3 : d.StartsWith("Power") ? 4 : d.StartsWith("NPC:") ? 5 : 6;
        });

    static bool Has(string[] parts, string p) => parts.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>"DisplayName" → "Name", "ItemSubcategory" → "Item Subcategory".</summary>
    static string Words(string field)
    {
        string f = field.Replace("DisplayName", "Name");
        var sb = new StringBuilder();
        for (int i = 0; i < f.Length; i++)
        {
            if (i > 0 && char.IsUpper(f[i]) && char.IsLower(f[i - 1])) sb.Append(' ');
            sb.Append(f[i]);
        }
        return sb.ToString();
    }
}
