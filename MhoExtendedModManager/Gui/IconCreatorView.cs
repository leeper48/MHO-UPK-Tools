using System.Drawing.Imaging;
using System.Numerics;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Create from 3D (Kurt): an icon made from a character in the preview's renderer, inside the editor's texture tabs. The
/// texture selected on the left is the target: the view has its shape and the snapshot its size, so what is framed is what
/// is saved. The game's original can be laid over the view (opacity slider) to line up pose and size. Each icon keeps its
/// own setup (character, animation and frame, camera, light, background: PreviewViews.IconSetup), so every icon can have
/// a different pose and camera. Stock style per kind (checked 2026-09-28 on Storm and Thor):
///   store_…    300×420, full body, transparent background
///   herohor…   180×136, head and shoulders on the blue hex backdrop (Assets\herohor_bluebackdrop.png, from Kurt)
///   costume…   40×40, bust, transparent background
/// Other textures: their own size, transparent. The snapshot is drawn 4× larger and scaled down (smooth edges, soft alpha)
/// and laid on the background; Use as Replacement hands a PNG to the editor, which converts it like any chosen PNG.
/// </summary>
sealed class IconCreatorView : UserControl
{
    public enum Kind { Store, Portrait, Costume, Other }

    int targetW = 300, targetH = 420;
    Kind kind;
    string texture = "";
    string setupKey = "";
    readonly string? cooked;
    readonly Func<IEnumerable<(string File, string Path)>> packages;
    readonly List<MeshRef> meshes = [];
    readonly DropDown meshBox = new();
    readonly DropDown animBox = new() { MaxDropDownItems = 24 };
    readonly TextBox animSearch = new() { PlaceholderText = "Search poses (e.g. idle, attack, run)" };
    // The animations the drop-down lists now (indexes into anims; Kurt: a search filter for poses). Item 0 is Rest Pose.
    List<int> shown = [];
    int current = -1;   // the animation shown (index into anims), -1 = rest pose
    readonly DropDown backBox = new();
    readonly CheckedListBox propList = new() { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.None };
    // Props shown with the character (Kurt: a character holding a sword or hammer): each loaded mesh and the bone it's held on.
    readonly List<(MeshRef Ref, ModMeshes.Loaded Mesh, int Bone, string BoneName)> props = [];
    readonly List<(MeshRef Ref, string? Bone)> propChoices = [];
    readonly PropRig rig = new();   // the props' geometry, shared with the main preview
    readonly LightSlider frameSlider = new() { Label = "Frame", Min = 0, Max = 1, Step = 1, Mark = null };
    readonly LightSlider lightSlider = new() { Min = 0f };
    readonly LightSlider overlaySlider = new() { Label = "Original", Min = 0, Max = 1, Step = 0.05f, Mark = null };
    // The overlay on / off (Kurt: a toggle button and a shortcut, O); the slider keeps its opacity while it's off.
    Button overlayBtn = null!;
    bool overlayOn = true;
    readonly LightSlider lensSlider = new() { Label = "Lens", Min = 15, Max = 200, Step = 0.5f, Mark = ModelView.DefaultFocalLength };
    /// <summary>The model's turn in degrees (0 = facing the camera); follows a drag in the view (a user: a visual slider for it).</summary>
    readonly LightSlider turnSlider = new() { Label = "Turn", Min = -180, Max = 180, Step = 1, Mark = 0 };
    bool syncingTurn;
    readonly ModelView view = new() { Background = Color.FromArgb(22, 22, 24) };
    readonly Button specBtn, reflBtn, glowBtn;

    /// <summary>The Spec / Reflect / Glow toggles (shared with the main preview) into the view, and their look.</summary>
    void ApplyShading()
    {
        view.ShowSpec = PreviewViews.Spec; view.ShowReflections = PreviewViews.Reflect; view.ShowGlow = PreviewViews.Glow; view.ShowBloom = PreviewViews.Bloom;
        Ui.Lit(specBtn, PreviewViews.Spec); Ui.Lit(reflBtn, PreviewViews.Reflect); Ui.Lit(glowBtn, PreviewViews.Glow);
    }
    readonly Panel stage = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 16, 18) };
    readonly PictureBox result = new() { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 16, 18) };
    readonly Label title = new() { AutoSize = true, Font = Ui.Bold(11f), Margin = new Padding(0, 0, 0, 2) };
    readonly Label hint = new() { AutoSize = true, Tag = "subtle" };
    readonly Label resultInfo = new() { AutoSize = true, Tag = "subtle" };
    readonly Label status = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 6, 0, 0) };
    readonly Button useBtn, saveBtn;
    Image? backdrop, customBack, original;
    string? customBackPath;
    Color backColor = Color.FromArgb(40, 50, 75);
    ModMeshes.Loaded? loaded;
    MeshRef? loadedRef;
    MeshAnimator? animator;
    List<AnimRef> anims = [];
    AnimExportCli.Animation.BoneAnimation? playing;
    float playFrames;
    int request;
    bool restoring;
    Bitmap? snapshot;

    /// <summary>Use as Replacement: (texture, the snapshot as a PNG).</summary>
    public event Action<string, string, bool>? Use;   // (texture, png, keep its size: Double Size)

    // Snapshots are the original's size. (A double-size test, 2026-09-29, showed the game draws an icon at its own pixel
    // size, twice as big, not scaled into the slot; shrinking a 2× DXT back to 1× and re-encoding measured no better.)
    int SnapW => targetW;
    int SnapH => targetH;
    const bool doubleSize = false;

    public static Kind KindOf(string texture) =>
        texture.StartsWith("store_", StringComparison.OrdinalIgnoreCase) ? Kind.Store
        : texture.StartsWith("herohor", StringComparison.OrdinalIgnoreCase) ? Kind.Portrait
        : texture.StartsWith("costume", StringComparison.OrdinalIgnoreCase) ? Kind.Costume : Kind.Other;

    public IconCreatorView(Func<IEnumerable<(string File, string Path)>> packages, string? cookedFolder)
    {
        this.packages = packages; cooked = cookedFolder;
        Dock = DockStyle.Fill;
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 6, 0, 0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260 * s)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220 * s));

        // Left: what to show and how.
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(0, 0, 4, 0) };
        int cw = (int)(250 * s) - SystemInformation.VerticalScrollBarWidth;   // no sideways scrolling
        void Row(Control c, int top = 0)
        {
            c.Margin = new Padding(0, top, 0, 4);
            if (c is Button b) { b.AutoSize = false; b.AutoEllipsis = true; b.Height = (int)(30 * s); }
            if (c is not System.Windows.Forms.Label) c.Width = cw;
            left.Controls.Add(c);
        }
        // Never scroll sideways: a horizontal bar appearing and going made the whole window jitter (a user, 0.35.23).
        left.HorizontalScroll.Maximum = 0; left.AutoScroll = false; left.HorizontalScroll.Visible = false; left.HorizontalScroll.Enabled = false; left.AutoScroll = true;
        Label Caption(string t) => new() { Text = t, AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f) };
        hint.MaximumSize = new Size(cw, 0);
        foreach (var sl in new[] { frameSlider, lightSlider, overlaySlider, lensSlider, turnSlider }) sl.Height = (int)(24 * s);
        // The framing buttons as a fixed 2 × 2 grid: always the column's width (a wrapping row overflowed at 150 % scaling).
        var presets = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Width = cw, Height = (int)(64 * s), Margin = new Padding(0) };
        presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        presets.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); presets.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        presets.Controls.AddRange([
            Ui.FlatButton("Full Body", () => { Preset(Kind.Store); SaveSetup(); }, tip: "Frame the whole character (as the store images are)."),
            Ui.FlatButton("Head & Shoulders", () => { Preset(Kind.Portrait); SaveSetup(); }, tip: "Frame the head and shoulders (as the hero portraits are)."),
            Ui.FlatButton("Bust", () => { Preset(Kind.Costume); SaveSetup(); }, tip: "Frame head and chest (as the costume icons are)."),
            Ui.FlatButton("Reset View", ResetView, tip: "Back to this icon's usual framing, facing front, with the standard lens and light."),
        ]);
        foreach (Button pb in presets.Controls) { pb.AutoSize = false; pb.AutoEllipsis = true; pb.Dock = DockStyle.Fill; pb.Margin = new Padding(0, 0, 3, 3); pb.Padding = new Padding(2, 0, 2, 0); }
        Row(title); Row(hint);
        Row(Caption("MODEL"), 8); Row(meshBox);
        Row(Ui.FlatButton("Open a .UPK", OpenUpk, tip: "Show a character from any package file (for example a stock costume) instead of the mod's own."));
        Row(Caption("PROPS"), 8);
        propList.Height = (int)(70 * s);
        Row(propList);
        // "Find" beside the search box (its grey placeholder text doesn't show in the dark theme).
        var findRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = false, Height = animSearch.PreferredHeight + 2, Margin = new Padding(0) };
        findRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); findRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        findRow.Controls.Add(new Label { Text = "Find", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Margin = new Padding(0, 0, 6, 0) }, 0, 0);
        animSearch.Dock = DockStyle.Fill; animSearch.Margin = new Padding(0);
        findRow.Controls.Add(animSearch, 1, 0);
        Row(Caption("POSE"), 8); Row(findRow); Row(animBox); Row(frameSlider);
        overlayBtn = Ui.FlatButton("Original Overlay", ToggleOverlay, tip: "Show or hide the game's original over the view (shortcut: O). Its opacity is the slider above.");
        // Spec / Reflect / Glow (Kurt): as in the main 3D preview, and shared with it; the snapshot is taken as shown.
        var shading = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Width = cw, Height = (int)(30 * s), Margin = new Padding(0) };
        for (int i = 0; i < 3; i++) shading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        specBtn = Ui.FlatButton("Spec", () => { PreviewViews.Spec = !PreviewViews.Spec; ApplyShading(); }, tip: "Show the specular highlights (shine) the materials set, in the view and the snapshot. Lit when on; shared with the main 3D preview.");
        reflBtn = Ui.FlatButton("Reflect", () => { PreviewViews.Reflect = !PreviewViews.Reflect; ApplyShading(); }, tip: "Show reflections of the materials' own environment images, in the view and the snapshot. Lit when on; shared with the main 3D preview.");
        glowBtn = Ui.FlatButton("Glow", () => { PreviewViews.Glow = !PreviewViews.Glow; ApplyShading(); }, tip: "Show glowing (emissive) parts, in the view and the snapshot. Lit when on; shared with the main 3D preview.");
        foreach (var b in new[] { specBtn, reflBtn, glowBtn }) { b.AutoSize = false; b.Dock = DockStyle.Fill; b.Margin = new Padding(0, 0, 3, 0); b.Padding = new Padding(0); shading.Controls.Add(b); }
        ApplyShading();
        // The main preview may have changed them since: pick them up whenever this shows again.
        VisibleChanged += (_, _) => { if (Visible) ApplyShading(); };
        Row(Caption("LOOK"), 8); Row(lightSlider); Row(shading); Row(backBox); Row(overlaySlider); Row(overlayBtn);
        previousBtn = Ui.FlatButton("Use Previous Setup", UsePrevious, tip: "Set this icon up like the one you had open before.");
        previousBtn.AutoEllipsis = true;
        Row(Caption("FRAMING"), 8); Row(turnSlider); Row(lensSlider); Row(presets); Row(previousBtn);
        root.Controls.Add(left, 0, 0);

        // Middle: the viewfinder in the target's shape.
        stage.Controls.Add(view);
        stage.Resize += (_, _) => FitView();
        root.Controls.Add(stage, 1, 0);

        // Right: the snapshot and what to do with it.
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(new Label { Text = "SNAPSHOT", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(0, 0, 0, 4) }, 0, 0);
        right.Controls.Add(result, 0, 1);
        right.Controls.Add(resultInfo, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        saveBtn = Ui.FlatButton("Save as .PNG", SavePng, tip: "Save the snapshot as a PNG file (it stays out of the mod).");
        useBtn = Ui.AccentButton("Use as Replacement", UseIt, tip: "Put the snapshot into the mod as the replacement for the selected texture (converted like a chosen PNG).");
        Button? largeBtn = null;
        largeBtn = Ui.FlatButton("Save Large PNG ▾", () => LargeMenu(largeBtn!), tip: "A large picture of the framed view for a post or promo shot (1024 or 2048 px; square or the icon's shape), saved as PNG. It stays out of the mod: the game only takes the icon's own size.");
        buttons.Controls.AddRange([Ui.AccentButton("Take Snapshot", Snap, tip: "Render the framed view at the texture's size (smooth edges) and show it above."), useBtn, saveBtn, largeBtn]);
        useBtn.Enabled = saveBtn.Enabled = false;
        right.Controls.Add(buttons, 0, 3);
        right.Controls.Add(status, 0, 4);
        root.Controls.Add(right, 2, 0);
        Controls.Add(root);

        // Backgrounds: transparent, the portrait backdrop, a color, or a picture.
        string back = Path.Combine(AppContext.BaseDirectory, "Assets", "herohor_bluebackdrop.png");
        if (File.Exists(back)) try { using var b = Image.FromFile(back); backdrop = new Bitmap(b); } catch (Exception ex) when (ex is OutOfMemoryException or IOException) { }
        backBox.Items.AddRange(["Background: Transparent", "Background: Portrait Backdrop (Blue Grid)", "Background: Color", "Background: Picture"]);
        backBox.SelectedIndexChanged += (_, _) => { if (!restoring) { BackgroundChanged(ask: true); SaveSetup(); } };
        Ui.Tip(propList, "Show these with the character: weapons and other props. Each is held on the bone the game attaches it to (for example Thor's hammer in the right palm), or in the right hand when the package doesn't say.");
        propList.ItemCheck += (_, _) => { if (!restoring) BeginInvoke(() => { LoadProps(null); }); };
        Ui.Tip(backBox, "What goes behind the character: transparent (store images and costume icons), the portrait backdrop (hero portraits), a color or a picture of your own.");
        Ui.Tip(meshBox, "The character (skeletal mesh) to show: from the mod's packages, or one opened with Open a .UPK.");
        Ui.Tip(animBox, "Pose the character with one of its animations (then pick the moment with the Frame slider).");
        Ui.Tip(frameSlider, "The moment of the animation to show.");
        Ui.Tip(lightSlider, "Light brightness (double-click: 100%).");
        Ui.Tip(overlaySlider, "The game's original image over the view, to line up pose and size (0: hidden). It isn't part of the snapshot.");

        meshBox.SelectedIndexChanged += (_, _) => { if (!restoring) LoadMesh(null); };
        animBox.SelectedIndexChanged += (_, _) => { if (!restoring) { current = animBox.SelectedIndex > 0 && animBox.SelectedIndex - 1 < shown.Count ? shown[animBox.SelectedIndex - 1] : -1; LoadAnim(0); } };
        animSearch.TextChanged += (_, _) => FillAnims();
        MhoPackageModifier.Gui.SearchBox.AddClear(animSearch);
        Ui.Tip(animSearch, "Narrow the pose list: every word must be in the animation's name. The pose shown stays until you pick another.");
        frameSlider.ValueChanged += Pose;
        frameSlider.Committed += SaveSetup;
        lightSlider.Value = 1;
        lightSlider.ValueChanged += () => view.Brightness = lightSlider.Value;
        lightSlider.Committed += SaveSetup;
        overlaySlider.Format = v => $"{v * 100:0} %";
        overlaySlider.Value = PreviewViews.Overlay;
        overlaySlider.ValueChanged += () => { overlayOn = true; ApplyOverlay(); };
        overlaySlider.Committed += () => PreviewViews.Overlay = overlaySlider.Value;
        overlaySlider.Home = () => 0.35f;
        ApplyOverlay();
        view.ViewChanged += SaveSetup;
        view.ViewChanged += SyncTurn;
        turnSlider.Format = v => $"{v:0}°";
        turnSlider.Home = () => 0;
        turnSlider.ValueChanged += () =>
        {
            if (restoring || syncingTurn) return;
            var vs = view.ViewState; vs[0] = turnSlider.Value * MathF.PI / 180f; view.ViewState = vs;
        };
        turnSlider.Committed += SaveSetup;
        Ui.Tip(turnSlider, "Turn the model: 0° faces the camera. Dragging in the view turns it too (double-click here: 0°).");
        lensSlider.Format = v => $"{v:0} mm";
        lensSlider.Value = ModelView.DefaultFocalLength;
        lensSlider.Home = () => ModelView.DefaultFocalLength;
        lensSlider.ValueChanged += () => { if (!restoring) view.FocalLength = lensSlider.Value; };
        lensSlider.Committed += SaveSetup;
        Ui.Tip(lensSlider, "The camera's lens (35 mm equivalent): short is wide with strong perspective, long is flatter. The character stays the same size in the frame; only the perspective changes (double-click: 50 mm, the 3D view's own).");
        title.Text = "Create from 3D";
        hint.Text = "Select a texture on the left.";
        ImageViewerForm.Attach(result, () => (snapshot, $"Snapshot: {texture}  ·  {targetW}×{targetH}", texture + "_snapshot"));   // the real pixels, not the enlarged copy
    }

    void ToggleOverlay() { overlayOn = !overlayOn; ApplyOverlay(); }

    /// <summary>The Turn slider follows the view (a drag, a preset, a restored setup) without turning it back.</summary>
    void SyncTurn()
    {
        float deg = view.ViewState[0] * 180f / MathF.PI;
        deg = ((deg + 180f) % 360f + 360f) % 360f - 180f;
        syncingTurn = true;
        try { turnSlider.Value = deg; } finally { syncingTurn = false; }
    }

    /// <summary>Reset View: this icon's usual framing, facing front, the standard lens and light.</summary>
    void ResetView()
    {
        lensSlider.Value = ModelView.DefaultFocalLength; view.FocalLength = ModelView.DefaultFocalLength;
        lightSlider.Value = 1; view.Brightness = 1;
        Preset(kind);
        SaveSetup();
    }

    /// <summary>The overlay at the slider's opacity, or hidden; the button is accent-coloured while it shows.</summary>
    void ApplyOverlay()
    {
        view.OverlayOpacity = overlayOn ? overlaySlider.Value : 0;
        bool on = overlayOn && overlaySlider.Value > 0.01f;
        overlayBtn.Text = on ? "Original Overlay: On" : "Original Overlay: Off";
        overlayBtn.Tag = on ? "accent" : "flat";
        overlayBtn.BackColor = on ? Ui.Accent : Ui.Bar; overlayBtn.ForeColor = on ? Color.White : Ui.Text;
        overlayBtn.FlatAppearance.BorderColor = on ? Ui.Accent : Ui.Line;
        overlayBtn.FlatAppearance.MouseOverBackColor = on ? Ui.AccentHover : Ui.CardHover;
        overlayBtn.Invalidate();
    }

    /// <summary>O toggles the original overlay anywhere in the creator, except while typing (the pose search).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.O && FindForm()?.ActiveControl is not TextBoxBase && !(ActiveControlDeep() is TextBoxBase)) { ToggleOverlay(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    Control? ActiveControlDeep()
    {
        Control? c = FindForm()?.ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c;
    }

    string KindName => kind switch { Kind.Store => "Store Image", Kind.Portrait => "Hero Portrait", Kind.Costume => "Costume Icon", _ => "Image" };

    /// <summary>
    /// Points the creator at a texture: its size, the game's original (for the overlay) and this icon's saved setup
    /// (under <paramref name="modKey"/>), else a starting one for its kind.
    /// </summary>
    public void SetTarget(string modKey, string tex, int w, int h, Image? originalImage)
    {
        if (texture.Length > 0 && CurrentSetup() is { } left) Remember(texture, left);
        texture = tex; targetW = Math.Max(1, w); targetH = Math.Max(1, h); kind = KindOf(tex);
        setupKey = modKey + "|" + tex;
        original?.Dispose(); original = originalImage; view.Overlay = original;
        title.Text = Ui.TitleCase($"Create {KindName}");
        hint.Text = $"{tex}: {w}×{h}, {(kind == Kind.Portrait ? "on the portrait backdrop" : "transparent background")}. Turn (left-drag), pan (right-drag) and zoom (wheel; hold Shift for fine steps) until it's framed, then Take Snapshot. The setup is kept for this icon.";
        snapshot?.Dispose(); snapshot = null; result.Image?.Dispose(); result.Image = null; resultInfo.Text = ""; useBtn.Enabled = saveBtn.Enabled = false;
        FitView();
        RefreshMeshes();
        Activated();
        Restore(PreviewViews.GetIcon(setupKey));
    }

    // The last icon worked on, across the editor's tabs (Kurt: recall its pose, frame, camera and light on another
    // icon, between herohor, store, costume …: every tab has its own creator, so this is shared).
    static PreviewViews.IconSetup? lastSetup;
    static string? lastTexture;
    PreviewViews.IconSetup? previous;
    string? previousTexture;
    Button previousBtn = null!;

    static void Remember(string tex, PreviewViews.IconSetup s) { lastSetup = s; lastTexture = tex; }

    /// <summary>The creator is shown (its tab opened): offer the last icon worked on anywhere, if it's another one.</summary>
    public void Activated()
    {
        if (lastSetup != null && lastTexture != null && !lastTexture.Equals(texture, StringComparison.OrdinalIgnoreCase)) { previous = lastSetup; previousTexture = lastTexture; }
        UpdatePreviousButton();
    }

    void UpdatePreviousButton()
    {
        bool can = previous != null && previousTexture != null && !previousTexture.Equals(texture, StringComparison.OrdinalIgnoreCase);
        previousBtn.Enabled = can;
        previousBtn.Text = can ? $"Use Previous Setup ({previousTexture})" : "Use Previous Setup";
        Ui.Tip(previousBtn, can ? $"Set this icon up like {previousTexture}, the one before: its character, props, pose and frame, camera, lens and light (this icon keeps its own background)."
                                : "Set this icon up like the one you had open before (switch from another icon first).");
    }

    /// <summary>Use Previous Setup: the previous icon's setup on this one (its own background kept), saved for this icon.</summary>
    void UsePrevious()
    {
        if (previous == null) return;
        var mine = PreviewViews.GetIcon(setupKey);
        var copy = new PreviewViews.IconSetup
        {
            Mesh = previous.Mesh, MeshFile = previous.MeshFile, Anim = previous.Anim, Frame = previous.Frame, View = previous.View,
            Light = previous.Light, Lens = previous.Lens, Props = previous.Props, Overlay = previous.Overlay,
            Back = mine?.Back ?? -1, BackColor = mine?.BackColor ?? 0, BackPicture = mine?.BackPicture,
        };
        PreviewViews.SetIcon(setupKey, copy);
        Restore(copy);
        status.Text = Ui.TitleCase($"Set Up Like \"{previousTexture}\"");
    }

    /// <summary>Shows an icon's setup (or the starting one for its kind): background, light, lens, character, props, pose, camera.</summary>
    void Restore(PreviewViews.IconSetup? setup)
    {
        restoring = true;
        backBox.SelectedIndex = setup?.Back is int bi and >= 0 and <= 3 ? bi : kind == Kind.Portrait && backdrop != null ? 1 : 0;
        if (setup != null) { if (setup.BackColor != 0) backColor = Color.FromArgb(setup.BackColor); customBackPath = setup.BackPicture; }
        lightSlider.Value = setup?.Light ?? 1;
        lensSlider.Value = setup?.Lens is float ln && ln > 0 ? ln : ModelView.DefaultFocalLength;
        restoring = false;
        BackgroundChanged(ask: false);
        int mi = setup?.Mesh is string mk ? meshes.FindIndex(m => m.Key.Equals(mk, StringComparison.OrdinalIgnoreCase)) : -1;
        if (mi < 0 && setup?.MeshFile is string mf && File.Exists(mf)) { AddMeshes([(Path.GetFileName(mf), mf)]); mi = meshes.FindIndex(m => m.Key.Equals(setup.Mesh, StringComparison.OrdinalIgnoreCase)); }
        if (mi < 0 && meshes.Count > 0) mi = 0;
        if (mi < 0) { view.ShowMessage("No character in this mod's packages: Open a .UPK."); loaded = null; loadedRef = null; return; }
        restoring = true; meshBox.SelectedIndex = mi; restoring = false;
        if (loadedRef != null && loadedRef.Key == meshes[mi].Key && loadedRef.File == meshes[mi].File) ApplySetup(setup);
        else LoadMesh(setup);
    }

    void RefreshMeshes()
    {
        var pk = packages().ToList();
        var keep = meshes.Where(m => !pk.Any(p => p.Path.Equals(m.File, StringComparison.OrdinalIgnoreCase))).ToList();   // opened .UPKs stay
        meshes.Clear(); meshBox.Items.Clear();
        AddMeshes(pk);
        foreach (var m in keep) if (!meshes.Any(x => x.File == m.File && x.Export == m.Export)) { meshes.Add(m); meshBox.Items.Add(Label(m)); }
    }

    static string Label(MeshRef m) => $"{m.Name}  ·  {m.Package.Replace(".upk", "", StringComparison.OrdinalIgnoreCase)}";

    void AddMeshes(IEnumerable<(string File, string Path)> pk)
    {
        foreach (var m in ModMeshes.List(pk))
        {
            if (meshes.Any(x => x.File.Equals(m.File, StringComparison.OrdinalIgnoreCase) && x.Export == m.Export)) continue;
            meshes.Add(m); meshBox.Items.Add(Label(m));
        }
    }

    void OpenUpk()
    {
        using var d = new OpenFileDialog { Title = "Open a Package With a Character", Filter = "Unreal packages (*.upk)|*.upk", InitialDirectory = cooked ?? "" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        int before = meshes.Count;
        AddMeshes([(Path.GetFileName(d.FileName), d.FileName)]);
        if (meshes.Count == before) { Dialog.Show(this, $"{Path.GetFileName(d.FileName)} has no character (skeletal mesh) that can be shown.", "Create from 3D"); return; }
        meshBox.SelectedIndex = before;
    }

    /// <summary>The view keeps the target's shape, as large as the space allows.</summary>
    void FitView()
    {
        var a = stage.ClientRectangle;
        if (a.Width < 20 || a.Height < 20) return;
        float k = Math.Min(a.Width / (float)targetW, a.Height / (float)targetH);
        int w = (int)(targetW * k), h = (int)(targetH * k);
        view.Bounds = new Rectangle(a.X + (a.Width - w) / 2, a.Y + (a.Height - h) / 2, w, h);
    }

    void BackgroundChanged(bool ask)
    {
        switch (backBox.SelectedIndex)
        {
            case 1:
                if (backdrop == null) { status.Text = "The Portrait Backdrop Is Missing (Assets\\herohor_bluebackdrop.png)"; restoring = true; backBox.SelectedIndex = 0; restoring = false; view.Backdrop = null; return; }
                view.Backdrop = backdrop; break;
            case 2:
                if (ask) using (var c = new ColorDialog { Color = backColor, FullOpen = true }) if (c.ShowDialog(this) == DialogResult.OK) backColor = c.Color;
                view.Backdrop = Solid(backColor); break;
            case 3:
                if (ask || customBack == null)
                {
                    string? path = customBackPath;
                    if (ask) using (var d = new OpenFileDialog { Title = "Background Picture", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" }) path = d.ShowDialog(this) == DialogResult.OK ? d.FileName : path;
                    if (path != null && File.Exists(path))
                        try { using var b = Image.FromFile(path); customBack?.Dispose(); customBack = new Bitmap(b); customBackPath = path; }
                        catch (Exception ex) when (ex is OutOfMemoryException or IOException) { Dialog.Show(this, $"{Path.GetFileName(path)} can't be read as a picture.", "Create from 3D"); }
                }
                if (customBack == null) { restoring = true; backBox.SelectedIndex = 0; restoring = false; view.Backdrop = null; return; }
                view.Backdrop = customBack; break;
            default: view.Backdrop = null; break;
        }
    }

    static Bitmap Solid(Color c) { var b = new Bitmap(4, 4); using var g = Graphics.FromImage(b); g.Clear(c); return b; }

    async void LoadMesh(PreviewViews.IconSetup? setup)
    {
        if (meshBox.SelectedIndex < 0) return;
        var r = meshes[meshBox.SelectedIndex];
        int req = ++request;
        status.Text = "Loading the Character…";
        view.ShowMessage("Loading…");
        var (l, why) = await Task.Run(() => { try { var x = ModMeshes.Load(r, cooked, out string w); return (x, w); } catch (Exception ex) { return ((ModMeshes.Loaded?)null, ex.Message); } });
        if (IsDisposed || req != request) return;
        if (l == null) { view.ShowMessage($"{r.Name} can't be shown ({why})."); status.Text = ""; loaded = null; loadedRef = null; return; }
        loaded = l; loadedRef = r;
        view.ShowMesh(l);
        view.Brightness = lightSlider.Value;
        animator = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences, l.Tangents);
        playing = null; anims = [];
        current = -1; shown = [];
        restoring = true; animBox.Items.Clear(); animBox.Items.Add("Rest Pose  ·  (looking for animations…)"); animBox.SelectedIndex = 0; restoring = false;
        frameSlider.Enabled = false;
        status.Text = "";
        SetLens(setup);
        if (setup?.View == null) Preset(kind); else { view.ViewState = setup.View; SyncTurn(); }
        FillProps(r, setup);
        await LoadPropsAsync(req);
        if (IsDisposed || req != request) return;
        var pkgs = meshes.Select(m => (m.Package, m.File)).Distinct().ToList();
        var found = await Task.Run(() => { try { return ModAnimations.For(r, l.Bones, pkgs, cooked); } catch { return []; } });
        if (IsDisposed || req != request) return;
        anims = found;
        restoring = true;
        restoring = false;
        int ai = setup?.Anim is string an ? anims.FindIndex(a => a.Name.Equals(an, StringComparison.OrdinalIgnoreCase)) : -1;
        current = ai;
        FillAnims();
        if (ai >= 0) LoadAnim(setup!.Frame);
        else SaveSetup();
    }

    /// <summary>
    /// The pose list: Rest Pose, then the animations whose names hold every word of the search. The pose shown stays
    /// selected (listed even when the search leaves it out, so the list never jumps to another pose by itself).
    /// </summary>
    void FillAnims()
    {
        var words = animSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        shown = [.. Enumerable.Range(0, anims.Count).Where(i => i == current || words.All(w => anims[i].Name.Contains(w, StringComparison.OrdinalIgnoreCase)))];
        restoring = true;
        animBox.BeginUpdate();
        animBox.Items.Clear();
        int matches = shown.Count(i => i != current || words.All(w => anims[i].Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        animBox.Items.Add(anims.Count == 0 ? "Rest Pose" : words.Length > 0 ? $"Rest Pose  ·  {matches} of {anims.Count} animations match" : $"Rest Pose  ·  {anims.Count} animations");
        foreach (int i in shown) animBox.Items.Add(anims[i].Name);
        animBox.SelectedIndex = current >= 0 && shown.IndexOf(current) is int k and >= 0 ? k + 1 : 0;
        animBox.EndUpdate();
        restoring = false;
    }

    /// <summary>The lens without moving the camera (the saved view's distance already belongs to it).</summary>
    void SetLens(PreviewViews.IconSetup? setup)
    {
        float mm = setup?.Lens is float ln && ln > 0 ? ln : ModelView.DefaultFocalLength;
        var keep = view.ViewState;
        view.FocalLength = mm;
        view.ViewState = keep; SyncTurn();
    }

    /// <summary>A mesh already shown: just this icon's animation, frame and camera.</summary>
    async void ApplySetup(PreviewViews.IconSetup? setup)
    {
        if (loadedRef != null) { FillProps(loadedRef, setup); await LoadPropsAsync(request); }
        SetLens(setup);
        if (setup?.View == null) Preset(kind); else { view.ViewState = setup.View; SyncTurn(); }
        int ai = setup?.Anim is string an ? anims.FindIndex(a => a.Name.Equals(an, StringComparison.OrdinalIgnoreCase)) : -1;
        current = ai;
        FillAnims();
        LoadAnim(setup?.Frame ?? 0);
    }

    async void LoadAnim(float frame)
    {
        if (animator == null || loaded == null) return;
        int i = current;
        if (i < 0 || i >= anims.Count) { playing = null; frameSlider.Enabled = false; PoseAll(); SaveSetup(); return; }
        int req = request;
        var a = anims[i];
        var ba = await Task.Run(() => ModAnimations.Load(a));
        if (IsDisposed || req != request || current != i || ba == null) return;
        playing = ba;
        playFrames = MeshAnimator.Span(ba).Frames;
        frameSlider.Max = Math.Max(1, playFrames);
        frameSlider.Format = v => $"{v:0} / {playFrames:0}";
        frameSlider.Enabled = true;
        restoring = true; frameSlider.Value = Math.Clamp(frame, 0, playFrames); restoring = false;
        Pose();
        SaveSetup();
    }

    void Pose()
    {
        if (animator == null || playing == null) return;
        PoseAll();
    }

    /// <summary>The character in its pose (the animation's frame, or rest) with each prop moved by the bone holding it.</summary>
    void PoseAll()
    {
        if (animator == null || loaded == null) return;
        animator.Pose(playing, playing != null ? frameSlider.Value : 0);
        rig.Update(view, animator);
    }

    /// <summary>
    /// The props to offer: the other meshes of the character's package(s) and the ones opened. Ticked: this icon's saved
    /// choice, else the ones the game attaches (a marvelattachment in the package names them).
    /// </summary>
    void FillProps(MeshRef main, PreviewViews.IconSetup? setup)
    {
        var attached = meshes.Select(m => m.File).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => ModMeshes.Attachments(f)).GroupBy(a => a.Mesh, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        restoring = true;
        propList.Items.Clear(); propChoices.Clear();
        foreach (var m in meshes.Where(m => m.Key != main.Key || m.File != main.File))
        {
            string? bone = attached.TryGetValue(m.Name, out var a) ? a.Bone : null;
            propChoices.Add((m, bone));
            bool on = setup?.Props != null ? setup.Props.Contains(m.Key, StringComparer.OrdinalIgnoreCase) : bone != null && m.File.Equals(main.File, StringComparison.OrdinalIgnoreCase);
            propList.Items.Add($"{m.Name}{(bone != null ? $"  ·  {bone}" : "")}", on);
        }
        restoring = false;
    }

    async void LoadProps(PreviewViews.IconSetup? setup) { await LoadPropsAsync(request); SaveSetup(); }

    /// <summary>Loads the ticked props and shows them with the character (the framing stays the character's).</summary>
    async Task LoadPropsAsync(int req)
    {
        if (loaded == null || animator == null) return;
        var want = propList.CheckedIndices.Cast<int>().Where(i => i < propChoices.Count).Select(i => propChoices[i]).ToList();
        props.Clear();
        foreach (var (r, bone) in want)
        {
            var m = await Task.Run(() => { try { return ModMeshes.Load(r, cooked, out _); } catch { return null; } });
            if (IsDisposed || req != request) return;
            if (m == null) continue;
            // The bone the game names, else the right hand (palm, hand or wrist, as the skeleton has it).
            int b = PropRig.BoneFor(animator, bone);
            props.Add((r, m, b, animator.BoneNames.ElementAt(b)));
        }
        rig.Clear();
        foreach (var (_, m, b, _) in props) rig.Add(m, b);
        var keep = view.ViewState;
        view.ShowMesh(rig.Combine(loaded), loaded.Positions.Length);
        view.ViewState = keep; SyncTurn();
        PoseAll();
    }

    /// <summary>This icon's setup, kept per mod and texture (preview_views.json).</summary>
    void SaveSetup()
    {
        if (restoring || setupKey.Length == 0 || loadedRef == null) return;
        var s = CurrentSetup()!;
        PreviewViews.SetIcon(setupKey, s);
        Remember(texture, s);
    }

    /// <summary>What's shown now, as an icon setup (null before a character is loaded).</summary>
    PreviewViews.IconSetup? CurrentSetup()
    {
        if (loadedRef == null) return null;
        bool opened = !packages().Any(p => p.Path.Equals(loadedRef.File, StringComparison.OrdinalIgnoreCase));
        return new PreviewViews.IconSetup
        {
            Mesh = loadedRef.Key, MeshFile = opened ? loadedRef.File : null,
            Anim = playing != null && current >= 0 && current < anims.Count ? anims[current].Name : null,
            Frame = playing != null ? frameSlider.Value : 0,
            View = view.ViewState, Light = lightSlider.Value, Lens = view.FocalLength,
            Back = backBox.SelectedIndex, BackColor = backColor.ToArgb(), BackPicture = customBackPath,
            Overlay = overlaySlider.Value,
            Props = [.. propList.CheckedIndices.Cast<int>().Where(i => i < propChoices.Count).Select(i => propChoices[i].Ref.Key)],
        };
    }

    /// <summary>
    /// Starting framings by the skeleton (wings, capes and weapons make the bounding box useless): the head bone, and the
    /// height from the feet (the lowest vertex) to it. The view shows 0.845 × distance vertically (field of view 0.8 rad).
    /// Without a head bone, by the bounding box. The camera sits on +X, where characters face, a little turned.
    /// </summary>
    void Preset(Kind k) { PresetCore(k); SyncTurn(); }

    void PresetCore(Kind k) => Framing.Apply(view, loaded != null ? animator : null, playing != null,
        k switch { Kind.Portrait => Framing.Shot.HeadShoulders, Kind.Costume => Framing.Shot.Bust, _ => Framing.Shot.Full });

    void Snap()
    {
        if (loaded == null) { status.Text = "Choose a Character First"; return; }
        int sw = SnapW, sh = SnapH;
        using var model = view.Snapshot(sw, sh);
        if (model == null) return;
        snapshot?.Dispose();
        snapshot = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(snapshot))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            if (view.Backdrop is { } b) g.DrawImage(b, new Rectangle(0, 0, sw, sh));
            g.DrawImage(model, 0, 0, sw, sh);
        }
        result.Image?.Dispose();
        result.Image = Enlarged(snapshot);
        resultInfo.Text = $"{sw}×{sh}  ·  {(view.Backdrop == null ? "transparent background" : "on its background")}";
        useBtn.Enabled = saveBtn.Enabled = true;
        status.Text = "Snapshot Taken";
    }

    /// <summary>The snapshot enlarged with sharp pixels (so a 40×40 icon can be judged) on a checkerboard where it's see-through.</summary>
    static Bitmap Enlarged(Bitmap b)
    {
        int k = Math.Max(1, Math.Min(8, 420 / Math.Max(b.Width, b.Height)));
        var big = new Bitmap(b.Width * k, b.Height * k);
        using var g = Graphics.FromImage(big);
        for (int y = 0; y < big.Height; y += 8)
            for (int x = 0; x < big.Width; x += 8)
                using (var br = new SolidBrush(((x + y) / 8 % 2 == 0) ? Color.FromArgb(52, 52, 58) : Color.FromArgb(38, 38, 44))) g.FillRectangle(br, x, y, 8, 8);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(b, new Rectangle(0, 0, big.Width, big.Height));
        return big;
    }

    void SavePng()
    {
        if (snapshot == null) return;
        using var d = new SaveFileDialog { Title = "Save Snapshot", Filter = "PNG image (*.png)|*.png", FileName = texture + ".png" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { snapshot.Save(d.FileName, ImageFormat.Png); status.Text = Ui.TitleCase($"Saved {Path.GetFileName(d.FileName)}"); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Dialog.Show(this, $"{Path.GetFileName(d.FileName)} can't be saved: {ex.Message}", "Create from 3D"); }
    }

    // --- large pictures (a user, 2026-10-03: a 1024 × 1024 store image for a promo shot): rendered from the view as framed
    // (the height is the viewfinder's; a square adds room at the sides), on the same background, never into the mod -------------
    void LargeMenu(Control button)
    {
        var m = new ContextMenuStrip();
        foreach (int size in new[] { 1024, 2048 })
        {
            int shapeW = (int)Math.Round(size * (double)SnapW / SnapH);
            m.Items.Add(new ToolStripMenuItem($"{size} × {size} (Square)", null, (_, _) => SaveLarge(size, size)));
            if (shapeW != size) m.Items.Add(new ToolStripMenuItem($"{shapeW} × {size} (Icon's Shape)", null, (_, _) => SaveLarge(shapeW, size)));
        }
        Ui.ShowUnder(m, button);
    }

    void SaveLarge(int w, int h)
    {
        if (loaded == null) { status.Text = "Choose a Character First"; return; }
        using var d = new SaveFileDialog { Title = "Save Large PNG", Filter = "PNG image (*.png)|*.png", FileName = $"{texture}_{w}x{h}.png" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        Cursor = Cursors.WaitCursor;
        try
        {
            using var big = Large(w, h);
            if (big == null) return;
            big.Save(d.FileName, ImageFormat.Png);
            status.Text = Ui.TitleCase($"Saved {Path.GetFileName(d.FileName)} ({w}×{h})");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException)
        { Dialog.Show(this, $"{Path.GetFileName(d.FileName)} can't be saved: {ex.Message}", "Create from 3D"); }
        finally { Cursor = Cursors.Default; }
    }

    /// <summary>The view at w × h (2× supersampled: smooth edges at this size), over the background scaled to cover it.</summary>
    internal Bitmap? Large(int w, int h)
    {
        using var model = view.Snapshot(w, h, supersample: 2);
        if (model == null) return null;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        if (view.Backdrop is { } b)
        {
            float k = Math.Max((float)w / b.Width, (float)h / b.Height);   // cover, centered (a square from the portrait backdrop)
            float bw = b.Width * k, bh = b.Height * k;
            g.DrawImage(b, (w - bw) / 2, (h - bh) / 2, bw, bh);
        }
        g.DrawImage(model, 0, 0, w, h);
        return bmp;
    }

    void UseIt()
    {
        if (snapshot == null) return;
        string dir = Path.Combine(Settings.Home, "converted");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, ModInstaller.Sanitise(texture) + "_3d.png");
        snapshot.Save(png, ImageFormat.Png);
        Use?.Invoke(texture, png, doubleSize);
        status.Text = "Used as the Replacement";
    }

    /// <summary>--icon-snapshot: waits for the character (and a pose, if named), takes the snapshot, saves it and the window.</summary>
    internal async Task<string> SelfTest(string dir, string? anim)
    {
        try { return await SelfTestRun(dir, anim); }
        catch (Exception ex) { return "FAIL self-test error: " + ex.GetType().Name + ": " + ex.Message; }
    }

    async Task<string> SelfTestRun(string dir, string? anim)
    {
        // wait for the character and its animation list (still "looking for animations" until it's read)
        bool Looking() => animBox.Items.Count > 0 && animBox.Items[0]?.ToString()?.Contains("looking", StringComparison.OrdinalIgnoreCase) == true;
        for (int t = 0; t < 20000 && (loaded == null || Looking()); t += 100) { await Task.Delay(100); Application.DoEvents(); }
        if (loaded == null) return "FAIL no character loaded: " + status.Text;
        if (anim != null)
        {
            int i = anims.FindIndex(a => a.Name.Contains(anim, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { current = i; FillAnims(); LoadAnim(0); for (int t = 0; t < 10000 && playing == null; t += 100) { await Task.Delay(100); Application.DoEvents(); } frameSlider.Value = playFrames * 0.4f; }
        }
        // The pose search: "idle" lists only idle animations (plus the pose shown), and clearing it lists them all again.
        animSearch.Text = "idle";
        int listed = animBox.Items.Count - 1;
        bool searchOk = shown.All(i => i == current || anims[i].Name.Contains("idle", StringComparison.OrdinalIgnoreCase)) && listed < anims.Count;
        animSearch.Text = "";
        searchOk &= animBox.Items.Count - 1 == anims.Count && (current < 0 || animBox.SelectedIndex == current + 1);
        string searchNote = $"; search \"idle\": {listed} of {anims.Count} listed{(searchOk ? "" : " (WRONG)")}";
        // The overlay toggle: O hides it, O shows it again at the slider's opacity; the button does the same.
        view.Focus();
        var km = new Message();
        ProcessCmdKey(ref km, Keys.O); bool hid = view.OverlayOpacity == 0 && overlayBtn.Text.EndsWith("Off");
        ProcessCmdKey(ref km, Keys.O); bool back = Math.Abs(view.OverlayOpacity - overlaySlider.Value) < 1e-4 && overlayBtn.Text.EndsWith("On");
        overlayBtn.PerformClick(); bool btn = view.OverlayOpacity == 0; overlayBtn.PerformClick();
        searchOk &= hid && back && btn;
        // The sliders by keyboard: → one frame, Shift+→ five, End the last.
        if (playing != null)
        {
            frameSlider.Value = 10;
            frameSlider.TestKey(Keys.Right); bool one = Math.Abs(frameSlider.Value - 11) < 1e-4;
            frameSlider.TestKey(Keys.Right | Keys.Shift); bool five = Math.Abs(frameSlider.Value - 16) < 1e-4;
            frameSlider.TestKey(Keys.End); bool end = Math.Abs(frameSlider.Value - frameSlider.Max) < 1e-4;
            frameSlider.Value = playFrames * 0.4f;
            searchOk &= one && five && end;
            searchNote += $"; slider keys (→ 1, Shift+→ 5, End): {(one && five && end ? "ok" : "WRONG")}";
        }
        searchNote += $"; overlay toggle (O key, button): {(hid && back && btn ? "ok" : "WRONG")}";
        await Task.Delay(300); Application.DoEvents();
        Snap();
        if (snapshot == null) return "FAIL no snapshot";
        try
        {
            Directory.CreateDirectory(dir);
            snapshot.Save(Path.Combine(dir, texture + ".png"), ImageFormat.Png);
            // the large pictures (Save Large PNG): a square and the icon's shape
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var sq = Large(1024, 1024)) sq?.Save(Path.Combine(dir, texture + "_1024x1024.png"), ImageFormat.Png);
            int shapeW = (int)Math.Round(1024.0 * SnapW / SnapH);
            using (var sh = Large(shapeW, 1024)) sh?.Save(Path.Combine(dir, $"{texture}_{shapeW}x1024.png"), ImageFormat.Png);
            searchNote += $"; large PNGs 1024² and {shapeW}×1024 in {sw.ElapsedMilliseconds} ms";
            var form = FindForm()!;
            using var b = new Bitmap(form.Width, form.Height); form.DrawToBitmap(b, new Rectangle(0, 0, form.Width, form.Height)); b.Save(Path.Combine(dir, texture + "_window.png"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or ArgumentException) { return "FAIL can't save to " + dir + ": " + ex.Message; }
        // Lens: 85 mm keeps the framed height (a dolly zoom), within a few percent.
        static int Tall(Bitmap b) { int lo = b.Height, hi = -1; for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) if (b.GetPixel(x, y).A > 128) { lo = Math.Min(lo, y); hi = Math.Max(hi, y); break; } return hi - lo; }
        string lensNote = "";
        if (view.Backdrop == null)
        {
            int before = Tall(snapshot);
            var keepView = view.ViewState; float keepLens = view.FocalLength;
            lensSlider.Value = 85;
            using (var tele = view.Snapshot(targetW, targetH)) { int after = tele == null ? 0 : Tall(tele); lensNote = $"; lens 50 → 85 mm: height {before} → {after} px"; if (tele != null) tele.Save(Path.Combine(dir, texture + "_85mm.png"), ImageFormat.Png); }
            lensSlider.Value = keepLens; view.ViewState = keepView; SyncTurn();
        }
        bool size = snapshot.Width == SnapW && snapshot.Height == SnapH;
        int opaque = 0, clear = 0;
        for (int y = 0; y < snapshot.Height; y++) for (int x = 0; x < snapshot.Width; x++) { int a = snapshot.GetPixel(x, y).A; if (a == 255) opaque++; else if (a == 0) clear++; }
        string snapInfo = $"{texture}: {snapshot.Width}x{snapshot.Height} ({kind}), {opaque} opaque / {clear} clear / {snapshot.Width * snapshot.Height - opaque - clear} soft-edge pixels, background {(view.Backdrop == null ? "transparent" : "backdrop")}";
        // Use Previous Setup: another icon (a different kind) set up like this one: pose, frame and camera carried over.
        string prevNote = "";
        if (playing != null)
        {
            var mine = CurrentSetup()!; string me = texture, modKey = setupKey[..setupKey.LastIndexOf('|')];
            string other = kind == Kind.Store ? "herohor_selftest_other" : "store_selftest_other";
            PreviewViews.ForgetIcon(modKey + "|" + other);
            SetTarget(modKey, other, kind == Kind.Store ? 180 : 300, kind == Kind.Store ? 136 : 420, null);
            for (int t = 0; t < 15000 && loaded == null; t += 100) { await Task.Delay(100); Application.DoEvents(); }
            bool offered = previousBtn.Enabled && previousBtn.Text.Contains(me);
            previousBtn.PerformClick();
            for (int t = 0; t < 15000 && (playing == null || current < 0 || anims[current].Name != mine.Anim); t += 100) { await Task.Delay(100); Application.DoEvents(); }
            var got = CurrentSetup();
            bool same = got != null && got.Anim == mine.Anim && Math.Abs(got.Frame - mine.Frame) < 1e-3 && got.View != null && mine.View != null && got.View.Zip(mine.View).All(p => Math.Abs(p.First - p.Second) < 1e-3);
            searchOk &= offered && same;
            prevNote = $"; Use Previous Setup ({me} → {other}): {(offered && same ? "ok" : $"WRONG (offered {offered}, same {same})")}";
        }

        return $"{(size && searchOk ? "ok  " : "FAIL")} {snapInfo}{lensNote}; props: {(props.Count == 0 ? "none" : string.Join(", ", props.Select(p => $"{p.Ref.Name} on {p.BoneName}")))}{searchNote}{prevNote}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { snapshot?.Dispose(); backdrop?.Dispose(); customBack?.Dispose(); original?.Dispose(); result.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The framings Create from 3D and the preview share (Kurt, 2026-09-30: the full / head / bust buttons in the 3D preview
/// too). By the skeleton (wings, capes and weapons make the bounding box useless): the head bone, and the height from the
/// feet (the lowest vertex) to it. Without a head bone, or for a model that doesn't stand (Jeff), by the bounding box. The
/// camera sits on +X, where characters face.
/// </summary>
static class Framing
{
    public enum Shot { Full, HeadShoulders, Bust }

    /// <param name="posed">An animation is showing (its pose is used); else the rest pose is made first.</param>
    /// <param name="centerHead">The 3D preview (a tall view): Head aims at the middle of the head itself (Kurt: the head should
    /// be centred); the icon maker's portraits (wide 180 x 136) keep the lower aim that leaves room for the shoulders.</param>
    public static void Apply(ModelView view, MeshAnimator? animator, bool posed, Shot k, bool centerHead = false)
    {
        const float yaw = 0f;   // facing the camera (was −0.25 rad, about 14°; a user: most want straight on)
        if (animator != null && HeadBone(animator) is int head)
        {
            if (!posed) animator.Pose(null, 0);
            var h = animator.BonePosition(head);
            float feet = animator.Positions.Min(p => p.Z), top = animator.Positions.Max(p => p.Z), tall = Math.Max(1f, h.Z - feet);
            // Only a standing character has its head near the top (a pet shark's "head" is low in its body: Jeff).
            float lens = view.FocalLength / ModelView.ReferenceFocalLength;   // a longer lens stands further back for the same framing
            if (tall >= 0.75f * (top - feet))
                switch (k)
                {
                    case Shot.HeadShoulders: view.Aim(h + new Vector3(0, 0, tall * (centerHead ? 0.05f : -0.1f)), tall * 0.5f * lens, yaw, 0.06f); return;
                    case Shot.Bust: view.Aim(h - new Vector3(0, 0, tall * 0.12f), tall * 0.58f * lens, yaw, 0.06f); return;
                    default: view.Aim(new Vector3(h.X, h.Y, feet + (top - feet) * 0.5f), (top - feet) * 1.42f * lens, yaw, 0.08f); return;
                }
        }
        view.ViewState = k switch
        {
            Shot.HeadShoulders => [yaw, 0.06f, 1.5f, 0f, 0f, 0.1f],   // not a standing character (a pet): the whole of it, a little closer
            Shot.Bust => [yaw, 0.06f, 1.6f, 0f, 0f, 0.1f],
            _ => [yaw, 0.1f, 2.3f, 0f, 0f, 0f],
        };
    }

    /// <summary>The head bone ("head", not "headwear" / "hair"), else null.</summary>
    static int? HeadBone(MeshAnimator animator)
    {
        int i = 0;
        foreach (string n in animator.BoneNames)
        {
            string l = n.ToLowerInvariant();
            if (l.Contains("head") && !l.Contains("wear") && !l.Contains("hair") && !l.Contains("nub") && !l.Contains("end")) return i;
            i++;
        }
        return null;
    }
}
