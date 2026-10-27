using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// Console output captured per call (the package code reports through Console, as in the CLI). Each capture goes to its own
/// writer for the code it runs (and the tasks that code starts), so captures on other threads at the same time don't take each
/// other's output or leave Console.Out pointing at a finished capture (2026-10-27: a Model tab build and the added props'
/// re-apply ran side by side, each swapping Console.Out, and a test run lost every line after them).
/// </summary>
static class ConsoleCapture
{
    static readonly AsyncLocal<TextWriter?> current = new();
    static readonly object gate = new();
    static TextWriter original = TextWriter.Null;
    static TextWriter? installed;

    /// <summary>Runs <paramref name="f"/> with what it writes to Console in <paramref name="said"/>.</summary>
    public static T Run<T>(Func<T> f, out string said)
    {
        Install();
        var sw = new StringWriter();
        var outer = current.Value;
        current.Value = sw;
        try { return f(); }
        finally { current.Value = outer; said = sw.ToString(); }
    }

    /// <summary>Runs <paramref name="a"/> and returns what it wrote to Console.</summary>
    public static string Run(Action a)
    {
        Run<bool>(() => { a(); return true; }, out string said);
        return said;
    }

    /// <summary>Console.Out becomes the router (again, when something set another writer since: that one is written to
    /// outside captures).</summary>
    static void Install()
    {
        lock (gate)
        {
            if (installed != null && ReferenceEquals(Console.Out, installed)) return;
            original = Console.Out;
            Console.SetOut(new Router());
            installed = Console.Out;   // (Console wraps it in a synchronized writer)
        }
    }

    sealed class Router : TextWriter
    {
        static TextWriter Target => current.Value ?? original;
        public override Encoding Encoding => Target.Encoding;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
        public override void WriteLine(string? value) => Target.WriteLine(value);
        public override void WriteLine() => Target.WriteLine();
        public override void Flush() => Target.Flush();
    }
}
