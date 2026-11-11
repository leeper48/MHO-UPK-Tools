using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Any character as a vanity pet (Kurt, 2026-11-06: "Iceman over a vanity pet", bipeds for quadrupeds and back).
///
/// Evidence (2026-11-06, --costume-anims / --props-under on the stock UC__MarvelVanityPet_*_SF packages):
///  - A pet plays only idle, movement_run and its fidgets (fidget01 / fidget02 / fidget03), the same names heroes use
///    (Iceman's base package has idle, movement_run, fidget01, fidget02).
///  - Pets use the heroes' animation trees (Cosmo, Spider-Ham, Bamf, Ice Golem: pc_at_v2; Vision, Mini Sentinel: pc_at_nocape;
///    Old Lace: monster_at), so a pet entity animates any model by those names.
///  - Mini pets are shrunk by the component's Scale (Vision's pet 0.5, Bamf 0.8, Old Lace 0.5), not a smaller mesh.
///
/// So the moved pet is the target pet's stock package (its class, GUID, item and powers untouched) with the source's model
/// copied in (CrossMove.Build: mesh, materials, physics asset, sockets, the source's animation tree), the source's idle /
/// run / fidgets copied in as the pet's slots (AnimSwap.Build: a set of its own, last in the list so it wins), and
/// optionally the Scale (1 = full size). No voice: pets don't speak.
/// </summary>
static class PetMove
{
    public const string Prefix = "UC__MarvelVanityPet_";

    /// <summary>The game's vanity pet packages (not the shared base package).</summary>
    public static List<string> Pets(string cooked) =>
        [.. Directory.EnumerateFiles(cooked, Prefix + "*_SF.upk").Select(Path.GetFileName).OfType<string>()
            .Where(f => !f.Equals(Prefix + "SF.upk", StringComparison.OrdinalIgnoreCase) && !f.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Contains("copy", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The pet package for a name ("Cosmo", "UC__MarvelVanityPet_Cosmo_SF", "…_SF.upk"), or null.</summary>
    public static string? PetFile(string cooked, string name)
    {
        string n = name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (!n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) n = Prefix + n;
        if (!n.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) n += "_SF";
        return Pets(cooked).FirstOrDefault(f => f.Equals(n + ".upk", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The character classes a package shows (default__&lt;class&gt;.initialskeletalmesh), lower case, and each one's model.</summary>
    public static List<(string Class, string Mesh)> Characters(Package p)
    {
        var list = new List<(string, string)>();
        for (int i = 0; i < p.Exports.Length; i++)
        {
            var e = p.Exports[i];
            if (!e.ObjectName.Equals("initialskeletalmesh", StringComparison.OrdinalIgnoreCase)) continue;
            string at = p.PathOf(e);
            const string head = "marvelgamecontent.default__";
            if (!at.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
            string cls = at[head.Length..^".initialskeletalmesh".Length].ToLowerInvariant();
            try
            {
                var d = p.ReadExportBytes(e);
                if (TagWalker.Walk(p, d, 16) is { } tags && tags.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) is { Size: 4 } sm
                    && BitConverter.ToInt32(d, sm.ValueAt) is int r && r > 0 && r <= p.Exports.Length)
                    list.Add((cls, p.PathOf(p.Exports[r - 1])));   // the path: a moved team-up keeps the stock mesh of the same name beside it
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        }
        return list;
    }

    /// <summary>Where a pet slot's animation comes from in the source, best first (a slot the source lacks takes the next).</summary>
    static IEnumerable<string> Candidates(string slot) => slot.ToLowerInvariant() switch
    {
        "idle" => ["idle", "idle_outofcombat", "idle_combat"],
        // (last the idle: a character that never walks, Professor X in his hover chair, Kurt 2026-11-10: "his idle could be
        // used for run"; the pet's own run can't fit another skeleton)
        "movement_run" => ["movement_run", "movement_run_outofcombat", "movement_walk", "idle", "idle_outofcombat"],
        "movement_run_outofcombat" => ["movement_run_outofcombat", "movement_run", "movement_walk", "idle", "idle_outofcombat"],
        "movement_walk" => ["movement_walk", "movement_run_outofcombat", "movement_run", "idle", "idle_outofcombat"],
        "idle_combat" => ["idle_combat", "idle"],
        var f when f.StartsWith("fidget") => [f, "fidget01", "fidget02", "fidget03", "emote_hello02", "emote_laugh", "emote_pose", "idle"],
        var other => [other],
    };

    public sealed record Result(byte[] Package, List<(string Slot, string From)> Anims);

    /// <param name="sourcePath">The package holding the source character (a mod's copy or the game's).</param>
    /// <param name="sourceClass">Its class (lower case), or null for the package's first character.</param>
    /// <param name="fileFor">Other packages by name (the source's hero base package, shared sets): the mod's copy first.</param>
    /// <param name="scale">The pet's size (1 = the source's full size), or null to keep the pet's own.</param>
    public static Result Build(string sourcePath, string? sourceClass, string targetStock, Func<string, (string Path, bool FromMod)?> fileFor,
        string cooked, float? scale, List<string> log)
    {
        var src = Package.Open(sourcePath);
        var chars = Characters(src);
        var (cls, mesh) = sourceClass == null ? chars.FirstOrDefault() : chars.FirstOrDefault(c => c.Class.Equals(sourceClass, StringComparison.OrdinalIgnoreCase));
        if (cls == null) throw new InvalidDataException($"no character in {Path.GetFileName(sourcePath)}" + (sourceClass != null ? $" with the class {sourceClass}" : ""));
        var dst = Package.Open(targetStock);
        var (petClass, petMesh) = Characters(dst).FirstOrDefault(c => c.Class.StartsWith("marvelvanitypet_", StringComparison.OrdinalIgnoreCase));
        if (petClass == null) throw new InvalidDataException($"{Path.GetFileName(targetStock)} has no vanity pet in it");
        log.Add($"source: {cls} ({mesh}) in {Path.GetFileName(sourcePath)}; pet: {petClass} ({petMesh})");

        // The source hero's base package (materials it imports from there, its animations).
        string? baseHero = null;
        var parts = cls.Split('_');
        if (cls.StartsWith("marvelplayer_") && parts.Length > 2 && fileFor($"UC__MarvelPlayer_{parts[1]}_SF") is { } bh) baseHero = bh.Path;

        byte[] built = CrossMove.Build(sourcePath, mesh, targetStock, petClass, baseHero, log, sounds: false, sourceClass: cls, cooked: cooked, voice: false, scale: scale);

        // The pet's slots, from the source's animations as the game plays them (its list, last set wins).
        string sourceFile = Path.GetFileName(sourcePath);
        var have = CostumeAnims.Read(sourcePath, sourceFile, fileFor, cls) ?? throw new InvalidDataException($"the animations of {cls} don't read");
        var pet = CostumeAnims.Read(targetStock, Path.GetFileName(targetStock), fileFor, petClass) ?? throw new InvalidDataException($"the animations of {petClass} don't read");
        var swaps = new List<AnimSwap.Swap>();
        var picked = new List<(string, string)>();
        // The slots: the pet's own (idle, movement_run, fidgets), the source's own idle / movement / fidget animations (its
        // tree comes along and asks for them: Kurt 2026-11-06, Iceman's pet ran in A-pose, as pc_at_v5 plays
        // movement_run_outofcombat out of combat, which a pet always is), and the names the heroes' trees ask for at a walk,
        // out of combat or in combat, filled from the run and idle when the source has none (Cosmo's set has only four).
        static bool Moving(string n) => n.StartsWith("idle", StringComparison.OrdinalIgnoreCase) || n.StartsWith("movement_", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("fidget", StringComparison.OrdinalIgnoreCase) || n.StartsWith("add_pivot", StringComparison.OrdinalIgnoreCase);
        var slots = pet.Anims.Select(a => a.Name)
            .Concat(have.Anims.Where(a => a.From.File != null && Moving(a.Name)).Select(a => a.Name))
            .Concat(["movement_run_outofcombat", "movement_walk", "idle_combat"])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var slot in slots)
        {
            var from = Candidates(slot).Select(n => have.Anims.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && a.From.File != null)).FirstOrDefault(a => a != null);
            if (from == null) { log.Add($"animation {slot}: the source has nothing to play there; the pet's own stays (it won't fit the new model)"); continue; }
            swaps.Add(new AnimSwap.Swap(slot, from.From.File!, from.From.Export, from.Export));
            picked.Add((slot, $"{from.Name} ({from.From.Path})"));
        }
        if (swaps.Count == 0) throw new InvalidDataException("the source has none of the pet's animations (idle, movement_run, fidgets)");
        string temp = Path.Combine(Path.GetTempPath(), "mho-pet-" + Guid.NewGuid().ToString("N")[..8] + ".upk");
        try
        {
            File.WriteAllBytes(temp, built);
            byte[] withAnims = AnimSwap.Build(temp, petClass, swaps, log);
            foreach (var (s, f) in picked) log.Add($"animation {s} ← {f}");
            return new Result(withAnims, picked);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    // ---- Move to a Pet (Costume ▾ → Pet, Kurt 2026-11-06: "a swap to Pet option just like Hero and Team-Up")

    /// <summary>A vanity pet the game has: its package, class (lower case), name in the game, item icon and store image.</summary>
    public sealed record Pet(string File, string Class, string Name, ulong NameId, string? Icon, string? Store);

    /// <summary>Every vanity pet with its picture and name (the pet item's: GamePictures), by name.</summary>
    public static List<Pet> All(GameState game, StockCatalog? catalog)
    {
        var eng = catalog?.Strings("eng").GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Text) ?? [];
        var list = new List<Pet>();
        foreach (string f in Pets(game.Cooked))
        {
            string cls = f[4..^7];   // UC__<class>_SF.upk
            var pics = GamePictures.For(game.Root, f);
            string name = pics != null && pics.NameId != 0 && eng.TryGetValue(pics.NameId, out var t) && t.Trim().Length > 0
                ? CostumeMove.Untagged(t)
                : System.Text.RegularExpressions.Regex.Replace(cls["MarvelVanityPet_".Length..].Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
            list.Add(new Pet(f, cls.ToLowerInvariant(), name, pics?.NameId ?? 0, pics?.Icon, pics?.Store));
        }
        return [.. list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static string NewName(Mod mod, Pet pet) => $"{mod.Name} (on Pet {pet.Name})";

    /// <summary>The pet a pet mod is for: its one vanity pet package (and no hero or team-up package), or null.</summary>
    public static Pet? SourcePet(Mod mod, IReadOnlyList<Pet> pets)
    {
        var ups = mod.Manifest.UpkReplacements;
        if (ups.Any(f => f.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || f.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase))) return null;
        var mine = ups.Where(f => f.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return mine.Count == 1 && File.Exists(Path.Combine(mod.Folder, mine[0])) ? pets.FirstOrDefault(p => p.File.Equals(mine[0], StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>
    /// The costume as a pet, a new mod ("&lt;name&gt; (on Pet &lt;pet&gt;)"): the pet's stock package with the costume's model,
    /// animations and optional size (Build), its model's paths made its own (UniqueNames, as a cross-hero move), the costume
    /// icon as the pet's item icon (both 40×40) and the hero portrait as the pet's store image (both 180×136), and the pet's
    /// name in game when one is given. Returns the folder name, or null with the reason.
    /// </summary>
    public static string? CreateMod(ModLibrary lib, Mod mod, string srcFile, Costume source, Pet pet, float? scale, string? name, GameState game,
        StockCatalog? catalog, List<string> log, out string? error, Mod? replace = null)
        => CreateMod(lib, mod, srcFile, new Source(source.Class, source.Icon, source.Portrait, "Costume Icon", "Hero Portrait", source.Short.Replace(".prototype", "")),
            pet, scale, name, game, catalog, log, out error, replace);

    /// <summary>
    /// Another pet as the source (Kurt, 2026-11-06: "move pet to another pet from right click"): a pet mod (its one
    /// UC__MarvelVanityPet_ package) onto another pet. Its item icon and store image go to the target pet's.
    /// </summary>
    public static string? CreateMod(ModLibrary lib, Mod mod, string srcFile, Pet source, Pet pet, float? scale, string? name, GameState game,
        StockCatalog? catalog, List<string> log, out string? error, Mod? replace = null)
        => CreateMod(lib, mod, srcFile, new Source(source.Class, source.Icon, source.Store, "Pet Icon", "Pet Store Image", "pet " + source.Name),
            pet, scale, name, game, catalog, log, out error, replace);

    /// <summary>What a move to a pet starts from: the character's class, the pictures that go to the pet's icon and store
    /// image (with their kinds for the log), and how the note names it.</summary>
    public sealed record Source(string Class, string? Icon, string? Store, string IconKind, string StoreKind, string Label);

    /// <summary>A pet's size: its component's Scale (1 when it sets none).</summary>
    public static float ScaleOf(string packagePath, string cls)
    {
        try
        {
            var p = Package.Open(packagePath);
            string comp = $"marvelgamecontent.default__{cls}.initialskeletalmesh";
            int c = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(comp, StringComparison.OrdinalIgnoreCase));
            if (c < 0) return 1f;
            byte[] d = p.ReadExportBytes(p.Exports[c]).ToArray();
            if (TagWalker.Walk(p, d, 16)?.FirstOrDefault(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } t) return BitConverter.ToSingle(d, t.ValueAt);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException) { }
        return 1f;
    }

    static string? CreateMod(ModLibrary lib, Mod mod, string srcFile, Source source, Pet pet, float? scale, string? name, GameState game,
        StockCatalog? catalog, List<string> log, out string? error, Mod? replace)
    {
        error = null;
        string? stock = new Originals(lib.DataFolder, game).Find(pet.File);
        if (stock == null) { error = $"no stock copy of {pet.File}"; return null; }
        string work = Path.Combine(lib.DataFolder, "pet-move-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            var res = Build(Path.Combine(mod.Folder, srcFile), source.Class.ToLowerInvariant(), stock, CostumeAnims.FilesFor(mod, game.Cooked), game.Cooked, scale, log);
            string file = Path.Combine(work, pet.File);
            File.WriteAllBytes(file, res.Package);
            var unique = UniqueNames.PlanFor(file, stock, pet.File, UniqueNames.Shared(UniqueNames.Folder(game.Cooked)),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pet.File }, UniqueNames.Tag(NewName(mod, pet)));
            if (unique.Changes) { File.WriteAllBytes(file, UniqueNames.Build(file, unique)); log.AddRange(unique.Notes.Select(n => "unique: " + n)); }

            var d = ModDraft.From(mod);
            d.Name = NewName(mod, pet);
            d.Packages = [(pet.File, file)];
            d.Textures = [[], [], []];
            d.Extra = [];
            var byPackage = Applier.IconPackages.Select((p, k) => (p.File, k)).ToDictionary(x => x.File, x => x.k, StringComparer.OrdinalIgnoreCase);
            var mine = Applier.IconPackages.SelectMany(p => p.List(mod.Manifest).Select(r => (Package: p.File, r.TextureName, r.DdsFileName)))
                .Concat(mod.Manifest.Extra.Select(r => (r.Package, r.TextureName, r.DdsFileName))).ToList();
            int icons = 0;
            foreach (var (kind, from, to) in new[] { (source.IconKind, source.Icon, pet.Icon), (source.StoreKind, source.Store, pet.Store) })
            {
                if (Costume.IconTexture(from) is not { } f || GamePictures.Split(to) is not { } t) continue;
                var have = mine.FirstOrDefault(x => x.Package.Equals(f.Package, StringComparison.OrdinalIgnoreCase) && x.TextureName.Equals(f.Texture, StringComparison.OrdinalIgnoreCase));
                if (have.DdsFileName == null) continue;
                var move = new CostumeMove.IconMove(kind, t.Package, f.Texture, t.Texture, have.DdsFileName, CostumeMove.DdsSize(Path.Combine(mod.Folder, have.DdsFileName)), catalog?.Size(t.Package, t.Texture));
                var resized = new List<string>();
                string dds = CostumeMove.FitIcon(move, mod.Folder, work, catalog, resized);
                foreach (string r in resized) log.Add("resized " + r);
                if (byPackage.TryGetValue(t.Package, out int k)) d.Textures[k].Add((t.Texture, dds)); else d.Extra.Add((t.Package, t.Texture, dds));
                icons++;
            }
            d.Strings = !string.IsNullOrWhiteSpace(name) && pet.NameId != 0
                ? [new StringReplacement("eng", CostumeMove.StringFileFor("eng", pet.NameId), pet.NameId, name.Trim())]
                : [];
            d.SoundPacks = [];
            d.PreviewImage = null; d.PreviewViews = null; d.CardPicture = null;
            d.PostNexus = d.PostDiscord = null; d.PostImages = [];
            d.NexusModId = null; d.Changes = ""; d.Changelog = [];
            if (replace != null) { d.Tags = [.. replace.ModTags]; d.Notes = replace.Manifest.Notes ?? ""; }
            else d.Notes = (d.Notes.Length > 0 ? d.Notes.TrimEnd() + Environment.NewLine : "") + $"Moved from {mod.Name} ({source.Label} → pet {pet.Name}{(scale is float sc ? $", size {sc:0.##}" : "")}).";
            CostumeMove.KeepPowerColors(d, mod, replace, sameHero: false);
            d.VoiceOff = [];
            d.AnimSwaps = [];
            d.MovedFrom = source.Label;
            log.Add($"icons: {icons}; name: {(d.Strings.Count > 0 ? d.Strings[0].Text : "the pet's own")}");
            if (replace != null) d = CostumeMove.UpdateDraft(d, replace, [pet.File], null);
            return ModWriter.Save(lib, d, replace, out error);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { error = ex.Message; return null; }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } }
    }
}
