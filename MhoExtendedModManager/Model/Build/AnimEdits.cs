using AnimExportCli.Animation;
using AnimExportCli.Fbx;
using Assimp;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Edits brought back from Blender (0.16.0, Kurt): an FBX imported in the preview's Single Animation ▾ menu either replaces the model's
/// mesh (its own weights, so weight painting comes along: <see cref="FbxReimport.Apply"/>) or one of the base hero's
/// animations (its clip, read by AnimExportCli's FbxAnimationImporter), or both. The FBX files are copied into the edits
/// folder first, so a later change to the original (or its deletion) doesn't change the work. The build writes both
/// (<see cref="ImportOptions.ModelFbx"/>, <see cref="ImportOptions.AnimFbx"/>).
/// </summary>
sealed class AnimEdits
{
    /// <summary>The FBX whose mesh replaces the retarget's (null = the retarget's own).</summary>
    public string? ModelFbx { get; set; }
    /// <summary>Animation name → the FBX whose clip replaces it.</summary>
    public SortedDictionary<string, string> Anims { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Any => ModelFbx != null || Anims.Count > 0;

    /// <summary>One line per edit, for undo states and project files ("model=…", "anim:name=…").</summary>
    public string Serialize() =>
        string.Join("\n", (ModelFbx != null ? new[] { "model=" + ModelFbx } : []).Concat(Anims.Select(kv => $"anim:{kv.Key}={kv.Value}")));

    public static AnimEdits Parse(string? text)
    {
        var e = new AnimEdits();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("model=")) e.ModelFbx = line[6..];
            else if (line.StartsWith("anim:") && line.IndexOf('=') is int eq and > 5) e.Anims[line[5..eq]] = line[(eq + 1)..];
        }
        return e;
    }

    /// <summary>The same edits with files inside <paramref name="folder"/> named relative to it (kept with a mod, which moves).</summary>
    public AnimEdits Relative(string folder) => Map(f => Path.IsPathRooted(f) && Path.GetFullPath(f).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(folder, f) : f);

    /// <summary>Relative file names made full again, from <paramref name="folder"/>.</summary>
    public AnimEdits Resolved(string folder) => Map(f => Path.IsPathRooted(f) ? f : Path.GetFullPath(Path.Combine(folder, f)));

    AnimEdits Map(Func<string, string> f)
    {
        var e = new AnimEdits { ModelFbx = ModelFbx != null ? f(ModelFbx) : null };
        foreach (var (k, v) in Anims) e.Anims[k] = f(v);
        return e;
    }

    /// <summary>What an FBX holds: its clips (name, frames) and how many of its meshes are skinned.</summary>
    public sealed record Contents(List<(string Name, double Frames)> Clips, int SkinnedMeshes, int Meshes);

    public static Contents Inspect(string fbx)
    {
        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(fbx, PostProcessSteps.None);
        return new Contents(scene.Animations.Select(a => (a.Name ?? "", a.DurationInTicks)).ToList(),
            scene.Meshes.Count(m => m.HasBones), scene.MeshCount);
    }

    /// <summary>The edits folder for one source on one base package (Settings.Home\edits\&lt;source&gt; on &lt;package&gt;).</summary>
    public static string Folder(string source, string package) =>
        Path.Combine(Settings.Home, "edits", $"{Safe(source)} on {Path.GetFileNameWithoutExtension(package)}");

    /// <summary>A copy of <paramref name="fbx"/> in <paramref name="folder"/> under <paramref name="name"/> (…_2 when that
    /// name holds another file); the same file already there is reused.</summary>
    public static string Keep(string fbx, string folder, string name)
    {
        Protected.CheckWrite(folder);
        Directory.CreateDirectory(folder);
        if (Path.GetFullPath(fbx).StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return fbx;
        byte[] data = File.ReadAllBytes(fbx);
        string stem = Safe(name), target = Path.Combine(folder, stem + ".fbx");
        for (int k = 2; File.Exists(target); k++)
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(data)) return target;
            target = Path.Combine(folder, $"{stem}_{k}.fbx");
        }
        File.WriteAllBytes(target, data);
        return target;
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    static readonly Dictionary<string, (DateTime Stamp, BoneAnimation Anim)> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The clip of <paramref name="fbx"/> as an animation of <paramref name="name"/>: keyed by bone name in engine space (the
    /// exporter's conversion undone), its length in seconds from <paramref name="secondsPerFrame"/> (FbxAnimationImporter
    /// gives the last frame there, not seconds). Cached by the file's write time.
    /// </summary>
    public static BoneAnimation Load(string fbx, string name, float secondsPerFrame)
    {
        var stamp = File.GetLastWriteTimeUtc(fbx);
        BoneAnimation raw;
        lock (cache)
        {
            if (!cache.TryGetValue(fbx, out var c) || c.Stamp != stamp) cache[fbx] = c = (stamp, FbxAnimationImporter.Read(fbx));
            raw = c.Anim;
        }
        float last = raw.Tracks.Values.SelectMany(t => t.RotationKeys.Select(k => k.TimeFrame).Concat(t.PositionKeys.Select(k => k.TimeFrame))).DefaultIfEmpty(0).Max();
        return new BoneAnimation { Name = name, DurationSeconds = last * (secondsPerFrame > 0 ? secondsPerFrame : 1f / 30), Tracks = raw.Tracks };
    }

    /// <summary>An imported clip as the game would play it: positions only for the bones its set takes positions for (the
    /// rest come from the mesh: UE3 sets are rotation-only), timed at the game animation's frame rate.</summary>
    public static BoneAnimation ForGame(string fbx, MhoExtendedModManager.AnimRef ar, BoneAnimation? original) => ForGame(fbx, ar.Name, ar.TranslationBones, original);

    public static BoneAnimation ForGame(string fbx, string name, IReadOnlySet<string>? translationBones, BoneAnimation? original)
    {
        var ar = new { Name = name, TranslationBones = translationBones };
        var a = Load(fbx, ar.Name, SecondsPerFrame(original));
        var tracks = a.Tracks.ToDictionary(kv => kv.Key,
            kv => KeepsPosition(translationBones, kv.Key) ? kv.Value : new BoneTrack { RotationKeys = kv.Value.RotationKeys }, StringComparer.OrdinalIgnoreCase);
        // the root always plays the game's: Blender's FBX export bakes its axis conversion into the root (180° turned and moved:
        // the whole model 0.6 units off, --fbx-vs-game on the MHO Actions add-on's own Batch Export), and the game plays it in
        // place anyway
        if (original != null && original.Tracks.TryGetValue("root", out var gameRoot)) tracks["root"] = gameRoot;
        // a bone the FBX doesn't animate keeps the game's track (Export FBX writes no channel for root and the *_offset bones,
        // single-key tracks: without them the whole model sat 0.58 units off, --edit-roundtrip)
        if (original != null)
            foreach (var (bone, t) in original.Tracks)
                if (!tracks.ContainsKey(bone)) tracks[bone] = KeepsPosition(translationBones, bone) ? t : new BoneTrack { RotationKeys = t.RotationKeys };
        return new BoneAnimation { Name = a.Name, DurationSeconds = a.DurationSeconds, Tracks = tracks };
    }

    /// <summary>Whether a bone's position keys count in the game: every bone in a set that isn't rotation-only, else the listed
    /// translation bones and the root (its height; the preview plays it in place).</summary>
    public static bool KeepsPosition(MhoExtendedModManager.AnimRef ar, string bone) => KeepsPosition(ar.TranslationBones, bone);

    public static bool KeepsPosition(IReadOnlySet<string>? translationBones, string bone) =>
        translationBones == null || translationBones.Contains(bone) || bone.Equals("root", StringComparison.OrdinalIgnoreCase);

    /// <summary>A game animation with position keys only where the game takes them (as <see cref="ForGame"/> does).</summary>
    public static BoneAnimation GamePositions(BoneAnimation a, IReadOnlySet<string>? translationBones) => translationBones == null ? a : new BoneAnimation
    {
        Name = a.Name, DurationSeconds = a.DurationSeconds,
        Tracks = a.Tracks.ToDictionary(kv => kv.Key, kv => KeepsPosition(translationBones, kv.Key) ? kv.Value : new BoneTrack { RotationKeys = kv.Value.RotationKeys }, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Seconds per frame of a decoded game animation (its length over its last key); 1/30 without one.</summary>
    public static float SecondsPerFrame(BoneAnimation? a)
    {
        if (a == null) return 1f / 30;
        var (frames, seconds) = MhoExtendedModManager.MeshAnimator.Span(a);
        return frames > 0 && seconds > 0 ? seconds / frames : 1f / 30;
    }
}
