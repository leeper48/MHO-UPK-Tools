using System.Security.Cryptography;

namespace MhoExtendedModManager;

/// <summary>
/// Signed updates (security audit, Kurt 2026-10-01). Each release zip has a "&lt;zip&gt;.sig" beside it: an ECDSA P-256
/// signature (SHA-256, IEEE P1363, base64) made with Kurt's private key, which stays on his PC (never on GitHub). The app
/// carries only the public key below and installs an update only when the signature checks out, so a hijacked GitHub
/// account or release run can't push an update. The .sha256 file only catches a corrupted download.
/// Release steps: CI drafts the release; download its zip, <c>--sign-file &lt;zip&gt; &lt;key.pem&gt;</c>, upload the .sig, publish.
/// </summary>
static class ReleaseSigning
{
    /// <summary>Kurt's release key (public half). Its private half: see CLAUDE.md (Signed updates).</summary>
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVauKwV0PesInDIcwJYxg+5SQnZh7
xI15tPQi8KvCVklr9HC1CTKDE7ANAtCCBv2cxiqOfOHQkAp2SZsn24bnGA==
-----END PUBLIC KEY-----
""";

    /// <summary>Does <paramref name="signatureBase64"/> sign <paramref name="data"/> with the release key? A test feed
    /// (MHO_EXTMM_UPDATE_FEED) may name its own key in MHO_EXTMM_SIGN_PUBKEY (a .pem file).</summary>
    public static bool Verify(byte[] data, string signatureBase64, bool testFeed)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64.Trim()); } catch (FormatException) { return false; }
        string pem = testFeed && Environment.GetEnvironmentVariable("MHO_EXTMM_SIGN_PUBKEY") is string f && File.Exists(f) ? File.ReadAllText(f) : PublicKeyPem;
        using var key = ECDsa.Create();
        try { key.ImportFromPem(pem); } catch (ArgumentException) { return false; }
        return key.VerifyData(data, sig, HashAlgorithmName.SHA256);
    }

    /// <summary>A new key pair: the private key written to <paramref name="privatePemPath"/> (refuses to overwrite one);
    /// returns the public key as PEM.</summary>
    public static string MakeKey(string privatePemPath)
    {
        if (File.Exists(privatePemPath)) throw new IOException($"{privatePemPath} exists already; a key is never overwritten.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privatePemPath))!);
        File.WriteAllText(privatePemPath, key.ExportPkcs8PrivateKeyPem());
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>Signs a file: writes &lt;file&gt;.sig (base64) and checks it against the built-in public key.</summary>
    public static string Sign(string file, string privatePemPath)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(privatePemPath));
        byte[] data = File.ReadAllBytes(file);
        string sig = Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256));
        File.WriteAllText(file + ".sig", sig);
        return Verify(data, sig, testFeed: false) ? "signed and checked against the app's built-in key" : "signed, but NOT with the key built into the app (wrong key file?)";
    }
}
