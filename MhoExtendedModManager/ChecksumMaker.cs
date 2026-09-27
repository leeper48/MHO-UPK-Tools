using System.IO.Hashing;
using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// --make-checksums &lt;clean CookedPCConsole&gt; &lt;out.json&gt; [--compare &lt;list.json&gt;]: our own stock checksum list,
/// made from a clean copy of the game's packages (Kurt's H:\CookedPCConsole - Original Backup), in the format of
/// StockData\upk_checksums.json: { "File.upk": "CRC32 HEX" }, standard CRC-32 of the whole file, sorted by name
/// ignoring case. Reads the folder only.
///
/// Every package is also checked for the stock traits: UE3 magic, dated 2024-03-14, and compressed (PackageFlags bit
/// 0x02000000, which the mod tools clear when they write a package uncompressed). --compare reports matches and
/// differences against another list (MHModManager's).
/// </summary>
static class ChecksumMaker
{
    static readonly DateTime StockDate = new(2024, 3, 14);

    public static int Run(string folder, string outPath, string? compareWith)
    {
        if (!Directory.Exists(folder)) { Console.WriteLine($"No folder {folder}"); return 1; }
        var files = Directory.GetFiles(folder, "*.upk").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"{files.Count} packages in {folder}");
        var list = new Dictionary<string, string>();
        var notStockDate = new List<string>(); var uncompressed = new List<string>(); var badMagic = new List<string>();
        var buffer = new byte[4 << 20];
        long bytes = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < files.Count; i++)
        {
            string f = files[i], name = Path.GetFileName(f);
            var crc = new Crc32();
            byte[] head = new byte[512];
            int headLen = 0;
            using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            {
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (headLen < head.Length) { int k = Math.Min(n, head.Length - headLen); Array.Copy(buffer, 0, head, headLen, k); headLen += k; }
                    crc.Append(buffer.AsSpan(0, n));
                    bytes += n;
                }
            }
            list[name] = Convert.ToHexString(crc.GetCurrentHash().Reverse().ToArray());   // big-endian hex, as MHModManager's list
            if (File.GetLastWriteTime(f).Date != StockDate) notStockDate.Add(name);
            var flags = PackageFlags(head.AsSpan(0, headLen));
            if (flags == null) badMagic.Add(name);
            else if ((flags.Value & 0x02000000) == 0) uncompressed.Add(name);
            if ((i + 1) % 1000 == 0) Console.WriteLine($"  {i + 1} / {files.Count}  ({bytes / 1048576.0 / Math.Max(1, sw.Elapsed.TotalSeconds):0} MB/s)");
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine($"Wrote {outPath}: {list.Count} packages, {bytes / 1073741824.0:0.00} GB read in {sw.Elapsed.TotalMinutes:0.0} min.");
        Console.WriteLine($"Stock traits: {files.Count - notStockDate.Count} dated 2024-03-14, {files.Count - uncompressed.Count - badMagic.Count} compressed, {badMagic.Count} not UE3 packages.");
        foreach (var (what, l) in new[] { ("not dated 2024-03-14", notStockDate), ("uncompressed (a mod tool's output?)", uncompressed), ("not a UE3 package", badMagic) })
            if (l.Count > 0) Console.WriteLine($"  {l.Count} {what}: {string.Join(", ", l.Take(20))}{(l.Count > 20 ? " …" : "")}");

        int problems = notStockDate.Count + uncompressed.Count + badMagic.Count;
        if (compareWith != null && File.Exists(compareWith))
        {
            var other = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(compareWith)) ?? [];
            var o = new Dictionary<string, string>(other, StringComparer.OrdinalIgnoreCase);
            int same = 0; var differ = new List<string>(); var onlyHere = new List<string>();
            foreach (var (name, crc) in list)
            {
                if (!o.TryGetValue(name, out var c)) onlyHere.Add(name);
                else if (c.Equals(crc, StringComparison.OrdinalIgnoreCase)) same++;
                else differ.Add(name);
            }
            var onlyThere = o.Keys.Where(k => !list.ContainsKey(k) && !list.Keys.Any(x => x.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
            Console.WriteLine($"Against {compareWith}: {same} identical, {differ.Count} different, {onlyHere.Count} only in the folder, {onlyThere.Count} only in that list.");
            foreach (var (what, l) in new[] { ("different", differ), ("only in the folder", onlyHere), ("only in that list", onlyThere) })
                if (l.Count > 0) Console.WriteLine($"  {what}: {string.Join(", ", l.Take(20))}{(l.Count > 20 ? " …" : "")}");
            bool bytesSame = File.ReadAllText(compareWith).Replace("\r\n", "\n").Trim() == File.ReadAllText(outPath).Replace("\r\n", "\n").Trim();
            Console.WriteLine(bytesSame ? "The new file is identical to that list (text)." : "The new file's text differs from that list (order, case or values: see above).");
            problems += differ.Count + onlyHere.Count + onlyThere.Count;
        }
        Console.WriteLine(problems == 0 ? "Clean: every package has the stock traits" + (compareWith != null ? " and matches the other list." : ".") : $"{problems} thing(s) to look at before trusting this list.");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>PackageFlags from a package's first bytes (magic, version, TotalHeaderSize, FolderName FString), or null.</summary>
    static uint? PackageFlags(ReadOnlySpan<byte> h)
    {
        if (h.Length < 16 || BitConverter.ToUInt32(h[..4]) != 0x9E2A83C1) return null;
        int v = BitConverter.ToInt32(h[4..8]) & 0xFFFF, p = 8;
        if (v >= 249) p += 4;
        if (v >= 269)
        {
            int len = BitConverter.ToInt32(h[p..(p + 4)]); p += 4;
            p += len >= 0 ? len : -len * 2;   // ANSI, or UTF-16 when negative
        }
        return p + 4 <= h.Length ? BitConverter.ToUInt32(h[p..(p + 4)]) : null;
    }
}
