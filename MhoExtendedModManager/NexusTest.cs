using System.IO.Compression;

namespace MhoExtendedModManager;

/// <summary>
/// --test-nexus: the Nexus chain against a local stand-in for the public API (MHO_EXTMM_NEXUS_API, a temp folder) and a
/// temp library: file names, page links, install + link (the file from the upload time in its name), update detection,
/// the update installed in place (lock and tags kept), then up to date; Find My Mods matching; no API key anywhere.
/// Nothing touches Nexus or the real library.
/// </summary>
static class NexusTest
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "mhoextmm_nexustest_" + Guid.NewGuid().ToString("N")[..8]);
        string api = Path.Combine(root, "api"), data = Path.Combine(root, "library"), files = Path.Combine(root, "files");
        int fails = 0;
        void Check(string what, bool ok) { if (!ok) fails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }
        string? oldApi = Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_API");
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "mods")); Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(data, "state.json"), "{ \"EnabledMods\": [], \"ModOrder\": [] }");

            // Names and links.
            Check("file name: Storm Classic-176-1-2-1690000000.zip → mod 176, v1.2", Nexus.FromFileName(@"C:\x\Storm Classic-176-1-2-1690000000.zip") is (176, "1.2", 1690000000));
            Check("file name with a copy number: X-12-2-0-1690000000 (1).7z → mod 12, v2.0", Nexus.FromFileName("X-12-2-0-1690000000 (1).7z") is (12, "2.0", 1690000000));
            Check("not a Nexus name: Storm Classic.zip", Nexus.FromFileName("Storm Classic.zip") == null);
            Check("page link → 176", Nexus.ParseModId("https://www.nexusmods.com/marvelheroesomega/mods/176?tab=files") == 176 && Nexus.ParseModId("176") == 176);
            Check("versions: 1.2 → 1.10 is an update; v2.0 = 2.0 isn't",
                  Nexus.UpdateFor(new NexusLink { ModId = 1, Version = "1.2", FromNexus = true }, Info("1.10"), null) == "1.10" && Nexus.UpdateFor(new NexusLink { ModId = 1, Version = "v2.0", FromNexus = true }, Info("2.0"), null) == null);

            // Two versions of a mod, as Nexus would serve them (the upload time is in each download's name).
            string Zip(string version, long uploaded)
            {
                string dir = Path.Combine(files, "v" + version); Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "Dummy.upk"), [1, 2, 3, (byte)version[0]]);
                File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{ \"Name\": \"Test Mod\", \"Version\": \"{version}\", \"UpkReplacements\": [\"Dummy.upk\"] }}");
                string zip = Path.Combine(files, $"Test Mod-176-{version.Replace('.', '-')}-{uploaded}.zip");
                ZipFile.CreateFromDirectory(dir, zip);
                return zip;
            }
            string v1 = Zip("1.0", 1690000000), v2 = Zip("2.0", 1700000000);
            void Api(string path, string json) { string f = Path.Combine(api, path.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, json); }
            Api("graphql_mods.json", "[ { \"modId\": 176, \"name\": \"Test Mod\", \"version\": \"2.0\", \"status\": \"published\", \"updatedAt\": \"2023-11-14T22:13:20Z\" } ]");
            Api("graphql_files.json", "{ \"176\": [ { \"fileId\": 1001, \"name\": \"Test Mod\", \"version\": \"1.0\", \"category\": \"OLD_VERSION\", \"date\": 1690000000, \"uri\": \"Test Mod-176-1-0-1690000000.zip\" }, " +
                "{ \"fileId\": 1002, \"name\": \"Test Mod\", \"version\": \"2.0\", \"category\": \"MAIN\", \"date\": 1700000000, \"uri\": \"Test Mod-176-2-0-1700000000.zip\" } ] }");
            Environment.SetEnvironmentVariable("MHO_EXTMM_NEXUS_API", api);

            // Install the 1.0 download: linked by its name; the upload time in the name picks file 1001.
            var cache = new NexusCache();
            var lib = ModLibrary.Load(data);
            var installed = ModInstaller.Install(v1, lib, []);
            lib = ModLibrary.Load(data);
            NexusUpdates.Link(lib, v1, installed, cache, online: true).GetAwaiter().GetResult();
            lib = ModLibrary.Load(data);
            var m = lib.Find("Test Mod")!;
            Check("installed and linked: mod 176, file 1001 (from the upload time in its name), v1.0", m.NexusLink is { ModId: 176, FileId: 1001, Version: "1.0", FromNexus: true });
            m.Lock = ModLock.Top; ModLibrary.AddTag(m, "Mine"); lib.SaveState();

            // Check (public data, no key): an update.
            var (n, problems) = NexusUpdates.Check(lib, cache).GetAwaiter().GetResult();
            Check($"checked {n} linked mod(s), {problems.Count} problem(s)", n == 1 && problems.Count == 0);
            Check("update found: v2.0", NexusUpdates.UpdateFor(m, cache) == "2.0");

            // The user downloads 2.0 from the Files page; it's installed in place and the link records file 1002.
            ModInstaller.Install(v2, ModLibrary.Load(data), [], (_, _) => true, into: m);
            lib = ModLibrary.Load(data);
            var fn = Nexus.FromFileName(v2)!.Value;
            NexusUpdates.Record(lib, m.FolderName, 176, Nexus.FileUploadedAt(cache.Mods[176], fn.Uploaded)?.FileId, fn.Version);
            lib = ModLibrary.Load(data); m = lib.Find("Test Mod")!;
            Check("updated in place to v2.0 (file 1002), lock and tag kept", m.Manifest.Version == "2.0" && m.NexusLink?.FileId == 1002 && m.Lock == ModLock.Top && m.UserTags.Contains("Mine"));
            Check("now up to date", NexusUpdates.UpdateFor(m, cache) == null);

            // Find My Mods on Nexus: the mod list (fake GraphQL answer) and the matching.
            Api("graphql_mods.json", "[ { \"modId\": 242, \"name\": \"Storm Classic Costume\", \"author\": \"WIzzerMH\", \"version\": \"1.0\", \"updatedAt\": \"2025-01-01T00:00:00Z\" }, " +
                "{ \"modId\": 300, \"name\": \"Capitan America Infinity war\", \"author\": \"Someone\", \"version\": \"1.0\" }, " +
                "{ \"modId\": 301, \"name\": \"Captain America Infinity War\", \"author\": \"Someone\", \"version\": \"1.0\" } ]");
            var all = NexusMatch.AllMods().GetAwaiter().GetResult();
            Check("mod list read: 3 mods, uploader optional", all.Count == 3 && all[0].Author == "WIzzerMH");
            Check("names: VU, filler and versions ignored", NexusMatch.Words("Storm Classic VU v1.2").SetEquals(["storm", "classic"]) && NexusMatch.NameScore("Storm Classic VU", "Storm Classic Costume") == 1);
            Check("author: Wlzzer ~ WIzzerMH", NexusMatch.SameAuthor("Wlzzer", all[0]));
            var storm = new Mod { Folder = root, FolderName = "Storm", Manifest = new ModManifest { Name = "Storm Classic VU", Author = "Wlzzer" } };
            var sc = NexusMatch.Candidates(storm, all);
            Check("Storm Classic VU → #242, confident", sc.Count > 0 && sc[0].Mod.ModId == 242 && NexusMatch.Confident(sc));
            var star = new Mod { Folder = root, FolderName = "Star", Manifest = new ModManifest { Name = "Starlord Infinity War", Author = "x" } };
            var ss = NexusMatch.Candidates(star, all);
            Check("Starlord Infinity War: Captain America's page isn't offered", !ss.Any(c => c.Mod.ModId == 301));

            // Linked by name (Find My Mods): which file the user has is unknown, so only uploads after the link are updates
            // (Miles Morales: local v0.1 vs Nexus's older, unrelated v2 is not an update).
            var older = new Nexus.ModInfo(49, "x", "2", 1000, true, [new Nexus.NexusFile(82, "x", "2", "MAIN", 1742538608, "x.zip")], []);
            var byName = new NexusLink { ModId = 49, Version = "0.1", Installed = new DateTime(2026, 9, 27) };
            Check("linked by name: an older Nexus v2 isn't an update of v0.1 made later", Nexus.UpdateFor(byName, older, "0.1", new DateTime(2026, 5, 19)) == null);
            Check("linked by name: a higher version uploaded after the user's copy is", Nexus.UpdateFor(byName, older, "0.1", new DateTime(2025, 1, 1)) == "2");
            var newer = older with { Files = [new Nexus.NexusFile(83, "x", "3", "MAIN", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "x.zip")] };
            Check("linked by name: a file uploaded after the link is", Nexus.UpdateFor(byName, newer, "0.1") == "3");
            byName.Ignore = 83;
            Check("an ignored file isn't", Nexus.UpdateFor(byName, newer, "0.1") == null);

            // Nexus doesn't allow apps to ask for personal API keys (2026-09-28): none is asked for, stored or sent.
            Check("no API key in the settings", typeof(Settings).GetProperties().All(pr => !pr.Name.Contains("ApiKey") && !pr.Name.Contains("Premium")));
            var old = System.Text.Json.JsonSerializer.Deserialize<Settings>("{ \"NexusApiKey\": \"secret\", \"NexusAccount\": \"x\" }")!;
            Check("an old settings.json's key is dropped when saved", !System.Text.Json.JsonSerializer.Serialize(old).Contains("secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MHO_EXTMM_NEXUS_API", oldApi);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(fails == 0 ? "All Nexus checks passed." : $"{fails} Nexus check(s) FAILED.");
        return fails == 0 ? 0 : 1;
    }

    static Nexus.ModInfo Info(string version) =>
        new(1, "x", version, 2, true, [new Nexus.NexusFile(2, "x", version, "MAIN", 2, "x.zip")], []);
}
