using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

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
            if (Kind == ClipKind.Image)
            {
                if (File.Exists(ImagePath)) return ImagePath;
                if (File.Exists(ThumbPath)) return ThumbPath;
            }
            if (Kind == ClipKind.Files)
                return FilePaths.FirstOrDefault(path => IsImageFile(path) && File.Exists(path));
            return null;
        }
    }

    bool IsSingleImage => FilePaths.Length == 1 && IsImageFile(FilePaths[0]);

    public bool HasThumb => Kind == ClipKind.Image && !string.IsNullOrEmpty(ThumbPath) && File.Exists(ThumbPath);

    public string PinGlyph => Pinned ? "★" : "☆";

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { KindLabel };
            if (Kind == ClipKind.Files && FilePaths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
                parts.Add("文件已不在原位置");
            if (!string.IsNullOrWhiteSpace(SourceApp))
                parts.Add(SourceApp);
            parts.Add(Relative(CreatedAt));
            return string.Join(" · ", parts);
        }
    }

    string KindLabel => Category switch
    {
        ContentCategory.Text => "文字",
        ContentCategory.Image => "图片",
        ContentCategory.Video => "视频",
        ContentCategory.Audio => "音频",
        ContentCategory.Link => "链接",
        ContentCategory.File when FilePaths.Length > 1 => $"{FilePaths.Length} 个文件",
        _ => "文件"
    };

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
        var span = DateTime.Now - time;
        if (span.TotalSeconds < 45) return "刚刚";
        if (span.TotalMinutes < 60) return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟前";
        if (span.TotalHours < 24) return $"{Math.Max(1, (int)span.TotalHours)} 小时前";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} 天前";
        return time.ToString("M月d日");
    }
}
