namespace MhoExtendedModManager;

/// <summary>
/// Voice filters on top of Voice Shift (a user asked, 2026-10-16: reverb, distortion, lo-fi, chorus …): each 0 (off) to 1,
/// on a mono or stereo voice line at its own rate (the game's voice is 22,050 Hz mono). Order: distortion, lo-fi, robot,
/// chorus, echo, reverb (the time-based ones last, so they color everything before them). Reverb and echo add a tail (up to
/// about a second), so a line ends with its echo instead of cutting it off. The peak stays near the original line's, so a
/// filter can't make a line much louder. Own code (textbook forms: Schroeder / Freeverb-style comb and all-pass network,
/// tanh soft clipping, RBJ biquads, modulated delay lines, ring modulation).
/// </summary>
static class VoiceEffects
{
    public static bool Any(VoiceShiftEntry e) => e.Reverb > 0.001f || e.Echo > 0.001f || e.Distortion > 0.001f || e.LoFi > 0.001f || e.Chorus > 0.001f || e.Robot > 0.001f;

    /// <summary>Samples after one more second at most of tail (reverb, echo), so the caller can size the output.</summary>
    public static int Tail(VoiceShiftEntry e, int rate) =>
        (int)(rate * Math.Max(e.Reverb > 0.001f ? 0.4f + 1.0f * e.Reverb : 0, e.Echo > 0.001f ? 0.25f * (2 + 4 * e.Echo) : 0));

    public static float[] Apply(float[] input, int rate, VoiceShiftEntry e)
    {
        if (!Any(e)) return input;
        float inPeak = input.Length == 0 ? 0 : input.Max(MathF.Abs);
        var x = new float[input.Length + Tail(e, rate)];
        Array.Copy(input, x, input.Length);
        if (e.Distortion > 0.001f) Distort(x, e.Distortion);
        if (e.LoFi > 0.001f) LoFi(x, rate, e.LoFi);
        if (e.Robot > 0.001f) Robot(x, rate, e.Robot);
        if (e.Chorus > 0.001f) x = Chorus(x, rate, e.Chorus);
        if (e.Echo > 0.001f) x = Echo(x, rate, e.Echo);
        if (e.Reverb > 0.001f) x = Reverb(x, rate, e.Reverb);
        // the peak near the line's own (at most 20 % above it, never clipping)
        float outPeak = x.Length == 0 ? 0 : x.Max(MathF.Abs);
        float limit = Math.Min(0.98f, Math.Max(inPeak * 1.2f, 0.05f));
        if (outPeak > limit) { float g = limit / outPeak; for (int i = 0; i < x.Length; i++) x[i] *= g; }
        // the tail trimmed where it has died away
        int end = x.Length;
        while (end > input.Length && MathF.Abs(x[end - 1]) < 1e-4f) end--;
        // a tail still ringing at the end fades out over its last 0.2 s (a strong echo would stop abruptly)
        int fade = Math.Min(end - input.Length, rate / 5);
        for (int i = 0; i < fade; i++) x[end - 1 - i] *= i / (float)fade;
        return end == x.Length ? x : x[..end];
    }

    /// <summary>Soft clipping: drive up to ×16, tanh, level kept.</summary>
    static void Distort(float[] x, float a)
    {
        float d = 1 + a * 15, n = MathF.Tanh(d), mix = Math.Min(1f, a * 2);
        for (int i = 0; i < x.Length; i++) x[i] = x[i] + (MathF.Tanh(d * x[i]) / n - x[i]) * mix;
    }

    /// <summary>Radio / lo-fi: a 300–3,400 Hz band (blended in), fewer bits (down to 5) and a lower sample rate (held samples).</summary>
    static void LoFi(float[] x, int rate, float a)
    {
        var hp = Biquad.HighPass(rate, 300 + 300 * a, 0.707f); var lp = Biquad.LowPass(rate, Math.Max(2400f, 3400 - 800 * a), 0.707f);
        float levels = MathF.Pow(2, 16 - a * 11) / 2;
        int hold = 1 + (int)MathF.Round(a * 3);
        float held = 0;
        for (int i = 0; i < x.Length; i++)
        {
            float band = lp.Run(hp.Run(x[i]));
            float v = x[i] + (band * 1.6f - x[i]) * a;
            if (i % hold == 0) held = MathF.Round(v * levels) / levels;
            x[i] = held;
        }
    }

    /// <summary>Robot: ring modulation at 50–90 Hz, blended in.</summary>
    static void Robot(float[] x, int rate, float a)
    {
        float f = 50 + 40 * a, w = 2 * MathF.PI * f / rate;
        for (int i = 0; i < x.Length; i++) x[i] = x[i] * (1 - a) + x[i] * MathF.Sin(w * i) * a * 1.4f;
    }

    /// <summary>Chorus: two copies delayed 18–30 ms, wavering at 0.6 and 0.9 Hz, mixed in.</summary>
    static float[] Chorus(float[] x, int rate, float a)
    {
        var y = new float[x.Length];
        float wet = a * 0.55f, depth = (0.002f + 0.006f * a) * rate;
        float Tap(int i, float delay)
        {
            float p = i - delay; if (p < 0) return 0;
            int k = (int)p; float fr = p - k;
            return k + 1 < x.Length ? x[k] * (1 - fr) + x[k + 1] * fr : x[k];
        }
        for (int i = 0; i < x.Length; i++)
        {
            float t = i / (float)rate;
            float d1 = 0.018f * rate + depth * (0.5f + 0.5f * MathF.Sin(2 * MathF.PI * 0.6f * t));
            float d2 = 0.024f * rate + depth * (0.5f + 0.5f * MathF.Sin(2 * MathF.PI * 0.9f * t + 1.3f));
            y[i] = x[i] + wet * 0.5f * (Tap(i, d1) + Tap(i, d2));
        }
        return y;
    }

    /// <summary>Echo: repeats every 250 ms, each 30–60 % of the last.</summary>
    static float[] Echo(float[] x, int rate, float a)
    {
        int d = (int)(0.25f * rate);
        float fb = 0.3f + 0.3f * a, wet = 0.35f + 0.4f * a;
        var y = new float[x.Length];
        var line = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            float back = i >= d ? line[i - d] : 0;
            line[i] = x[i] + back * fb;
            y[i] = x[i] + back * wet;
        }
        return y;
    }

    /// <summary>Reverb: four damped comb filters into two all-pass filters (Freeverb's delays, scaled to the rate), the room
    /// growing with the amount.</summary>
    static float[] Reverb(float[] x, int rate, float a)
    {
        float s = rate / 44100f, room = 0.70f + 0.26f * a, damp = 0.35f, wet = 0.2f + 0.45f * a;
        int[] combs = [.. new[] { 1116, 1188, 1277, 1356 }.Select(n => Math.Max(1, (int)(n * s * (1 + a))))];
        int[] alls = [.. new[] { 556, 441 }.Select(n => Math.Max(1, (int)(n * s)))];
        var sum = new float[x.Length];
        foreach (int len in combs)
        {
            var buf = new float[len]; int p = 0; float store = 0;
            for (int i = 0; i < x.Length; i++)
            {
                float o = buf[p];
                store = o * (1 - damp) + store * damp;
                buf[p] = x[i] + store * room;
                p = (p + 1) % len;
                sum[i] += o * 0.25f;
            }
        }
        foreach (int len in alls)
        {
            var buf = new float[len]; int p = 0;
            for (int i = 0; i < x.Length; i++)
            {
                float b = buf[p], o = -sum[i] + b;
                buf[p] = sum[i] + b * 0.5f;
                p = (p + 1) % len;
                sum[i] = o;
            }
        }
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] + sum[i] * wet;
        return y;
    }

    /// <summary>An RBJ-cookbook biquad.</summary>
    sealed class Biquad(float b0, float b1, float b2, float a1, float a2)
    {
        float x1, x2, y1, y2;
        public float Run(float x) { float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2; x2 = x1; x1 = x; y2 = y1; y1 = y; return y; }

        public static Biquad LowPass(int rate, float f, float q)
        {
            float w = 2 * MathF.PI * f / rate, c = MathF.Cos(w), al = MathF.Sin(w) / (2 * q), a0 = 1 + al;
            return new((1 - c) / 2 / a0, (1 - c) / a0, (1 - c) / 2 / a0, -2 * c / a0, (1 - al) / a0);
        }

        public static Biquad HighPass(int rate, float f, float q)
        {
            float w = 2 * MathF.PI * f / rate, c = MathF.Cos(w), al = MathF.Sin(w) / (2 * q), a0 = 1 + al;
            return new((1 + c) / 2 / a0, -(1 + c) / a0, (1 + c) / 2 / a0, -2 * c / a0, (1 - al) / a0);
        }
    }
}
