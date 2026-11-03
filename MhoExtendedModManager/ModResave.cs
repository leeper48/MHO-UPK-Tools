using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// The mod list's Resave (Kurt, 2026-11-03: after each new version the travel tests needed Edit, then Save; opening the
/// Editor and waiting for its tabs defeated the purpose): Save's build steps run on the saved mod as it is, without the
/// Editor, in the Editor's order (ModEditorView.Save): borrowed powers, power colors, parts, power sounds, voice repair,
/// unique paths, then the write. Voice edits and voice shifts only rebuild when changed, so an unchanged mod has none.
/// Dodge ghosts and added props are built by the Editor's own code: a mod with either is refused with a reason.
/// </summary>
static class ModResave
{
    /// <summary>The mod saved again; its folder name, or null with <paramref name="error"/>. Call off the UI thread.</summary>
    public static string? Run(ModLibrary lib, GameState game, Mod m, List<string> log, out string? error)
    {
        error = null;
        var draft = ModDraft.From(m);
        if (draft.DodgeGhosts.Count > 0 || draft.AddedProps.Count > 0)
        {
            error = $"\"{m.Name}\" has {(draft.DodgeGhosts.Count > 0 ? "dodge ghosts" : "added props")}, which only the Editor builds: use Edit, then Save.";
            return null;
        }
        var works = new List<string>();
        string? modelWork = null;
        // (as the Editor's WorkFolder: the mod's Model folder copied into a work folder, which Save copies back)
        string ModelFolder()
        {
            if (draft.ModelFolder != null) return draft.ModelFolder;
            modelWork = Path.Combine(lib.DataFolder, "model-work-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(modelWork);
            if (Path.Combine(m.Folder, ModelWork.Folder) is var own && Directory.Exists(own)) ModelWork.CopyInto(own, modelWork);
            return draft.ModelFolder = modelWork;
        }
        void Keep(string? w) { if (w != null) works.Add(w); }
        try
        {
            Fx.GameData? db = null;
            var borrowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (draft.PowerBorrows.Count > 0 || m.Manifest.PowerBorrows is { Count: > 0 })
            {
                db = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(game.Root, "Data", "Game", "Calligraphy.sip")));
                string work = Path.Combine(lib.DataFolder, "power-borrow-" + Guid.NewGuid().ToString("N")[..8]);
                Keep(work);
                foreach (string f in PowerBorrow.Rebuild(draft, m, lib, game, db, work, ModelFolder(), log).Keys) borrowed.Add(f);
            }
            Keep(PowerColorBuild.Apply(draft, m, lib, game, ref db, log, ModelFolder, borrowed));
            Keep(PowerParts.Build(draft, m, lib, game, log));
            Keep(PowerSounds.Apply(draft, m, lib, game, new Dictionary<string, byte[]>(), log));
            Keep(SaveSteps.RepairVoices(draft, lib, log));
            Keep(SaveSteps.MakeUnique(draft, lib, game, log));
            string? saved = ModWriter.Save(lib, draft, m, out error);
            if (saved != null) ModHistory.Add(lib.DataFolder, saved, "Resaved", null);
            return saved;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or ArgumentException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            foreach (string w in works.Append(modelWork).OfType<string>())
                try { if (Directory.Exists(w)) Directory.Delete(w, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
