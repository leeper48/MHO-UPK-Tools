using System.Diagnostics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The image editor the Materials tab opens a map in (Kurt, 2026-10-06: GIMP, Corel PHOTO-PAINT, Photoshop …): the one chosen in
/// Settings ▾ → Model → Choose Image Editor, else the first one found installed. Each save there comes back into the Model tab.
/// </summary>
static class ImageEditor
{
    /// <summary>The image editors found under Program Files (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita, Paint.NET),
    /// newest version first within a kind.</summary>
    public static List<string> Installed()
    {
        var found = new List<string>();
        void Add(IEnumerable<string> files) => found.AddRange(files.Where(File.Exists).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase));
        // Program Files, and the per-user installs (GIMP installs into AppData\Local\Programs; the profile's real path: an app
        // package's own LocalApplicationData can be redirected)
        string perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Programs");
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), perUser }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> Dirs(string pattern) { try { return Directory.GetDirectories(root, pattern); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; } }
            IEnumerable<string> Files(string dir, string pattern) { try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; } }
            // GIMP: bin\gimp-3.0.exe / gimp-2.10.exe (not the console or tool executables)
            Add(Dirs("GIMP*").SelectMany(d => Files(Path.Combine(d, "bin"), "gimp-*.exe")).Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^gimp-\d+\.\d+\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
            Add(Dirs("Adobe").SelectMany(a => Directory.GetDirectories(a, "Adobe Photoshop*")).Select(d => Path.Combine(d, "Photoshop.exe")));
            // Corel PHOTO-PAINT: Corel\CorelDRAW Graphics Suite[]\Programs64\CorelPP.exe
            Add(Dirs("Corel").SelectMany(c => { try { return Directory.GetFiles(c, "CorelPP.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }); } catch (IOException) { return []; } }));
            Add(Dirs("Affinity").SelectMany(a => Directory.GetDirectories(a)).SelectMany(d => Files(d, "Photo*.exe")));
            Add(Dirs("Krita*").Select(d => Path.Combine(d, "bin", "krita.exe")));
            Add(Dirs("paint.net").Select(d => Path.Combine(d, "paintdotnet.exe")));
        }
        // GIMP first, then Photoshop, PHOTO-PAINT, Affinity, Krita, Paint.NET (each kind newest first, as found)
        static int Rank(string f) { string n = Path.GetFileName(f).ToLowerInvariant(); return n.StartsWith("gimp") ? 0 : n == "photoshop.exe" ? 1 : n == "corelpp.exe" ? 2 : n.StartsWith("photo") ? 3 : n == "krita.exe" ? 4 : 5; }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Select((f, i) => (f, i)).OrderBy(x => Rank(x.f)).ThenBy(x => x.i).Select(x => x.f).ToList();
    }

    /// <summary>The editor to use: the chosen one, else the first found; null when there's none.</summary>
    public static string? Find() => Environment.GetEnvironmentVariable("MHO_TEST_EDITOR") == "1" ? Environment.ProcessPath : Settings.Current.ImageEditorPath is string set && File.Exists(set) ? set : Installed().FirstOrDefault();

    /// <summary>A program's name as it calls itself ("GIMP 3.0", "Adobe Photoshop 2025", "Corel PHOTO-PAINT").</summary>
    public static string Describe(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            string file = Path.GetFileName(exe).ToLowerInvariant();
            // the known ones by their usual names (their product names are generic: "Corel Graphics Applications", "GNU Image Manipulation Program")
            if (file == "corelpp.exe") return "Corel PHOTO-PAINT" + (v.ProductMajorPart > 0 ? $" {v.ProductMajorPart}" : "");
            if (file.StartsWith("gimp-")) return "GIMP " + Path.GetFileNameWithoutExtension(exe)["gimp-".Length..];
            if (file == "krita.exe") return "Krita";
            if (file == "paintdotnet.exe") return "Paint.NET";
            string name = (v.ProductName is { Length: > 0 } pn ? pn : v.FileDescription is { Length: > 0 } fd ? fd : Path.GetFileNameWithoutExtension(exe)).Trim();
            if (name.Equals("GIMP", StringComparison.OrdinalIgnoreCase) && v.ProductMajorPart > 0) name += $" {v.ProductMajorPart}.{v.ProductMinorPart}";
            return name;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException) { return Path.GetFileNameWithoutExtension(exe); }
    }

    /// <summary>Opens <paramref name="file"/> in the editor. Null = started; else why not.</summary>
    public static string? Open(string exe, string file)
    {
        if (Environment.GetEnvironmentVariable("MHO_TEST_EDITOR") == "1") return null;   // tests: nothing opens on screen
        try { Process.Start(new ProcessStartInfo(exe, $"\"{file}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file)! }); return null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ex.Message; }
    }
}
