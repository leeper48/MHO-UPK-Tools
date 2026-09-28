using System.Numerics;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// What a character material tells the preview's renderer (Kurt: closer to the game's look). Character materials are
/// instances of one base shader, chbasematerial_v2 (seen on Thor Age of Ultron): texture parameters diffusetex, normaltex
/// and specmult_specpow_reflectivity_emissive (a packed map; channel use by its name, checked by --material-probe), a rim
/// light (rimcolor × rimcolormult), and static switches naming the features in use (usenormalmap, usespec, useemissive,
/// userimlight, usediffuseinrim …). The instance chain is followed to its parents in the package; a child's value wins.
/// The base shader itself is compiled (its defaults are baked in), so a parameter no instance sets is unknown here.
/// </summary>
sealed class MaterialInfo
{
    public string Name = "", Parent = "";
    public readonly Dictionary<string, int> Textures = new(StringComparer.OrdinalIgnoreCase);        // parameter → Texture2D export index
    public readonly Dictionary<string, float> Scalars = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Vector4> Vectors = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, bool> Switches = new(StringComparer.OrdinalIgnoreCase);        // quality level 0 (high)

    public bool Switch(string name, bool fallback = false) => Switches.TryGetValue(name, out bool v) ? v : fallback;
    public float Scalar(string name, float fallback) => Scalars.TryGetValue(name, out float v) ? v : fallback;
    public bool Masked => Parent.Contains("masked", StringComparison.OrdinalIgnoreCase);
    public bool Translucent => Parent.Contains("translucent", StringComparison.OrdinalIgnoreCase);

    /// <summary>The texture parameter whose name contains one of the parts (first match), or -1.</summary>
    public int Texture(params string[] parts)
    {
        foreach (string part in parts)
            foreach (var (k, v) in Textures) if (k.Contains(part, StringComparison.OrdinalIgnoreCase)) return v;
        return -1;
    }
}

static class ModMaterials
{
    /// <summary>A material instance and its parents in the package (null when it isn't a material instance here).</summary>
    public static MaterialInfo? Read(Package pkg, int materialRef)
    {
        if (materialRef <= 0 || materialRef > pkg.Exports.Length) return null;
        var info = new MaterialInfo { Name = pkg.Exports[materialRef - 1].ObjectName };
        int reference = materialRef;
        for (int depth = 0; depth < 8 && reference != 0; depth++)
        {
            if (reference < 0) { info.Parent = pkg.RefName(reference); break; }
            var e = pkg.Exports[reference - 1];
            string cls = pkg.ClassOf(e);
            if (!cls.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) { info.Parent = e.ObjectName; break; }
            byte[] d = pkg.ReadExportBytes(e);
            int parent = 0;
            if (TagWalker.Walk(pkg, d, 4) is { } tags)
                foreach (var t in tags)
                {
                    if (t.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase) && t.Size == 4) parent = BitConverter.ToInt32(d, t.ValueAt);
                    else if (t.Name.Equals("ScalarParameterValues", StringComparison.OrdinalIgnoreCase)) Params(pkg, d, t, (n, at, size) => { if (size == 4) info.Scalars.TryAdd(n, BitConverter.ToSingle(d, at)); });
                    else if (t.Name.Equals("VectorParameterValues", StringComparison.OrdinalIgnoreCase)) Params(pkg, d, t, (n, at, size) => { if (size == 16) info.Vectors.TryAdd(n, new Vector4(BitConverter.ToSingle(d, at), BitConverter.ToSingle(d, at + 4), BitConverter.ToSingle(d, at + 8), BitConverter.ToSingle(d, at + 12))); });
                    else if (t.Name.Equals("TextureParameterValues", StringComparison.OrdinalIgnoreCase))
                        Params(pkg, d, t, (n, at, size) =>
                        {
                            int r = size == 4 ? BitConverter.ToInt32(d, at) : 0;
                            if (r > 0 && r <= pkg.Exports.Length && pkg.ClassOf(pkg.Exports[r - 1]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) info.Textures.TryAdd(n, r - 1);
                        });
                }
            try
            {
                // "static0.switch[3]: useemissive = true (override)": quality level 0 is the high one.
                foreach (string s in ExportCopy.StaticSwitches(pkg, d))
                {
                    if (!s.StartsWith("static0.", StringComparison.OrdinalIgnoreCase)) continue;
                    int colon = s.IndexOf(": ", StringComparison.Ordinal), eq = s.LastIndexOf(" = ", StringComparison.Ordinal);
                    if (colon < 0 || eq < colon) continue;
                    info.Switches.TryAdd(s[(colon + 2)..eq].Trim(), s[(eq + 3)..].StartsWith("true", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
            reference = parent;
        }
        return info;
    }

    /// <summary>Each element of a parameter array: its ParameterName and where its ParameterValue is (offset, size).</summary>
    static void Params(Package pkg, byte[] d, TagWalker.Tag array, Action<string, int, int> each)
    {
        int count = BitConverter.ToInt32(d, array.ValueAt), p = array.ValueAt + 4;
        for (int i = 0; i < count && p < array.End; i++)
        {
            if (TagWalker.Walk(pkg, d, p) is not { } el) return;
            string name = ""; int at = -1, size = 0;
            foreach (var t in el)
            {
                if (t.Name.Equals("ParameterName", StringComparison.OrdinalIgnoreCase) && t.Size == 8) name = TagWalker.NameAt(pkg, d, t.ValueAt);
                if (t.Name.Equals("ParameterValue", StringComparison.OrdinalIgnoreCase)) { at = t.ValueAt; size = t.Size; }
            }
            if (name.Length > 0 && at >= 0) each(name, at, size);
            p = el.NoneAt + 8;
        }
    }
}
