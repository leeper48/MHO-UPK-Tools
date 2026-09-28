using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// Signing in with Nexus Mods (OAuth 2.0 + PKCE, as Nexus's guide describes: modding.wiki/en/api/oauth2-guide), for
/// one-click updates: Nexus only gives download links to Premium members, and only to registered apps a user has signed
/// in to (2026-09-28: Nexus's review doesn't allow personal API keys). Without signing in, everything else works on
/// public data, and updates go through the mod's Files page.
///
/// Flow: a random verifier and its S256 challenge; the browser opens users.nexusmods.com/oauth/authorize; Nexus sends
/// the user back to http://127.0.0.1:31985/callback (the address registered with Nexus), where a small loopback
/// listener (no admin rights, nothing reachable from outside) takes the code; the code and the verifier are exchanged
/// for tokens; the access token (a JWT) is checked against Nexus's public key and read for the user's name and Premium.
/// The tokens are kept on this PC only, encrypted for the Windows user (data\nexus_login.dat), refreshed when they
/// expire, and revoked at Nexus on Sign Out.
///
/// Tests: MHO_EXTMM_NEXUS_AUTH (a stand-in for users.nexusmods.com), MHO_EXTMM_NEXUS_CLIENT (a client ID),
/// MHO_EXTMM_NEXUS_JWTKEY (the public key the stand-in signs with).
/// </summary>
static class NexusAuth
{
    /// <summary>The client ID Nexus issues when the app is registered (empty: sign-in isn't offered yet).</summary>
    public const string ClientId = "";
    public const int CallbackPort = 31985;
    public static string RedirectUri => $"http://127.0.0.1:{CallbackPort}/callback";
    static string AuthBase => Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_AUTH") ?? "https://users.nexusmods.com";
    static string Client => Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_CLIENT") ?? ClientId;
    /// <summary>Sign-in can be offered (the app has a client ID).</summary>
    public static bool Available => Client.Length > 0;

    // Nexus's public key for its tokens (from the guide above).
    const string NexusPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAs/57oX8HW8xC+W/etH7J
        PgoSTiGPKZa6Gq3/K/7GgrpJcZhPdr9MTGocb2uLzQBJW+u1XpSgyeKH4JCxxeHF
        3zcUtb7SUg3KnxlR5QUmOnqBvbUuL4opUpfgWUGltASduYqZBJD2WTK8Hvwh9X1v
        ACeqp1zgorZm3f0J2H15TDbzIp9ihCFuthJUFumdzvrt/WvimW2fiyqndTNQwe5h
        XM8hj8cemdWQXCd99qnj7UQkpu+yNisVMHQCsAqXITe6Ehp6IY9eCd4DJKjDvyLc
        3vbY8UL+bcVK5tYAKemZ56uw3q1YdcyqGlItyLi4j4EISdBQaCCqT7YZUhMYzhUd
        1QIDAQAB
        -----END PUBLIC KEY-----
        """;
    static string PublicKey => Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_JWTKEY") ?? NexusPublicKey;

    public sealed class Login
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public string UserName { get; set; } = "";
        public bool Premium { get; set; }
    }

    // ---- PKCE (RFC 7636)

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] FromB64Url(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)); }
    /// <summary>The S256 challenge for a verifier.</summary>
    public static string Challenge(string verifier) => B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    static string NewVerifier() => B64Url(RandomNumberGenerator.GetBytes(64));   // 86 characters

    // ---- sign in / out

    /// <summary>
    /// Signs in: opens the browser (<paramref name="openBrowser"/>) at Nexus's authorize page and waits (up to 5
    /// minutes) for Nexus to send the user back to the callback address. Saves and returns the login.
    /// </summary>
    public static async Task<Login> SignIn(string home, Func<string, Task> openBrowser, CancellationToken cancel = default)
    {
        if (!Available) throw new Nexus.NexusException("Signing in with Nexus isn't available in this version yet.");
        string verifier = NewVerifier(), state = B64Url(RandomNumberGenerator.GetBytes(16));
        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try { listener.Start(); }
        catch (SocketException) { throw new Nexus.NexusException($"The sign-in reply address (port {CallbackPort} on this PC) is in use by another program. Close it and try again."); }
        try
        {
            string url = AuthBase + "/oauth/authorize?" + Form(new()
            {
                ["client_id"] = Client, ["response_type"] = "code", ["scope"] = "", ["redirect_uri"] = RedirectUri,
                ["state"] = state, ["code_challenge_method"] = "S256", ["code_challenge"] = Challenge(verifier),
            });
            await openBrowser(url);
            var query = await WaitForCallback(listener, state, cancel);
            string? code = query.GetValueOrDefault("code");
            if (query.GetValueOrDefault("state") != state || string.IsNullOrEmpty(code))
                throw new Nexus.NexusException(query.GetValueOrDefault("error_description") ?? query.GetValueOrDefault("error") ?? "Signing in was cancelled.");
            var login = await Tokens(new()
            {
                ["grant_type"] = "authorization_code", ["redirect_uri"] = RedirectUri, ["client_id"] = Client,
                ["code"] = code, ["code_verifier"] = verifier, ["scope"] = "",
            });
            Save(home, login);
            return login;
        }
        finally { listener.Stop(); }
    }

    /// <summary>Waits for the browser's request to /callback, answers it with a short page, returns its query.</summary>
    static async Task<Dictionary<string, string>> WaitForCallback(TcpListener listener, string state, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new Nexus.NexusException(cancel.IsCancellationRequested ? "Signing in was cancelled." : "No answer from the browser within 5 minutes; signing in was cancelled."); }
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var buf = new byte[8192]; int n = 0, r;
                while (n < buf.Length && (r = await stream.ReadAsync(buf.AsMemory(n, buf.Length - n), timeout.Token)) > 0)
                {
                    n += r;
                    if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                }
                string first = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];   // GET /callback?code=…&state=… HTTP/1.1
                string target = first.Split(' ').ElementAtOrDefault(1) ?? "";
                if (!target.StartsWith("/callback", StringComparison.Ordinal))
                {
                    await Answer(stream, 404, "Not found.");   // e.g. the browser asking for a favicon
                    continue;
                }
                var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
                bool ok = query.GetValueOrDefault("state") == state && query.ContainsKey("code");
                await Answer(stream, 200, ok ? "You're signed in to MHO Extended Mod Manager. You can close this tab and go back to the app."
                                             : "Signing in didn't complete. You can close this tab and try again from the app.");
                return query;
            }
        }
    }

    static async Task Answer(NetworkStream stream, int status, string message)
    {
        string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>MHO Extended Mod Manager</title></head>" +
                      $"<body style=\"font-family:Segoe UI,sans-serif;background:#1b2330;color:#e8e8ee;padding:40px\"><h2>MHO Extended Mod Manager</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head); await stream.WriteAsync(body); await stream.FlushAsync();
    }

    static Dictionary<string, string> ParseQuery(string q) =>
        q.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
         .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    static string Form(Dictionary<string, string> d) => string.Join("&", d.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    /// <summary>A token request (code exchange or refresh); the tokens checked and read.</summary>
    static async Task<Login> Tokens(Dictionary<string, string> form)
    {
        using var h = Client0();
        using var resp = await h.PostAsync(AuthBase + "/oauth/token", new FormUrlEncodedContent(form));
        string text = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode is >= 400 and < 500) throw new SignedOutException($"Nexus didn't accept the sign-in ({(int)resp.StatusCode}).");
        resp.EnsureSuccessStatusCode();
        var j = JsonDocument.Parse(text).RootElement;
        string access = j.GetProperty("access_token").GetString() ?? "";
        var user = ReadToken(access);
        return new Login
        {
            AccessToken = access,
            RefreshToken = j.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
            ExpiresAt = DateTime.UtcNow.AddSeconds(j.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int s) ? s : 3600),
            UserName = user.Name, Premium = user.Premium,
        };
    }

    /// <summary>A token or refresh Nexus refused (e.g. the user revoked the app): the user counts as signed out.</summary>
    public sealed class SignedOutException(string message) : Nexus.NexusException(message);

    /// <summary>Checks a token's signature (RS256, Nexus's public key) and reads the user's name and Premium.</summary>
    public static (string Name, bool Premium) ReadToken(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new Nexus.NexusException("Nexus sent an unreadable sign-in token.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKey);
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new Nexus.NexusException("The sign-in token isn't signed by Nexus; not accepted.");
        var p = JsonDocument.Parse(FromB64Url(parts[1])).RootElement;
        var u = p.GetProperty("user");
        var roles = u.TryGetProperty("membership_roles", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
        return (u.TryGetProperty("username", out var n) ? n.GetString() ?? "" : "", roles.Any(x => x.Contains("premium", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A current access token (refreshed when it has expired), or null when not signed in. A refused refresh signs out.</summary>
    public static async Task<string?> AccessToken(string home)
    {
        if (Load(home) is not Login l) return null;
        if (DateTime.UtcNow < l.ExpiresAt.AddSeconds(-60)) return l.AccessToken;
        try
        {
            var fresh = await Tokens(new() { ["grant_type"] = "refresh_token", ["client_id"] = Client, ["refresh_token"] = l.RefreshToken });
            if (fresh.RefreshToken.Length == 0) fresh.RefreshToken = l.RefreshToken;
            Save(home, fresh);
            return fresh.AccessToken;
        }
        catch (SignedOutException) { Delete(home); return null; }
    }

    /// <summary>Signs out: forgets the login here and asks Nexus to revoke it.</summary>
    public static async Task SignOut(string home)
    {
        var l = Load(home);
        Delete(home);
        if (l == null || !Available) return;
        try { using var h = Client0(); using var _ = await h.PostAsync(AuthBase + "/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = l.RefreshToken, ["client_id"] = Client })); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }

    static HttpClient Client0()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MHO-Ext-ModManager/" + Program.Version);
        h.DefaultRequestHeaders.Add("Application-Name", "MHO Extended Mod Manager");
        h.DefaultRequestHeaders.Add("Application-Version", Program.Version);
        return h;
    }

    // ---- the saved login: data\nexus_login.dat, encrypted for this Windows user (DPAPI)

    static string FileOf(string home) => Path.Combine(home, "nexus_login.dat");
    public static Login? Load(string home)
    {
        try
        {
            if (!File.Exists(FileOf(home))) return null;
            var plain = Unprotect(File.ReadAllBytes(FileOf(home)));
            return plain == null ? null : JsonSerializer.Deserialize<Login>(plain);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
    static void Save(string home, Login l)
    {
        Directory.CreateDirectory(home);
        File.WriteAllBytes(FileOf(home), Protect(JsonSerializer.SerializeToUtf8Bytes(l)) ?? throw new IOException("couldn't encrypt the sign-in"));
    }
    static void Delete(string home) { try { File.Delete(FileOf(home)); } catch (IOException) { } }

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    static byte[]? Crypt(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect ? CryptProtectData(ref input, "MHO Extended Mod Manager", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
            if (!ok) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    static byte[]? Protect(byte[] b) => Crypt(b, true);
    static byte[]? Unprotect(byte[] b) => Crypt(b, false);
}
