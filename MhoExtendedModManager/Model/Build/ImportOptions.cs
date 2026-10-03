using System.Globalization;

namespace MhoMffImporter;

/// <summary>
/// The choices of one import, read once (the command line reads them from the MFF_* environment switches; a GUI sets them
/// directly). Every switch here was an A/B test that became a setting; null / false = the automatic choice.
/// </summary>
sealed record ImportOptions
{
    /// <summary>Which MFF parts (null / "default" = the importer's guess of the body, "all", or names).</summary>
    public string? Parts { get; init; }
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
    /// <summary>FBX edits (0.16.0): animation name → an FBX whose clip replaces it in the built mod (<see cref="AnimEdits"/>).</summary>
    public IReadOnlyDictionary<string, string>? AnimFbx { get; init; }

    public static ImportOptions FromEnvironment(string? parts, string? mapFile, string? checkAnimation)
    {
        static string? Env(string n) => Environment.GetEnvironmentVariable(n);
        return new ImportOptions
        {
            Parts = parts, MapFile = mapFile, CheckAnimation = checkAnimation,
            Material = Env("MFF_MATERIAL") is { Length: > 0 } m ? m : null,
            ValuesFrom = Env("MFF_VALUES_FROM"),
            Glow = float.TryParse(Env("MFF_GLOW"), NumberStyles.Float, CultureInfo.InvariantCulture, out float g) ? g : 3f,
            Normal = Env("MFF_NORMAL"),
            Spec = Env("MFF_SPEC"),
            Reflect = Env("MFF_REFLECT"),
            PlaceholderMaterials = Env("MFF_PLACEHOLDER_MATERIALS") == "1",
            Subdivide = Env("MFF_SUBDIVIDE") == "1",
            ModelFbx = Env("MFF_MODEL_FBX") is { Length: > 0 } mf ? mf : null,
            SourceFbx = Env("MFF_SOURCE_FBX") is { Length: > 0 } sf ? sf : null,
            Hair = int.TryParse(Env("MFF_HAIR"), out int hn) ? hn : 0,
            Cape = int.TryParse(Env("MFF_CAPE"), out int cn) ? cn : 0,
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
        + (AnimFbx is { Count: > 0 } af ? $", {af.Count} animation(s) edited" : "");
}
