# Marvel Heroes Omega Modding Tools

C# / .NET 8 tools for reading and writing Marvel Heroes Omega `.upk` packages (a UE3 fork). They export meshes and animations to FBX, import FBX static meshes back into packages (confirmed working in-game), and are working toward importing FBX animation. Windows-only. The owner is Kurt.

## Repo layout

```
AnimExportCli/   Skeletal mesh + animation export to FBX; FBX-to-UPK animation import (in progress). CLI + WinForms GUI. v1.3.1
UpkMeshScan/     StaticMesh scan, export (FBX + textures), import (FBX -> package), property + material-parameter edits, zone placeholders and whole-zone builds, cross-package copies, level actors, texture import/export incl. .tfc, undo/redo, diagnostics. CLI + WinForms GUI (no args = GUI, dark mode default), AssimpNet. v2.26.2
```

Git: commit straight to `main` (GitHub `leeper48/MHO-UPK-Tools`), one commit per feature, only when Kurt asks. No PRs or feature branches for now; `gh` isn't installed. Build scripts, scans and FBX/texture work files live in `UpkMeshScan/publish/` (gitignored). Generated files go in `publish/exports`; `publish/imports` holds only Kurt's edited files.

Each tool has its own `build.bat`. Neither tool references the other yet. The planned merge folds UpkMeshScan into AnimExportCli. UpkMeshScan now has the only **package writer** (`PackageWriter.cs`, proven in-game), and animation Phase 3 should reuse it rather than write a new one.

## Rules that are not negotiable

1. **Game files are modified only through the import command.** Before the first write, back up the live file to `<name>.upk.bak` and verify the copy byte for byte. Never overwrite or delete an existing `.bak`, because it is the oldest copy and counts as the original. Build the new package in a temporary file, verify it by reading it back, and only then replace the live file. `--revert` copies the `.bak` back, verifies it, and keeps it. Imports build from the live file, so mods stack. No other code path writes into the game folder. Refuse, or ask first, if one would. Every write goes through `MeshImport.WriteLive`, which also records an undo snapshot (see Undo / redo). Kurt's standing OK: when a build is ready and its dry run passed, write it live if the game (`MarvelHeroesOmega`) isn't running; if it is running, ask him to close it.
2. **Evidence before fixes.** Several plausible-sounding theories in this project turned out to be wrong. Before changing decode or encode logic, get concrete data: run `--dump` / `--dump-bone`, a verifier, a reference FBX, or ask Kurt to check in Blender. State a hypothesis as a hypothesis until data confirms it.
3. **Blender is the authority for FBX.** Blender's FBX reader is independent of Assimp. If an Assimp-based check disagrees with Blender, suspect the Assimp tooling first.
4. **Verifiers are ground truth.** Run `--verify-roundtrip` and `--verify-encoder` after any change to the FBX import/export path or the encoder. Clean results are about 0.04–0.07° (decode only) or 0.08–0.12° (encode+decode) for rotation, and about 0.0000 for position.
5. **Work in phases with a verified stopping point.** Don't do large speculative rewrites.
6. **Bump `<Version>` in the csproj on every change Kurt will test.** The version banner (read via reflection, printed first in CLI output, shown in the GUI title) exists because of repeated stale-build confusion.

## Build conventions

- Every `.bat` file **must use CRLF line endings** and **`goto`-based flow control**. Don't use multi-line parenthesized `if/else` blocks. With LF endings or those blocks, `cmd.exe` fails silently and the window flash-closes.
- UpkMeshScan follows the same pattern as AnimExportCli (`net8.0-windows` WinExe, `AttachConsole(-1)` for CLI), and writes UTF-8 **without a BOM**. With a BOM, stray bytes appeared before the version banner. The GUI (`Gui/MainForm.cs`) contains no write logic. It calls the same entry points as the CLI, and every game-folder write still goes through `MeshImport.WriteLive` / `Revert`.
- AnimExportCli: `net8.0-windows`, `OutputType=WinExe`, x64, AssimpNet. When launched with CLI args it calls `AttachConsole(-1)` and **must set console encoding to UTF-8 after attaching** (this regressed once).
- Kurt runs builds via a Send To shortcut to a stable copy in `C:\Tools\`.

## Package format facts (confirmed on real files)

- Magic `0x9E2A83C1`. Real files seen: **FileVersion 868, Licensee 3**.
- Layout: header, then name / import / export tables, then object data. The package may be LZO1X chunk-compressed; offsets index the *uncompressed* body.
- **Every stock package is LZO-compressed.** All 172 uncompressed packages in the game folder were modified by mod tools, and they load in-game. Those tools write the header with CompressionFlags 0 and chunk count 0, so it ends exactly at NameOffset, and they clear PackageFlags bit 0x02000000. An earlier note said `*_SF` packages are uncompressed; that came from an already-modded WinterSoldier file.
- Stock files are dated 2024-03-14. Anything dated later has been modified.
- Character weapons (knife, pistol, mine, launcher…) are **SkeletalMesh**, not StaticMesh. Static meshes in character packages are mostly VFX (for example dodge afterimages). Buildings and props live in environment/zone packages.
- **Writing packages (what works in-game):** write the package uncompressed. Drop the chunk table (CompressionFlags 0, count 0), so the summary ends exactly at NameOffset and every stored offset stays valid. Clear PackageFlags 0x02000000. Append the replacement export at the end of the body and rewrite only its export-table entry (SerialSize/SerialOffset), leaving the old bytes in place. There is no TOC or size registry to update.
- Seek-free region packages (`SCS__*RegionBand_SF`) are shared mesh libraries. Map-tile packages import meshes from them by name. The Midtown free-roam tiles (`UES_Static_*_X#_Y#`, a 6×6 grid) import from `SCS__OpDailyBugleRegionBand_SF`. Some tiles also carry their own mesh copies. Use `--import-sources` / `--find-name` to see where a mesh's geometry really lives before importing.
- **Adding objects (`PackageRebuilder.cs`):** rewrite the whole package uncompressed. Append new names after the existing ones, and new imports after the existing imports, so every existing index keeps its meaning. Append new exports with their own table entries and empty depends lists. Stale inline-mip "offset in file" fields in moved textures are harmless; stock-modded packages have them too.
- An import's outer can be an **export**. Stock packages import `MarvelGame.upk` objects through forced-export package objects.
- **Shaders are global.** No level or region package contains a ShaderCache. Every material's compiled shaders are in `RefShaderCache-PC-D3D-SM3.upk`, found by the material resource's GUIDs and static parameters. A material copied unchanged into another package renders; the game can't compile new ones.
- **Cross-package imports only resolve if that package is already loaded.** A zone's main level loads before its region `SCS__` library. An import into the library gave the default checker material. Imports from `MarvelGame.upk` (always loaded) work.
- **Objects are identified by path.** A copied object with the same path as one in another package replaces it for everything loaded after it (a broken copy of the water material broke the stock pier water zone-wide). Rename a copy you intend to change.
- MHO StaticMeshComponent properties start at byte 8 (an extra int32, then NetIndex), not 4 or 16. A component's native data is its lighting record. "Nothing baked" is 21 bytes: int 1, then 17 zero bytes. New components use that, which is fine for unlit materials and our placeholders.
- **Static switches are baked into shaders.** A material instance can only use a static-switch combination that already exists in the shader cache. To get a feature (for example emissive), copy an existing MIC whose switches already have it and change only its textures and scalar/vector parameters. `--dump-export` prints a MIC's static switches.
- Mesh UVs are half floats. Keep them near zero (shift by whole repeats per object): at |u| ≈ 24, the precision is only 1/64 of a repeat.
- **Level actors:** a Level's native data starts with its Actors list (owner = the level, count, refs). An actor copied into a level must be appended there (`--add-level-actor`) or the game ignores it.
- UpkMeshScan validates the header's compressed-chunk table. If that table doesn't check out, it locates the table by scanning byte-by-byte for chunk signatures (the header isn't 4-byte aligned, because of the FolderName FString).

## Hard-won animation knowledge (don't re-derive)

1. UE3 omits default-valued properties. Defaults: `TranslationCompressionFormat=ACF_None`, `RotationCompressionFormat=ACF_Float96NoW`, `KeyEncodingFormat=AKF_ConstantKeyLerp`.
2. Compressed rotation tracks store the **conjugate** (XYZ negated, W kept). Apply the correction **only when format ≠ `ACF_None`**. Applying it to our own encoder output inverts rotations.
3. For single-key rotation tracks (root, `*_offset` bones), `FbxExporter.ResolveSingleKeyRotation` picks whichever of the decoded rotation or its conjugate is closer to the bone's bind pose.
4. Quaternion continuity: flip a key's sign if its dot product with the previous key is negative.
5. AssimpNet's FBX **writer** ignores tick settings and applies a hard-coded ×24. Export divides frames by 24 (`AssimpFbxTickRate`). **On import, don't reapply ×24.**
6. `TicksPerSecond = 1.0` made Assimp's **reader** truncate at about 1 second. It is now `1000.0` in `FbxExporter.WriteAnimated`. This was never a real export bug.
7. Accepted gap: some `*_offset` bones (sometimes `root`, `g_pelvis`) get no channel on export. Leave it unless it causes a real problem.
8. WinForms: set `SplitterDistance` after parenting. Use `TableLayoutPanel` placement plus `AutoSize`/`GrowAndShrink`, not Dock order or fixed pixel heights.

## Current work: FBX → UPK animation import

Goal: import animations that include **new bones not in the original AnimSequence**. Another modder's tool imports the new skeleton (the new bones are visible in game) but fails to apply animation to them. That gap is the reason this tool exists.

- **Phase 1** (FBX → engine-space `BoneAnimation`): ✅ done, verified
- **Phase 2** (`AnimSequenceEncoder`: → raw `ACF_None` + `AKF_ConstantKeyLerp` bytes, `EncoderRoundTripVerifier`): ✅ done, verified
- **Phase 3** (package writer): **next, highest risk**. It needs to:
  - Build the full AnimSequence property block around the encoder output (NumFrames, SequenceLength, CompressedTrackOffsets, format names).
  - **Add new track slots** for new bones (CompressedTrackOffsets entries, track/bone-name list entries, byte data), not just re-encode existing tracks.
  - Patch the export table by appending the resized export and rewriting only its entry. **This is solved:** reuse UpkMeshScan's `PackageWriter`.
  - An LZO1X compressor is **not needed**. Uncompressed packages load in-game (see Package format facts).
  - Add a `--verify-package-write` self-test in the style of the other verifiers.
- **Phase 4** (CLI/GUI wiring): deliberately last.

Open questions for Kurt before Phase 3 design:
1. Does the source FBX contain keyframes for the new bones?
2. Where does the bone/track-name list live: AnimSet or AnimSequence?
3. Is a before/after UPK pair from the other modder's tool available for a binary diff?

## Static meshes (UpkMeshScan): done, confirmed in-game

- `--export-fbx` (with textures), `--import-fbx` (`--dry-run`, `.bak`, verified temp, swap), `--revert`, `--verify-import-roundtrip` (self-test: export, then FBX, then import).
- `--decode-static` (folder-wide parser check: all 37,107 meshes decode).
- `--set-property` changes existing float/int/Color/LinearColor properties, and material-instance parameters as `param:<name>=value` (entries of ScalarParameterValues / VectorParameterValues; for example it dimmed Hightown's procedural sky to night). A `Color` struct is stored as bytes **B, G, R, A** (little-endian 0xAARRGGBB): read that way, Midtown's sun comes out warm and its sky-side fog blue. `LinearColor` is four floats R, G, B, A. Values are given as `R,G,B[,A]`; a missing alpha keeps the original. Zone fog is an ExponentialHeightFogComponent in the zone's persistent-level packages (Midtown: `MidTown_Static.upk` + `MidTown_Dynamic.upk`; Cannery Row: `JerseyDocks_Cannery_A.upk`). Confirmed in-game.
- FBX export writes **no normals** (Blender reads them as custom split normals, which Kurt deletes). Only `--verify-import-roundtrip` exports them. The reader generates flat normals when a file has none.
- Diagnostics: `--dump-export` (also prints object arrays by path, such as a component's Materials, and a MIC's static switches), `--uv-info` (also per-channel vertex-colour stats), `--inspect-fbx` (prints UV ranges too), `--find-name` (`*` wildcards), `--import-sources`, `--mesh-users`, `--texture-info`, `--export-textures`, `--list-exports`, `--export-deps` (what an export pulls in; `--cut` props).
- Confirmed in-game: edited buildings render (including 3.5× height). A package written this way loads.

Layout facts (don't re-derive; see the `StaticMesh.cs` / `StaticMeshBuilder.cs` headers for the evidence):
- **A StaticMesh's last 20 bytes are a name (usually "None"), an int32 0 or 1, then 8 zero bytes.** Copies must remap that name: an unmapped one crashed Asgard_Hub_B on load ("Bad Name Index 2012 / 865"). `--copy-export` remaps it since 2.17.1. Use `--names <pkg> [index...]` to read name tables.
- LOD 0 layout: bounds, BodySetup, kDOP, InternalVersion 18, 4 unknown ints, LOD count, bulk header (offset points at itself), sections (45 bytes each), positions, tangent+UV buffer (half UVs), color buffer, index buffer (**always 16-bit**), wireframe (always empty), adjacency (**always 12 per triangle**). After that comes a tail that starts with int 1.
- **Vertex limit is 65,535 welded vertices** (16-bit indices). This is the practical cap on how big an edit can get.
- Tangents: the standard UV-gradient tangent. TangentX.W = 0x80. TangentZ.W = 0xFF for +1 binormal sign, 0x00 for −1.
- An empty collision tree is written as root bound +FLT_MAX / −FLT_MAX, nodes (6,0), triangles (8,0), sections EnableCollision 0. Imports currently write this.
- Meshes cooked into map tiles have section material ref 0; the placed component supplies the materials by section index. Those sections are named `section<N>` on export/import, and section order must be preserved.
- The importer matches FBX objects by the mesh name (`<mesh>_section<N>`, `<mesh>.001`) and materials by name, ignoring Blender's `.NNN` suffixes.
- Textures: stock textures keep only small mips (mostly 64×64) in the package; larger mips are in `.tfc` caches with offset/size −1 in the package. **The lookup is `TextureFileCacheManifest.bin`** (`TfcCache.cs`): count, then per texture path, GUID (= the texture's TextureFileCacheGuid after its mips), cache name, and (mip, offset, size) list. At the offset is a UE3 LZO compressed chunk. `--export-textures` / `--export-fbx` now write the largest stored mip. Mip 0 is often cooked out (flag 0x20). Mod-tool-injected textures have one full-size inline mip, `NeverStream`, and no cache. `--import-texture` writes that form, but stores every mip in the .dds inline (`publish/scans/tools/make_mips.py` writes a DXT1 .dds with the full chain, coverage-kept 1-bit alpha and optional `--scale`). **A texture seen from a distance needs mips:** with one mip, ICP's baked ground plane speckled (confirmed in-game).

Open items: real collision (kDOP build), editing placements (move, add, or remove meshes in a tile), following component-supplied materials, and optional LZO compression of written packages. Zone main levels grow from ~150 KB to 0.5–15 MB, almost all added content: storing uncompressed only costs ~1.6× the stock size. Parked:
- Hightown GameCenter stairwell (stock untextured quads)
- occasional Hightown client crash (tbbmalloc, also happened before the mods; DXVK + ReShade)
- ICP clouds

## Zones and placeholders (UpkMeshScan): confirmed in-game

- A tiled zone is a main level package (sky sphere, fog, sun, one StaticMeshCollectionActor) plus `<Prefix>_X#Y#` tile packages. Tiles store placements in **world** coordinates on a 2304-unit grid, and import meshes from one `SCS__*` region library (find it with `--import-sources`). Examples: Midtown = `MidTown_Static`/`_Dynamic` + `UES_Static_*`; Cannery Row = `JerseyDocks_Cannery_A` + `JerseyDocks_Cannery_X*`.
- Zones built from exit-named cells (`Shipping_A_NESW_A`, `Industrial_Processing_*`, `SiegeCity_A_*`) are laid out at run time. Every cell is stored at the origin, so placeholders at fixed positions can't work there. Industry City's main level is `Brooklyn_Docks_A` (Shipping cells).
- `--zone-placeholders` builds footprint prisms from the tiles (FBX, for Blender review). `--add-cell-placeholders` adds one mesh + component per cell to the main level's collection actor with `MinDrawDistance` (the MHO client honours it), rebuilt from the `.bak` with live edits carried over. Midtown (3500, `--shrink 0.8889`) and Cannery Row are written.
- `--add-sky-placeholders <pkg> none --ground-z Z --ground-box x0,y0,x1,y1[,z][;...] [--color R,G,B | --ground-material pkg.obj] [--ground-grid N] [--sky-drop F]` adds ground-only planes (one per box, each at its own z) as a second section of the sky sphere, for zones with a void past their edge. `--sky-drop` lowers the dome's own section by F of its height. Industry City: z −220 (water ≈ −205), ±100224, the zone's water material, UV tile 2304 (the cells' `terrain_flat_filler` tiling).
- **Translucent water is fogged per vertex.** A huge water quad with corners ~100k away and a thin one with corners near the player fog differently, so the boxes show as bands running to the horizon. `--ground-grid 4608` splits every box into a vertex grid; it fixed the ICP band (confirmed in-game), and both water recipes use it.
- `--add-cell-placeholders` options: `--offset X,Y[,Z]` (tile coordinates to in-game), `--lift Z` (diagnostic: boxes float above their buildings), `--always-fbx` (pieces with MinDrawDistance 0, such as ground slabs under the streets), `--wall-material pkg.obj --wall-uv N` (wall faces get that material with box-projected UVs; roofs stay grey), `--from-live` (build on the live file, for levels that got a copied sky actor).
- **Region centring (don't re-derive):** MHServerEmu's `RegionGenerator.CenterRegion` moves every area by minus the centre of the union of all area bounds, sub-areas included. Tiles move with their cells; the main level doesn't. So placeholders built from tile coordinates need `--offset` = −centre. Method: turn on GenerateLog, enter the zone, then take the union of the logged `cellpos` (pre-centring = RegionBounds centre) ± half of each `.cell`'s bounds. A `.cell` file is a 12-byte header, then max vec3, then min vec3. Hightown: +5208, −15512 (district + Jumbotron Overlook sub-area at −30000,−30000 + sewers at 0,60000), confirmed in-game. Industry City: main cells +3456, −2304.
- **Components owned by a standalone actor** (StaticMeshActor, InterpActor, …) are placed by the actor's Location / Rotation / DrawScale; collection-actor components carry their own Translation etc. The scan applies the owner's transform since 2.16.1. Before that, Asgard's Bifrost gun landed at the origin, and ICP has 102 such meshes (not yet rescanned). An actor's tags start after a few native fields (for example at byte 0x1A), so `ComponentTransform.ReadActor` tries starts up to 96.
- A zone's main level is its region's `ClientMap` asset (`Regions/RegionClientMap.type`; see publish/scans/tools/cally.py). Examples: Industry City = `Brooklyn_Docks_A`, Cannery Row = `JerseyDocks_Cannery_A`, Midtown = `MidTown_Static`, Hightown = `Madripoor_HighTown_B`, Odin's Palace (Kurse) = `Asgard_Hub_B`.
- Ground slabs come from the cells' height maps (72×72 samples of 32 units; −32768 = none) via `publish/scans/tools/groundboxes.py <cells> <out> [lo,hi] [pull]`. Hightown uses band −6..110, pulled back 4 samples from anything lower, so stairwells stay open. Slabs sit at −78..−8.

## Zone builds (UpkMeshScan): confirmed in-game

- `--build-zone <zone> <game-folder> [--walls facade|grey] [--dry-run]` and the GUI **Zones** tab rebuild a zone's main level from stock with its whole recipe (`ZoneBuilds.cs`). Every step runs as a dry run on a scratch copy of the `.bak` and verifies itself; the result is written once, so one undo step takes the whole rebuild back. Inputs that aren't in the game ship in `ZoneData/<Zone>/` (copied next to the exe; the `C:\Tools` copy needs it too). `--build-zone` reads the exe's copy, so **after changing a ZoneData file, run `dotnet build` before building the zone** (a stale copy once pushed an old ICP bake).
- **Hightown** (`Madripoor_HighTown_B`, which originally had no mesh actor), 10 steps:
  1. Copy the stock `Brooklyn_Docks_A` collection actor (sky dome) and add it to the level's actor list.
  2. Facade: import 3 textures, then copy `madripoor_hitown_storefront_a_mat` (opaque, UseEmissive on) as `ht_facade_mat` with our textures.
  3. Building boxes (raster from all pieces 150+ wide with tops ≥ 250, street props skipped; MinDrawDistance 3500) plus always-drawn ground slabs, `--offset 5208,-15512`, walls textured at `--wall-uv 512`.
  4. Copy the Hightown water, as two layers (−80 and −85) in 4 boxes around the GameCenter stairwell hole.
  5. Sky dropped 0.2 and dimmed to night.
- The facade texture is generated (`make_facade.py`: DXT1 diffuse + spec with R = specular, G = emissive, B = reflection, plus a flat normal). Hightown's own textures are trim atlases, not tileable facades.
- **Odin's Palace** (`Asgard_Hub_B`, district of 12 `Asgardia_INS` tiles + 2 bridge cells, library `SCS__DailyGAsgardINSTRegionL40_SF`; region not centred, no offset), 10 steps:
  1. Sky backdrop copies.
  2. Brooklyn's sky mesh and material, as templates only.
  3. The Bifrost deck meshes and material, copied in.
  4. Building boxes on a 64-unit raster (`--raster 64 --raster-step 64`: a third of the 32-unit triangle count), via `--component-template` (the level's waterfall component).
  5. Kurt's edited multi-level ground slabs (`groundboxes.py ... -200,300 0 64`: each slab 8 under its own surface).
  6. `--add-mesh-instances` recreating the 11 deck placements from the bridge tiles at z −2, MinDrawDistance 3500. Confirmed in-game.
- `--add-mesh-instances <level> <tile> <meshes> --template <comp>` recreates a tile's placements of chosen meshes, with their materials, in the main level: real geometry as a distant stand-in. Copy the meshes and materials first; `--copy-export` reuses objects the level already has.
- **Hightown baked ground (textured mode), confirmed in-game:**
  - `--export-placed <folder> <layout> <library> --out f.fbx [--offset] [--max-height] [--min-footprint] [--skip-material] [--diffuse mat=tex]` exports the real placed tile meshes in world position, with component materials, textures and vertex colours. A material without a diffuse parameter (vertex-blended terrain) gets all its compiled textures written and listed in `<out>_layers.txt`. For Hightown's flat ground: `--max-height 16 --min-footprint 128 --skip-material water,cell_dev.,(no material)`.
  - Kurt bakes that top-down onto one quad (diffuse + alpha).
  - The recipe imports it as DXT1 with a 1-bit alpha (split at 85/255 = the default masked clip; half DXT5's size, identical cut-out), copies the library's masked `brooklyn_pieredge_mat` as `ht_groundplane_mat`, and places the quad with `--textured-fbx ... --textured-material ... --textured-z -75`, always drawn.
  - Grey mode keeps the height-map slabs (`--top-material` can texture slab tops instead).
- `--remove-components <tile> --mesh X --material Y [--library]` takes components off their collection actor's list (their data stays). It removed Hightown's 52 stock water planes (`terrain_flat_filler`, z −75) from 36 tiles, so our −80/−85 layers are the only harbour water. Always add `--mesh`: sidewalk fillers, shop floors and fountains also have water sections. Only components still on their actor's list are reported.
- **Industry City tiles (done 2026-09-25):** stock water (`terrain_flat_filler`, −116) removed, and the dirt seabeds under the water (`terrain_flat_filler` with `mtntslms_foundation.dirt`, −484) removed from the 9 harbour water cell types. The seabed made cell water darker than our open water. The 2 AIM Sub pit cells keep theirs (−612): without it the sky dome showed cyan through the pit water.
- **Industry City** (`Brooklyn_Docks_A`, 10 steps): Kurt's baked ground plane at −116 (grate-free export, full mips, colour ×0.7 to match the real ground), building boxes, the water at −121/−126 in 4 boxes around the AIM Sub hole, pit water at −210/−215 over 10× the hole (so no view past the hole's edge finds the sky), and the photo sky. The plane shows where the real ground isn't drawn: the distance and the pit ring (red test). It's still solid under the hub's floor grates (the terrain runs under them), where stock shows water.
- **Building LODs (ICP, first one confirmed in-game 2026-09-26):** `--export-placed` one cell with `--min-height 100 --min-footprint 100 --min-z -100000 --max-z 100000` (structures and containers, not props; buildings are modular pieces under 280 long, so a length cut alone fails). Kurt bakes a low-poly textured LOD in Blender. `--add-cell-placeholders --lod <fbx>=<material>` places it with the boxes' MinDrawDistance; `--lod-inset 0.98 --lod-drop 4` keeps it just inside the real building (ICP's cells are still drawn past 3500, and a coincident LOD z-fought); `--exclude-box` removes the grey boxes under it. **LODs must be unlit:** our components have no lightmap, so a lit material left the side away from the sun nearly black. They use a copy of Hightown's emissive `madripoor_hitown_storefront_a_mat` (emissive = diffuse × spec G × EmissiveMult 1) with a full-G spec texture (no specular or reflection) and a flat normal; bakes are converted at `--scale 0.6`. **Whole-zone bundle (ICP, confirmed in-game 2026-09-26):** one export of all 61 cells (fences, trees, poles, railings, cables, ladders skipped: ~790k tris), which Kurt baked into 4 parts with a 2048 atlas each. They replace the grey boxes entirely (`--exclude-box` over everything). `--lod-shrink 6` (in along the normals) replaces the centre-scaling inset, which would shift the edges of a zone-wide mesh. **The game measures draw distance to the centre of a component's bounds**, so each part is split per 2304 cell (each connected piece goes to the cell of its centre; over 60k vertices splits further). One zone-wide mesh only appeared once its middle was 3500 away, leaving a gap where the real cells had streamed out. MinDrawDistance is 2500 (3500 still left a small gap). Atlas brightness per part: warehouses 0.45, the rest 0.6. ICP main level is ~24.7 MB.
- ICP's middle block is random: two rows of 3 cells (x 3456/5760/8064, y 0 and 2304) swap between runs (WareSuper A trio vs Dockyard NESW, FoodTrucks, Dockyard ESW). `IndustryCity_layout.txt` uses the common arrangement (WareSuper at y 0, 3 of 4 logged runs). `publish/scans/tools/layout.py <logs>` lists what differs.
- ICP ground is `brooklyn_terrain_docks_vertexpaint_mat` (docks / dockscracked / mud blended by vertex colour). Its node graph is cooked out, so the channel mapping is unknown. Only the 3 `shipping_*_ground_a` meshes and the cargo-ship terrain carry real paint (mostly R and B). The 1,059 `hk_street_straighta` tiles that also use it (via component override) have black/white leftover colours.
- `publish/scans/tools/ht_build.sh` / `icp_build.sh` are the older script recipes.

## Sky backdrops: confirmed in-game

- `--sky-coverage <pkg> <component[,...|prefix*]> [--from x,y,z]` maps which view directions placed backdrop meshes cover (# = mesh, . = black sky).
- `--add-component-copies <pkg> <component> --yaw y[:pitch[:roll]],...` adds rotated copies of a placed component to its collection actor, with an empty lighting record.
- Odin's Palace (`Asgard_Hub_B`): the `starfield_a` backdrop (unlit nebula + stars) covered only ~130° below the horizon. Copies at yaw +90/180/270 fix the sides, and roll 180 at 4 yaws fixes the top: 100% coverage, "perfect".

## Undo / redo

- `MeshImport.WriteLive` and `--revert` snapshot the previous live file to `%LOCALAPPDATA%\UpkMeshScan\history\<file>_<hash>\` (last 20 steps, `History.cs`). `--undo` / `--redo` / `--history <pkg>` restore through the same verified path and refuse if the live file isn't the expected version (`--force` overrides). The GUI Backups tab has Undo / Redo (Ctrl+Z / Ctrl+Y). The `.bak` is never touched.

## Copying materials between packages (UpkMeshScan): confirmed in-game

- `--copy-export <src.upk> <export-path> <dst.upk> [--cut prop,...] [--dry-run]` copies an export and everything it references. An exact parser finds every name and object reference: tags, struct arrays, known object arrays (`expressions`, `functionexpressions`), string arrays, the Material/MIC native resource, and Texture2D inline-mip offsets. Everything is renumbered into the target. Anything unknown stops the copy. Verification re-parses every copy and checks each reference resolves to the same path.
- Material/MIC native layout: quality mask (3 = two levels), then per level CompileErrors (0), TextureDependencyLengthMap (0), MaxTextureDependencyLength, Id GUID, NumUserTexCoords, UniformExpressionTextures (object refs), 6 ints, TextureLookups, 4 ints. For a MIC, add StaticParameters: BaseMaterialId, StaticSwitchParameters (name, value, override, GUID), then 3 empty arrays.
- **Never cut `materialfunctioninfos`.** Nulling it gave the default checker material (the function state GUIDs are checked). Cutting `physmaterial` (footstep splashes, sounds) is fine.
- `--rename` / `--replace-ref src=dst` copy a material under a new name with references swapped, for example to imported textures (the ICP photo sky and the Hightown facade). `--copy-export` also copies meshes, StaticMeshComponents (empty lighting only) and collection actors.
- First use: Industry City's water. `brooklyn_docks_lighting.brooklyn_docks_water_mat` was copied from `SCS__CH0201ShippingYardRegion_SF` into `Brooklyn_Docks_A` (85 exports, `--cut physmaterial`) and used on the ground plane. Its animated water covers the plane.

## Working style

Kurt is direct, evidence-driven, and tests every change with screenshots, terminal output, or FBX files. Keep explanations concise. Say plainly when a theory was wrong.
