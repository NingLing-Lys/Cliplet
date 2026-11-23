using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace Cliplet;

public sealed class ClipStore : INotifyPropertyChanged, IDisposable
{
    const int MaxUnpinned = 400;
    readonly ClipDatabase database;
    readonly List<ClipItem> all = [];
    readonly string settingsPath;
    uint lastSequence;
    uint internalSequence;
    bool retrying;
    Settings settings;

    public ObservableCollection<ClipItem> Visible { get; } = [];
    public event Action? ListReset;
    public string? SelectedId { get; set; }

    string query = "";
    string status = "单击复制，双击或回车粘贴";
    string hotkeyLabel = "Ctrl+Alt+V";
    bool paused;

    public ClipStore()
    {
        database = new ClipDatabase();
        settingsPath = Path.Combine(database.DirectoryPath, "settings.json");
        settings = LoadSettings();
        paused = settings.Paused;
        all.AddRange(database.LoadAll());
        RepairStoredImages();
        lastSequence = NativeMethods.GetClipboardSequenceNumber();
        Rebuild();
    }

    public string Query
    {
        get => query;
        set
        {
            if (query == value) return;
            query = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public string Status
    {
        get => status;
        private set
        {
            if (status == value) return;
            status = value;
            OnPropertyChanged();
        }
    }

    public string HotkeyLabel
    {
        get => hotkeyLabel;
        set
        {
            if (hotkeyLabel == value) return;
            hotkeyLabel = value;
            OnPropertyChanged();
        }
    }

    public bool Paused
    {
        get => paused;
        private set
        {
            if (paused == value) return;
            paused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    public bool IsEmpty => Visible.Count == 0;

    public string EmptyText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Query)) return "没有匹配的记录";
            return Paused ? "记录已暂停" : "复制文本、图片或文件后会出现在这里";
        }
    }

    public double WindowWidth => settings.Width;
    public double WindowHeight => settings.Height;

    public void Report(string text) => Status = text;

    public void OnClipboardChanged()
    {
        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (sequence == lastSequence || sequence == internalSequence) return;
        if (Paused || NativeMethods.IsPasswordFocused())
        {
            lastSequence = sequence;
            return;
        }

        try
        {
            var draft = ReadClipboard();
            lastSequence = sequence;
            if (draft != null) Accept(draft);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            if (retrying)
            {
                lastSequence = sequence;
                return;
            }
            retrying = true;
            Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(100);
                retrying = false;
                OnClipboardChanged();
            });
        }
    }

    public void Copy(ClipItem item)
    {
        var rich = WriteClipboard(item);
        Status = rich
            ? "已复制。Word 里会显示公式，纯文本框仍是 LaTeX"
            : "已复制";
    }

    public void CopyOptimizedPrompt(ClipItem item)
    {
        if (item.Kind != ClipKind.Text || string.IsNullOrWhiteSpace(item.Text)) return;
        var data = new DataObject();
        data.SetText(FormulaText.OptimizePrompt(item.Text));
        Clipboard.SetDataObject(data, copy: true);
        Status = "已复制优化后的提示词，贴到对话里发送";
    }

    public void CopyFormulaImage(ClipItem item)
    {
        if (item.Kind != ClipKind.Text) return;
        var image = FormulaText.RenderSheet(item.Text);
        if (image == null)
        {
            Status = "这段里没有能排版的公式";
            return;
        }
        var data = new DataObject();
        data.SetImage(image);
        Clipboard.SetDataObject(data, copy: true);
        internalSequence = NativeMethods.GetClipboardSequenceNumber();
        lastSequence = internalSequence;
        Status = "已复制公式图片，可直接粘贴";
    }

    public void TogglePinned(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        database.SetPinned(item.Id, item.Pinned);
        Status = item.Pinned ? "已置顶" : "已取消置顶";
        Rebuild();
    }

    public void Delete(ClipItem item)
    {
        all.Remove(item);
        database.Delete(item.Id);
        RemoveFiles(item);
        if (SelectedId == item.Id) SelectedId = null;
        Status = "已删除";
        Rebuild();
    }

    public void ClearUnpinned()
    {
        var doomed = database.DeleteUnpinned();
        all.RemoveAll(item => !item.Pinned);
        foreach (var item in doomed) RemoveFiles(item);
        SelectedId = null;
        Status = "已清空未置顶记录";
        Rebuild();
    }

    public void TogglePaused()
    {
        Paused = !Paused;
        settings.Paused = Paused;
        SaveSettings();
        Status = Paused ? "已暂停记录" : "继续记录复制内容";
    }

    public void SaveWindowSize(double width, double height)
    {
        if (width < 360 || height < 420) return;
        settings.Width = width;
        settings.Height = height;
        SaveSettings();
    }

    public void RefreshLabels()
    {
        foreach (var item in all) item.RefreshLabels();
    }

    public void Dispose() => database.Dispose();

    void Accept(Draft draft)
    {
        var existing = all.FirstOrDefault(item => item.ContentHash == draft.Hash);
        if (existing != null)
        {
            existing.CreatedAt = DateTime.Now;
            database.Touch(existing.Id, existing.CreatedAt);
            Rebuild();
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        string? imagePath = null;
        string? thumbPath = null;
        if (draft.Image != null)
        {
            imagePath = Path.Combine(database.ImageDirectory, id + ".png");
            thumbPath = Path.Combine(database.ImageDirectory, id + ".thumb.png");
            SavePng(Scale(draft.Image, 1600), imagePath);
            SavePng(Scale(draft.Image, 160), thumbPath);
        }

        var item = new ClipItem
        {
            Id = id,
            Kind = draft.Kind,
            Text = draft.Text,
            ImagePath = imagePath,
            ThumbPath = thumbPath,
            FilePaths = draft.FilePaths,
            Preview = draft.Preview,
            SourceApp = draft.SourceApp,
            Pinned = false,
            CreatedAt = DateTime.Now,
            ContentHash = draft.Hash
        };
        database.Insert(item);
        all.Insert(0, item);
        Trim();
        Rebuild();
    }

    void Trim()
    {
        foreach (var extra in database.UnpinnedBeyond(MaxUnpinned))
        {
            database.Delete(extra.Id);
            all.RemoveAll(item => item.Id == extra.Id);
            RemoveFiles(extra);
        }
    }

    void Rebuild()
    {
        var needle = Query.Trim();
        IEnumerable<ClipItem> items = all;
        if (needle.Length > 0)
        {
            items = all.Where(item =>
                item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                item.Text.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                item.Preview.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                item.SourceApp.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                item.FilePaths.Any(path => path.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        var ordered = items
            .OrderByDescending(item => item.Pinned)
            .ThenByDescending(item => item.CreatedAt)
            .ToList();
        Visible.Clear();
        foreach (var item in ordered) Visible.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        ListReset?.Invoke();
    }

    Draft? ReadClipboard()
    {
        var source = NativeMethods.ForegroundAppName();
        if (Clipboard.ContainsFileDropList())
        {
            var paths = Clipboard.GetFileDropList()
                .Cast<string>()
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length > 0)
            {
                var preview = string.Join("、", paths.Select(Path.GetFileName));
                return new Draft(ClipKind.Files, preview, null, paths, preview, source, Hash(Encoding.UTF8.GetBytes(string.Join("\n", paths))));
            }
        }

        string? text = Clipboard.ContainsText() ? Clipboard.GetText()?.Trim() : null;
        if (text?.Length > 2_000_000) text = text[..2_000_000];
        BitmapSource? image = null;
        if (Clipboard.ContainsImage())
        {
            var raw = Clipboard.GetImage();
            if (raw != null && (long)raw.PixelWidth * raw.PixelHeight <= 40_000_000)
                image = Snapshot(raw);
        }

        var textIsUrl = text != null && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var largeImage = image != null && Math.Max(image.PixelWidth, image.PixelHeight) >= 200;
        if (image != null && (string.IsNullOrWhiteSpace(text) || (textIsUrl && largeImage)))
            return ImageDraft(image, source);
        if (!string.IsNullOrWhiteSpace(text))
        {
            text = FormulaText.AttachHiddenFormulas(text, ReadHtml());
            return new Draft(ClipKind.Text, text, null, [], PreviewText(text), source, Hash(Encoding.UTF8.GetBytes(text)));
        }
        return image == null ? null : ImageDraft(image, source);
    }

    Draft ImageDraft(BitmapSource image, string source)
    {
        var preview = $"图片 {image.PixelWidth}×{image.PixelHeight}";
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Scale(image, 1600)));
        encoder.Save(stream);
        return new Draft(ClipKind.Image, "", image, [], preview, source, Hash(stream.ToArray()));
    }

    static string? ReadHtml()
    {
        try
        {
            return Clipboard.ContainsData(DataFormats.Html) ? Clipboard.GetData(DataFormats.Html) as string : null;
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return null;
        }
    }

    bool WriteClipboard(ClipItem item)
    {
        var data = new DataObject();
        var rich = false;
        switch (item.Kind)
        {
            case ClipKind.Text:
                data.SetText(item.Text);
                if (FormulaText.ContainsFormula(item.Text))
                {
                    try
                    {
                        var rtf = FormulaText.BuildRtf(item.Text);
                        if (rtf != null)
                        {
                            data.SetData(DataFormats.Rtf, rtf);
                            rich = true;
                        }
                    }
                    catch (Exception ex) when (ex is System.Windows.Markup.XamlParseException or InvalidOperationException or ArgumentException)
                    {
                        rich = false;
                    }
                }
                break;
            case ClipKind.Image:
                if (item.ImagePath != null && LoadBitmap(item.ImagePath) is { } image)
                    data.SetImage(image);
                break;
            case ClipKind.Files:
                data.SetData(DataFormats.FileDrop, item.FilePaths);
                break;
        }
        Clipboard.SetDataObject(data, copy: true);
        internalSequence = NativeMethods.GetClipboardSequenceNumber();
        lastSequence = internalSequence;
        return rich;
    }

    static BitmapSource Snapshot(BitmapSource source)
    {
        BitmapSource formatted = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            formatted = converted;
        }
        var width = formatted.PixelWidth;
        var height = formatted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        formatted.CopyPixels(pixels, stride, 0);
        if (!HasVisiblePixel(pixels))
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }
        var copy = BitmapSource.Create(width, height, formatted.DpiX, formatted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    static BitmapSource Scale(BitmapSource source, int maxPixel)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxPixel) return source;
        var scale = maxPixel / (double)longest;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static BitmapSource? LoadBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    static string PreviewText(string text)
    {
        var collapsed = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 180 ? collapsed : collapsed[..180] + "…";
    }

    static bool HasVisiblePixel(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8) return true;
        }
        return false;
    }

    void RepairStoredImages()
    {
        foreach (var item in all)
        {
            if (item.Kind != ClipKind.Image || string.IsNullOrEmpty(item.ImagePath)) continue;
            try
            {
                if (!RepairTransparentPng(item.ImagePath) || string.IsNullOrEmpty(item.ThumbPath)) continue;
                WriteThumb(item.ImagePath, item.ThumbPath);
            }
            catch
            {
                // 单张损坏不影响其他记录。
            }
        }
    }

    static bool RepairTransparentPng(string path)
    {
        if (!File.Exists(path)) return false;
        var temp = path + ".fix.png";
        var changed = false;
        using (var loaded = new Drawing.Bitmap(path))
        {
            var width = loaded.Width;
            var height = loaded.Height;
            if (width <= 0 || height <= 0) return false;
            var bits = loaded.LockBits(
                new Drawing.Rectangle(0, 0, width, height),
                DrawingImaging.ImageLockMode.ReadOnly,
                DrawingImaging.PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[width * height * 4];
                var anyOpaque = false;
                var anyColor = false;
                for (var y = 0; y < height; y++)
                {
                    var row = new byte[width * 4];
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                    Buffer.BlockCopy(row, 0, pixels, y * row.Length, row.Length);
                    if (anyOpaque) continue;
                    for (var x = 0; x < width; x++)
                    {
                        var i = x * 4;
                        if (row[i + 3] > 8)
                        {
                            anyOpaque = true;
                            break;
                        }
                        if (row[i] != 0 || row[i + 1] != 0 || row[i + 2] != 0)
                            anyColor = true;
                    }
                }
                if (anyOpaque || !anyColor) return false;
                for (var i = 3; i < pixels.Length; i += 4)
                    pixels[i] = 255;
                using var output = new Drawing.Bitmap(width, height, DrawingImaging.PixelFormat.Format32bppArgb);
                var outBits = output.LockBits(
                    new Drawing.Rectangle(0, 0, width, height),
                    DrawingImaging.ImageLockMode.WriteOnly,
                    DrawingImaging.PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < height; y++)
                        Marshal.Copy(pixels, y * width * 4, outBits.Scan0 + y * outBits.Stride, width * 4);
                }
                finally
                {
                    output.UnlockBits(outBits);
                }
                output.Save(temp, DrawingImaging.ImageFormat.Png);
                changed = true;
            }
            finally
            {
                loaded.UnlockBits(bits);
            }
        }
        if (!changed) return false;
        File.Move(temp, path, overwrite: true);
        return true;
    }

    static void WriteThumb(string sourcePath, string thumbPath)
    {
        using var source = new Drawing.Bitmap(sourcePath);
        var scale = Math.Min(1, 160d / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var thumb = new Drawing.Bitmap(width, height);
        using (var graphics = Drawing.Graphics.FromImage(thumb))
        {
            graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, width, height);
        }
        var temp = thumbPath + ".fix.png";
        thumb.Save(temp, DrawingImaging.ImageFormat.Png);
        File.Move(temp, thumbPath, overwrite: true);
    }

    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    static void RemoveFiles(ClipItem item)
    {
        foreach (var path in new[] { item.ImagePath, item.ThumbPath })
        {
            if (!string.IsNullOrEmpty(path))
            {
                try { File.Delete(path); } catch { /* 文件占用时下次清理。 */ }
            }
        }
    }

    Settings LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) ?? new Settings();
        }
        catch
        {
            // 设置损坏时用默认值。
        }
        return new Settings();
    }

    void SaveSettings()
    {
        var json = JsonSerializer.Serialize(settings);
        File.WriteAllText(settingsPath, json);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    sealed record Draft(ClipKind Kind, string Text, BitmapSource? Image, string[] FilePaths, string Preview, string SourceApp, string Hash);

    sealed class Settings
    {
        public bool Paused { get; set; }
        public double Width { get; set; } = 420;
        public double Height { get; set; } = 560;
    }
}
