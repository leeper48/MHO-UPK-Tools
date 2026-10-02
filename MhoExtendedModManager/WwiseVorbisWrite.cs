namespace MhoExtendedModManager;

/// <summary>
/// Standard Vorbis → Wwise Vorbis (.wem), the reverse of WwiseVorbis (Kurt, 2026-10-02: shifted voice lines MHModManager 1.0.1
/// can apply too, which writes every added sound as Vorbis). Wwise strips the Vorbis headers and refers to codebooks by id in
/// its shared library (packed_codebooks_aoTuV_603, the one the game's runtime has); so a stream can be written the game's way
/// only if each of its codebooks is in that library, checked here bit for bit.
/// </summary>
static class WwiseVorbisWrite
{
    /// <summary>Bits written LSB first (Vorbis order).</summary>
    internal sealed class BitW
    {
        readonly List<byte> bytes = [];
        int bit;
        public int Count { get; private set; }
        public void Write(uint v, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (bit == 0) bytes.Add(0);
                if ((v >> i & 1) != 0) bytes[^1] |= (byte)(1 << bit);
                bit = (bit + 1) & 7; Count++;
            }
        }
        public byte[] ToArray() => [.. bytes];
        public string Key => Count + ":" + Convert.ToBase64String(bytes.ToArray());
    }

    static Dictionary<string, int>? library;

    /// <summary>Every library codebook rebuilt to its standard form, by its exact bits → its id.</summary>
    static Dictionary<string, int> LibraryIndex()
    {
        if (library != null) return library;
        var (bytes, offs) = WwiseVorbis.Library();
        var d = new Dictionary<string, int>();
        for (int id = 0; id < offs.Length - 1; id++)
        {
            int start = offs[id], size = offs[id + 1] - start;
            if (size <= 0) continue;
            var w = new BitW();
            try { WwiseVorbis.Rebuild(new WwiseVorbis.BitIn(bytes[..(start + size)], start), size, w.Write); }
            catch (InvalidDataException) { continue; }
            d.TryAdd(w.Key, id);
        }
        return library = d;
    }

    /// <summary>The codebooks of a standard setup packet ("\x05vorbis", count, codebooks…), each with its library id or -1.</summary>
    public static List<(int Id, int Bits)> MatchCodebooks(byte[] setup)
    {
        var lib = LibraryIndex();
        var bis = new WwiseVorbis.BitIn(setup, 7);
        int count = (int)bis.Read(8) + 1;
        var res = new List<(int, int)>();
        for (int i = 0; i < count; i++)
        {
            var w = new BitW();
            WwiseVorbis.Copy(bis, w.Write);
            res.Add((lib.TryGetValue(w.Key, out int id) ? id : -1, w.Count));
        }
        return res;
    }

    public static int LibraryCount => LibraryIndex().Count;

    static int ILog(uint v) { int r = 0; while (v != 0) { r++; v >>= 1; } return r; }

    /// <summary>
    /// A standard setup packet stripped the Wwise way (the reverse of WwiseVorbis.Setup): codebooks as 10-bit library ids;
    /// no time-domain list; floors without their type (all floor1); residue types in 2 bits; mappings without their type;
    /// modes without window and transform types; no framing bit. Also returns each mode's long-block flag.
    /// </summary>
    public static (byte[] Setup, bool[] ModeBlockflag) StripSetup(byte[] setup, int channels)
    {
        var lib = LibraryIndex();
        var ss = new WwiseVorbis.BitIn(setup, 7);
        var os = new BitW();
        uint cbLess1 = ss.Read(8); os.Write(cbLess1, 8);
        for (uint i = 0; i <= cbLess1; i++)
        {
            var w = new BitW();
            WwiseVorbis.Copy(ss, w.Write);
            if (!lib.TryGetValue(w.Key, out int id)) throw new InvalidDataException($"codebook {i} isn't in Wwise's library");
            os.Write((uint)id, 10);
        }
        uint timeLess1 = ss.Read(6);
        for (uint i = 0; i <= timeLess1; i++) if (ss.Read(16) != 0) throw new InvalidDataException("time domain not 0");
        uint floorLess1 = ss.Read(6); os.Write(floorLess1, 6);
        for (uint i = 0; i <= floorLess1; i++)
        {
            if (ss.Read(16) != 1) throw new InvalidDataException("not floor 1");
            uint partitions = ss.Read(5); os.Write(partitions, 5);
            var cls = new uint[partitions]; uint maxClass = 0;
            for (int j = 0; j < partitions; j++) { cls[j] = ss.Read(4); os.Write(cls[j], 4); maxClass = Math.Max(maxClass, cls[j]); }
            var dims = new uint[maxClass + 1];
            for (uint j = 0; j <= maxClass; j++)
            {
                uint d = ss.Read(3); os.Write(d, 3); dims[j] = d + 1;
                uint sub = ss.Read(2); os.Write(sub, 2);
                if (sub != 0) os.Write(ss.Read(8), 8);
                for (int k = 0; k < 1 << (int)sub; k++) os.Write(ss.Read(8), 8);
            }
            os.Write(ss.Read(2), 2);
            uint rangebits = ss.Read(4); os.Write(rangebits, 4);
            for (int j = 0; j < partitions; j++) for (uint k = 0; k < dims[cls[j]]; k++) os.Write(ss.Read((int)rangebits), (int)rangebits);
        }
        uint resLess1 = ss.Read(6); os.Write(resLess1, 6);
        for (uint i = 0; i <= resLess1; i++)
        {
            uint type = ss.Read(16); os.Write(type, 2);
            os.Write(ss.Read(24), 24); os.Write(ss.Read(24), 24); os.Write(ss.Read(24), 24);
            uint classLess1 = ss.Read(6); os.Write(classLess1, 6); os.Write(ss.Read(8), 8);
            var cascade = new uint[classLess1 + 1];
            for (int j = 0; j <= classLess1; j++)
            {
                uint low = ss.Read(3); os.Write(low, 3); uint flag = ss.Read(1); os.Write(flag, 1); uint high = 0;
                if (flag != 0) { high = ss.Read(5); os.Write(high, 5); }
                cascade[j] = high * 8 + low;
            }
            for (int j = 0; j <= classLess1; j++) for (int k = 0; k < 8; k++) if ((cascade[j] & 1 << k) != 0) os.Write(ss.Read(8), 8);
        }
        uint mapLess1 = ss.Read(6); os.Write(mapLess1, 6);
        for (uint i = 0; i <= mapLess1; i++)
        {
            if (ss.Read(16) != 0) throw new InvalidDataException("mapping type not 0");
            uint subFlag = ss.Read(1); os.Write(subFlag, 1); uint submaps = 1;
            if (subFlag != 0) { uint sm = ss.Read(4); os.Write(sm, 4); submaps = sm + 1; }
            uint polar = ss.Read(1); os.Write(polar, 1);
            if (polar != 0)
            {
                uint steps = ss.Read(8); os.Write(steps, 8); int bits = ILog((uint)channels - 1);
                for (uint j = 0; j <= steps; j++) { os.Write(ss.Read(bits), bits); os.Write(ss.Read(bits), bits); }
            }
            os.Write(ss.Read(2), 2);
            if (submaps > 1) for (int j = 0; j < channels; j++) os.Write(ss.Read(4), 4);
            for (uint j = 0; j < submaps; j++) { os.Write(ss.Read(8), 8); os.Write(ss.Read(8), 8); os.Write(ss.Read(8), 8); }
        }
        uint modeLess1 = ss.Read(6); os.Write(modeLess1, 6);
        var flags = new bool[modeLess1 + 1];
        for (uint i = 0; i <= modeLess1; i++)
        {
            uint flag = ss.Read(1); os.Write(flag, 1); flags[i] = flag != 0;
            ss.Read(16); ss.Read(16);
            os.Write(ss.Read(8), 8);
        }
        return (os.ToArray(), flags);
    }

    /// <summary>Wwise's codebook hash (vorb uHashCodebook): FNV-1 over the stripped setup's bytes (matches the game's files).</summary>
    public static uint SetupHash(byte[] stripped) { uint h = 2166136261; foreach (byte c in stripped) { h *= 16777619; h ^= c; } return h; }

    /// <summary>Decoder allocation sizes (vorb dwDecodeAllocSize / dwDecodeX64AllocSize) for a setup the game's own voice files
    /// use (by its hash); a stream with another setup isn't written (its sizes aren't known).</summary>
    // The game's three Vorbis setups (--wem-setups over every .pck, 2026-10-02: one allocation pair each): 22 kHz mono (every
    // voice, 87,467 streams), 32 kHz stereo (774), 22 kHz stereo (60).
    static readonly Dictionary<uint, (uint Alloc, uint Alloc64)> KnownAlloc = new() { [0x20CEF588] = (0x451C, 0x46C4), [0xC279396B] = (0x3ED0, 0x40B0), [0xE1B9F11E] = (0x3314, 0x34B4) };

    /// <summary>
    /// An encoded stream as a Wwise Vorbis .wem, laid out as the game's own voice files (SFX_Cyclops_INT, checked field by
    /// field): RIFF/WAVE; a 0x42-byte fmt (0xFFFF, channels, rate, average bytes per second, 0, 0, cbSize 0x30, Wwise's channel
    /// config, then the vorb fields: frames, loop start = audio offset, loop end = data size, 0 / last-granule extra, seek
    /// table 0, audio offset, max packet size / last-granule extra, the decoder's allocation sizes, the setup's hash, the two
    /// block sizes); a data chunk of the stripped setup and the audio packets, each after a 16-bit size, the audio in Wwise's
    /// form (no packet-type bit; long blocks without the previous / next window flags).
    /// <paramref name="samples"/> is the line's true length (the encoder's lead-in silence is what a decoder drops).
    /// </summary>
    public static byte[] ToWem(VorbisEncode.Encoded e, long samples)
    {
        var (stripped, blockflag) = StripSetup(e.Setup, e.Channels);
        uint hash = SetupHash(stripped);
        if (!KnownAlloc.TryGetValue(hash, out var alloc)) throw new InvalidDataException($"the stream's setup (hash {hash:X8}) isn't one the game's own files use; encode at a quality that matches (0.1 to 0.4 for 22 kHz mono)");
        int bs0 = e.Info[28] & 15, bs1 = e.Info[28] >> 4;
        int modeBits = ILog((uint)blockflag.Length - 1);
        var data = new MemoryStream(); var dw = new BinaryWriter(data);
        dw.Write((ushort)stripped.Length); dw.Write(stripped);
        uint audioOffset = (uint)data.Length;
        long total = 0; int prev = 0, maxPacket = 0;
        foreach (var (pk, _) in e.Audio)
        {
            if (pk.Length == 0) continue;
            var bis = new WwiseVorbis.BitIn(pk, 0);
            if (bis.Read(1) != 0) throw new InvalidDataException("not an audio packet");
            uint mode = bis.Read(modeBits);
            var w = new BitW(); w.Write(mode, modeBits);
            if (blockflag[mode]) bis.Read(2);   // previous / next window flags: Wwise works them out from the neighbours
            for (long left = pk.Length * 8L - bis.TotalBits; left > 0; left--) w.Write(bis.Read(1), 1);
            byte[] mp = w.ToArray();
            dw.Write((ushort)mp.Length); dw.Write(mp);
            maxPacket = Math.Max(maxPacket, mp.Length);
            int cur = 1 << (blockflag[mode] ? bs1 : bs0);
            if (prev != 0) total += prev / 4 + cur / 4;
            prev = cur;
        }
        if (total < samples) throw new InvalidDataException($"the packets decode to {total} samples, fewer than the line's {samples}");
        uint extra = (uint)(total - samples);
        byte[] body = data.ToArray();
        uint config = (uint)e.Channels | 1u << 8 | (e.Channels == 1 ? 0x4u : 0x3u) << 12;
        var ms = new MemoryStream(); var o = new BinaryWriter(ms);
        o.Write("RIFF"u8); o.Write(4 + 8 + 0x42 + 8 + body.Length); o.Write("WAVE"u8);
        o.Write("fmt "u8); o.Write(0x42);
        o.Write((ushort)0xFFFF); o.Write((ushort)e.Channels); o.Write(e.Rate); o.Write((uint)(body.Length * (long)e.Rate / samples));
        o.Write((ushort)0); o.Write((ushort)0); o.Write((ushort)0x30); o.Write((ushort)0); o.Write(config);
        o.Write((uint)samples); o.Write(audioOffset); o.Write((uint)body.Length); o.Write((ushort)0); o.Write((ushort)extra);
        o.Write(0u); o.Write(audioOffset); o.Write((ushort)maxPacket); o.Write((ushort)extra);
        o.Write(alloc.Alloc); o.Write(alloc.Alloc64); o.Write(hash); o.Write((byte)bs0); o.Write((byte)bs1);
        o.Write("data"u8); o.Write(body.Length); o.Write(body);
        return ms.ToArray();
    }
}
