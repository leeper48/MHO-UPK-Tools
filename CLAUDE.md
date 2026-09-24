# Marvel Heroes Omega Modding Tools

C# / .NET 8 tools for reading and writing Marvel Heroes Omega `.upk` packages (a UE3 fork). They export meshes and animations to FBX, import FBX static meshes back into packages (confirmed working in-game), and are working toward importing FBX animation. Windows-only. The owner is Kurt.

## Repo layout

```
AnimExportCli/   Skeletal mesh + animation export to FBX; FBX-to-UPK animation import (in progress). CLI + WinForms GUI. v1.3.1
UpkMeshScan/     StaticMesh scan, export (FBX + textures), import (FBX -> package), diagnostics. CLI only, AssimpNet. v1.3.1
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
- Diagnostics: `--dump-export`, `--inspect-fbx`, `--find-name`, `--import-sources`, `--mesh-users`, `--texture-info`, `--export-textures`.
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

## Working style

Kurt is direct, evidence-driven, and tests every change with screenshots, terminal output, or FBX files. Keep explanations concise. Say plainly when a theory was wrong.
