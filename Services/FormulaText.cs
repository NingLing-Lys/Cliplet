using System.Globalization;
using System.IO;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using XamlMath.Exceptions;

namespace Cliplet;

static class FormulaText
{
    const int MaxFormulas = 40;

    static readonly Regex Environment = new(
        @"\\begin\{((?:equation|align|aligned|gather|eqnarray|multline)\*?)\}([\s\S]+?)\\end\{\1\}",
        RegexOptions.Compiled);
    static readonly Regex Fence = new(
        @"```(?:math|latex|tex)\s*([\s\S]+?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Annotation = new(
        @"<annotation\b[^>]*encoding\s*=\s*[""']application/x-tex[""'][^>]*>(.*?)</annotation>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex DataLatex = new(
        @"data-(?:latex|tex)\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Label = new(@"\\(?:label|tag)\{[^{}]*\}", RegexOptions.Compiled);

    readonly record struct Span(int Start, int End, string Latex, bool Display);

    sealed class Piece
    {
        public string Text { get; init; } = "";
        public bool Math { get; init; }
        public bool Display { get; init; }
        public BitmapSource? Image { get; init; }

        public static Piece Plain(string text) => new() { Text = text };
    }

    public static bool ContainsFormula(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return FindNext(text, 0) != null || IsBareLatex(text.Trim());
    }

    public static string AttachHiddenFormulas(string text, string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || ContainsFormula(text)) return text;
        var found = new List<string>();
        foreach (Match match in Annotation.Matches(html))
            AddFormula(found, WebUtility.HtmlDecode(match.Groups[1].Value));
        foreach (Match match in DataLatex.Matches(html))
            AddFormula(found, WebUtility.HtmlDecode(match.Groups[1].Value));
        if (found.Count == 0) return text;
        var blocks = string.Join("\n\n", found.Select(formula => "$$\n" + formula + "\n$$"));
        return text.TrimEnd() + "\n\n" + blocks;
    }

    public static FlowDocument? BuildDocument(string text, Color textColor, Color formulaColor)
    {
        var pieces = Compose(text, formulaColor);
        if (!pieces.Any(piece => piece.Math)) return null;

        var brush = new SolidColorBrush(textColor);
        if (brush.CanFreeze) brush.Freeze();
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = brush,
            TextAlignment = TextAlignment.Left
        };
        Paragraph? current = null;

        void Ensure()
        {
            if (current != null) return;
            current = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            document.Blocks.Add(current);
        }

        foreach (var piece in pieces)
        {
            if (!piece.Math)
            {
                var lines = piece.Text.Replace("\r", "").Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    if (index > 0 && current is { Inlines.Count: > 0 })
                        current.Inlines.Add(new LineBreak());
                    if (lines[index].Length == 0) continue;
                    Ensure();
                    current!.Inlines.Add(new Run(lines[index]));
                }
                continue;
            }

            if (piece.Display)
            {
                current = null;
                var block = new Paragraph
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    TextAlignment = TextAlignment.Center
                };
                block.Inlines.Add(InlineOf(piece));
                document.Blocks.Add(block);
                continue;
            }

            Ensure();
            current!.Inlines.Add(InlineOf(piece));
        }

        return document.Blocks.Count == 0 ? null : document;
    }

    public static string? BuildRtf(string text)
    {
        var pieces = Compose(text, Colors.Black);
        if (!pieces.Any(piece => piece.Image != null)) return null;

        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg936\deff0{\fonttbl{\f0\fnil Microsoft YaHei;}}\viewkind4\uc1\pard\f0\fs24 ");
        foreach (var piece in pieces)
        {
            if (!piece.Math)
            {
                rtf.Append(RtfText(piece.Text));
                continue;
            }

            if (piece.Image == null)
            {
                rtf.Append(RtfText(piece.Display ? "$$" + piece.Text + "$$" : "$" + piece.Text + "$"));
                continue;
            }

            if (piece.Display) rtf.Append(@"\par ");
            AppendPict(rtf, piece.Image);
            if (piece.Display) rtf.Append(@"\par ");
        }
        rtf.Append('}');
        return rtf.ToString();
    }

    public static BitmapSource? RenderSheet(string text)
    {
        var pieces = Compose(text, Colors.Black);
        if (!pieces.Any(piece => piece.Image != null)) return null;

        const double pageWidth = 640;
        const double pad = 16;
        var contentWidth = pageWidth - pad * 2;
        var typeface = new Typeface("Microsoft YaHei UI");
        var ops = new List<Action<DrawingContext>>();
        var y = pad;

        foreach (var piece in pieces)
        {
            if (!piece.Math)
            {
                foreach (var line in piece.Text.Replace("\r", "").Split('\n'))
                {
                    if (line.Length == 0)
                    {
                        y += 8;
                        continue;
                    }
                    var formatted = new FormattedText(
                        line,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        15,
                        Brushes.Black,
                        1.0)
                    {
                        MaxTextWidth = contentWidth
                    };
                    var top = y;
                    ops.Add(dc => dc.DrawText(formatted, new Point(pad, top)));
                    y += formatted.Height + 3;
                    if (y > 3600) break;
                }
            }
            else if (piece.Image != null)
            {
                var scale = Math.Min(1, contentWidth / Math.Max(1, piece.Image.Width));
                var w = piece.Image.Width * scale;
                var h = piece.Image.Height * scale;
                var x = piece.Display ? pad + Math.Max(0, (contentWidth - w) / 2) : pad;
                y += 4;
                var top = y;
                var image = piece.Image;
                ops.Add(dc => dc.DrawImage(image, new Rect(x, top, w, h)));
                y += h + 4;
            }
            else
            {
                var raw = piece.Display ? "$$" + piece.Text + "$$" : "$" + piece.Text + "$";
                var formatted = new FormattedText(raw, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 14, Brushes.Black, 1.0)
                {
                    MaxTextWidth = contentWidth
                };
                var top = y;
                ops.Add(dc => dc.DrawText(formatted, new Point(pad, top)));
                y += formatted.Height + 3;
            }

            if (y > 3600) break;
        }

        y += pad;
        var height = Math.Max(1, (int)Math.Ceiling(y));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageWidth, height));
            foreach (var op in ops) op(context);
        }

        var bitmap = new RenderTargetBitmap((int)pageWidth, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static string OptimizePrompt(string text)
    {
        var body = text.Trim();
        if (body.Length > 8000) body = body[..8000] + "\n…";
        if (ContainsFormula(body) || ContainsLatexCommand(body))
        {
            return "下面这段在复制后公式无法正常显示。请保持原意和结论，把所有公式改写成 LaTeX：行内公式用 $...$，单独成行的公式用 $$...$$。不要用图片，不要省略符号第一次出现时的含义。\n\n原文：\n" + body;
        }

        return "请回答下面的问题。先给结论，再写必要步骤。公式一律用 LaTeX，行内用 $...$，独立公式用 $$...$$，不要用图片。符号第一次出现时说明含义。如果缺条件，先列出你采用的假设。\n\n问题：\n" + body;
    }

    static void AddFormula(List<string> found, string latex)
    {
        var body = latex.Trim();
        if (body.Length is 0 or > 2000) return;
        if (found.Contains(body) || found.Count >= 8) return;
        found.Add(body);
    }

    static List<Piece> Compose(string text, Color formulaColor)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var spans = new List<Span>();
        var index = 0;
        while (index < text.Length && spans.Count < MaxFormulas)
        {
            var span = FindNext(text, index);
            if (span == null) break;
            spans.Add(span.Value);
            index = span.Value.End;
        }

        var trimmed = text.Trim();
        if (spans.Count == 0 && IsBareLatex(trimmed))
            spans.Add(new Span(text.IndexOf(trimmed, StringComparison.Ordinal), text.IndexOf(trimmed, StringComparison.Ordinal) + trimmed.Length, trimmed, true));
        if (spans.Count == 0) return [];

        var pieces = new List<Piece>();
        var cursor = 0;
        foreach (var span in spans)
        {
            if (span.Start > cursor)
                pieces.Add(Piece.Plain(text[cursor..span.Start]));
            pieces.Add(new Piece
            {
                Math = true,
                Display = span.Display,
                Text = span.Latex,
                Image = TryRender(span.Latex, span.Display, formulaColor)
            });
            cursor = span.End;
        }
        if (cursor < text.Length)
            pieces.Add(Piece.Plain(text[cursor..]));
        return pieces;
    }

    static Span? FindNext(string text, int from)
    {
        Span? best = null;
        void Consider(Span? candidate)
        {
            if (candidate == null) return;
            if (best == null || candidate.Value.Start < best.Value.Start ||
                (candidate.Value.Start == best.Value.Start && candidate.Value.End > best.Value.End))
                best = candidate;
        }

        Consider(FindRegex(text, from, Fence, display: true));
        Consider(FindWrapped(text, from, "$$", "$$", display: true, checkMath: false));
        Consider(FindWrapped(text, from, "\\[", "\\]", display: true, checkMath: false));
        Consider(FindWrapped(text, from, "\\(", "\\)", display: false, checkMath: false));
        Consider(FindRegex(text, from, Environment, display: true));
        Consider(FindInlineDollar(text, from));
        return best;
    }

    static Span? FindRegex(string text, int from, Regex regex, bool display)
    {
        var match = regex.Match(text, from);
        while (match.Success)
        {
            var latex = match.Groups[match.Groups.Count - 1].Value.Trim();
            if (latex.Length is > 0 and <= 2500)
                return new Span(match.Index, match.Index + match.Length, latex, display);
            match = match.NextMatch();
        }
        return null;
    }

    static Span? FindWrapped(string text, int from, string open, string close, bool display, bool checkMath)
    {
        var search = from;
        while (search < text.Length)
        {
            var start = text.IndexOf(open, search, StringComparison.Ordinal);
            if (start < 0) return null;
            var contentStart = start + open.Length;
            var end = text.IndexOf(close, contentStart, StringComparison.Ordinal);
            if (end < 0) return null;
            var latex = text[contentStart..end].Trim();
            if (latex.Length is > 0 and <= 2500 && (!checkMath || LooksLikeMath(latex)))
                return new Span(start, end + close.Length, latex, display);
            search = start + open.Length;
        }
        return null;
    }

    static Span? FindInlineDollar(string text, int from)
    {
        var search = from;
        while (search < text.Length)
        {
            var start = text.IndexOf('$', search);
            if (start < 0 || start + 1 >= text.Length) return null;
            if (text[start + 1] == '$')
            {
                search = start + 2;
                continue;
            }
            var end = text.IndexOf('$', start + 1);
            if (end < 0) return null;
            if (text.IndexOf('\n', start, end - start) >= 0)
            {
                search = start + 1;
                continue;
            }
            var latex = text[(start + 1)..end].Trim();
            if (LooksLikeMath(latex))
                return new Span(start, end + 1, latex, false);
            search = start + 1;
        }
        return null;
    }

    static bool LooksLikeMath(string body)
    {
        var value = body.Trim();
        if (value.Length is 0 or > 300) return false;
        if (value.Any(ch => ch is >= '\u4e00' and <= '\u9fff')) return value.Contains('\\');
        if (Regex.IsMatch(value, @"^\d+([.,]\d+)?$")) return false;
        if (value.Contains('\\') || value.Contains('^') || value.Contains('_') || value.Contains('=') || value.Contains('{'))
            return true;
        if ((value.Contains('+') || value.Contains('-') || value.Contains('/') || value.Contains('*') || value.Contains('(')) &&
            value.Any(char.IsLetter))
            return true;
        return Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9]{0,8}$");
    }

    static bool IsBareLatex(string text) =>
        text.Length is > 1 and <= 2000 && text.StartsWith('\\') && ContainsLatexCommand(text);

    static bool ContainsLatexCommand(string text) =>
        Regex.IsMatch(text, @"\\(?:frac|sum|int|iint|sqrt|alpha|beta|gamma|theta|lambda|left|right|begin|cdot|times|infty|partial|nabla|mathrm|mathbf|vec|overline|hat)\b");

    static BitmapSource? TryRender(string latex, bool display, Color color)
    {
        var body = Label.Replace(latex, "").Trim();
        if (body.Length == 0) return null;
        var direct = RenderOne(body, display, color) ?? RenderOne(body.Replace("&", " "), display, color);
        if (direct != null) return direct;

        var rows = Regex.Split(body, @"\\\\")
            .Select(row => row.Replace("&", " ").Trim())
            .Where(row => row.Length > 0)
            .Select(row => RenderOne(row, true, color))
            .OfType<BitmapSource>()
            .ToArray();
        return rows.Length == 0 ? null : Stack(rows);
    }

    static BitmapSource? RenderOne(string latex, bool display, Color color)
    {
        try
        {
            var formula = WpfTeXFormulaParser.Instance.Parse(latex);
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            var environment = WpfTeXEnvironment.Create(
                display ? TexStyle.Display : TexStyle.Text,
                display ? 18 : 15,
                "Segoe UI",
                brush);
            var bitmap = formula.RenderToBitmap(environment);
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is TexException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    static BitmapSource Stack(IReadOnlyList<BitmapSource> images)
    {
        const double gap = 6;
        var width = images.Max(image => image.Width);
        var height = images.Sum(image => image.Height) + gap * (images.Count - 1);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var y = 0.0;
            foreach (var image in images)
            {
                context.DrawImage(image, new Rect(Math.Max(0, (width - image.Width) / 2), y, image.Width, image.Height));
                y += image.Height + gap;
            }
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width)),
            Math.Max(1, (int)Math.Ceiling(height)),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static Inline InlineOf(Piece piece)
    {
        if (piece.Image == null)
        {
            return new Run(piece.Display ? "$$" + piece.Text + "$$" : "$" + piece.Text + "$")
            {
                FontFamily = new FontFamily("Consolas")
            };
        }

        var image = new System.Windows.Controls.Image
        {
            Source = piece.Image,
            Stretch = Stretch.Uniform,
            MaxHeight = piece.Display ? 150 : 32,
            SnapsToDevicePixels = true
        };
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
    }

    static void AppendPict(StringBuilder rtf, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var dpiX = image.DpiX > 1 ? image.DpiX : 96;
        var dpiY = image.DpiY > 1 ? image.DpiY : 96;
        var goalW = Math.Max(1, (int)Math.Round(image.PixelWidth * 1440.0 / dpiX));
        var goalH = Math.Max(1, (int)Math.Round(image.PixelHeight * 1440.0 / dpiY));
        rtf.Append(@"{\pict\pngblip\picw").Append(image.PixelWidth)
            .Append(@"\pich").Append(image.PixelHeight)
            .Append(@"\picwgoal").Append(goalW)
            .Append(@"\pichgoal").Append(goalH)
            .Append(' ');
        foreach (var value in stream.ToArray())
            rtf.Append(value.ToString("X2"));
        rtf.Append('}');
    }

    static string RtfText(string text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text.Replace("\r", ""))
        {
            switch (ch)
            {
                case '\\' or '{' or '}':
                    builder.Append('\\').Append(ch);
                    break;
                case '\n':
                    builder.Append(@"\par ");
                    break;
                default:
                    if (ch < 128)
                        builder.Append(ch);
                    else
                        builder.Append(@"\u").Append((short)ch).Append('?');
                    break;
            }
        }
        return builder.ToString();
    }
}
