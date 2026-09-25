# Marvel Heroes Omega Modding Tools

C# / .NET 8 tools for reading and writing Marvel Heroes Omega `.upk` packages (a UE3 fork). They export meshes and animations to FBX, import FBX static meshes back into packages (confirmed working in-game), and are working toward importing FBX animation. Windows-only. The owner is Kurt.

## Repo layout

```
AnimExportCli/   Skeletal mesh + animation export to FBX; FBX-to-UPK animation import (in progress). CLI + WinForms GUI. v1.3.1
UpkMeshScan/     StaticMesh scan, export (FBX + textures), import (FBX -> package), property edits (e.g. fog, colours), zone placeholders, cross-package material copy, diagnostics. CLI + WinForms GUI (no args = GUI, dark mode default), AssimpNet. v2.4.0
```

Each tool has its own `build.bat`. Neither tool references the other yet. The planned merge folds UpkMeshScan into AnimExportCli. UpkMeshScan now has the only **package writer** (`PackageWriter.cs`, proven in-game), and animation Phase 3 should reuse it rather than write a new one.

## Rules that are not negotiable

1. **Game files are modified only through the import command.** Before the first write, back up the live file to `<name>.upk.bak` and verify the copy byte for byte. Never overwrite or delete an existing `.bak`, because it is the oldest copy and counts as the original. Build the new package in a temporary file, verify it by reading it back, and only then replace the live file. `--revert` copies the `.bak` back, verifies it, and keeps it. Imports build from the live file, so mods stack. No other code path writes into the game folder. Refuse, or ask first, if one would.
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
- MHO StaticMeshComponent properties start at byte 8 (an extra int32, then NetIndex), not 4 or 16.
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
- `--set-property` changes existing float/int/Color/LinearColor properties. A `Color` struct is stored as bytes **B, G, R, A** (little-endian 0xAARRGGBB): read that way, Midtown's sun comes out warm and its sky-side fog blue. `LinearColor` is four floats R, G, B, A. Values are given as `R,G,B[,A]`; a missing alpha keeps the original. Zone fog is an ExponentialHeightFogComponent in the zone's persistent-level packages (Midtown: `MidTown_Static.upk` + `MidTown_Dynamic.upk`; Cannery Row: `JerseyDocks_Cannery_A.upk`). Confirmed in-game.
- Diagnostics: `--dump-export`, `--inspect-fbx` (prints UV ranges too), `--find-name` (`*` wildcards), `--import-sources`, `--mesh-users`, `--texture-info`, `--export-textures`, `--list-exports`, `--export-deps` (what an export pulls in; `--cut` props).
- Confirmed in-game: edited buildings render (including 3.5× height). A package written this way loads.

Layout facts (don't re-derive; see the `StaticMesh.cs` / `StaticMeshBuilder.cs` headers for the evidence):
- LOD 0 layout: bounds, BodySetup, kDOP, InternalVersion 18, 4 unknown ints, LOD count, bulk header (offset points at itself), sections (45 bytes each), positions, tangent+UV buffer (half UVs), color buffer, index buffer (**always 16-bit**), wireframe (always empty), adjacency (**always 12 per triangle**). After that comes a tail that starts with int 1.
- **Vertex limit is 65,535 welded vertices** (16-bit indices). This is the practical cap on how big an edit can get.
- Tangents: the standard UV-gradient tangent. TangentX.W = 0x80. TangentZ.W = 0xFF for +1 binormal sign, 0x00 for −1.
- An empty collision tree is written as root bound +FLT_MAX / −FLT_MAX, nodes (6,0), triangles (8,0), sections EnableCollision 0. Imports currently write this.
- Meshes cooked into map tiles have section material ref 0; the placed component supplies the materials by section index. Those sections are named `section<N>` on export/import, and section order must be preserved.
- The importer matches FBX objects by the mesh name (`<mesh>_section<N>`, `<mesh>.001`) and materials by name, ignoring Blender's `.NNN` suffixes.
- Textures: stock textures keep only small mips (mostly 64×64) in the package. Full size is in `.tfc` files, and the package stores offset/size −1 for those mips, so the lookup is still unknown. Mod-tool-injected textures have one full-size inline mip and no cache.

Open items: high-res texture export (`.tfc` lookup), real collision (kDOP build), editing placements (move, add, or remove meshes in a tile), and following component-supplied materials.

## Zones and placeholders (UpkMeshScan): confirmed in-game

- A tiled zone is a main level package (sky sphere, fog, sun, one StaticMeshCollectionActor) plus `<Prefix>_X#Y#` tile packages. Tiles store placements in **world** coordinates on a 2304-unit grid, and import meshes from one `SCS__*` region library (find it with `--import-sources`). Examples: Midtown = `MidTown_Static`/`_Dynamic` + `UES_Static_*`; Cannery Row = `JerseyDocks_Cannery_A` + `JerseyDocks_Cannery_X*`.
- Zones built from exit-named cells (`Shipping_A_NESW_A`, `Industrial_Processing_*`, `SiegeCity_A_*`) are laid out at run time. Every cell is stored at the origin, so placeholders at fixed positions can't work there. Industry City's main level is `Brooklyn_Docks_A` (Shipping cells).
- `--zone-placeholders` builds footprint prisms from the tiles (FBX, for Blender review). `--add-cell-placeholders` adds one mesh + component per cell to the main level's collection actor with `MinDrawDistance` (the MHO client honours it), rebuilt from the `.bak` with live edits carried over. Midtown (3500, `--shrink 0.8889`) and Cannery Row are written.
- `--add-sky-placeholders <pkg> none --ground-z Z --ground-box x0,y0,x1,y1 [--color R,G,B | --ground-material pkg.obj]` adds a ground-only plane as a second section of the sky sphere, for zones with a void past their edge. Industry City: z −220 (water ≈ −205), ±100224, the zone's water material, UV tile 2304 (the cells' `terrain_flat_filler` tiling).

## Copying materials between packages (UpkMeshScan): confirmed in-game

- `--copy-export <src.upk> <export-path> <dst.upk> [--cut prop,...] [--dry-run]` copies an export and everything it references. An exact parser finds every name and object reference: tags, struct arrays, known object arrays (`expressions`, `functionexpressions`), string arrays, the Material/MIC native resource, and Texture2D inline-mip offsets. Everything is renumbered into the target. Anything unknown stops the copy. Verification re-parses every copy and checks each reference resolves to the same path.
- Material/MIC native layout: quality mask (3 = two levels), then per level CompileErrors (0), TextureDependencyLengthMap (0), MaxTextureDependencyLength, Id GUID, NumUserTexCoords, UniformExpressionTextures (object refs), 6 ints, TextureLookups, 4 ints. For a MIC, add StaticParameters: BaseMaterialId, StaticSwitchParameters (name, value, override, GUID), then 3 empty arrays.
- **Never cut `materialfunctioninfos`.** Nulling it gave the default checker material (the function state GUIDs are checked). Cutting `physmaterial` (footstep splashes, sounds) is fine.
- First use: Industry City's water. `brooklyn_docks_lighting.brooklyn_docks_water_mat` was copied from `SCS__CH0201ShippingYardRegion_SF` into `Brooklyn_Docks_A` (85 exports, `--cut physmaterial`) and used on the ground plane. Its animated water covers the plane.

## Working style

Kurt is direct, evidence-driven, and tests every change with screenshots, terminal output, or FBX files. Keep explanations concise. Say plainly when a theory was wrong.
