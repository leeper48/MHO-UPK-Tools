using System.Globalization;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The choices of one import, read once (the command line reads them from the MFF_* environment switches; a GUI sets them
/// directly). Every switch here was an A/B test that became a setting; null / false = the automatic choice.
/// </summary>
sealed record ImportOptions
{
    /// <summary>Which MFF parts (null / "default" = the importer's guess of the body, "all", or names).</summary>
    public string? Parts { get; init; }
    /// <summary>Feet on the Floor: the mesh's Origin raised or lowered so the soles meet the floor where the stock model's do
    /// (users, 2026-10-22: some MFF models' feet hovered or sank). On by default.</summary>
    public bool FeetOnFloor { get; init; } = true;
    /// <summary>The Model tab's Feet slider when moved from its automatic value: the model moved by this (units, + up), in
    /// place of the automatic amount. Null: automatic (FeetOnFloor).</summary>
    public float? FeetShift { get; init; }
    /// <summary>FeetShift is the Model tab's automatic amount (measured on the model posed in the hero's idle), not one the user set.</summary>
    public bool FeetMeasured { get; init; }
    /// <summary>Keep the Source's Size (RetargetOptions.KeepSourceSize).</summary>
    public bool KeepSourceSize { get; init; }
    /// <summary>Lean (RetargetOptions.Lean): the body above the feet turned at the ankles, degrees, + forward.</summary>
    public float Lean { get; init; }   // (NaN: the automatic one)
    /// <summary>Game Joint Blend (RetargetOptions.JointFit).</summary>
    public bool JointFit { get; init; }
    /// <summary>The Model tab's weight paint (WeightPaint), applied last; null = none.</summary>
    public string? PaintFile { get; init; }
    /// <summary>An edited bone map (--map); null = the automatic one.</summary>
    public string? MapFile { get; init; }
    /// <summary>The animation the check render poses (name part); null = the first attack.</summary>
    public string? CheckAnimation { get; init; }

    /// <summary>MFF_MATERIAL: the material instance the MFF materials copy, "&lt;stock package&gt;[:&lt;instance&gt;]", or "base" for
    /// the base mesh's own; null = automatic (<see cref="MaterialChoice"/>).</summary>
    public string? Material { get; init; }
    /// <summary>MFF_VALUES_FROM: the instance (same package, same parent) whose scalar / vector values the template takes;
    /// null = automatic (Angela's armour values for her weapon material), "" = none.</summary>
    public string? ValuesFrom { get; init; }
    /// <summary>MFF_GLOW: emissivemultiplier when values are merged (default 3).</summary>
    public float Glow { get; init; } = 3f;
    /// <summary>MFF_NORMAL: "flat" = no generated normal map, a number = its strength; null = the default strength.</summary>
    public string? Normal { get; init; }
    /// <summary>MFF_SPEC: "neutral" (the _sp map left out), "matte" (no spec / rim / reflection), "raw" (the _sp map unpacked).</summary>
    public string? Spec { get; init; }
    /// <summary>MFF_REFLECT: which _sp channel drives v1 reflection (R, G, B, none); null = B.</summary>
    public string? Reflect { get; init; }
    /// <summary>MFF_PLACEHOLDER_MATERIALS=1: the base mesh's own materials (the Phase 3 test).</summary>
    public bool PlaceholderMaterials { get; init; }
    /// <summary>MFF_SUBDIVIDE=1: one level of Loop subdivision on the picked parts (for low-poly models).</summary>
    public bool Subdivide { get; init; }
    /// <summary>An edited model.fbx whose mesh replaces the retarget's (FBX round trip, <see cref="FbxReimport"/>).</summary>
    public string? ModelFbx { get; init; }
    /// <summary>An FBX as the source instead of an MFF model (0.11.3): skeleton proportions, mesh and textures from it
    /// (<see cref="FbxReimport.Load"/>); <see cref="Parts"/> then names its meshes.</summary>
    public string? SourceFbx { get; init; }
    /// <summary>Borrowed long hair (0.15.0; Hair ▾: 1-3, 4 = Mega Hair, 0 = none): the donor's hair bones in the mesh and
    /// their motion in a copy of the hero's animation sets (<see cref="HairAnims"/>). MFF_HAIR on the command line.</summary>
    public int Hair { get; init; }
    /// <summary>Borrowed cape (Cape ▾: 1-3; 0.17.0: built like the hair, <see cref="HairAnims"/>). MFF_CAPE.</summary>
    public int Cape { get; init; }
    /// <summary>Other borrowed rigs (coat, dress, loincloth, tail, chains, wings: BorrowedRig.Kind, donor number). MFF_RIGS ("Coat:1,Tail:2").</summary>
    public IReadOnlyList<(BorrowedRig.Kind Kind, int Number)> Rigs { get; init; } = [];
    /// <summary>FBX edits (0.16.0): animation name → an FBX whose clip replaces it in the built mod (<see cref="AnimEdits"/>).</summary>
    public IReadOnlyDictionary<string, string>? AnimFbx { get; init; }
    /// <summary>Only the package (the Mod Manager's Model tab puts it into its draft): no mod folder or .zip.</summary>
    public bool NoMod { get; init; }
    /// <summary>The Materials tab's overrides file (materials\&lt;source&gt;.json; MaterialOverrides), or null.</summary>
    public string? MaterialOverrides { get; init; }
    /// <summary>The Model tab's Size: the character this much larger or smaller in game than the game has it (1 = unchanged),
    /// written as the costume's mesh component Scale (<see cref="ImportBuild"/>.ApplySize).</summary>
    public float Size { get; init; } = 1;
    /// <summary>A prop target's Prop Fit sliders (move, turn, size after the fit); null: none.</summary>
    public PropAdjust? PropFit { get; init; }
    /// <summary>The package the game's size is read from when the build starts from a copy that already holds another build
    /// (a shield built onto a package whose body was built: its size is set already, and must not be multiplied); null = the
    /// start package.</summary>
    public string? SizeFrom { get; init; }

    public static ImportOptions FromEnvironment(string? parts, string? mapFile, string? checkAnimation)
    {
        static string? Env(string n) => Environment.GetEnvironmentVariable(n);
        return new ImportOptions
        {
            MaterialOverrides = Env("MFF_OVERRIDES"),   // a Materials tab overrides file (materials\<source>.json), for command-line builds and tests
            Parts = parts, MapFile = mapFile, CheckAnimation = checkAnimation, KeepSourceSize = Env("MFF_KEEPSIZE") == "1", JointFit = Env("MFF_JOINTFIT") == "1",
            Material = Env("MFF_MATERIAL") is { Length: > 0 } m ? m : null,
            ValuesFrom = Env("MFF_VALUES_FROM"),
            Glow = float.TryParse(Env("MFF_GLOW"), NumberStyles.Float, CultureInfo.InvariantCulture, out float g) ? g : 3f,
            Normal = Env("MFF_NORMAL"),
            Spec = Env("MFF_SPEC"),
            Reflect = Env("MFF_REFLECT"),
            PlaceholderMaterials = Env("MFF_PLACEHOLDER_MATERIALS") == "1",
            Subdivide = Env("MFF_SUBDIVIDE") == "1",
            // MHO_LEAN=<degrees> or auto (the Lean slider; unset: none, as before)
            Lean = Env("MHO_LEAN") == "auto" ? float.NaN : float.TryParse(Env("MHO_LEAN"), NumberStyles.Float, CultureInfo.InvariantCulture, out float ln) ? ln : 0,
            ModelFbx = Env("MFF_MODEL_FBX") is { Length: > 0 } mf ? mf : null,
            SourceFbx = Env("MFF_SOURCE_FBX") is { Length: > 0 } sf ? sf : null,
            Hair = int.TryParse(Env("MFF_HAIR"), out int hn) ? hn : 0,
            Cape = int.TryParse(Env("MFF_CAPE"), out int cn) ? cn : 0,
            Rigs = BorrowedRig.ParsePicks(Env("MFF_RIGS")),
            // MFF_ANIM_FBX=name=file.fbx;name2=file2.fbx (the command line's FBX edits)
            AnimFbx = Env("MFF_ANIM_FBX") is { Length: > 0 } ae ? ae.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase) : null,
        };
    }

    /// <summary>The A/B variant in the mod's name ("" for the automatic build).</summary>
    public string Variant(string? donor) =>
        (Normal is { Length: > 0 } nv ? $", normals {nv}" : "")
        + (Spec is "neutral" or "matte" or "raw" ? ", spec " + Spec : "")
        + (donor is { } md && !MaterialChoice.IsAutomatic(md) ? ", material " + md.Split(':').Last() : donor == null ? ", material base" : "")
        + (Reflect is { Length: > 0 } rf ? ", reflect " + rf : "")
        + (Subdivide ? ", smooth" : "")
        + (ModelFbx != null ? ", edited" : "")
        + (Cape > 0 ? ", " + BorrowedRig.Title(BorrowedRig.Kind.Cape, Cape) : "")
        + (Hair > 0 ? ", " + BorrowedRig.Title(BorrowedRig.Kind.Hair, Hair) : "")
        + string.Concat(Rigs.Select(x => ", " + BorrowedRig.Title(x.Kind, x.Number)))
        + (AnimFbx is { Count: > 0 } af ? $", {af.Count} animation(s) edited" : "")
        + (Math.Abs(Size - 1) > 1e-4 ? $", size {Size * 100:0} %" : "");
}
