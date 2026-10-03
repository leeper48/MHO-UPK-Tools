namespace MhoExtendedModManager;

static partial class Program
{
    /// <summary>
    /// The Model tab's engine from the command line (the MFF model importer, moved in 2026-10-03). Null when
    /// <paramref name="cmd"/> isn't one of these. Every one writes only into the folder it's given (never the game, the clean
    /// stock folder or the MFF source: the engine's Protected check).
    /// </summary>
    static int? ModelCommand(string cmd, List<string> rest)
    {
        switch (cmd)
        {
            case "--model-build":
            {
                // --model-build <mff model> <package name or file> <out folder> [--map bonemap.json]: the importer's --encode-mff
                // (MFF_HAIR, MFF_CAPE, MFF_ANIM_FBX, MFF_MODEL_FBX … as there; MHO_MFF_SOURCE = the MFF folder, else Settings)
                if (rest.Count < 4) { Console.WriteLine("--model-build <mff model> <package name or file> <out folder> [--map bonemap.json]"); return 1; }
                int mi = rest.IndexOf("--map");
                string? map = mi > 0 && mi + 1 < rest.Count ? rest[mi + 1] : null;
                MhoMffImporter.Settings.Reset();
                try
                {
                    string package = MhoMffImporter.BasePackage.Resolve(rest[2], true);
                    var result = MhoMffImporter.ImportBuild.Run(rest[1], package, rest[3], MhoMffImporter.ImportOptions.FromEnvironment(null, map, null), Console.WriteLine);
                    return result == null ? 1 : 0;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { Console.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            default: return null;
        }
    }
}
