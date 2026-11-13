namespace MhoExtendedModManager;

/// <summary>
/// Small state files written so a crash or power cut can't leave them broken (a user, 2026-11-13: the PC reset during
/// modding and state.json came back full of zero bytes; the app then crashed at every start). The text goes to a temp
/// file that is flushed to the disk itself (not just the Windows cache) before it's swapped in; the file before it is
/// kept as &lt;name&gt;.bak. Without the flush, NTFS can record the swap but not the data, which is exactly a zero-filled file.
/// </summary>
static class SafeFile
{
    public static void WriteAllText(string path, string text, bool keepBak = true)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        if (keepBak && File.Exists(path) && LooksLikeText(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path, overwrite: true);
    }

    /// <summary>False for an empty file or one starting with a zero byte (a cut-off write): never kept as the .bak.</summary>
    public static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length <= 2) return false;
            int b = fs.ReadByte();
            return b > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Moves a broken file aside as &lt;name&gt;.damaged-&lt;time&gt; (kept for a look, never read again).</summary>
    public static string? SetAside(string path)
    {
        try
        {
            string to = $"{path}.damaged-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            File.Move(path, to);
            return to;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
