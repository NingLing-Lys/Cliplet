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

    public string Title => Kind switch
    {
        ClipKind.Files when FilePaths.Length == 1 => Path.GetFileName(FilePaths[0]),
        ClipKind.Files => $"{FilePaths.Length} 个文件",
        _ => string.IsNullOrWhiteSpace(Preview) ? "空白文本" : Preview
    };

    public string KindMark => Kind switch
    {
        ClipKind.Text => "文",
        ClipKind.Image => "图",
        ClipKind.Files when IsSingleImage => "图",
        ClipKind.Files when FilePaths.Length > 1 => FilePaths.Length.ToString(),
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
                return FilePaths.FirstOrDefault(IsImageFile);
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

    string KindLabel => Kind switch
    {
        ClipKind.Text => "文本",
        ClipKind.Image => "图片",
        ClipKind.Files when IsSingleImage => "图片",
        ClipKind.Files when FilePaths.Length > 1 => $"{FilePaths.Length} 个文件",
        _ => "文件"
    };

    public void RefreshLabels() => OnPropertyChanged(nameof(Subtitle));

    public static bool IsImageFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        return Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".ico" or ".jfif";
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
