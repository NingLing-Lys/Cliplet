using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cliplet;

public enum ClipKind
{
    Text,
    Image,
    Files
}

public enum ContentCategory
{
    Text,
    Image,
    Video,
    Audio,
    Link,
    File
}

public enum ContentFilter
{
    All,
    Text,
    Image,
    Video,
    Audio,
    Link,
    File
}

public sealed class ClipItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required ClipKind Kind { get; init; }
    public required string Text { get; init; }
    public string? ImagePath { get; init; }
    public string? ThumbPath { get; init; }
    public required string[] FilePaths { get; init; }
    public required string Preview { get; init; }
    public required string SourceApp { get; init; }
    public required string ContentHash { get; init; }

    DateTime createdAt;
    bool pinned;
    bool isChecked;

    public required DateTime CreatedAt
    {
        get => createdAt;
        set
        {
            createdAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public required bool Pinned
    {
        get => pinned;
        set
        {
            pinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinGlyph));
        }
    }

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            OnPropertyChanged();
        }
    }

    public string Title => Kind switch
    {
        ClipKind.Image => "图片",
        ClipKind.Files when FilePaths.Length == 1 => Path.GetFileName(FilePaths[0]),
        ClipKind.Files => $"{FilePaths.Length} 个文件",
        _ => string.IsNullOrWhiteSpace(Preview) ? "空白文本" : Preview
    };

    public ContentCategory Category => Classify();

    public string KindMark => Category switch
    {
        ContentCategory.Text => "文",
        ContentCategory.Image => "图",
        ContentCategory.Video => "视",
        ContentCategory.Audio => "音",
        ContentCategory.Link => "链",
        ContentCategory.File when FilePaths.Length > 1 => FilePaths.Length.ToString(),
        _ => "档"
    };

    public string? PreviewPath
    {
        get
        {
            string? path = null;
            if (Kind == ClipKind.Image)
            {
                if (File.Exists(ImagePath)) path = ImagePath;
                else if (File.Exists(ThumbPath)) path = ThumbPath;
            }
            else if (Kind == ClipKind.Files)
                path = FilePaths.FirstOrDefault(p => IsImageFile(p) && File.Exists(p));

            // Tiny clipboard icons look awful when blown up in the preview card.
            return IsLargeEnoughPreview(path) ? path : null;
        }
    }

    public string? ThumbPathForList
    {
        get
        {
            if (Kind == ClipKind.Image)
            {
                if (File.Exists(ThumbPath)) return ThumbPath;
                if (File.Exists(ImagePath) && IsLargeEnoughPreview(ImagePath)) return ImagePath;
            }
            if (Kind == ClipKind.Files)
            {
                var path = FilePaths.FirstOrDefault(p => IsImageFile(p) && File.Exists(p));
                return IsLargeEnoughPreview(path) ? path : null;
            }
            return null;
        }
    }

    /// <summary>Small crisp app badge shown beside the subtitle.</summary>
    public ImageSource? SourceIcon => AppIcons.Resolve(SourceApp);

    public bool ShowListThumb => !string.IsNullOrEmpty(ThumbPathForList);

    public bool ShowSourceIcon => SourceIcon != null && !ShowListThumb;

    public bool ShowKindMark => !ShowListThumb && !ShowSourceIcon;

    static bool IsLargeEnoughPreview(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            // Very small files are almost always icons / emoji bitmaps.
            if (info.Length < 8_000) return false;
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return Math.Max(frame.PixelWidth, frame.PixelHeight) >= 96;
        }
        catch
        {
            return false;
        }
    }

    bool IsSingleImage => FilePaths.Length == 1 && IsImageFile(FilePaths[0]);

    public bool HasThumb => Kind == ClipKind.Image && !string.IsNullOrEmpty(ThumbPath) && File.Exists(ThumbPath);

    public string PinGlyph => Pinned ? "★" : "☆";

    string shortcutHint = "";
    public string ShortcutHint
    {
        get => shortcutHint;
        set
        {
            if (shortcutHint == value) return;
            shortcutHint = value;
            OnPropertyChanged();
        }
    }

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { KindLabel };
            if (Kind == ClipKind.Files && FilePaths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
                parts.Add("文件已不在原位置");
            var source = LocalizeSource(SourceApp);
            if (!string.IsNullOrWhiteSpace(source))
                parts.Add(source);
            parts.Add(Relative(CreatedAt));
            return string.Join(" · ", parts);
        }
    }

    string KindLabel => Category switch
    {
        ContentCategory.Text => "文本",
        ContentCategory.Image => "图片",
        ContentCategory.Video => "视频",
        ContentCategory.Audio => "音频",
        ContentCategory.Link => "链接",
        ContentCategory.File when FilePaths.Length > 1 => $"{FilePaths.Length} 个文件",
        _ => "文件"
    };

    public static string LocalizeSource(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = raw.Trim();
        var key = name.ToLowerInvariant();

        // Common process names / English product names → Chinese when natural.
        if (key is "weixin" or "wechat" or "wechatappex" or "wechatbrowser") return "微信";
        if (key is "qq" or "tim") return "QQ";
        if (key is "dingtalk" or "dingtalklauncher") return "钉钉";
        if (key is "feishu" or "lark" or "larkshell") return "飞书";
        if (key is "notepad") return "记事本";
        if (key is "explorer") return "资源管理器";
        if (key is "applicationframehost") return "系统应用";
        if (key is "windowsterminal" or "windowsterminal.exe" or "wt") return "终端";
        if (key is "cmd" or "powershell" or "pwsh") return "终端";
        if (key is "chrome" or "google chrome") return "Chrome";
        if (key is "msedge" or "microsoft edge" or "edge") return "Edge";
        if (key is "firefox") return "Firefox";
        if (key is "code" or "code - oss" or "code.exe") return "VS Code";
        if (key is "cursor") return "Cursor";
        if (key is "winword" or "microsoft word") return "Word";
        if (key is "excel" or "microsoft excel") return "Excel";
        if (key is "powerpnt" or "microsoft powerpoint") return "PowerPoint";
        if (key is "wps" or "wpsoffice" or "kingsoft office") return "WPS";
        if (key is "snippingtool" or "screensketch") return "截图";
        if (key is "telegram" or "telegram.exe") return "Telegram";
        if (key is "discord") return "Discord";
        if (key is "spotify") return "Spotify";
        if (key.Contains("weixin") || key.Contains("wechat")) return "微信";
        if (key.Contains("dingtalk")) return "钉钉";
        if (key.Contains("feishu") || key.Contains("lark")) return "飞书";
        if (name.Contains("微信", StringComparison.Ordinal)) return "微信";

        // Strip trailing "App" / version noise for cleaner labels.
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    public void RefreshLabels() => OnPropertyChanged(nameof(Subtitle));

    public bool Matches(ContentFilter filter) => filter switch
    {
        ContentFilter.All => true,
        ContentFilter.Text => Category == ContentCategory.Text,
        ContentFilter.Image => Category == ContentCategory.Image,
        ContentFilter.Video => Category == ContentCategory.Video,
        ContentFilter.Audio => Category == ContentCategory.Audio,
        ContentFilter.Link => Category == ContentCategory.Link,
        ContentFilter.File => Category == ContentCategory.File,
        _ => true
    };

    ContentCategory Classify()
    {
        if (Kind == ClipKind.Image) return ContentCategory.Image;
        if (Kind == ClipKind.Text)
            return IsLinkText(Text) ? ContentCategory.Link : ContentCategory.Text;
        if (FilePaths.Length == 0) return ContentCategory.File;

        var categories = FilePaths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(ClassifyPath)
            .Distinct()
            .ToArray();
        if (categories.Length == 0) return ContentCategory.File;
        if (categories.Length == 1) return categories[0];
        return ContentCategory.File;
    }

    static ContentCategory ClassifyPath(string path)
    {
        if (Directory.Exists(path)) return ContentCategory.File;
        if (IsImageFile(path)) return ContentCategory.Image;
        if (IsVideoFile(path)) return ContentCategory.Video;
        if (IsAudioFile(path)) return ContentCategory.Audio;
        return ContentCategory.File;
    }

    public static bool IsImageFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".ico" or ".jfif";
    }

    public static bool IsVideoFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Path.GetExtension(path).ToLowerInvariant() is
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v" or ".mpeg" or ".mpg" or ".ts" or ".m2ts";
    }

    public static bool IsAudioFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Path.GetExtension(path).ToLowerInvariant() is
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" or ".aiff" or ".opus";
    }

    public static bool IsLinkText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.Contains('\n') || value.Contains(' ')) return false;
        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    static string Relative(DateTime time)
    {
        if (time.Date == DateTime.Today)
            return time.ToString("HH:mm");
        var span = DateTime.Now - time;
        if (span.TotalDays < 7) return $"{Math.Max(1, (int)span.TotalDays)} 天前";
        return time.ToString("M月d日");
    }
}
