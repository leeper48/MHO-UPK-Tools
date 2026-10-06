using System.Drawing.Drawing2D;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// What's running in the background (Kurt, 2026-10-06: "a progress bar or pinwheel while things are processing"): each job
/// says what it is while it runs (<c>using var _ = Busy.Begin("Model: building …")</c>); the <see cref="Spinner"/>s turn while
/// any is running and name them in their tooltip. Thread-safe: jobs begin and end on any thread.
/// </summary>
static class Busy
{
    static readonly object gate = new();
    static readonly List<(int Id, string What)> jobs = [];
    static int next;

    /// <summary>A job started or ended (on the thread that did it).</summary>
    public static event Action? Changed;

    /// <summary>The jobs running now, oldest first.</summary>
    public static List<string> Now { get { lock (gate) return [.. jobs.Select(j => j.What)]; } }

    public static IDisposable Begin(string what)
    {
        int id;
        lock (gate) { id = ++next; jobs.Add((id, what)); }
        Changed?.Invoke();
        return new Job(id);
    }

    sealed class Job(int id) : IDisposable
    {
        int done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            lock (gate) jobs.RemoveAll(j => j.Id == id);
            Changed?.Invoke();
        }
    }
}

/// <summary>A small turning ring (accent color) while <see cref="Busy"/> has a job (only the ones <see cref="Shows"/> keeps, when
/// set); empty, taking the same room, when nothing runs. Its tooltip lists what's running.</summary>
sealed class Spinner : Control
{
    readonly System.Windows.Forms.Timer tick = new() { Interval = 40 };
    float angle;
    bool spinning;
    /// <summary>Which jobs this one shows (null: all).</summary>
    public Func<string, bool>? Shows { get; init; }
    /// <summary>The tooltip while nothing runs.</summary>
    public string IdleTip { get; init; } = "Nothing is running in the background.";

    public Spinner()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Size = new Size(22, 22);
        tick.Tick += (_, _) => { angle = (angle + 24) % 360; Invalidate(); };
        Busy.Changed += OnBusy;
        HandleCreated += (_, _) => Update_();
    }

    protected override void OnCreateControl() { base.OnCreateControl(); int s = (int)Math.Round(22 * DeviceDpi / 96f); Size = new Size(s, s); }

    void OnBusy()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(Update_); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    void Update_()
    {
        if (IsDisposed) return;
        var now = Busy.Now.Where(w => Shows?.Invoke(w) != false).ToList();
        bool on = now.Count > 0;
        if (on != spinning) { spinning = on; tick.Enabled = on; Invalidate(); }
        Ui.Tip(this, on ? "Working in the background:\n" + string.Join("\n", now.Distinct().Select(w => "• " + w)) : IdleTip);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!spinning) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Math.Max(2f, Width / 8f);
        var r = new RectangleF(w, w, Width - 2 * w - 1, Height - 2 * w - 1);
        using (var track = new Pen(Color.FromArgb(50, Ui.Accent), w)) g.DrawEllipse(track, r);
        using var pen = new Pen(Ui.Accent, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, r, angle, 100);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Busy.Changed -= OnBusy; tick.Dispose(); }
        base.Dispose(disposing);
    }
}
