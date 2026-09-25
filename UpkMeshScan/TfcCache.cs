using System.Buffers.Binary;
using System.Text;

namespace UpkMeshScan;

/// <summary>
/// Texture file caches (.tfc): where the mips that aren't inside a package live. Cooked textures store those mips
/// with flags "in separate file" and offset/size -1; the real location is in TextureFileCacheManifest.bin next to
/// the caches (decoded from the file: int32 count, then per texture FString path ("MapTemplates.Sky.t_udk_sky_
/// cloudmask01"), 16-byte GUID (= the texture's TextureFileCacheGuid, stored after its mips), FString cache name
/// ("Textures" -> Textures.tfc), int32 n, n x (int32 mip index, uint32 offset, int32 size)). At that offset the
/// cache holds the mip as a UE3 compressed chunk (magic 0x9E2A83C1, block size, summary, block sizes, LZO blocks).
/// Read-only.
/// </summary>
static class TfcCache
{
    public sealed record Entry(string Path, byte[] Guid, string Cache, List<(int Mip, uint Offset, int Size)> Mips);

    static readonly Dictionary<string, Dictionary<string, List<Entry>>?> manifests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The manifest in a folder (cached), or null if there is none.</summary>
    static Dictionary<string, List<Entry>>? Manifest(string folder)
    {
        if (manifests.TryGetValue(folder, out var m)) return m;
        string path = System.IO.Path.Combine(folder, "TextureFileCacheManifest.bin");
        if (!File.Exists(path)) return manifests[folder] = null;
        byte[] b = File.ReadAllBytes(path);
        int p = 0;
        string Str()
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
            string s = len >= 0 ? Encoding.Latin1.GetString(b, p, Math.Max(0, len - 1)) : Encoding.Unicode.GetString(b, p, Math.Max(0, -len * 2 - 2));
            p += len >= 0 ? len : -len * 2;
            return s;
        }
        int count = BinaryPrimitives.ReadInt32LittleEndian(b); p = 4;
        var map = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string tex = Str();
            byte[] guid = b.AsSpan(p, 16).ToArray(); p += 16;
            string cache = Str();
            int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
            if (n < 0 || n > 32) throw new InvalidDataException($"texture cache manifest: entry {i} ({tex}) has {n} mips");
            var mips = new List<(int, uint, int)>();
            for (int k = 0; k < n; k++)
            {
                mips.Add((BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4)), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 8))));
                p += 12;
            }
            if (!map.TryGetValue(tex, out var list)) map[tex] = list = new();
            list.Add(new Entry(tex, guid, cache, mips));
        }
        if (p != b.Length) throw new InvalidDataException($"texture cache manifest: {count} entries end at {p}, file is {b.Length} bytes");
        return manifests[folder] = map;
    }

    /// <summary>The manifest entry for a texture (by path; the GUID picks among several), or null.</summary>
    public static Entry? Find(string folder, string texturePath, byte[] guid)
    {
        var m = Manifest(folder);
        if (m == null || !m.TryGetValue(texturePath, out var list)) return null;
        return list.FirstOrDefault(e => e.Guid.AsSpan().SequenceEqual(guid)) ?? (list.Count == 1 ? list[0] : null);
    }

    /// <summary>One mip's pixel data from the cache, decompressed to `expected` bytes; null if the manifest doesn't list it.</summary>
    public static byte[]? ReadMip(string folder, Entry e, int mip, int expected)
    {
        var loc = e.Mips.FirstOrDefault(x => x.Mip == mip);
        if (loc.Size <= 0) return null;
        string file = Directory.EnumerateFiles(folder, e.Cache + ".tfc").FirstOrDefault()
            ?? throw new FileNotFoundException($"{e.Cache}.tfc not found in {folder}");
        byte[] block = new byte[loc.Size];
        using (var fs = File.OpenRead(file)) { fs.Seek(loc.Offset, SeekOrigin.Begin); fs.ReadExactly(block); }
        if (BinaryPrimitives.ReadUInt32LittleEndian(block) != 0x9E2A83C1) throw new InvalidDataException($"{e.Cache}.tfc @{loc.Offset:X}: not a compressed chunk");
        return TextureExport.DecompressChunk(block, expected);
    }
}
