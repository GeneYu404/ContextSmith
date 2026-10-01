using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ContextSmith.Services;

/// <summary>
/// 从 "path,index" 形式的图标描述中提取小图标。
/// 结果被复制为已冻结的 BitmapSource，可以在后台线程生成、在 UI 线程使用。
/// </summary>
internal static class IconLoader
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Load(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        return Cache.GetOrAdd(source.Trim(), LoadCore);
    }

    private static ImageSource? LoadCore(string source)
    {
        var (path, index) = Parse(source);
        if (path is null) return null;

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        try
        {
            var count = Native.ExtractIconEx(path, index, large, small, 1);
            if (count == 0 || count == uint.MaxValue) return null;

            var handle = small[0] != IntPtr.Zero ? small[0] : large[0];
            if (handle == IntPtr.Zero) return null;

            var interop = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // 拷贝像素到普通 BitmapSource，保证可以安全 Freeze 并跨线程使用
            var converted = new FormatConvertedBitmap(interop, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) Native.DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) Native.DestroyIcon(small[0]);
        }
    }

    /// <summary>解析 "%SystemRoot%\system32\shell32.dll,-16769" / "\"C:\a b\x.exe\",0" 等写法</summary>
    public static (string? path, int index) Parse(string source)
    {
        var s = Environment.ExpandEnvironmentVariables(source.Trim());
        var index = 0;

        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 1)
            {
                var rest = s[(end + 1)..].Trim();
                s = s[1..end];
                if (rest.StartsWith(',') && int.TryParse(rest[1..].Trim(), out var i1)) index = i1;
            }
        }
        else
        {
            var comma = s.LastIndexOf(',');
            if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var i2))
            {
                index = i2;
                s = s[..comma];
            }
        }

        return (ShellText.FindFile(s), index);
    }
}
