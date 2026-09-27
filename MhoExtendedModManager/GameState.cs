using System.IO.Hashing;
using System.Text.Json;

namespace MhoExtendedModManager;

enum PackageState { Applied, Stock, OtherVersion, NotInGame, NotInMod }

/// <summary>
/// Compares live packages with mod copies and with the stock CRC32 list MHModManager ships (upk_checksums.json:
/// 15,250 packages, standard CRC-32, checked against stock files 2026-09-27).
/// </summary>
sealed class GameState
{
    public static readonly DateTime StockDate = new(2024, 3, 14);

    public string Root { get; }
    public string Cooked { get; }
    /// <summary>Data\Game\Loco: one &lt;lang&gt;.all folder of .string files per language.</summary>
    public string Loco => Path.Combine(Root, "Data", "Game", "Loco");
    readonly Dictionary<string, uint> stock = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, long, DateTime), uint> crcCache = [];

    public bool HasStockList => stock.Count > 0;
    public int StockCount => stock.Count;

    public GameState(string gameRoot, string? dataFolder)
    {
        Root = gameRoot;
        Cooked = Settings.Cooked(gameRoot);
        // The checksum list: in our library (copied there by --migrate), or next to MHModManager.exe, one level above its data folder.
        string? list = dataFolder == null ? null : new[] { Path.Combine(dataFolder, "upk_checksums.json"), Path.Combine(Path.GetDirectoryName(dataFolder.TrimEnd('\\'))!, "upk_checksums.json"), ModInstaller.ShippedStockList }.FirstOrDefault(File.Exists);
        if (list != null)
            foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(list)) ?? [])
                if (uint.TryParse(kv.Value, System.Globalization.NumberStyles.HexNumber, null, out uint c)) stock[kv.Key] = c;
    }

    public uint Crc(string path)
    {
        var fi = new FileInfo(path);
        var key = (fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc);
        lock (crcCache) if (crcCache.TryGetValue(key, out uint c)) return c;
        var crc = new Crc32();
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20)) crc.Append(s);
        uint v = crc.GetCurrentHashAsUInt32();
        lock (crcCache) crcCache[key] = v;
        return v;
    }

    public bool IsStockName(string file) => stock.ContainsKey(file);

    public uint CrcOf(byte[] bytes) => Crc32.HashToUInt32(bytes);

    /// <summary>Does the file at <paramref name="path"/> have the stock CRC of package <paramref name="file"/>?</summary>
    public bool MatchesStock(string file, string path) => stock.TryGetValue(file, out uint want) && File.Exists(path) && Crc(path) == want;

    public bool? IsStock(string file)
    {
        string live = Path.Combine(Cooked, file);
        if (!File.Exists(live) || !stock.TryGetValue(file, out uint want)) return null;
        return Crc(live) == want;
    }

    /// <summary>Is the mod's copy of this package the live one? If not, is the live one stock or something else?</summary>
    public PackageState Check(Mod mod, string file)
    {
        string live = Path.Combine(Cooked, file), mine = Path.Combine(mod.Folder, file);
        if (!File.Exists(mine)) return PackageState.NotInMod;
        if (!File.Exists(live)) return PackageState.NotInGame;
        if (new FileInfo(live).Length == new FileInfo(mine).Length && Crc(live) == Crc(mine)) return PackageState.Applied;
        return IsStock(file) == true ? PackageState.Stock : PackageState.OtherVersion;
    }

    /// <summary>Game packages dated after the stock date (cheap: no hashing), i.e. modified by some tool.</summary>
    public IEnumerable<FileInfo> ModifiedByDate() =>
        new DirectoryInfo(Cooked).EnumerateFiles("*.upk").Where(f => f.LastWriteTime.Date != StockDate);

    public static string Describe(PackageState s) => s switch
    {
        PackageState.Applied => "applied",
        PackageState.Stock => "not applied (stock file live)",
        PackageState.OtherVersion => "not applied (a different modified file is live)",
        PackageState.NotInGame => "not in the game folder",
        _ => "missing from the mod folder",
    };
}
