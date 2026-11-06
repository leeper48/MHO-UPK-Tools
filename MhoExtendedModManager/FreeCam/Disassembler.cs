using MhoPackageModifier;
namespace MhoExtendedModManager.FreeCam;

/// <summary>Readable text for bytecode (for checking only; names of the common natives from UE3's Object.uc).</summary>
sealed class Disassembler(Package pkg)
{
    static readonly Dictionary<int, string> Natives = new()
    {
        [0x81] = "!", [0x82] = "&&", [0x83] = "^^", [0x84] = "||", [0x8F] = "neg_i", [0x90] = "*i", [0x91] = "/i", [0x92] = "+i",
        [0x93] = "-i", [0x96] = "<i", [0x97] = ">i", [0x98] = "<=i", [0x99] = ">=i", [0x9A] = "==i", [0x9B] = "!=i", [0x9C] = "&i",
        [0x9E] = "|i", [0xA1] = "+=i", [0xA2] = "-=i", [0xA9] = "neg_f", [0xAB] = "*f", [0xAC] = "/f", [0xAE] = "+f", [0xAF] = "-f",
        [0xB0] = "<f", [0xB1] = ">f", [0xB2] = "<=f", [0xB3] = ">=f", [0xB4] = "==f", [0xB5] = "!=f", [0xB6] = "*=f", [0xB8] = "+=f",
        [0xB9] = "-=f", [0xBA] = "Abs", [0xBB] = "Sin", [0xBC] = "Cos", [0xBE] = "Atan", [0xC1] = "Sqrt", [0xD3] = "neg_v",
        [0xD4] = "*vf", [0xD7] = "+v", [0xD8] = "-v", [0xD9] = "==v", [0xDA] = "!=v", [0xDF] = "+=v", [0xE0] = "-=v", [0xE1] = "VSize",
        [0xE2] = "Normal", [0xF2] = "==b", [0xF3] = "!=b", [0xF4] = "FMin", [0xF5] = "FMax", [0xF6] = "FClamp", [0xF7] = "Lerp",
        [0xF9] = "Min", [0xFA] = "Max", [0xFB] = "Clamp", [0x72] = "==o", [0x77] = "!=o", [0xFE] = "==n", [0xFF] = "!=n",
    };

    static readonly Dictionary<byte, string> Casts = new()
    {
        [0x38] = "int2byte?", [0x39] = "int", [0x3A] = "bool2int?", [0x3F] = "int2float?", [0x41] = "int2bool?", [0x44] = "float?",
        [0x45] = "bool2float?", [0x46] = "float2bool?", [0x36] = "vector", [0x5A] = "vector2rot?",
    };

    string Obj(int i) => pkg.RefName(i);
    string Nm(OName n) => n.Number > 0 ? $"{pkg.Names[n.Index]}_{n.Number - 1}" : pkg.Names[n.Index];
    string E(Op o) => o is OExpr e ? Text(e.V) : "?";
    string Ps(Op o) => o is OParams ps ? string.Join(", ", ps.V.Select(Text)) : "?";

    public string Text(Node n)
    {
        var o = n.Ops;
        string J(int k) => o[k] is OJump { Target: { } t } ? t.Name : o[k] is OJump j ? $"{j.Raw:X4}" : "?";
        if (n.Tok >= 0x60)
        {
            int idx = n.Tok < 0x70 ? ((n.Tok - 0x60) << 8) | ((OByte)o[0]).V : n.Tok;
            return $"{(Natives.TryGetValue(idx, out var s) ? s : $"native{idx:X}")}({Ps(o[^1])})";
        }
        return n.Tok switch
        {
            0x00 or 0x01 or 0x02 or 0x48 or 0x29 => Obj(((OObj)o[0]).V),
            0x04 => $"return {E(o[0])}",
            0x05 => $"switch ({E(o[2])})",
            0x06 => $"goto {J(0)}",
            0x07 => $"if !({E(o[1])}) goto {J(0)}",
            0x0A => o.Count == 1 ? "default:" : $"case {E(o[1])}:  (next {J(0)})",
            0x0B => "nothing",
            0x0E => E(o[1]),
            0x0F or 0x14 => $"{E(o[0])} = {E(o[1])}",
            0x10 or 0x1A => $"{E(o[1])}[{E(o[0])}]",
            0x12 or 0x19 => $"{E(o[0])}.{E(o[4])}",
            0x13 => $"class<{Obj(((OObj)o[0]).V)}>({E(o[1])})",
            0x17 => "self",
            0x18 => E(o[1]),
            0x1B => $"{Nm((OName)o[0])}({Ps(o[1])})",
            0x1C => $"{Obj(((OObj)o[0]).V)}({Ps(o[1])})",
            0x1D => ((OI32)o[0]).V.ToString(),
            0x1E => ((OF32)o[0]).V.ToString("R"),
            0x1F => $"\"{System.Text.Encoding.Latin1.GetString(((OBytes)o[0]).V).TrimEnd('\0')}\"",
            0x20 => $"obj'{Obj(((OObj)o[0]).V)}'",
            0x21 => $"'{Nm((OName)o[0])}'",
            0x22 => "rot(" + string.Join(",", Enumerable.Range(0, 3).Select(k => BitConverter.ToInt32(((OBytes)o[0]).V, k * 4))) + ")",
            0x23 => "vect(" + string.Join(",", Enumerable.Range(0, 3).Select(k => BitConverter.ToSingle(((OBytes)o[0]).V, k * 4))) + ")",
            0x24 or 0x2C => ((OByte)o[0]).V.ToString(),
            0x25 => "0", 0x26 => "1", 0x27 => "true", 0x28 => "false", 0x2A => "none",
            0x2D => E(o[0]),
            0x2E => $"{Obj(((OObj)o[0]).V)}({E(o[1])})",
            0x35 => $"{E(o[4])}.{Obj(((OObj)o[0]).V)}",
            0x36 => $"{E(o[0])}.Length",
            0x37 => $"global.{Nm((OName)o[0])}({Ps(o[1])})",
            0x38 => $"{(Casts.TryGetValue(((OByte)o[0]).V, out var c) ? c : $"cast{((OByte)o[0]).V:X2}")}({E(o[1])})",
            0x3A => "return",
            0x41 => "<debug>",
            0x45 => $"({E(o[0])} ? {E(o[2])} : {E(o[4])})",
            0x49 => $"default {E(o[1])}",
            0x4A => "<empty>",
            0x53 => "<end>",
            _ => $"tok{n.Tok:X2}(...)",
        };
    }
}
