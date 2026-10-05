using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>One bone of the new reference skeleton: MHO name, parent and orientation, position from the MFF model.</summary>
sealed class RefBone
{
    public required string Name;
    public int Parent;
    /// <summary>Bone → model, MHO model space (row vectors).</summary>
    public Matrix4x4 Global;
    public bool Mapped;               // an MFF bone drives it (else placed from the MHO skeleton around it)
    public string? From;              // the MFF bone mapped to it
    public Vector3 Position => Global.Translation;
}

sealed class RefSection
{
    public required string Material;
    public required Textures Tex;
    public required Vector3[] Pos;        // MHO model space
    public required Vector3[] Normal;
    public required Vector2[] Uv;
    public required int[] Tris;           // MHO winding (clockwise about the normal)
    public required (int Bone, float Weight)[][] Weights;   // MHO bone indices, ≤ 4, sum 1
}

/// <summary>The MFF model on the MHO skeleton: the result of <see cref="Retarget.Run"/>.</summary>
sealed class Retargeted
{
    /// <summary>The MFF model it came from; null when the source is an FBX file (FbxReimport.Load).</summary>
    public required MffModel? Source;
    public required MhoSkeleton Target;
    public List<RefBone> Bones { get; } = new();
    public List<RefSection> Sections { get; } = new();
    public Dictionary<string, string> Map { get; } = new(StringComparer.OrdinalIgnoreCase);   // MFF bone → MHO bone
    public float Scale;                  // MFF cm → MHO units
    public List<string> Notes { get; } = new();
    /// <summary>Hair hanging below the neck and the coat / body vertices it can rest on ((section, vertex)), for the clip check.</summary>
    public List<(int Section, int V)> HairVerts { get; } = new();
    public List<(int Section, int V)> BodyVerts { get; } = new();
    /// <summary>Extra chains matched (MFF chain → MHO chain, with the fit).</summary>
    public List<string> Chains { get; } = new();
    /// <summary>The chain pairs made (for the bone map file).</summary>
    public List<(string MffRoot, string MhoRoot, float Fit, List<string> Bones)> ChainPairs { get; } = new();
    /// <summary>MFF chains (first bone) left on their parent.</summary>
    public List<string> UnpairedChains { get; } = new();
    /// <summary>The mapping came from a bone map file (--map), not the automatic rules.</summary>
    public bool FromFile;
}
