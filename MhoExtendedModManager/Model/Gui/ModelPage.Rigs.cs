using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>The Model tab's Add Rigs ▾ (Kurt, 2026-10-10: coats, dresses, loincloths, tails, chains and wings borrowed like the
/// cape and hair, in one menu instead of a drop-down each).</summary>
sealed partial class ModelPage
{
    /// <summary>The rigs picked: kind → donor number (0 or missing = none). Cape and hair are saved as before (State.Cape / Hair).</summary>
    readonly Dictionary<BorrowedRig.Kind, int> rigs = new();
    Button rigsButton = null!;
    /// <summary>The kinds the shown hero has bones of its own for (kept with their motion: nothing is added).</summary>
    HashSet<BorrowedRig.Kind> ownKinds = [];

    int CapeChoice => rigs.GetValueOrDefault(BorrowedRig.Kind.Cape);
    int HairChoice => rigs.GetValueOrDefault(BorrowedRig.Kind.Hair);

    /// <summary>The kinds other than cape and hair, in turn, with a donor picked.</summary>
    List<(BorrowedRig.Kind Kind, int Number)> OtherRigs() =>
        [.. Enum.GetValues<BorrowedRig.Kind>().Where(k => k is not BorrowedRig.Kind.Cape and not BorrowedRig.Kind.Hair && rigs.GetValueOrDefault(k) > 0).Select(k => (k, rigs[k]))];

    /// <summary>The other kinds as saved ("Coat:1,Tail:2").</summary>
    string OtherRigsText() => string.Join(",", OtherRigs().Select(x => $"{x.Kind}:{x.Number}"));

    /// <summary>Every pick as saved with Undo ("Cape:2,Coat:1").</summary>
    string RigsText() => string.Join(",", Enum.GetValues<BorrowedRig.Kind>().Where(k => rigs.GetValueOrDefault(k) > 0).Select(k => $"{k}:{rigs[k]}"));

    /// <summary>The picks from saved text (cape and hair given separately, as State keeps them).</summary>
    void SetRigs(int cape, int hair, string? others, bool preview = true)
    {
        rigs.Clear();
        if (cape > 0) rigs[BorrowedRig.Kind.Cape] = cape;
        if (hair > 0) rigs[BorrowedRig.Kind.Hair] = hair;
        foreach (var (k, n) in BorrowedRig.ParsePicks(others))
            if (k is not BorrowedRig.Kind.Cape and not BorrowedRig.Kind.Hair && n <= BorrowedRig.Donors(k).Length) rigs[k] = n;
        UpdateRigsButton();
        if (preview) SchedulePreview();
    }

    /// <summary>The picks from Undo's text ("Cape:2,Coat:1").</summary>
    void SetRigsText(string text)
    {
        var all = BorrowedRig.ParsePicks(text);
        SetRigs(all.FirstOrDefault(x => x.Kind == BorrowedRig.Kind.Cape).Number, all.FirstOrDefault(x => x.Kind == BorrowedRig.Kind.Hair).Number, text);
    }

    static string KindName(BorrowedRig.Kind k) => k.ToString();

    static string KindTip(BorrowedRig.Kind k) => k switch
    {
        BorrowedRig.Kind.Cape => "Cape 1-3 move like Thor's, Doctor Strange's and Vision's capes.",
        BorrowedRig.Kind.Hair => "Hair 1 is short, Hair 3 the longest; Mega Hair stretches Hair 3's strands to the model's own hair length (manes such as Scream's or Medusa's).",
        BorrowedRig.Kind.Coat => "Long coat tails: Coat 1 and 2 move like Blade's and Nick Fury's coats.",
        BorrowedRig.Kind.Dress => "A dress or long skirt, front and back panels at each leg: Dress 1-3 move like Angela's, Emma Frost's and Black Cat's.",
        BorrowedRig.Kind.Loincloth => "Loincloth or sash strips that hang front and back: Loincloth 1-3 move like Iron Fist's, Scarlet Witch's and Doctor Strange's.",
        BorrowedRig.Kind.Tail => "A tail: Tail 1 and 2 move like Nightcrawler's and Squirrel Girl's.",
        BorrowedRig.Kind.Chains => "Hanging chains: Chains 1 moves like Luke Cage's.",
        _ => "Wings: Wings 1-4 move like Angel's feathered wings, Archangel's metal ones, Wasp's insect wings and Black Bolt's cape webbing (small beats).",
    };

    const string RigsTip = "Adds moving parts a base hero doesn't have: a cape, long hair, a coat, a dress, a loincloth, a tail, chains or wings. For each, bones are added so the model's own strips, strands or tail ride them, and every animation gets motion matched from an MHO hero whose part of that kind is hand-animated (for each frame, the one whose body moves most alike). Several can be on at once. A hero with bones of its own for a kind always keeps them and their motion, so that kind can't be added. Build includes them when the target is a base package (its animation sets are copied into the mod with the added motion); so do Full Export and Open in Blender.";

    void InitRigs()
    {
        rigsButton = Ui.FlatButton("Add Rigs ▾", ShowRigsMenu, RigsTip);
        UpdateRigsButton();
    }

    /// <summary>The button's text: what's picked ("Rigs: Cape 2, Coat 1"), else Add Rigs; the tooltip lists them.</summary>
    void UpdateRigsButton()
    {
        if (rigsButton == null) return;
        var picked = Enum.GetValues<BorrowedRig.Kind>().Where(k => rigs.GetValueOrDefault(k) > 0 && !ownKinds.Contains(k)).Select(k => BorrowedRig.Title(k, rigs[k])).ToList();
        rigsButton.Text = picked.Count == 0 ? "Add Rigs ▾" : $"Rigs: {string.Join(", ", picked)} ▾";
        string clash = Clash();
        Ui.Tip(rigsButton, (picked.Count == 0 ? "" : "Added: " + string.Join(", ", picked) + "." + Environment.NewLine + Environment.NewLine) + (clash.Length > 0 ? clash + Environment.NewLine + Environment.NewLine : "") + RigsTip);
    }

    /// <summary>Picks that cover the same place (a coat and a dress, a cape and wings): the model's strips can pair with either.</summary>
    string Clash()
    {
        bool On(BorrowedRig.Kind k) => rigs.GetValueOrDefault(k) > 0 && !ownKinds.Contains(k);
        var pairs = new List<string>();
        if (On(BorrowedRig.Kind.Coat) && On(BorrowedRig.Kind.Dress)) pairs.Add("a coat and a dress");
        if (On(BorrowedRig.Kind.Cape) && On(BorrowedRig.Kind.Wings)) pairs.Add("a cape and wings");
        if (On(BorrowedRig.Kind.Dress) && On(BorrowedRig.Kind.Loincloth)) pairs.Add("a dress and a loincloth");
        return pairs.Count == 0 ? "" : $"Note: {string.Join(" and ", pairs)} hang in the same place; the model's strips pair with whichever is nearer, so the other may get none.";
    }

    void ShowRigsMenu()
    {
        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        foreach (var kind in Enum.GetValues<BorrowedRig.Kind>())
        {
            int now = rigs.GetValueOrDefault(kind);
            bool own = ownKinds.Contains(kind);
            var head = new ToolStripMenuItem(own ? $"{KindName(kind)}: Hero's Own" : now > 0 ? $"{KindName(kind)}: {BorrowedRig.Title(kind, now)}" : KindName(kind))
            { ToolTipText = own ? $"This hero has {KindName(kind).ToLowerInvariant()} bones of its own: they're always kept, with their hand-animated motion, so nothing is added." : KindTip(kind), Enabled = !own, Checked = now > 0 && !own };
            void Pick(int n) { rigs[kind] = n; UpdateRigsButton(); SchedulePreview(); }
            head.DropDownItems.Add(new ToolStripMenuItem("None", null, (_, _) => Pick(0)) { Checked = now == 0, ToolTipText = $"No added {KindName(kind).ToLowerInvariant()} (it removes nothing the model has)." });
            int count = BorrowedRig.Donors(kind).Length + (kind == BorrowedRig.Kind.Hair ? 1 : 0);
            for (int n = 1; n <= count; n++)
            {
                int pick = kind == BorrowedRig.Kind.Hair && n > BorrowedRig.Donors(kind).Length ? BorrowedRig.MegaHair : n;
                head.DropDownItems.Add(new ToolStripMenuItem(BorrowedRig.Title(kind, pick), null, (_, _) => Pick(pick)) { Checked = now == pick, ToolTipText = KindTip(kind) });
            }
            menu.Items.Add(head);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Remove All", null, (_, _) => { rigs.Clear(); UpdateRigsButton(); SchedulePreview(); }) { Enabled = rigs.Values.Any(v => v > 0), ToolTipText = "Takes every added rig off (nothing the model or hero has is removed)." });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Ui.ShowUnder(menu, rigsButton);
    }

    static readonly Dictionary<string, HashSet<BorrowedRig.Kind>> ownRigs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which kinds the target hero has bones of its own for (read once per package version): those are shown as the
    /// hero's own in the menu, and a pick of them is ignored.</summary>
    async Task ShowOwnRigs(string packagePath)
    {
        string key = packagePath + "|" + File.GetLastWriteTimeUtc(packagePath).Ticks;
        if (!ownRigs.TryGetValue(key, out var own))
        {
            try
            {
                own = await Task.Run(() =>
                {
                    var names = MhoSkeleton.Load(packagePath, null).Bones.Select(b => b.Name).ToList();
                    return Enum.GetValues<BorrowedRig.Kind>().Where(k => names.Any(BorrowedRig.Pattern(k).IsMatch)).ToHashSet();
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            ownRigs[key] = own;
        }
        if (IsDisposed) return;
        ownKinds = own;
        UpdateRigsButton();
    }

    /// <summary>Test: the cape / hair as the menu shows them (text, can be picked).</summary>
    internal (string Cape, bool CapeOn, string Hair, bool HairOn) TestOwnRigs =>
        (ownKinds.Contains(BorrowedRig.Kind.Cape) ? "Hero's Own Cape" : CapeChoice > 0 ? BorrowedRig.Title(BorrowedRig.Kind.Cape, CapeChoice) : "No Added Cape", !ownKinds.Contains(BorrowedRig.Kind.Cape),
         ownKinds.Contains(BorrowedRig.Kind.Hair) ? "Hero's Own Hair" : HairChoice > 0 ? BorrowedRig.Title(BorrowedRig.Kind.Hair, HairChoice) : "No Added Hair", !ownKinds.Contains(BorrowedRig.Kind.Hair));
}
