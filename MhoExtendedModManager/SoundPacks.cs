using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// .mhsfx sound packs (MHModManager's format): a zip of mod.json + .wem files. Each patch ("new_event") adds a Wwise
/// event to a bank inside a CookedPCConsole .pck, cloned from an existing event (original_event_name) so the mod's
/// costume package can play it by name (its AkEvent objects are named after event_name).
/// </summary>
sealed class SoundPack
{
    public sealed record Patch(string Type, string OriginalEvent, string EventName, uint EventHash, uint ActionId, uint SoundId, uint SourceId, string WemFile, string Bank, string PckFile);

    public required string File { get; init; }
    public string Name { get; init; } = "";
    public List<Patch> Patches { get; } = [];
    public Dictionary<string, byte[]> Wems { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static SoundPack Load(string path)
    {
        using var z = ZipFile.OpenRead(path);
        var entry = z.GetEntry("mod.json") ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no mod.json");
        using var doc = JsonDocument.Parse(new StreamReader(entry.Open()).ReadToEnd());
        var r = doc.RootElement;
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
        uint H(JsonElement e, string n) { string s = S(e, n); return Convert.ToUInt32(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, 16); }
        var pack = new SoundPack { File = path, Name = S(r, "name") };
        foreach (var p in r.GetProperty("patches").EnumerateArray())
            pack.Patches.Add(new Patch(S(p, "type"), S(p, "original_event_name"), S(p, "event_name"), H(p, "event_hash"), H(p, "action_id"), H(p, "sound_id"), H(p, "source_id"), S(p, "wem_file"), S(p, "bank_name"), S(p, "pck_file")));
        // The .pck a line patches is a game file: a bare name only (ModSafety; "..\x" would leave the game folder).
        if (pack.Patches.FirstOrDefault(x => !ModSafety.GameFileName(x.PckFile) || !x.PckFile.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)) is { } bad)
            throw new InvalidDataException($"{Path.GetFileName(path)}: pck_file \"{bad.PckFile}\" isn't a game sound file name");
        // Read into memory: at most 1 GB of audio per pack (a zip bomb would otherwise exhaust memory).
        if (z.Entries.Where(e => e.FullName.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Length) > 1L << 30)
            throw new InvalidDataException($"{Path.GetFileName(path)}: more than 1 GB of sound");
        foreach (var e in z.Entries.Where(e => e.FullName.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)))
        {
            using var ms = new MemoryStream(); e.Open().CopyTo(ms); pack.Wems[e.FullName] = ms.ToArray();
        }
        return pack;
    }

    /// <summary>Wwise's 32-bit FNV-1 hash of a lower-cased name (event and bank IDs).</summary>
    public static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (byte c in System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant())) { h *= 16777619; h ^= c; }
        return h;
    }
}

/// <summary>
/// A Wwise file package (.pck, "AKPK") and the patching MHModManager 1.0.1's sound library does, re-derived from the files
/// and checked against that library's own output on copies (2026-09-27, both of the Miles Morales pack's .pck files).
/// <list type="bullet">
/// <item>Header: "AKPK", header size, version (1), sizes of the language map, bank table, stream table and externals table
/// (4: an empty count); then those four. Tables: count, then per file ID, block size (1), size, start block, language.</item>
/// <item>Written as: header, every bank (table order), every stream (table order), each start aligned to 16 with zeros.
/// Streams are sorted by ID; new ones are inserted.</item>
/// <item>A bank is chunks (tag, size, data). HIRC: count, then objects (type u8, size u32 = 4 + body, ID, body). Each patch
/// appends 3 objects: a Sound cloned from the original chain (source ID at body 5; for media embedded in the bank, stream
/// type 0 at body 4, also the media size at body 9), an Action cloned from the event's first action with its target
/// (body 2) set to the new sound, and an Event (1 action). The chain's sound is the action's target, or for a container
/// the first sound ID listed in its body.</item>
/// <item>New media: streamed (stream type 2) goes into the stream table (bank's language); embedded (0) is appended to
/// the bank's DATA (each piece padded to 16) with a DIDX entry (ID, offset, size) inserted in ID order.</item>
/// </list>
/// </summary>
sealed class Akpk
{
    public sealed record Entry(uint Id, uint BlockSize, uint Size, uint StartBlock, uint Language);

    public uint Version;
    public byte[] LanguageMap = [];
    public byte[] Externals = [];
    public List<Entry> Banks = [];
    public List<Entry> Streams = [];

    public static Akpk Read(Stream f)
    {
        var h = new byte[28];
        f.Position = 0; f.ReadExactly(h);
        if (h[0] != 'A' || h[1] != 'K' || h[2] != 'P' || h[3] != 'K') throw new InvalidDataException("not a Wwise file package (no AKPK)");
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(at));
        uint headerSize = U(4);
        var hdr = new byte[8 + headerSize];
        f.Position = 0; f.ReadExactly(hdr);
        int lang = (int)U(12), banks = (int)U(16), streams = (int)U(20), ext = (int)U(24);
        if (28 + lang + banks + streams + ext != hdr.Length) throw new InvalidDataException("AKPK header sizes don't add up");
        var a = new Akpk { Version = U(8), LanguageMap = hdr.AsSpan(28, lang).ToArray(), Externals = hdr.AsSpan(28 + lang + banks + streams, ext).ToArray() };
        a.Banks = Table(hdr, 28 + lang); a.Streams = Table(hdr, 28 + lang + banks);
        return a;
    }

    static List<Entry> Table(byte[] b, int at)
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        var l = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            var s = b.AsSpan(at + 4 + 20 * i);
            l.Add(new Entry(BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[16..])));
        }
        return l;
    }

    public static byte[] ReadData(Stream f, Entry e)
    {
        var d = new byte[e.Size];
        f.Position = (long)e.StartBlock * e.BlockSize; f.ReadExactly(d);
        return d;
    }

    /// <summary>
    /// Applies the packs' patches for this .pck (in the given order) to the original and returns the new file, or null
    /// with problems. Patches whose original event isn't in its bank are skipped with a note (as the old library does).
    /// </summary>
    public static byte[]? Build(string originalPath, string pckName, IReadOnlyList<SoundPack> packs, List<string> notes, List<string> problems)
    {
        using var f = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var pk = Read(f);
        var patches = packs.SelectMany(p => p.Patches.Where(x => x.PckFile.Equals(pckName, StringComparison.OrdinalIgnoreCase)).Select(x => (Pack: p, Patch: x))).ToList();
        var bankData = new Dictionary<uint, byte[]>();
        var newStreams = new List<(uint Id, uint Lang, byte[] Data)>();
        foreach (var group in patches.GroupBy(x => SoundPack.Fnv(x.Patch.Bank)))
        {
            var entry = pk.Banks.FirstOrDefault(b => b.Id == group.Key);
            if (entry == null) { problems.Add($"{pckName}: no bank '{group.First().Patch.Bank}'"); continue; }
            var bank = new Bank(ReadData(f, entry));
            // A line can copy an event another line adds (a voice pack building on its own lines): lines whose original
            // event isn't there yet are tried again after the rest, until a pass adds nothing. (User report 2026-09-28:
            // an Aurheon voice pack's lines copy Aurheon_… events, which aren't in the stock bank.)
            var waiting = new List<(SoundPack Pack, SoundPack.Patch Patch, byte[] Wem, string Err)>();
            string? Add(SoundPack pack, SoundPack.Patch p, byte[] wem)
            {
                string? err = bank.AddEvent(p, wem, out bool streamed);
                if (err != null) return err;
                if (streamed)
                {
                    if (pk.Streams.Any(s => s.Id == p.SourceId) || newStreams.Any(s => s.Id == p.SourceId)) { problems.Add($"{p.EventName}: stream {p.SourceId:X8} already exists"); return null; }
                    newStreams.Add((p.SourceId, entry.Language, wem));
                }
                return null;
            }
            foreach (var (pack, p) in group)
            {
                if (p.Type != "new_event") { problems.Add($"{p.EventName}: patch type '{p.Type}' not supported"); continue; }
                if (!pack.Wems.TryGetValue(p.WemFile, out byte[]? wem)) { problems.Add($"{p.EventName}: {p.WemFile} missing from {Path.GetFileName(pack.File)}"); continue; }
                if (SoundPack.Fnv(p.EventName) != p.EventHash) notes.Add($"{p.EventName}: event_hash {p.EventHash:X8} isn't the name's hash {SoundPack.Fnv(p.EventName):X8} (used as given)");
                if (Add(pack, p, wem) is string err) waiting.Add((pack, p, wem, err));
            }
            for (bool progress = true; progress && waiting.Count > 0; )
            {
                progress = false;
                foreach (var wt in waiting.ToList())
                    if (wt.Err.StartsWith("original event") && Add(wt.Pack, wt.Patch, wt.Wem) == null) { waiting.Remove(wt); progress = true; }
            }
            foreach (var wt in waiting) notes.Add($"{wt.Patch.EventName}: skipped ({wt.Err})");
            bankData[entry.Id] = bank.Write();
        }
        if (problems.Count > 0) return null;

        // Layout: header, banks, streams, each aligned to 16.
        var streams = pk.Streams.Select(s => (s.Id, s.Language, Old: (Entry?)s, New: (byte[]?)null))
            .Concat(newStreams.Select(s => (s.Id, Language: s.Lang, Old: (Entry?)null, New: (byte[]?)s.Data))).OrderBy(s => s.Id).ToList();
        int lutBanks = 4 + 20 * pk.Banks.Count, lutStreams = 4 + 20 * streams.Count;
        long headerEnd = 28 + pk.LanguageMap.Length + lutBanks + lutStreams + pk.Externals.Length;
        long pos = headerEnd;
        long Align(long p) => (p + 15) / 16 * 16;
        var bankOut = new List<Entry>();
        foreach (var b in pk.Banks)
        {
            pos = Align(pos);
            uint size = bankData.TryGetValue(b.Id, out var nb) ? (uint)nb.Length : b.Size;
            bankOut.Add(new Entry(b.Id, 1, size, checked((uint)pos), b.Language));
            pos += size;
        }
        var streamOut = new List<Entry>();
        foreach (var s in streams)
        {
            pos = Align(pos);
            uint size = s.Old?.Size ?? (uint)s.New!.Length;
            streamOut.Add(new Entry(s.Id, 1, size, checked((uint)pos), s.Language));
            pos += size;
        }
        var output = new byte[pos];
        var w = output.AsSpan();
        "AKPK"u8.CopyTo(w);
        BinaryPrimitives.WriteUInt32LittleEndian(w[4..], (uint)(headerEnd - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(w[8..], pk.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(w[12..], (uint)pk.LanguageMap.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(w[16..], (uint)lutBanks);
        BinaryPrimitives.WriteUInt32LittleEndian(w[20..], (uint)lutStreams);
        BinaryPrimitives.WriteUInt32LittleEndian(w[24..], (uint)pk.Externals.Length);
        int at = 28;
        pk.LanguageMap.CopyTo(w[at..]); at += pk.LanguageMap.Length;
        at = WriteTable(w, at, bankOut); at = WriteTable(w, at, streamOut);
        pk.Externals.CopyTo(w[at..]);
        for (int i = 0; i < pk.Banks.Count; i++)
            (bankData.TryGetValue(pk.Banks[i].Id, out var nb) ? nb : ReadData(f, pk.Banks[i])).CopyTo(w[(int)bankOut[i].StartBlock..]);
        for (int i = 0; i < streams.Count; i++)
            (streams[i].New ?? ReadData(f, streams[i].Old!)).CopyTo(output, streamOut[i].StartBlock);
        return output;
    }

    /// <summary>Diagnostic (--sound-container): the sound a patch would clone for an action target, or why not.</summary>
    public static string ProbeTarget(string pckPath, uint bankId, uint target)
    {
        using var f = File.OpenRead(pckPath);
        var pk = Read(f);
        var entry = pk.Banks.FirstOrDefault(b => b.Id == bankId);
        if (entry == null) return $"no bank {bankId:X8}";
        return new Bank(ReadData(f, entry)).Probe(target);
    }

    /// <summary>The event IDs (HIRC type 4) of a bank's bytes, without building the bank (voice playback's event index).</summary>
    public static IEnumerable<uint> EventIds(byte[] b)
    {
        var ids = new List<uint>();
        for (int p = 0; p + 8 <= b.Length; )
        {
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
            if (n < 0 || p + 8 + n > b.Length) break;
            if (b[p] == 'H' && b[p + 1] == 'I' && b[p + 2] == 'R' && b[p + 3] == 'C')
            {
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 8)), q = p + 12, end = p + 8 + n;
                for (int i = 0; i < count && q + 9 <= end; i++)
                {
                    int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(q + 1));
                    if (b[q] == 4) ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(q + 5)));
                    q += 5 + size;
                }
            }
            p += 8 + n;
        }
        return ids;
    }

    /// <summary>Voice playback: what an event in one of this .pck's banks plays (see Bank.Media).</summary>
    public static (uint SourceId, byte[]? Embedded)? EventMedia(Stream f, Entry bank, uint eventId) => new Bank(ReadData(f, bank)).Media(eventId);

    static int WriteTable(Span<byte> w, int at, List<Entry> t)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(w[at..], (uint)t.Count); at += 4;
        foreach (var e in t)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(w[at..], e.Id); BinaryPrimitives.WriteUInt32LittleEndian(w[(at + 4)..], e.BlockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(w[(at + 8)..], e.Size); BinaryPrimitives.WriteUInt32LittleEndian(w[(at + 12)..], e.StartBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(w[(at + 16)..], e.Language);
            at += 20;
        }
        return at;
    }

    /// <summary>One bank's chunks, with the HIRC objects and embedded media editable.</summary>
    sealed class Bank
    {
        readonly List<(string Tag, byte[] Data)> chunks = [];
        readonly List<(byte Type, uint Id, byte[] Body)> objects = [];
        readonly Dictionary<uint, int> index = [];
        readonly List<(uint Id, uint Offset, uint Size)> didx = [];
        readonly MemoryStream data = new();

        public Bank(byte[] b)
        {
            int p = 0;
            while (p < b.Length)
            {
                string tag = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
                chunks.Add((tag, b.AsSpan(p + 8, n).ToArray()));
                p += 8 + n;
            }
            if (Chunk("HIRC") is byte[] h)
            {
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(h), q = 4;
                for (int i = 0; i < count; i++)
                {
                    byte t = h[q]; int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(q + 1)); uint id = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(q + 5));
                    index.TryAdd(id, objects.Count);
                    objects.Add((t, id, h.AsSpan(q + 9, size - 4).ToArray()));
                    q += 5 + size;
                }
            }
            if (Chunk("DIDX") is byte[] d)
                for (int i = 0; i + 12 <= d.Length; i += 12)
                    didx.Add((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 4)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(i + 8))));
            if (Chunk("DATA") is byte[] dd) data.Write(dd);
        }

        byte[]? Chunk(string tag) => chunks.FirstOrDefault(c => c.Tag == tag).Data;
        (byte Type, uint Id, byte[] Body)? Obj(uint id) => index.TryGetValue(id, out int i) ? objects[i] : null;

        /// <summary>
        /// A container whose children are containers (seen 2026-09-28: 3F5A4534, a random container holding two more): the
        /// first sound under its first listed real child, depth first. A child is an object the container lists that also
        /// names the container (its parent) in its own body, so the parent, a bus or the like is never followed.
        /// </summary>
        (byte Type, uint Id, byte[] Body)? NestedSound((byte Type, uint Id, byte[] Body) c, int depth, HashSet<uint> seen)
        {
            if (depth > 6 || !seen.Add(c.Id)) return null;
            Span<byte> key = stackalloc byte[4], parentKey = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(parentKey, c.Id);
            var kids = new List<(int At, (byte Type, uint Id, byte[] Body) Obj)>();
            foreach (var o in objects)
            {
                if (o.Id == c.Id || o.Type is not (2 or 5 or 6 or 9) || o.Body.AsSpan().IndexOf(parentKey) < 0) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(key, o.Id);
                int at = c.Body.AsSpan().IndexOf(key);
                if (at >= 0) kids.Add((at, o));
            }
            foreach (var (_, o) in kids.OrderBy(k => k.At))
            {
                if (o.Type == 2) return o;
                if (NestedSound(o, depth + 1, seen) is { } s) return s;
            }
            return null;
        }

        /// <summary>
        /// What an event plays (voice playback): its first action whose target is, or holds, a sound → that sound's media:
        /// embedded (the bytes, from DIDX / DATA) or streamed (the source ID, to look up in a .pck's stream table). Null if none.
        /// </summary>
        public (uint SourceId, byte[]? Embedded)? Media(uint eventId)
        {
            if (Obj(eventId) is not { } ev || ev.Type != 4 || ev.Body.Length < 4) return null;
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(ev.Body);
            for (int i = 0; i < n && 8 + 4 * i <= ev.Body.Length; i++)
            {
                if (Obj(BinaryPrimitives.ReadUInt32LittleEndian(ev.Body.AsSpan(4 + 4 * i))) is not { } act || act.Type != 3 || act.Body.Length < 6) continue;
                if (Obj(BinaryPrimitives.ReadUInt32LittleEndian(act.Body.AsSpan(2))) is not { } tgt) continue;
                var sound = tgt.Type == 2 ? tgt : NestedSound(tgt, 0, []);
                if (sound is not { } s || s.Body.Length < 13) continue;
                uint source = BinaryPrimitives.ReadUInt32LittleEndian(s.Body.AsSpan(5));
                if (s.Body[4] != 0) return (source, null);
                var d = didx.FirstOrDefault(x => x.Id == source);
                if (d.Id != source || d.Offset + d.Size > data.Length) continue;
                return (source, data.GetBuffer().AsSpan((int)d.Offset, (int)d.Size).ToArray());
            }
            return null;
        }

        public string Probe(uint target)
        {
            if (Obj(target) is not { } tgt) return $"{target:X8} not in the bank";
            if (tgt.Type == 2) return $"{target:X8} is a sound";
            return NestedSound(tgt, 0, []) is { } s ? $"{target:X8} (type {tgt.Type}): first sound {s.Id:X8}" : $"{target:X8} (type {tgt.Type}): no sound under it";
        }

        /// <summary>Adds the Sound, Action and Event for one patch. Returns an error (nothing added) or null; streamed says where the media goes.</summary>
        public string? AddEvent(SoundPack.Patch p, byte[] wem, out bool streamed)
        {
            streamed = false;
            if (Obj(SoundPack.Fnv(p.OriginalEvent)) is not { } ev || ev.Type != 4) return $"original event '{p.OriginalEvent}' not in the bank";
            if (Obj(p.EventHash) != null || Obj(p.ActionId) != null || Obj(p.SoundId) != null) return "its IDs are already in the bank";
            if (BinaryPrimitives.ReadUInt32LittleEndian(ev.Body) == 0) return "original event has no actions";
            uint actionId = BinaryPrimitives.ReadUInt32LittleEndian(ev.Body.AsSpan(4));
            if (Obj(actionId) is not { } act || act.Type != 3 || act.Body.Length < 6) return "original event's action not found";
            uint target = BinaryPrimitives.ReadUInt32LittleEndian(act.Body.AsSpan(2));
            if (Obj(target) is not { } tgt) return "action target not in the bank";
            var sound = tgt;
            if (tgt.Type != 2)
            {
                // A container: its first listed child sound.
                int best = int.MaxValue; (byte, uint, byte[])? found = null;
                Span<byte> key = stackalloc byte[4];
                foreach (var o in objects.Where(o => o.Type == 2))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(key, o.Id);
                    int at = tgt.Body.AsSpan().IndexOf(key);
                    if (at >= 0 && at < best) { best = at; found = o; }
                }
                found ??= NestedSound(tgt, 0, []);
                if (found is not { } fs) return $"target {target:X8} (type {tgt.Type}) has no child sound";
                sound = fs;
            }
            byte[] body = (byte[])sound.Body.Clone();
            if (body.Length < 13) return "sound object too short";
            byte streamType = body[4];
            if (streamType is not (0 or 2)) return $"stream type {streamType} not supported";
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), p.SourceId);
            if (streamType == 0)
            {
                if (Chunk("DIDX") == null || Chunk("DATA") == null) return "embedded media but the bank has no DIDX/DATA";
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), (uint)wem.Length);
                uint offset = (uint)data.Length;
                data.Write(wem);
                while (data.Length % 16 != 0) data.WriteByte(0);
                didx.Add((p.SourceId, offset, (uint)wem.Length));
            }
            else streamed = true;
            byte[] action = (byte[])act.Body.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(action.AsSpan(2), p.SoundId);
            byte[] evBody = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(evBody, 1); BinaryPrimitives.WriteUInt32LittleEndian(evBody.AsSpan(4), p.ActionId);
            foreach (var o in new[] { ((byte)2, p.SoundId, body), ((byte)3, p.ActionId, action), ((byte)4, p.EventHash, evBody) })
            {
                index[o.Item2] = objects.Count; objects.Add(o);
            }
            return null;
        }

        public byte[] Write()
        {
            using var ms = new MemoryStream();
            Span<byte> u = stackalloc byte[4];
            foreach (var (tag, old) in chunks)
            {
                byte[] d = tag switch
                {
                    "HIRC" => Hirc(),
                    "DIDX" => didx.OrderBy(x => x.Id).SelectMany(x => BitConverter.GetBytes(x.Id).Concat(BitConverter.GetBytes(x.Offset)).Concat(BitConverter.GetBytes(x.Size))).ToArray(),
                    "DATA" => data.ToArray(),
                    _ => old,
                };
                ms.Write(System.Text.Encoding.ASCII.GetBytes(tag));
                BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)d.Length); ms.Write(u);
                ms.Write(d);
            }
            return ms.ToArray();
        }

        byte[] Hirc()
        {
            using var ms = new MemoryStream();
            Span<byte> u = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)objects.Count); ms.Write(u);
            foreach (var (t, id, body) in objects)
            {
                ms.WriteByte(t);
                BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)(body.Length + 4)); ms.Write(u);
                BinaryPrimitives.WriteUInt32LittleEndian(u, id); ms.Write(u);
                ms.Write(body);
            }
            return ms.ToArray();
        }
    }
}
