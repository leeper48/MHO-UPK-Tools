using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Props view (Kurt, 2026-10-27: "feels kind of disjointed": a tab after Characters in place of the Add a Prop ▾ button). The
/// Characters view's Source / Preview / Target, with two differences: a click on a source character doesn't make it the source
/// and never changes the target, it only lists its props in the bottom third of the Source column (a game costume's props from
/// the census, an MFF character's parts with its weapons first, a Single Model FBX as made); and a click on one of those props
/// adds it to the costume picked under Target (Add a Prop, as the Parts tab's list shows it after). Characters are changed only
/// in the Characters view.
/// </summary>
sealed partial class ModelPage
{
    bool propMode;
    /// <summary>The MFF character whose parts the Props view lists (loaded on its own: the Characters view's source stays).</summary>
    MffModel? propModel;
    string? propModelKey;
    int propModelRun;
    bool addingProp;

    const string PropGame = "propgame:", PropMff = "propmff:", PropFbx = "propfbx:", PropSummon = "propsummon:";

    /// <summary>A list title without the "changed in the mod" dot.</summary>
    static string Plain(string title) => title.Replace(" •", "").Replace("•", "").Trim();

    void SetPropMode(bool on)
    {
        if (propMode == on) return;
        propMode = on;
        if (!on) HidePropShow();
        sourceSummonsTitle.Text = "";
        ShowSummonsPanel();
        if (!on && sourceSummonsPanel.Visible) FillSourceSummons();   // (the Characters view's summons and props again)
        Ui.Tip(characters, on
            ? "Props view: click a character to list its props below (the target and the Characters view's source stay as they are). Left-click a prop there to see it, right-click it to add it to the target."
            : "Click a character to use it as the source.");
    }

    /// <summary>The characters list's pick, for the Props view: the bottom third lists its props.</summary>
    void FillPropSource()
    {
        fillingSummons = true;
        sourceSummons.BeginUpdate(); sourceSummons.Items.Clear();
        string target = ChosenPackage is CharacterList.Item t ? t.Title : "the target";
        var it = characters.SelectedItem as CharacterList.Item;
        void Note(string text) => sourceSummons.Items.Add(new CharacterList.Item("label:" + text, text, "", Label: true));
        if (it == null || it.Label || it.Key == "browse:")
        {
            sourceSummonsTitle.Text = "PROPS";
            Note("Pick a character above: its props are listed here");
        }
        else if (it.Key.StartsWith(GameSrc) || it.Key.StartsWith("group:game "))
        {
            string hero; string? costume = null;
            if (it.Header) hero = it.Key["group:game ".Length..];
            else
            {
                string file = SplitGameKey(it.Key).File;
                hero = DescribePackage(file).Title;
                if (KindOf(file) == 0) costume = ConditionSwaps.ClassOf(file);
            }
            // every kind of the costume's props (Kurt, 2026-10-27: Squirrel Girl's surf squirrels are an animated one), then its
            // summons and power models (as the Characters view's panel lists them), each added as a model of its own
            var props = PropsFor(hero, costume, allKinds: true);
            string who = it.Header || it.Title.Contains(hero, StringComparison.OrdinalIgnoreCase) ? it.Title : $"{hero} {it.Title}";
            sourceSummonsTitle.Text = $"PROPS · {Plain(who).ToUpperInvariant()} · LEFT-CLICK TO SEE ONE, RIGHT-CLICK TO ADD IT TO {Plain(target).ToUpperInvariant()}";
            foreach (var p in props)
                sourceSummons.Items.Add(new CharacterList.Item(PropGame + Props.IndexOf(p), PropLabel(p.Mesh),
                    $"{(costume == null ? string.Join(", ", Props.Where(x => x.MeshPath.Equals(p.MeshPath, StringComparison.OrdinalIgnoreCase)).Select(x => Spaced(x.Costume)).Distinct().Take(4)) + " · " : "")}"
                    + (p.Kind == "other" ? "animated prop: moves with its own animations" : p.Kind == "rigged" ? "rides the character's bones" : $"held on {p.Bone}"), ThumbKey: p.CostumeFile));
            var summons = WhatsNew.Summons ? allPackages.Where(p => IsSummon(p.File) && Summons[Path.GetFileName(p.File)][0].Hero.Equals(hero, StringComparison.OrdinalIgnoreCase)
                && ForCostume(Summons[Path.GetFileName(p.File)], costume, hero)).ToList() : [];
            if (summons.Count > 0) Note("SUMMONS AND POWER MODELS · added as a model of their own");
            foreach (var p in summons)
            {
                int dot = p.Detail.IndexOf(" · ", StringComparison.Ordinal);
                sourceSummons.Items.Add(new CharacterList.Item(PropSummon + p.File, dot > 0 ? p.Detail[..dot] : p.Detail, dot > 0 ? p.Detail[(dot + 3)..] : p.File, ThumbKey: p.File));
            }
            if (props.Count == 0 && summons.Count == 0) Note(it.Header || !it.Key.StartsWith(GameSrc + "UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) ? "No props (heroes' costumes have them)" : "This costume has no props of its own");
        }
        else if (it.Header) { sourceSummonsTitle.Text = "PROPS"; Note("Pick one of its models above"); }
        else if (it.Key.StartsWith("fbx:"))
        {
            string file = it.Key[4..];
            sourceSummonsTitle.Text = $"PROPS · LEFT-CLICK TO SEE IT, RIGHT-CLICK TO ADD IT TO {target.ToUpperInvariant()}";
            sourceSummons.Items.Add(new CharacterList.Item(PropFbx + file, Path.GetFileNameWithoutExtension(file) + " (FBX)", "The whole file as a prop, as made: its size and shape, its origin on the bone"));
        }
        else
        {
            // an MFF character (the repository's, or a folder picked on its own): its parts, the weapons first
            string key = it.Key;
            var m = propModelKey == key ? propModel : chosenKey == key ? model : null;
            sourceSummonsTitle.Text = $"PROPS · {it.Title.ToUpperInvariant()} · LEFT-CLICK TO SEE ONE, RIGHT-CLICK TO ADD IT TO {target.ToUpperInvariant()}";
            if (m == null) { Note("Loading its parts…"); _ = LoadPropModel(key); }
            else
            {
                foreach (var p in m.Parts.OrderByDescending(p => p.IsProp))
                    sourceSummons.Items.Add(new CharacterList.Item(PropMff + p.Name, p.Name,
                        $"{(p.IsProp ? "Prop part" : p.IsAlternate ? "Swap part" : "Body part")} · {p.Verts:N0} vertices"));
                if (m.Parts.Count == 0) Note("No parts");
            }
        }
        sourceSummons.EndUpdate();
        sourceSummons.ClearSelected();
        fillingSummons = false;
    }

    async Task LoadPropModel(string key)
    {
        int run = ++propModelRun;
        string src = key.StartsWith(MffDir) ? key[MffDir.Length..] : key;
        MffModel m;
        using (Busy.Begin("Model: reading the character's parts"))
            try { m = await Task.Run(() => MffModel.Load(Source.ResolveModelFile(src))); }
            catch (Exception ex) { Log($"{key}: {ex.Message}"); return; }
        if (IsDisposed || run != propModelRun) return;
        propModel = m; propModelKey = key;
        if (propMode && (characters.SelectedItem as CharacterList.Item)?.Key == key) FillPropSource();
    }

    /// <summary>A prop clicked in the Props view: added to the costume picked under Target.</summary>
    async void PropPicked(CharacterList.Item it)
    {
        if (ChosenPackage is not CharacterList.Item pkg) { Log("Pick the costume to add the prop to under Target first."); return; }
        if (addingProp) { Log($"{Plain(it.Title)}: one moment, a prop is still being added (click it again when the log says it's in)."); return; }
        addingProp = true;
        HidePropShow();   // (the target, to see it there)
        int kind = sourceKind.SelectedIndex;
        string? charKey = (characters.SelectedItem as CharacterList.Item)?.Key;
        try
        {
            if (it.Key.StartsWith(PropGame) && int.TryParse(it.Key[PropGame.Length..], out int n) && n >= 0 && n < Props.Count)
                await AddGameProp(pkg.Key, Props[n]);
            else if (it.Key.StartsWith(PropFbx)) await AddFbxProp(pkg.Key, it.Key[PropFbx.Length..]);
            else if (it.Key.StartsWith(PropSummon)) await AddSummonProp(pkg.Key, it.Key[PropSummon.Length..], it.Title);
            else if (it.Key.StartsWith(PropMff) && propModelKey == charKey && propModel is { } pm) await AddPartProp(pm, it.Key[PropMff.Length..]);
            else if (it.Key.StartsWith(PropMff) && chosenKey == charKey && model is { } cm) await AddPartProp(cm, it.Key[PropMff.Length..]);
        }
        finally
        {
            addingProp = false;
            // an FBX prop is built with the FBX as the source (Single Model): back to the list the user was in
            if (!IsDisposed && propMode && sourceKind.SelectedIndex != kind)
            {
                kindByState = true; sourceKind.SelectedIndex = kind; kindByState = false;
                if (charKey != null)
                {
                    if (charKey.StartsWith(GameSrc) && allPackages.FirstOrDefault(p => GameSrc + p.File == charKey) is { File: not null } gp) expanded.Add("game " + gp.Title);
                    FillCharacters();
                    Reselect(characters, charKey);
                }
            }
        }
    }

    /// <summary>A summon or power model (its character's model from the game, written as an FBX) added to <paramref name="key"/> as a
    /// prop of its own, as made (its own size; Prop Fit and Held On ▾ place it).</summary>
    async Task AddSummonProp(string key, string file, string title)
    {
        status.Text = $"Reading {title} from the game…";
        string fbx;
        using (Busy.Begin($"Model: reading {file} from the game"))
            try { fbx = await Task.Run(() => GameSource.Export(file, line => BeginInvoke(() => Log(line)))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { Log($"{file}: {ex.Message}"); UpdateStatus(); return; }
        if (IsDisposed) return;
        // "Summon: Pet Squirrel Modern, Triple Squirrel (For Monkey Joe!, …)" → "Pet Squirrel Modern, Triple Squirrel (Summon)"
        string name = Plain(title);
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) name = name[..paren];
        int colon = name.IndexOf(": ", StringComparison.Ordinal);
        string kind = colon > 0 ? name[..colon] : "Summon";
        if (colon > 0) name = name[(colon + 2)..];
        await AddFbxProp(key, fbx, $"{name} ({kind})");
    }

    /// <summary>A game costume's prop added to <paramref name="key"/> (Add a Prop): its settings taken from the package that has its
    /// class (the costume's own, else the hero's base package).</summary>
    async Task AddGameProp(string key, PropRow p)
    {
        string name = $"{AutoTags.DisplayName(p.Hero) ?? Spaced(p.Hero)}: {PropLabel(p.Mesh)}";
        if (host.AddedProps(key).Any(a => a.Class.Equals(p.Class, StringComparison.OrdinalIgnoreCase))) { Log($"{name} is on {key} already."); return; }
        string? from = await Task.Run(() =>
        {
            if (Settings.Current.CookedFolder is not string cooked) return null;
            foreach (string f in new[] { p.CostumeFile, $"UC__MarvelPlayer_{p.Hero}_SF.upk", p.MeshPackage }.Distinct(StringComparer.OrdinalIgnoreCase))
                try
                {
                    string path = StockFiles.For(cooked, f);
                    if (File.Exists(path) && PowerBorrow.PropClasses(path).Contains(p.Class, StringComparer.OrdinalIgnoreCase)) return f;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
            return null;
        });
        if (IsDisposed) return;
        // a costume's own prop class (Beta Ray Bill's hammer: it builds on Thor's class, which the game loads only with Thor) can't
        // go to another hero as it is: its model comes in on a plain prop class instead, held on the same bone
        if (from == null || !from.Equals($"UC__MarvelPlayer_{p.Hero}_SF.upk", StringComparison.OrdinalIgnoreCase))
        {
            Log($"{name}: its class is the costume's own ({p.Class}, built on its hero's), so its model comes in on a plain prop class, held on {p.Bone}.");
            status.Text = $"Reading {name} from the game…";
            string fbx;
            using (Busy.Begin($"Model: reading {p.Mesh} from the game"))
                try { fbx = await Task.Run(() => GameSource.Export(p.MeshPackage, line => BeginInvoke(() => Log(line)), p.Mesh)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { Log($"{name}: {ex.Message}"); UpdateStatus(); return; }
            if (IsDisposed) return;
            await AddFbxProp(key, fbx, name + " (Model)", p.Bone.Length > 0 ? p.Bone : null);
            return;
        }
        string before = PropBefore();
        if (await host.AddPropTo(key, new AddPropForm.Pick(from, p.Class, name)) is not { } e || IsDisposed) return;
        PropDone(before, $"Add {e.Name}");
        await AddedPropsChanged(key, e);
        Log($"{e.Name} added to {key}: it's picked under the preview, so the Prop Fit sliders move it. Save Changes keeps it.");
        if (p.Kind == "other")
            Log($"{e.Name}: an animated prop (it moves with animations of its own, played with its character's animations of the same name): the preview shows it only while one of those plays, and on another character it may not show in game either.");
    }
}
