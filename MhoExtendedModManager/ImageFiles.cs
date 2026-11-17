using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MhoExtendedModManager;

/// <summary>
/// WebP pictures (Kurt, 2026-11-17). GDI+ (System.Drawing) can't read them, so a picked .webp is decoded by the Windows
/// Imaging Component (Windows' own WebP codec: built into Windows 11, the free "WebP Image Extensions" on Windows 10) and
/// saved as a PNG under data\converted\webp; every picker then uses that PNG as it would any other picture.
/// </summary>
static class ImageFiles
{
    /// <summary>A file the rest of the app can read: the file itself, or for a .webp its PNG copy (made once per file version).
    /// Throws IOException with a plain message when the WebP can't be read.</summary>
    public static string Usable(string path)
    {
        if (!IsWebP(path)) return path;
        var fi = new FileInfo(path);
        string dir = Path.Combine(Settings.Home, "converted", "webp");
        string png = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}_{(fi.Length ^ fi.LastWriteTimeUtc.Ticks) & 0xFFFFFFF:x7}.png");
        if (File.Exists(png)) return png;
        Directory.CreateDirectory(dir);
        using var bmp = LoadWebP(path);
        bmp.Save(png, ImageFormat.Png);
        return png;
    }

    public static bool IsWebP(string path) => Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Any picture as a Bitmap (WebP through WIC, the rest through GDI+), not locking the file.</summary>
    public static Bitmap Load(string path)
    {
        if (IsWebP(path)) return LoadWebP(path);
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    /// <summary>The ";*.webp" a file dialog's picture filter gets (both the shown list and the pattern).</summary>
    public const string WebP = ";*.webp";

    public static Bitmap LoadWebP(string path)
    {
        IWICImagingFactory? f = null; IWICBitmapDecoder? dec = null; IWICBitmapFrameDecode? frame = null; IWICFormatConverter? conv = null;
        try
        {
            f = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a"))!)!;
            int hr = f.CreateDecoderFromFilename(path, IntPtr.Zero, 0x80000000 /*GENERIC_READ*/, 1 /*cache on load*/, out dec);
            if (hr < 0 || dec == null)
                throw new IOException($"{Path.GetFileName(path)} can't be read as a WebP picture. On Windows 10, install Microsoft's free \"WebP Image Extensions\" from the Microsoft Store (error 0x{hr:X8}).");
            Check(dec.GetFrame(0, out frame));
            Check(f.CreateFormatConverter(out conv));
            var bgra = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");   // GUID_WICPixelFormat32bppBGRA (straight alpha, as GDI+'s 32bppArgb)
            Check(conv.Initialize(frame, ref bgra, 0, IntPtr.Zero, 0, 0));
            Check(conv.GetSize(out int w, out int h));
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Check(conv.CopyPixels(IntPtr.Zero, (uint)data.Stride, (uint)(data.Stride * h), data.Scan0)); }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }
        catch (COMException ex) { throw new IOException($"{Path.GetFileName(path)} can't be read as a WebP picture ({ex.Message}).", ex); }
        finally
        {
            foreach (object? o in new object?[] { conv, frame, dec, f }) if (o != null) Marshal.ReleaseComObject(o);
        }
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    // ---- the WIC interfaces used (vtable order; unused slots are placeholders)

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr vendor, uint access, int options, out IWICBitmapDecoder? decoder);
        void CreateDecoderFromStream(); void CreateDecoderFromFileHandle(); void CreateComponentInfo(); void CreateDecoder();
        void CreateEncoder(); void CreatePalette();
        [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICBitmapDecoder
    {
        void QueryCapability(); void Initialize(); void GetContainerFormat(); void GetDecoderInfo(); void CopyPalette();
        void GetMetadataQueryReader(); void GetPreview(); void GetColorContexts(); void GetThumbnail(); void GetFrameCount();
        [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
    }

    [ComImport, Guid("3B16811B-6A43-4ec9-A813-3D930C13B940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICBitmapFrameDecode { }   // (only passed on to the converter)

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICFormatConverter
    {
        // IWICBitmapSource
        [PreserveSig] int GetSize(out int width, out int height);
        void GetPixelFormat(); void GetResolution(); void CopyPalette();
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        // IWICFormatConverter
        [PreserveSig] int Initialize(IWICBitmapFrameDecode source, ref Guid format, int dither, IntPtr palette, double alphaThreshold, int paletteType);
    }
}
