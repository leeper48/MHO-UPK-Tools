using System.Runtime.InteropServices;
using AnimExportCli.Animation;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;
using AnimExportCli.Packages;
using AnimExportCli.UI;

namespace AnimExportCli;

internal static class Program
{
    // Bump <Version> in the .csproj on every meaningful change, not this.
    // Reflection reads whatever the build actually was, so it can't drift
    // out of sync with reality the way a manually-maintained string could.
    internal static string Version => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    // WinForms needs STA; harmless for plain console/CLI use.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            RunGui();
            return 0;
        }

        // Built as a WinExe now, precisely so a plain double-click opens the
        // GUI with no console window flashing up behind it. That also means
        // Windows never gives this process a console of its own — so for the
        // CLI path specifically, attach to whichever console actually
        // launched it (a terminal), then re-wire Console's streams to it:
        // .NET may otherwise still be holding onto the handles it had before
        // the attach, and output would just silently vanish instead of
        // showing up where the person is looking. If there's no parent
        // console at all (e.g. launched from a shortcut with arguments),
        // AttachConsole fails harmlessly and output has nowhere to go — an
        // acceptable trade for never showing a console on a normal launch.
        if (AttachConsole(AttachParentProcess))
        {
            // Also fixes a real regression from adding this attach step in
            // the first place: a StreamWriter with no encoding specified
            // fell back to something that mangled every non-ASCII character
            // this tool prints (the em dashes and degree signs came out as
            // garbage). Setting the console's own output encoding to UTF-8
            // before wrapping the streams in it keeps both sides consistent.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }

        Console.WriteLine($"AnimExportCli v{Version}");

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        string upkPath = args[0];
        if (!File.Exists(upkPath))
        {
            Console.Error.WriteLine($"File not found: {upkPath}");
            return 1;
        }

        string? outDir = ReadOption(args, "--out");
        string? meshArg = ReadOption(args, "--mesh");
        string? dumpFilter = ReadOption(args, "--dump");
        string? dumpBoneFilter = ReadOption(args, "--dump-bone");
        string? animFilter = ReadOption(args, "--anim");
        bool listOnly = HasFlag(args, "--list");
        bool verifyRoundtrip = HasFlag(args, "--verify-roundtrip");
        bool verifyEncoder = HasFlag(args, "--verify-encoder");

        Package package;
        try
        {
            package = Package.Open(upkPath);
        }
        catch (InvalidPackageException ex)
        {
            Console.Error.WriteLine($"Could not read '{upkPath}' as a package: {ex.Message}");
            return 1;
        }

        IReadOnlyList<ExportWorkflow.MeshEntry> meshes = ExportWorkflow.ListMeshes(package);
        if (meshes.Count == 0)
        {
            Console.Error.WriteLine("No readable SkeletalMesh objects in this package.");
            return 1;
        }

        Console.WriteLine($"Skeletal meshes in {Path.GetFileName(upkPath)}:");
        for (int i = 0; i < meshes.Count; i++)
            Console.WriteLine($"  [{i}] {meshes[i]}");

        if (listOnly) return 0;

        if (!TryPickMesh(meshes, meshArg, out int chosen))
        {
            Console.Error.WriteLine(meshArg is not null ? $"'{meshArg}' does not match a mesh index or name above." : "Not a valid selection.");
            return 1;
        }

        SkeletalMesh selectedMesh = meshes[chosen].Mesh;
        string outputDirectory = outDir ?? ExportWorkflow.DefaultOutputDirectory(upkPath, selectedMesh.Name);

        List<AnimObjectReader.AnimSetInfo> animSets = AnimObjectReader.FindAnimSets(package).ToList();

        if (dumpFilter is not null)
        {
            bool foundAny = false;
            foreach (AnimObjectReader.AnimSetInfo animSet in animSets)
            {
                foreach (ObjectReference sequenceRef in animSet.Sequences)
                {
                    if (!sequenceRef.IsExport) continue;
                    if (dumpFilter.Length > 0 &&
                        !AnimObjectReader.GetSequenceDisplayName(package, sequenceRef.ExportIndex).Contains(dumpFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foundAny = true;
                    Console.WriteLine();
                    AnimObjectReader.DumpSequenceDiagnostics(package, sequenceRef.ExportIndex, animSet.TrackBoneNames, Console.Out, dumpBoneFilter);
                }
            }

            if (!foundAny) Console.WriteLine($"No sequence matching '{dumpFilter}' found.");
            return 0;
        }

        if (animSets.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "This package holds no AnimSet objects. This game usually keeps a character's " +
                "animations in a separate package from its mesh, linked only by sharing the same " +
                "bone names — point this tool at that package instead.");
            return 0;
        }

        if (verifyRoundtrip)
        {
            SkeletalMeshLod? verifyLod = selectedMesh.HighestDetail;
            if (verifyLod is null || !verifyLod.HasGeometry)
            {
                Console.Error.WriteLine($"'{selectedMesh.Name}' has no exportable geometry (no usable LOD).");
                return 1;
            }

            var boneNames = new HashSet<string>(selectedMesh.Bones.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
            int checkedCount = 0;

            foreach (AnimObjectReader.AnimSetInfo animSet in animSets)
            {
                if (!animSet.TrackBoneNames.Any(n => n.Length > 0 && boneNames.Contains(n))) continue;

                foreach (ObjectReference sequenceRef in animSet.Sequences)
                {
                    if (!sequenceRef.IsExport) continue;

                    if (!string.IsNullOrEmpty(animFilter) &&
                        !AnimObjectReader.GetSequenceDisplayName(package, sequenceRef.ExportIndex).Contains(animFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    BoneAnimation? animation = AnimObjectReader.TryRead(package, sequenceRef.ExportIndex, animSet.TrackBoneNames);
                    if (animation is null) continue;

                    checkedCount++;
                    RoundTripVerifier.Result result;
                    try
                    {
                        result = RoundTripVerifier.Verify(selectedMesh, verifyLod, animation);
                    }
                    catch (Exception ex) when (ex is MeshExportException or AnimationImportException)
                    {
                        Console.Error.WriteLine($"{animation.Name}: verify failed — {ex.Message}");
                        continue;
                    }

                    string status = result.Clean ? "clean" : "DRIFTED";
                    string missing = result.MissingBones.Count > 0
                        ? $" [missing: {string.Join(", ", result.MissingBones.Take(5))}{(result.MissingBones.Count > 5 ? $", +{result.MissingBones.Count - 5} more" : string.Empty)}]"
                        : string.Empty;
                    string keyCountOnly = result.KeyCountMismatchBones.Count > 0
                        ? $" [re-keyed but matches: {string.Join(", ", result.KeyCountMismatchBones.Take(5))}{(result.KeyCountMismatchBones.Count > 5 ? $", +{result.KeyCountMismatchBones.Count - 5} more" : string.Empty)}]"
                        : string.Empty;
                    Console.WriteLine(
                        $"{animation.Name}: {status} - {result.BoneCount - result.MismatchedBoneCount}/{result.BoneCount} bones matched, " +
                        $"max position error {result.MaxPositionError:F4}, max rotation error {result.MaxRotationDegrees:F3} deg" +
                        (result.WorstBone is not null ? $" (worst: {result.WorstBone})" : string.Empty) + missing + keyCountOnly);
                }
            }

            if (checkedCount == 0) Console.WriteLine("No matching animations found to verify.");
            return 0;
        }

        if (verifyEncoder)
        {
            var boneNames = new HashSet<string>(selectedMesh.Bones.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
            int checkedCount = 0;

            foreach (AnimObjectReader.AnimSetInfo animSet in animSets)
            {
                if (!animSet.TrackBoneNames.Any(n => n.Length > 0 && boneNames.Contains(n))) continue;

                foreach (ObjectReference sequenceRef in animSet.Sequences)
                {
                    if (!sequenceRef.IsExport) continue;

                    if (!string.IsNullOrEmpty(animFilter) &&
                        !AnimObjectReader.GetSequenceDisplayName(package, sequenceRef.ExportIndex).Contains(animFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    BoneAnimation? animation = AnimObjectReader.TryRead(package, sequenceRef.ExportIndex, animSet.TrackBoneNames);
                    if (animation is null) continue;

                    // BoneAnimation doesn't carry NumFrames directly (see TryRead) —
                    // the highest frame actually keyed is a fine stand-in for this
                    // diagnostic, though the real write path will need the sequence's
                    // own declared NumFrames once this goes further than a self-test.
                    float highestFrame = 0f;
                    foreach (BoneTrack track in animation.Tracks.Values)
                    {
                        foreach (BonePositionKey key in track.PositionKeys) highestFrame = Math.Max(highestFrame, key.TimeFrame);
                        foreach (BoneRotationKey key in track.RotationKeys) highestFrame = Math.Max(highestFrame, key.TimeFrame);
                    }
                    int numFrames = (int)highestFrame + 1;

                    checkedCount++;
                    EncoderRoundTripVerifier.Result result = EncoderRoundTripVerifier.Verify(selectedMesh, animSet.TrackBoneNames, animation, numFrames);

                    string status = result.Clean ? "clean" : "DRIFTED";
                    Console.WriteLine(
                        $"{animation.Name}: {status} - {result.TrackCount} tracks, numFrames={numFrames}, " +
                        $"max position error {result.MaxPositionError:F4}, max rotation error {result.MaxRotationDegrees:F3} deg" +
                        (result.WorstBone is not null ? $" (worst: {result.WorstBone})" : string.Empty));
                }
            }

            if (checkedCount == 0) Console.WriteLine("No matching animations found to verify.");
            return 0;
        }

        ExportWorkflow.ExportSummary summary = ExportWorkflow.Export(
            package, selectedMesh, animSets, outputDirectory, animFilter,
            log: s => Console.WriteLine("  " + s),
            logError: s => Console.Error.WriteLine("  " + s));

        Console.WriteLine();
        Console.WriteLine($"{summary.Exported} animation(s) exported to {outputDirectory}");

        if (summary.SkippedNoOverlap > 0)
            Console.WriteLine($"({summary.SkippedNoOverlap} AnimSet(s) shared no bone name with '{selectedMesh.Name}' and were skipped.)");
        if (summary.SkippedNoTracks > 0)
        {
            Console.WriteLine(
                $"({summary.SkippedNoTracks} sequence(s) decoded with no usable bone tracks — likely a " +
                "per-track-compressed sequence, or a compression format this reader doesn't decode.)");
        }
        if (summary.SkippedExportFailed > 0)
            Console.WriteLine($"({summary.SkippedExportFailed} sequence(s) failed to export — see the messages above.)");

        return 0;
    }

    private static void RunGui()
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new MainForm());
    }

    private static bool TryPickMesh(IReadOnlyList<ExportWorkflow.MeshEntry> meshes, string? meshArg, out int index)
    {
        if (meshArg is not null)
        {
            if (int.TryParse(meshArg, out index) && index >= 0 && index < meshes.Count) return true;

            for (int i = 0; i < meshes.Count; i++)
            {
                if (!string.Equals(meshes[i].Name, meshArg, StringComparison.OrdinalIgnoreCase)) continue;
                index = i;
                return true;
            }

            index = -1;
            return false;
        }

        Console.Write($"\nPick a mesh [0-{meshes.Count - 1}]: ");
        string? input = Console.ReadLine();
        return int.TryParse(input, out index) && index >= 0 && index < meshes.Count;
    }

    private static bool HasFlag(string[] args, string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            AnimExportCli

            Exports every animation for one skeletal mesh in a UPK to FBX, one file per
            animation, named "AnimationName.fbx" in a folder named after the mesh. Fully
            standalone: it reads the package format, the mesh, and the animations itself,
            with no dependency on any other tool.

            Run with no arguments to open the GUI instead.

            Usage:
              AnimExportCli <path-to.upk> [--mesh <index-or-name>] [--out <folder>] [--list]

              --mesh   Pick a mesh without the interactive prompt (by its listed index,
                       or by its exact name).
              --out    Where to write the FBX files. Defaults to a new folder named after
                       the mesh, next to the UPK.
              --list   Print the meshes found in the package and exit.
              --dump   Print raw bytes and decoded values for every sequence whose name
                       contains this text (case-insensitive; pass an empty string to dump
                       every sequence found), instead of exporting. For diagnosing a
                       decode that looks wrong.
              --dump-bone
                       Used together with --dump: for any bone whose name contains this
                       text, print its full decoded key sequence (every position and
                       rotation key, with each rotation key's dot product against the one
                       before it) instead of just the first key. For chasing a problem
                       down to specific frames within one bone's track.
              --anim   Only export sequences whose name contains this text
                       (case-insensitive). Skips the rest instead of exporting all of them.
              --verify-roundtrip
                       For each matching animation: bake it to a temporary FBX, read that
                       FBX back, and report how far the result drifted from the original
                       decode. Nothing is written to --out or to any package. For checking
                       the FBX import math in isolation, with no risk to any file.
              --verify-encoder
                       For each matching animation: re-encode its decoded data with the
                       new ACF_None writer, decode the result back through the same
                       decoder that reads real UPKs, and report any drift. Nothing is
                       written to --out or to any package.

            Animations are only found when their AnimSet object lives in this same UPK
            and shares bone names with the chosen mesh — this game usually keeps a
            character's animations in a separate package from its mesh, so you may need
            to point this at more than one file.
            """);
    }
}
