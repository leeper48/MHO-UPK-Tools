using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace UpkMeshScan;

static class Program
{
    static readonly HashSet<string> StaticClasses = new(StringComparer.OrdinalIgnoreCase) { "StaticMesh", "FracturedStaticMesh" };
    static readonly HashSet<string> SkeletalClasses = new(StringComparer.OrdinalIgnoreCase) { "SkeletalMesh" };

    sealed record MeshHit(string Class, string Name, string Path, int Size, string? DecodeError = null, IReadOnlyList<string>? Notes = null, bool Decoded = false, IReadOnlyList<string>? Facts = null, bool Stock = false);
    sealed record Result(string File, long Bytes, string Version, string ChunkSource, List<MeshHit> Meshes, string? Error, bool Stock = false);

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    static int Main(string[] args)
    {
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

        // No arguments: the GUI. It's a WinExe so a double-click shows no console window.
        if (args.Length == 0)
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Gui.MainForm(version));
            return 0;
        }

        // Arguments: the CLI. A WinExe has no console of its own, so attach to the one that launched
        // it (as AnimExportCli does) and re-open the streams. UTF-8 without a BOM: with a BOM, every
        // run printed stray bytes before the version banner.
        if (AttachConsole(-1))
        {
            var utf8 = new UTF8Encoding(false);
            Console.OutputEncoding = utf8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        Console.WriteLine($"UpkMeshScan v{version}");
        return Run(args, version);
    }

    internal static int Run(string[] args, string version)
    {
        int removeAt = Array.FindIndex(args, a => a.Equals("--remove-components", StringComparison.OrdinalIgnoreCase));
        if (removeAt >= 0)
        {
            // --remove-components <package.upk> [--material text] [--mesh text] [--library lib.upk] [--dry-run]
            string ROpt(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
            if (removeAt + 1 >= args.Length || (ROpt("--material").Length == 0 && ROpt("--mesh").Length == 0)) { Usage(); return 2; }
            return ComponentRemove.Run(args[removeAt + 1], ROpt("--material") is { Length: > 0 } m ? m : null, ROpt("--mesh") is { Length: > 0 } me ? me : null,
                ROpt("--library") is { Length: > 0 } l ? l : null, args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int placedAt = Array.FindIndex(args, a => a.Equals("--export-placed", StringComparison.OrdinalIgnoreCase));
        if (placedAt >= 0)
        {
            // --export-placed <folder> <layout.txt> <library.upk> --out file.fbx [--offset X,Y[,Z]] [--min-z -400] [--max-z 150] [--min-footprint 64] [--min-height 0] [--skip a,b]
            string POpt(string name, string fallback) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
            if (placedAt + 3 >= args.Length || POpt("--out", "").Length == 0) { Usage(); return 2; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var o = POpt("--offset", "0,0,0").Split(',').Select(v => float.Parse(v, inv)).Concat([0f, 0f, 0f]).Take(3).ToArray();
            return PlacedExport.Run(args[placedAt + 1], args[placedAt + 2], args[placedAt + 3], POpt("--out", ""), new System.Numerics.Vector3(o[0], o[1], o[2]),
                float.Parse(POpt("--min-z", "-400"), inv), float.Parse(POpt("--max-z", "150"), inv), float.Parse(POpt("--min-footprint", "64"), inv),
                POpt("--skip", "terrain_flat_filler,godray,lightbeam").Split(',', StringSplitOptions.RemoveEmptyEntries),
                float.Parse(POpt("--max-height", "1e9"), inv),
                POpt("--skip-material", "").Split(',', StringSplitOptions.RemoveEmptyEntries),
                POpt("--diffuse", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).Where(x => x.Length == 2).Select(x => (x[0], x[1])).ToList(),
                float.Parse(POpt("--min-height", "0"), inv));
        }

        int namesAt = Array.FindIndex(args, a => a.Equals("--names", StringComparison.OrdinalIgnoreCase));
        if (namesAt >= 0 && namesAt + 1 < args.Length)
        {
            // --names <package.upk> [index ...]: name-table entries (all, or the given indices). Read-only.
            var pk = Package.Open(args[namesAt + 1]);
            var want = args.Skip(namesAt + 2).Where(x => int.TryParse(x, out _)).Select(int.Parse).ToList();
            Console.WriteLine($"{Path.GetFileName(args[namesAt + 1])}: {pk.Names.Length} names");
            foreach (int i in want.Count > 0 ? want : Enumerable.Range(0, pk.Names.Length))
                Console.WriteLine($"  {i,5}  {(i >= 0 && i < pk.Names.Length ? pk.Names[i] : "(out of range)")}");
            return 0;
        }

        int instAt = Array.FindIndex(args, a => a.Equals("--add-mesh-instances", StringComparison.OrdinalIgnoreCase));
        if (instAt >= 0)
        {
            // --add-mesh-instances <package.upk> <source.upk> <mesh[,mesh...]> --template <component-path> [--min-draw 3500] [--z-offset dz] [--dry-run]
            string IOpt(string name, string fallback) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
            if (instAt + 3 >= args.Length || IOpt("--template", "").Length == 0) { Usage(); return 2; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return MeshInstances.Run(args[instAt + 1], args[instAt + 2], args[instAt + 3].Split(',', StringSplitOptions.RemoveEmptyEntries), IOpt("--template", ""),
                float.Parse(IOpt("--min-draw", "3500"), inv), float.Parse(IOpt("--z-offset", "0"), inv), args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int copiesAt = Array.FindIndex(args, a => a.Equals("--add-component-copies", StringComparison.OrdinalIgnoreCase));
        if (copiesAt >= 0)
        {
            // --add-component-copies <package.upk> <component-path> --yaw y1[:pitch[:roll]],y2,... [--dry-run] (degrees, added)
            int yi = Array.FindIndex(args, a => a.Equals("--yaw", StringComparison.OrdinalIgnoreCase));
            if (copiesAt + 2 >= args.Length || yi < 0 || yi + 1 >= args.Length) { Usage(); return 2; }
            var turns = args[yi + 1].Split(',').Select(x =>
            {
                var v = x.Split(':').Select(f => float.Parse(f, System.Globalization.CultureInfo.InvariantCulture)).Concat([0f, 0f]).ToArray();
                return new System.Numerics.Vector3(v[0], v[1], v[2]);
            }).ToList();
            return ComponentCopies.Run(args[copiesAt + 1], args[copiesAt + 2], turns, args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int coverAt = Array.FindIndex(args, a => a.Equals("--sky-coverage", StringComparison.OrdinalIgnoreCase));
        if (coverAt >= 0)
        {
            // --sky-coverage <package.upk> <component-path> [--from x,y,z]
            if (coverAt + 2 >= args.Length) { Usage(); return 2; }
            int fi = Array.FindIndex(args, a => a.Equals("--from", StringComparison.OrdinalIgnoreCase));
            System.Numerics.Vector3? from = null;
            if (fi >= 0 && fi + 1 < args.Length) { var v = args[fi + 1].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); from = new(v[0], v[1], v[2]); }
            return SkyCoverage.Run(args[coverAt + 1], args[coverAt + 2], from);
        }

        int buildZoneAt = Array.FindIndex(args, a => a.Equals("--build-zone", StringComparison.OrdinalIgnoreCase));
        if (buildZoneAt >= 0)
        {
            // --build-zone <zone> <game-folder> [--walls facade|grey] [--dry-run]
            if (buildZoneAt + 2 >= args.Length) { Usage(); return 2; }
            int wi = Array.FindIndex(args, a => a.Equals("--walls", StringComparison.OrdinalIgnoreCase));
            var walls = wi >= 0 && wi + 1 < args.Length && args[wi + 1].Equals("grey", StringComparison.OrdinalIgnoreCase) ? ZoneBuilds.Walls.Grey : ZoneBuilds.Walls.Facade;
            return ZoneBuilds.Build(args[buildZoneAt + 1], args[buildZoneAt + 2], walls, args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int importAt = Array.FindIndex(args, a => a.Equals("--import-fbx", StringComparison.OrdinalIgnoreCase));
        if (importAt >= 0)
        {
            if (importAt + 3 >= args.Length) { Usage(); return 2; }
            int outAt = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
            return MeshImport.Import(args[importAt + 1], args[importAt + 2], args[importAt + 3],
                dryRun: args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)),
                outDir: outAt >= 0 && outAt + 1 < args.Length ? args[outAt + 1] : null);
        }

        int revertAt = Array.FindIndex(args, a => a.Equals("--revert", StringComparison.OrdinalIgnoreCase));
        if (revertAt >= 0)
        {
            if (revertAt + 1 >= args.Length) { Usage(); return 2; }
            return MeshImport.Revert(args[revertAt + 1]);
        }

        int roundTripAt = Array.FindIndex(args, a => a.Equals("--verify-import-roundtrip", StringComparison.OrdinalIgnoreCase));
        if (roundTripAt >= 0)
        {
            if (roundTripAt + 2 >= args.Length) { Usage(); return 2; }
            return MeshImport.VerifyRoundTrip(args[roundTripAt + 1], args[roundTripAt + 2]);
        }

        int cellAt = Array.FindIndex(args, a => a.Equals("--add-cell-placeholders", StringComparison.OrdinalIgnoreCase));
        if (cellAt >= 0)
        {
            // --add-cell-placeholders <package.upk> <placeholders.fbx> [--min-draw 3500] [--cell 2304] [--gray 0.03] [--exclude-box ...]
            //     [--shrink F] [--add-fbx f.fbx ...] [--ground-z -40 [--ground-margin 4000]] [--from-live] [--offset X,Y[,Z]] [--lift Z] [--always-fbx f.fbx ...] [--wall-material pkg.obj [--wall-uv 512]] [--lod f.fbx=pkg.mat ... [--lod-inset 0.98 | --lod-shrink 6] [--lod-drop 4] [--lod-exclude-box x0,y0,x1,y1 ...]] [--dry-run]
            if (cellAt + 2 >= args.Length) { Usage(); return 2; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string Opt(string name, string fallback) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
            List<string> Multi(string name) => args.Select((a, i) => (a, i)).Where(x => x.a.Equals(name, StringComparison.OrdinalIgnoreCase) && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList();
            string ground = Opt("--ground-z", "");
            return CellPlaceholders.Run(args[cellAt + 1], args[cellAt + 2], float.Parse(Opt("--min-draw", "3500"), inv), float.Parse(Opt("--cell", "2304"), inv),
                float.Parse(Opt("--gray", "0.03"), inv), args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)),
                Multi("--exclude-box").Select(b => b.Split(',').Select(v => float.Parse(v, inv)).ToArray()).Where(b => b.Length == 4).ToList(),
                ground.Length > 0 ? float.Parse(ground, inv) : null, float.Parse(Opt("--ground-margin", "4000"), inv),
                float.Parse(Opt("--shrink", "1"), inv), Multi("--add-fbx"),
                fromLive: args.Any(a => a.Equals("--from-live", StringComparison.OrdinalIgnoreCase)), lift: float.Parse(Opt("--lift", "0"), inv),
                offset: Opt("--offset", "0,0,0").Split(',').Select(v => float.Parse(v, inv)).Concat([0f, 0f, 0f]).Take(3).ToArray() is var o ? new System.Numerics.Vector3(o[0], o[1], o[2]) : default,
                alwaysFbx: Multi("--always-fbx"),
                wallMaterial: Opt("--wall-material", "") is { Length: > 0 } wm ? wm : null, wallUv: float.Parse(Opt("--wall-uv", "512"), inv),
                componentTemplate: Opt("--component-template", "") is { Length: > 0 } ct ? ct : null,
                topMaterial: Opt("--top-material", "") is { Length: > 0 } tm ? tm : null, topUv: float.Parse(Opt("--top-uv", "512"), inv),
                texturedFbx: Opt("--textured-fbx", "") is { Length: > 0 } tf ? tf : null,
                texturedMaterial: Opt("--textured-material", "") is { Length: > 0 } tmat ? tmat : null,
                texturedZ: Opt("--textured-z", "") is { Length: > 0 } tz ? float.Parse(tz, inv) : null,
                lods: Multi("--lod").Select(l => l.Split('=', 2)).Where(l => l.Length == 2).Select(l => (l[0], l[1])).ToList(),
                lodInset: float.Parse(Opt("--lod-inset", "1"), inv), lodDrop: float.Parse(Opt("--lod-drop", "0"), inv),
                lodShrink: float.Parse(Opt("--lod-shrink", "0"), inv),
                lodExcludeBoxes: Multi("--lod-exclude-box").Select(b => b.Split(',').Select(v => float.Parse(v, inv)).ToArray()).Where(b => b.Length == 4).ToList());
        }

        int matAt = Array.FindIndex(args, a => a.Equals("--material-params", StringComparison.OrdinalIgnoreCase));
        if (matAt >= 0)
        {
            if (matAt + 2 >= args.Length) { Usage(); return 2; }
            return MaterialParams.Run(args[matAt + 1], args[matAt + 2]);
        }

        int skyAt = Array.FindIndex(args, a => a.Equals("--add-sky-placeholders", StringComparison.OrdinalIgnoreCase));
        if (skyAt >= 0)
        {
            // --add-sky-placeholders <package.upk> <placeholders.fbx | none> [--mesh sm_skysphere] [--material m_procedural_sky_daytime] [--gray 0.03]
            //     [--color R,G,B] [--ground-z Z [--ground-margin 4000 | --ground-box x0,y0,x1,y1]] [--ground-material pkg.obj [--ground-uv 2304]] [--ground-grid N] [--sky-drop F] [--dry-run]
            if (skyAt + 2 >= args.Length) { Usage(); return 2; }
            string Opt(string name, string fallback) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var boxes = args.Select((a, i) => (a, i)).Where(x => x.a.Equals("--exclude-box", StringComparison.OrdinalIgnoreCase) && x.i + 1 < args.Length)
                .Select(x => args[x.i + 1].Split(',').Select(v => float.Parse(v, inv)).ToArray()).Where(b => b.Length == 4).ToList();
            string ground = Opt("--ground-z", "");
            return SkyPlaceholders.Run(args[skyAt + 1], args[skyAt + 2], Opt("--mesh", "sm_skysphere"), Opt("--material", "m_procedural_sky_daytime"),
                float.Parse(Opt("--gray", "0.03"), inv), args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)),
                boxes, ground.Length > 0 ? float.Parse(ground, inv) : null, float.Parse(Opt("--ground-margin", "4000"), inv),
                float.Parse(Opt("--shrink", "1"), inv),
                args.Select((a, i) => (a, i)).Where(x => x.a.Equals("--add-fbx", StringComparison.OrdinalIgnoreCase) && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToList(),
                // --ground-box x0,y0,x1,y1[,z][;x0,y0,x1,y1[,z]...]: one quad per box, each at its own height (default --ground-z).
                Opt("--ground-box", "") is { Length: > 0 } gb
                    ? gb.Split(';', StringSplitOptions.RemoveEmptyEntries).SelectMany(bx => { var v = bx.Split(',').Select(x => float.Parse(x, inv)).ToList(); if (v.Count == 4) v.Add(float.NaN); return v.Count == 5 ? v : throw new ArgumentException($"--ground-box '{bx}': 4 or 5 numbers"); }).ToArray()
                    : null,
                Opt("--color", "") is { Length: > 0 } col && col.Split(',').Select(v => float.Parse(v, inv)).ToArray() is { Length: 3 } c
                    ? new System.Numerics.Vector3(c[0], c[1], c[2]) : null,
                Opt("--ground-material", "") is { Length: > 0 } gm ? gm : null, float.Parse(Opt("--ground-uv", "2304"), inv),
                float.Parse(Opt("--sky-drop", "0"), inv), float.Parse(Opt("--ground-grid", "0"), inv));
        }

        int testRebuildAt = Array.FindIndex(args, a => a.Equals("--test-rebuild", StringComparison.OrdinalIgnoreCase));
        if (testRebuildAt >= 0)
        {
            if (testRebuildAt + 1 >= args.Length) { Usage(); return 2; }
            return SkyPlaceholders.TestRebuild(args[testRebuildAt + 1], testRebuildAt + 2 < args.Length ? args[testRebuildAt + 2] : null);
        }

        int zoneAt = Array.FindIndex(args, a => a.Equals("--zone-placeholders", StringComparison.OrdinalIgnoreCase));
        if (zoneAt >= 0)
        {
            // --zone-placeholders <folder> <tile-prefix | layout.txt> <library.upk> [--out file.fbx] [--min-height N] [--min-footprint N] [--inset F]
            //     [--ground-boxes rects.txt [--box-top -8] [--box-bottom -220]] [--no-meshes] [--raster 32 [--raster-min-z 40] [--raster-step 32]]
            if (zoneAt + 3 >= args.Length) { Usage(); return 2; }
            string Opt(string name, string fallback) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return ZonePlaceholders.Run(args[zoneAt + 1], args[zoneAt + 2], args[zoneAt + 3],
                Opt("--out", Path.Combine(AppContext.BaseDirectory, "exports", args[zoneAt + 2].TrimEnd('_') + "_placeholders.fbx")),
                float.Parse(Opt("--min-height", "400"), inv), float.Parse(Opt("--min-footprint", "200"), inv), float.Parse(Opt("--inset", "0.90"), inv),
                Opt("--skip", "tree").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Opt("--only", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Opt("--ground-boxes", "") is { Length: > 0 } gb ? gb : null, float.Parse(Opt("--box-top", "-8"), inv), float.Parse(Opt("--box-bottom", "-220"), inv),
                args.Any(a => a.Equals("--no-meshes", StringComparison.OrdinalIgnoreCase)),
                float.Parse(Opt("--raster", "0"), inv), float.Parse(Opt("--raster-min-z", "40"), inv), float.Parse(Opt("--raster-step", "32"), inv));
        }

        int setAt = Array.FindIndex(args, a => a.Equals("--set-property", StringComparison.OrdinalIgnoreCase));
        if (setAt >= 0)
        {
            // --set-property <package.upk> <export-path> <Name=Value> [<Name=Value> ...] [--dry-run]
            var rest = args.Skip(setAt + 3).Where(a => !a.StartsWith("--")).ToList();
            if (setAt + 2 >= args.Length || rest.Count == 0 || rest.Any(a => !a.Contains('='))) { Usage(); return 2; }
            var changes = rest.Select(a => (a[..a.IndexOf('=')], a[(a.IndexOf('=') + 1)..])).ToList();
            return PropertyEdit.Run(args[setAt + 1], args[setAt + 2], changes, args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int listAt = Array.FindIndex(args, a => a.Equals("--list-exports", StringComparison.OrdinalIgnoreCase));
        if (listAt >= 0)
        {
            if (listAt + 1 >= args.Length) { Usage(); return 2; }
            var pkg = Package.Open(args[listAt + 1]);
            string? classFilter = listAt + 2 < args.Length && !args[listAt + 2].StartsWith("--") ? args[listAt + 2] : null;
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                var e = pkg.Exports[i];
                string cls = pkg.ClassOf(e);
                if (classFilter != null && !cls.Contains(classFilter, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine($"  #{i + 1,-6} {cls,-36} {e.SerialSize,9:N0} B  {pkg.PathOf(e)}");
            }
            return 0;
        }

        int texExportAt = Array.FindIndex(args, a => a.Equals("--export-textures", StringComparison.OrdinalIgnoreCase));
        if (texExportAt >= 0)
        {
            if (texExportAt + 1 >= args.Length) { Usage(); return 2; }
            int outAt = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
            string? filter = texExportAt + 2 < args.Length && !args[texExportAt + 2].StartsWith("--") ? args[texExportAt + 2] : null;
            string dir = outAt >= 0 && outAt + 1 < args.Length ? args[outAt + 1] : Path.Combine(AppContext.BaseDirectory, "textures", Path.GetFileNameWithoutExtension(args[texExportAt + 1]));
            return TextureExport.Run(args[texExportAt + 1], filter, dir);
        }

        int texAt = Array.FindIndex(args, a => a.Equals("--texture-info", StringComparison.OrdinalIgnoreCase));
        if (texAt >= 0)
        {
            if (texAt + 1 >= args.Length) { Usage(); return 2; }
            var pkg = Package.Open(args[texAt + 1]);
            string? filter = texAt + 2 < args.Length ? args[texAt + 2] : null;
            foreach (var e in pkg.Exports.Where(x => pkg.ClassOf(x).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)))
            {
                if (filter != null && !e.ObjectName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var ti = TextureInfo.Read(pkg, e);
                    // Inline mips store their own absolute file offset; does it still point at the data?
                    var inl = ti.Mips.Where(m => m.Inline).ToList();
                    int ok = inl.Count(m => m.Offset == e.SerialOffset + m.InlineAt);
                    Console.WriteLine($"{ti}  | inline mip offsets: {ok}/{inl.Count} point at their data");
                }
                catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { Console.WriteLine($"{e.ObjectName}: {ex.Message}"); }
            }
            return 0;
        }

        int sourcesAt = Array.FindIndex(args, a => a.Equals("--import-sources", StringComparison.OrdinalIgnoreCase));
        if (sourcesAt >= 0)
        {
            if (sourcesAt + 2 >= args.Length) { Usage(); return 2; }
            return ImportSources.Run(args[sourcesAt + 1], args[sourcesAt + 2]);
        }

        int usersAt = Array.FindIndex(args, a => a.Equals("--mesh-users", StringComparison.OrdinalIgnoreCase));
        if (usersAt >= 0)
        {
            if (usersAt + 2 >= args.Length) { Usage(); return 2; }
            return MeshUsers.Run(args[usersAt + 1], args[usersAt + 2]);
        }

        foreach (var (flag, act) in new (string, Func<string, bool, int>)[] { ("--undo", History.Undo), ("--redo", History.Redo), ("--history", (p, _) => History.List(p)) })
        {
            // --undo / --redo <package.upk> [--force]: step through the write history (snapshots outside the game folder).
            // --history <package.upk>: list it.
            int at = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            if (at < 0) continue;
            if (at + 1 >= args.Length) { Usage(); return 2; }
            return act(args[at + 1], args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase)));
        }

        int levelActorAt = Array.FindIndex(args, a => a.Equals("--add-level-actor", StringComparison.OrdinalIgnoreCase));
        if (levelActorAt >= 0)
        {
            // --add-level-actor <package.upk> <actor-path> [--dry-run]
            if (levelActorAt + 2 >= args.Length) { Usage(); return 2; }
            return LevelEdit.AddActor(args[levelActorAt + 1], args[levelActorAt + 2], args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int uvInfoAt = Array.FindIndex(args, a => a.Equals("--uv-info", StringComparison.OrdinalIgnoreCase));
        if (uvInfoAt >= 0)
        {
            // --uv-info <package.upk> <staticmesh>: UV channel ranges per section. Read-only.
            if (uvInfoAt + 2 >= args.Length) { Usage(); return 2; }
            return MeshUv.Info(args[uvInfoAt + 1], args[uvInfoAt + 2]);
        }
        int scaleUvAt = Array.FindIndex(args, a => a.Equals("--scale-uv", StringComparison.OrdinalIgnoreCase));
        if (scaleUvAt >= 0)
        {
            // --scale-uv <package.upk> <staticmesh> <channel> <factor | fu,fv> [--section 0] [--dry-run]
            if (scaleUvAt + 4 >= args.Length) { Usage(); return 2; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int si = Array.FindIndex(args, a => a.Equals("--section", StringComparison.OrdinalIgnoreCase));
            var f = args[scaleUvAt + 4].Split(',').Select(x => float.Parse(x, inv)).ToArray();
            return MeshUv.Scale(args[scaleUvAt + 1], args[scaleUvAt + 2], int.Parse(args[scaleUvAt + 3]), new System.Numerics.Vector2(f[0], f.Length > 1 ? f[1] : f[0]),
                si >= 0 && si + 1 < args.Length ? int.Parse(args[si + 1]) : 0, args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int setObjAt = Array.FindIndex(args, a => a.Equals("--set-object", StringComparison.OrdinalIgnoreCase));
        if (setObjAt >= 0)
        {
            // --set-object <package.upk> <export-path> <property | property[i]> <target-export-path> [--dry-run]
            if (setObjAt + 4 >= args.Length) { Usage(); return 2; }
            return ObjectEdit.Run(args[setObjAt + 1], args[setObjAt + 2], args[setObjAt + 3], args[setObjAt + 4],
                args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int texImportAt = Array.FindIndex(args, a => a.Equals("--import-texture", StringComparison.OrdinalIgnoreCase));
        if (texImportAt >= 0)
        {
            // --import-texture <package.upk> <template-texture-path> <new-name> <file.dds> [--dry-run]
            if (texImportAt + 4 >= args.Length) { Usage(); return 2; }
            return TextureImport.Run(args[texImportAt + 1], args[texImportAt + 2], args[texImportAt + 3], args[texImportAt + 4],
                args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)));
        }

        int copyAt = Array.FindIndex(args, a => a.Equals("--copy-export", StringComparison.OrdinalIgnoreCase));
        if (copyAt >= 0)
        {
            // --copy-export <source.upk> <export-path> <target.upk> [--cut prop,...] [--rename name] [--replace-ref src.path=target.path ...] [--dry-run]
            if (copyAt + 3 >= args.Length) { Usage(); return 2; }
            int ci = Array.FindIndex(args, a => a.Equals("--cut", StringComparison.OrdinalIgnoreCase));
            int ri = Array.FindIndex(args, a => a.Equals("--rename", StringComparison.OrdinalIgnoreCase));
            var replaceRefs = args.Select((a, i) => (a, i)).Where(x => x.a.Equals("--replace-ref", StringComparison.OrdinalIgnoreCase) && x.i + 1 < args.Length)
                .Select(x => args[x.i + 1].Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1], StringComparer.OrdinalIgnoreCase);
            return ExportCopy.Run(args[copyAt + 1], args[copyAt + 2], args[copyAt + 3],
                ci >= 0 && ci + 1 < args.Length ? args[ci + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [],
                args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)),
                ri >= 0 && ri + 1 < args.Length ? args[ri + 1] : null, replaceRefs);
        }

        int depsAt = Array.FindIndex(args, a => a.Equals("--export-deps", StringComparison.OrdinalIgnoreCase));
        if (depsAt >= 0)
        {
            // --export-deps <package.upk> <export> [--depth 20]: what a copy of that export would need. Read-only.
            if (depsAt + 2 >= args.Length) { Usage(); return 2; }
            int di = Array.FindIndex(args, a => a.Equals("--depth", StringComparison.OrdinalIgnoreCase));
            int ci = Array.FindIndex(args, a => a.Equals("--cut", StringComparison.OrdinalIgnoreCase));
            return ExportDeps.Run(args[depsAt + 1], args[depsAt + 2], di >= 0 && di + 1 < args.Length ? int.Parse(args[di + 1]) : 20,
                ci >= 0 && ci + 1 < args.Length ? args[ci + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null);
        }

        int findAt = Array.FindIndex(args, a => a.Equals("--find-name", StringComparison.OrdinalIgnoreCase));
        if (findAt >= 0)
        {
            if (findAt + 2 >= args.Length) { Usage(); return 2; }
            return FindName.Run(args[findAt + 1], args[findAt + 2], args.Any(a => a.Equals("--include-backups", StringComparison.OrdinalIgnoreCase)));
        }

        int inspectAt = Array.FindIndex(args, a => a.Equals("--inspect-fbx", StringComparison.OrdinalIgnoreCase));
        if (inspectAt >= 0) return FbxInspect.Run(args.Skip(inspectAt + 1));

        int exportAt = Array.FindIndex(args, a => a.Equals("--export-fbx", StringComparison.OrdinalIgnoreCase));
        if (exportAt >= 0)
        {
            if (exportAt + 2 >= args.Length) { Usage(); return 2; }
            int outAt = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase));
            string exportDir = outAt >= 0 && outAt + 1 < args.Length ? args[outAt + 1] : Path.Combine(AppContext.BaseDirectory, "exports");
            return StaticMeshExport.Run(args[exportAt + 1], args[exportAt + 2], exportDir);
        }

        int dumpAt = Array.FindIndex(args, a => a.Equals("--dump-export", StringComparison.OrdinalIgnoreCase));
        if (dumpAt >= 0)
        {
            if (dumpAt + 2 >= args.Length) { Usage(); return 2; }
            int outAt = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase));
            string dumpDir = outAt >= 0 && outAt + 1 < args.Length ? args[outAt + 1] : Path.Combine(AppContext.BaseDirectory, "dumps");
            return ExportDump.Run(args[dumpAt + 1], args[dumpAt + 2], dumpDir);
        }

        string? folder = null, outPath = null;
        bool recursive = true, skeletal = false, includeEmpty = false, includeBackups = false, decode = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--out": case "-o": outPath = i + 1 < args.Length ? args[++i] : null; break;
                case "--top-only": recursive = false; break;
                case "--skeletal": skeletal = true; break;
                case "--include-empty": includeEmpty = true; break;
                case "--include-backups": includeBackups = true; break;
                case "--decode-static": decode = true; break;
                case "--help": case "-h": case "/?": Usage(); return 0;
                default:
                    if (args[i].StartsWith('-')) { Console.WriteLine($"Unknown option: {args[i]}"); Usage(); return 2; }
                    folder = args[i]; break;
            }
        }

        if (folder == null)
        {
            Console.Write("Folder to scan: ");
            folder = Console.ReadLine()?.Trim().Trim('"');
        }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            Usage();
            return 2;
        }
        folder = Path.GetFullPath(folder);
        outPath ??= Path.Combine(AppContext.BaseDirectory, $"{new DirectoryInfo(folder).Name}_MeshScan.txt");

        var files = Directory.EnumerateFiles(folder, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .ToList();
        int skippedBackups = 0;
        if (!includeBackups)
        {
            int before = files.Count;
            files = files.Where(f => !IsBackupName(f)).ToList();
            skippedBackups = before - files.Count;
        }
        Console.WriteLine($"Scanning {files.Count} package(s) in {folder}{(recursive ? " (recursive)" : "")}{(skippedBackups > 0 ? $", skipped {skippedBackups} bak/copy file(s)" : "")} ...");

        var sw = Stopwatch.StartNew();
        var results = new ConcurrentBag<Result>();
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f =>
        {
            results.Add(ScanOne(f, skeletal, decode));
            int n = Interlocked.Increment(ref done);
            if (n % 200 == 0 || n == files.Count) Console.WriteLine($"  {n}/{files.Count}");
        });

        var ordered = results.OrderBy(r => Path.GetRelativePath(folder, r.File), StringComparer.OrdinalIgnoreCase).ToList();
        WriteReport(outPath, folder, version, ordered, skeletal, includeEmpty, skippedBackups, decode, sw.Elapsed);

        int meshes = ordered.Sum(r => r.Meshes.Count);
        int failed = ordered.Count(r => r.Error != null);
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s: {meshes} mesh(es) in {ordered.Count(r => r.Meshes.Count > 0)} package(s); {failed} package(s) failed to read.");
        if (decode)
        {
            var tried = ordered.SelectMany(r => r.Meshes).Where(m => m.Decoded).ToList();
            int bad = tried.Count(m => m.DecodeError != null);
            Console.WriteLine($"StaticMesh decode: {tried.Count - bad}/{tried.Count} OK, {bad} failed, {tried.Count(m => m.Notes is { Count: > 0 })} with notes.");
        }
        Console.WriteLine($"Report: {outPath}");
        return 0;
    }

    static Result ScanOne(string file, bool skeletal, bool decode)
    {
        long bytes = 0;
        try
        {
            bytes = new FileInfo(file).Length;
            var pkg = Package.Open(file);
            var hits = new List<MeshHit>();
            foreach (var e in pkg.Exports)
            {
                string cls = pkg.ClassOf(e);
                if (StaticClasses.Contains(cls) || (skeletal && SkeletalClasses.Contains(cls)))
                {
                    var hit = new MeshHit(cls, e.ObjectName, pkg.PathOf(e), e.SerialSize);
                    if (decode && StaticClasses.Contains(cls)) hit = TryDecode(pkg, e, hit);
                    hits.Add(hit);
                }
            }
            hits.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return new Result(file, bytes, $"v{pkg.FileVersion}/L{pkg.LicenseeVersion}", pkg.ChunkSource, hits, null, File.GetLastWriteTime(file).Date == new DateTime(2024, 3, 14));
        }
        catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return new Result(file, bytes, "?", "?", new List<MeshHit>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    static MeshHit TryDecode(Package pkg, ExportEntry e, MeshHit hit)
    {
        try
        {
            var mesh = StaticMesh.Read(pkg, e);
            return hit with { Decoded = true, Notes = mesh.Notes, Facts = mesh.Facts };
        }
        catch (Exception ex) when (ex is PackageFormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return hit with { Decoded = true, DecodeError = $"{(ex is PackageFormatException ? "" : ex.GetType().Name + ": ")}{ex.Message}" };
        }
    }

    /// <summary>Error text with numbers and quoted names replaced, so failures group by cause.</summary>
    static string FailureKind(string message) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(message, "'[^']*'", "'…'"), @"-?(0x[0-9A-Fa-f]+|\d+(\.\d+)?)", "#");

    static void WriteReport(string outPath, string folder, string version, List<Result> results, bool skeletal, bool includeEmpty, int skippedBackups, bool decode, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        var ok = results.Where(r => r.Error == null).ToList();
        var withMeshes = ok.Where(r => r.Meshes.Count > 0).ToList();
        int staticCount = ok.Sum(r => r.Meshes.Count(m => !SkeletalClasses.Contains(m.Class)));
        int skelCount = ok.Sum(r => r.Meshes.Count(m => SkeletalClasses.Contains(m.Class)));

        sb.AppendLine($"UpkMeshScan v{version} — mesh scan report");
        sb.AppendLine($"Folder   : {folder}");
        sb.AppendLine($"Date     : {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Packages : {results.Count} scanned, {ok.Count} read OK, {results.Count - ok.Count} failed ({elapsed.TotalSeconds:F1}s){(skippedBackups > 0 ? $"; {skippedBackups} bak/copy file(s) skipped" : "")}");
        sb.AppendLine($"Meshes   : {staticCount} static{(skeletal ? $", {skelCount} skeletal" : "")} in {withMeshes.Count} package(s)");
        sb.AppendLine($"Versions : {string.Join(", ", ok.GroupBy(r => r.Version).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}"))}");
        sb.AppendLine($"Chunks   : {string.Join(", ", ok.GroupBy(r => r.ChunkSource).Select(g => $"{g.Key} x{g.Count()}"))}");
        if (decode)
        {
            var tried = ok.SelectMany(r => r.Meshes.Where(m => m.Decoded).Select(m => (r, m))).ToList();
            var bad = tried.Where(x => x.m.DecodeError != null).ToList();
            var noted = tried.Where(x => x.m.Notes is { Count: > 0 }).ToList();
            sb.AppendLine($"Decode   : {tried.Count - bad.Count}/{tried.Count} StaticMesh exports decoded OK, {bad.Count} failed, {noted.Count} with notes");
            sb.AppendLine(new string('=', 100));
            sb.AppendLine();
            sb.AppendLine("DECODE FAILURES BY KIND (up to 5 examples each)");
            foreach (var g in bad.GroupBy(x => FailureKind(x.m.DecodeError!)).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"  {g.Count(),6}  {g.Key}");
                foreach (var (r, m) in g.OrderBy(x => x.m.Size).Take(5))
                    sb.AppendLine($"            {Path.GetRelativePath(folder, r.File)} :: {m.Path} ({m.Size:N0} B) — {m.DecodeError}");
            }
            sb.AppendLine();
            sb.AppendLine("LAYOUT FACTS (meshes in stock-dated packages / all meshes)");
            foreach (var g in tried.Where(x => x.m.Facts != null).SelectMany(x => x.m.Facts!.Select(f => (f, x.r))).GroupBy(x => System.Text.RegularExpressions.Regex.Replace(x.f, @"tail after LOD 0: \d+", "tail after LOD 0: N")).OrderBy(g => g.Key))
                sb.AppendLine($"  {g.Count(x => x.r.Stock),6} / {g.Count(),6}  {g.Key}");
            sb.AppendLine();
            sb.AppendLine("NOTES BY KIND (decoded, but a soft cross-check differed)");
            foreach (var g in noted.SelectMany(x => x.m.Notes!.Select(n => (x.r, x.m, n))).GroupBy(x => FailureKind(x.n)).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"  {g.Count(),6}  {g.Key}");
                var pkgs = g.Select(x => Path.GetRelativePath(folder, x.r.File)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                sb.AppendLine($"            in {pkgs.Count} package(s): {string.Join(", ", pkgs.Take(12))}{(pkgs.Count > 12 ? ", …" : "")}");
                foreach (var (r, m, n) in g.Take(3))
                    sb.AppendLine($"            {Path.GetRelativePath(folder, r.File)} :: {m.Path} — {n}");
            }
        }
        sb.AppendLine(new string('=', 100));

        foreach (var r in includeEmpty ? ok : withMeshes)
        {
            sb.AppendLine();
            sb.AppendLine($"{Path.GetRelativePath(folder, r.File)}   ({r.Meshes.Count} mesh{(r.Meshes.Count == 1 ? "" : "es")})");
            if (r.Meshes.Count == 0) continue;
            string Label(MeshHit m) =>
                (SkeletalClasses.Contains(m.Class) ? "[Skel] " : m.Class.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ? "" : $"[{m.Class}] ") + m.Name;
            int w = Math.Min(64, r.Meshes.Max(m => Label(m).Length));
            foreach (var m in r.Meshes)
            {
                string path = m.Path.Equals(m.Name, StringComparison.Ordinal) ? "" : $"  {m.Path}";
                sb.AppendLine($"    {Label(m).PadRight(w)}  {m.Size,12:N0} B{path}");
            }
        }

        var failed = results.Where(r => r.Error != null).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 100));
            sb.AppendLine($"FAILED TO READ ({failed.Count})");
            foreach (var r in failed) sb.AppendLine($"    {Path.GetRelativePath(folder, r.File)}  —  {r.Error}");
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Backups and copies of packages ("Foo - Copy.upk", "Foo_bak.upk") sit beside the live files; skip them by default.</summary>
    internal static bool IsBackupName(string path)
    {
        string name = Path.GetFileName(path);
        return name.Contains("bak", StringComparison.OrdinalIgnoreCase) || name.Contains("copy", StringComparison.OrdinalIgnoreCase);
    }

    static void Usage()
    {
        Console.WriteLine("""

        Usage: UpkMeshScan <folder> [options]
          Scans every .upk/.umap under <folder> and lists the static meshes each one contains.

          --out <file>       Report path (default: <FolderName>_MeshScan.txt next to the exe)
          --top-only         Don't recurse into subfolders
          --skeletal         Also list SkeletalMesh exports (tagged [Skel])
          --include-empty    Also list packages that contain no meshes
          --include-backups  Also scan files with "bak" or "copy" in the name (skipped by default)
          --decode-static    Also run the StaticMesh parser on every static mesh (writes nothing) and
                             report which ones decode, grouped by failure reason

        Usage: UpkMeshScan --dump-export <package.upk> <export-name-or-path> [--out <folder>]
          Writes that export's raw bytes (.bin) and an annotated dump (.txt: property tags, then
          hex of the native data) to <folder> (default: dumps\ next to the exe). Read-only.

        Usage: UpkMeshScan --export-fbx <package.upk> <staticmesh-name-or-path> [--out <folder>]
          Writes LOD 0 of that StaticMesh to <folder>\<name>.fbx (default: exports\ next to the exe),
          one part per material section. Read-only on the package.

        Usage: UpkMeshScan --import-fbx <package.upk> <staticmesh> <file.fbx> [--dry-run [--out <folder>]]
          Replaces LOD 0 of that StaticMesh with the FBX (sections matched by material name).
          Builds and verifies the new package in memory first. With --dry-run it's written to
          <folder> (default: import_out\ next to the exe) and the game file is untouched.
          Without it: <package>.upk.bak is created if missing (never overwritten), the new package
          is written to a temp file, verified again from disk, then replaces the live file.
          Collision for the imported mesh is removed (empty collision tree) for now.

        Usage: UpkMeshScan --export-textures <package.upk> [name-filter] [--out <folder>]
          Writes each Texture2D's largest mip stored inside the package as .dds (DXT data kept as is;
          default folder textures\<package>\ next to the exe). Stock textures only carry small mips
          (mostly 64x64) in the package; full size is in .tfc files, which aren't read.
          --export-fbx also writes the mesh's material textures and links them in the FBX.
        Usage: UpkMeshScan --set-property <package.upk> <export-path> <Name=Value> [...] [--dry-run]
          Changes float/int properties that already exist in an export (e.g. fog density), with the
          same .bak / verified temp / swap workflow as --import-fbx. --revert undoes it.
        Usage: UpkMeshScan --zone-placeholders <folder> <tile-prefix> <library.upk> [--out file.fbx]
                           [--min-height 400] [--min-footprint 200] [--inset 0.90] [--skip tree,...] [--only mesh,...]
          Low-poly footprint prisms for every building-sized placed mesh in the tiles (e.g. prefix UES_Static_,
          library SCS__OpDailyBugleRegionBand_SF.upk), at their real world positions, into one FBX
          (+ .txt list). Read-only.
        Usage: UpkMeshScan --add-sky-placeholders <package.upk> <placeholders.fbx> [--gray 0.03] [--dry-run]
                           [--exclude-box minX,minY,maxX,maxY ...] [--ground-z -40 [--ground-margin 4000]]
                           [--shrink F]  (scale each piece: 0.8/0.9 = 0.889 turns 90% placeholders into 80%)
                           [--add-fbx more.fbx ...]  (merged as is, after exclusion and shrink)
                           [--color R,G,B]  (flat colour instead of --gray, linear 0..1)
                           [--ground-box minX,minY,maxX,maxY]  (exact ground extent instead of the margin)
                           [--ground-material package.object [--ground-uv 2304]]  (with "none": the plane uses that
                               MaterialInstanceConstant through new imports, UVs tiling every 2304 units)
                           [--ground-grid N]  (split each ground box into cells of at most N units, so translucent
                               water is fogged evenly; big boxes otherwise show as bands)
          <placeholders.fbx> may be "none" (with --ground-z and --ground-box): only the ground plane, for zones
          whose cells are placed at run time.
          Adds the FBX's geometry (world space, e.g. from --zone-placeholders) to the zone's sky sphere
          mesh as a second section with a new flat-grey copy of the sky material. The package is rebuilt
          to hold the new material; everything else stays byte-identical. Same .bak / verify / swap.
        Usage: UpkMeshScan --build-zone <zone> <game-folder> [--walls facade|grey] [--dry-run]
          Rebuilds a zone's main level from stock with its whole placeholder recipe (zones: Hightown). Every step
          runs on a scratch copy and verifies itself; the result is written once (.bak / verified temp / swap), so
          --undo takes the whole rebuild back. Walls: the zone's facade texture (default) or flat grey.
        Usage: UpkMeshScan --add-cell-placeholders <package.upk> <placeholders.fbx> [--min-draw 3500] [--cell 2304]
                           [--exclude-box ...] [--shrink F] [--add-fbx ...] [--ground-z -40] [--gray 0.03] [--dry-run]
          Placeholders as one placed object per map cell with MinDrawDistance (hidden near the camera),
          built from the package's .bak with the live file's other edits (e.g. fog) carried over.
        Usage: UpkMeshScan --test-rebuild <package.upk> [export-to-copy]
          Self-test: rebuild the package (optionally with one export copied) and verify. Writes nothing.
        Usage: UpkMeshScan --list-exports <package.upk> [class-filter]
          Lists exports (index, class, size, path), optionally only classes containing the filter.
        Usage: UpkMeshScan --texture-info <package.upk> [name-filter]
          Lists textures: size, format, cache, and where each mip's data is stored.

        Usage: UpkMeshScan --revert <package.upk>
          Restores <package>.upk from <package>.upk.bak (verified; the .bak is kept).

        Usage: UpkMeshScan --verify-import-roundtrip <package.upk> <staticmesh>
          Self-test, writes nothing to the package folder: export -> FBX -> import, compared with
          the original, plus the package writer and verifier run in memory.
        """);
    }

}
