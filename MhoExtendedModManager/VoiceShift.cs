using System.Numerics;

namespace MhoExtendedModManager;

/// <summary>
/// Pitch shifting for listening to voice lines (Kurt, 2026-10-02: a test whether female voices can pass as male and the
/// other way round, before anything goes into the game). Works on the 16-bit PCM WAV the Voice tab plays. Two ways:
/// <list type="bullet">
/// <item>Tape: resampled, so pitch and speed change together (what Wwise's own Pitch property would do in the game).</item>
/// <item>Natural: a phase vocoder stretches the time by the pitch ratio, then a resample brings the length back, so only the
/// pitch moves; the voice's spectral envelope (its formants, the size of the throat you hear) is held in place by a cepstral
/// envelope correction, and moved separately by <c>formant</c> semitones.</item>
/// </list>
/// </summary>
static class VoiceShift
{
    public enum Mode { Tape, Natural }

    /// <summary>The WAV with its pitch moved by <paramref name="semitones"/> (and, Natural, its formants by
    /// <paramref name="formant"/> semitones). The same WAV back when nothing changes.</summary>
    /// <param name="warmth">Natural: dB on the low end (below about 250 Hz, fading out by 800 Hz) at the final pitch, to give a
    /// raised voice its body back (or take some away).</param>
    public static byte[] Shift(byte[] wav, float semitones, float formant, Mode mode, float warmth = 0)
    {
        if (Math.Abs(semitones) < 0.01f && (mode == Mode.Tape || Math.Abs(formant) < 0.01f && Math.Abs(warmth) < 0.01f)) return wav;
        var (chans, rate) = Read(wav);
        float r = MathF.Pow(2, semitones / 12f), f = MathF.Pow(2, formant / 12f);
        var outCh = chans.Select(c => mode == Mode.Tape ? Resample(c, r) : Natural(c, r, f, rate, warmth)).ToArray();
        return Write(outCh, rate);
    }

    /// <summary>
    /// A 16-bit PCM WAV as a Wwise PCM .wem, laid out as the game's own (SFX_IronMan_INT has one): RIFF/WAVE, a 24-byte fmt
    /// (WAVE_FORMAT_EXTENSIBLE 0xFFFE, cbSize 6, then 0 valid bits and Wwise's channel config: channel count | type 1 &lt;&lt; 8 |
    /// mask &lt;&lt; 12; mono 0x4101 = centre), a 4-byte JUNK, data.
    /// </summary>
    public static byte[] PcmWem(byte[] wav)
    {
        var (c, rate) = Read(wav);
        int ch = c.Length, frames = c.Min(x => x.Length);
        uint config = (uint)ch | 1u << 8 | (ch == 1 ? 0x4u : 0x3u) << 12;   // mono: centre; stereo: left + right
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        int dataLen = frames * ch * 2;
        w.Write("RIFF"u8); w.Write(4 + (8 + 24) + (8 + 4) + (8 + dataLen)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(24); w.Write((ushort)0xFFFE); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
        w.Write((short)6); w.Write((short)0); w.Write(config);
        w.Write("JUNK"u8); w.Write(4); w.Write(0);
        w.Write("data"u8); w.Write(dataLen);
        for (int i = 0; i < frames; i++)
            for (int k = 0; k < ch; k++) w.Write((short)Math.Clamp((int)MathF.Round(c[k][i] * 32767f), short.MinValue, short.MaxValue));
        return ms.ToArray();
    }

    // ---- WAV (16-bit PCM, as VoiceAudio.ToWav writes it)

    static (float[][] Channels, int Rate) Read(byte[] wav)
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

    static byte[] Write(float[][] c, int rate)
    {
        int ch = c.Length, frames = c.Min(x => x.Length);
        // Keep it from clipping (a shift can raise peaks): scale down only if needed.
        float peak = c.Max(x => x.Take(frames).Select(MathF.Abs).DefaultIfEmpty(0).Max());
        float g = peak > 0.98f ? 0.98f / peak : 1;
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + frames * ch * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * ch * 2);
        for (int i = 0; i < frames; i++)
            for (int k = 0; k < ch; k++) w.Write((short)Math.Clamp((int)MathF.Round(c[k][i] * g * 32767f), short.MinValue, short.MaxValue));
        return ms.ToArray();
    }

    // ---- Tape: read the samples at <paramref name="r"/> times the speed

    const int SincHalf = 16;

    /// <summary>
    /// Resampled to 1/<paramref name="r"/> the length: a windowed-sinc interpolator whose cut-off follows the new rate, so
    /// reading faster (pitch up) first filters out what the new rate can't hold. (The first version interpolated without a
    /// filter: raising a voice folded its highs back down as a metallic, tinny edge; Kurt 2026-10-02.)
    /// </summary>
    static float[] Resample(float[] x, float r)
    {
        int n = Math.Max(1, (int)(x.Length / r));
        var y = new float[n];
        float cut = Math.Min(1f, 1f / r);                // the pass band, as a fraction of the input's Nyquist
        int half = (int)MathF.Ceiling(SincHalf / cut);   // a wider kernel when the cut-off is lower
        System.Threading.Tasks.Parallel.For(0, n, i =>
        {
            double t = i * (double)r; int c0 = (int)Math.Floor(t);
            double sum = 0, wsum = 0;
            for (int j = c0 - half + 1; j <= c0 + half; j++)
            {
                double d = t - j, xx = d * cut;
                double sinc = Math.Abs(xx) < 1e-9 ? 1 : Math.Sin(Math.PI * xx) / (Math.PI * xx);
                double win = Math.Abs(d) >= half ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * d / half);
                double w = sinc * win;
                wsum += w;
                if (j >= 0 && j < x.Length) sum += x[j] * w;
            }
            y[i] = (float)(wsum > 1e-9 ? sum / wsum : 0);
        });
        return y;
    }

    // ---- Natural: phase-vocoder time stretch by r with the envelope corrected, then resampled by r

    const int N = 2048, Hop = N / 4, Lifter = 40;

    static float[] Natural(float[] x, float r, float f, int rate, float warmth = 0)
    {
        int hs = Hop, ha = Math.Max(1, (int)MathF.Round(hs / r));   // analysis hop: synthesis hop / r, so the output is r times as long
        float rEff = hs / (float)ha;
        var win = new float[N];
        for (int i = 0; i < N; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / N);
        int frames = Math.Max(1, (x.Length + ha - 1) / ha + 1);
        var y = new float[frames * hs + N];
        var norm = new float[y.Length];
        var lastPhase = new float[N / 2 + 1]; var sumPhase = new float[N / 2 + 1];
        var buf = new Complex[N];
        var mag = new float[N / 2 + 1]; var env = new float[N / 2 + 1];
        var aPhase = new float[N / 2 + 1]; var peakOf = new int[N / 2 + 1];
        // Warmth: a gain per bin at its final frequency (bin k ends at k·r after the resample).
        var gain = new float[N / 2 + 1];
        for (int k = 0; k <= N / 2; k++)
        {
            float hz = k * r * rate / N, t = Math.Clamp((hz - 250) / 550f, 0, 1);   // full below 250 Hz, none above 800
            gain[k] = MathF.Pow(10, warmth * (1 - t) / 20);
        }
        // Where the envelope is read for each output bin: the stretch keeps bins, the resample then multiplies frequencies by
        // r, so bin k ends up at k·r. To have the envelope end up at E(ν / f) there, read E at k·r / f now.
        float warp = rEff / f;
        bool first = true;
        for (int fr = 0; fr < frames; fr++)
        {
            int at = fr * ha - N / 2;
            for (int i = 0; i < N; i++) { int j = at + i; buf[i] = new Complex(j >= 0 && j < x.Length ? x[j] * win[i] : 0, 0); }
            Fft(buf, false);
            for (int k = 0; k <= N / 2; k++) mag[k] = (float)buf[k].Magnitude;
            Envelope(mag, env);
            // Phase locking (Laroche & Dolson's identity locking): the spectrum's peaks keep their own phase tracks, and the
            // bins around each peak keep their phase relation to it, so a harmonic's bins move as one (a plain phase vocoder
            // lets them drift apart: a hollow, metallic sound).
            for (int k = 0; k <= N / 2; k++) aPhase[k] = (float)buf[k].Phase;
            var peaks = new List<int>();
            for (int k = 2; k < N / 2 - 1; k++)
                if (mag[k] > mag[k - 1] && mag[k] >= mag[k + 1] && mag[k] > mag[k - 2] && mag[k] >= mag[k + 2]) peaks.Add(k);
            if (peaks.Count == 0) peaks.Add(Array.IndexOf(mag, mag.Max()));
            for (int pi = 0, k = 0; k <= N / 2; k++)
            {
                while (pi + 1 < peaks.Count && Math.Abs(peaks[pi + 1] - k) < Math.Abs(peaks[pi] - k)) pi++;
                peakOf[k] = peaks[pi];
            }
            var synth = new float[N / 2 + 1];
            for (int k = 0; k <= N / 2; k++)
            {
                float ph = aPhase[k];
                // Phase vocoder: the bin's true frequency from the phase advance over the analysis hop, carried over the
                // synthesis hop.
                float omega = 2 * MathF.PI * k * ha / N;
                float dp = first ? 0 : ph - lastPhase[k] - omega;
                dp -= 2 * MathF.PI * MathF.Round(dp / (2 * MathF.PI));
                lastPhase[k] = ph;
                float freq = (omega + dp) / ha;   // radians per sample
                synth[k] = first ? ph : sumPhase[k] + freq * hs;   // (used for the peaks)
            }
            for (int k = 0; k <= N / 2; k++)
            {
                int pk = peakOf[k];
                float phase = k == pk ? synth[k] : synth[pk] + (aPhase[k] - aPhase[pk]);
                sumPhase[k] = phase;
                // The envelope moved: the excitation (magnitude over envelope) on the envelope read at k·warp.
                float src = k * warp, e2 = src >= N / 2 ? env[N / 2] * 1e-3f : Lerp(env, src);
                float m = env[k] > 1e-9f ? mag[k] / env[k] * e2 * gain[k] : 0;
                buf[k] = Complex.FromPolarCoordinates(m, phase);
                if (k > 0 && k < N / 2) buf[N - k] = Complex.Conjugate(buf[k]);
            }
            first = false;
            Fft(buf, true);
            int o = fr * hs;
            for (int i = 0; i < N; i++) { y[o + i] += (float)buf[i].Real * win[i]; norm[o + i] += win[i] * win[i]; }
        }
        for (int i = 0; i < y.Length; i++) y[i] = norm[i] > 1e-6f ? y[i] / norm[i] : 0;
        // Drop the half window of lead-in, then back to the original length (and up by r in pitch).
        var stretched = y.Skip(N / 2).Take((int)(x.Length * rEff)).ToArray();
        var outp = Resample(stretched, rEff);
        Array.Resize(ref outp, x.Length);
        return outp;
    }

    static float Lerp(float[] a, float t) { int i = (int)t; float u = t - i; return i + 1 < a.Length ? a[i] * (1 - u) + a[i + 1] * u : a[^1]; }

    /// <summary>The spectral envelope: the log magnitude's low quefrencies (cepstral smoothing).</summary>
    static void Envelope(float[] mag, float[] env)
    {
        var c = new Complex[N];
        for (int k = 0; k <= N / 2; k++) { double l = Math.Log(mag[k] + 1e-9); c[k] = l; if (k > 0 && k < N / 2) c[N - k] = l; }
        Fft(c, true);
        for (int q = Lifter; q <= N - Lifter; q++) c[q] = 0;
        Fft(c, false);
        for (int k = 0; k <= N / 2; k++) env[k] = (float)Math.Exp(c[k].Real);
    }

    /// <summary>In-place radix-2 FFT; the inverse is scaled by 1/N.</summary>
    static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    var u = a[i + j]; var v = a[i + j + len / 2] * w;
                    a[i + j] = u + v; a[i + j + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) a[i] /= n;
    }

    /// <summary>The main frequency of a mono signal's middle (autocorrelation peak between 60 and 600 Hz), for tests.</summary>
    public static float Pitch(float[] x, int rate)
    {
        int start = x.Length / 4, len = Math.Min(4096, x.Length / 2);
        int lo = rate / 600, hi = rate / 60;
        var r = new double[hi + 2];
        for (int lag = lo; lag <= hi + 1; lag++)
        {
            double s = 0;
            for (int i = 0; i < len && start + i + lag < x.Length; i++) s += x[start + i] * x[start + i + lag];
            r[lag] = s;
        }
        double best = r.Skip(lo).Max();
        // The shortest period that correlates nearly as well as the best (a multiple of the period scores the same).
        for (int lag = lo + 1; lag <= hi; lag++)
            if (r[lag] >= 0.9 * best && r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1]) return rate / (float)lag;
        return rate / (float)Array.IndexOf(r, best);
    }

    /// <summary>Self-test: a 220 Hz tone with harmonics shifted -12 / +7 semitones in both modes; reports the pitch found and
    /// the length kept (Natural) or changed (Tape).</summary>
    public static List<string> Test()
    {
        int rate = 44100; var x = new float[rate * 2];
        for (int i = 0; i < x.Length; i++) { float t = i / (float)rate; x[i] = 0.3f * MathF.Sin(2 * MathF.PI * 220 * t) + 0.15f * MathF.Sin(2 * MathF.PI * 440 * t) + 0.08f * MathF.Sin(2 * MathF.PI * 660 * t); }
        var res = new List<string>();
        foreach (float st in new[] { -12f, 7f })
            foreach (var mode in new[] { Mode.Tape, Mode.Natural })
            {
                var w = Write([x], rate);
                var (o, _) = Read(Shift(w, st, 0, mode));
                float want = 220 * MathF.Pow(2, st / 12), got = Pitch(o[0], rate);
                bool ok = Math.Abs(got / want - 1) < 0.04f && (mode == Mode.Tape ? Math.Abs(o[0].Length - x.Length / MathF.Pow(2, st / 12)) < 4 : o[0].Length == x.Length);
                res.Add($"{(ok ? "ok  " : "FAIL")} {mode,-7} {st,+4:+0;-0} st: pitch {got:0.0} Hz (want {want:0.0}), length {o[0].Length / (float)rate:0.00} s");
            }
        return res;
    }
}
