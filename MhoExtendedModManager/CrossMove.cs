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
/// set to the copy and its PhysicsAsset to the source mesh's own (copied; 0.37.25: APEX cloth needs it), else none (the target's is built for the target's bones).
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
        string? sourceClass = null, string? cooked = null, List<VoiceOffEntry>? autoOff = null)
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

        // (2c) The source mesh's own physics asset (Kurt, 2026-09-30: Doctor Strange's cape stayed static on Colossus; APEX cloth
        // needs the component's physics asset, and the source's is built for the copied mesh's bones). From the source
        // costume's component, else the "<mesh>_physics" beside the mesh. If it can't be copied, the component gets none.
        int physRef = 0;
        int pa = SourcePhysics(src, meshName, sourceClass);
        if (pa >= 0)
        {
            var pc = Quiet(() => ExportCopy.Copy(src, pa, pkg, [], null, replace), out string psaid);
            if (pc != null) { pkg = Package.FromBytes(pc.Output); physRef = pc.RootRef; log.Add($"physics asset {src.PathOf(src.Exports[pa])} is export #{physRef}"); }
            else log.Add("physics asset: not copied (" + string.Join(" ", psaid.Split(Environment.NewLine).Where(l => l.Contains("can't")).Select(l => l.Trim())) + "); the component gets none");
        }

        // (2d) The target hero's sockets the moved mesh lacks (Kurt, 2026-10-01: Photonic Devastation's beam was invisible on
        // Spider-Noir moved onto Captain Marvel). Powers spawn effects at sockets by name (her beam at socket_beam), and a
        // moved mesh only has its own hero's. Each socket of the target's stock mesh that the copy doesn't have, on a bone the
        // copy's skeleton has, is added: a copy of the stock socket object (names and numbers only) owned by the moved mesh,
        // appended to its Sockets list.
        var socketAdds = new List<NewExport>();
        byte[]? meshData = null;
        var socketData = new Dictionary<int, byte[]>();
        {
            string cp = $"marvelgamecontent.default__{targetClass}.initialskeletalmesh";
            int ci = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(cp, StringComparison.OrdinalIgnoreCase));
            int stockMesh = -1;
            if (ci >= 0 && TagWalker.Walk(pkg, pkg.ReadExportBytes(pkg.Exports[ci]).ToArray(), 16) is { } ct
                && ct.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } st)
                stockMesh = BinaryPrimitives.ReadInt32LittleEndian(pkg.ReadExportBytes(pkg.Exports[ci]).AsSpan(st.ValueAt)) - 1;
            int moved = copy.RootRef - 1;
            if (stockMesh >= 0 && stockMesh != moved)
                (meshData, socketAdds, socketData) = AddMissingSockets(pkg, stockMesh, moved, log);
        }

        // (3) The costume's mesh component: SkeletalMesh → the copy, PhysicsAsset → the copied one (else none).
        string compPath = $"marvelgamecontent.default__{targetClass}.initialskeletalmesh";
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0) throw new InvalidDataException($"no {compPath} in the target");
        byte[] d = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var tags = TagWalker.Walk(pkg, d, 16) ?? throw new InvalidDataException("the mesh component's properties don't read");
        var skel = tags.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) ?? throw new InvalidDataException("the mesh component has no SkeletalMesh property");
        int oldMesh = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(skel.ValueAt));
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(skel.ValueAt), copy.RootRef);
        var addNames = new List<string>();
        int noneAt = tags.Count > 0 ? tags[^1].End : 16;   // where the component's None tag starts (new tags go before it)
        if (tags.FirstOrDefault(t => t.Name.Equals("PhysicsAsset", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } phys)
        {
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(phys.ValueAt), physRef);
            log.Add(physRef > 0 ? $"component: PhysicsAsset → #{physRef}" : "physics asset: none (the target's is built for its own bones)");
        }
        else if (physRef > 0)
        {
            // The target's component doesn't name one (Colossus): a PhysicsAsset ObjectProperty goes in before its None.
            int NameIdx(string n)
            {
                int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
                int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
                return pkg.Names.Length + j;
            }
            int at = noneAt;
            var tag = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("PhysicsAsset"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ObjectProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(24), physRef);
            d = [.. d.AsSpan(0, at), .. tag, .. d.AsSpan(at)];
            noneAt += tag.Length;
            log.Add($"component: PhysicsAsset added → #{physRef}");
        }
        log.Add($"component: SkeletalMesh {(oldMesh > 0 ? pkg.PathOf(pkg.Exports[oldMesh - 1]) : oldMesh.ToString())} → #{copy.RootRef}");
        var replaceData = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => d };
        if (meshData != null) { byte[] md = meshData; replaceData[copy.RootRef - 1] = _ => md; }
        foreach (var (si, sd) in socketData) replaceData[si] = _ => sd;
        // The target hero's situations the moved voice has no line for: set to none, so the hero's own lines don't play there
        // (Kurt, 2026-10-02: Rescue on Iron Man still used Iron Man's "power on cooldown"). The hero's set: its base package's,
        // else its default voice package (Spider-Man's lives in UC__MarvelPlayerAudio_Spiderman_Default_SF).
        if (voiceComp >= 0 && voiceData != null && cooked != null && targetClass.StartsWith("MarvelPlayer_", StringComparison.OrdinalIgnoreCase))
        {
            string hero = targetClass.Split('_')[1];
            string? heroVoice = new[] { $"UC__MarvelPlayer_{hero}_SF.upk", $"UC__MarvelPlayerAudio_{hero}_Default_SF.upk" }
                .Select(f => StockFiles.For(cooked, f)).FirstOrDefault(f => File.Exists(f) && VoiceSet.Find(Package.Open(f)) >= 0);
            if (heroVoice != null)
            {
                int NameIdxV(string n)
                {
                    int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) return i;
                    int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                    if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
                    return pkg.Names.Length + j;
                }
                voiceData = VoiceSet.AddMissing(pkg, voiceData, heroVoice, Path.GetFileName(targetStock), NameIdxV, autoOff ?? [], log);
            }
        }
        if (voiceComp >= 0 && voiceData != null) replaceData[voiceComp] = _ => voiceData;   // the target costume's voice set = the source's
        // The animation tree (Kurt, 2026-09-30: Doctor Strange's cape stayed pinned on Colossus and Daredevil, moved on his own
        // Fear Itself and Punisher S2's coat moved on Daredevil). Heroes use shared trees in Startup.upk: pc_at_v2 (Colossus,
        // Daredevil, Punisher, Hulk, Spider-Man …) has per-bone blends starting at g_cape1 / g_l_cape1-3 / g_r_cape1-3 (one is
        // "overridephysicsbones"), pc_at_nocape (Doctor Strange, Moon Knight, Emma Frost, Scarlet Witch, Thor …) leaves them
        // out. Doctor Strange's cape hangs from exactly those bones; Punisher's coat from g_coat* / g_*coatback*. So the moved
        // model gets the source's tree when that's a shared one (an import); a hero's own tree (Silver Surfer, Deadpool) stays.
        var addImports = new List<NewImport>();
        if (SourceTree(src, sourceClass, baseHero) is { } tree)
        {
            int NameIdx4(string n)
            {
                int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
                if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
                return pkg.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            }
            int treeRef = -1 - Array.FindIndex(pkg.Imports, im => im.ObjectName.Equals(tree.Name, StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals("AnimTree", StringComparison.OrdinalIgnoreCase)
                && im.OuterIndex < 0 && pkg.Imports[-im.OuterIndex - 1].ObjectName.Equals(tree.Outer, StringComparison.OrdinalIgnoreCase));
            if (treeRef == 0)   // not imported yet (FindIndex -1 → 0)
            {
                int outerIdx = Array.FindIndex(pkg.Imports, im => im.ObjectName.Equals(tree.Outer, StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase) && im.OuterIndex == 0);
                int outerRef;
                if (outerIdx >= 0) outerRef = -1 - outerIdx;
                else
                {
                    foreach (string n in new[] { "Core", "Package", tree.Outer }) NameIdx4(n);
                    addImports.Add(new NewImport("Core", "Package", 0, tree.Outer));
                    outerRef = -(pkg.Imports.Length + addImports.Count);
                }
                foreach (string n in new[] { "Engine", "AnimTree", tree.Name }) NameIdx4(n);
                addImports.Add(new NewImport("Engine", "AnimTree", outerRef, tree.Name));
                treeRef = -(pkg.Imports.Length + addImports.Count);
            }
            if (tags.FirstOrDefault(t => t.Name.Equals("AnimTreeTemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } at0)
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at0.ValueAt), treeRef);
            else
            {
                var tag = new byte[28];
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx4("AnimTreeTemplate"));
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx4("ObjectProperty"));
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(24), treeRef);
                d = [.. d.AsSpan(0, noneAt), .. tag, .. d.AsSpan(noneAt)];
                noneAt += tag.Length;
            }
            log.Add($"component: AnimTreeTemplate → {tree.Outer}.{tree.Name} (the source's)");
        }

        // PhysicsWeight (Kurt, 2026-09-30: the cape stayed static with the physics asset alone): Doctor Strange's base component
        // sets 1.0, Colossus's leaves the default 0, and that was the only difference between them besides their own meshes
        // and assets. The source's value (its costume component, else its hero's base component) goes to the target's
        // component when that doesn't set one; only with the source's own physics asset.
        // A target that sets its own (Doctor Strange Fear Itself: 0.5) gets the source's too.
        if (physRef > 0 && SourceFloat(src, sourceClass, baseHero, "PhysicsWeight") is float w0
            && tags.FirstOrDefault(t => t.Name.Equals("PhysicsWeight", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } pw)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(pw.ValueAt), w0);   // existing tags sit before None, so an insert at None doesn't move them
            log.Add($"component: PhysicsWeight {w0} (as the source's)");
        }
        else if (physRef > 0 && SourceFloat(src, sourceClass, baseHero, "PhysicsWeight") is float weight
            && !tags.Any(t => t.Name.Equals("PhysicsWeight", StringComparison.OrdinalIgnoreCase)))
        {
            int NameIdx2(string n)
            {
                int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
                int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
                return pkg.Names.Length + j;
            }
            int at = noneAt;
            var tag = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx2("PhysicsWeight"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx2("FloatProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
            BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(24), weight);
            d = [.. d.AsSpan(0, at), .. tag, .. d.AsSpan(at)];
            replaceData[comp] = _ => d;
            log.Add($"component: PhysicsWeight {weight} (as the source's)");
        }
        // TargetPhysicsWeight on the costume class default (Kurt, 2026-09-30: with the physics asset and PhysicsWeight the sash
        // swung on Colossus but the cape didn't). Stock cloth costumes set it to 0 (Storm Modern VU, Thing Incognito, Moon Knight
        // Modern, Punisher TV, Old Man Logan, Iron Fist Immortal, Doctor Strange Classic), costumes without cloth leave the
        // non-zero default (Storm Classic, Thing Classic, Colossus Modern). The source's value goes to the target's default.
        if (physRef > 0 && SourceFloat(src, sourceClass, baseHero, "TargetPhysicsWeight", "") is float target)
        {
            string defPath = $"marvelgamecontent.default__{targetClass}";
            int def = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(defPath, StringComparison.OrdinalIgnoreCase));
            byte[]? dd = def < 0 ? null : replaceData.TryGetValue(def, out var f0) ? f0(0) : pkg.ReadExportBytes(pkg.Exports[def]).ToArray();
            var dtags = dd == null ? null : TagWalker.Walk(pkg, dd, 4);
            if (dd == null || dtags == null) log.Add("target physics weight: the costume's default doesn't read; left as it is");
            else
            {
                if (dtags.FirstOrDefault(t => t.Name.Equals("TargetPhysicsWeight", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } tt)
                    BinaryPrimitives.WriteSingleLittleEndian(dd.AsSpan(tt.ValueAt), target);
                else
                {
                    int NameIdx3(string n)
                    {
                        int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                        if (i >= 0) return i;
                        int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                        if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
                        return pkg.Names.Length + j;
                    }
                    int at = dtags.Count > 0 ? dtags[^1].End : 4;
                    var tag = new byte[28];
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx3("TargetPhysicsWeight"));
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx3("FloatProperty"));
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
                    BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(24), target);
                    dd = [.. dd.AsSpan(0, at), .. tag, .. dd.AsSpan(at)];
                }
                byte[] final = dd;
                replaceData[def] = _ => final;
                log.Add($"costume default: TargetPhysicsWeight {target} (as the source's)");
            }
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, replaceData, socketAdds, out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(pkg, output, replaceData.Keys.ToList(), socketAdds, written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        // The GUID stays the target's (the game checks it): the target stock package's summary is kept as it was.
        var t0 = Package.Open(targetStock);
        if (!File.ReadAllBytes(targetStock).AsSpan(t0.GenerationsAt - 16, 16).SequenceEqual(output.AsSpan(Package.FromBytes(output).GenerationsAt - 16, 16)))
            throw new InvalidDataException("the GUID isn't the target's");
        return output;
    }

    /// <summary>The source's animation tree when it's a shared one (an import inside a package import, e.g. biped_lib.pc_at_nocape
    /// from Startup.upk): the source costume component's AnimTreeTemplate, else its hero base component's; null otherwise.</summary>
    static (string Outer, string Name)? SourceTree(Package src, string? sourceClass, string? baseHero)
    {
        if (sourceClass == null) return null;
        string lower = sourceClass.ToLowerInvariant(), heroClass = "marvelplayer_" + lower.Split('_')[1];
        foreach (var (p, comp) in new[] { (src, $"marvelgamecontent.default__{lower}.initialskeletalmesh"),
                                          (baseHero != null && File.Exists(baseHero) ? Package.Open(baseHero) : null, $"marvelgamecontent.default__{heroClass}.initialskeletalmesh") })
        {
            if (p == null) continue;
            int c = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(comp, StringComparison.OrdinalIgnoreCase));
            if (c < 0) continue;
            byte[] cd = p.ReadExportBytes(p.Exports[c]).ToArray();
            if (TagWalker.Walk(p, cd, 16)?.FirstOrDefault(t => t.Name.Equals("AnimTreeTemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } t) continue;
            int r = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(t.ValueAt));
            if (r >= 0) return null;                                  // none, or the hero's own tree (an export)
            var im = p.Imports[-r - 1];
            if (im.OuterIndex >= 0 || !im.ClassName.Equals("AnimTree", StringComparison.OrdinalIgnoreCase)) return null;
            var outer = p.Imports[-im.OuterIndex - 1];
            if (outer.OuterIndex != 0 || !outer.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase)) return null;
            return (outer.ObjectName, im.ObjectName);
        }
        return null;
    }

    /// <summary>A float property of the source's mesh component: its costume component, else its hero's base component.</summary>
    /// <summary>
    /// The stock mesh's sockets the moved mesh lacks (by SocketName), on bones the moved mesh has: the moved mesh's new data
    /// (its Sockets array lengthened) and the new socket objects (copies of the stock ones, owned by the moved mesh). Null
    /// data when nothing is missing.
    /// </summary>
    static (byte[]? MeshData, List<NewExport> Adds, Dictionary<int, byte[]> SocketData) AddMissingSockets(Package pkg, int stockMesh, int moved, List<string> log)
    {
        var turned = new Dictionary<int, byte[]>();
        var none = ((byte[]?)null, new List<NewExport>(), turned);
        List<(int Export, string Name, string Bone)> Sockets(int mesh, out TagWalker.Tag? tag, out byte[] data)
        {
            data = pkg.ReadExportBytes(pkg.Exports[mesh]).ToArray();
            var list = new List<(int, string, string)>();
            tag = TagWalker.Walk(pkg, data, 4)?.FirstOrDefault(t => t.Name.Equals("Sockets", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase));
            if (tag == null) return list;
            int n = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(tag.ValueAt));
            for (int k = 0; k < n; k++)
            {
                int r = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(tag.ValueAt + 4 + 4 * k));
                if (r <= 0) continue;
                byte[] sd = pkg.ReadExportBytes(pkg.Exports[r - 1]).ToArray();
                var props = TagWalker.Walk(pkg, sd, 4);
                string? Nm(string p) => props?.FirstOrDefault(t => t.Name.Equals(p, StringComparison.OrdinalIgnoreCase)) is { } t ? TagWalker.NameAt(pkg, sd, t.ValueAt) : null;
                if (Nm("SocketName") is string sn && Nm("BoneName") is string bn) list.Add((r - 1, sn, bn));
            }
            return list;
        }
        var stock = Sockets(stockMesh, out _, out _);
        var have = Sockets(moved, out var arr, out byte[] meshBytes);
        if (arr == null || stock.Count == 0) return none;
        // The moved mesh's bones (its skeleton, read by AnimExportCli's mesh reader).
        var ap = AnimExportCli.Packages.Package.Read(pkg.RawFile);
        var mesh = AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, moved);
        if (mesh == null) return none;
        var bones = mesh.Bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Sockets both meshes have, on the same bone: the target's turn (Kurt, 2026-10-02: Rescue on Iron Man fired the
        // Unibeam 90° to the side). A power's effect is set up for its own hero's socket: Iron Man's body sockets face along
        // the bone (X = model -Y at rest), Rescue's are yawed 90°. The animations drive the bones the same way on both
        // skeletons, so the target's RelativeRotation goes into the moved socket; its place (RelativeLocation) stays the
        // moved mesh's own, on its body.
        (int[] Rot, bool Has, TagWalker.Tag? Tag, byte[] Data) RotOf(int export)
        {
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[export]).ToArray();
            var t = TagWalker.Walk(pkg, sd, 4)?.FirstOrDefault(x => x.Name.Equals("RelativeRotation", StringComparison.OrdinalIgnoreCase) && x.Size == 12);
            int[] r = t == null ? [0, 0, 0] : [BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(t.ValueAt)), BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(t.ValueAt + 4)), BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(t.ValueAt + 8))];
            return (r, t != null, t, sd);
        }
        var turnedNames = new List<string>(); var cantTurn = new List<string>();
        foreach (var h in have)
        {
            var st = stock.FirstOrDefault(x => x.Name.Equals(h.Name, StringComparison.OrdinalIgnoreCase));
            if (st.Name == null || !st.Bone.Equals(h.Bone, StringComparison.OrdinalIgnoreCase)) continue;
            var want = RotOf(st.Export);
            var mine = RotOf(h.Export);
            if (want.Rot.SequenceEqual(mine.Rot)) continue;
            if (mine.Tag == null) { cantTurn.Add(h.Name); continue; }   // no RelativeRotation to change (would need a new tag)
            byte[] nd0 = mine.Data;
            for (int c = 0; c < 3; c++) BinaryPrimitives.WriteInt32LittleEndian(nd0.AsSpan(mine.Tag.ValueAt + 4 * c), want.Rot[c]);
            turned[h.Export] = nd0;
            turnedNames.Add(h.Name);
        }
        if (turnedNames.Count > 0) log.Add($"sockets: {turnedNames.Count} turned to the target's ({string.Join(", ", turnedNames)})");
        if (cantTurn.Count > 0) log.Add($"sockets: {string.Join(", ", cantTurn)} face another way than the target's but have no rotation to change");

        var missing = stock.Where(x => !have.Any(h => h.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase)) && bones.Contains(x.Bone)).ToList();
        var noBone = stock.Where(x => !have.Any(h => h.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase)) && !bones.Contains(x.Bone)).Select(x => $"{x.Name} ({x.Bone})").ToList();
        if (missing.Count == 0) { if (noBone.Count > 0) log.Add($"sockets: the target's {string.Join(", ", noBone)} not added (no such bone in the moved skeleton)"); return none; }
        var adds = new List<NewExport>();
        int count = BinaryPrimitives.ReadInt32LittleEndian(meshBytes.AsSpan(arr.ValueAt));
        var refs = new List<byte>();
        for (int k = 0; k < missing.Count; k++)
        {
            int src = missing[k].Export;
            byte[] entry = pkg.Body.AsSpan(pkg.ExportEntryStart[src], pkg.ExportEntryEnd[src] - pkg.ExportEntryStart[src]).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), moved + 1);         // owner: the moved mesh
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 20000 + k);        // a name number no socket of it uses
            byte[] data = pkg.ReadExportBytes(pkg.Exports[src]).ToArray();              // names and numbers only: copied as is
            adds.Add(new NewExport(src, 0, _ => data) { Entry = entry });
            refs.AddRange(BitConverter.GetBytes(pkg.Exports.Length + k + 1));
        }
        // The Sockets array: the new references after the old ones; count and the tag's size grow.
        int end = arr.ValueAt + 4 + 4 * count;
        byte[] nd = [.. meshBytes.AsSpan(0, end), .. refs, .. meshBytes.AsSpan(end)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(arr.ValueAt), count + missing.Count);
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(arr.Start + 16), arr.Size + 4 * missing.Count);
        log.Add($"sockets: the target's {string.Join(", ", missing.Select(x => x.Name))} added to the moved mesh" +
                (noBone.Count > 0 ? $"; not {string.Join(", ", noBone)} (no such bone)" : ""));
        return (nd, adds, turned);
    }

    static float? SourceFloat(Package src, string? sourceClass, string? baseHero, string prop, string sub = ".initialskeletalmesh")
    {
        if (sourceClass == null) return null;
        string lower = sourceClass.ToLowerInvariant(), heroClass = "marvelplayer_" + lower.Split('_')[1];
        foreach (var (p, comp) in new[] { (src, $"marvelgamecontent.default__{lower}{sub}"),
                                          (baseHero != null && File.Exists(baseHero) ? Package.Open(baseHero) : null, $"marvelgamecontent.default__{heroClass}{sub}") })
        {
            if (p == null) continue;
            int c = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(comp, StringComparison.OrdinalIgnoreCase));
            if (c < 0) continue;
            byte[] cd = p.ReadExportBytes(p.Exports[c]).ToArray();
            if (TagWalker.Walk(p, cd, sub.Length == 0 ? 4 : 16)?.FirstOrDefault(t => t.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } t)
                return BinaryPrimitives.ReadSingleLittleEndian(cd.AsSpan(t.ValueAt));
        }
        return null;
    }

    /// <summary>The source mesh's physics asset: the source costume component's PhysicsAsset (an export), else a PhysicsAsset
    /// inside the mesh's group ("drstrange_classicvu.drstrange_classicvu_physics"); -1 if none.</summary>
    /// <summary>
    /// The character model of a costume in a package: the SkeletalMesh its class default's initialskeletalmesh component
    /// names, else the package's first skeletal mesh. (A hero's main package holds props too: Star-Lord's has a knife,
    /// techknife, before his own model, and the first mesh was taken.)
    /// </summary>
    public static string SourceMeshName(Mod mod, string file, string sourceClass)
    {
        var listed = ModMeshes.List(mod).Where(r => r.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).ToList();
        try
        {
            var p = Package.Open(Path.Combine(mod.Folder, file));
            string comp = $"marvelgamecontent.default__{sourceClass.ToLowerInvariant()}.initialskeletalmesh";
            int ci = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(comp, StringComparison.OrdinalIgnoreCase));
            if (ci >= 0)
            {
                var d = p.ReadExportBytes(p.Exports[ci]);
                if (TagWalker.Walk(p, d, 16) is { } tags && tags.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) is { Size: 4 } sm
                    && BitConverter.ToInt32(d, sm.ValueAt) is int r && r > 0 && r <= p.Exports.Length && listed.Contains(p.Exports[r - 1].ObjectName, StringComparer.OrdinalIgnoreCase))
                    return p.Exports[r - 1].ObjectName;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException or IndexOutOfRangeException) { }
        return listed.FirstOrDefault() ?? "";
    }

    static int SourcePhysics(Package src, string meshName, string? sourceClass)
    {
        if (sourceClass != null)
        {
            string compPath = $"marvelgamecontent.default__{sourceClass}.initialskeletalmesh";
            int c = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
            if (c >= 0)
            {
                byte[] cd = src.ReadExportBytes(src.Exports[c]).ToArray();
                if (TagWalker.Walk(src, cd, 16)?.FirstOrDefault(t => t.Name.Equals("PhysicsAsset", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } t
                    && BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(t.ValueAt)) is int r && r > 0) return r - 1;
            }
        }
        return Array.FindIndex(src.Exports, e => src.ClassOf(e).Equals("PhysicsAsset", StringComparison.OrdinalIgnoreCase)
            && src.PathOf(e).StartsWith(meshName + ".", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"She-Hulk (on Storm Modern)": a move to another hero names the hero too.</summary>
    public static string NewName(Mod mod, Costume target) => target.IsTeamUp ? $"{mod.Name} (on Team-Up {target.Title})" : $"{mod.Name} (on {target.Short.Split('/')[0]} {target.Title})";

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
        string meshName = SourceMeshName(mod, srcFile, source.Class);
        string heroBase = "UC__MarvelPlayer_" + source.Class.Split('_')[1] + "_SF.upk";
        string? baseHero = File.Exists(Path.Combine(mod.Folder, heroBase)) ? Path.Combine(mod.Folder, heroBase) : File.Exists(Path.Combine(game.Cooked, heroBase)) ? Path.Combine(game.Cooked, heroBase) : null;
        var plan = CostumeMove.Make(mod, srcFile, source, target, all, game.Cooked, catalog);   // icons, text, sound packs
        string work = Path.Combine(lib.DataFolder, "costume-move-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            // No sounds across heroes (Kurt, option 1): a pack's events are played by its own hero's powers and animations
            // (MHSFXEditor renames the costume package's own AkEvents), which another hero doesn't have.
            var autoOff = new List<VoiceOffEntry>();
            byte[] built = Build(modPkg, meshName, stock, target.Class, baseHero, log, sounds: false, sourceClass: source.Class, cooked: game.Cooked, autoOff: autoOff);
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
                var resized = new List<string>();
                string srcDds = CostumeMove.FitIcon(i, mod.Folder, work, catalog, resized);
                foreach (string r in resized) log.Add("resized " + r);
                if (byPackage.TryGetValue(i.Package, out int k)) d.Textures[k].Add((i.To, srcDds)); else d.Extra.Add((i.Package, i.To, srcDds));
            }
            d.Strings = [.. plan.Strings.Select(sm => mod.Strings.First(x => x.Id == sm.From && x.Language.Equals(sm.Language, StringComparison.OrdinalIgnoreCase)) with { Id = sm.To, File = sm.File, Variants = null })];
            // The voice set moves with its events, so a mod's sound pack (the audio of its renamed events) goes along.
            d.SoundPacks = log.Any(l => l.StartsWith("voice: ")) ? [.. mod.Manifest.AudioPacks.Select(f => Path.Combine(mod.Folder, f))] : [];
            d.PreviewImage = null; d.PreviewViews = null; d.CardPicture = null;
            d.PostNexus = d.PostDiscord = null; d.PostImages = [];
            d.NexusModId = null; d.Changes = ""; d.Changelog = [];
            if (replace != null) { d.Tags = [.. replace.ModTags]; d.Notes = replace.Manifest.Notes ?? ""; }
            else d.Notes = (d.Notes.Length > 0 ? d.Notes.TrimEnd() + Environment.NewLine : "") + $"Moved from {mod.Name} ({source.Short.Replace(".prototype", "")} → {target.Short.Replace(".prototype", "")}).";
            CostumeMove.KeepPowerColors(d, mod, replace, sameHero: false);
            // Voice lines: the original's turned-off lines don't apply to the new package; the hero's lines the moved voice
            // has no entry for are listed as off (Missing), for the Voice tab.
            d.VoiceOff = [.. autoOff.Select(o => { o.Package = target.Package; return o; })];
            d.AnimSwaps = [];   // the model goes into the target's stock package: the target hero's animations
            d.MovedFrom = source.Short.Replace(".prototype", "");
            if (d.PowerColors.Count > 0) log.Add($"power colors kept: {d.PowerColors.Count}");
            log.Add($"icons: {plan.Icons.Count}; text: {plan.Strings.Count}; sound packs: {d.SoundPacks.Count}");
            if (replace != null) d = CostumeMove.UpdateDraft(d, replace, [target.Package], d.VoiceOff);
            return ModWriter.Save(lib, d, replace, out error);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { error = Friendly(ex.Message); return null; }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } }
    }

    /// <summary>A refused copy in plain words. APEX cloth (capes, coats) copies since 0.37.22 (ported from the MHO Hero Creator):
    /// all 58 of the library's costume mods build onto Daredevil Modern since 0.37.25 (physics assets copy: Scarlet Witch House of M's
    /// material has an import whose outer is a physics constraint).</summary>
    static string Friendly(string why) =>
        why.Contains("clothing", StringComparison.OrdinalIgnoreCase) ? "Its model's APEX cloth (a cape or coat simulated by PhysX) is set up in a way that can't be moved to another hero yet."
        : why.Contains("physicsassetinstance", StringComparison.OrdinalIgnoreCase) ? "Its files link part of its model to its own physics setup, which can't be moved to another hero yet."
        : why.Contains("timevarying", StringComparison.OrdinalIgnoreCase) ? "It uses an animated material that can't be moved to another hero yet."
        : why.Contains("rb_bodysetup", StringComparison.OrdinalIgnoreCase) ? "Its model has its own physics setup, which can't be moved to another hero yet."
        : why;
}
