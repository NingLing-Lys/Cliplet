using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Cliplet;

/// <summary>
/// Resolves crisp source-app badges for the list.
/// Prefers CC0 Simple Icons SVG badges (sharp at small sizes) over extracted .ico files.
/// </summary>
static class AppIcons
{
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Brand> Brands = BuildBrands();
    static string? assetsDir;

    public static ImageSource? Resolve(string? sourceApp)
    {
        if (string.IsNullOrWhiteSpace(sourceApp)) return null;
        if (Cache.TryGetValue(sourceApp, out var hit)) return hit;

        var brand = MatchBrand(sourceApp);
        // Vector badge first — stays sharp when shown at 16–20px.
        ImageSource? icon = brand == null
            ? null
            : RenderSvgBadge(brand) ?? LoadBundledPng(brand.FileKey) ?? ExtractKnownInstall(brand);

        if (icon != null)
            Cache[sourceApp] = icon;
        return icon;
    }

    static Brand? MatchBrand(string sourceApp)
    {
        var key = sourceApp.Trim().ToLowerInvariant();
        foreach (var brand in Brands.Values)
        {
            if (brand.Aliases.Any(alias => key == alias || key.Contains(alias, StringComparison.Ordinal)))
                return brand;
        }
        return null;
    }

    static ImageSource? LoadBundledPng(string fileKey)
    {
        var dir = AssetsDir();
        if (dir == null) return null;
        var path = Path.Combine(dir, fileKey + ".png");
        return File.Exists(path) ? LoadBitmap(path, 72) : null;
    }

    static ImageSource? ExtractKnownInstall(Brand brand)
    {
        foreach (var candidate in brand.ExeHints)
        {
            var path = Environment.ExpandEnvironmentVariables(candidate);
            if (!File.Exists(path)) continue;
            var cached = CacheExtracted(brand.FileKey, path);
            if (cached != null) return cached;
        }
        return null;
    }

    static ImageSource? CacheExtracted(string fileKey, string exePath)
    {
        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cliplet", "appicons");
            Directory.CreateDirectory(cacheDir);
            var png = Path.Combine(cacheDir, fileKey + ".png");
            if (!File.Exists(png) || new FileInfo(png).Length < 200)
            {
                if (!SaveAssociatedIcon(exePath, png, 64))
                    return null;
            }
            return LoadBitmap(png, 72);
        }
        catch
        {
            return null;
        }
    }

    static bool SaveAssociatedIcon(string exePath, string pngPath, int size)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        try
        {
            if (ExtractIconEx(exePath, 0, large, small, 1) == 0 || large[0] == IntPtr.Zero)
            {
                using var fallback = Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (fallback == null) return false;
                using var bmp = fallback.ToBitmap();
                using var scaled = new Drawing.Bitmap(size, size);
                using var g = Drawing.Graphics.FromImage(scaled);
                g.Clear(Drawing.Color.Transparent);
                g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, size, size);
                scaled.Save(pngPath, Drawing.Imaging.ImageFormat.Png);
                return true;
            }

            using var icon = Drawing.Icon.FromHandle(large[0]);
            using var src = icon.ToBitmap();
            using var dst = new Drawing.Bitmap(size, size);
            using (var g = Drawing.Graphics.FromImage(dst))
            {
                g.Clear(Drawing.Color.Transparent);
                g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            dst.Save(pngPath, Drawing.Imaging.ImageFormat.Png);
            return true;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
    }

    static ImageSource? RenderSvgBadge(Brand brand)
    {
        var dir = AssetsDir();
        if (dir == null) return null;
        var svgPath = Path.Combine(dir, brand.FileKey + ".svg");
        if (!File.Exists(svgPath) && !string.IsNullOrEmpty(brand.FallbackSvg))
            svgPath = Path.Combine(dir, brand.FallbackSvg);
        if (!File.Exists(svgPath)) return null;

        try
        {
            var svg = File.ReadAllText(svgPath);
            var pathData = ExtractPathData(svg);
            if (string.IsNullOrWhiteSpace(pathData)) return null;
            var geometry = Geometry.Parse(pathData);
            geometry.Freeze();

            // Render at 3× so 18px display stays crisp on high-DPI screens.
            const int px = 54;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bg = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(brand.Color)!;
                dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, px, px), 12, 12);

                var bounds = geometry.Bounds;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
                const double pad = 11;
                var scale = Math.Min((px - pad * 2) / bounds.Width, (px - pad * 2) / bounds.Height);
                var group = new TransformGroup();
                group.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
                group.Children.Add(new ScaleTransform(scale, scale));
                group.Children.Add(new TranslateTransform(
                    (px - bounds.Width * scale) / 2,
                    (px - bounds.Height * scale) / 2));
                dc.PushTransform(group);
                dc.DrawGeometry(System.Windows.Media.Brushes.White, null, geometry);
                dc.Pop();
            }

            var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    static string? ExtractPathData(string svg)
    {
        foreach (var marker in new[] { " d=\"", "d=\"" })
        {
            var start = svg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += marker.Length;
            var end = svg.IndexOf('"', start);
            if (end < 0) continue;
            return svg[start..end];
        }
        return null;
    }

    static BitmapImage? LoadBitmap(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    static string? AssetsDir()
    {
        if (assetsDir != null) return assetsDir;
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Assets", "apps"),
            Path.Combine(baseDir, "apps"),
        };
        assetsDir = candidates.FirstOrDefault(Directory.Exists);
        return assetsDir;
    }

    static Dictionary<string, Brand> BuildBrands() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["wechat"] = new Brand("wechat", "#07C160",
            ["微信", "weixin", "wechat"],
            [@"%ProgramFiles%\Tencent\Weixin\Weixin.exe",
             @"%ProgramFiles%\Tencent\WeChat\WeChat.exe",
             @"%ProgramFiles(x86)%\Tencent\WeChat\WeChat.exe"]),
        ["feishu"] = new Brand("feishu", "#3370FF",
            ["飞书", "feishu", "lark"],
            [@"%LocalAppData%\Feishu\Feishu.exe",
             @"%LocalAppData%\Feishu\app\Feishu.exe",
             @"%LocalAppData%\Lark\Lark.exe"]),
        ["qq"] = new Brand("qq", "#12B7F5",
            ["qq", "tim", "tencentqq"],
            [@"%ProgramFiles%\Tencent\QQNT\QQ.exe",
             @"%ProgramFiles%\Tencent\QQ\Bin\QQ.exe"],
            FallbackSvg: "tencentqq.svg"),
        ["dingtalk"] = new Brand("dingtalk", "#0089FF",
            ["钉钉", "dingtalk", "ding"],
            [@"%ProgramFiles%\DingTalk\DingTalk.exe",
             @"%LocalAppData%\DingTalk\DingTalk.exe",
             @"%ProgramFiles(x86)%\DingDing\DingTalk.exe"]),
        ["telegram"] = new Brand("telegram", "#26A5E4",
            ["telegram"],
            [@"%AppData%\Telegram Desktop\Telegram.exe"]),
        ["discord"] = new Brand("discord", "#5865F2",
            ["discord"],
            [@"%LocalAppData%\Discord\Update.exe"]),
        ["chrome"] = new Brand("chrome", "#4285F4",
            ["chrome", "google chrome"],
            [@"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
             @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"],
            FallbackSvg: "chrome.svg"),
        ["edge"] = new Brand("edge", "#0078D4",
            ["edge", "microsoft edge", "msedge"],
            [@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
             @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"]),
        ["firefox"] = new Brand("firefox", "#FF7139",
            ["firefox"],
            [@"%ProgramFiles%\Mozilla Firefox\firefox.exe"]),
        ["github"] = new Brand("github", "#181717", ["github"], []),
        ["notion"] = new Brand("notion", "#000000", ["notion"],
            [@"%LocalAppData%\Programs\Notion\Notion.exe"]),
        ["slack"] = new Brand("slack", "#4A154B", ["slack"],
            [@"%LocalAppData%\slack\slack.exe"]),
        ["whatsapp"] = new Brand("whatsapp", "#25D366", ["whatsapp"], []),
        ["cursor"] = new Brand("cursor", "#000000",
            ["cursor"],
            [@"%LocalAppData%\Programs\cursor\Cursor.exe"]),
        ["vscode"] = new Brand("vscode", "#007ACC",
            ["vs code", "vscode", "code"],
            [@"%LocalAppData%\Programs\Microsoft VS Code\Code.exe",
             @"%ProgramFiles%\Microsoft VS Code\Code.exe"]),
        ["word"] = new Brand("word", "#2B579A", ["word", "winword", "microsoft word"], []),
        ["excel"] = new Brand("excel", "#217346", ["excel", "microsoft excel"], []),
        ["notepad"] = new Brand("notepad", "#1C1C1E", ["记事本", "notepad"],
            [@"%SystemRoot%\System32\notepad.exe", @"%SystemRoot%\notepad.exe"]),
    };

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);

    sealed record Brand(
        string FileKey,
        string Color,
        string[] Aliases,
        string[] ExeHints,
        string? FallbackSvg = null);
}

sealed class SourceIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string source ? AppIcons.Resolve(source) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

sealed class HasSourceIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string source && AppIcons.Resolve(source) != null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

sealed class NoSourceIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Show kind mark only when there is no preview image and no source icon.
        if (value is not ClipItem item) return Visibility.Visible;
        if (!string.IsNullOrEmpty(item.PreviewPath)) return Visibility.Collapsed;
        return AppIcons.Resolve(item.SourceApp) == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
