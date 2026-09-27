namespace MhoPackageModifier;

/// <summary>
/// The app's own folders: settings under %APPDATA%\MhoPackageModifier, undo history under %LOCALAPPDATA%\MhoPackageModifier.
/// Until 2.50.0 the tool was called UpkMeshScan and used those names; the first run after the rename moves an existing
/// UpkMeshScan folder across, so settings and every undo step carry over (if it can't be moved, the old one is used).
/// </summary>
static class AppFolders
{
    public const string Name = "MhoPackageModifier";
    const string OldName = "UpkMeshScan";

    public static string Roaming => Folder(Environment.SpecialFolder.ApplicationData);
    public static string Local => Folder(Environment.SpecialFolder.LocalApplicationData);

    static string Folder(Environment.SpecialFolder where)
    {
        string root = Environment.GetFolderPath(where);
        string now = Path.Combine(root, Name), old = Path.Combine(root, OldName);
        if (!Directory.Exists(now) && Directory.Exists(old))
        {
            try { Directory.Move(old, now); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return old; }
        }
        return now;
    }
}
