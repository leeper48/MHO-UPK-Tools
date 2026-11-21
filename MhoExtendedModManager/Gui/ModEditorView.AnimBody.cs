namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Animations tab's Bone Lengths and Shape Keys (Kurt, 2026-11-17; Carnage's Vicious Lunge on Medusa): for the selected
/// animation, Keep Bone Lengths (the bones whose animated positions this costume's copy doesn't take: AnimSwap leaves them out
/// of the copied set's UseTranslationBoneNames) and Shape Keys (the morph-target curves left out of the copy's CurveData). An
/// animation not changed yet becomes a copy of itself (a change from where it plays now), so only this costume changes.
/// </summary>
sealed partial class ModEditorView
{
    Button? animBody, animLevel;

    /// <summary>The animations that bank a ridden vehicle in turns (additive: the turn's lean is all that's in them).</summary>
    static readonly string[] LeanAnims = ["add_leanleft", "add_leanright", "add_pivotleft", "add_pivotright", "lean_pivotleft", "lean_pivotright"];
    const string MountBone = "g_mountattach";

    /// <summary>
    /// Keep Vehicle Level (Kurt, 2026-11-20; confirmed in game with a car on Ghost Rider's bike): every lean / pivot animation of
    /// the costume that turns <see cref="MountBone"/> gets it in Keep Bones from Turning at 0 %, as the Bone Lengths window would,
    /// one copy after another. When all of them hold it already, it's taken out again (they bank as the game does).
    /// </summary>
    async Task KeepVehicleLevel()
    {
        if (animData == null || AnimFile is not string file || DraftPath(file) is not string src || game == null) return;
        var slots = animData.Anims.Where(a => a.From.Alias == null && a.From.File != null && LeanAnims.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        animStatus.Text = "Reading the Lean Animations…";
        var turning = new List<string>();
        foreach (var s in slots)
        {
            var read = await Task.Run(() => ReadBody(s));
            bool held = SwapOf(file, s)?.StillBones?.Contains(MountBone, StringComparer.OrdinalIgnoreCase) == true;
            if (held || read.Turning.Any(t => t.Name.Equals(MountBone, StringComparison.OrdinalIgnoreCase))) turning.Add(s.Name);
        }
        animStatus.Text = "";
        if (IsDisposed) return;
        if (turning.Count == 0)
        {
            Dialog.Show(this, $"This costume's animations don't bank a vehicle: none of its lean or pivot animations ({string.Join(", ", LeanAnims)}) turns {MountBone}, the bone a ridden vehicle hangs on.", "Nothing to Level", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        bool allHeld = turning.All(n => animData.Anims.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } a
            && SwapOf(file, a)?.StillBones?.Contains(MountBone, StringComparer.OrdinalIgnoreCase) == true);
        int done = 0;
        foreach (string name in turning)
        {
            // (each copy reloads the list: the slot is looked up again)
            var before = animData;
            if (animData?.Anims.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } slot) continue;
            var entry = SwapOf(file, slot);
            var still = new List<string>(entry?.StillBones ?? []);
            still.RemoveAll(b => b.Equals(MountBone, StringComparison.OrdinalIgnoreCase));
            if (!allHeld) still.Add(MountBone);
            await ApplyBody(slot, file, src, entry, entry?.KeepLengths ?? [], entry?.CurvesOff ?? [], still, 0);
            done++;
            for (int i = 0; i < 100 && !IsDisposed && ReferenceEquals(animData, before); i++) await Task.Delay(100);
            if (IsDisposed) return;
        }
        animStatus.Text = allHeld ? $"{done} Lean Animation(s) Bank the Vehicle Again" : $"Vehicle Kept Level in {done} Lean Animation(s)";
    }

    async void EditAnimBody()
    {
        if (SelectedAnim is not { } slot || animData == null || AnimFile is not string file || DraftPath(file) is not string src || game == null) return;
        if (slot.From.Alias != null) { Dialog.Show(this, "Bone lengths and shape keys can't be set on a form's animation yet.", "Not Available", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (slot.From.File == null) return;
        var entry = SwapOf(file, slot);
        animStatus.Text = "Reading the Animation…";
        // what it moves and the shape keys it drives (the copy playing now: a kept bone still has its keys, an unticked curve is
        // gone, so the entry's lists are added back)
        var read = await Task.Run(() => ReadBody(slot));
        animStatus.Text = "";
        if (IsDisposed) return;
        var keep = new HashSet<string>(entry?.KeepLengths ?? [], StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<string>(entry?.CurvesOff ?? [], StringComparer.OrdinalIgnoreCase);
        var rows = read.Moving.Select(m => new AnimBodyForm.Bone(m.Name, m.Reach, keep.Contains(m.Name), slot.From.TranslationBones == null || slot.From.TranslationBones.Contains(m.Name))).ToList();
        foreach (string k in keep) if (!rows.Any(r => r.Name.Equals(k, StringComparison.OrdinalIgnoreCase))) rows.Add(new AnimBodyForm.Bone(k, 0, true, true));
        var curveRows = read.Curves.Select(c => (c, off.Contains(c))).Concat(off.Where(o => o != "*" && !read.Curves.Contains(o, StringComparer.OrdinalIgnoreCase)).Select(o => (o, true))).ToList();
        var still = new HashSet<string>(entry?.StillBones ?? [], StringComparer.OrdinalIgnoreCase);
        float stillAt = entry?.StillStrength ?? 0;
        var turnRows = read.Turning.Select(m => (m.Name, m.Degrees, still.Contains(m.Name))).ToList();
        foreach (string k in still) if (!turnRows.Any(r => r.Name.Equals(k, StringComparison.OrdinalIgnoreCase))) turnRows.Add((k, 0, true));
        List<string> newKeep, newOff, newStill; float newAt;
        using (var f = new AnimBodyForm(slot.Name, rows, curveRows, turnRows, stillAt))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            newKeep = f.KeepLengths; newOff = f.CurvesOff; newStill = f.StillBones; newAt = f.StillStrength;
        }
        if (newKeep.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(keep) && newOff.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(off)
            && newStill.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(still) && (newStill.Count == 0 || Math.Abs(newAt - stillAt) < 0.001f)) return;
        await ApplyBody(slot, file, src, entry, newKeep, newOff, newStill, newAt);
    }

    /// <summary>The bones the sequence slides (position keys reaching more than 0.05 from their first) and its shape-key curves.</summary>
    static (List<(string Name, float Reach)> Moving, List<string> Curves, List<(string Name, float Degrees)> Turning) ReadBody(CostumeAnims.Anim slot)
    {
        var moving = new List<(string, float)>();
        var curves = new List<string>();
        var turning = new List<(string, float)>();
        try
        {
            var ap = AnimExportCli.Packages.Package.Open(slot.From.File!);
            if (AnimExportCli.Animation.AnimObjectReader.TryRead(ap, slot.Export, slot.From.TrackBoneNames) is { } anim)
                foreach (var (bone, track) in anim.Tracks)
                {
                    if (track.PositionKeys.Count < 2 || bone.Equals("root", StringComparison.OrdinalIgnoreCase)) continue;
                    var p0 = track.PositionKeys[0].Position;
                    float reach = track.PositionKeys.Max(k => System.Numerics.Vector3.Distance(k.Position, p0));
                    if (reach > 0.05f) moving.Add((bone, reach));
                }
            // the bones it turns: from "no turn" for an additive animation (the lean: only the lean is in it), else from its first key
            var mp = MhoPackageModifier.Package.Open(slot.From.File!);
            byte[] sq = mp.ReadExportBytes(mp.Exports[slot.Export]).ToArray();
            bool additive = MhoPackageModifier.TagWalker.Walk(mp, sq, 4)?.FirstOrDefault(x => x.Name.Equals("bIsAdditive", StringComparison.OrdinalIgnoreCase)) is { } at && at.ValueAt > 0 && sq[at.ValueAt - 1] != 0;   // (a bool's byte is just before ValueAt)
            if (AnimExportCli.Animation.AnimObjectReader.TryRead(ap, slot.Export, slot.From.TrackBoneNames) is { } an2)
                foreach (var (bone, track) in an2.Tracks)
                {
                    if (track.RotationKeys.Count == 0 || bone.Equals("root", StringComparison.OrdinalIgnoreCase)) continue;
                    var r0 = additive ? System.Numerics.Quaternion.Identity : track.RotationKeys[0].Rotation;
                    float deg = track.RotationKeys.Max(k => 2 * MathF.Acos(Math.Clamp(MathF.Abs(System.Numerics.Quaternion.Dot(System.Numerics.Quaternion.Normalize(k.Rotation), r0)), 0, 1)) * 180 / MathF.PI);
                    if (deg > 1) turning.Add((bone, deg));
                }
            var pkg = MhoPackageModifier.Package.Open(slot.From.File!);
            curves = AnimSwap.Curves(pkg, pkg.ReadExportBytes(pkg.Exports[slot.Export]).ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or AnimExportCli.Packages.InvalidPackageException or ArgumentOutOfRangeException) { }
        return (moving, curves, turning);
    }

    /// <summary>
    /// The slot's copy made again with these choices: from its change's source (the manifest's), or, for an animation not
    /// changed yet, from where it plays now.
    /// </summary>
    async Task ApplyBody(CostumeAnims.Anim slot, string file, string src, AnimSwapEntry? entry, List<string> keepLengths, List<string> curvesOff, List<string>? stillBones = null, float stillStrength = 0)
    {
        stillBones ??= entry?.StillBones ?? [];
        if (animData == null || game == null) return;
        string cls = animData.Class, cooked = game.Cooked;
        var inherited = animData.Inherited ? animData.Sets.Select(x => x.Path).ToList() : null;
        // the source: the change's (read again from its package), else the animation where it plays now
        string donorFile; int donorSet, donorSeq;
        if (entry != null)
        {
            if (!entry.Donor.EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
            { Dialog.Show(this, $"\"{slot.Name}\" comes from the mod \"{entry.Donor}\": change it again (Change Animation), then set its bone lengths and shape keys.", "Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bool self = entry.Donor.Equals(file, StringComparison.OrdinalIgnoreCase);
            string donorPath = self ? src : DraftPath(entry.Donor) is string dp && File.Exists(dp) ? dp : StockFiles.For(cooked, entry.Donor);
            var found = await Task.Run(() => SourceOf(donorPath, entry.Donor, entry.Animation, cooked));
            if (found == null) { Dialog.Show(this, $"{entry.Donor} has no \"{entry.Animation}\" any more: change \"{slot.Name}\" again.", "Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            (donorFile, donorSet, donorSeq) = found.Value;
        }
        else (donorFile, donorSet, donorSeq) = (slot.From.File!, slot.From.Export, slot.Export);
        var swap = Lowered(file, new AnimSwap.Swap(slot.Name, donorFile, donorSet, donorSeq) { Heights = entry?.HeightKeys is { Count: > 0 } hk ? hk : null })
            with { KeepLengths = keepLengths.Count > 0 ? keepLengths : null, CurvesOff = curvesOff.Count > 0 ? curvesOff : null, StillBones = stillBones.Count > 0 ? stillBones : null, StillStrength = stillStrength };
        var log = new List<string>();
        animStatus.Text = "Making This Costume's Copy…";
        byte[] bytes;
        bool fromSource = IsModelSource(file, donorFile);
        try
        {
            var turns = await Task.Run(() => RestTurnsFor(donorFile, src, fromSource));
            var dskel = fromSource ? PowerBorrow.HeroSkeleton(donorFile) : null;
            bytes = await Task.Run(() => AnimSwap.Build(src, cls, [swap], log, inherited, retarget: turns, skeleton: dskel != null ? PowerBorrow.HeroSkeleton(src) : null, donorSkeleton: dskel));
            OnBorrowBase(file, b => AnimSwap.Build(b, cls, [swap], log, inherited, retarget: turns, skeleton: dskel != null ? PowerBorrow.HeroSkeleton(b) : null, donorSkeleton: dskel));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException)
        { animStatus.Text = ""; Dialog.Show(this, $"\"{slot.Name}\" couldn't be changed: {ex.Message}", "Animation Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (IsDisposed) return;
        UseWorkCopy(file, bytes);
        if (entry == null)
        {
            entry = new AnimSwapEntry { Package = file, Slot = slot.Name, Donor = Path.GetFileName(donorFile), Animation = slot.Name, Title = slot.From.Label };
            draft.AnimSwaps.Add(entry);
        }
        entry.KeepLengths = keepLengths.Count > 0 ? keepLengths : null;
        entry.CurvesOff = curvesOff.Count > 0 ? curvesOff : null;
        entry.StillBones = stillBones.Count > 0 ? stillBones : null;
        entry.StillStrength = stillBones.Count > 0 ? stillStrength : null;
        await PropsAfterAnimChange(file);
        animStatus.Text = $"\"{slot.Name}\": " + (keepLengths.Count + curvesOff.Count + stillBones.Count == 0 ? "Its Own Bone Lengths and Shape Keys"
            : string.Join(" · ", new[] { keepLengths.Count > 0 ? $"{keepLengths.Count} Bone Length(s) Kept" : null, curvesOff.Count > 0 ? $"{curvesOff.Count} Shape Key(s) Off" : null, stillBones.Count > 0 ? $"{stillBones.Count} Bone(s) Kept from Turning ({stillStrength * 100:0} %)" : null }.OfType<string>()));
        ReloadAt(slot.Name);
    }

    /// <summary>A change's source animation in its package: the set and sequence it's read from (a copy this app made is
    /// skipped for the set it replaces).</summary>
    (string File, int Set, int Seq)? SourceOf(string donorPath, string donorFile, string animation, string cooked)
    {
        var files = CostumeAnims.FilesFor(editing, cooked);
        var dca = CostumeAnims.Read(donorPath, donorFile, files);
        var a = dca?.Anims.FirstOrDefault(x => x.Name.Equals(animation, StringComparison.OrdinalIgnoreCase));
        if (a?.From.File == null) return null;
        if (a.From.Kind == CostumeAnims.Source.Costume && a.From.Path.Split('.')[^1].Contains("_on_", StringComparison.OrdinalIgnoreCase))
        {
            // (the package's own copy: the set under it that has the animation)
            foreach (var o in a.Overrides.Reverse())
                if (o.File != null && o.Sequences.FirstOrDefault(q => q.Name.Equals(animation, StringComparison.OrdinalIgnoreCase)) is { Name: not null } q2) return (o.File, o.Export, q2.Export);
            return null;
        }
        return (a.From.File, a.From.Export, a.Export);
    }
}
