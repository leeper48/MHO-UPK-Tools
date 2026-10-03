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
            case "--model-tab-test":
            {
                // --model-tab-test <mod> <mff model> <package file> (scratch MHO_EXTMM_HOME only: it saves the mod): the editor's
                // Model tab driven through its own controls; exit 0 when every check passes.
                if (rest.Count < 4) { Console.WriteLine("--model-tab-test <mod> <mff model> <package file>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library): this test saves the mod"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelTabTest(rest[1], rest[2], rest[3], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            default: return null;
        }
    }
}
