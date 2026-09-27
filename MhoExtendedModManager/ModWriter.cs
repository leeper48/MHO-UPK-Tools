using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MhoExtendedModManager;

/// <summary>
/// A mod being made or edited: its info and the files it brings (each from where it is now: anywhere on disk, or the
/// mod's own folder when editing).
/// </summary>
sealed class ModDraft
{
    public string Name = "", Author = "", Version = "1.0";
    public List<(string File, string Source)> Packages = [];
    /// <summary>Per icon package (Applier.IconPackages order): texture name → .dds source.</summary>
    public List<(string Texture, string Source)>[] Textures = [[], [], []];
    public List<StringReplacement> Strings = [];
    public List<string> SoundPacks = [];
    /// <summary>Extension: textures in other icon packages (package file, texture, .dds source).</summary>
    public List<(string Package, string Texture, string Source)> Extra = [];
    /// <summary>Extension: the mod's own tags and note (they travel with the mod).</summary>
    public List<string> Tags = [];
    public string Notes = "";

    public static ModDraft From(Mod m)
    {
        var d = new ModDraft { Name = m.Name, Author = m.Manifest.Author ?? "", Version = m.Manifest.Version ?? "" };
        d.Packages = m.Manifest.UpkReplacements.Select(f => (f, Path.Combine(m.Folder, f))).ToList();
        for (int k = 0; k < Applier.IconPackages.Length; k++)
            d.Textures[k] = Applier.IconPackages[k].List(m.Manifest).Select(r => (r.TextureName, Path.Combine(m.Folder, r.DdsFileName))).ToList();
        d.Strings = [.. m.Strings];
        d.SoundPacks = m.Manifest.AudioPacks.Select(f => Path.Combine(m.Folder, f)).ToList();
        d.Extra = m.Manifest.Extra.Select(r => (r.Package, r.TextureName, Path.Combine(m.Folder, r.DdsFileName))).ToList();
        d.Tags = [.. m.ModTags];
        d.Notes = m.Manifest.Notes ?? "";
        return d;
    }

    public int TextureCount => Textures.Sum(t => t.Count) + Extra.Count;

    /// <summary>What's wrong before saving (empty = fine).</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) p.Add("The mod needs a name.");
        if (Packages.Count + TextureCount + Strings.Count + SoundPacks.Count == 0) p.Add("Add at least one change (a package, texture, string or sound pack).");
        foreach (var g in Packages.GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) p.Add($"{g.Key} is added twice.");
        for (int k = 0; k < Textures.Length; k++)
            foreach (var g in Textures[k].GroupBy(x => x.Texture, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) p.Add($"{g.Key} is replaced twice.");
        foreach (var g in Strings.GroupBy(s => (s.Language, s.File.ToLowerInvariant(), s.Id)).Where(g => g.Count() > 1)) p.Add($"String {g.Key.Id} is changed twice.");
        foreach (var g in Extra.GroupBy(x => (x.Package.ToLowerInvariant(), x.Texture.ToLowerInvariant())).Where(g => g.Count() > 1)) p.Add($"{g.Key.Item2} ({g.Key.Item1}) is replaced twice.");
        foreach (string f in Packages.Select(x => x.Source).Concat(Textures.SelectMany(t => t.Select(x => x.Source))).Concat(Extra.Select(x => x.Source)).Concat(SoundPacks))
            if (!File.Exists(f)) p.Add($"File not found: {f}");
        return p;
    }
}

/// <summary>
/// Writes a draft as a mod folder in MHModManager's format (manifest.json, &lt;lang&gt;.json, the files), new or in place of
/// an existing mod: built in a temporary folder, then swapped in; the replaced folder goes to the Recycle Bin.
/// </summary>
static class ModWriter
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <returns>The mod's folder name, or null with the reason.</returns>
    public static string? Save(ModLibrary lib, ModDraft d, Mod? editing, out string? error)
    {
        error = null;
        var problems = d.Problems();
        if (problems.Count > 0) { error = string.Join("\n", problems); return null; }
        // Editing without renaming keeps the mod's folder (its name can differ from the mod's, e.g. "Bucky …_3").
        string name = editing != null && d.Name.Trim() == editing.Name ? editing.FolderName : ModInstaller.Sanitise(d.Name.Trim());
        string mods = Path.Combine(lib.DataFolder, "mods"), target = Path.Combine(mods, name);
        if (Directory.Exists(target) && (editing == null || !Path.GetFullPath(target).Equals(Path.GetFullPath(editing.Folder), StringComparison.OrdinalIgnoreCase)))
        { error = $"A mod named '{name}' already exists."; return null; }

        string temp = Path.Combine(mods, $"{name}.saving-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(temp);
        try
        {
            var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // file name in the mod -> source
            string Place(string source, string? wanted = null)
            {
                string file = wanted ?? Path.GetFileName(source);
                string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
                for (int n = 2; used.TryGetValue(file, out var other) && !SameFile(other, source); n++) file = $"{stem}_{n}{ext}";
                if (!used.ContainsKey(file)) { used[file] = source; File.Copy(source, Path.Combine(temp, file)); }
                return file;
            }

            // Empty author / version are left out, as in MHModManager's manifests.
            var manifest = new ModManifest { Name = d.Name.Trim(), Author = NullIfEmpty(d.Author), Version = NullIfEmpty(d.Version) };
            var tags = d.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            manifest.Tags = tags.Count > 0 ? tags : null;
            manifest.Notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim().Replace("\r\n", "\n");
            foreach (var (file, source) in d.Packages) manifest.UpkReplacements.Add(Place(source, file));
            var lists = new[] { manifest.Replacements, manifest.AchievementReplacements, manifest.StoreReplacements };
            for (int k = 0; k < lists.Length; k++)
                foreach (var (tex, source) in d.Textures[k]) lists[k].Add(new TextureReplacement { TextureName = tex, DdsFileName = Place(source) });
            foreach (string s in d.SoundPacks) manifest.AudioPacks.Add(Place(s));
            // Extension: only written when used, so mods without it keep MHModManager's exact manifest.
            if (d.Extra.Count > 0)
                manifest.ExtraIconReplacements = d.Extra.Select(x => new ExtraIconReplacement { Package = x.Package, TextureName = x.Texture, DdsFileName = Place(x.Source) }).ToList();

            // Strings: one <lang>.json per language, grouped by file, as MHModManager writes them.
            // In the order they were added (MHModManager keeps insertion order; re-saving an unchanged mod gives the same file).
            foreach (var byLang in d.Strings.GroupBy(s => s.Language))
            {
                var root = new JsonObject();
                foreach (var byFile in byLang.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase))
                {
                    var o = new JsonObject();
                    foreach (var s in byFile)
                        o[s.Id.ToString()] = new JsonObject
                        {
                            ["Variants"] = new JsonArray((s.Variants ?? []).Select(v => (JsonNode)new JsonObject { ["FlagsConsumed"] = v.FlagsConsumed, ["FlagsProduced"] = v.FlagsProduced, ["String"] = v.Text }).ToArray()),
                            ["FlagsProduced"] = s.FlagsProduced,
                            ["String"] = s.Text,
                        };
                    root[byFile.Key] = o;
                }
                File.WriteAllText(Path.Combine(temp, byLang.Key + ".json"), root.ToJsonString(Json));
                manifest.Languages.Add(byLang.Key);
            }

            manifest.HasUpkReplacements = manifest.UpkReplacements.Count > 0;
            manifest.HasTextures = lists.Any(l => l.Count > 0);
            manifest.HasStrings = d.Strings.Count > 0;
            manifest.HasAudio = manifest.AudioPacks.Count > 0;
            manifest.TextureReplacementCount = lists.Sum(l => l.Count);
            var kinds = new List<ModType>();
            if (manifest.HasTextures || d.Extra.Count > 0) kinds.Add(ModType.Texture);
            if (manifest.HasStrings) kinds.Add(ModType.String);
            if (manifest.HasUpkReplacements) kinds.Add(ModType.Upk);
            if (manifest.HasAudio) kinds.Add(ModType.Audio);
            manifest.Type = kinds.Count == 1 ? kinds[0] : ModType.Mixed;
            File.WriteAllText(Path.Combine(temp, "manifest.json"), JsonSerializer.Serialize(manifest, ModManifest.Json));

            // Check it reads back as the same mod before swapping it in.
            var probe = new Mod { Folder = temp, FolderName = name, Manifest = ModManifest.Load(Path.Combine(temp, "manifest.json")) };
            var missing = probe.MissingFiles().ToList();
            if (missing.Count > 0) throw new IOException("files missing after writing: " + string.Join(", ", missing));

            // Swap in: the old folder (when editing) goes to the Recycle Bin.
            string? old = editing?.Folder;
            if (old != null && Directory.Exists(old))
            {
                string aside = old + ".replaced-" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(old, aside);
                Directory.Move(temp, target);
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(aside, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else Directory.Move(temp, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            error = ex.Message;
            return null;
        }

        // Order and enabled state: a new mod goes at the top, disabled; an edited one keeps its place (renamed if needed).
        var st = lib.State;
        if (editing == null) st.ModOrder = [name, .. lib.Mods.OrderBy(m => m.Priority).Select(m => m.FolderName)];
        else
        {
            st.ModOrder = lib.Mods.OrderBy(m => m.Priority).Select(m => m == editing ? name : m.FolderName).ToList();
            st.EnabledMods = lib.Mods.Where(m => m.Enabled).OrderBy(m => m.Priority).Select(m => m == editing ? name : m.FolderName).ToList();
            if (!name.Equals(editing.FolderName, StringComparison.OrdinalIgnoreCase)) st.RenameMod(editing.FolderName, name);   // lock and tags follow a rename
        }
        st.ApplyLocks();   // locked mods keep their place at the top / bottom
        File.WriteAllText(Path.Combine(lib.DataFolder, "state.json"), JsonSerializer.Serialize(st, ModManifest.Json));
        return name;
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static bool SameFile(string a, string b) =>
        Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase) ||
        (new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)));
}
