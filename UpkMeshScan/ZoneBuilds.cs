using System.Diagnostics;

namespace UpkMeshScan;

/// <summary>
/// --build-zone: rebuilds a zone's main level from stock with its whole placeholder recipe, inside the tool (the
/// recipes were bash scripts before: publish/scans/tools/ht_build.sh). Every step runs as a dry run on a scratch copy
/// of the stock file (the .bak, or the live file if it has never been modified), each writer verifying its own
/// output as usual; the finished package is then written once through MeshImport.WriteLive (.bak / verified temp /
/// swap), so a whole rebuild is one undo step. Inputs that aren't in the game (placeholder FBX, facade textures)
/// ship in ZoneData/&lt;Zone&gt; next to the exe.
/// </summary>
static class ZoneBuilds
{
    public enum Walls { Facade, Grey }

    public sealed record Zone(string Name, string Package, string Description, Func<Walls, List<string[]>> Steps, bool HasFacade = true);

    const string Target = "@target", Data = "@data/";
    const string Library = "@lib:";                                     // a game package, read from its .bak if one exists

    public static readonly Zone[] Zones =
    [
        new("Hightown", "Madripoor_HighTown_B.upk",
            "Upper Madripoor / Hightown: sky dome, building boxes (MinDrawDistance 3500), always-drawn ground slabs, " +
            "two water layers with a hole over the GameCenter stairwell, night sky; textured: night facade walls + Kurt's baked ground plane, or all grey (with ground slabs).",
            HightownSteps),
        new("OdinsPalace", "Asgard_Hub_B.upk",
            "Odin's Palace (Kurse operation): full starfield sky (backdrop copies for the sides and top), building boxes " +
            "(64-unit raster, MinDrawDistance 3500), always-drawn slabs under the floors and platforms, the real Bifrost deck at a distance. Grey walls only so far.",
            OdinsPalaceSteps, HasFacade: false),
    ];

    /// <summary>
    /// Odin's Palace (tuned in-game 2026-09-25). Sky: the starfield backdrop only covered ~130 degrees below the horizon
    /// (--sky-coverage); rotated copies (yaw 90/180/270, then roll 180 at four yaws) cover every direction. Boxes: the
    /// region isn't centred (the server log's area bounds = tile bounds), so no offset. The level has no procedural sky
    /// dome, so the sky sphere mesh and its material are copied from stock Brooklyn_Docks_A only as templates (layout,
    /// grey copy) and the components copy the level's own waterfall component.
    /// </summary>
    static List<string[]> OdinsPalaceSteps(Walls walls)
    {
        const string actor = "theworld.persistentlevel.staticmeshcollectionactor_0";
        const string brooklyn = Library + "Brooklyn_Docks_A.upk";
        const string lib = Library + "SCS__DailyGAsgardINSTRegionL40_SF.upk";
        const string bridgeA = Library + "Asgardia_Bridge_EXT_INS_A.upk", bridgeB = Library + "Asgardia_Bridge_EXT_INS_B.upk";
        const string deck = "rainbowbridge,rainbowbridgeicecover_polysurface4,rainbowbridgeicecover_polysurface5";
        return
        [
            ["--add-component-copies", Target, actor + ".sma_starfield_a_smc_15", "--yaw", "90,180,270,0:0:180,90:0:180,180:0:180,270:0:180"],
            ["--copy-export", brooklyn, "maptemplates.sky.m_procedural_sky_daytime", Target],
            ["--copy-export", brooklyn, "maptemplates.sky.sm_skysphere", Target, "--cut", "bodysetup"],
            // The Bifrost deck as real geometry (the tiles' ice cover + rainbow bridge segments; ice_water_mat and its
            // textures are already in the stock level): meshes and the rainbow material copied in, the tiles' 11
            // placements recreated 2 under the real ones, drawn beyond 3500.
            ["--copy-export", bridgeB, "asgard.instance.rainbowbridgeicecover_polysurface4", Target, "--cut", "bodysetup"],
            ["--copy-export", bridgeA, "asgard.instance.rainbowbridgeicecover_polysurface5", Target, "--cut", "bodysetup"],
            ["--copy-export", lib, "asgard.rainbowbridge", Target, "--cut", "bodysetup"],
            ["--copy-export", lib, "asgard.mat_rainbowbridge_dead", Target],
            // Floors, platforms and the bridge: slabs from the cells' height maps, each 8 under its own surface and 192 thick,
            // drawn at every distance (groundboxes.py band -200..300, multi-level tolerance 64). Kurt's Blender edit, which
            // also removes the bridge-corridor slabs (the walkable strip is ~3x the deck's width; the real deck is copied in).
            ["--add-cell-placeholders", Target, Data + "raster.fbx", "--always-fbx", Data + "ground.fbx", "--from-live",
                "--component-template", actor + ".staticmeshactor_smc_0", "--min-draw", "3500"],
            ["--add-mesh-instances", Target, bridgeA, deck, "--template", actor + ".staticmeshactor_smc_0", "--min-draw", "3500", "--z-offset", "-2"],
            ["--add-mesh-instances", Target, bridgeB, deck, "--template", actor + ".staticmeshactor_smc_0", "--min-draw", "3500", "--z-offset", "-2"],
        ];
    }

    /// <summary>The Hightown recipe (tuned in-game 2026-09-25; the reasons are in the ht_build.sh comments).</summary>
    static List<string[]> HightownSteps(Walls walls)
    {
        const string actor = "theworld.persistentlevel.staticmeshcollectionactor_0";
        const string lib = Library + "SCS__DailyRHighTownInvasionRegionL30_SF.upk";
        const string water = "madripoor_hitown_floortiles.madripoor_hitown_water";
        const string sf = "madripoor_hitown_buildings.madripoor_hitown_storefront_a";
        const string template = "maptemplates.sky.t_udk_sky_cloudmask01";
        // Tile coordinates -> in-game: the server centres the region (district + Jumbotron Overlook sub-area + sewers).
        const string shift = "5208,-15512";
        // City water (tile -15000,-15000..40000,41000 + shift) in 4 boxes around the GameCenter stairwell hole, at -80
        // (5 under the stock water at -75), plus the same boxes 5 lower at -85 (two translucent layers read as depth).
        string[] city = ["-9792,-30512,45208,-4440", "-9792,-3384,45208,25488", "-9792,-4440,7064,-3384", "8120,-4440,45208,-3384"];
        string boxes = string.Join(';', city.Concat(city.Select(b => b + ",-85")));

        var steps = new List<string[]>
        {
            new[] { "--copy-export", Library + "Brooklyn_Docks_A.upk", actor, Target, "--cut", "bodysetup" },
            new[] { "--add-level-actor", Target, actor },
        };
        if (walls == Walls.Facade)
        {
            steps.Add(["--import-texture", Target, template, "ht_facade_diff", Data + "ht_facade_diff.dds"]);
            steps.Add(["--import-texture", Target, template, "ht_facade_spec", Data + "ht_facade_spec.dds"]);
            steps.Add(["--import-texture", Target, template, "ht_flat_nrml", Data + "ht_flat_nrml.dds"]);
            steps.Add(["--copy-export", lib, sf + "_mat", Target, "--cut", "physmaterial", "--rename", "ht_facade_mat",
                "--replace-ref", sf + "_diff=maptemplates.sky.ht_facade_diff", "--replace-ref", sf + "_spec=maptemplates.sky.ht_facade_spec",
                "--replace-ref", sf + "_nrml=maptemplates.sky.ht_flat_nrml"]);
            // Ground: Kurt's baked plane (the real streets, alpha where there's no ground) with a copy of the library's
            // masked pier-edge material (envbaseshaderv3_masked, diffuse only) pointing at the baked texture (DXT1 with 1-bit alpha at the 1/3 mask clip: half DXT5's size, same cut-out; RGB x0.8
            // from Kurt's TopDown_D.png: "darken 20%", 2026-09-25).
            steps.Add(["--import-texture", Target, template, "ht_groundplane_diff", Data + "ht_groundplane_diff.dds"]);
            steps.Add(["--copy-export", lib, "brooklyn_pieredge_a.brooklyn_pieredge_mat", Target, "--cut", "physmaterial", "--rename", "ht_groundplane_mat",
                "--replace-ref", "brooklyn_pieredge_a.brooklyn_pieredge_diff_a=maptemplates.sky.ht_groundplane_diff"]);
        }
        var cells = new List<string> { "--add-cell-placeholders", Target, Data + "raster.fbx", "--from-live", "--offset", shift, "--min-draw", "3500" };
        // Textured: the baked ground plane at the old stock-water height (-75: under the real ground, over our water layers)
        // replaces the grey slabs, which would sit on top of it. Grey: the slabs.
        if (walls == Walls.Facade) cells.AddRange(["--wall-material", "madripoor_hitown_buildings.ht_facade_mat", "--wall-uv", "512",
            "--textured-fbx", Data + "groundplane.fbx", "--textured-material", "brooklyn_pieredge_a.ht_groundplane_mat", "--textured-z", "-75"]);
        else cells.AddRange(["--always-fbx", Data + "ground.fbx"]);
        steps.Add([.. cells]);
        steps.Add(["--copy-export", lib, water, Target, "--cut", "physmaterial"]);
        steps.Add(["--add-sky-placeholders", Target, "none", "--ground-z", "-80", "--ground-box", boxes, "--ground-material", water, "--sky-drop", "0.2"]);
        steps.Add(["--set-property", Target, "maptemplates.sky.m_procedural_sky_daytime", "param:horizoncolor=0.0312,0.0857,0.1355",
            "param:zenithcolor=0,0.0312,0.0829", "param:rimcolor=0.5,0.3688,0.1631", "param:sun=5,3,1", "param:cloudbrightness=0.5"]);
        return steps;
    }

    public static Zone? Find(string name) => Zones.FirstOrDefault(z => z.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool GameRunning() => Process.GetProcessesByName("MarvelHeroesOmega").Length > 0;

    public static int Build(string zoneName, string gameFolder, Walls walls, bool dryRun)
    {
        var zone = Find(zoneName);
        if (zone == null) { Console.WriteLine($"No zone recipe '{zoneName}'. Known: {string.Join(", ", Zones.Select(z => z.Name))}"); return 2; }
        string live = Path.Combine(gameFolder, zone.Package);
        if (!File.Exists(live)) { Console.WriteLine($"{live} not found."); return 2; }
        string dataDir = Path.Combine(AppContext.BaseDirectory, "ZoneData", zone.Name);
        if (!Directory.Exists(dataDir)) { Console.WriteLine($"Zone data folder missing: {dataDir}"); return 2; }
        if (!dryRun && GameRunning()) { Console.WriteLine("The game is running; close it first. Nothing written."); return 1; }

        string stock = File.Exists(live + ".bak") ? live + ".bak" : live;
        string work = Path.Combine(Path.GetTempPath(), "UpkMeshScan_zone", zone.Name);
        Directory.CreateDirectory(work);
        string workFile = Path.Combine(work, zone.Package);
        File.Copy(stock, workFile, overwrite: true);
        string outFile = Path.Combine(AppContext.BaseDirectory, "import_out", zone.Package);
        if (walls == Walls.Facade && !zone.HasFacade) { Console.WriteLine($"  {zone.Name} has no facade yet: grey walls."); walls = Walls.Grey; }
        var steps = zone.Steps(walls);
        Console.WriteLine($"Build zone {zone.Name} ({zone.Package}), walls {walls.ToString().ToLowerInvariant()}, from {(stock == live ? "the live file (no .bak yet: stock)" : "its .bak (stock)")}{(dryRun ? "  [dry run]" : "")}");

        string Resolve(string a) =>
            a == Target ? workFile
            : a.StartsWith(Data) ? Path.Combine(dataDir, a[Data.Length..])
            : a.StartsWith(Library) ? (File.Exists(Path.Combine(gameFolder, a[Library.Length..]) + ".bak") ? Path.Combine(gameFolder, a[Library.Length..]) + ".bak" : Path.Combine(gameFolder, a[Library.Length..]))
            : a;

        for (int n = 0; n < steps.Count; n++)
        {
            string[] args = [.. steps[n].Select(Resolve), "--dry-run"];
            Console.WriteLine();
            Console.WriteLine($"== step {n + 1}/{steps.Count}: {steps[n][0]}");
            if (File.Exists(outFile)) File.Delete(outFile);
            int rc = Program.Run(args, "");
            if (rc != 0 || !File.Exists(outFile)) { Console.WriteLine($"Step {n + 1} failed (exit {rc}); nothing written to the game."); return 1; }
            File.Copy(outFile, workFile, overwrite: true);
        }

        byte[] built = File.ReadAllBytes(workFile);
        Console.WriteLine();
        Console.WriteLine($"All {steps.Count} steps passed: {built.Length:N0} bytes.");
        if (dryRun)
        {
            File.Copy(workFile, outFile, overwrite: true);
            Console.WriteLine($"  dry run: wrote {outFile} (game folder untouched)");
            return 0;
        }
        History.Label = $"build-zone {zone.Name} walls={walls.ToString().ToLowerInvariant()}";
        return MeshImport.WriteLive(live, built, bytes => bytes.AsSpan().SequenceEqual(built) ? [] : ["the file on disk isn't the built package"]) ? 0 : 1;
    }
}
