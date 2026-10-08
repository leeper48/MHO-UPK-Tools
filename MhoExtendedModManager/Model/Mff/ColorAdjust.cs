using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// A material's color map adjusted (Kurt, 2026-10-07, a user's request: filters on color maps, hue / saturation / brightness
/// and levels): levels first (input black and white points, gamma, output black and white), then hue, saturation and
/// brightness as the Powers tab turns them (PowerColor: hue in degrees, the others as factors). The color map's own file is
/// never changed: the preview, Build and Export FBX read an adjusted copy (<see cref="FileFor"/>), kept in the temp folder
/// by the source file, its date and the values. Alpha is kept.
/// </summary>
sealed class ColorAdjust
{
    public float Hue { get; set; }
    public float Saturation { get; set; } = 1;
    public float Brightness { get; set; } = 1;
    /// <summary>Levels, 0-255: input black and white points, gamma (1 = none; above lightens the middle), output range.</summary>
    public float InBlack { get; set; }
    public float InWhite { get; set; } = 255;
    public float Gamma { get; set; } = 1;
    public float OutBlack { get; set; }
    public float OutWhite { get; set; } = 255;

    public bool IsNone => Math.Abs(Hue) < 0.5f && Math.Abs(Saturation - 1) < 0.005f && Math.Abs(Brightness - 1) < 0.005f
        && InBlack < 0.5f && InWhite > 254.5f && Math.Abs(Gamma - 1) < 0.005f && OutBlack < 0.5f && OutWhite > 254.5f;

    public ColorAdjust Copy() => (ColorAdjust)MemberwiseClone();

    /// <summary>What it does, for the log and tooltips ("hue +40°, levels 10-240 γ1.2").</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Math.Abs(Hue) >= 0.5f) parts.Add($"hue {Hue:+0;-0}°");
        if (Math.Abs(Saturation - 1) >= 0.005f) parts.Add($"saturation {Saturation * 100:0} %");
        if (Math.Abs(Brightness - 1) >= 0.005f) parts.Add($"brightness {Brightness * 100:0} %");
        if (InBlack >= 0.5f || InWhite <= 254.5f || Math.Abs(Gamma - 1) >= 0.005f) parts.Add($"levels {InBlack:0}-{InWhite:0} gamma {Gamma:0.00}");
        if (OutBlack >= 0.5f || OutWhite <= 254.5f) parts.Add($"output {OutBlack:0}-{OutWhite:0}");
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>The pixels (BGRA, in place).</summary>
    public void Apply(byte[] bgra)
    {
        // a lookup table for the levels (per channel value), then the HSB change per pixel
        var lut = new float[256];
        float inB = Math.Clamp(InBlack, 0, 254), inW = Math.Clamp(InWhite, inB + 1, 255), g = Math.Clamp(Gamma, 0.1f, 10f);
        for (int v = 0; v < 256; v++)
        {
            float t = Math.Clamp((v - inB) / (inW - inB), 0, 1);
            t = MathF.Pow(t, 1 / g);
            lut[v] = (OutBlack + t * (OutWhite - OutBlack)) / 255f;
        }
        var hsb = new MhoExtendedModManager.PowerColor(Hue, Saturation, Brightness);
        bool shift = Math.Abs(Hue) >= 0.5f || Math.Abs(Saturation - 1) >= 0.005f || Math.Abs(Brightness - 1) >= 0.005f;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            var c = new Vector3(lut[bgra[i + 2]], lut[bgra[i + 1]], lut[bgra[i]]);
            if (shift) c = hsb.Apply(c);
            bgra[i + 2] = (byte)Math.Clamp(MathF.Round(c.X * 255), 0, 255);
            bgra[i + 1] = (byte)Math.Clamp(MathF.Round(c.Y * 255), 0, 255);
            bgra[i] = (byte)Math.Clamp(MathF.Round(c.Z * 255), 0, 255);
        }
    }

    /// <summary>The adjusted copy of <paramref name="colorFile"/> (made once per file, date and values); the file itself when
    /// there's nothing to adjust or it can't be read.</summary>
    public static string FileFor(string colorFile, ColorAdjust? a)
    {
        if (a == null || a.IsNone || !File.Exists(colorFile)) return colorFile;
        string key = string.Join("|", Path.GetFullPath(colorFile).ToLowerInvariant(), File.GetLastWriteTimeUtc(colorFile).Ticks,
            a.Hue, a.Saturation, a.Brightness, a.InBlack, a.InWhite, a.Gamma, a.OutBlack, a.OutWhite);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        string dir = Path.Combine(Path.GetTempPath(), "MHO_ExtMM_adjusted");
        // (named after the color map, so the Materials tab and an exported FBX still show which one it is)
        string outFile = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(colorFile)}_adjusted_{hash[..8]}.png");
        if (File.Exists(outFile)) return outFile;
        try
        {
            var (w, h, px) = ImagePixels.ReadBgra(colorFile);
            a.Apply(px);
            Directory.CreateDirectory(dir);
            string tmp = outFile + ".tmp.png";
            ImagePixels.Save(w, h, px, tmp);
            File.Move(tmp, outFile, true);
            return outFile;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException) { return colorFile; }
    }
}
