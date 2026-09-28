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

            // A page with a default and a variant side by side (Rogue #300, real file names): each copy follows its own file.
            const string Def = "Rogue Classic 90's Costume Visual Update", Var = "Rogue Classic 90's Costume Visual Update (Variant)";
            var rogue = new Nexus.ModInfo(300, "Rogue 90's X-Men Costume Visual Update", "5", 0, true, [
                new Nexus.NexusFile(727, Var, "4", "OLD_VERSION", 1789533863, "v4.zip"), new Nexus.NexusFile(726, Def, "4", "OLD_VERSION", 1789533826, "d4.zip"),
                new Nexus.NexusFile(781, Var, "5", "MAIN", 1790611272, "v5.zip"), new Nexus.NexusFile(782, Def, "5", "MAIN", 1790611297, "d5.zip")], []);
            Check("two files on the page", Nexus.Lines(rogue).Count == 2);
            Check("the variant copy (installed file known) updates to the variant", Nexus.Latest(rogue, Nexus.LineFor(new NexusLink { ModId = 300, FileId = 727, FromNexus = true }, rogue))?.FileId == 781);
            Check("the default copy updates to the default", Nexus.Latest(rogue, Nexus.LineFor(new NexusLink { ModId = 300, FileId = 726, FromNexus = true }, rogue))?.FileId == 782);
            Check("linked by name: \"Variant\" in the mod's name picks the variant", Nexus.LineFor(new NexusLink { ModId = 300 }, rogue, "Rogue 90s VU Variant") == Var);
            Check("linked by name without a telling word: unknown, so the user is asked", Nexus.LineFor(new NexusLink { ModId = 300 }, rogue, "Rogue 90s") == null && Nexus.NeedsChoice(rogue, null));
            var wrong = new NexusLink { ModId = 300, FileId = 782, FromNexus = true, File = Var };   // updated to the default by mistake, then the variant chosen
            Check("a copy switched to the variant is offered the variant", Nexus.UpdateFor(wrong, rogue, "5", null, Nexus.LineFor(wrong, rogue)) == "5" && Nexus.Latest(rogue, Nexus.LineFor(wrong, rogue))?.FileId == 781);
            var right = new NexusLink { ModId = 300, FileId = 781, FromNexus = true, File = Var };
            Check("the variant's own newest file is up to date", Nexus.UpdateFor(right, rogue, "5", null, Nexus.LineFor(right, rogue)) == null);

            // Sign in with Nexus (OAuth 2.0 + PKCE) against a local stand-in for users.nexusmods.com and the v1 API.
            Check("PKCE: the RFC 7636 example challenge", NexusAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
            fails += SignInTest(root, m, cache, v2, Check);
            {
                // Nexus's real public key (built in) reads, and refuses a token it didn't sign.
                string? why = null;
                try { NexusAuth.ReadToken("eyJhbGciOiJSUzI1NiJ9.eyJ1c2VyIjp7InVzZXJuYW1lIjoieCJ9fQ.AAAA"); }
                catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
                Check("Nexus's built-in public key reads and refuses a forged token", why != null && why.Contains("isn't signed by Nexus"));
            }

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

    /// <summary>
    /// The sign-in, refresh, Premium download and sign-out against a stand-in server (localhost): it answers authorize
    /// with a redirect to the app's callback, only gives tokens for a matching PKCE verifier, signs them with a test key,
    /// and gives a download link only for the current access token. Returns extra failures (0: all well).
    /// </summary>
    static int SignInTest(string root, Mod m, NexusCache cache, string v2Zip, Action<string, bool> Check)
    {
        int port = 45000 + Random.Shared.Next(2000);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string Jwt(string user, bool premium, System.Security.Cryptography.RSA key)
        {
            string h = B64(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            string p = B64(System.Text.Encoding.UTF8.GetBytes($"{{\"user\":{{\"username\":\"{user}\",\"membership_roles\":[\"member\"{(premium ? ",\"premium\"" : "")}]}},\"exp\":{DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds()}}}"));
            return h + "." + p + "." + B64(key.SignData(System.Text.Encoding.ASCII.GetBytes(h + "." + p), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        }
        string? challenge = null, current = null; bool refuseRefresh = false, revoked = false; int tokensIssued = 0;
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        var loop = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var q = ctx.Request.QueryString; string path = ctx.Request.Url!.AbsolutePath; int status = 200; string body = "";
                var form = ctx.Request.HttpMethod == "POST" ? System.Web.HttpUtility.ParseQueryString(new StreamReader(ctx.Request.InputStream).ReadToEnd()) : null;
                if (path == "/oauth/authorize")
                {
                    challenge = q["code_challenge"];
                    bool okReq = q["client_id"] == "test-client" && q["code_challenge_method"] == "S256" && q["redirect_uri"] == NexusAuth.RedirectUri;
                    ctx.Response.Redirect($"{q["redirect_uri"]}?{(okReq ? "code=abc" : "error=bad_request")}&state={Uri.EscapeDataString(q["state"] ?? "")}");
                    status = 302;
                }
                else if (path == "/oauth/token" && form != null)
                {
                    if (form["grant_type"] == "authorization_code" && form["code"] == "abc" && challenge != null && NexusAuth.Challenge(form["code_verifier"] ?? "") == challenge)
                    { current = Jwt("Tester", true, rsa); tokensIssued++; body = $"{{\"access_token\":\"{current}\",\"refresh_token\":\"r1\",\"expires_in\":1}}"; }
                    else if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "r1" && !refuseRefresh)
                    { current = Jwt("Tester", true, rsa); tokensIssued++; body = $"{{\"access_token\":\"{current}\",\"refresh_token\":\"r1\",\"expires_in\":1}}"; }
                    else { status = 400; body = "{\"error\":\"invalid_grant\"}"; }
                }
                else if (path == "/oauth/revoke") revoked = true;
                else if (path == $"/v1/games/{Nexus.Game}/mods/176/files/1002/download_link.json")
                {
                    if (ctx.Request.Headers["Authorization"] == "Bearer " + current) body = $"[{{\"name\":\"Test CDN\",\"URI\":{System.Text.Json.JsonSerializer.Serialize(v2Zip)}}}]";
                    else status = 401;
                }
                else status = 404;
                ctx.Response.StatusCode = status;
                if (status != 302) { var bytes = System.Text.Encoding.UTF8.GetBytes(body); ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(bytes); }
                ctx.Response.Close();
            }
        });
        var env = new Dictionary<string, string?>
        {
            ["MHO_EXTMM_NEXUS_AUTH"] = $"http://localhost:{port}", ["MHO_EXTMM_NEXUS_V1"] = $"http://localhost:{port}/v1/",
            ["MHO_EXTMM_NEXUS_CLIENT"] = "test-client", ["MHO_EXTMM_NEXUS_JWTKEY"] = rsa.ExportSubjectPublicKeyInfoPem(),
        };
        var old = env.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
        string home = Path.Combine(root, "home");
        int fails = 0;
        void C(string what, bool ok) { if (!ok) fails++; Check(what, ok); }
        try
        {
            // The "browser": follows the authorize redirect to the app's callback, without waiting for it.
            using var browser = new HttpClient();
            var login = NexusAuth.SignIn(home, url => { _ = browser.GetAsync(url); return Task.CompletedTask; }).GetAwaiter().GetResult();
            C("sign in: PKCE code exchanged, token checked: Tester (Premium)", login.UserName == "Tester" && login.Premium && tokensIssued == 1);
            C("sign-in kept on this PC, encrypted (no token readable in the file)", NexusAuth.Load(home)?.UserName == "Tester" &&
                !File.ReadAllText(Path.Combine(home, "nexus_login.dat")).Contains(login.RefreshToken));
            using (var other = System.Security.Cryptography.RSA.Create(2048))
            {
                bool rejected = false;
                try { NexusAuth.ReadToken(Jwt("Mallory", true, other)); } catch (Nexus.NexusException) { rejected = true; }
                C("a token not signed with Nexus's key is refused", rejected);
            }
            Thread.Sleep(1500);   // the access token (1 s) has expired
            string? token = NexusAuth.AccessToken(home).GetAwaiter().GetResult();
            C("expired token refreshed", token != null && tokensIssued == 2 && token == current);
            var (path, file) = NexusUpdates.DownloadLatest(m, token!, cache, root, null).GetAwaiter().GetResult();
            C("Premium one-click: download link for the signed-in user, file 1002 downloaded", file.FileId == 1002 && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(v2Zip)));
            bool refusedWithout = false;
            try { Nexus.DownloadLink("not-a-token", 176, 1002).GetAwaiter().GetResult(); } catch (NexusAuth.SignedOutException) { refusedWithout = true; }
            C("no download link without a valid sign-in", refusedWithout);
            Thread.Sleep(1500); refuseRefresh = true;
            C("a refused refresh signs out", NexusAuth.AccessToken(home).GetAwaiter().GetResult() == null && NexusAuth.Load(home) == null);
            refuseRefresh = false;
            NexusAuth.SignIn(home, url => { _ = browser.GetAsync(url); return Task.CompletedTask; }).GetAwaiter().GetResult();
            NexusAuth.SignOut(home).GetAwaiter().GetResult();
            C("sign out: login removed here and revoked at Nexus", NexusAuth.Load(home) == null && revoked);
        }
        catch (Exception ex) { C("sign-in test ran: " + ex.GetType().Name + ": " + ex.Message, false); }
        finally
        {
            foreach (var (k, v) in old) Environment.SetEnvironmentVariable(k, v);
            server.Stop();
        }
        return fails;
    }

    static Nexus.ModInfo Info(string version) =>
        new(1, "x", version, 2, true, [new Nexus.NexusFile(2, "x", version, "MAIN", 2, "x.zip")], []);
}
