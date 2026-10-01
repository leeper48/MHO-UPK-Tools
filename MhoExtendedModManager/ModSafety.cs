namespace MhoExtendedModManager;

/// <summary>
/// File names a mod gives (manifest, sound packs) are the author's, not trusted (security audit, Kurt 2026-10-01): a name
/// such as "..\..\x.upk" or "C:\…" would point outside the mod or the game folder. Checked once where mods come in (the
/// library loading a mod, Install, a sound pack loading); Apply's writer checks its target folder again (Applier.Execute).
/// </summary>
static class ModSafety
{
    /// <summary>A file inside the mod's own folder: relative, no "..", no drive, no ':' (also NTFS streams), no invalid
    /// characters. Sub-folders are fine (Pictures\card.png).</summary>
    public static bool InModFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 260) return false;
        if (Path.IsPathRooted(name) || name.Contains(':') || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        foreach (string part in name.Split('/', '\\'))
            if (part.Length == 0 || part == "." || part == ".." || part.Trim() != part || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    /// <summary>A game file's name: a bare file name (no folders at all).</summary>
    public static bool GameFileName(string? name) => InModFolder(name) && name!.IndexOfAny(['/', '\\']) < 0;

    /// <summary>A language code (eng, deu, …): letters, digits, '_' and '-' only.</summary>
    public static bool LanguageCode(string? name) => !string.IsNullOrEmpty(name) && name.Length <= 16 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>Every unsafe file name in a manifest, described; empty when it's fine.</summary>
    public static List<string> Problems(ModManifest m)
    {
        var p = new List<string>();
        void Check(IEnumerable<string?> names, Func<string?, bool> ok, string what)
        {
            foreach (string? n in names) if (!ok(n)) p.Add($"{what} \"{n}\"");
        }
        Check(m.UpkReplacements, GameFileName, "package");
        Check(m.Extra.Select(r => r.Package), GameFileName, "icon package");
        Check(m.Replacements.Concat(m.AchievementReplacements).Concat(m.StoreReplacements).Select(r => r.DdsFileName), InModFolder, "image");
        Check(m.Extra.Select(r => r.DdsFileName), InModFolder, "image");
        Check(m.AudioPacks, InModFolder, "sound pack");
        Check(m.Languages, LanguageCode, "language");
        foreach (string? key in new[] { m.CardPicture, m.PreviewImage })
            if (key != null && key.StartsWith(ModPictures.Prefix, StringComparison.OrdinalIgnoreCase) && !InModFolder(key[ModPictures.Prefix.Length..]))
                p.Add($"picture \"{key}\"");
        return p;
    }
}
