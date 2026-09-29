using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Moving a costume mod onto another hero's costume (Kurt, 2026-09-29), phase 2: the target costume's stock package with the
/// mod's mesh copied in (MeshCopy / MPM ExportCopy), then the costume's initialskeletalmesh component pointed at it.
///
/// Why this way (not CostumeMove's rename): a costume's class is its hero's (Thor's has hammer-throw settings …), so the
/// class, default object and components must stay the target's; only the look moves. The package keeps the target's stock
/// GUID (the game checks it), its class, animations come from the target hero (humanoid heroes share 80 core bones, checked
/// on 8 heroes; bones a mesh has beyond the target's rig stay at rest).
/// Steps: (1) the mod's imports of materials / textures from its hero's base package (She-Hulk's hair material lives in
/// UC__MarvelPlayer_SheHulk_SF, which isn't loaded for another hero) are copied into the target first, as exports under the
/// same path; (2) the mesh is copied with those imports pointed at the copies; (3) the component's SkeletalMesh property is
/// set to the copy and its PhysicsAsset to none (the target's physics asset is built for the target's bones).
/// </summary>
static class CrossMove
{
    static readonly HashSet<string> Content = new(StringComparer.OrdinalIgnoreCase) { "Material", "MaterialInstanceConstant", "Texture2D" };

    static readonly object consoleGate = new();
    /// <summary>Runs the copier with its console output captured (the GUI has no console; the refusal reason goes in the error).</summary>
    internal static T Quiet<T>(Func<T> f, out string said)
    {
        lock (consoleGate)
        {
            var old = Console.Out; var sw = new StringWriter();
            Console.SetOut(sw);
            try { return f(); } finally { Console.SetOut(old); said = sw.ToString(); }
        }
    }

    /// <summary>An import's path (outer chain; an outer can be an export).</summary>
    static string ImportPath(Package p, int i)
    {
        var parts = new List<string>();
        for (int r = -(i + 1), guard = 0; r != 0 && guard < 32; guard++)
        {
            if (r < 0) { var im = p.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
            else { parts.Insert(0, p.PathOf(p.Exports[r - 1])); break; }
        }
        return string.Join('.', parts);
    }

    /// <summary>
    /// The target costume package with the mod's mesh in it. <paramref name="baseHero"/>: the source hero's base package (the
    /// mod's copy, else the game's) for step 1, or null. Returns the verified package bytes; throws with the reason.
    /// </summary>
    public static byte[] Build(string modPackage, string meshName, string targetStock, string targetClass, string? baseHero, List<string> log, bool sounds = true,
        string? sourceClass = null)
    {
        MeshCopy.Register();
        var src = Package.Open(modPackage);
        var dst = Package.Open(targetStock);
        int mesh = Array.FindIndex(src.Exports, e => e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && src.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        if (mesh < 0) throw new InvalidDataException($"no skeletal mesh '{meshName}' in {Path.GetFileName(modPackage)}");

        // (1) Imported materials / textures from the source hero's base package, copied in under their own paths.
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseHero != null && File.Exists(baseHero))
        {
            var bas = Package.Open(baseHero);
            for (int i = 0; i < src.Imports.Length; i++)
            {
                if (!Content.Contains(src.Imports[i].ClassName)) continue;
                string path = ImportPath(src, i);
                int bi = Array.FindIndex(bas.Exports, e => bas.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase) && bas.ClassOf(e).Equals(src.Imports[i].ClassName, StringComparison.OrdinalIgnoreCase));
                if (bi < 0) continue;
                if (!dst.Exports.Any(e => dst.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    var r = Quiet(() => ExportCopy.Copy(bas, bi, dst, []), out _) ?? throw new InvalidDataException($"{path} couldn't be copied from {Path.GetFileName(baseHero)}");
                    dst = Package.FromBytes(r.Output);
                }
                replace[path] = path;
                log.Add($"copied from {Path.GetFileName(baseHero)}: {path} ({src.Imports[i].ClassName})");
            }
        }

        // (2) The mesh and everything it needs; with sounds, also the package's sound events and banks (AkEvent / AkBank:
        // a sound pack's new events are AkEvents in the costume package, e.g. Miles Morales' 82).
        var extra = sounds
            ? Enumerable.Range(0, src.Exports.Length).Where(i => src.ClassOf(src.Exports[i]) is var c && (c.Equals("AkEvent", StringComparison.OrdinalIgnoreCase) || c.Equals("AkBank", StringComparison.OrdinalIgnoreCase))).ToList()
            : [];
        if (extra.Count > 0) log.Add($"sound events and banks copied: {extra.Count}");
        var copy = Quiet(() => ExportCopy.Copy(src, mesh, dst, [], null, replace, extra), out string said)
                   ?? throw new InvalidDataException($"{meshName} couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not supported")).Select(l => l.Trim())));
        var pkg = Package.FromBytes(copy.Output);
        log.Add($"mesh {src.PathOf(src.Exports[mesh])} is export #{copy.RootRef}");

        // (2b) The voice (Kurt: audio across heroes). A costume's voice set is its class default's soundscomponent
        // (MarvelEntityCompSounds): each situation (death, level up, crit, damage, banter by enemy, mission banter …)
        // names a sound event (AkEvent, which names its bank). Stock costumes leave it empty (24 bytes: the hero's default
        // applies); a voice mod fills it (Miles: 15,744 bytes, its events renamed by MHSFXEditor to play its own lines). The
        // source's set — the mod's, else its hero's stock one in the base package — is copied with its events and banks,
        // and becomes the target costume's, so the target hero's powers play the source's lines.
        int voiceComp = -1;
        byte[]? voiceData = null;
        if (sourceClass != null)
        {
            string lower = sourceClass.ToLowerInvariant(), heroClass = "marvelplayer_" + lower.Split('_')[1];
            var candidates = new List<(string Path, string Comp, string Default)>
            {
                (modPackage, $"marvelgamecontent.default__{lower}.soundscomponent", $"marvelgamecontent.default__{lower}"),
            };
            if (baseHero != null) candidates.Add((baseHero, $"marvelgamecontent.default__{heroClass}.soundscomponent", $"marvelgamecontent.default__{heroClass}"));
            foreach (var (path, compName, defName) in candidates)
            {
                var vp = path == modPackage ? src : Package.Open(path);
                int vc = Array.FindIndex(vp.Exports, e => vp.PathOf(e).Equals(compName, StringComparison.OrdinalIgnoreCase));
                if (vc < 0 || vp.Exports[vc].SerialSize <= 64) continue;          // empty: this costume uses its hero's
                string targetDefault = $"marvelgamecontent.default__{targetClass.ToLowerInvariant()}";
                var got = VoiceSet.CopyInto(pkg, targetDefault, vp, vc, log);
                if (got == null) break;
                (pkg, voiceComp, voiceData) = got.Value;
                log.Add($"voice: {compName} from {Path.GetFileName(path)} ({vp.Exports[vc].SerialSize:N0} bytes, with its sound events)");
                break;
            }
        }

        // (3) The costume's mesh component: SkeletalMesh → the copy, PhysicsAsset → none.
        string compPath = $"marvelgamecontent.default__{targetClass}.initialskeletalmesh";
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0) throw new InvalidDataException($"no {compPath} in the target");
        byte[] d = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var tags = TagWalker.Walk(pkg, d, 16) ?? throw new InvalidDataException("the mesh component's properties don't read");
        var skel = tags.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) ?? throw new InvalidDataException("the mesh component has no SkeletalMesh property");
        int oldMesh = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(skel.ValueAt));
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(skel.ValueAt), copy.RootRef);
        if (tags.FirstOrDefault(t => t.Name.Equals("PhysicsAsset", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } phys)
        {
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(phys.ValueAt), 0);
            log.Add("physics asset: none (the target's is built for its own bones)");
        }
        log.Add($"component: SkeletalMesh {(oldMesh > 0 ? pkg.PathOf(pkg.Exports[oldMesh - 1]) : oldMesh.ToString())} → #{copy.RootRef}");
        var replaceData = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => d };
        if (voiceComp >= 0 && voiceData != null) replaceData[voiceComp] = _ => voiceData;   // the target costume's voice set = the source's
        byte[] output = PackageRebuilder.Rebuild(pkg, replaceData, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, replaceData.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        // The GUID stays the target's (the game checks it): the target stock package's summary is kept as it was.
        var t0 = Package.Open(targetStock);
        if (!File.ReadAllBytes(targetStock).AsSpan(t0.GenerationsAt - 16, 16).SequenceEqual(output.AsSpan(Package.FromBytes(output).GenerationsAt - 16, 16)))
            throw new InvalidDataException("the GUID isn't the target's");
        return output;
    }

    /// <summary>"She-Hulk (on Storm Modern)": a move to another hero names the hero too.</summary>
    public static string NewName(Mod mod, Costume target) => $"{mod.Name} (on {target.Short.Split('/')[0]} {target.Title})";

    /// <summary>
    /// The moved costume as a new mod (at the top, disabled; the original untouched): the target package with the mod's mesh
    /// (and sound events), the costume's own icons under the target's names, its text under the target's IDs, and its sound
    /// packs. Icons, text and sound-pack checks are CostumeMove's (they don't depend on the hero). Returns the folder or null.
    /// </summary>
    public static string? CreateMod(ModLibrary lib, Mod mod, string srcFile, Costume source, Costume target, List<Costume> all, GameState game,
        StockCatalog? catalog, List<string> log, out string? error, Mod? replace = null)
    {
        error = null;
        var originals = new Originals(lib.DataFolder, game);
        string? stock = originals.Find(target.Package);
        if (stock == null) { error = $"no stock copy of {target.Package}"; return null; }
        string modPkg = Path.Combine(mod.Folder, srcFile);
        string meshName = ModMeshes.List(mod).Where(r => r.Package.Equals(srcFile, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).FirstOrDefault() ?? "";
        string heroBase = "UC__MarvelPlayer_" + source.Class.Split('_')[1] + "_SF.upk";
        string? baseHero = File.Exists(Path.Combine(mod.Folder, heroBase)) ? Path.Combine(mod.Folder, heroBase) : File.Exists(Path.Combine(game.Cooked, heroBase)) ? Path.Combine(game.Cooked, heroBase) : null;
        var plan = CostumeMove.Make(mod, srcFile, source, target, all, game.Cooked, catalog);   // icons, text, sound packs
        foreach (var i in plan.Icons)
            if (i.DdsSize is not { } a || i.TargetSize is not { } t || a.W != t.W || a.H != t.H)
            { error = $"{i.Kind}: {i.Dds} isn't the size of the target's {i.To} (resizing isn't built yet)"; return null; }
        string work = Path.Combine(lib.DataFolder, "costume-move-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            // No sounds across heroes (Kurt, option 1): a pack's events are played by its own hero's powers and animations
            // (MHSFXEditor renames the costume package's own AkEvents), which another hero doesn't have.
            byte[] built = Build(modPkg, meshName, stock, target.Class, baseHero, log, sounds: false, sourceClass: source.Class);
            string file = Path.Combine(work, target.Package);
            File.WriteAllBytes(file, built);
            var d = ModDraft.From(mod);
            d.Name = NewName(mod, target);
            d.Packages = [(target.Package, file)];
            var byPackage = Applier.IconPackages.Select((p, k) => (p.File, k)).ToDictionary(x => x.File, x => x.k, StringComparer.OrdinalIgnoreCase);
            d.Textures = [[], [], []];
            d.Extra = [];
            foreach (var i in plan.Icons)
            {
                string srcDds = Path.Combine(mod.Folder, i.Dds);
                if (byPackage.TryGetValue(i.Package, out int k)) d.Textures[k].Add((i.To, srcDds)); else d.Extra.Add((i.Package, i.To, srcDds));
            }
            d.Strings = [.. plan.Strings.Select(sm => mod.Strings.First(x => x.Id == sm.From && x.Language.Equals(sm.Language, StringComparison.OrdinalIgnoreCase)) with { Id = sm.To, File = sm.File, Variants = null })];
            // The voice set moves with its events, so a mod's sound pack (the audio of its renamed events) goes along.
            d.SoundPacks = log.Any(l => l.StartsWith("voice: ")) ? [.. mod.Manifest.AudioPacks.Select(f => Path.Combine(mod.Folder, f))] : [];
            d.PreviewImage = null; d.PreviewViews = null;
            d.PostNexus = d.PostDiscord = null; d.PostImages = [];
            d.NexusModId = null; d.Changes = ""; d.Changelog = [];
            if (replace != null) { d.Tags = [.. replace.ModTags]; d.Notes = replace.Manifest.Notes ?? ""; }
            else d.Notes = (d.Notes.Length > 0 ? d.Notes.TrimEnd() + Environment.NewLine : "") + $"Moved from {mod.Name} ({source.Short.Replace(".prototype", "")} → {target.Short.Replace(".prototype", "")}).";
            log.Add($"icons: {plan.Icons.Count}; text: {plan.Strings.Count}; sound packs: {d.SoundPacks.Count}");
            return ModWriter.Save(lib, d, replace, out error);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { error = Friendly(ex.Message); return null; }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } }
    }

    /// <summary>A refused copy in plain words (checked on the library: 146 of 155 costume meshes copy; APEX cloth, animated
    /// materials and a mesh's own physics asset don't yet).</summary>
    static string Friendly(string why) =>
        why.Contains("clothing", StringComparison.OrdinalIgnoreCase) ? "Its model uses APEX cloth (a cape or coat simulated by PhysX), which can't be moved to another hero yet."
        : why.Contains("timevarying", StringComparison.OrdinalIgnoreCase) ? "It uses an animated material that can't be moved to another hero yet."
        : why.Contains("rb_bodysetup", StringComparison.OrdinalIgnoreCase) ? "Its model has its own physics setup, which can't be moved to another hero yet."
        : why;
}
