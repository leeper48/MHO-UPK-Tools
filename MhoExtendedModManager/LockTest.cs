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
            if (lib.Move(M("B"), 1)) fails++; Expect("move locked B: refused", "[A][B]CDEF{G}{H}");
            lib.Move(M("C"), -1); Expect("C up: stops under the top lock", "[A][B]CDEF{G}{H}");
            lib.MoveToEnd(M("C"), 1); Expect("C to bottom: above the bottom lock", "[A][B]DEFC{G}{H}");
            lib.MoveToEnd(M("E"), -1); Expect("E to top: below the top lock", "[A][B]EDFC{G}{H}");
            lib.Move(M("C"), 1); Expect("C down: stops above the bottom lock", "[A][B]EDFC{G}{H}");
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
            Console.WriteLine(fails == 0 ? "All lock checks passed." : $"{fails} lock check(s) FAILED.");
            return fails == 0 ? 0 : 1;
        }
        finally { try { Directory.Delete(data, true); } catch (IOException) { } }
    }
}
