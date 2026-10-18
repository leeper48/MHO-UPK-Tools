using System.Reflection;
using System.Runtime.CompilerServices;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Debug (--editor-cycles): who keeps an object alive. A breadth-first walk from every static field of the app's own
/// assemblies (and the open windows) through instance fields, arrays and collections, until it reaches the target; the path
/// is printed (Kurt, 2026-10-18: a closed editor stayed in memory, 420 MB each time).
/// </summary>
static class RootFinder
{
    public static string? PathTo(object target, int maxNodes = 3_000_000)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Obj, string Path)>();
        var asms = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is string n
            && (n.StartsWith("MHO", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AnimExport", StringComparison.OrdinalIgnoreCase) || n.Contains("Winforms", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("System.Windows.Forms", StringComparison.OrdinalIgnoreCase) || n.Equals("Microsoft.Win32.SystemEvents", StringComparison.OrdinalIgnoreCase)));
        foreach (var asm in asms)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsPrimitive || f.IsLiteral) continue;
                    object? v;
                    try { v = f.GetValue(null); } catch { continue; }
                    if (v != null && seen.Add(v)) queue.Enqueue((v, $"{t.FullName}.{f.Name}"));
                }
            }
        }
        foreach (Form f in Application.OpenForms) if (seen.Add(f)) queue.Enqueue((f, "OpenForms:" + f.Text));
        int n = 0;
        while (queue.Count > 0 && n++ < maxNodes)
        {
            var (o, path) = queue.Dequeue();
            if (ReferenceEquals(o, target)) return path;
            var type = o.GetType();
            if (type.IsPrimitive || o is string || o is Type || o is Delegate d && d.Method == null) continue;
            if (o is Array arr)
            {
                if (arr.GetType().GetElementType() is { } et && (et.IsPrimitive || et == typeof(string))) continue;
                int i = 0;
                foreach (var x in arr) { if (x != null && seen.Add(x)) queue.Enqueue((x, $"{path}[{i}]")); if (++i > 100000) break; }
                continue;
            }
            for (var ty = type; ty != null && ty != typeof(object); ty = ty.BaseType)
                foreach (var f in ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsPrimitive || f.FieldType.IsPointer || f.FieldType == typeof(string)) continue;
                    object? v;
                    try { v = f.GetValue(o); } catch { continue; }
                    if (v == null || v.GetType().IsPrimitive) continue;
                    if (seen.Add(v)) queue.Enqueue((v, $"{path}.{f.Name}"));
                }
        }
        return null;
    }
}
