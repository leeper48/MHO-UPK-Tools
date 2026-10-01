using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The middle column of the Mods page: the selected mod's picture, big, with a strip of the pictures it can show below
/// (Kurt: authors choose one, users pick their own, and it's remembered). The candidates are the mod's own store images,
/// hero portraits, costume and inventory icons and the game's originals (PreviewImages). Which one shows: the user's pick,
/// else the mod's choice, else automatic. A click on a thumbnail picks it (Picked, saved per mod on this PC); a right-click
/// offers going back to the mod's choice. Decoded in the background (stock images one at a time, Ui.StockLock).
/// </summary>
sealed partial class StorePreview : Control
{
    Mod? mod;
    List<PreviewCandidate> items = [];
    int index = -1;
    // The 3D view (Kurt): the mod's skeletal meshes (ModMeshes), shown in ModelView where the picture goes.
    List<MeshRef> meshes = [];
    bool show3D;
    int meshIndex;
    ModelView? viewer;
    readonly Dictionary<string, ModMeshes.Loaded?> meshCache = [];
    Rectangle meshPrev, meshNext;
    // Animation (Kurt: pick one for the 3D view): the mesh's animations, the one playing, and the drop-down.
    List<AnimRef> anims = [];
    string? wantedAnim;                           // from the pick's "@animation" part
    double? restoreTime;                          // the saved frame (seconds) to show when the animation being restored has loaded
    string? shownMesh;                            // the mesh the 3D view holds (kept while a picture shows, so going back keeps pose and camera)
    AnimExportCli.Animation.BoneAnimation? playing;
    MeshAnimator? animator;
    float playFrames, playSeconds;
    readonly System.Diagnostics.Stopwatch playClock = new();
    readonly System.Windows.Forms.Timer playTimer = new() { Interval = 33 };
    DropDown? animBox;
    bool fillingAnims;
    // Play / pause (an animation loads paused on its first frame), loop (remembered), reset view (the default camera).
    Button? playBtn, loopBtn, restBtn;
    // Power effects (Kurt, 2026-09-30; ported from the MHO Hero Creator): the effects of the power an animation belongs to,
    // played with it. The game data is read once (in the background); each power's effects when its animation is picked.
    Button? powersBtn;
    Fx.PowerEffects.Player? fxPlayer;
    string fxNote = "";                  // "Shockwave · 5 Effects" in the 3D caption
    Dictionary<string, (string Bone, System.Numerics.Matrix4x4 Local)> fxSockets = new();
    double fxTime;                       // where the effects are (seconds into the animation)
    int fxRequest;
    static Task<Fx.GameData?>? fxDb;
    readonly PropRig rig = new();
    ModMeshes.Loaded? shownLoaded;       // the character as loaded (without props)
    LightSlider? lightSlider, lensSlider, frameSlider;
    bool settingFrame;   // the frame slider follows playback without scrubbing
    // Full screen (Kurt, 2026-09-30): the whole preview moves into a borderless window covering the app's monitor, the 3D
    // view filling it with the controls in a column on the right; Esc, F11 or the button bring it back.
    Button? fullBtn;
    // Compact 3D controls (Kurt, 2026-09-30: room for more): playback in a bar over the view's bottom and framing in its top
    // right corner, both shown while the mouse is over the view (always in full screen); the look toggles and the Light /
    // Lens sliders in a Look ▾ menu; under the view only the caption and Look ▾ · Reset View · ⛶.
    Panel? playBar;
    // Power buttons (Kurt, 2026-09-30: like the MHO Hero Creator's 3D View): the hero's powers as icons in the strip under
    // the preview while the 3D view shows (the Power Icons button switches back to the pictures); a click filters the
    // animations to that power's and plays its first, with its effects; a second click shows them all again.
    List<Fx.PowerList.Power> heroPowers = [];
    List<AnimRef> allAnims = [];
    bool autoPlay, powersLoaded;
    string? powerFilter;
    readonly List<(Rectangle Rect, int Index)> powerRects = [];
    Rectangle powerLeft, powerRight;
    int powerScroll, hoverPower = -1, heroPowersRequest;
    Button? lookBtn;
    ContextMenuStrip? lookMenu;
    Rectangle viewRect;
    Button? frameFullBtn, frameHeadBtn, frameBustBtn;   // framings (Kurt: as in Create from 3D)
    Form? fullForm;
    Control? homeParent;
    int homeIndex;
    /// <summary>The preview is in its full-screen window.</summary>
    public bool IsFull => fullForm != null;
    bool paused = true;
    double playTime, lastTick;
    static string MeshPart(string key) { int at = key.IndexOf('@'); return at < 0 ? key : key[..at]; }
    static string? AnimPart(string? key) { int at = key?.IndexOf('@') ?? -1; return at < 0 ? null : key![(at + 1)..]; }
    bool MeshOk => meshIndex >= 0 && meshIndex < meshes.Count;
    string CurrentMeshKey() => !MeshOk ? "" : meshes[meshIndex].Key + (playing != null && animBox?.SelectedIndex > 0 && animBox.SelectedIndex - 1 < anims.Count ? "@" + anims[animBox.SelectedIndex - 1].Name : "");
    int Offset => meshes.Count > 0 ? 1 : 0;   // strip tile 0 is "3D" when the mod has meshes
    int Tiles => items.Count + Offset;
    /// <summary>The game's CookedPCConsole (textures streamed from the .tfc caches).</summary>
    public string? CookedFolder { get; set; }
    Image? image;
    int request;
    int scroll;   // the strip's scroll offset (pixels)
    readonly Dictionary<string, Image?> thumbs = new(StringComparer.OrdinalIgnoreCase);   // folder|key → thumbnail (null: loading / none)
    readonly List<(Rectangle Rect, int Index)> thumbRects = [];
    Rectangle leftArrow, rightArrow, strip;
    int hoverThumb = -1;
    readonly ToolTip tips;
    readonly Font titleFont = Ui.Bold(8.5f), smallFont = Ui.Regular(8.25f);
    /// <summary>Stock pictures (the game's originals); null: none.</summary>
    public StockCatalog? Catalog { get; set; }
    /// <summary>The user picked a picture for a mod (a key), or went back to the mod's choice (null).</summary>
    public event Action<Mod, string?>? Picked;
    /// <summary>A pick made by the window for this preview (a custom picture), handled like a click's.</summary>
    public void RaisePicked(Mod m, string? key) => Picked?.Invoke(m, key);
    /// <summary>Right-click → Custom Image (a user's request): the window asks for a file and picks it.</summary>
    public event Action<Mod>? CustomRequested;

    public StorePreview()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        tips = Ui.NewTips(() => null);
    }

    float S => DeviceDpi / 96f;
    int ThumbSize => (int)(56 * S);

    public Mod? Mod
    {
        get => mod;
        set
        {
            string stamp = value == null ? "" : FilesStamp(value);
            if (value != null && mod != null && value.FolderName == mod.FolderName && value.FilesMade == mod.FilesMade && stamp == modStamp && (items.Count > 0 || meshes.Count > 0))
            {
                // The same mod after a reload (a pick, a tag, undo …): keep the pictures and the 3D view (Kurt: its pose,
                // zoom and animation were lost when a picture was picked and then 3D again); only show the new choice.
                bool again = value.LocalPreview != mod.LocalPreview || value.Manifest.PreviewImage != mod.Manifest.PreviewImage;
                mod = value;
                if (again) Resolve();
                return;
            }
            SaveAnim();   // leaving this mod: its animation and frame are kept for next time
            mod = value; modStamp = stamp;
            items = []; meshes = []; index = -1; meshIndex = 0; scroll = 0; show3D = false; shownMesh = null;
            if (viewer != null) viewer.Visible = false;
            StopAnimation(); anims = []; animator = null; wantedAnim = null;
            HideAnimControls();
            var old = image; image = null; old?.Dispose();
            Invalidate();
            if (value == null) return;
            int req = ++request;
            var cat = Catalog; var m = value;
            Task.Run(() =>
            {
                List<PreviewCandidate> pics; List<MeshRef> ms;
                try { lock (Ui.StockLock) pics = PreviewImages.For(m, cat); } catch { pics = []; }
                try { ms = ModMeshes.List(m); } catch { ms = []; }
                return (pics, ms);
            }).ContinueWith(t =>
            {
                if (IsDisposed || req != request) return;
                (items, meshes) = t.Result;
                Resolve();
                LoadThumbs();
                Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    Image? Decode(PreviewCandidate c, int size)
    {
        try
        {
            if (c.FromMod) return c.File != null ? Ui.DdsThumb(c.File, size) : null;
            lock (Ui.StockLock) return Catalog?.Preview(c.Package, c.Texture) is { } p ? Ui.Thumb(p.Bgra, p.W, p.H, size) : null;
        }
        catch { return null; }
    }

    void LoadBig()
    {
        var old = image; image = null; old?.Dispose();
        Invalidate();
        if (index < 0 || index >= items.Count) return;
        int req = ++request;
        var c = items[index];
        Task.Run(() => Decode(c, 1024)).ContinueWith(t =>
        {
            if (IsDisposed || req != request) { t.Result?.Dispose(); return; }
            image = t.Result;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    string modStamp = "";

    /// <summary>The mod's files as they are (names, sizes, dates; not the manifest): an edit or update changes it even when
    /// the copied files keep their old write times, so the preview reloads its pictures and meshes.</summary>
    static string FilesStamp(Mod m)
    {
        try
        {
            return string.Join(";", Directory.EnumerateFiles(m.Folder, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => Path.GetFileName(f) + "|" + Ui.FileStamp(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>A strip thumbnail's cache key: the mod, the picture, and its file as it is now (a replaced icon gets a new one).</summary>
    static string ThumbKey(Mod m, PreviewCandidate c) => m.FolderName + "|" + c.Key + (c.FromMod && c.File != null ? "|" + Ui.FileStamp(c.File) : "");

    void LoadThumbs()
    {
        if (mod is not { } m || items.Count < 2) return;
        var todo = items.Where(c => !thumbs.ContainsKey(ThumbKey(m, c))).ToList();
        foreach (var c in todo) thumbs[ThumbKey(m, c)] = null;
        int size = ThumbSize * 2;
        Task.Run(() => todo.Select(c => (Key: ThumbKey(m, c), Img: Decode(c, size))).ToList()).ContinueWith(t =>
        {
            if (IsDisposed) { foreach (var x in t.Result) x.Img?.Dispose(); return; }
            foreach (var (k, img) in t.Result) thumbs[k] = img;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void ScrollIntoView()
    {
        int tile = show3D ? 0 : index + Offset;
        if (tile < 0 || strip.Width <= 0) return;
        int step = ThumbSize + (int)(6 * S), x = tile * step;
        if (x < scroll) scroll = x;
        else if (x + ThumbSize > scroll + strip.Width) scroll = x + ThumbSize - strip.Width;
    }

    int MaxScroll => Math.Max(0, Tiles * (ThumbSize + (int)(6 * S)) - (int)(6 * S) - strip.Width);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.PaintGradient(g, this, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = (int)(6 * S);
        var title = new Rectangle(pad, (int)(4 * S), Width - 2 * pad, (int)(20 * S));
        TextRenderer.DrawText(g, "PREVIEW", titleFont, title, Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Card at the store images' 300:420 aspect, as wide as the column allows; caption and strip below.
        bool full = IsFull && show3D;
        // The pictures strip is always there; the power block (Powers toggle + two rows of power buttons) sits at the bottom
        // (Kurt, 2026-09-30), kept while the hero's powers load and dropped when the hero has none.
        bool powerBlock = PowerStrip && show3D && HeroOfMesh() != null && (!powersLoaded || heroPowers.Count > 0);
        bool showStrip = Tiles > 1 && !full;
        int stripH = showStrip ? ThumbSize + (int)(12 * S) : 0;
        int pbh = animBox?.Height ?? (int)(26 * S), pgap = (int)(6 * S);
        int powersH = powerBlock && !full ? pbh + pgap + 2 * ThumbSize + pgap + (int)(4 * S) : 0;
        int captionH = (int)((show3D ? 80 : 40) * S);
        int panelW = (int)(320 * S);
        Rectangle card;
        if (full)
        {
            // Full screen: the 3D view fills everything left of a controls column.
            card = Rectangle.FromLTRB(pad, title.Bottom + (int)(4 * S), Width - panelW - 2 * pad, Height - pad);
            if (card.Width <= 0 || card.Height <= 0) return;
        }
        else
        {
            int w = Width - 2 * pad, h = (int)(w * 420f / 300f);
            int maxH = Height - title.Bottom - (int)(8 * S) - captionH - stripH - powersH;
            if (h > maxH && maxH > 0) { h = maxH; if (!show3D) w = (int)(h * 300f / 420f); }   // the 3D view uses the column's whole width; pictures keep their shape
            if (w <= 0 || h <= 0) return;
            card = new Rectangle((Width - w) / 2, title.Bottom + (int)(4 * S), w, h);
        }
        // Where the caption and the 3D controls go: under the card, or the column on the right when full screen.
        int ctlX = full ? card.Right + pad : card.X, ctlW = full ? panelW : card.Width;
        int fbs = (int)(33 * S);   // ⛶ and the three framing buttons: one square size (Kurt: ⛶ 50 % bigger)
        // The caption under the card leaves room on the right for the ⛶ button (Kurt: lower right, under the view).
        int capX = full ? ctlX : pad, capW = full ? panelW : Width - 2 * pad, capY = full ? card.Top : card.Bottom + (int)(4 * S);
        if (show3D && viewer != null)
        {
            // The 3D view fills the card; the caption steps through the meshes.
            var inner = Rectangle.Inflate(card, -(int)(2 * S), -(int)(2 * S));
            // The playback bar (frame slider, animation, ▶, Loop) is always shown, under the view inside the card (Kurt:
            // it kept disappearing as a hover overlay).
            if (animBox != null) inner.Height -= (int)(22 * S) + animBox.Height + 3 * (int)(4 * S);
            if (viewer.Bounds != inner) viewer.Bounds = inner;
            viewRect = inner;
            if (!viewer.Visible) viewer.Visible = true;
            using (var path = Ui.Round(card, 5 * S)) using (var fill = new SolidBrush(Ui.Card)) g.FillPath(fill, path);
        }
        else using (var path = Ui.Round(card, 5 * S))
        {
            using (var fill = new SolidBrush(Ui.Card)) g.FillPath(fill, path);
            if (image != null)
            {
                var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float k = Math.Min((float)card.Width / image.Width, (float)card.Height / image.Height);   // (pictures only; the 3D view is its own control)
                k = Math.Min(k, 3f * S);   // small images (40×40 costume icons) at most 3× their size (Kurt: filling the card was far too blocky)
                float iw = image.Width * k, ih = image.Height * k;
                g.DrawImage(image, card.X + (card.Width - iw) / 2, card.Y + (card.Height - ih) / 2, iw, ih);
                g.Clip = clip;
            }
            else
            {
                string text = mod == null ? "" : items.Count == 0 && index < 0 && request > 0 ? "No Picture\nfor This Mod" : "Loading…";
                TextRenderer.DrawText(g, text, smallFont, card, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            }
        }
        // Caption (3D): "◀ mesh ▶", then the package and whose choice it is.
        meshPrev = meshNext = Rectangle.Empty;
        if (mod != null && show3D && meshIndex >= 0 && meshIndex < meshes.Count)
        {
            var r = meshes[meshIndex];
            var cap = new Rectangle(capX, capY, capW, (int)(18 * S));
            if (meshes.Count > 1)
            {
                meshPrev = new Rectangle(cap.X, cap.Y, (int)(24 * S), cap.Height);
                meshNext = new Rectangle(cap.Right - (int)(24 * S), cap.Y, (int)(24 * S), cap.Height);
                TextRenderer.DrawText(g, "◀", smallFont, meshPrev, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "▶", smallFont, meshNext, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            var mid = Rectangle.FromLTRB(cap.X + (int)(26 * S), cap.Y, cap.Right - (int)(26 * S), cap.Bottom);
            TextRenderer.DrawText(g, meshes.Count > 1 ? $"{r.Name}  ({meshIndex + 1} of {meshes.Count})" : r.Name, smallFont, mid, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string whose = mod.LocalPreview != null && MeshPart(mod.LocalPreview) == r.Key ? "User Pick" : mod.Manifest.PreviewImage != null && MeshPart(mod.Manifest.PreviewImage) == r.Key ? "The Mod's Choice" : "3D View";
            cap.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, $"3D  ·  {r.Package.Replace(".upk", "", StringComparison.OrdinalIgnoreCase)}  ·  {whose}{(fxNote.Length > 0 ? "  ·  " + fxNote : "")}", smallFont, cap, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (animBox != null && playBtn != null && loopBtn != null && restBtn != null && playBar != null && lookBtn != null && fullBtn != null && frameSlider != null)
            {
                int bh = animBox.Height, gap = (int)(4 * S), wPlay = (int)(30 * S), wLoop = (int)(56 * S), wRest = (int)(80 * S), wLook = (int)(78 * S);
                // Under the caption: Look ▾ · Reset View, and ⛶ at the right.
                // One row: Look ▾ · Reset View on the left; Full Body / Head / Bust and ⛶ (square icon buttons) on the right.
                int y = cap.Bottom + (int)(4 * S), by = y + (fbs - bh) / 2;
                var lk = new Rectangle(ctlX, by, wLook, bh); if (lookBtn.Bounds != lk) lookBtn.Bounds = lk;
                var rb = new Rectangle(lk.Right + gap, by, wRest, bh); if (restBtn.Bounds != rb) restBtn.Bounds = rb;
                var fr = new Rectangle(ctlX + ctlW - fbs, y, fbs, fbs); if (fullBtn.Bounds != fr) fullBtn.Bounds = fr;
                string label = full ? "Back" : "Full Screen";
                if (fullBtn.Text != label) { fullBtn.Text = label; fullBtn.Invalidate(); }
                foreach (Control c in new Control[] { lookBtn, restBtn, fullBtn }) if (!c.Visible) c.Visible = true;
                if (powersBtn != null)
                {
                    // The Powers toggle heads the power block: the bottom of the panel, or under the Look row when full screen.
                    var pw2 = full ? new Rectangle(ctlX, fr.Bottom + (int)(10 * S), (int)(92 * S), bh) : new Rectangle(pad, Height - powersH, (int)(92 * S), bh);
                    if (powersBtn.Bounds != pw2) powersBtn.Bounds = pw2;
                    if (powersBtn.Visible != powerBlock) powersBtn.Visible = powerBlock;
                }
                // The playback bar under the view, always shown.
                int barH = (int)(22 * S) + bh + 3 * gap;
                var bar = new Rectangle(viewRect.X, viewRect.Bottom, viewRect.Width, barH);
                if (playBar.Bounds != bar) playBar.Bounds = bar;
                int iw = bar.Width - 2 * gap;
                var fsb = new Rectangle(gap, gap, iw, (int)(22 * S)); if (frameSlider.Bounds != fsb) frameSlider.Bounds = fsb;
                var ab = new Rectangle(gap, fsb.Bottom + gap, iw - wPlay - wLoop - 2 * gap, bh); if (animBox.Bounds != ab) animBox.Bounds = ab;
                var pb = new Rectangle(ab.Right + gap, ab.Y, wPlay, bh); if (playBtn.Bounds != pb) playBtn.Bounds = pb;
                var lb = new Rectangle(pb.Right + gap, ab.Y, wLoop, bh); if (loopBtn.Bounds != lb) loopBtn.Bounds = lb;
                if (!playBar.Visible) playBar.Visible = true;
                if (frameFullBtn != null && frameHeadBtn != null && frameBustBtn != null)
                {
                    // Left to right: Head, Bust, Full Body (Kurt).
                    var f3 = new Rectangle(fr.X - 2 * gap - fbs, y, fbs, fbs); if (frameFullBtn.Bounds != f3) frameFullBtn.Bounds = f3;
                    var f2 = new Rectangle(f3.X - gap - fbs, y, fbs, fbs); if (frameBustBtn.Bounds != f2) frameBustBtn.Bounds = f2;
                    var f1 = new Rectangle(f2.X - gap - fbs, y, fbs, fbs); if (frameHeadBtn.Bounds != f1) frameHeadBtn.Bounds = f1;
                    foreach (var b in new[] { frameFullBtn, frameHeadBtn, frameBustBtn }) if (!b.Visible) b.Visible = true;
                }
            }
        }
        // Caption: the texture, where it's from, and whose choice it is.
        else if (mod != null && index >= 0 && index < items.Count)
        {
            var c = items[index];
            var cap = new Rectangle(pad, card.Bottom + (int)(4 * S), Width - 2 * pad, (int)(18 * S));
            TextRenderer.DrawText(g, c.Texture, smallFont, cap, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string whose = mod.LocalPreview == c.Key ? "User Pick" : mod.Manifest.PreviewImage == c.Key ? "The Mod's Choice" : "Chosen Automatically";
            cap.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, $"{c.Source}  ·  {whose}", smallFont, cap, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        // The strip: thumbnails, with arrows when they don't all fit.
        thumbRects.Clear();
        leftArrow = rightArrow = strip = Rectangle.Empty;
        powerRects.Clear(); powerLeft = powerRight = Rectangle.Empty;
        if (mod != null && powerBlock && powersBtn != null)
        {
            var hb = powersBtn.Bounds;
            string head = !powersLoaded ? "Loading Powers…" : powerFilter != null && heroPowers.FirstOrDefault(p => p.Prototype == powerFilter) is { } fp ? fp.Name + " (Click Again for All)" : $"{heroPowers.Count} Powers: Click One to Play It";
            var ht = Rectangle.FromLTRB(hb.Right + pgap, hb.Top, full ? ctlX + ctlW : Width - pad, hb.Bottom);
            TextRenderer.DrawText(g, head, smallFont, ht, Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        if (mod != null && show3D && heroPowers.Count > 0 && powerBlock && powersBtn != null)
        {
            if (full)
            {
                // Full screen: every power as a grid in the column, under the Powers toggle.
                int gx = card.Right + pad, gy = powersBtn.Bottom + pgap, cell = ThumbSize, gp = pgap;
                int per = Math.Max(1, (panelW + gp) / (cell + gp));
                for (int k = 0; k < heroPowers.Count; k++)
                {
                    var r = new Rectangle(gx + k % per * (cell + gp), gy + k / per * (cell + gp), cell, cell);
                    if (r.Bottom > Height - pad) break;
                    DrawPower(g, r, k);
                }
            }
            else
            {
                // Two rows under the Powers toggle, scrolled sideways together when they don't fit.
                int ptop = powersBtn.Bottom + pgap, aw = (int)(16 * S), step = ThumbSize + pgap, areaH = 2 * ThumbSize + pgap;
                int cols = (heroPowers.Count + 1) / 2;
                var pstrip = new Rectangle(pad, ptop, Width - 2 * pad, areaH);
                int ptotal = cols * step - pgap;
                if (ptotal > pstrip.Width)
                {
                    powerLeft = new Rectangle(pad, ptop, aw, areaH);
                    powerRight = new Rectangle(Width - pad - aw, ptop, aw, areaH);
                    pstrip = new Rectangle(powerLeft.Right + (int)(4 * S), ptop, powerRight.Left - powerLeft.Right - (int)(8 * S), areaH);
                    powerScroll = Math.Clamp(powerScroll, 0, Math.Max(0, ptotal - pstrip.Width));
                    foreach (var (ar, left, on) in new[] { (powerLeft, true, powerScroll > 0), (powerRight, false, powerScroll < ptotal - pstrip.Width) })
                    {
                        using var ab = new SolidBrush(on ? Ui.Text : Color.FromArgb(70, 255, 255, 255));
                        float cx = ar.X + ar.Width / 2f, cy = ar.Y + ar.Height / 2f, aa = 5 * S;
                        g.FillPolygon(ab, left ? [new PointF(cx + aa / 2, cy - aa), new PointF(cx - aa / 2, cy), new PointF(cx + aa / 2, cy + aa)]
                                               : [new PointF(cx - aa / 2, cy - aa), new PointF(cx + aa / 2, cy), new PointF(cx - aa / 2, cy + aa)]);
                    }
                }
                else { powerScroll = 0; pstrip = new Rectangle(pad, ptop, Math.Max(0, ptotal), areaH); }
                var pclip = g.Clip;
                g.SetClip(pstrip);
                for (int k = 0; k < heroPowers.Count; k++)
                {
                    var r = new Rectangle(pstrip.X + k % cols * step - powerScroll, ptop + k / cols * step, ThumbSize, ThumbSize);
                    if (r.Right < pstrip.Left || r.Left > pstrip.Right) continue;
                    DrawPower(g, r, k);
                }
                g.Clip = pclip;
            }
        }
        if (!showStrip || mod == null) return;
        int top = card.Bottom + captionH - (int)(2 * S), arrowW = (int)(16 * S);
        strip = new Rectangle(pad, top, Width - 2 * pad, ThumbSize);
        int total = Tiles * (ThumbSize + (int)(6 * S)) - (int)(6 * S);
        if (total > strip.Width)
        {
            leftArrow = new Rectangle(pad, top, arrowW, ThumbSize);
            rightArrow = new Rectangle(Width - pad - arrowW, top, arrowW, ThumbSize);
            strip = new Rectangle(leftArrow.Right + (int)(4 * S), top, rightArrow.Left - leftArrow.Right - (int)(8 * S), ThumbSize);
            scroll = Math.Clamp(scroll, 0, MaxScroll);
            void Arrow(Rectangle r, bool left, bool on)
            {
                using var b = new SolidBrush(on ? Ui.Text : Color.FromArgb(70, 255, 255, 255));
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, a = 5 * S;
                g.FillPolygon(b, left ? [new PointF(cx + a / 2, cy - a), new PointF(cx - a / 2, cy), new PointF(cx + a / 2, cy + a)]
                                      : [new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a)]);
            }
            Arrow(leftArrow, true, scroll > 0); Arrow(rightArrow, false, scroll < MaxScroll);
        }
        else { scroll = 0; strip = new Rectangle((Width - total) / 2, top, total, ThumbSize); }
        var oldClip = g.Clip;
        g.SetClip(strip);
        for (int ti = 0; ti < Tiles; ti++)
        {
            var r = new Rectangle(strip.X + ti * (ThumbSize + (int)(6 * S)) - scroll, top, ThumbSize, ThumbSize);
            if (r.Right < strip.Left || r.Left > strip.Right) continue;
            thumbRects.Add((r, ti));
            if (ti < Offset)
            {
                // The 3D tile.
                using var p3 = Ui.Round(r, 4 * S);
                using (var fill = new SolidBrush(ti == hoverThumb ? Ui.CardHover : Ui.Card)) g.FillPath(fill, p3);
                TextRenderer.DrawText(g, "3D", Ui.Heavy(11f), r, show3D ? Ui.Text : Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using var pen3 = new Pen(show3D ? Ui.Accent : Ui.Line, show3D ? Math.Max(2f, 2f * S) : 1f);
                g.DrawPath(pen3, p3);
                continue;
            }
            int i = ti - Offset;
            using (var path = Ui.Round(r, 4 * S))
            {
                using (var fill = new SolidBrush(ti == hoverThumb ? Ui.CardHover : Ui.Card)) g.FillPath(fill, path);
                if (thumbs.GetValueOrDefault(ThumbKey(mod, items[i])) is Image t)
                {
                    var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float k = Math.Min((float)r.Width / t.Width, (float)r.Height / t.Height);
                    g.DrawImage(t, r.X + (r.Width - t.Width * k) / 2, r.Y + (r.Height - t.Height * k) / 2, t.Width * k, t.Height * k);
                    g.Clip = clip;
                }
                // The game's originals get a small "G" corner mark; the one showing gets an accent border.
                if (!items[i].FromMod)
                {
                    var mark = new Rectangle(r.Right - (int)(14 * S), r.Bottom - (int)(14 * S), (int)(12 * S), (int)(12 * S));
                    using var mb = new SolidBrush(Color.FromArgb(200, 20, 24, 32)); g.FillEllipse(mb, mark);
                    TextRenderer.DrawText(g, "G", Ui.Heavy(6.5f), mark, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                bool on = !show3D && i == index;
                using var pen = new Pen(on ? Ui.Accent : Ui.Line, on ? Math.Max(2f, 2f * S) : 1f);
                g.DrawPath(pen, path);
            }
        }
        g.Clip = oldClip;
    }

    /// <summary>The user's pick changed (saved by the window): show what's chosen now.</summary>
    public void ChoiceChanged()
    {
        if (mod == null) return;
        // A custom picture may be new, or a new file under the same name: read the pictures again.
        if (ModPictures.IsFile(mod.LocalPreview))
        { var m = mod; mod = null; Mod = m; return; }
        Resolve();
    }

    /// <summary>What shows: the user's pick, else the mod's choice (a picture or a mesh), else a picture chosen automatically.</summary>
    void Resolve()
    {
        if (mod == null) return;
        string? Offered(string? k) => k == null ? null : k.StartsWith("mesh:", StringComparison.OrdinalIgnoreCase)
            ? (meshes.Any(x => x.Key.Equals(MeshPart(k), StringComparison.OrdinalIgnoreCase)) ? k : null)
            : (items.Any(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase)) ? k : null);
        string? key = Offered(mod.LocalPreview) ?? Offered(mod.Manifest.PreviewImage) ?? PreviewImages.Automatic(items) ?? meshes.FirstOrDefault()?.Key;
        if (key != null && key.StartsWith("mesh:", StringComparison.OrdinalIgnoreCase))
        {
            int mi = meshes.FindIndex(x => x.Key.Equals(MeshPart(key), StringComparison.OrdinalIgnoreCase));
            bool changed = !show3D || mi != meshIndex;
            show3D = true; meshIndex = Math.Max(0, mi); index = -1;
            if (changed && !Reveal3D()) { wantedAnim = AnimPart(key); LoadMesh(); }
        }
        else
        {
            int i = items.FindIndex(x => x.Key == key);
            bool changed = show3D || i != index;
            show3D = false; index = i;
            meshIndex = 0;   // the 3D tile then opens the costume's own model, not one picked before (Kurt: Doctor Strange on Daredevil reopened Daredevil)
            Hide3D();
            if (changed) LoadBig();
        }
        ScrollIntoView();
        Invalidate();
    }

    int ThumbAt(Point p)
    {
        foreach (var (r, i) in thumbRects) if (r.Contains(p) && strip.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (mod == null) return;
        if (powerLeft.Contains(e.Location)) { powerScroll = Math.Max(0, powerScroll - (ThumbSize + (int)(6 * S)) * 3); Invalidate(); return; }
        if (powerRight.Contains(e.Location)) { powerScroll += (ThumbSize + (int)(6 * S)) * 3; Invalidate(); return; }
        if (e.Button == MouseButtons.Left && powerRects.FirstOrDefault(x => x.Rect.Contains(e.Location)) is { Rect.Width: > 0 } hit) { PowerClicked(hit.Index); return; }
        if (leftArrow.Contains(e.Location)) { scroll = Math.Max(0, scroll - (ThumbSize + (int)(6 * S)) * 3); Invalidate(); return; }
        if (rightArrow.Contains(e.Location)) { scroll = Math.Min(MaxScroll, scroll + (ThumbSize + (int)(6 * S)) * 3); Invalidate(); return; }
        if (e.Button == MouseButtons.Left && (meshPrev.Contains(e.Location) || meshNext.Contains(e.Location)) && meshes.Count > 1)
        {
            meshIndex = (meshIndex + (meshNext.Contains(e.Location) ? 1 : meshes.Count - 1)) % meshes.Count;
            wantedAnim = null;
            LoadMesh();
            Picked?.Invoke(mod, meshes[meshIndex].Key);
            return;
        }
        int ti = ThumbAt(e.Location);
        if (ti < 0) return;
        if (e.Button == MouseButtons.Left)
        {
            if (ti < Offset)
            {
                if (meshIndex < 0 || meshIndex >= meshes.Count) meshIndex = 0;   // 0.33.0 crash: an index left from a mod with more meshes
                if (show3D && mod.LocalPreview != null && MeshPart(mod.LocalPreview) == meshes[meshIndex].Key) return;
                show3D = true; index = -1;
                var old = image; image = null; old?.Dispose();
                if (!Reveal3D()) LoadMesh();
                Picked?.Invoke(mod, CurrentMeshKey());
                return;
            }
            int i = ti - Offset;
            if (!show3D && i == index && mod.LocalPreview == items[i].Key) return;
            show3D = false; index = i;
            Hide3D();
            LoadBig();
            Picked?.Invoke(mod, items[i].Key);
        }
        else if (e.Button == MouseButtons.Right)
        {
            var menu = new ContextMenuStrip { Font = Ui.Regular(9.5f) };
            var m = mod;
            menu.Items.Add("Custom Image", null, (_, _) => CustomRequested?.Invoke(m)).ToolTipText = "Show a picture of your own for this mod (a .PNG, .JPG or .DDS; kept on this PC, and Export can put it into the mod).";
            if (mod.LocalPreview != null) menu.Items.Add(mod.Manifest.PreviewImage != null ? "Use the Mod's Choice" : "Choose Automatically Again", null, (_, _) => Picked?.Invoke(m, null));
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            Ui.ShowAt(menu, PointToScreen(e.Location));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!powerLeft.IsEmpty && powerRects.Count > 0 && e.Y >= powerLeft.Top && e.Y <= powerLeft.Bottom) { powerScroll = Math.Max(0, powerScroll - Math.Sign(e.Delta) * (ThumbSize + (int)(6 * S))); Invalidate(); return; }
        if (strip.IsEmpty || MaxScroll == 0) return;
        scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * (ThumbSize + (int)(6 * S)), 0, MaxScroll);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int pk = powerRects.FirstOrDefault(x => x.Rect.Contains(e.Location)) is { Rect.Width: > 0 } ph ? ph.Index : -1;
        if (pk >= heroPowers.Count) pk = -1;   // 0.37.49 crash: areas from the last paint while the list reloads
        if (pk >= 0 || hoverPower >= 0)
        {
            Cursor = pk >= 0 || powerLeft.Contains(e.Location) || powerRight.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            if (pk != hoverPower)
            {
                hoverPower = pk; Invalidate();
                if (pk >= 0)
                {
                    var pw = heroPowers[pk];
                    tips.Show($"{pw.Name}\n{string.Join(", ", pw.Animations)}\n{(powerFilter == pw.Prototype ? "Click to show all animations again." : "Click to play it with its effects (the animation list shows only this power's).")}", this, e.X + (int)(14 * S), e.Y + (int)(20 * S), 8000);
                }
                else tips.Hide(this);
            }
            if (pk >= 0) return;
        }
        int i = ThumbAt(e.Location);
        Cursor = i >= 0 || leftArrow.Contains(e.Location) || rightArrow.Contains(e.Location) || meshPrev.Contains(e.Location) || meshNext.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (i == hoverThumb) return;
        hoverThumb = i; Invalidate();
        if (i >= 0 && i < Offset && mod != null)
            tips.Show($"3D view of the mod's meshes ({meshes.Count}).\nDrag to turn, wheel to zoom, double-click to frame; ◀ ▶ under it step through the meshes.\nClick to show it here (remembered for this mod).", this, e.X + (int)(14 * S), e.Y + (int)(20 * S), 8000);
        else if (i >= 0 && mod != null)
        {
            var c = items[i - Offset];
            string whose = mod.Manifest.PreviewImage == c.Key ? "\nThe mod's choice." : "";
            tips.Show($"{c.Texture}\n{c.Source}{whose}\nClick to show it here (remembered for this mod)." + (mod.LocalPreview != null ? "\nRight-click: back to the mod's choice." : ""), this, e.X + (int)(14 * S), e.Y + (int)(20 * S), 8000);
        }
        else tips.Hide(this);
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverThumb = -1; hoverPower = -1; tips.Hide(this); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { SaveAnim(); playTimer.Dispose(); image?.Dispose(); foreach (var t in thumbs.Values) t?.Dispose(); tips.Dispose(); viewer?.Dispose(); animBox?.Dispose(); playBtn?.Dispose(); loopBtn?.Dispose(); restBtn?.Dispose(); lightSlider?.Dispose(); lensSlider?.Dispose(); frameSlider?.Dispose(); }
        base.Dispose(disposing);
    }
}
