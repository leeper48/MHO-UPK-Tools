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
        new("IndustryCity", "Brooklyn_Docks_A.upk",
            "Industry City (ICP): distant building boxes (Kurt's edit), Kurt's baked ground plane, the zone's water on planes " +
            "(two layers just under the stock level, whose planes are removed from the tiles; AIM Sub pit kept), Kurt's photo sky (no clouds).",
            IndustryCitySteps, HasFacade: false),
        new("OdinsPalace", "Asgard_Hub_B.upk",
            "Odin's Palace (Kurse operation): full starfield sky (backdrop copies for the sides and top), building boxes " +
            "(64-unit raster, MinDrawDistance 3500), always-drawn slabs under the floors and platforms, the real Bifrost deck at a distance. Grey walls only so far.",
            OdinsPalaceSteps, HasFacade: false),
    ];

    /// <summary>
    /// Industry City, ported from icp_build.sh (2026-09-24/25). Cells are laid out at run time and stored at the origin;
    /// the placeholder FBX are already in final positions (IndustryCity_layout.txt). Sky: the Brood skydome material
    /// copied as icp_sky_mat with Kurt's photo, a black "stars" slot and a plain grey mask (clouds are parked).
    /// </summary>
    // ZoneData/IndustryCity/lod_bundle<n>.fbx + lod_bundle<n>_diff.dds (make_mips.py --scale: 1 warehouses 0.45, read too
    // bright next to the real ones at 0.6; 2 sub area, 3 containers, 4 cargo ship 0.6).
    const int LodBundles = 4;

    static List<string[]> IndustryCitySteps(Walls walls)
    {
        const string lib = Library + "SCS__CH0201ShippingYardRegion_SF.upk";
        const string water = "brooklyn_docks_lighting.brooklyn_docks_water_mat";
        const string template = "maptemplates.sky.t_udk_sky_cloudmask01";
        const string brood = "madripoor_broodship_skydome";
        const string sf = "madripoor_hitown_buildings.madripoor_hitown_storefront_a";
        const string skyComp = "theworld.persistentlevel.staticmeshcollectionactor_0.sma_sm_skysphere_smc_16";
        string[] around = ["-100224,-100224,100224,-1152", "-100224,1152,100224,100224", "-100224,-1152,-4608,1152", "0,-1152,100224,1152"];
        // The hole itself gets water at pit level (-210 / -215, under the stock pit water at -205, so no flicker when the
        // tile loads): from a distance the pit is otherwise empty. 10x the hole (Kurt, 2026-09-26), reaching under the main water, so
        // no view through the hole's edge finds the sky.
        string icpWater = string.Join(';', around.Concat(around.Select(b => b + ",-126")).Concat(["-25344,-11520,20736,11520,-210", "-25344,-11520,20736,11520,-215"]));
        return
        [
            // Ground: Kurt's baked plane (from --export-placed, vertex-blended terrain baked top-down, alpha where there's no
            // ground) on a masked copy of the library's pier-edge material, at the old stock-water height -116 (under the
            // docks, over our water). It shows where the real ground isn't drawn: the distance and the pit ring (red test,
            // 2026-09-26). DXT1 1-bit alpha at the 1/3 mask clip, with a full mip chain (make_mips.py --scale 0.7, to match the real ground): with one mip it
            // shimmered from a distance.
            ["--import-texture", Target, template, "icp_groundplane_diff", Data + "icp_groundplane_diff.dds"],
            ["--copy-export", lib, "brooklyn_pieredge_a.brooklyn_pieredge_mat", Target, "--cut", "physmaterial", "--rename", "icp_groundplane_mat",
                "--replace-ref", "brooklyn_pieredge_a.brooklyn_pieredge_diff_a=maptemplates.sky.icp_groundplane_diff"],
            // Building LODs: Kurt's low-poly textured bakes of every --export-placed piece 100+ tall and 100+ long (fences,
            // trees, poles, railings, cables, ladders skipped), in 4 parts, each with its own 2048 atlas (2026-09-26); they
            // replace the grey boxes (all excluded). Drawn like the boxes (MinDrawDistance 3500). Unlit: the bake already has
            // its lighting, and our components have no lightmap, so a lit material left the side away from the sun nearly
            // black. Hightown's emissive storefront material (emissive = diffuse x spec G x EmissiveMult 1) with G full, no
            // specular (R 0) or reflection (B 0), flat normal: the bake shows as it is. Atlases converted with make_mips.py
            // --scale 0.6 (0.7 read a little bright next to the real buildings). --lod-shrink pulls every face 6 units in
            // along its normal and --lod-drop 4 lowers it, so each LOD surface sits just inside the real one (ICP's cells
            // are still drawn past 3500: coincident surfaces z-fought).
            ["--import-texture", Target, template, "icp_lod_emissive_spec", Data + "lod_emissive_spec.dds"],
            ["--import-texture", Target, template, "icp_lod_flat_nrml", Data + "lod_flat_nrml.dds"],
            .. Enumerable.Range(1, LodBundles).SelectMany(n => new[]
            {
                new[] { "--import-texture", Target, template, $"icp_lod_bundle{n}_diff", Data + $"lod_bundle{n}_diff.dds" },
                ["--copy-export", Library + "SCS__DailyRHighTownInvasionRegionL30_SF.upk", sf + "_mat", Target, "--cut", "physmaterial", "--rename", $"icp_lod_bundle{n}_mat",
                    "--replace-ref", sf + $"_diff=maptemplates.sky.icp_lod_bundle{n}_diff", "--replace-ref", sf + "_spec=maptemplates.sky.icp_lod_emissive_spec",
                    "--replace-ref", sf + "_nrml=maptemplates.sky.icp_lod_flat_nrml"],
            }),
            // MinDrawDistance 2500 (3500 left a small gap where the real cells had already streamed out).
            ["--add-cell-placeholders", Target, Data + "raster.fbx", "--from-live", "--min-draw", "2500",
                "--textured-fbx", Data + "groundplane.fbx", "--textured-material", "brooklyn_pieredge_a.icp_groundplane_mat", "--textured-z", "-116",
                .. Enumerable.Range(1, LodBundles).SelectMany(n => new[] { "--lod", Data + $"lod_bundle{n}.fbx=madripoor_hitown_buildings.icp_lod_bundle{n}_mat" }),
                "--exclude-box", "-200000,-200000,200000,200000", "--lod-shrink", "6", "--lod-drop", "4",
                // The random middle block (two rows of 3 cells that swap between seeds): no building LOD there, or a
                // swapped run would show warehouses in the wrong row. The ground plane still covers it.
                "--lod-exclude-box", "2304,-1152,9216,3456"],
            ["--copy-export", lib, water, Target, "--cut", "physmaterial"],
            // Water: the stock harbour water is terrain_flat_filler at -116 in the cells (removed from those tiles with
            // --remove-components, 2026-09-25), so two layers just under it (-121, -126: translucent, reads as depth), with
            // a hole over the two AIM Sub cells, which keep their own pit water at -205.
            // No --sky-drop: the dome maps the photo's bottom rows (v 0.9..1) to 0..6 degrees above the horizon and v 0 to the
            // poles (mirrored top/bottom; colour-band test 2026-09-26), so the city already sits on the horizon and any drop
            // hides it. City size is set in the texture instead: ICP_Sky.dds (4096x2048) = ICP_Sky_photo.dds with its city
            // rows (bottom 10%) squeezed into the bottom 2.5% (about 3 degrees tall), twice around below v 0.55 (small city) and once around above v
            // 0.45 (no repeated cloud streaks where the copies converge toward the pole), crossfaded between; rows above v
            // 0.25 flattened to their average colour (fading back to the photo by 0.40), so nothing pinches at the zenith.
            ["--add-sky-placeholders", Target, "none", "--ground-z", "-121", "--ground-box", icpWater, "--ground-material", water, "--ground-grid", "4608"],
            ["--import-texture", Target, template, "icp_sky_photo", Data + "ICP_Sky.dds"],
            ["--import-texture", Target, template, "icp_black", Data + "icp_black.dds"],
            ["--import-texture", Target, template, "icp_mask", Data + "icp_mask.dds"],
            ["--copy-export", Library + "BroodSpaceship_A.upk", $"{brood}.{brood}_mat", Target, "--rename", "icp_sky_mat",
                "--replace-ref", $"{brood}.{brood}_milkyway_diff=maptemplates.sky.icp_sky_photo",
                "--replace-ref", $"{brood}.{brood}_stars=maptemplates.sky.icp_black",
                "--replace-ref", $"{brood}.{brood}_alpha=maptemplates.sky.icp_mask"],
            ["--set-object", Target, skyComp, "materials[0]", $"{brood}.icp_sky_mat"],
            // The Brood shader samples the photo at 2 x v (the city showed twice: at the horizon and ~45-50 degrees up), so
            // the dome's own UV0 v is halved (0.499, so the horizon row doesn't wrap to the top): the photo spans pole to
            // horizon once (2026-09-26).
            ["--scale-uv", Target, "maptemplates.sky.sm_skysphere", "0", "1,0.499", "--section", "0"],
            // Night harbour mist (2026-09-26; stock: opacity 0.5, start 100, density 0.1, height 764, opposite light blue
            // 138,182,244, inscattering warm 222,218,146): more distance fog (hides the LOD swap and the zone edge), clear
            // around the player (MHO's camera sits high), hugging the water, cooler colours for the night sky.
            ["--set-property", Target, "theworld.persistentlevel.exponentialheightfog_0.exponentialheightfogcomponent_0",
                "FogMaxOpacity=0.8", "StartDistance=1200", "FogDensity=0.2", "FogHeight=400",
                "OppositeLightColor=110,125,145", "LightInscatteringColor=150,145,130"],
        ];
    }

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
        steps.Add(["--add-sky-placeholders", Target, "none", "--ground-z", "-80", "--ground-box", boxes, "--ground-material", water, "--ground-grid", "4608", "--sky-drop", "0.2"]);
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
