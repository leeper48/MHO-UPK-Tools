using MhoPackageModifier;
using System.Buffers.Binary;
using System.Text;

namespace MhoExtendedModManager.FreeCam;

/// <summary>
/// UnrealScript bytecode of this client (UE3 v868, 64-bit): parse, edit and write.
/// On disk an object reference is 4 bytes and a name 8; in memory (where jump targets and skip sizes count) an object
/// reference is 8. Every function in MarvelGame.upk must parse, give the header's memory size and write back byte for
/// byte (<c>--roundtrip</c>) before anything built with this is trusted.
/// </summary>
public sealed class Node
{
    public byte Tok;
    public readonly List<Op> Ops = [];
    /// <summary>Label of a statement (jump target); set on top-level statements only.</summary>
    public JumpLabel? Label;
    public Node(byte tok) { Tok = tok; }
    public override string ToString() => $"tok {Tok:X2}";
}

public sealed class JumpLabel
{
    public int Mem = -1;
    public readonly string Name;
    public JumpLabel(string name) { Name = name; }
}

public abstract record Op;
public sealed record OByte(byte V) : Op;
public sealed record OU16(ushort V) : Op;                 // raw 16-bit (assert line)
public sealed record OJump(JumpLabel? Target, ushort Raw) : Op; // jump target; Raw 0xFFFF = "default" case
public sealed record OSkip(int From, int Count, int Extra, int Raw = -1) : Op; // memory size of Count operands starting From after it, plus Extra
public sealed record OObj(int V) : Op;
public sealed record OName(int Index, int Number) : Op;
public sealed record OI32(int V) : Op;
public sealed record OF32(float V) : Op;
public sealed record OBytes(byte[] V) : Op;                // fixed raw bytes, same size in memory (strings, vectors, debug info)
public sealed record OExpr(Node V) : Op;
public sealed record OParams(List<Node> V) : Op;          // expressions, then EndFunctionParms (0x16)
public sealed record OLabels(List<(int Name, int Number, int Offset)> V) : Op;

public static class Bytecode
{
    public const byte EndFunctionParms = 0x16, EndOfScript = 0x53;

    // Operand layout per token. E expr, O object, N name, B byte, W raw u16, J jump, S skip (covers the rest, see
    // SkipCount), I int, F float, T zero-terminated string, U zero-terminated UTF-16 string, R 12 raw bytes,
    // G debug info (13 raw bytes), P parameters to 0x16, L label table, C case.
    static readonly Dictionary<byte, string> Spec = new()
    {
        [0x00] = "O", [0x01] = "O", [0x02] = "O", [0x48] = "O", [0x29] = "O",
        [0x04] = "E", [0x05] = "OBE", [0x06] = "J", [0x07] = "JE", [0x08] = "", [0x09] = "WBE", [0x0A] = "C",
        [0x0B] = "", [0x0C] = "L", [0x0D] = "E", [0x0E] = "OE", [0x0F] = "EE", [0x10] = "EE", [0x11] = "EEEEE",
        [0x12] = "ESOBE", [0x13] = "OE", [0x14] = "EE", [0x15] = "", [0x16] = "", [0x17] = "", [0x18] = "SE",
        [0x19] = "ESOBE", [0x1A] = "EE", [0x1B] = "NP", [0x1C] = "OP", [0x1D] = "I", [0x1E] = "F", [0x1F] = "T",
        [0x20] = "O", [0x21] = "N", [0x22] = "R", [0x23] = "R", [0x24] = "B", [0x25] = "", [0x26] = "", [0x27] = "",
        [0x28] = "", [0x2A] = "", [0x2C] = "B", [0x2D] = "E", [0x2E] = "OE", [0x2F] = "EJ", [0x30] = "", [0x31] = "",
        [0x32] = "OEE", [0x33] = "OEE", [0x34] = "U", [0x35] = "OOBBE", [0x36] = "E", [0x37] = "NP", [0x38] = "BE",
        [0x39] = "EEEP", [0x3A] = "O", [0x3B] = "P", [0x3C] = "P", [0x3D] = "P", [0x3E] = "P", [0x3F] = "",
        [0x40] = "EEEP", [0x41] = "G", [0x42] = "ONP", [0x43] = "NO", [0x44] = "EE", [0x45] = "ESESE",
        [0x46] = "ESP", [0x47] = "ESP", [0x49] = "SEE", [0x4A] = "", [0x4B] = "N", [0x51] = "E", [0x52] = "OE",
        [0x53] = "", [0x54] = "ESP", [0x55] = "ESP", [0x56] = "ESP", [0x57] = "ESP", [0x58] = "EEBEJ", [0x59] = "ESP",
        [0x5A] = "SE",
    };

    /// <summary>What a skip at operand <paramref name="i"/> of <paramref name="spec"/> covers: (first operand after it, count, extra bytes).</summary>
    static (int From, int Count, int Extra) SkipSpan(byte tok, string spec, int i) => (tok, spec) switch
    {
        (_, "ESOBE") => (3, 1, 0),   // context: only the member expression (checked on MarvelGame.upk)
        (0x18, _) => (1, 1, 1),      // skip inside && / ||: the right operand and the call's closing EndFunctionParms
        (_, "ESESE") => (1, 1, 0),   // conditional: the branch that follows
        (_, "SEE") => (1, 2, 0),     // default parameter value: the value and EndParmValue
        _ => (1, spec.Length - i - 1, 0),
    };

    // ------------------------------------------------------------------ parse

    public static List<Node> Parse(byte[] code)
    {
        int p = 0;
        var list = new List<Node>();
        while (p < code.Length)
        {
            var n = ParseExpr(code, ref p);
            list.Add(n);
            if (n.Tok == EndOfScript) break;
        }
        if (p != code.Length) throw new InvalidDataException($"code continues after EndOfScript at {p}");
        return list;
    }

    static Node ParseExpr(byte[] c, ref int p)
    {
        int at = p;
        byte t = c[p++];
        var n = new Node(t);
        string spec;
        if (t >= 0x60) // native call: 0x60-0x6F extended (one more index byte), 0x70+ the index itself
        {
            if (t < 0x70) n.Ops.Add(new OByte(c[p++]));
            spec = "P";
        }
        else if (!Spec.TryGetValue(t, out spec!)) throw new InvalidDataException($"unknown token 0x{t:X2} at {at}");

        for (int i = 0; i < spec.Length; i++)
        {
            switch (spec[i])
            {
                case 'E': n.Ops.Add(new OExpr(ParseExpr(c, ref p))); break;
                case 'O': n.Ops.Add(new OObj(I32(c, p))); p += 4; break;
                case 'N': n.Ops.Add(new OName(I32(c, p), I32(c, p + 4))); p += 8; break;
                case 'B': n.Ops.Add(new OByte(c[p++])); break;
                case 'W': n.Ops.Add(new OU16(U16(c, p))); p += 2; break;
                case 'J': n.Ops.Add(new OJump(null, U16(c, p))); p += 2; break;
                case 'S': { var (f, k, x) = SkipSpan(t, spec, i); n.Ops.Add(new OSkip(f, k, x, U16(c, p))); p += 2; break; }
                case 'I': n.Ops.Add(new OI32(I32(c, p))); p += 4; break;
                case 'F': n.Ops.Add(new OF32(BitConverter.ToSingle(c, p))); p += 4; break;
                case 'R': n.Ops.Add(new OBytes(c[p..(p + 12)])); p += 12; break;
                case 'G': n.Ops.Add(new OBytes(c[p..(p + 13)])); p += 13; break;
                case 'T': { int e = Array.IndexOf(c, (byte)0, p); n.Ops.Add(new OBytes(c[p..(e + 1)])); p = e + 1; break; }
                case 'U':
                    {
                        int e = p; while (c[e] != 0 || c[e + 1] != 0) e += 2;
                        n.Ops.Add(new OBytes(c[p..(e + 2)])); p = e + 2; break;
                    }
                case 'P':
                    {
                        var ps = new List<Node>();
                        while (c[p] != EndFunctionParms) ps.Add(ParseExpr(c, ref p));
                        p++;
                        n.Ops.Add(new OParams(ps)); break;
                    }
                case 'C':
                    {
                        ushort j = U16(c, p); p += 2;
                        n.Ops.Add(new OJump(null, j));
                        if (j != 0xFFFF) n.Ops.Add(new OExpr(ParseExpr(c, ref p)));
                        break;
                    }
                case 'L':
                    {
                        var ls = new List<(int, int, int)>();
                        while (true)
                        {
                            int ni = I32(c, p), nn = I32(c, p + 4), off = I32(c, p + 8); p += 12;
                            ls.Add((ni, nn, off));
                            if (off == 0xFFFF || ni == 0 && nn == 0 && off == 0) break;
                        }
                        n.Ops.Add(new OLabels(ls)); break;
                    }
                default: throw new InvalidOperationException(spec);
            }
        }
        return n;
    }

    // ------------------------------------------------------------------ sizes

    public static int MemSize(Node n) => 1 + (n.Tok is >= 0x60 and < 0x70 ? 0 : 0) + n.Ops.Sum(MemSize);

    public static int MemSize(Op o) => o switch
    {
        OByte => 1,
        OU16 or OJump or OSkip => 2,
        OObj => 8,
        OName => 8,
        OI32 or OF32 => 4,
        OBytes b => b.V.Length,
        OExpr e => MemSize(e.V),
        OParams ps => ps.V.Sum(MemSize) + 1,
        OLabels l => l.V.Count * 16,
        _ => throw new InvalidOperationException(),
    };

    public static int MemSize(List<Node> code) => code.Sum(MemSize);

    // ------------------------------------------------------------------ jumps as labels

    /// <summary>
    /// Turns raw jump targets into labels on the top-level statements they point at, so statements can be inserted or
    /// replaced and the jumps follow. Throws when a target isn't a statement start.
    /// </summary>
    public static void BindLabels(List<Node> code)
    {
        var at = new Dictionary<int, Node>();
        int mem = 0;
        foreach (var s in code) { at[mem] = s; mem += MemSize(s); }
        int k = 0;
        foreach (var s in code) Walk(s, n =>
        {
            for (int i = 0; i < n.Ops.Count; i++)
                if (n.Ops[i] is OJump { Target: null } j && j.Raw != 0xFFFF)
                {
                    if (!at.TryGetValue(j.Raw, out var target)) throw new InvalidDataException($"jump to {j.Raw:X4} is not a statement start");
                    target.Label ??= new JumpLabel($"L{k++}");
                    n.Ops[i] = j with { Target = target.Label };
                }
        });
    }

    public static void Walk(Node n, Action<Node> f)
    {
        f(n);
        foreach (var o in n.Ops)
        {
            if (o is OExpr e) Walk(e.V, f);
            else if (o is OParams ps) foreach (var x in ps.V) Walk(x, f);
        }
    }

    // ------------------------------------------------------------------ write

    /// <summary>Disk bytes. Labels get their memory offsets first; skips are computed (raw ones checked when <paramref name="mismatches"/> is given).</summary>
    public static byte[] Write(List<Node> code, List<string>? mismatches = null)
    {
        int mem = 0;
        foreach (var s in code) { if (s.Label != null) s.Label.Mem = mem; mem += MemSize(s); }
        var w = new MemoryStream();
        foreach (var s in code) WriteNode(w, s, mismatches);
        return w.ToArray();
    }

    static void WriteNode(Stream w, Node n, List<string>? mm)
    {
        w.WriteByte(n.Tok);
        for (int i = 0; i < n.Ops.Count; i++)
        {
            switch (n.Ops[i])
            {
                case OByte b: w.WriteByte(b.V); break;
                case OU16 u: W16(w, u.V); break;
                case OJump j:
                    if (j.Target == null) W16(w, j.Raw);
                    else { if (j.Target.Mem < 0) throw new InvalidOperationException($"label {j.Target.Name} not placed"); W16(w, (ushort)j.Target.Mem); }
                    break;
                case OSkip s:
                    {
                        int size = s.Extra;
                        for (int k = 0; k < s.Count; k++) size += MemSize(n.Ops[i + s.From + k]);
                        // A parsed skip keeps its stored value: the compiler stores odd ones around iterators and some calls
                        // (70 of 3,563 functions). New nodes (Raw -1) get the rule that holds for all the others.
                        if (mm != null && s.Raw >= 0 && size != s.Raw) mm.Add($"tok {n.Tok:X2}: skip {s.Raw} stored, {size} computed");
                        W16(w, (ushort)(s.Raw >= 0 ? s.Raw : size));
                        break;
                    }
                case OObj o: W32(w, o.V); break;
                case OName nm: W32(w, nm.Index); W32(w, nm.Number); break;
                case OI32 x: W32(w, x.V); break;
                case OF32 f: w.Write(BitConverter.GetBytes(f.V)); break;
                case OBytes b: w.Write(b.V); break;
                case OExpr e: WriteNode(w, e.V, mm); break;
                case OParams ps: foreach (var x in ps.V) WriteNode(w, x, mm); w.WriteByte(EndFunctionParms); break;
                case OLabels l: foreach (var (a, b, c) in l.V) { W32(w, a); W32(w, b); W32(w, c); } break;
            }
        }
    }

    static int I32(byte[] c, int p) => BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(p));
    static ushort U16(byte[] c, int p) => BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(p));
    static void W16(Stream w, ushort v) { w.WriteByte((byte)v); w.WriteByte((byte)(v >> 8)); }
    static void W32(Stream w, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); w.Write(b); }
}

/// <summary>A Function export: header fields, code, trailer (layout checked on MarvelGame.upk, see <c>--roundtrip</c>).</summary>
public sealed class ScriptFunction
{
    // 0 NetIndex, 4 None (8), 12 Next, 16 SuperStruct, 20 ScriptText, 24 Children, 28 CppText, 32 Line, 36 TextPos,
    // 40 script size in memory, 44 script size on disk, 48 code, then iNative (2), OperPrecedence (1), FunctionFlags (4),
    // [RepOffset (2) when FUNC_Net], FriendlyName (8).
    public const int CodeAt = 48;
    public const uint FuncExec = 0x200, FuncNative = 0x400, FuncNet = 0x40;

    public byte[] Head = [];     // bytes 0..40
    public int MemSize, DiskSize;
    public byte[] Code = [];
    public byte[] Tail = [];

    public uint Flags => BinaryPrimitives.ReadUInt32LittleEndian(Tail.AsSpan(3));
    public int Children => BinaryPrimitives.ReadInt32LittleEndian(Head.AsSpan(24));

    public static ScriptFunction Read(byte[] d)
    {
        var f = new ScriptFunction
        {
            Head = d[..40],
            MemSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(40)),
            DiskSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(44)),
        };
        if (CodeAt + f.DiskSize > d.Length) throw new InvalidDataException("code size past the export");
        f.Code = d[CodeAt..(CodeAt + f.DiskSize)];
        f.Tail = d[(CodeAt + f.DiskSize)..];
        uint flags = f.Flags;
        int want = 2 + 1 + 4 + ((flags & FuncNet) != 0 ? 2 : 0) + 8;
        if (f.Tail.Length != want) throw new InvalidDataException($"trailer is {f.Tail.Length} bytes, expected {want}");
        return f;
    }

    public byte[] Build(List<Node> code)
    {
        byte[] bytes = Bytecode.Write(code);
        var w = new MemoryStream();
        w.Write(Head);
        w.Write(BitConverter.GetBytes(Bytecode.MemSize(code)));
        w.Write(BitConverter.GetBytes(bytes.Length));
        w.Write(bytes);
        w.Write(Tail);
        return w.ToArray();
    }
}
