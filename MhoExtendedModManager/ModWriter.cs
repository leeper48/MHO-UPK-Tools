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
    /// <summary>Extension: description, this version's changes, and the earlier versions' changelog (newest first).</summary>
    public string Description = "";
    public string Changes = "";
    public List<ChangelogEntry> Changelog = [];
    /// <summary>The mod's saved post (Kurt: kept in the mod, written out next to an exported zip): its Nexus and Discord
    /// text (null = not saved, made fresh) and its images (source files), stored in the mod folder's Post\ subfolder.</summary>
    public string? PostNexus, PostDiscord;
    /// <summary>Extension: the mod's Nexus page (mod ID), set by its author.</summary>
    public int? NexusModId;
    /// <summary>Extension: the picture shown big for the mod ("mod:&lt;texture&gt;" / "game:&lt;texture&gt;"; null: automatic).</summary>
    public string? PreviewImage;
    /// <summary>Extension: the card picture in the mod list (a TextureName the mod replaces; null: automatic).</summary>
    public string? CardPicture;
    /// <summary>Custom pictures the preview / card can use ("Pictures/x.png" → source file); the ones in use are saved.</summary>
    public List<(string File, string Source)> Pictures = [];
    /// <summary>Extension: the author's 3D camera per mesh (kept as it is by the editor).</summary>
    public Dictionary<string, float[]>? PreviewViews;
    /// <summary>Extension: the author's 3D light level (kept as it is by the editor).</summary>
    public float? PreviewLight;
    /// <summary>Extension: voice lines turned off (the packages already carry the change; this lets them be turned on again).</summary>
    public List<VoiceOffEntry> VoiceOff = [];
    public List<AnimSwapEntry> AnimSwaps = [];
    public string? MovedFrom;
    public List<VoiceShiftEntry> VoiceShifts = [];
    public List<PowerColorEntry> PowerColors = [];
    public List<string> PostImages = [];
    /// <summary>The Model tab's work (bone maps, FBX edits, its choices, the package before the model): a folder copied into
    /// the mod as Model\ on Save (null: the mod's own Model folder stays as it is).</summary>
    public string? ModelFolder;

    /// <summary>The changelog as it will be saved: this version's changes (if any) on top of the earlier entries.</summary>
    public List<ChangelogEntry> FullChangelog()
    {
        var list = Changelog.Where(e => !e.Version.Trim().Equals(Version.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(Changes)) list.Insert(0, new ChangelogEntry { Version = Version.Trim(), Changes = Changes.Trim().Replace("\r\n", "\n") });
        return list;
    }

    /// <summary>What this draft would save, for posts made before saving: texture .dds names are the source paths.</summary>
    public ModManifest Preview()
    {
        var m = new ModManifest { Name = Name.Trim(), Author = NullIfEmptyText(Author), Version = NullIfEmptyText(Version), Description = NullIfEmptyText(Description), Tags = Tags.Count > 0 ? [.. Tags] : null };
        var log = FullChangelog();
        m.Changelog = log.Count > 0 ? log : null;
        m.UpkReplacements = Packages.Select(p => p.File).ToList();
        var lists = new[] { m.Replacements, m.AchievementReplacements, m.StoreReplacements };
        for (int k = 0; k < 3; k++) lists[k].AddRange(Textures[k].Select(t => new TextureReplacement { TextureName = t.Texture, DdsFileName = t.Source }));
        m.ExtraIconReplacements = Extra.Count > 0 ? Extra.Select(x => new ExtraIconReplacement { Package = x.Package, TextureName = x.Texture, DdsFileName = x.Source }).ToList() : null;
        m.AudioPacks = SoundPacks.Select(Path.GetFileName).OfType<string>().ToList();
        m.Languages = Strings.Select(s => s.Language).Distinct().ToList();
        return m;
    }

    static string? NullIfEmptyText(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
        d.Description = m.Manifest.Description ?? "";
        (d.PostNexus, d.PostDiscord, d.PostImages) = ModPost.Read(m.Folder);
        d.NexusModId = m.Manifest.NexusModId;
        d.PreviewImage = m.Manifest.PreviewImage;
        d.CardPicture = m.Manifest.CardPicture;
        d.Pictures = [.. ModPictures.Own(m.Folder).Select(x => (x.Key[ModPictures.Prefix.Length..], x.File))];
        d.PreviewViews = m.Manifest.PreviewViews;
        d.PreviewLight = m.Manifest.PreviewLight;
        d.VoiceOff = m.Manifest.VoiceOff?.ToList() ?? [];
        d.AnimSwaps = m.Manifest.AnimSwaps?.ToList() ?? [];
        d.MovedFrom = m.Manifest.MovedFrom;
        d.VoiceShifts = m.Manifest.VoiceShifts?.ToList() ?? [];
        d.PowerColors = m.Manifest.PowerColors?.ToList() ?? [];
        var log = m.Manifest.Changelog ?? [];
        d.Changes = log.FirstOrDefault(e => e.Version.Trim().Equals((m.Manifest.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase))?.Changes ?? "";
        d.Changelog = log.Select(e => new ChangelogEntry { Version = e.Version, Changes = e.Changes }).ToList();
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
            manifest.Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim().Replace("\r\n", "\n");
            var changelog = d.FullChangelog();
            manifest.Changelog = changelog.Count > 0 ? changelog : null;
            manifest.NexusModId = d.NexusModId;
            manifest.PreviewImage = d.PreviewImage;
            manifest.CardPicture = d.CardPicture;
            manifest.PreviewViews = d.PreviewViews is { Count: > 0 } pv ? pv : null;
            manifest.PreviewLight = d.PreviewLight;
            manifest.VoiceOff = d.VoiceOff.Count > 0 ? d.VoiceOff : null;
            manifest.AnimSwaps = d.AnimSwaps.Count > 0 ? d.AnimSwaps : null;
            manifest.MovedFrom = string.IsNullOrEmpty(d.MovedFrom) ? null : d.MovedFrom;
            manifest.VoiceShifts = d.VoiceShifts.Count > 0 ? d.VoiceShifts : null;
            manifest.PowerColors = d.PowerColors.Count > 0 ? d.PowerColors : null;
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
            ModPost.Write(temp, d.PostNexus, d.PostDiscord, d.PostImages);   // copied before the old folder goes away
            // The Model tab's work (Model\): the editor's work folder, else the mod's own (both before the old folder goes)
            string? modelFrom = d.ModelFolder ?? (editing != null ? Path.Combine(editing.Folder, ModelWork.Folder) : null);
            if (modelFrom != null && Directory.Exists(modelFrom)) ModelWork.CopyInto(modelFrom, Path.Combine(temp, ModelWork.Folder));
            // Custom pictures the preview or card uses (Pictures\), copied before the old folder goes away too.
            foreach (var (file, source) in d.Pictures)
                if ((ModPictures.Prefix + file).Equals(d.PreviewImage, StringComparison.OrdinalIgnoreCase) || (ModPictures.Prefix + file).Equals(d.CardPicture, StringComparison.OrdinalIgnoreCase))
                {
                    string dest = Path.Combine(temp, file.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(source, dest, overwrite: true);
                }

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

/// <summary>
/// A mod's saved post: &lt;mod&gt;\Post\nexus.txt, discord.md and Images\*.png|jpg (Kurt: the post text and preview pictures
/// are kept in the mod, and written out next to the zip when it's exported). Not listed in the manifest, not in the zip:
/// MHModManager and mod users never see it.
/// </summary>
static class ModPost
{
    public const string Folder = "Post";
    static readonly string[] ImageExt = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    public static (string? Nexus, string? Discord, List<string> Images) Read(string modFolder)
    {
        string p = Path.Combine(modFolder, Folder), img = Path.Combine(p, "Images");
        string? Text(string f) => File.Exists(Path.Combine(p, f)) ? File.ReadAllText(Path.Combine(p, f)) : null;
        var images = Directory.Exists(img) ? Directory.GetFiles(img).Where(f => ImageExt.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
        return (Text("nexus.txt"), Text("discord.md"), images);
    }

    /// <summary>Writes the post into a mod folder (texts, and the images copied under their own names). Removes it when empty.</summary>
    public static void Write(string modFolder, string? nexus, string? discord, IEnumerable<string> images)
    {
        string p = Path.Combine(modFolder, Folder), img = Path.Combine(p, "Images");
        var list = images.Where(File.Exists).ToList();
        // Read the sources first: they may be the files about to be replaced.
        var bytes = list.Select(f => (Name: Path.GetFileName(f), Data: File.ReadAllBytes(f))).ToList();
        if (Directory.Exists(p)) Directory.Delete(p, true);
        if (nexus == null && discord == null && bytes.Count == 0) return;
        Directory.CreateDirectory(p);
        if (nexus != null) File.WriteAllText(Path.Combine(p, "nexus.txt"), nexus.Replace("\r\n", "\n"));
        if (discord != null) File.WriteAllText(Path.Combine(p, "discord.md"), discord.Replace("\r\n", "\n"));
        if (bytes.Count > 0)
        {
            Directory.CreateDirectory(img);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, data) in bytes)
            {
                string n = name, stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
                for (int i = 2; !used.Add(n); i++) n = $"{stem} ({i}){ext}";
                File.WriteAllBytes(Path.Combine(img, n), data);
            }
        }
    }

    /// <summary>
    /// Export: "&lt;zip name&gt; - Post" next to the zip, with the saved post (or a fresh one) and the images (the saved ones,
    /// or the mod's store images, costume icons and hero portraits). Returns the folder.
    /// </summary>
    public static string WriteBeside(Mod mod, string zipPath)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zipPath))!, Path.GetFileNameWithoutExtension(zipPath) + " - Post");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var (nexus, discord, images) = Read(mod.Folder);
        var src = PostWriter.From(mod);
        File.WriteAllText(Path.Combine(dir, "nexus.txt"), (nexus ?? PostWriter.Nexus(src)).Replace("\r\n", "\n"));
        File.WriteAllText(Path.Combine(dir, "discord.md"), (discord ?? PostWriter.Discord(src)).Replace("\r\n", "\n"));
        string imgDir = Path.Combine(dir, "Images");
        if (images.Count > 0) { Directory.CreateDirectory(imgDir); foreach (string f in images) File.Copy(f, Path.Combine(imgDir, Path.GetFileName(f)), true); }
        else PostWriter.SaveImages(src, mod.Folder, imgDir);
        return dir;
    }
}

/// <summary>
/// The Model tab's work kept in a mod (2026-10-03): Model\ holds the bone maps, the FBX edits, the tab's choices and the
/// package as it was before the model (Model\base). Not in the manifest; a legacy export leaves it out. Build outputs
/// (builds\) aren't kept: the built package is the mod's own package.
/// </summary>
static class ModelWork
{
    public const string Folder = "Model";

    /// <summary>The packages (without .upk) the Model tab built, from its state.json; empty when there's none.</summary>
    static HashSet<string> Built(string folder)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "state.json")));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("Built", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var e in p.Value.EnumerateArray()) if (e.GetString() is string s) set.Add(Path.GetFileNameWithoutExtension(s));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return set;
    }

    /// <summary>
    /// Deletes the editor's work folders (library\model-work-xxxxxxxx) left by a run that didn't close normally (killed, crashed).
    /// Only at a normal start, which holds the single instance, so none can be in use; what's in them was never saved into a
    /// mod (Save copies the work into the mod's Model folder). The count deleted.
    /// </summary>
    public static int SweepOrphans(string? library)
    {
        if (library == null || !Directory.Exists(library)) return 0;
        int n = 0;
        foreach (var d in Directory.GetDirectories(library, "model-work-*"))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), "^model-work-[0-9a-f]{8}$")) continue;
            try { Directory.Delete(d, true); n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    public static void CopyInto(string from, string to)
    {
        // rigs (rigs\<fbx> on <package>): only for the packages the tab built, and only the rig's own files
        var built = Built(from);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f);
            if (rel.StartsWith("builds" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("rigs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var parts = rel.Split(Path.DirectorySeparatorChar);
                int on = parts.Length == 3 ? parts[1].LastIndexOf(" on ", StringComparison.Ordinal) : -1;
                if (on < 0 || !built.Contains(parts[1][(on + 4)..]) || !MhoMffImporter.AutoRig.IsRigFile(parts[2])) continue;
            }
            string dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }
}

