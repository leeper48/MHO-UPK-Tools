using MhoPackageModifier;
namespace MhoExtendedModManager.FreeCam;

sealed class FreeCamKeyError(string what) : Exception($"'{what}' not found in the package");

/// <summary>
/// Builds bytecode nodes by object path, in the forms the compiler itself uses in MarvelGame.upk (checked with --ops):
/// a context is (object, skip, the member's property, size byte 0, member); a struct member is (member, struct, 0, 1 when
/// written / 0 when read); casts 0x3F int→float, 0x44 float→int. Natives by their Core.upk index (--natives).
/// </summary>
sealed class Asm
{
    readonly Package pkg;
    readonly Dictionary<string, int> objects = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> names = new(StringComparer.OrdinalIgnoreCase);

    // Core.upk native indices (checked on the stock file)
    public const int AddInt = 0x92, SubInt = 0x93, AndInt = 0x9C, AddEqInt = 0xA1, MulF = 0xAB, AddF = 0xAE, SubF = 0xAF,
        LessInt = 0x96, GreaterInt = 0x97, LessEqInt = 0x98, GreaterEqInt = 0x99, LessF = 0xB0, GreaterF = 0xB1, LessEqF = 0xB2, GreaterEqF = 0xB3, FClamp = 0xF6, EqF = 0xB4, NotEqF = 0xB5, AddEqF = 0xB8, Exp = 0xBF, MulVF = 0xD4, Cross = 0xDC, AddEqV = 0xDF,
        Normal = 0xE2, FMin = 0xF4, FMax = 0xF5, Clamp = 0xFB, NotEqObj = 0x77;
    public const byte IntToFloat = 0x3F, FloatToInt = 0x44;

    public Asm(Package pkg)
    {
        this.pkg = pkg;
        for (int i = 0; i < pkg.Exports.Length; i++) objects.TryAdd(pkg.PathOf(pkg.Exports[i]), i + 1);
        for (int i = 0; i < pkg.Imports.Length; i++) objects.TryAdd(ImportPath(i), -i - 1);
        for (int i = 0; i < pkg.Names.Length; i++) names.TryAdd(pkg.Names[i], i);
    }

    string ImportPath(int i)
    {
        var parts = new List<string>();
        int idx = -i - 1, guard = 0;
        while (idx != 0 && guard++ < 32)
        {
            if (idx < 0) { var im = pkg.Imports[-idx - 1]; parts.Add(im.ObjectName); idx = im.OuterIndex; }
            else { var ex = pkg.Exports[idx - 1]; parts.Add(ex.ObjectName); idx = ex.OuterIndex; }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    public int O(string path) => objects.TryGetValue(path, out int v) ? v : throw new FreeCamKeyError(path);
    public bool Has(string path) => objects.ContainsKey(path);

    static Node N(byte tok, params Op[] ops) { var n = new Node(tok); n.Ops.AddRange(ops); return n; }

    public Node Inst(string path) => N(0x01, new OObj(O(path)));
    public Node Local(string path) => N(0x00, new OObj(O(path)));
    public static Node F(float v) => N(0x1E, new OF32(v));
    public static Node I(int v) => N(0x1D, new OI32(v));
    public static Node None() => N(0x2A);

    public static Node Nat(int index, params Node[] args)
    {
        var n = index >= 0x100 ? N((byte)(0x60 + (index >> 8)), new OByte((byte)(index & 0xFF))) : N((byte)index);
        n.Ops.Add(new OParams([.. args]));
        return n;
    }

    public Node Final(string function, params Node[] args) => N(0x1C, new OObj(O(function)), new OParams([.. args]));
    public static Node Cast(byte kind, Node e) => N(0x38, new OByte(kind), new OExpr(e));
    public Node DynCast(string cls, Node e) => N(0x2E, new OObj(O(cls)), new OExpr(e));

    /// <summary>obj.member, where member is an instance variable (its property path).</summary>
    public Node Ctx(Node obj, string memberProperty) =>
        N(0x19, new OExpr(obj), new OSkip(3, 1, 0), new OObj(O(memberProperty)), new OByte(0), new OExpr(Inst(memberProperty)));

    /// <summary>structExpr.member (e.g. Core.Object.Vector.X of a Vector).</summary>
    public Node Member(Node structExpr, string structPath, string member, bool write) =>
        N(0x35, new OObj(O($"{structPath}.{member}")), new OObj(O(structPath)), new OByte(0), new OByte((byte)(write ? 1 : 0)), new OExpr(structExpr));

    public static Node Let(Node target, Node value) => N(0x0F, new OExpr(target), new OExpr(value));
    /// <summary>A bool assignment (EX_LetBool, as stock code sets gameCamIndexChanged = true).</summary>
    public static Node LetBool(Node target, Node value) => N(0x14, new OExpr(target), new OExpr(value));
    public static Node True() => N(0x27);
    public static Node False() => N(0x28);
    /// <summary>A string constant (EX_StringConst: Latin-1, zero-terminated).</summary>
    public static Node Str(string text) => N(0x1F, new OBytes([.. System.Text.Encoding.Latin1.GetBytes(text), 0]));

    /// <summary>obj.Function(args) for a final function: the context names the function's ReturnValue property (as compiled code does).</summary>
    public Node CtxFinal(Node obj, string function, params Node[] args) =>
        N(0x19, new OExpr(obj), new OSkip(3, 1, 0), new OObj(O(function + ".ReturnValue")), new OByte(0), new OExpr(Final(function, args)));

    /// <summary>obj.Call() for a function with no return value (as compiled code calls m_gameSettings.SaveConfig()): the context
    /// names no property.</summary>
    public static Node CtxCall(Node obj, Node call) => N(0x19, new OExpr(obj), new OSkip(3, 1, 0), new OObj(0), new OByte(0), new OExpr(call));

    /// <summary>array[index] (EX_ArrayElement: the index, then the array, as compiled).</summary>
    public static Node Elem(Node index, Node array) => N(0x10, new OExpr(index), new OExpr(array));

    public static Node IfNot(Node condition, JumpLabel target) => N(0x07, new OJump(target, 0), new OExpr(condition));
    public static Node Goto(JumpLabel target) => N(0x06, new OJump(target, 0));
    public static Node ReturnNothing() => N(0x04, new OExpr(N(0x0B)));
    public static Node End() => N(Bytecode.EndOfScript);

    public static Node At(JumpLabel label, Node statement) { statement.Label = label; return statement; }
}
