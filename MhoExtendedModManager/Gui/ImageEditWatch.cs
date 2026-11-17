using MhoExtendedModManager.Model;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// A picture open in the user's image editor (Settings ▾ → Model → Choose Image Editor): each save there over the same
/// file calls <c>changed</c> on the owner's thread (after 600 ms of quiet). Used by the Editor's showcase image.
/// </summary>
sealed class ImageEditWatch : IDisposable
{
    readonly FileSystemWatcher watcher;
    readonly System.Windows.Forms.Timer delay = new() { Interval = 600 };
    readonly string file;

    ImageEditWatch(Control owner, string file, Action changed)
    {
        this.file = file;
        delay.Tick += (_, _) => { delay.Stop(); if (!owner.IsDisposed) changed(); };
        watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, "*.*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
        void Changed(object? _, FileSystemEventArgs e)
        {
            if (!string.Equals(e.FullPath, this.file, StringComparison.OrdinalIgnoreCase)) return;
            try { owner.BeginInvoke(() => { delay.Stop(); delay.Start(); }); } catch (InvalidOperationException) { }
        }
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Renamed += (o, e) => Changed(o, e);
    }

    /// <summary>Opens <paramref name="file"/> in the editor and watches it. Null when there's no editor or it didn't start
    /// (<paramref name="note"/> says why, or how to save when it did).</summary>
    public static ImageEditWatch? Start(Control owner, string file, Action changed, out string note)
    {
        string? exe = ImageEditor.Find();
        if (exe == null) { note = "No image editor was found (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita or Paint.NET). Pick yours: Settings ▾ → Model → Choose Image Editor."; return null; }
        var w = new ImageEditWatch(owner, file, changed);
        string name = ImageEditor.Describe(exe);
        if (ImageEditor.Open(exe, file) is string failed) { w.Dispose(); note = $"{name} didn't start: {failed}"; return null; }
        note = $"Editing in {name}: save there over the same file{(name.StartsWith("GIMP", StringComparison.OrdinalIgnoreCase) ? " (GIMP: Ctrl+E)" : "")} and it comes back here.";
        return w;
    }

    public void Dispose() { watcher.Dispose(); delay.Dispose(); }
}
