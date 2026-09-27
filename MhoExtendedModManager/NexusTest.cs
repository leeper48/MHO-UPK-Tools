using System.IO.Compression;

namespace MhoExtendedModManager;

/// <summary>
/// --test-nexus: the Nexus chain against a local stand-in for the API (MHO_EXTMM_NEXUS_API, a temp folder) and a temp
/// library: file names, page links, install + link (by name and MD5), update detection, a Premium download that updates
/// the mod in place (lock and tags kept), then up to date. Nothing touches Nexus or the real library.
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
            Check("file name: Storm Classic-176-1-2-1690000000.zip → mod 176, v1.2", Nexus.FromFileName(@"C:\x\Storm Classic-176-1-2-1690000000.zip") is (176, "1.2"));
            Check("file name with a copy number: X-12-2-0-1690000000 (1).7z → mod 12, v2.0", Nexus.FromFileName("X-12-2-0-1690000000 (1).7z") is (12, "2.0"));
            Check("not a Nexus name: Storm Classic.zip", Nexus.FromFileName("Storm Classic.zip") == null);
            Check("page link → 176", Nexus.ParseModId("https://www.nexusmods.com/marvelheroesomega/mods/176?tab=files") == 176 && Nexus.ParseModId("176") == 176);
            Check("versions: 1.2 → 1.10 is an update; v2.0 = 2.0 isn't",
                  Nexus.UpdateFor(new NexusLink { ModId = 1, Version = "1.2", FromNexus = true }, Info("1.10"), null) == "1.10" && Nexus.UpdateFor(new NexusLink { ModId = 1, Version = "v2.0", FromNexus = true }, Info("2.0"), null) == null);

            // Two versions of a mod, as Nexus would serve them.
            string Zip(string version)
            {
                string dir = Path.Combine(files, "v" + version); Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "Dummy.upk"), [1, 2, 3, (byte)version[0]]);
                File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{ \"Name\": \"Test Mod\", \"Version\": \"{version}\", \"UpkReplacements\": [\"Dummy.upk\"] }}");
                string zip = Path.Combine(files, $"Test Mod-176-{version.Replace('.', '-')}-1690000000.zip");
                ZipFile.CreateFromDirectory(dir, zip);
                return zip;
            }
            string v1 = Zip("1.0"), v2 = Zip("2.0");
            string md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(v1))).ToLowerInvariant();
            void Api(string path, string json) { string f = Path.Combine(api, path.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, json); }
            Api("users/validate.json", "{ \"name\": \"Tester\", \"is_premium\": true }");
            Api($"games/{Nexus.Game}/mods/176.json", "{ \"name\": \"Test Mod\", \"version\": \"2.0\", \"updated_timestamp\": 2000, \"available\": true }");
            Api($"games/{Nexus.Game}/mods/176/files.json", "{ \"files\": [ { \"file_id\": 1001, \"name\": \"Test Mod\", \"version\": \"1.0\", \"category_name\": \"OLD_VERSION\", \"uploaded_timestamp\": 1000, \"file_name\": \"Test Mod-176-1-0-1690000000.zip\" }, " +
                "{ \"file_id\": 1002, \"name\": \"Test Mod\", \"version\": \"2.0\", \"category_name\": \"MAIN\", \"uploaded_timestamp\": 2000, \"file_name\": \"Test Mod-176-2-0-1690000000.zip\" } ], \"file_updates\": [ { \"old_file_id\": 1001, \"new_file_id\": 1002 } ] }");
            Api($"games/{Nexus.Game}/mods/md5_search/{md5}.json", $"[ {{ \"mod\": {{ \"mod_id\": 176, \"domain_name\": \"{Nexus.Game}\" }}, \"file_details\": {{ \"file_id\": 1001, \"version\": \"1.0\" }} }} ]");
            Api($"games/{Nexus.Game}/mods/176/files/1002/download_link.json", $"[ {{ \"name\": \"Test CDN\", \"URI\": {System.Text.Json.JsonSerializer.Serialize(v2)} }} ]");
            Environment.SetEnvironmentVariable("MHO_EXTMM_NEXUS_API", api);

            var account = Nexus.Validate("test-premium").GetAwaiter().GetResult();
            Check("account: Tester, Premium", account.Name == "Tester" && account.Premium);
            bool refused = false;
            try { Nexus.Validate("wrong").GetAwaiter().GetResult(); } catch (Nexus.NexusException) { refused = true; }
            Check("a wrong key is refused", refused);

            // Install the 1.0 download: linked by its name and MD5.
            var lib = ModLibrary.Load(data);
            var installed = ModInstaller.Install(v1, lib, []);
            lib = ModLibrary.Load(data);
            NexusUpdates.Link(lib, v1, installed, "test-key").GetAwaiter().GetResult();
            lib = ModLibrary.Load(data);
            var m = lib.Find("Test Mod")!;
            Check("installed and linked: mod 176, file 1001 (MD5), v1.0", m.NexusLink is { ModId: 176, FileId: 1001, Version: "1.0" });
            m.Lock = ModLock.Top; ModLibrary.AddTag(m, "Mine"); lib.SaveState();

            // Check: an update.
            var cache = new NexusCache();
            var (n, problems) = NexusUpdates.Check(lib, "test-key", cache).GetAwaiter().GetResult();
            Check($"checked {n} linked mod(s), {problems.Count} problem(s)", n == 1 && problems.Count == 0);
            Check("update found: v2.0", NexusUpdates.UpdateFor(m, cache) == "2.0");

            // Premium: download and update in place.
            var (path, file) = NexusUpdates.DownloadLatest(m, "test-premium", cache, root, null).GetAwaiter().GetResult();
            ModInstaller.Install(path, ModLibrary.Load(data), [], (_, _) => true, into: m);
            lib = ModLibrary.Load(data);
            NexusUpdates.Record(lib, m.FolderName, 176, file.FileId, file.Version);
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

            // The key is kept encrypted, and reads back.
            string? stored = Nexus.Protect("abc-123");
            Check("API key stored encrypted and read back", stored != null && !stored.Contains("abc") && Nexus.Unprotect(stored) == "abc-123");
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
