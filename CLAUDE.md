# Marvel Heroes Omega Modding Tools

C# / .NET 8 tools for reading and writing Marvel Heroes Omega `.upk` packages (a UE3 fork), exporting meshes and animations to FBX, and importing FBX animation back into packages. Windows-only. The owner is Kurt.

## Repo layout

```
AnimExportCli/   Skeletal mesh + animation export to FBX; FBX-to-UPK animation import (in progress). CLI + WinForms GUI. v1.3.1
UpkMeshScan/     Folder scanner: lists StaticMesh / SkeletalMesh exports per package into a text report. v1.0.0
```

Each tool has its own `build.bat`. Neither tool references the other yet. The planned merge is to fold UpkMeshScan into AnimExportCli as `--scan-meshes`, reusing AnimExportCli's package reader.

## Rules that are not negotiable

1. **Never modify an original game file.** Every write goes to a new copy (for example `<name>_modded.upk`). Refuse, or ask first, if a code path would overwrite the source.
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
- `*_SF.upk` character packages (for example `UC__MarvelPlayer_WinterSoldier_SF.upk`) are **uncompressed**.
- Character weapons (knife, pistol, mine, launcher…) are **SkeletalMesh**, not StaticMesh. Static meshes in character packages are mostly VFX (for example dodge afterimages). Buildings and props live in environment/zone packages.
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
  - Patch the export table. Prefer appending the resized export at the end of the file and rewriting only its table entry, rather than shifting data.
  - Write an LZO1X **compressor** (only a decompressor exists today). Verify it by round-tripping through the existing decompressor.
  - Add a `--verify-package-write` self-test in the style of the other verifiers.
- **Phase 4** (CLI/GUI wiring): deliberately last.

Open questions for Kurt before Phase 3 design:
1. Does the source FBX contain keyframes for the new bones?
2. Where does the bone/track-name list live: AnimSet or AnimSequence?
3. Is a before/after UPK pair from the other modder's tool available for a binary diff?

## Next after that: static mesh export

UpkMeshScan works on real files. The next step is a StaticMesh body parser and FBX export, developed against a real environment package (a building or car). Don't guess the fork's StaticMesh layout; dump real bytes first.

## Working style

Kurt is direct, evidence-driven, and tests every change with screenshots, terminal output, or FBX files. Keep explanations concise. Say plainly when a theory was wrong.
