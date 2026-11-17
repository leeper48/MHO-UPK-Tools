namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Animations tab's Bone Lengths and Shape Keys (Kurt, 2026-11-17; Carnage's Vicious Lunge on Medusa): for the selected
/// animation, Keep Bone Lengths (the bones whose animated positions this costume's copy doesn't take: AnimSwap leaves them out
/// of the copied set's UseTranslationBoneNames) and Shape Keys (the morph-target curves left out of the copy's CurveData). An
/// animation not changed yet becomes a copy of itself (a change from where it plays now), so only this costume changes.
/// </summary>
sealed partial class ModEditorView
{
    Button? animBody;

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
        List<string> newKeep, newOff;
        using (var f = new AnimBodyForm(slot.Name, rows, curveRows))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            newKeep = f.KeepLengths; newOff = f.CurvesOff;
        }
        if (newKeep.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(keep) && newOff.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(off)) return;
        await ApplyBody(slot, file, src, entry, newKeep, newOff);
    }

    /// <summary>The bones the sequence slides (position keys reaching more than 0.05 from their first) and its shape-key curves.</summary>
    static (List<(string Name, float Reach)> Moving, List<string> Curves) ReadBody(CostumeAnims.Anim slot)
    {
        var moving = new List<(string, float)>();
        var curves = new List<string>();
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
            var pkg = MhoPackageModifier.Package.Open(slot.From.File!);
            curves = AnimSwap.Curves(pkg, pkg.ReadExportBytes(pkg.Exports[slot.Export]).ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or AnimExportCli.Packages.InvalidPackageException or ArgumentOutOfRangeException) { }
        return (moving, curves);
    }

    /// <summary>
    /// The slot's copy made again with these choices: from its change's source (the manifest's), or, for an animation not
    /// changed yet, from where it plays now.
    /// </summary>
    async Task ApplyBody(CostumeAnims.Anim slot, string file, string src, AnimSwapEntry? entry, List<string> keepLengths, List<string> curvesOff)
    {
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
            with { KeepLengths = keepLengths.Count > 0 ? keepLengths : null, CurvesOff = curvesOff.Count > 0 ? curvesOff : null };
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
        await PropsAfterAnimChange(file);
        animStatus.Text = $"\"{slot.Name}\": " + (keepLengths.Count + curvesOff.Count == 0 ? "Its Own Bone Lengths and Shape Keys"
            : string.Join(" · ", new[] { keepLengths.Count > 0 ? $"{keepLengths.Count} Bone Length(s) Kept" : null, curvesOff.Count > 0 ? $"{curvesOff.Count} Shape Key(s) Off" : null }.OfType<string>()));
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
