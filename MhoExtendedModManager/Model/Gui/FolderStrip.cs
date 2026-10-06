using System.Diagnostics;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Model tab's folders (Kurt, 2026-10-04: tidied, the MFF path was clipped): a small caption beside each path (MFF,
/// GAME, STOCK), each path in its real letter case, shortened in the middle when there's no room (the full one in the
/// tooltip); a click opens the folder.
/// </summary>
sealed class FolderStrip : Control
{
    readonly List<(string Caption, string? Path, string Missing)> items = [];
    readonly List<(Rectangle Hit, string Path)> hits = [];
    int hot = -1;
    readonly Font captionFont = Ui.Bold(7.5f), pathFont = Ui.Regular(9f);

    public FolderStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = (int)(24 * MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
    }

    public void SetFolders(params (string Caption, string? Path, string Missing)[] folders)
    {
        items.Clear();
        items.AddRange(folders.Select(f => (f.Caption, f.Path != null ? MhoExtendedModManager.Settings.TrueCase(f.Path) : null, f.Missing)));   // (real letter case, read once)
        Invalidate();
    }

    /// <summary>Test: the strip's text as shown (captions and paths).</summary>
    internal string TextForTest => string.Join("  ·  ", items.Select(i => $"{i.Caption}: {i.Path ?? i.Missing}"));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        hits.Clear();
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        int gap = (int)(18 * s), pad = (int)(6 * s), x = 0;
        // each path gets an equal share of what the captions leave
        int capsW = items.Sum(i => TextRenderer.MeasureText(g, i.Caption, captionFont, Size.Empty, TextFormatFlags.NoPadding).Width + pad) + gap * Math.Max(0, items.Count - 1);
        int share = Math.Max((int)(60 * s), (Width - capsW) / Math.Max(1, items.Count));
        for (int k = 0; k < items.Count; k++)
        {
            var (caption, path, missing) = items[k];
            var cs = TextRenderer.MeasureText(g, caption, captionFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, caption, captionFont, new Rectangle(x, 0, cs.Width + 1, Height), Ui.Subtle, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            x += cs.Width + pad;
            string shown = path ?? missing;
            int want = TextRenderer.MeasureText(g, shown, pathFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            int w = Math.Min(want + 2, share);
            var r = new Rectangle(x, 0, w, Height);
            var color = path == null ? Ui.OverrideAmber : k == hot ? Ui.AccentHover : Ui.Text;
            TextRenderer.DrawText(g, shown, pathFont, r, color, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            if (path != null) hits.Add((r, path));
            x += w + gap;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int now = -1; string? tip = null;
        for (int k = 0, h = 0; k < items.Count; k++)
        {
            if (items[k].Path == null) continue;
            if (hits.Count > h && hits[h].Hit.Contains(e.Location)) { now = k; tip = hits[h].Path; }
            h++;
        }
        Cursor = now >= 0 ? Cursors.Hand : Cursors.Default;
        if (now != hot)
        {
            hot = now; Invalidate();
            Ui.Tip(this, tip != null ? $"{tip}\nClick to open it in Explorer. The folders are set in Settings > Model." : "The folders the Model tab reads: the MFF models, the game, and a clean copy of the game's packages (Stock). Set in Settings > Model.");
        }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (hit, path) in hits)
            if (hit.Contains(e.Location) && Directory.Exists(path)) { Process.Start("explorer.exe", $"\"{path}\""); return; }
    }
}
