using System.Buffers.Binary;

namespace MhoExtendedModManager;

/// <summary>
/// Wwise RIFF Vorbis (.wem, format 0xFFFF) → standard Ogg Vorbis, a C# port of hcs's ww2ogg 0.24 (BSD licence, see
/// THIRD-PARTY-NOTICES.txt; Tremor's CRC and quantvals are Xiph's). Wwise strips the Vorbis headers and replaces the
/// codebooks with ids into a shared library (Assets\packed_codebooks_aoTuV_603.bin); this rebuilds them. Only the forms
/// ww2ogg handles with its defaults (external codebooks, stripped setup; the old header-triad form too).
/// </summary>
static class WwiseVorbis
{
    /// <summary>The samples the packets of the last ToOgg(positions: true) decode to, from their block sizes (analysis).</summary>
    [ThreadStatic] internal static long LastBlockTotal;

    static byte[]? codebooks;
    static int[]? codebookOffsets;

    /// <summary>The packed codebook library: (bytes, offsets; entry i = offsets[i] .. offsets[i + 1]).</summary>
    internal static (byte[] Bytes, int[] Offsets) Library() { LoadCodebooks(); return (codebooks!, codebookOffsets!); }

    static void LoadCodebooks()
    {
        if (codebooks != null) return;
        string file = Path.Combine(AppContext.BaseDirectory, "Assets", "packed_codebooks_aoTuV_603.bin");
        byte[] b = File.ReadAllBytes(file);
        int offsetOffset = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(b.Length - 4));
        int count = (b.Length - offsetOffset) / 4;
        var offs = new int[count];
        for (int i = 0; i < count; i++) offs[i] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offsetOffset + 4 * i));
        codebookOffsets = offs;
        codebooks = b;
    }

    internal sealed class BitIn(byte[] data, int start)
    {
        int pos = start, bitsLeft;
        byte buffer;
        public long TotalBits;
        public int Position => pos;

        public bool Bit()
        {
            if (bitsLeft == 0)
            {
                if (pos >= data.Length) throw new InvalidDataException("out of bits");
                buffer = data[pos++]; bitsLeft = 8;
            }
            TotalBits++; bitsLeft--;
            return (buffer & (0x80 >> bitsLeft)) != 0;
        }

        public uint Read(int bits)
        {
            uint v = 0;
            for (int i = 0; i < bits; i++) if (Bit()) v |= 1u << i;
            return v;
        }
    }

    sealed class OggOut
    {
        const int HeaderBytes = 27, MaxSegments = 255, SegmentSize = 255;
        readonly MemoryStream os = new();
        readonly byte[] page = new byte[HeaderBytes + MaxSegments + SegmentSize * MaxSegments];
        byte bitBuffer; int bitsStored, payload;
        bool first = true, continued;
        uint seqno;
        public uint Granule;

        public void Bit(bool b)
        {
            if (b) bitBuffer |= (byte)(1 << bitsStored);
            if (++bitsStored == 8) FlushBits();
        }

        public void Write(uint v, int bits) { for (int i = 0; i < bits; i++) Bit((v & (1u << i)) != 0); }

        void FlushBits()
        {
            if (bitsStored == 0) return;
            if (payload == SegmentSize * MaxSegments) throw new InvalidDataException("ran out of space in an Ogg packet");
            page[HeaderBytes + MaxSegments + payload++] = bitBuffer;
            bitsStored = 0; bitBuffer = 0;
        }

        public void FlushPage(bool nextContinued = false, bool last = false)
        {
            if (payload != SegmentSize * MaxSegments) FlushBits();
            if (payload == 0) return;
            int segments = (payload + SegmentSize) / SegmentSize;
            if (segments == MaxSegments + 1) segments = MaxSegments;
            Array.Copy(page, HeaderBytes + MaxSegments, page, HeaderBytes + segments, payload);
            page[0] = (byte)'O'; page[1] = (byte)'g'; page[2] = (byte)'g'; page[3] = (byte)'S'; page[4] = 0;
            page[5] = (byte)((continued ? 1 : 0) | (first ? 2 : 0) | (last ? 4 : 0));
            var s = page.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s[6..], Granule);
            BinaryPrimitives.WriteUInt32LittleEndian(s[10..], Granule == 0xFFFFFFFF ? 0xFFFFFFFF : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(s[14..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(s[18..], seqno);
            BinaryPrimitives.WriteUInt32LittleEndian(s[22..], 0);
            page[26] = (byte)segments;
            for (int i = 0, left = payload; i < segments; i++)
            {
                if (left >= SegmentSize) { left -= SegmentSize; page[27 + i] = SegmentSize; }
                else page[27 + i] = (byte)left;
            }
            int total = HeaderBytes + segments + payload;
            BinaryPrimitives.WriteUInt32LittleEndian(s[22..], Crc(page, total));
            os.Write(page, 0, total);
            seqno++; first = false; continued = nextContinued; payload = 0;
        }

        public byte[] ToArray() { FlushPage(); return os.ToArray(); }
    }

    static int ILog(uint v) { int r = 0; while (v != 0) { r++; v >>= 1; } return r; }

    static uint QuantVals(uint entries, uint dimensions)
    {
        int bits = ILog(entries);
        int vals = (int)(entries >> (int)((bits - 1) * (dimensions - 1) / dimensions));
        while (true)
        {
            ulong acc = 1, acc1 = 1;
            for (int i = 0; i < dimensions; i++) { acc *= (ulong)vals; acc1 *= (ulong)(vals + 1); }
            if (acc <= entries && acc1 > entries) return (uint)vals;
            if (acc > entries) vals--; else vals++;
        }
    }

    /// <summary>Rebuilds one packed codebook (4-bit dimensions, 14-bit entries …) as a full Vorbis codebook.</summary>
    internal static void Rebuild(BitIn bis, long cbSize, Action<uint, int> bos)
    {
        uint dimensions = bis.Read(4), entries = bis.Read(14);
        bos(0x564342, 24); bos(dimensions, 16); bos(entries, 24);
        uint ordered = bis.Read(1); bos(ordered, 1);
        if (ordered != 0)
        {
            bos(bis.Read(5), 5);
            uint current = 0;
            while (current < entries)
            {
                int n = ILog(entries - current);
                uint number = bis.Read(n); bos(number, n);
                current += number;
            }
            if (current > entries) throw new InvalidDataException("current_entry out of range");
        }
        else
        {
            int lengthLength = (int)bis.Read(3);
            uint sparse = bis.Read(1);
            if (lengthLength == 0 || lengthLength > 5) throw new InvalidDataException("nonsense codeword length");
            bos(sparse, 1);
            for (uint i = 0; i < entries; i++)
            {
                bool present = true;
                if (sparse != 0) { uint p = bis.Read(1); bos(p, 1); present = p != 0; }
                if (present) bos(bis.Read(lengthLength), 5);
            }
        }
        uint lookup = bis.Read(1);
        bos(lookup, 4);
        if (lookup == 1) CopyLookup(bis, bos, entries, dimensions);
        if (cbSize != 0 && bis.TotalBits / 8 + 1 != cbSize) throw new InvalidDataException("codebook size mismatch");
    }

    static void CopyLookup(BitIn bis, Action<uint, int> bos, uint entries, uint dimensions)
    {
        bos(bis.Read(32), 32); bos(bis.Read(32), 32);
        uint valueLength = bis.Read(4); bos(valueLength, 4);
        bos(bis.Read(1), 1);
        uint q = QuantVals(entries, dimensions);
        for (uint i = 0; i < q; i++) bos(bis.Read((int)valueLength + 1), (int)valueLength + 1);
    }

    /// <summary>Copies a full (inline) Vorbis codebook.</summary>
    internal static void Copy(BitIn bis, Action<uint, int> bos)
    {
        uint id = bis.Read(24), dimensions = bis.Read(16), entries = bis.Read(24);
        if (id != 0x564342) throw new InvalidDataException("invalid codebook identifier");
        bos(id, 24); bos(dimensions, 16); bos(entries, 24);
        uint ordered = bis.Read(1); bos(ordered, 1);
        if (ordered != 0)
        {
            bos(bis.Read(5), 5);
            uint current = 0;
            while (current < entries)
            {
                int n = ILog(entries - current);
                uint number = bis.Read(n); bos(number, n);
                current += number;
            }
            if (current > entries) throw new InvalidDataException("current_entry out of range");
        }
        else
        {
            uint sparse = bis.Read(1); bos(sparse, 1);
            for (uint i = 0; i < entries; i++)
            {
                bool present = true;
                if (sparse != 0) { uint p = bis.Read(1); bos(p, 1); present = p != 0; }
                if (present) bos(bis.Read(5), 5);
            }
        }
        uint lookup = bis.Read(4); bos(lookup, 4);
        if (lookup == 1) CopyLookup(bis, bos, entries, dimensions);
        else if (lookup != 0) throw new InvalidDataException("invalid lookup type");
    }

    static void VorbisHeader(OggOut os, byte type)
    {
        os.Write(type, 8);
        foreach (char c in "vorbis") os.Write(c, 8);
    }

    /// <summary>
    /// The .wem as an Ogg Vorbis file, byte-identical to ww2ogg's (checked). Wwise's 2-byte packet headers carry no sample
    /// positions, so ww2ogg's pages all say 0, which NVorbis can't play (it hung); <paramref name="positions"/> writes each
    /// page's real end sample instead (from the packets' block sizes; the last page = the sample count), for playback.
    /// </summary>
    public static byte[] ToOgg(byte[] f, bool positions = false)
    {
        uint R32(long at) => BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan((int)at));
        ushort R16(long at) => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan((int)at));
        if (f.Length < 12 || f[0] != 'R' || f[1] != 'I' || f[2] != 'F' || f[3] != 'F') throw new InvalidDataException("not a Wwise RIFF sound (RIFX isn't supported)");
        long riffSize = R32(4) + 8L;
        if (riffSize > f.Length) throw new InvalidDataException("RIFF truncated");
        if (f[8] != 'W' || f[9] != 'A' || f[10] != 'V' || f[11] != 'E') throw new InvalidDataException("missing WAVE");

        long fmtOff = -1, smplOff = -1, vorbOff = -1, dataOff = -1, fmtSize = -1, vorbSize = -1, dataSize = -1;
        long chunk = 12;
        while (chunk < riffSize)
        {
            if (chunk + 8 > riffSize) throw new InvalidDataException("chunk header truncated");
            string type = System.Text.Encoding.ASCII.GetString(f, (int)chunk, 4);
            uint size = R32(chunk + 4);
            switch (type)
            {
                case "fmt ": fmtOff = chunk + 8; fmtSize = size; break;
                case "smpl": smplOff = chunk + 8; break;
                case "vorb": vorbOff = chunk + 8; vorbSize = size; break;
                case "data": dataOff = chunk + 8; dataSize = size; break;
            }
            chunk += 8 + size;
        }
        if (chunk > riffSize) throw new InvalidDataException("chunk truncated");
        if (fmtOff == -1 || dataOff == -1) throw new InvalidDataException("expected fmt, data chunks");
        if (vorbOff == -1 && fmtSize != 0x42) throw new InvalidDataException("expected 0x42 fmt if vorb missing");
        if (vorbOff != -1 && fmtSize is not (0x28 or 0x18 or 0x12)) throw new InvalidDataException("bad fmt size");
        if (vorbOff == -1) vorbOff = fmtOff + 0x18;

        if (R16(fmtOff) != 0xFFFF) throw new InvalidDataException("not Wwise Vorbis (codec " + R16(fmtOff).ToString("X4") + ")");
        uint channels = R16(fmtOff + 2), sampleRate = R32(fmtOff + 4), avgBytes = R32(fmtOff + 8);

        uint loopCount = 0, loopStart = 0, loopEnd = 0;
        if (smplOff != -1)
        {
            loopCount = R32(smplOff + 0x1C);
            if (loopCount != 1) throw new InvalidDataException("expected one loop");
            loopStart = R32(smplOff + 0x2C); loopEnd = R32(smplOff + 0x30);
        }
        if (vorbSize is not (-1 or 0x28 or 0x2A or 0x2C or 0x32 or 0x34)) throw new InvalidDataException("bad vorb size");
        uint sampleCount = R32(vorbOff);
        bool noGranule = false, modPackets = false;
        long p;
        if (vorbSize is -1 or 0x2A)
        {
            noGranule = true;
            uint modSignal = R32(vorbOff + 4);
            if (modSignal is not (0x4A or 0x4B or 0x69 or 0x70)) modPackets = true;
            p = vorbOff + 0x10;
        }
        else p = vorbOff + 0x18;
        uint setupOffset = R32(p), firstAudioOffset = R32(p + 4);
        bool triad = vorbSize is 0x28 or 0x2C;
        int bs0 = 0, bs1 = 0;
        if (!triad)
        {
            p = vorbSize is 0x32 or 0x34 ? vorbOff + 0x2C : vorbOff + 0x24;
            bs0 = f[p + 4]; bs1 = f[p + 5];
        }
        if (loopCount != 0)
        {
            loopEnd = loopEnd == 0 ? sampleCount : loopEnd + 1;
            if (loopStart >= sampleCount || loopEnd > sampleCount || loopStart > loopEnd) throw new InvalidDataException("loops out of range");
        }

        var os = new OggOut();
        bool[]? modeBlockflag = null;
        int modeBits = 0;
        if (triad) HeaderTriad(f, os, dataOff + setupOffset, dataOff + firstAudioOffset);
        else
        {
            // Identification
            VorbisHeader(os, 1);
            os.Write(0, 32); os.Write(channels, 8); os.Write(sampleRate, 32);
            os.Write(0, 32); os.Write(avgBytes * 8, 32); os.Write(0, 32);
            os.Write((uint)bs0, 4); os.Write((uint)bs1, 4); os.Write(1, 1);
            os.FlushPage();
            // Comment
            VorbisHeader(os, 3);
            const string vendor = "converted from Audiokinetic Wwise by ww2ogg 0.24";
            os.Write((uint)vendor.Length, 32);
            foreach (char c in vendor) os.Write(c, 8);
            if (loopCount == 0) os.Write(0, 32);
            else
            {
                os.Write(2, 32);
                foreach (string s in new[] { "LoopStart=" + loopStart, "LoopEnd=" + loopEnd })
                {
                    os.Write((uint)s.Length, 32);
                    foreach (char c in s) os.Write(c, 8);
                }
            }
            os.Write(1, 1);
            os.FlushPage();
            // Setup
            VorbisHeader(os, 5);
            long setupAt = dataOff + setupOffset;
            uint setupSize = R16(setupAt);
            long setupPayload = setupAt + (noGranule ? 2 : 6);
            if (!noGranule && R32(setupAt + 2) != 0) throw new InvalidDataException("setup packet granule != 0");
            var ss = new BitIn(f, (int)setupPayload);
            (modeBlockflag, modeBits) = Setup(ss, os, channels);
            os.FlushPage();
            if ((ss.TotalBits + 7) / 8 != setupSize) throw new InvalidDataException("didn't read exactly setup packet");
            if (setupPayload + setupSize != dataOff + firstAudioOffset) throw new InvalidDataException("first audio packet doesn't follow setup packet");
        }

        // Audio
        long offset = dataOff + firstAudioOffset, end = dataOff + dataSize;
        bool prevBlockflag = false;
        int headerSize = triad ? 8 : noGranule ? 2 : 6;
        bool track = positions && noGranule && modeBlockflag != null;
        long total = 0; int prevSize = 0;
        while (offset < end)
        {
            if (offset + headerSize > end) throw new InvalidDataException("page header truncated");
            uint size = triad ? R32(offset) : R16(offset);
            uint granule = triad ? R32(offset + 4) : noGranule ? 0 : R32(offset + 2);
            long payload = offset + headerSize, next = payload + size;
            os.Granule = granule == 0xFFFFFFFF ? 1 : granule;
            if (payload + size > f.Length) throw new InvalidDataException("file truncated");
            if (modPackets)
            {
                if (modeBlockflag == null) throw new InvalidDataException("didn't load mode_blockflag");
                os.Write(0, 1);
                var bs = new BitIn(f, (int)payload);
                uint mode = bs.Read(modeBits);
                os.Write(mode, modeBits);
                uint remainder = bs.Read(8 - modeBits);
                if (modeBlockflag[mode])
                {
                    bool nextBlockflag = false;
                    if (next + headerSize <= end && R16(next) > 0)
                        nextBlockflag = modeBlockflag[new BitIn(f, (int)(next + headerSize)).Read(modeBits)];
                    os.Write(prevBlockflag ? 1u : 0, 1);
                    os.Write(nextBlockflag ? 1u : 0, 1);
                }
                prevBlockflag = modeBlockflag[mode];
                os.Write(remainder, 8 - modeBits);
            }
            else os.Write(f[payload], 8);
            if (track && size > 0)
            {
                // The packet's mode: its first bits (after the packet-type bit in a standard packet).
                var mb = new BitIn(f, (int)payload);
                if (!modPackets) mb.Read(1);
                uint mode = mb.Read(modeBits);
                int cur = 1 << (mode < modeBlockflag!.Length && modeBlockflag[mode] ? bs1 : bs0);
                if (prevSize != 0) total += prevSize / 4 + cur / 4;
                prevSize = cur;
                os.Granule = (uint)(next == end ? Math.Min(total, sampleCount) : total);
            }
            for (long i = 1; i < size; i++) os.Write(f[payload + i], 8);
            offset = next;
            os.FlushPage(false, offset == end);
        }
        if (offset > end) throw new InvalidDataException("page truncated");
        LastBlockTotal = total;
        return os.ToArray();
    }

    /// <summary>The stripped setup header: codebook ids, then floors, residues, mappings and modes without their fixed fields.</summary>
    static (bool[] ModeBlockflag, int ModeBits) Setup(BitIn ss, OggOut os, uint channels)
    {
        LoadCodebooks();
        uint cbCountLess1 = ss.Read(8), cbCount = cbCountLess1 + 1;
        os.Write(cbCountLess1, 8);
        for (uint i = 0; i < cbCount; i++)
        {
            int id = (int)ss.Read(10);
            if (id < 0 || id >= codebookOffsets!.Length - 1) throw new InvalidDataException($"invalid codebook id {id}");
            int start = codebookOffsets[id], size = codebookOffsets[id + 1] - start;
            Rebuild(new BitIn(codebooks![..(start + size)], start), size, os.Write);
        }
        os.Write(0, 6); os.Write(0, 16);   // time domain placeholders

        uint floorCountLess1 = ss.Read(6), floorCount = floorCountLess1 + 1;
        os.Write(floorCountLess1, 6);
        for (uint i = 0; i < floorCount; i++)
        {
            os.Write(1, 16);
            uint partitions = ss.Read(5); os.Write(partitions, 5);
            var classList = new uint[partitions];
            uint maxClass = 0;
            for (int j = 0; j < partitions; j++)
            {
                uint c = ss.Read(4); os.Write(c, 4);
                classList[j] = c; maxClass = Math.Max(maxClass, c);
            }
            var dims = new uint[maxClass + 1];
            for (uint j = 0; j <= maxClass; j++)
            {
                uint d = ss.Read(3); os.Write(d, 3); dims[j] = d + 1;
                uint sub = ss.Read(2); os.Write(sub, 2);
                if (sub != 0)
                {
                    uint master = ss.Read(8); os.Write(master, 8);
                    if (master >= cbCount) throw new InvalidDataException("invalid floor1 masterbook");
                }
                for (int k = 0; k < (1 << (int)sub); k++)
                {
                    uint bookPlus1 = ss.Read(8); os.Write(bookPlus1, 8);
                    if ((int)bookPlus1 - 1 >= 0 && bookPlus1 - 1 >= cbCount) throw new InvalidDataException("invalid floor1 subclass book");
                }
            }
            os.Write(ss.Read(2), 2);
            int rangebits = (int)ss.Read(4); os.Write((uint)rangebits, 4);
            for (int j = 0; j < partitions; j++)
                for (uint k = 0; k < dims[classList[j]]; k++) os.Write(ss.Read(rangebits), rangebits);
        }

        uint residueCountLess1 = ss.Read(6), residueCount = residueCountLess1 + 1;
        os.Write(residueCountLess1, 6);
        for (uint i = 0; i < residueCount; i++)
        {
            uint type = ss.Read(2); os.Write(type, 16);
            if (type > 2) throw new InvalidDataException("invalid residue type");
            uint begin = ss.Read(24), rend = ss.Read(24), psize = ss.Read(24), classLess1 = ss.Read(6), classbook = ss.Read(8);
            os.Write(begin, 24); os.Write(rend, 24); os.Write(psize, 24); os.Write(classLess1, 6); os.Write(classbook, 8);
            if (classbook >= cbCount) throw new InvalidDataException("invalid residue classbook");
            uint classifications = classLess1 + 1;
            var cascade = new uint[classifications];
            for (int j = 0; j < classifications; j++)
            {
                uint high = 0, low = ss.Read(3); os.Write(low, 3);
                uint flag = ss.Read(1); os.Write(flag, 1);
                if (flag != 0) { high = ss.Read(5); os.Write(high, 5); }
                cascade[j] = high * 8 + low;
            }
            for (int j = 0; j < classifications; j++)
                for (int k = 0; k < 8; k++)
                    if ((cascade[j] & (1 << k)) != 0)
                    {
                        uint book = ss.Read(8); os.Write(book, 8);
                        if (book >= cbCount) throw new InvalidDataException("invalid residue book");
                    }
        }

        uint mappingCountLess1 = ss.Read(6), mappingCount = mappingCountLess1 + 1;
        os.Write(mappingCountLess1, 6);
        for (uint i = 0; i < mappingCount; i++)
        {
            os.Write(0, 16);
            uint submapsFlag = ss.Read(1); os.Write(submapsFlag, 1);
            uint submaps = 1;
            if (submapsFlag != 0) { uint s = ss.Read(4); os.Write(s, 4); submaps = s + 1; }
            uint polar = ss.Read(1); os.Write(polar, 1);
            if (polar != 0)
            {
                uint stepsLess1 = ss.Read(8); os.Write(stepsLess1, 8);
                int bits = ILog(channels - 1);
                for (uint j = 0; j <= stepsLess1; j++)
                {
                    uint magnitude = ss.Read(bits), angle = ss.Read(bits);
                    os.Write(magnitude, bits); os.Write(angle, bits);
                    if (angle == magnitude || magnitude >= channels || angle >= channels) throw new InvalidDataException("invalid coupling");
                }
            }
            uint reserved = ss.Read(2); os.Write(reserved, 2);
            if (reserved != 0) throw new InvalidDataException("mapping reserved field nonzero");
            if (submaps > 1)
                for (uint j = 0; j < channels; j++)
                {
                    uint mux = ss.Read(4); os.Write(mux, 4);
                    if (mux >= submaps) throw new InvalidDataException("mapping_mux >= submaps");
                }
            for (uint j = 0; j < submaps; j++)
            {
                os.Write(ss.Read(8), 8);
                uint floor = ss.Read(8); os.Write(floor, 8);
                if (floor >= floorCount) throw new InvalidDataException("invalid floor mapping");
                uint residue = ss.Read(8); os.Write(residue, 8);
                if (residue >= residueCount) throw new InvalidDataException("invalid residue mapping");
            }
        }

        uint modeCountLess1 = ss.Read(6), modeCount = modeCountLess1 + 1;
        os.Write(modeCountLess1, 6);
        var blockflag = new bool[modeCount];
        int modeBits = ILog(modeCount - 1);
        for (uint i = 0; i < modeCount; i++)
        {
            uint flag = ss.Read(1); os.Write(flag, 1);
            blockflag[i] = flag != 0;
            os.Write(0, 16); os.Write(0, 16);
            uint mapping = ss.Read(8); os.Write(mapping, 8);
            if (mapping >= mappingCount) throw new InvalidDataException("invalid mode mapping");
        }
        os.Write(1, 1);
        return (blockflag, modeBits);
    }

    /// <summary>The old form: full Vorbis header packets with 8-byte packet headers.</summary>
    static void HeaderTriad(byte[] f, OggOut os, long offset, long firstAudio)
    {
        uint R32(long at) => BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan((int)at));
        foreach (byte want in new byte[] { 1, 3 })
        {
            uint size = R32(offset);
            if (R32(offset + 4) != 0) throw new InvalidDataException("header packet granule != 0");
            if (f[offset + 8] != want) throw new InvalidDataException("wrong type for header packet");
            for (long i = 0; i < size; i++) os.Write(f[offset + 8 + i], 8);
            os.FlushPage();
            offset += 8 + size;
        }
        uint setupSize = R32(offset);
        if (R32(offset + 4) != 0) throw new InvalidDataException("setup packet granule != 0");
        var ss = new BitIn(f, (int)(offset + 8));
        uint c = ss.Read(8);
        if (c != 5) throw new InvalidDataException("wrong type for setup packet");
        os.Write(c, 8);
        for (int i = 0; i < 6; i++) os.Write(ss.Read(8), 8);
        uint cbLess1 = ss.Read(8); os.Write(cbLess1, 8);
        for (uint i = 0; i <= cbLess1; i++) Copy(ss, os.Write);
        while (ss.TotalBits < setupSize * 8L) os.Write(ss.Read(1), 1);
        os.FlushPage();
        if (offset + 8 + setupSize != firstAudio) throw new InvalidDataException("first audio packet doesn't follow setup packet");
    }

    static readonly uint[] CrcTable = MakeCrc();

    static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    static uint Crc(byte[] data, int n)
    {
        uint crc = 0;
        for (int i = 0; i < n; i++) crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ data[i]];
        return crc;
    }
}
