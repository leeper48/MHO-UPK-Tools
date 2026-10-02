using NVorbis;
using OggVorbisEncoder;

namespace MhoExtendedModManager;

/// <summary>
/// Vorbis encoding (Kurt, 2026-10-02: shifted voice lines that MHModManager 1.0.1 can apply too, which only knows Vorbis
/// sounds). OggVorbisEncoder (Steve Lillis, MIT: a managed port of libvorbis's encoder) does the encoding; this wraps it for
/// the 16-bit PCM WAVs the voice tools use, and keeps the raw packets for the Wwise .wem converter (phase 2).
/// </summary>
static class VorbisEncode
{
    /// <summary>An encoded stream: the three header packets (identification, comments, setup), the audio packets with their
    /// end positions (granule, in samples), and the same as a standard Ogg file.</summary>
    public sealed record Encoded(byte[] Info, byte[] Comments, byte[] Setup, List<(byte[] Data, long Granule)> Audio, byte[] Ogg, int Channels, int Rate, long Samples);

    /// <summary>Silence put before the audio (see Encode).</summary>
    public const int Lead = 512;

    /// <param name="quality">libvorbis's VBR quality, -0.1 to 1 (0.4 ≈ 128 kbit/s stereo).</param>
    public static Encoded Encode(byte[] wav, float quality = 0.5f)
    {
        var (raw, rate) = WavPcm.Read(wav);
        // The first half-window a decoder makes is dropped (the encoder's start isn't marked in the stream positions): 512
        // samples of silence go first, so that's what's lost (measured: without it the decoded line lacked its first 512).
        var chans = raw.Select(c => new float[Lead].Concat(c).ToArray()).ToArray();
        int ch = chans.Length, frames = chans.Min(c => c.Length);
        var info = VorbisInfo.InitVariableBitRate(ch, rate, quality);
        var ogg = new OggStream(new Random(1).Next());
        var infoP = HeaderPacketBuilder.BuildInfoPacket(info);
        var comP = HeaderPacketBuilder.BuildCommentsPacket(new Comments());
        var setupP = HeaderPacketBuilder.BuildBooksPacket(info);
        ogg.PacketIn(infoP); ogg.PacketIn(comP); ogg.PacketIn(setupP);
        var outMs = new MemoryStream();
        void Pages(bool force)
        {
            while (ogg.PageOut(out OggPage page, force)) { outMs.Write(page.Header); outMs.Write(page.Body); }
        }
        Pages(true);
        var state = ProcessingState.Create(info);
        var audio = new List<(byte[], long)>();
        const int block = 1024;
        var buf = Enumerable.Range(0, ch).Select(_ => new float[block]).ToArray();
        void Drain()
        {
            while (state.PacketOut(out OggPacket p))
            {
                audio.Add((p.PacketData, p.GranulePosition));
                ogg.PacketIn(p);
                Pages(false);
            }
        }
        for (int at = 0; at < frames; at += block)
        {
            int n = Math.Min(block, frames - at);
            for (int k = 0; k < ch; k++) Array.Copy(chans[k], at, buf[k], 0, n);
            state.WriteData(buf, n, 0);
            Drain();
        }
        state.WriteEndOfStream();
        Drain();
        Pages(true);
        return new Encoded(infoP.PacketData, comP.PacketData, setupP.PacketData, audio, outMs.ToArray(), ch, rate, frames);
    }

    /// <summary>An Ogg Vorbis file decoded (NVorbis) back to channels of samples.</summary>
    public static (float[][] Channels, int Rate) Decode(byte[] ogg)
    {
        using var r = new VorbisReader(new MemoryStream(ogg), true);
        int ch = r.Channels; var all = new List<float>();
        var buf = new float[4096 * ch]; int n;
        while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
        int frames = all.Count / ch;
        var c = Enumerable.Range(0, ch).Select(_ => new float[frames]).ToArray();
        for (int i = 0; i < frames; i++) for (int k = 0; k < ch; k++) c[k][i] = all[i * ch + k];
        return (c, r.SampleRate);
    }

    /// <summary>Signal-to-noise ratio (dB) of <paramref name="b"/> against <paramref name="a"/>, over their common length,
    /// with the best alignment within ±2048 samples (an encoder may shift the start).</summary>
    public static (double Snr, int Shift) Snr(float[] a, float[] b)
    {
        int best = 0; double bestErr = double.MaxValue, sig = 0;
        int n = Math.Min(a.Length, b.Length) - 4096;
        if (n <= 0) return (0, 0);
        for (int i = 2048; i < n; i++) sig += a[i] * (double)a[i];
        for (int s = -2048; s <= 2048; s += 1)
        {
            double err = 0;
            for (int i = 2048; i < n; i += 4) { double d = a[i] - b[i + s]; err += d * d; }
            if (err < bestErr) { bestErr = err; best = s; }
        }
        double e2 = 0;
        for (int i = 2048; i < n; i++) { double d = a[i] - b[i + best]; e2 += d * d; }
        return (10 * Math.Log10(sig / Math.Max(e2, 1e-12)), best);
    }
}

/// <summary>16-bit PCM WAV in and out (the voice tools' format).</summary>
static class WavPcm
{
    public static (float[][] Channels, int Rate) Read(byte[] wav)
    {
        int ch = BitConverter.ToInt16(wav, 22), rate = BitConverter.ToInt32(wav, 24);
        int p = 12, data = -1, len = 0;
        while (p + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, p, 4); int size = BitConverter.ToInt32(wav, p + 4);
            if (id == "data") { data = p + 8; len = Math.Min(size, wav.Length - data); break; }
            p += 8 + size + (size & 1);
        }
        if (data < 0 || ch < 1) throw new InvalidDataException("not a PCM WAV");
        int frames = len / 2 / ch;
        var c = Enumerable.Range(0, ch).Select(_ => new float[frames]).ToArray();
        for (int i = 0; i < frames; i++)
            for (int k = 0; k < ch; k++) c[k][i] = BitConverter.ToInt16(wav, data + (i * ch + k) * 2) / 32768f;
        return (c, rate);
    }
}
