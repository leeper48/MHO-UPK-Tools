namespace MhoPackageModifier;

/// <summary>
/// Every command-line command in one place: the CLI usage text, the GUI's Tools tab (command picker with a filled-in
/// template) and the manual's command reference are all built from this list, so they can't drift apart.
/// Template tokens (Tools tab): {pkg} = the open package, {folder} = the game folder, {export} = the export selected
/// on the Browse tab, {out} = the export folder.
/// </summary>
static class CommandCatalog
{
    public sealed record Command(string Group, string Flag, string Syntax, string Summary, bool Writes, string Template);

    public static readonly string[] Groups =
        ["Inspect", "Meshes", "Textures", "Properties and materials", "Objects", "Placements (Blender round trip)", "Zones", "Sky", "Backups and history", "Self-tests", "App"];

    public static readonly Command[] All =
    [
        // ---- Inspect (read-only)
        new("Inspect", "(scan)", "<folder> [--out file] [--top-only] [--skeletal] [--include-empty] [--include-backups] [--decode-static]",
            "Scan every package in a folder and list the static meshes each one contains (--decode-static: also run the mesh parser on every one).", false, "{folder} --top-only"),
        new("Inspect", "--list-exports", "<package.upk> [class-filter]", "List a package's exports: index, class, size, path.", false, "--list-exports {pkg} staticmesh"),
        new("Inspect", "--dump-export", "<package.upk> <export> [--out folder]", "Write one export's raw bytes and an annotated dump (property tags, object arrays, a material instance's static switches, native data in hex).", false, "--dump-export {pkg} {export}"),
        new("Inspect", "--names", "<package.upk> [index ...]", "Print the name table (all entries, or the given indices).", false, "--names {pkg}"),
        new("Inspect", "--find-name", "<folder> <name> [--include-backups]", "Every package that exports or imports an object with this name (* wildcards).", false, "--find-name {folder} {export}"),
        new("Inspect", "--import-sources", "<folder> <package.upk>", "For every StaticMesh a package imports: which packages hold its geometry (finds a zone's region library).", false, "--import-sources {folder} {pkg}"),
        new("Inspect", "--mesh-users", "<package.upk> <staticmesh>", "Where a mesh is placed in a package (components and their transforms).", false, "--mesh-users {pkg} {export}"),
        new("Inspect", "--export-deps", "<package.upk> <export> [--depth 20] [--cut prop,...]", "Everything an export pulls in (what --copy-export would copy), and the imports it needs.", false, "--export-deps {pkg} {export}"),
        new("Inspect", "--inspect-fbx", "<file.fbx> [...]", "Node tree, meshes, materials, bounds and UV ranges of an FBX, as the tool reads it.", false, "--inspect-fbx \"{out}\\file.fbx\""),

        // ---- Meshes
        new("Meshes", "--export-fbx", "<package.upk> <staticmesh> [--out folder]", "Write a StaticMesh (LOD 0, one part per material section) as FBX with its textures.", false, "--export-fbx {pkg} {export} --out \"{out}\""),
        new("Meshes", "--import-fbx", "<package.upk> <staticmesh> <file.fbx> [--dry-run [--out folder]]", "Replace a StaticMesh's geometry with an FBX (sections matched by material name). Collision becomes empty.", true, "--import-fbx {pkg} {export} \"{out}\\file.fbx\" --dry-run"),
        new("Meshes", "--uv-info", "<package.upk> <staticmesh>", "UV ranges per section and channel, vertex-colour statistics, sky-dome v-band elevations.", false, "--uv-info {pkg} {export}"),
        new("Meshes", "--scale-uv", "<package.upk> <staticmesh> <channel> <factor | fu,fv> [--section 0] [--dry-run]", "Scale one UV channel of a mesh (e.g. tile a texture more often).", true, "--scale-uv {pkg} {export} 0 2 --dry-run"),
        new("Meshes", "--mesh-compare", "<stock.upk> <mesh> <edited.upk> <mesh>", "Compare an edited mesh with its stock original triangle by triangle: normals, tangents, lightmap UVs (diagnoses odd lighting on edits).", false, "--mesh-compare <stock.upk> <mesh> {pkg} {export}"),

        new("Meshes", "--mesh-planes", "<package.upk> <mesh>", "A mesh's vertical faces by wall line and the side they face: a wall line facing both ways has a part turned round (diagnoses walls that look wrong from outside).", false, "--mesh-planes {pkg} {export}"),

        // ---- Textures
        new("Textures", "--texture-info", "<package.upk> [name-filter]", "Textures: size, format, and where each mip's data lives (package or .tfc cache).", false, "--texture-info {pkg}"),
        new("Textures", "--texture-png", "<package.upk> <texture> <out.png>", "Save a texture as PNG (largest mip available, including from the .tfc caches; alpha kept).", false,
            "--texture-png {pkg} {export} \"{out}\\texture.png\""),
        new("Textures", "--export-textures", "<package.upk> [name-filter] [--out folder]", "Write textures as .dds (largest mip available, including from the .tfc caches).", false, "--export-textures {pkg} --out \"{out}\\textures\""),
        new("Textures", "--import-texture", "<package.upk> <template-texture> <new-name> <file.png|jpg|bmp|dds> [--format dxt1|dxt5] [--split 85] [--scale 1] [--no-mips] [--max-size N] [--dry-run]",
            "Add a new texture to a package from an image, using an existing texture as the template (compression settings, group). PNG alpha: DXT1 with a 1-bit cut at --split, or DXT5 for soft alpha.", true,
            "--import-texture {pkg} {export} my_texture \"{out}\\image.png\" --dry-run"),

        new("Textures", "--replace-texture", "<package.upk> <texture> <file.png|jpg|bmp|dds> [--format dxt1|dxt5] [--split 85] [--scale 1] [--no-mips] [--max-size N] [--dry-run]",
            "Replace an existing texture's image (same name and path, so every material using it shows the new one). Its other settings are kept; the new mips are stored in the package.", true,
            "--replace-texture {pkg} {export} \"{out}\\image.png\" --dry-run"),

        // ---- Properties and materials
        new("Properties and materials", "--set-property", "<package.upk> <export> <Name=Value> [...] [--dry-run]",
            "Change existing float/int/colour properties, or material-instance parameters as param:<name>=value (colours as R,G,B[,A]).", true, "--set-property {pkg} {export} FogDensity=0.2 --dry-run"),
        new("Properties and materials", "--material-params", "<package.upk> <material-instance>", "List a material instance's scalar, vector and texture parameters.", false, "--material-params {pkg} {export}"),
        new("Properties and materials", "--find-mic", "<folder> <parent-name-part> [switch=true|false ...] [--limit 20]", "Find material instances by parent material and static switches (e.g. translucent + useemissive=true), to copy one with the features you need.", false, "--find-mic {folder} storefront useemissive=true"),
        new("Properties and materials", "--set-object", "<package.upk> <export> <property | property[i]> <target-export> [--dry-run]", "Point an object property (e.g. a component's Materials[0]) at another export.", true, "--set-object {pkg} {export} Materials[0] <material-path> --dry-run"),

        // ---- Objects
        new("Objects", "--copy-export", "<source.upk> <export> <target.upk> [--rename name] [--replace-ref src=dst|none ...] [--cut prop,...] [--dry-run]",
            "Copy an export and everything it references into another package, renumbered and verified. --rename gives the copy its own path; --replace-ref points references at objects the target already has (or none).", true,
            "--copy-export <source.upk> <export> {pkg} --rename my_copy --dry-run"),
        new("Objects", "--remove-components", "<package.upk> [--mesh text] [--material text] [--library lib.upk] [--dry-run]", "Take placed meshes off their collection actor's list (e.g. stock water planes); their data stays.", true, "--remove-components {pkg} --mesh terrain_flat_filler --material water --dry-run"),
        new("Objects", "--add-level-actor", "<package.upk> <actor> [--dry-run]", "Add a copied actor to the level's actor list (the game ignores actors that aren't on it).", true, "--add-level-actor {pkg} {export} --dry-run"),
        new("Objects", "--add-component-copies", "<package.upk> <component> --yaw y[:pitch[:roll]],... [--dry-run]", "Add rotated copies of a placed component to its collection actor (degrees).", true, "--add-component-copies {pkg} {export} --yaw 90,180,270 --dry-run"),
        new("Objects", "--add-mesh-instances", "<level.upk> <tile.upk> <mesh[,mesh...]> --template <component> [--min-draw 3500] [--z-offset dz] [--dry-run]", "Recreate a tile's placements of chosen meshes, with their materials, in a main level (distant stand-ins).", true, "--add-mesh-instances {pkg} <tile.upk> <mesh> --template <component> --dry-run"),

        // ---- Placements
        new("Placements (Blender round trip)", "--export-placements", "<folder> <layout.txt> <library.upk> --out file.fbx [--offset X,Y] [--min-footprint 100] [--min-height 100] [--skip a,b] [--skip-material a,b]",
            "Export a tiled zone's placed meshes, one Blender object per placement, plus a _placements.txt sidecar that the import needs.", false,
            "--export-placements {folder} layout.txt SCS__library.upk --out \"{out}\\zone_placements.fbx\" --min-height 0 --min-footprint 64"),
        new("Placements (Blender round trip)", "--import-placements", "<folder> <sidecar.txt> <edited.fbx> [--keep-lighting] [--apply-deletes] [--library lib.upk] [--dry-run]",
            "Apply the edited FBX: duplicates become new placements, moved originals move, Edit Mode changes become new meshes, deleted originals are removed (--apply-deletes). Re-importing an updated file replaces the earlier import.", true,
            "--import-placements {folder} \"{out}\\zone_placements_placements.txt\" \"{out}\\edited.fbx\" --keep-lighting --dry-run"),
        new("Placements (Blender round trip)", "--test-placements", "<folder> <sidecar.txt> <exported.fbx> [--library lib.upk]", "Self-test of the round trip: a known duplicate, move and mesh edit, imported as a dry run.", false,
            "--test-placements {folder} \"{out}\\zone_placements_placements.txt\" \"{out}\\zone_placements.fbx\""),
        new("Placements (Blender round trip)", "--export-placed", "<folder> <layout.txt> <library.upk> --out file.fbx [--offset X,Y] [--min-z -400] [--max-z 150] [--min-footprint 64] [--min-height 0] [--max-height N] [--skip a,b] [--skip-material a,b]",
            "Export placed tile meshes in world position with materials and textures as one scene, e.g. to bake a ground plane or building LODs in Blender.", false,
            "--export-placed {folder} layout.txt SCS__library.upk --out \"{out}\\lod_bundle.fbx\" --min-height 100 --min-footprint 100"),

        // ---- Zones
        new("Zones", "--build-zone", "<zone> <game-folder> [--walls facade|grey] [--lod-size 512|0] [--dry-run]", "Rebuild a zone's main level from its original with the whole recipe (sky, distant buildings, ground, water). One undo step.", true, "--build-zone Hightown {folder} --dry-run"),
        new("Zones", "--zone-placeholders", "<folder> <tile-prefix | layout.txt> <library.upk> [--out file.fbx] [--min-height N] [--min-footprint N] [--inset F] [--raster 32]", "Low-poly footprint boxes for the buildings of a zone's tiles, as FBX for review in Blender.", false, "--zone-placeholders {folder} layout.txt SCS__library.upk --out \"{out}\\boxes.fbx\""),
        new("Zones", "--add-cell-placeholders", "<level.upk> <boxes.fbx> [--min-draw 3500] [--offset X,Y] [--wall-material pkg.obj] [--lod f.fbx=material ...] [--dry-run] ...", "Add distant building boxes / LOD meshes to a main level, one piece per cell, hidden near the camera.", true, "--add-cell-placeholders {pkg} \"{out}\\boxes.fbx\" --dry-run"),

        // ---- Sky
        new("Sky", "--add-sky-placeholders", "<level.upk> <boxes.fbx | none> [--ground-z Z --ground-box x0,y0,x1,y1] [--ground-material pkg.obj] [--ground-grid 4608] [--color R,G,B] [--dry-run] ...", "Add ground/water planes (and optional boxes) as a second section of the sky sphere, for zones with a void past their edge.", true, "--add-sky-placeholders {pkg} none --ground-z -220 --ground-box -100000,-100000,100000,100000 --dry-run"),
        new("Sky", "--add-cloud-dome", "<level.upk> <sky-component> <material> [--shrink 0.8] [--uv fu,fv] [--sort-priority N] [--horizon-fade lo,hi] [--dry-run] ...", "Add an inner dome (e.g. a translucent skyline or cloud layer) inside the sky sphere.", true, "--add-cloud-dome {pkg} {export} <material> --shrink 0.8 --dry-run"),
        new("Sky", "--sky-coverage", "<level.upk> <component[,...|prefix*]> [--from x,y,z]", "Map which view directions the placed backdrop meshes cover.", false, "--sky-coverage {pkg} {export}"),

        // ---- Backups and history
        new("Backups and history", "--history", "<package.upk>", "List a package's undo / redo steps.", false, "--history {pkg}"),
        new("Backups and history", "--undo", "<package.upk> [--force]", "Undo the last change this tool made to the package (verified restore).", true, "--undo {pkg}"),
        new("Backups and history", "--redo", "<package.upk> [--force]", "Redo an undone change.", true, "--redo {pkg}"),
        new("Backups and history", "--revert", "<package.upk>", "Restore the original from <package>.upk.bak (verified; the .bak is kept).", true, "--revert {pkg}"),

        // ---- Self-tests
        new("Self-tests", "--verify-import-roundtrip", "<package.upk> <staticmesh>", "Export a mesh, re-import it and compare with the original. Writes nothing to the game.", false, "--verify-import-roundtrip {pkg} {export}"),
        new("App", "--find-game", "", "Look for the game's CookedPCConsole folder in the Steam libraries (what the app does on its first run).", false, "--find-game"),
        new("App", "--check-update", "", "Check GitHub for a newer release of the app.", false, "--check-update"),
        new("App", "--update", "", "Download, verify and install the newest release over this app folder (close the app's window first).", false, "--update"),
        new("App", "--install-zip", "<MHO_Package_Modifier_vX.Y.Z.zip>", "Install a release zip downloaded by hand over this app folder (checked against the .sha256 next to it).", false, "--install-zip <zip>"),
        new("Self-tests", "--test-rebuild", "<package.upk> [export-to-copy]", "Rebuild a package (optionally with one export copied) and verify. Writes nothing.", false, "--test-rebuild {pkg}"),
    ];

    /// <summary>The CLI usage text: every command by group.</summary>
    public static string UsageText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Usage: MHO_UPK_Mod <command> <arguments>      (no arguments: the GUI; the Help button there has the full manual)");
        sb.AppendLine("Commands that write change the game file only without --dry-run: the original is kept as <package>.upk.bak,");
        sb.AppendLine("the new file is verified before it replaces the live one, and --undo steps back. Close the game first.");
        foreach (string g in Groups)
        {
            sb.AppendLine();
            sb.AppendLine($"{g}:");
            foreach (var c in All.Where(c => c.Group == g))
            {
                sb.AppendLine($"  {(c.Flag == "(scan)" ? "" : c.Flag + " ")}{c.Syntax}{(c.Writes ? "   [writes]" : "")}");
                sb.AppendLine($"      {c.Summary}");
            }
        }
        return sb.ToString();
    }

    /// <summary>The manual's command reference (HTML table rows by group).</summary>
    public static string HtmlReference()
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new System.Text.StringBuilder();
        foreach (string g in Groups)
        {
            sb.AppendLine($"<h3>{E(g)}</h3><table><tr><th>Command</th><th>What it does</th></tr>");
            foreach (var c in All.Where(c => c.Group == g))
                sb.AppendLine($"<tr><td><code>{E((c.Flag == "(scan)" ? "" : c.Flag + " ") + c.Syntax)}</code>{(c.Writes ? " <span class=\"w\">writes</span>" : "")}</td><td>{E(c.Summary)}</td></tr>");
            sb.AppendLine("</table>");
        }
        return sb.ToString();
    }
}
