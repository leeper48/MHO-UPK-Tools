namespace MhoExtendedModManager;

/// <summary>
/// --test-locks: self-test of the top/bottom padlocks and the priority moves on a throwaway library in the temp folder
/// (8 empty mods A..H). Prints each step's order; locked mods are shown as [X] (top) or {X} (bottom).
/// </summary>
static class LockTest
{
    public static int Run()
    {
        string data = Path.Combine(Path.GetTempPath(), "mhoextmm_locktest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (string n in new[] { "A", "B", "C", "D", "E", "F", "G", "H" })
            {
                Directory.CreateDirectory(Path.Combine(data, "mods", n));
                File.WriteAllText(Path.Combine(data, "mods", n, "manifest.json"), $"{{ \"Name\": \"{n}\" }}");
            }
            File.WriteAllText(Path.Combine(data, "state.json"), "{ \"EnabledMods\": [], \"ModOrder\": [\"A\",\"B\",\"C\",\"D\",\"E\",\"F\",\"G\",\"H\"] }");
            var lib = ModLibrary.Load(data);
            int fails = 0;
            void Expect(string step, string want)
            {
                lib.SaveState();
                lib = ModLibrary.Load(data);   // every step round-trips through state.json
                string got = string.Concat(lib.Mods.Select(m => m.Lock == ModLock.Top ? $"[{m.FolderName}]" : m.Lock == ModLock.Bottom ? $"{{{m.FolderName}}}" : m.FolderName));
                bool ok = got == want;
                if (!ok) fails++;
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {step,-44} {got}{(ok ? "" : "   expected " + want)}");
            }
            Mod M(string n) => lib.Find(n)!;
            bool Lock(string n) => lib.ToggleLock(M(n));

            Expect("start", "ABCDEFGH");
            Console.WriteLine($"  {(Lock("C") ? "FAIL" : "ok  ")} C (middle) can't be locked");
            if (!Lock("A")) fails++; Expect("lock A (top)", "[A]BCDEFGH");
            if (!Lock("B")) fails++; Expect("lock B (next to A)", "[A][B]CDEFGH");
            if (!Lock("H")) fails++; Expect("lock H (bottom)", "[A][B]CDEFG{H}");
            if (!Lock("G")) fails++; Expect("lock G (next to H)", "[A][B]CDEF{G}{H}");
            // A locked mod moves within its locked run only (Kurt, 2026-10-01).
            lib.Move(M("B"), 1); Expect("locked B down: stays in the top run", "[A][B]CDEF{G}{H}");
            lib.Move(M("B"), -1); Expect("locked B up: above A, still locked", "[B][A]CDEF{G}{H}");
            lib.MoveToEnd(M("B"), 1); Expect("locked B to bottom: end of the top run", "[A][B]CDEF{G}{H}");
            lib.Move(M("C"), -1); Expect("C up: stops under the top lock", "[A][B]CDEF{G}{H}");
            lib.MoveToEnd(M("C"), 1); Expect("C to bottom: above the bottom lock", "[A][B]DEFC{G}{H}");
            lib.MoveToEnd(M("E"), -1); Expect("E to top: below the top lock", "[A][B]EDFC{G}{H}");
            lib.Move(M("C"), 1); Expect("C down: stops above the bottom lock", "[A][B]EDFC{G}{H}");
            // Drag and drop (MoveTo): to a position, never past the locks; a locked mod can't be dragged.
            // (positions count from the top, locked mods included)
            lib.MoveTo(M("C"), 3); Expect("drag C to position 3", "[A][B]ECDF{G}{H}");
            lib.MoveTo(M("F"), 0); Expect("drag F onto the top: stops under the top lock", "[A][B]FECD{G}{H}");
            lib.MoveTo(M("E"), 99); Expect("drag E past the end: stops above the bottom lock", "[A][B]FCDE{G}{H}");
            lib.MoveTo(M("G"), 2); Expect("drag locked G up: stays in the bottom run", "[A][B]FCDE{G}{H}");
            lib.MoveTo(M("G"), 7); Expect("drag locked G below H", "[A][B]FCDE{H}{G}");
            lib.MoveGroup([M("H")], M("G"), below: true); Expect("drag locked H below G", "[A][B]FCDE{G}{H}");
            lib.MoveGroup([M("C")], M("G"), below: true); Expect("drag C below locked G: stops above the bottom run", "[A][B]FDEC{G}{H}");
            lib.MoveTo(M("C"), 3); Expect("C back", "[A][B]FCDE{G}{H}");
            lib.MoveTo(M("E"), 2); lib.MoveTo(M("D"), 3); Expect("back as before", "[A][B]EDFC{G}{H}");
            // Marked groups (Shift / Ctrl click): move together, keep their order, stop at the locks, locked members stay.
            lib.MoveGroupBy([M("E"), M("F")], 1); Expect("group E,F down one (each past the next mod outside the group)", "[A][B]DECF{G}{H}");
            lib.MoveGroupBy([M("E"), M("F")], -1); Expect("group E,F up one", "[A][B]EDFC{G}{H}");
            lib.MoveGroupBy([M("E"), M("D")], -1); Expect("group E,D up: already under the lock", "[A][B]EDFC{G}{H}");
            lib.MoveGroupToEnd([M("E"), M("F")], 1); Expect("group E,F to the bottom", "[A][B]DCEF{G}{H}");
            lib.MoveGroupToEnd([M("C"), M("F"), M("G")], -1); Expect("group C,F,G to the top (G locked: stays)", "[A][B]CFDE{G}{H}");
            lib.MoveGroup([M("C"), M("F")], M("E"), below: true); Expect("drag group C,F below E", "[A][B]DECF{G}{H}");
            lib.MoveGroup([M("C"), M("F")], M("D"), below: false); Expect("drag group C,F above D", "[A][B]CFDE{G}{H}");
            lib.MoveTo(M("E"), 2); lib.MoveTo(M("D"), 3); lib.MoveTo(M("F"), 4); Expect("back as before", "[A][B]EDFC{G}{H}");
            Lock("A"); Expect("unlock A (outer): goes under the locked B", "[B]AEDFC{G}{H}");
            // A new mod written at the top of ModOrder (as ModWriter / install do) lands under the top lock.
            Directory.CreateDirectory(Path.Combine(data, "mods", "N"));
            File.WriteAllText(Path.Combine(data, "mods", "N", "manifest.json"), "{ \"Name\": \"N\" }");
            lib.State.ModOrder = ["N", .. lib.State.ModOrder];
            File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(lib.State, ModManifest.Json));
            lib = ModLibrary.Load(data);
            Expect("new mod at the top: under the top lock", "[B]NAEDFC{G}{H}");
            // The real add paths, reading state.json straight after (before any load puts things right).
            string? RawOrder() => string.Concat(ModState.Load(Path.Combine(data, "state.json")).ModOrder);
            string src = Path.Combine(data + "_src", "I");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "manifest.json"), "{ \"Name\": \"I\" }");
            var log = new List<string>();
            ModInstaller.Install(src, lib, log);
            Directory.Delete(data + "_src", true);
            string raw = RawOrder() ?? "";
            if (raw != "BINAEDFCGH") fails++;
            Console.WriteLine($"  {(raw == "BINAEDFCGH" ? "ok  " : "FAIL")} {"install: state.json as written",-44} {raw}");
            lib = ModLibrary.Load(data);
            Expect("install: under the top lock, above bottom", "[B]INAEDFC{G}{H}");
            string dummy = Path.Combine(Path.GetTempPath(), "mhoextmm_locktest_dummy.upk");
            File.WriteAllBytes(dummy, [1, 2, 3]);
            ModWriter.Save(lib, new ModDraft { Name = "J", Packages = [("Dummy.upk", dummy)] }, null, out string? err);
            File.Delete(dummy);
            raw = RawOrder() ?? "";
            if (raw != "BJINAEDFCGH") fails++;
            Console.WriteLine($"  {(raw == "BJINAEDFCGH" ? "ok  " : "FAIL")} {"New Mod: state.json as written" + (err != null ? " (" + err + ")" : ""),-44} {raw}");
            lib = ModLibrary.Load(data);
            Expect("New Mod: under the top lock", "[B]JINAEDFC{G}{H}");
            // Tags and a lock follow a rename in the editor.
            if (!Lock("J")) fails++;
            ModLibrary.AddTag(M("J"), "costume"); ModLibrary.AddTag(M("E"), "x-men");
            Expect("lock J under B, tags", "[B][J]INAEDFC{G}{H}");
            var draft = ModDraft.From(M("J")); draft.Name = "J2";
            if (ModWriter.Save(lib, draft, M("J"), out string? err2) == null) { fails++; Console.WriteLine("  FAIL rename: " + err2); }
            lib = ModLibrary.Load(data);
            bool renamed = lib.Find("J2") is Mod j2 && j2.Lock == ModLock.Top && j2.UserTags.SequenceEqual(["costume"]) && M("E").UserTags.SequenceEqual(["x-men"]);
            if (!renamed) fails++;
            Console.WriteLine($"  {(renamed ? "ok  " : "FAIL")} rename J to J2: lock and tag follow it");
            // Export: the mod's own tags and note travel; the user's are added when asked; a legacy copy has neither.
            {
                var j = M("J2");
                j.LocalNote = "my note"; lib.SaveState();
                var d2 = ModDraft.From(j); d2.Tags = ["Author Tag"]; d2.Notes = "The author's note."; d2.PreviewImage = "game:store_test";
                d2.PreviewViews = new() { ["mesh:a.upk|author"] = [1, 2, 3, 4, 5, 6] };
                d2.PreviewLight = 1.3f;
                ModWriter.Save(lib, d2, j, out _);
                lib = ModLibrary.Load(data);
                j = M("J2");
                string zip = Path.Combine(data, "export.zip"), zipL = Path.Combine(data, "export_legacy.zip");
                string zipA = Path.Combine(data, "export_author.zip");
                ModInstaller.Export(j, zipA);   // nothing of the user's: the author's light goes as it is
                ModInstaller.Export(j, zip, false, j.UserTags, j.LocalNote, "mesh:a.upk|mine@run", new() { ["mesh:a.upk|mine"] = [7, 0.2f, 1.5f, 0, 0, 0] }, 1.6f);
                ModInstaller.Export(j, zipL, legacy: true);
                ModManifest Read(string z) { using var a = System.IO.Compression.ZipFile.OpenRead(z); using var r = new StreamReader(a.GetEntry("manifest.json")!.Open()); return System.Text.Json.JsonSerializer.Deserialize<ModManifest>(r.ReadToEnd(), ModManifest.Json)!; }
                var full = Read(zip); var leg = Read(zipL);
                bool travels = j.ModTags.SequenceEqual(["Author Tag"]) && j.Note == "my note" && j.Manifest.Notes == "The author's note."
                            && full.Tags != null && full.Tags.SequenceEqual(["Author Tag", "costume"]) && full.Notes == "my note"
                            && leg.Tags == null && leg.Notes == null
                            && full.PreviewImage == "mesh:a.upk|mine@run" && leg.PreviewImage == null
                            && j.Manifest.PreviewViews is { } kept && kept.ContainsKey("mesh:a.upk|author")
                            && full.PreviewViews is { } fv && fv.ContainsKey("mesh:a.upk|author") && fv["mesh:a.upk|mine"][0] == 7 && leg.PreviewViews == null
                            && j.Manifest.PreviewLight == 1.3f && Read(zipA).PreviewLight == 1.3f && full.PreviewLight == 1.6f && leg.PreviewLight == null
                            && !File.ReadAllText(Path.Combine(j.Folder, "manifest.json")).Contains("my note");
                if (!travels) fails++;
                Console.WriteLine($"  {(travels ? "ok  " : "FAIL")} export: mod tags / note / preview / 3D views / light travel, yours on request, legacy has none");
                bool lockKept = j.Lock == ModLock.Top && j.UserTags.SequenceEqual(["costume"]);
                if (!lockKept) fails++;
                Console.WriteLine($"  {(lockKept ? "ok  " : "FAIL")} edit keeps lock and your tags");
                j.LocalNote = null;
                var d3 = ModDraft.From(j); d3.Tags = []; d3.Notes = "";
                ModWriter.Save(lib, d3, j, out _);
                lib = ModLibrary.Load(data);
                File.Delete(zip); File.Delete(zipL);
            }
            // Updating: the same name installed again replaces in place (asked); "Update from a file" with another name too.
            {
                var j = M("J2");
                j.LocalNote = "keep me"; lib.SaveState();
                lib = ModLibrary.Load(data); j = M("J2");
                int prio = j.Priority;
                string zipName = ModInstaller.ZipName(j);
                string src2 = Path.Combine(data + "_upd", "J2");
                Directory.CreateDirectory(src2);
                File.WriteAllBytes(Path.Combine(src2, "Dummy.upk"), [9, 9, 9, 9]);
                File.WriteAllText(Path.Combine(src2, "manifest.json"), "{ \"Name\": \"J2\", \"Version\": \"2.0\", \"UpkReplacements\": [\"Dummy.upk\"] }");
                var log2 = new List<string>();
                ModInstaller.Install(src2, lib, log2);   // no answer: refused
                lib = ModLibrary.Load(data);
                bool refused = M("J2").Manifest.Version != "2.0";
                bool asked = false;
                ModInstaller.Install(src2, lib, log2, (ex, inc) => { asked = ex.FolderName == "J2" && inc.Version == "2.0"; return true; });
                lib = ModLibrary.Load(data); j = M("J2");
                bool kept = asked && refused && j.Manifest.Version == "2.0" && j.Priority == prio && j.Lock == ModLock.Top && j.UserTags.SequenceEqual(["costume"]) && j.LocalNote == "keep me"
                            && File.ReadAllBytes(Path.Combine(j.Folder, "Dummy.upk")).Length == 4 && !Directory.Exists(j.Folder + ".new");
                if (!kept) { fails++; log2.ForEach(x => Console.WriteLine("    " + x)); }
                Console.WriteLine($"  {(kept ? "ok  " : "FAIL")} update in place: asked, version 2.0, same place, lock, tags, note");
                // Another name, into this mod.
                string src3 = Path.Combine(data + "_upd", "Other");
                Directory.CreateDirectory(src3);
                File.WriteAllBytes(Path.Combine(src3, "Dummy.upk"), [7, 7, 7, 7, 7]);
                File.WriteAllText(Path.Combine(src3, "manifest.json"), "{ \"Name\": \"J2 Renamed\", \"Version\": \"3\", \"UpkReplacements\": [\"Dummy.upk\"] }");
                ModInstaller.Install(src3, lib, log2, (_, _) => true, into: j);
                lib = ModLibrary.Load(data); j = M("J2");
                bool into = j.Name == "J2 Renamed" && j.Manifest.Version == "3" && j.Priority == prio && j.Lock == ModLock.Top && lib.Mods.Count(x => x.Name.StartsWith("J2")) == 1;
                if (!into) fails++;
                Console.WriteLine($"  {(into ? "ok  " : "FAIL")} update from a file with another name: same folder and place, new name");
                bool zn = zipName == "J2 - v1.0.zip" && ModInstaller.ZipName(j) == "J2 Renamed - v3.zip" && ModInstaller.ZipName(j, legacy: true) == "J2 Renamed - v3 (legacy).zip";
                if (!zn) { fails++; Console.WriteLine($"    names: {zipName} / {ModInstaller.ZipName(j)}"); }
                Console.WriteLine($"  {(zn ? "ok  " : "FAIL")} export file name: <name> - v<version>.zip");
                Directory.Delete(data + "_upd", true);
                j.LocalNote = null; lib.SaveState();
            }
            // The post: kept in the mod (Post\), through an edit and an update in place; out of the zip, written beside it.
            {
                var j = M("J2");
                string shot = Path.Combine(data, "shot.png");
                using (var bmp = new System.Drawing.Bitmap(8, 8)) bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                ModPost.Write(j.Folder, "NEXUS TEXT", "DISCORD TEXT", [shot]);
                var (n1, d1, i1) = ModPost.Read(j.Folder);
                bool stored = n1 == "NEXUS TEXT" && d1 == "DISCORD TEXT" && i1.Count == 1 && Path.GetFileName(i1[0]) == "shot.png";
                var pd = ModDraft.From(j); pd.Description = "Edited.";
                ModWriter.Save(lib, pd, j, out _);
                lib = ModLibrary.Load(data); j = M("J2");
                var (n2, _, i2) = ModPost.Read(j.Folder);
                bool keptEdit = n2 == "NEXUS TEXT" && i2.Count == 1 && File.Exists(i2[0]);
                string zip = Path.Combine(data, "post_export.zip");
                ModInstaller.Export(j, zip);
                string beside = ModPost.WriteBeside(j, zip);
                bool zipClean;
                using (var z = System.IO.Compression.ZipFile.OpenRead(zip)) zipClean = !z.Entries.Any(e => e.FullName.StartsWith("Post/", StringComparison.OrdinalIgnoreCase) || e.FullName.StartsWith("Post\\", StringComparison.OrdinalIgnoreCase));
                bool besideOk = File.ReadAllText(Path.Combine(beside, "nexus.txt")) == "NEXUS TEXT" && File.ReadAllText(Path.Combine(beside, "discord.md")) == "DISCORD TEXT"
                                && File.Exists(Path.Combine(beside, "Images", "shot.png")) && Path.GetFileName(beside) == "post_export - Post";
                // Update in place from a copy without a post: the saved post stays.
                string psrc = Path.Combine(data + "_upd2", "J2");
                Directory.CreateDirectory(psrc);
                File.WriteAllBytes(Path.Combine(psrc, "Dummy.upk"), [1, 2, 3]);
                File.WriteAllText(Path.Combine(psrc, "manifest.json"), "{ \"Name\": \"J2 Renamed\", \"Version\": \"4\", \"UpkReplacements\": [\"Dummy.upk\"] }");
                ModInstaller.Install(psrc, lib, new List<string>(), (_, _) => true, into: j);
                Directory.Delete(data + "_upd2", true);
                lib = ModLibrary.Load(data); j = M("J2");
                var (n3, _, i3) = ModPost.Read(j.Folder);
                bool keptUpdate = j.Manifest.Version == "4" && n3 == "NEXUS TEXT" && i3.Count == 1;
                foreach (var (ok, what) in new[] { (stored, "post saved in the mod (text and image)"), (keptEdit, "post kept through an edit"), (zipClean, "post left out of the zip"),
                                                   (besideOk, "post written beside the zip (texts and images)"), (keptUpdate, "post kept through an update in place") })
                {
                    if (!ok) fails++;
                    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
                }
                ModPost.Write(j.Folder, null, null, []);
                File.Delete(zip); Directory.Delete(beside, true); File.Delete(shot);
            }
            Lock("J2"); ModLibrary.RemoveTag(M("J2"), "costume"); ModLibrary.RemoveTag(M("E"), "x-men"); lib.SaveState();
            Directory.Move(Path.Combine(data, "mods", "J2"), Path.Combine(data, "mods", "J"));
            lib.State.ModOrder = lib.State.ModOrder.Select(n => n == "J2" ? "J" : n).ToList();
            File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(lib.State, ModManifest.Json));
            lib = ModLibrary.Load(data);
            // A mod appended at the bottom (as capture does) lands above the bottom lock.
            Directory.CreateDirectory(Path.Combine(data, "mods", "K"));
            File.WriteAllText(Path.Combine(data, "mods", "K", "manifest.json"), "{ \"Name\": \"K\" }");
            lib.State.ModOrder = [.. lib.Mods.Select(m => m.FolderName), "K"];
            lib.State.ApplyLocks();
            raw = string.Concat(lib.State.ModOrder);
            if (raw != "BJINAEDFCKGH") fails++;
            Console.WriteLine($"  {(raw == "BJINAEDFCKGH" ? "ok  " : "FAIL")} {"added at the bottom: above the bottom lock",-44} {raw}");
            File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(lib.State, ModManifest.Json));
            lib = ModLibrary.Load(data);
            Lock("B"); Lock("G"); Lock("H");
            Expect("unlock all", "BJINAEDFCKGH");
            bool clean = !File.ReadAllText(Path.Combine(data, "state.json")).Contains("Locked");
            if (!clean) fails++;
            Console.WriteLine($"  {(clean ? "ok  " : "FAIL")} no lock fields left in state.json");
            foreach (var (input, want) in new[]
            {
                ("●  Game not running", "●  Game Not Running"),
                ("No conflicts  ·  the game matches your list", "No Conflicts  ·  The Game Matches Your List"),
                ("3 file(s) to change", "3 File(s) to Change"),
                ("turn off \"storm classic\"", "Turn Off \"storm classic\""),
                ("Undone: tag \"Storm\" as \"x-men\"", "Undone: Tag \"Storm\" as \"x-men\""),
                ("MHModManager's own folder: read-only here (Settings → Migrate)", "MHModManager's Own Folder: Read-Only Here (Settings → Migrate)"),
                ("save as .dds or .png", "Save as .dds or .png"),
            })
            {
                string got = MhoExtendedModManager.Gui.Ui.TitleCase(input);
                bool ok = got == want;
                if (!ok) fails++;
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} Title Case: {got}{(ok ? "" : "   expected " + want)}");
            }
            Console.WriteLine(fails == 0 ? "All lock checks passed." : $"{fails} lock check(s) FAILED.");
            return fails == 0 ? 0 : 1;
        }
        finally { try { Directory.Delete(data, true); } catch (IOException) { } }
    }
}
