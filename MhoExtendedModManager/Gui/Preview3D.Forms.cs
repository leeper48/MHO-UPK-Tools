namespace MhoExtendedModManager.Gui;

/// <summary>
/// A hero's form in the preview (Kurt, 2026-10-19, a user's report: Jean Grey's Phoenix form didn't show). Picking a power
/// that applies a form (Unleash the Phoenix) or a form's own version of a power (Pyrokinetic Barrier (Unleash the Phoenix))
/// shows the character in that form, as the game does while its condition is on: the animation set its alias names replaces
/// the animations of the same name (Jean's flying versions), its weapon-slot rules hold the whole time (her wings on, her
/// cape off), and its own effects (flames, wing trails) play under whatever plays. Another power picked takes the form off.
/// </summary>
sealed partial class Preview3D
{
    /// <summary>The character's own animations (without a form).</summary>
    List<AnimRef> baseAnims = [];
    /// <summary>The form shown (the power that applies it), or null.</summary>
    string? form;
    List<AnimRef> formAnims = [];
    List<ModMeshes.PropRule> formRules = [];
    List<string> formFiles = [];
    int formRequest;
    /// <summary>The form changed since the last power click: the animation picked loads again (the form's own version).</summary>
    bool formChanged;
    readonly Dictionary<string, (List<AnimRef> Anims, List<ModMeshes.PropRule> Rules, List<string> Files)> formCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The form shown (the power that applies it: Powers\Player\JeanGrey\Talents\PhoenixFormSpec.prototype), or null.</summary>
    public string? Form => form;

    /// <summary>The character's animations given (a new character or its list read): kept as its own, the form's laid over them.</summary>
    List<AnimRef> WithForm(List<AnimRef> animations)
    {
        baseAnims = animations;
        return form == null ? animations : Overlay(animations, formAnims);
    }

    /// <summary>The character's animations with the form's in place of those of the same name (and the form's own added).</summary>
    static List<AnimRef> Overlay(List<AnimRef> own, List<AnimRef> formSet)
    {
        var byName = formSet.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var list = own.Select(a => byName.TryGetValue(a.Name, out var f) ? f : a).ToList();
        foreach (var f in formSet) if (!own.Any(a => a.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))) list.Add(f);
        return [.. list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The form a power shows the character in: its own (a form's version of a power) or itself (the power applying it).</summary>
    static string? FormOf(Fx.PowerList.Power pw) => pw.Form ?? (pw.IsForm ? pw.Prototype : null);

    /// <summary>
    /// Shows <paramref name="want"/> (null: no form). False when it's shown already (nothing to wait for); true when it
    /// changes: <paramref name="then"/> runs once it's in (a form's animations and rules are read in the background, once).
    /// </summary>
    bool SetForm(string? want, Action then)
    {
        if (string.Equals(want, form, StringComparison.OrdinalIgnoreCase)) return false;
        int req = ++formRequest;
        if (want == null)
        {
            form = null; formAnims = []; formRules = []; formFiles = [];
            allAnims = baseAnims;
            formChanged = true;
            then();
            return true;
        }
        if (formCache.TryGetValue(want, out var hit)) { Apply(hit); return true; }
        if (ctx is not { Cooked: string cooked, Hero: string hero, MainRef: MeshRef main } c) { form = want; formChanged = true; then(); return true; }   // (nothing to read it from: shown as it is)
        var mods = c.ModPackages.ToList();
        fxLabel.Text = "Reading the Form…";
        PowerPreview.GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            using var busy = Busy.Begin("3D view: reading the form");
            if (dbt.Result is not { } db || Fx.HeroForms.AliasOf(db, want, cooked, mods.Select(m => m.Path)) is not { } a) return (Anims: new List<AnimRef>(), Rules: new List<ModMeshes.PropRule>(), Files: new List<string>());
            string baseFile = $"UC__MarvelPlayer_{hero}_SF.upk";
            string basePath = mods.FirstOrDefault(m => m.File.Equals(baseFile, StringComparison.OrdinalIgnoreCase)).Path is { } mine && File.Exists(mine) ? mine : StockFiles.For(cooked, baseFile);
            var set = Fx.HeroForms.AnimSetFor(a.Alias, [main.File, basePath]);
            var anims = set is { } s ? ModAnimations.FromSet(s.Path, s.Export) : [];
            var rules = a.Files.SelectMany(ModMeshes.PropRules).Distinct().ToList();
            return (Anims: anims, Rules: rules, Files: a.Files.Append(basePath).ToList());
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != formRequest) return;
            var got = t.Status == TaskStatus.RanToCompletion ? t.Result : (new List<AnimRef>(), new List<ModMeshes.PropRule>(), new List<string>());
            formCache[want] = got;
            Apply(got);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return true;

        void Apply((List<AnimRef> Anims, List<ModMeshes.PropRule> Rules, List<string> Files) f)
        {
            if (req != formRequest) return;
            form = want; formAnims = f.Anims; formRules = f.Rules; formFiles = f.Files;
            allAnims = Overlay(baseAnims, formAnims);
            formChanged = true;
            then();
        }
    }

    /// <summary>Selects animation <paramref name="index"/> (the drop-down's), loading it again when it's selected already and
    /// <paramref name="force"/> (the form changed: the same name is now the form's version).</summary>
    void Pick(int index, bool force)
    {
        if (anims.SelectedIndex == index) { if (force) AnimationChosen(); }
        else anims.SelectedIndex = index;
    }

    /// <summary>Effects with the form's under them (read in the background with the power's).</summary>
    PowerPreview.Loaded? FormEffects(Fx.GameData db, PowerPreview.Loaded? power, string? hero, string cooked, IReadOnlyList<string> modFiles, string? entity, MeshRef main, string? formNow)
    {
        if (formNow == null) return power;
        var f = PowerPreview.ForForm(db, formNow, hero, cooked, modFiles, entity, main.File, main.Name);
        // the power applying the form itself: its form's effects only (not the Dark Phoenix it can turn into)
        // (one of its effects alone, the Parts ▶: from all it sets off, so its Dark Phoenix parts show too)
        if (power != null && power.Power.Equals(formNow, StringComparison.OrdinalIgnoreCase)) return effects?.OnlyEffect != null ? power : f;
        return PowerPreview.WithForm(power, f);
    }
}
