using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using ShapePath = System.Windows.Shapes.Path;

namespace Cliplet;

/// <summary>
/// WeChat-style region snip + basic annotation toolbar.
/// </summary>
sealed class SnipOverlay : Window
{
    enum Tool { None, Rect, Arrow, Pen, Text, Mosaic }
    enum MosaicKind { Fine, Medium, Coarse, Blur }

    readonly Drawing.Bitmap freezeBitmap;
    readonly System.Windows.Controls.Image bgImage;
    readonly ShapePath dimPath;
    readonly RectangleGeometry dimOuter = new();
    readonly RectangleGeometry dimHole = new();
    readonly GeometryGroup dimGroup;
    readonly System.Windows.Shapes.Rectangle frame;
    readonly Canvas annotate;
    readonly Border toolbar;
    readonly Border optionPanel;
    readonly StackPanel optionHost;
    readonly Canvas layer;
    readonly IReadOnlyList<Rect> windowDips;
    readonly double scaleX;
    readonly double scaleY;
    readonly Stack<UIElement> undo = new();
    readonly List<ToggleButtonLite> toolButtons = [];

    Point? dragStart;
    Point? drawStart;
    UIElement? draft;
    Rect selectionDip;
    bool selecting;
    bool awaitingConfirm;
    bool captured;
    Rect lastHover;
    Tool currentTool = Tool.None;

    MediaColor penColor = MediaColor.FromRgb(0xFA, 0x51, 0x50);
    double penThickness = 3;
    MosaicKind mosaicKind = MosaicKind.Medium;
    readonly List<Border> colorChips = [];
    readonly List<Border> thickChips = [];
    readonly List<Border> mosaicChips = [];

    SnipOverlay(Drawing.Bitmap freeze, IReadOnlyList<Rect> windowDips)
    {
        freezeBitmap = freeze;
        this.windowDips = windowDips;

        var freezeSource = ToBitmapSource(freeze);
        freezeSource.Freeze();

        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = MediaBrushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = System.Windows.Input.Cursors.Cross;
        Focusable = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        scaleX = freeze.Width / Width;
        scaleY = freeze.Height / Height;

        bgImage = new System.Windows.Controls.Image
        {
            Source = freezeSource,
            Stretch = Stretch.Fill,
            Width = Width,
            Height = Height,
            IsHitTestVisible = false
        };

        dimGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dimGroup.Children.Add(dimOuter);
        dimGroup.Children.Add(dimHole);
        dimPath = new ShapePath
        {
            Fill = new SolidColorBrush(MediaColor.FromArgb(0xA0, 0x00, 0x00, 0x00)),
            Data = dimGroup,
            IsHitTestVisible = false
        };

        frame = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(MediaColor.FromRgb(0x07, 0xC1, 0x60)),
            StrokeThickness = 1,
            Fill = MediaBrushes.Transparent,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        System.Windows.Media.RenderOptions.SetEdgeMode(frame, System.Windows.Media.EdgeMode.Aliased);

        annotate = new Canvas
        {
            ClipToBounds = true,
            Background = MediaBrushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        optionHost = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        optionPanel = MakeChromeBar(optionHost, padX: 10, padY: 6);
        optionPanel.Visibility = Visibility.Collapsed;

        toolbar = BuildToolbar();
        toolbar.Visibility = Visibility.Collapsed;

        layer = new Canvas { Width = Width, Height = Height };
        layer.Children.Add(bgImage);
        layer.Children.Add(dimPath);
        layer.Children.Add(annotate);
        layer.Children.Add(frame);
        layer.Children.Add(toolbar);
        layer.Children.Add(optionPanel);
        Content = layer;

        dimOuter.Rect = new Rect(0, 0, Width, Height);
        dimHole.Rect = Rect.Empty;

        PreviewKeyDown += OnKey;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseLeftButtonUp += OnUp;
        Loaded += OnOverlayLoaded;
        Closed += (_, _) => freezeBitmap.Dispose();
    }

    void OnOverlayLoaded(object sender, RoutedEventArgs e)
    {
        // Pin the overlay to the exact virtual-screen pixel box the freeze bitmap came from.
        var vs = Forms.SystemInformation.VirtualScreen;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            vs.Left,
            vs.Top,
            vs.Width,
            vs.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);

        bgImage.Width = ActualWidth;
        bgImage.Height = ActualHeight;
        Activate();
        Focus();
    }

    Rect SnapToPixels(Rect dip)
    {
        var l = Math.Round(dip.Left * scaleX) / scaleX;
        var t = Math.Round(dip.Top * scaleY) / scaleY;
        var r = Math.Round(dip.Right * scaleX) / scaleX;
        var b = Math.Round(dip.Bottom * scaleY) / scaleY;
        return new Rect(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
    }

    static Border MakeChromeBar(UIElement child, double padX, double padY) => new()
    {
        Background = new SolidColorBrush(MediaColor.FromRgb(0x2C, 0x2C, 0x2E)),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(padX, padY, padX, padY),
        BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = child,
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 2,
            Opacity = 0.35,
            Color = Colors.Black
        }
    };

    Border BuildToolbar()
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(MakeToolIcon(Tool.Rect, "矩形", IconRect()));
        row.Children.Add(MakeToolIcon(Tool.Arrow, "箭头", IconArrow()));
        row.Children.Add(MakeToolIcon(Tool.Pen, "画笔", IconPen()));
        row.Children.Add(MakeToolIcon(Tool.Text, "文字", IconText()));
        row.Children.Add(MakeToolIcon(Tool.Mosaic, "马赛克", IconMosaic()));
        row.Children.Add(MakeActionIcon("撤销", IconUndo(), UndoLast, false));
        row.Children.Add(MakeSep());
        row.Children.Add(MakeActionIcon("保存", IconSave(), SaveToFile, false));
        row.Children.Add(MakeActionIcon("取消", IconClose(), Close, true));
        row.Children.Add(MakeActionIcon("完成", IconCheck(), Confirm, false, MediaColor.FromRgb(0x07, 0xC1, 0x60)));
        return MakeChromeBar(row, 6, 4);
    }

    static Border MakeSep() => new()
    {
        Width = 1,
        Height = 16,
        Margin = new Thickness(4, 0, 4, 0),
        Background = new SolidColorBrush(MediaColor.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center
    };

    ToggleButtonLite MakeToolIcon(Tool tool, string tip, UIElement icon)
    {
        var btn = new ToggleButtonLite(tool, icon, tip);
        btn.Click += () =>
        {
            if (currentTool == tool) SetTool(Tool.None);
            else SetTool(tool);
        };
        toolButtons.Add(btn);
        return btn;
    }

    Button MakeActionIcon(string tip, UIElement icon, Action action, bool danger, MediaColor? tint = null)
    {
        if (tint != null && icon is ShapePath path)
            path.Stroke = new SolidColorBrush(tint.Value);
        if (danger && icon is ShapePath p2)
            p2.Stroke = new SolidColorBrush(MediaColor.FromRgb(0xFA, 0x51, 0x50));

        var btn = new Button
        {
            Content = new Viewbox { Width = 15, Height = 15, Child = icon },
            Width = 28,
            Height = 28,
            Background = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = tip,
            Padding = new Thickness(0)
        };
        btn.Template = IconButtonTemplate();
        btn.Click += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return btn;
    }

    static ControlTemplate IconButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.BackgroundProperty, MediaBrushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(MediaColor.FromArgb(0x28, 0xFF, 0xFF, 0xFF)), "Bd"));
        template.Triggers.Add(trigger);
        return template;
    }

    void SetTool(Tool tool)
    {
        currentTool = tool;
        foreach (var b in toolButtons)
            b.IsOn = b.TagTool == tool;
        annotate.IsHitTestVisible = tool != Tool.None;
        Cursor = tool == Tool.None
            ? System.Windows.Input.Cursors.Arrow
            : System.Windows.Input.Cursors.Cross;
        RefreshOptionPanel();
        if (awaitingConfirm)
            PlaceToolbar(selectionDip);
    }

    void RefreshOptionPanel()
    {
        optionHost.Children.Clear();
        colorChips.Clear();
        thickChips.Clear();
        mosaicChips.Clear();

        if (currentTool == Tool.Pen)
        {
            foreach (var c in new[]
                     {
                         MediaColor.FromRgb(0xFA, 0x51, 0x50),
                         MediaColor.FromRgb(0xFA, 0x9D, 0x3B),
                         MediaColor.FromRgb(0x07, 0xC1, 0x60),
                         MediaColor.FromRgb(0x10, 0xAE, 0xFF),
                         MediaColor.FromRgb(0xFF, 0xFF, 0xFF),
                         MediaColor.FromRgb(0x1C, 0x1C, 0x1E)
                     })
                optionHost.Children.Add(MakeColorChip(c));

            optionHost.Children.Add(MakeSep());

            foreach (var t in new[] { 2.0, 3.5, 5.5, 8.0 })
                optionHost.Children.Add(MakeThickChip(t));

            optionPanel.Visibility = Visibility.Visible;
        }
        else if (currentTool == Tool.Mosaic)
        {
            optionHost.Children.Add(MakeMosaicChip(MosaicKind.Fine, "细"));
            optionHost.Children.Add(MakeMosaicChip(MosaicKind.Medium, "中"));
            optionHost.Children.Add(MakeMosaicChip(MosaicKind.Coarse, "粗"));
            optionHost.Children.Add(MakeMosaicChip(MosaicKind.Blur, "模糊"));
            optionPanel.Visibility = Visibility.Visible;
        }
        else
        {
            optionPanel.Visibility = Visibility.Collapsed;
        }
    }

    Border MakeColorChip(MediaColor color)
    {
        var ring = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(3, 0, 3, 0),
            BorderThickness = new Thickness(color.Equals(penColor) ? 2 : 0),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x07, 0xC1, 0x60)),
            Background = MediaBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(color.R > 240 && color.G > 240 && color.B > 240 ? 1 : 0)
            },
            Tag = color
        };
        ring.MouseLeftButtonUp += (_, e) =>
        {
            penColor = color;
            foreach (var chip in colorChips)
            {
                var c = (MediaColor)chip.Tag;
                chip.BorderThickness = new Thickness(c.Equals(penColor) ? 2 : 0);
            }
            e.Handled = true;
        };
        colorChips.Add(ring);
        return ring;
    }

    Border MakeThickChip(double thickness)
    {
        var selected = Math.Abs(thickness - penThickness) < 0.1;
        var chip = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2, 0, 2, 0),
            Background = selected
                ? new SolidColorBrush(MediaColor.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
                : MediaBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = thickness,
            Child = new Ellipse
            {
                Width = thickness + 2,
                Height = thickness + 2,
                Fill = new SolidColorBrush(MediaColor.FromRgb(0xF2, 0xF2, 0xF7)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            penThickness = thickness;
            foreach (var c in thickChips)
            {
                var t = (double)c.Tag;
                c.Background = Math.Abs(t - penThickness) < 0.1
                    ? new SolidColorBrush(MediaColor.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
                    : MediaBrushes.Transparent;
            }
            e.Handled = true;
        };
        thickChips.Add(chip);
        return chip;
    }

    Border MakeMosaicChip(MosaicKind kind, string label)
    {
        var selected = mosaicKind == kind;
        var chip = new Border
        {
            MinWidth = 32,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(8, 0, 8, 0),
            Background = selected
                ? new SolidColorBrush(MediaColor.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
                : MediaBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = kind,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0xF2, 0xF2, 0xF7)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            }
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            mosaicKind = kind;
            foreach (var c in mosaicChips)
            {
                var k = (MosaicKind)c.Tag;
                c.Background = k == mosaicKind
                    ? new SolidColorBrush(MediaColor.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
                    : MediaBrushes.Transparent;
            }
            e.Handled = true;
        };
        mosaicChips.Add(chip);
        return chip;
    }

    // —— Icons ——

    static ShapePath IconRect() => StrokePath("M3.5,3.5 H16.5 V16.5 H3.5 Z");

    static ShapePath IconArrow() => StrokePath("M4,16 L16,4 M10,4 H16 V10");

    // Brush / marker tip
    static ShapePath IconPen() => StrokePath(
        "M5,15 L7,17 L15.5,8.5 L13.5,6.5 Z M12.8,5.8 L15.2,8.2 M5,15 L4.2,17.8 L7,17");

    static ShapePath IconText() => StrokePath("M5,5 H15 M10,5 V16");

    static ShapePath IconMosaic() => StrokePath(
        "M3.5,3.5 H8.5 V8.5 H3.5 Z M11.5,3.5 H16.5 V8.5 H11.5 Z M3.5,11.5 H8.5 V16.5 H3.5 Z M11.5,11.5 H16.5 V16.5 H11.5 Z");

    // Lucide "undo" (ISC) — https://lucide.dev/icons/undo
    static ShapePath IconUndo()
    {
        var group = new GeometryGroup();
        group.Children.Add(Geometry.Parse("M3,7 V13 H9"));
        group.Children.Add(Geometry.Parse("M21,17 A9,9 0 0 0 12,8 A9,9 0 0 0 6,10.3 L3,13"));
        return new ShapePath
        {
            Data = group,
            Stroke = new SolidColorBrush(MediaColor.FromRgb(0xF2, 0xF2, 0xF7)),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = MediaBrushes.Transparent,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };
    }

    static ShapePath IconSave() => StrokePath("M10,3.5 V13 M6.5,9.5 L10,13 L13.5,9.5 M4.5,16.5 H15.5");

    static ShapePath IconClose() => StrokePath("M5.5,5.5 L14.5,14.5 M14.5,5.5 L5.5,14.5");

    static ShapePath IconCheck() => StrokePath("M4.5,10 L8.5,14 L15.5,6");

    static ShapePath StrokePath(string data) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = new SolidColorBrush(MediaColor.FromRgb(0xF2, 0xF2, 0xF7)),
        StrokeThickness = 1.6,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Fill = MediaBrushes.Transparent,
        Width = 20,
        Height = 20,
        Stretch = Stretch.Uniform,
        IsHitTestVisible = false
    };

    void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (currentTool != Tool.None)
            {
                SetTool(Tool.None);
                e.Handled = true;
                return;
            }
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Enter or Key.Return)
        {
            if (awaitingConfirm && selectionDip.Width >= 4)
                Confirm();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            UndoLast();
            e.Handled = true;
        }
    }

    void OnMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(layer);

        if (awaitingConfirm && currentTool != Tool.None && drawStart != null && e.LeftButton == MouseButtonState.Pressed)
        {
            ContinueDraw(e.GetPosition(annotate));
            e.Handled = true;
            return;
        }

        if (selecting && dragStart != null && e.LeftButton == MouseButtonState.Pressed)
        {
            ApplySelection(Normalize(dragStart.Value, pos), showToolbar: false);
            e.Handled = true;
            return;
        }

        if (awaitingConfirm || selecting) return;

        if (HitTestWindow(pos, out var win) && !AlmostEqual(win, lastHover))
        {
            lastHover = win;
            ApplySelection(win, showToolbar: false);
        }
        else if (!HitTestWindow(pos, out _) && lastHover.Width > 0)
        {
            lastHover = default;
            ClearSelection();
        }
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (toolbar.IsMouseOver || optionPanel.IsMouseOver) return;

        if (awaitingConfirm)
        {
            if (currentTool == Tool.None) return;
            var local = e.GetPosition(annotate);
            if (local.X < 0 || local.Y < 0 || local.X > annotate.Width || local.Y > annotate.Height)
                return;
            BeginDraw(local);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        dragStart = e.GetPosition(layer);
        selecting = true;
        awaitingConfirm = false;
        toolbar.Visibility = Visibility.Collapsed;
        optionPanel.Visibility = Visibility.Collapsed;
        annotate.Visibility = Visibility.Collapsed;
        Cursor = System.Windows.Input.Cursors.Cross;
        CaptureMouse();
        ApplySelection(new Rect(dragStart.Value, new System.Windows.Size(1, 1)), showToolbar: false);
        e.Handled = true;
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (awaitingConfirm && drawStart != null)
        {
            EndDraw(e.GetPosition(annotate));
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (!selecting || dragStart == null) return;
        ReleaseMouseCapture();
        selecting = false;

        var end = e.GetPosition(layer);
        var dragged = Math.Abs(end.X - dragStart.Value.X) > 4 || Math.Abs(end.Y - dragStart.Value.Y) > 4;

        if (dragged)
            ApplySelection(Normalize(dragStart.Value, end), showToolbar: true);
        else if (HitTestWindow(dragStart.Value, out var win))
            ApplySelection(win, showToolbar: true);
        else
            ClearSelection();

        dragStart = null;
        e.Handled = true;
    }

    SolidColorBrush PenBrush() => new(penColor);

    void BeginDraw(Point local)
    {
        drawStart = local;
        draft = null;

        switch (currentTool)
        {
            case Tool.Rect:
                draft = new System.Windows.Shapes.Rectangle
                {
                    Stroke = PenBrush(),
                    StrokeThickness = Math.Max(1.5, penThickness * 0.7),
                    Fill = MediaBrushes.Transparent
                };
                break;
            case Tool.Arrow:
                draft = new ShapePath
                {
                    Stroke = PenBrush(),
                    StrokeThickness = Math.Max(1.5, penThickness * 0.7),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Fill = PenBrush()
                };
                break;
            case Tool.Pen:
                var fig = new PathFigure { StartPoint = local, IsClosed = false };
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                draft = new ShapePath
                {
                    Stroke = PenBrush(),
                    StrokeThickness = penThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = geo,
                    Tag = fig
                };
                break;
            case Tool.Mosaic:
                draft = new System.Windows.Shapes.Rectangle
                {
                    Fill = BuildMosaicBrush(local, local),
                    Stroke = MediaBrushes.Transparent
                };
                break;
            case Tool.Text:
                PlaceText(local);
                drawStart = null;
                return;
        }

        if (draft != null)
        {
            annotate.Children.Add(draft);
            ContinueDraw(local);
        }
    }

    void ContinueDraw(Point local)
    {
        if (drawStart == null || draft == null) return;
        var a = drawStart.Value;
        var r = Normalize(a, local);

        switch (draft)
        {
            case System.Windows.Shapes.Rectangle rect when currentTool == Tool.Rect:
                Canvas.SetLeft(rect, r.X);
                Canvas.SetTop(rect, r.Y);
                rect.Width = Math.Max(1, r.Width);
                rect.Height = Math.Max(1, r.Height);
                break;
            case System.Windows.Shapes.Rectangle mosaic when currentTool == Tool.Mosaic:
                Canvas.SetLeft(mosaic, r.X);
                Canvas.SetTop(mosaic, r.Y);
                mosaic.Width = Math.Max(1, r.Width);
                mosaic.Height = Math.Max(1, r.Height);
                if (r.Width >= 6 && r.Height >= 6)
                    mosaic.Fill = BuildMosaicBrush(a, local);
                break;
            case ShapePath arrow when currentTool == Tool.Arrow:
                arrow.Data = BuildArrowGeometry(a, local);
                break;
            case ShapePath pen when currentTool == Tool.Pen && pen.Tag is PathFigure fig:
                fig.Segments.Add(new LineSegment(local, true));
                break;
        }
    }

    void EndDraw(Point local)
    {
        if (drawStart == null) { draft = null; return; }
        ContinueDraw(local);
        if (draft != null)
            undo.Push(draft);
        draft = null;
        drawStart = null;
    }

    void PlaceText(Point local)
    {
        var box = new System.Windows.Controls.TextBox
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = PenBrush(),
            Background = new SolidColorBrush(MediaColor.FromArgb(0x66, 0x00, 0x00, 0x00)),
            BorderBrush = PenBrush(),
            BorderThickness = new Thickness(1),
            MinWidth = 72,
            CaretBrush = PenBrush(),
            Padding = new Thickness(4, 2, 4, 2)
        };
        Canvas.SetLeft(box, local.X);
        Canvas.SetTop(box, local.Y);
        annotate.Children.Add(box);
        undo.Push(box);
        box.Loaded += (_, _) => box.Focus();
        box.LostKeyboardFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                annotate.Children.Remove(box);
            }
            else
            {
                var tb = new TextBlock
                {
                    Text = box.Text,
                    FontSize = box.FontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = box.Foreground
                };
                Canvas.SetLeft(tb, Canvas.GetLeft(box));
                Canvas.SetTop(tb, Canvas.GetTop(box));
                var idx = annotate.Children.IndexOf(box);
                annotate.Children.Remove(box);
                if (idx >= 0) annotate.Children.Insert(idx, tb);
                else annotate.Children.Add(tb);
                if (undo.Count > 0 && ReferenceEquals(undo.Peek(), box))
                {
                    undo.Pop();
                    undo.Push(tb);
                }
            }
        };
    }

    System.Windows.Media.Brush BuildMosaicBrush(Point a, Point b)
    {
        var r = Normalize(a, b);
        if (r.Width < 4 || r.Height < 4)
            return new SolidColorBrush(MediaColor.FromArgb(0x88, 0x80, 0x80, 0x80));

        try
        {
            var px = Clamp((int)Math.Round((selectionDip.X + r.X) * scaleX), 0, freezeBitmap.Width - 1);
            var py = Clamp((int)Math.Round((selectionDip.Y + r.Y) * scaleY), 0, freezeBitmap.Height - 1);
            var pw = Clamp((int)Math.Round(r.Width * scaleX), 1, freezeBitmap.Width - px);
            var ph = Clamp((int)Math.Round(r.Height * scaleY), 1, freezeBitmap.Height - py);
            using var crop = freezeBitmap.Clone(new Drawing.Rectangle(px, py, pw, ph), System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            Drawing.Bitmap result = mosaicKind == MosaicKind.Blur
                ? ApplyBlur(crop)
                : ApplyBlockMosaic(crop, mosaicKind switch
                {
                    MosaicKind.Fine => 8,
                    MosaicKind.Coarse => 28,
                    _ => 16
                });

            using (result)
            {
                var src = ToBitmapSource(result);
                src.Freeze();
                return new ImageBrush(src) { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.RelativeToBoundingBox };
            }
        }
        catch
        {
            return new SolidColorBrush(MediaColor.FromArgb(0xAA, 0x90, 0x90, 0x90));
        }
    }

    static Drawing.Bitmap ApplyBlockMosaic(Drawing.Bitmap source, int block)
    {
        block = Math.Max(4, block);
        var w = source.Width;
        var h = source.Height;
        var output = new Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var srcData = source.LockBits(new Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var dstData = output.LockBits(new Drawing.Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var srcStride = srcData.Stride;
            var dstStride = dstData.Stride;
            var srcBytes = new byte[Math.Abs(srcStride) * h];
            var dstBytes = new byte[Math.Abs(dstStride) * h];
            Marshal.Copy(srcData.Scan0, srcBytes, 0, srcBytes.Length);

            for (var by = 0; by < h; by += block)
            {
                for (var bx = 0; bx < w; bx += block)
                {
                    long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    var count = 0;
                    var yMax = Math.Min(by + block, h);
                    var xMax = Math.Min(bx + block, w);
                    for (var y = by; y < yMax; y++)
                    {
                        var row = y * Math.Abs(srcStride);
                        for (var x = bx; x < xMax; x++)
                        {
                            var i = row + x * 4;
                            sumB += srcBytes[i];
                            sumG += srcBytes[i + 1];
                            sumR += srcBytes[i + 2];
                            sumA += srcBytes[i + 3];
                            count++;
                        }
                    }
                    if (count == 0) continue;
                    var b = (byte)(sumB / count);
                    var g = (byte)(sumG / count);
                    var r = (byte)(sumR / count);
                    var a = (byte)(sumA / count);
                    for (var y = by; y < yMax; y++)
                    {
                        var row = y * Math.Abs(dstStride);
                        for (var x = bx; x < xMax; x++)
                        {
                            var i = row + x * 4;
                            dstBytes[i] = b;
                            dstBytes[i + 1] = g;
                            dstBytes[i + 2] = r;
                            dstBytes[i + 3] = a;
                        }
                    }
                }
            }

            Marshal.Copy(dstBytes, 0, dstData.Scan0, dstBytes.Length);
        }
        finally
        {
            source.UnlockBits(srcData);
            output.UnlockBits(dstData);
        }
        return output;
    }

    static Drawing.Bitmap ApplyBlur(Drawing.Bitmap source)
    {
        // Box blur via scaledown / scaleup — soft WeChat-like blur
        var w = source.Width;
        var h = source.Height;
        var factor = 8;
        var sw = Math.Max(1, w / factor);
        var sh = Math.Max(1, h / factor);
        using var small = new Drawing.Bitmap(sw, sh);
        using (var g = Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, 0, 0, sw, sh);
        }
        var output = new Drawing.Bitmap(w, h);
        using (var g = Drawing.Graphics.FromImage(output))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(small, 0, 0, w, h);
        }
        return output;
    }

    static Geometry BuildArrowGeometry(Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return Geometry.Empty;
        var ux = dx / len;
        var uy = dy / len;
        var head = Math.Min(16, len * 0.35);
        var hx = to.X - ux * head;
        var hy = to.Y - uy * head;
        var px = -uy;
        var py = ux;
        var wing = head * 0.45;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(to, true, true);
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(new Point(hx + px * wing, hy + py * wing), true, true);
            ctx.LineTo(new Point(hx - px * wing, hy - py * wing), true, true);
        }
        geo.Freeze();
        return geo;
    }

    void UndoLast()
    {
        if (undo.Count == 0) return;
        var el = undo.Pop();
        annotate.Children.Remove(el);
    }

    void SaveToFile()
    {
        var image = RenderResult();
        if (image == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png",
            FileName = $"Cliplet-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(dialog.FileName);
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            MessageHost.Trace("save snip failed: " + ex.Message);
        }
    }

    void ApplySelection(Rect dip, bool showToolbar)
    {
        if (dip.Width < 1 || dip.Height < 1)
        {
            ClearSelection();
            return;
        }

        dip = SnapToPixels(dip);
        selectionDip = dip;
        frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(frame, dip.X);
        Canvas.SetTop(frame, dip.Y);
        frame.Width = Math.Max(1, dip.Width);
        frame.Height = Math.Max(1, dip.Height);
        dimHole.Rect = dip;

        Canvas.SetLeft(annotate, dip.X);
        Canvas.SetTop(annotate, dip.Y);
        annotate.Width = dip.Width;
        annotate.Height = dip.Height;
        annotate.Visibility = Visibility.Visible;

        if (showToolbar)
        {
            awaitingConfirm = true;
            Cursor = System.Windows.Input.Cursors.Arrow;
            PlaceToolbar(dip);
            toolbar.Visibility = Visibility.Visible;
            RefreshOptionPanel();
        }
    }

    void ClearSelection()
    {
        selectionDip = default;
        frame.Visibility = Visibility.Collapsed;
        toolbar.Visibility = Visibility.Collapsed;
        optionPanel.Visibility = Visibility.Collapsed;
        annotate.Visibility = Visibility.Collapsed;
        annotate.Children.Clear();
        undo.Clear();
        awaitingConfirm = false;
        currentTool = Tool.None;
        foreach (var b in toolButtons) b.IsOn = false;
        Cursor = System.Windows.Input.Cursors.Cross;
        dimHole.Rect = Rect.Empty;
    }

    void PlaceToolbar(Rect dip)
    {
        toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = toolbar.DesiredSize;
        var x = dip.X + (dip.Width - size.Width) / 2;
        var y = dip.Y + dip.Height + 10;
        if (y + size.Height > Height - 8)
            y = dip.Y - size.Height - 10;
        if (y < 8) y = 8;
        if (x < 8) x = 8;
        if (x + size.Width > Width - 8)
            x = Width - size.Width - 8;
        Canvas.SetLeft(toolbar, x);
        Canvas.SetTop(toolbar, y);

        if (optionPanel.Visibility == Visibility.Visible)
        {
            optionPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var os = optionPanel.DesiredSize;
            var ox = x + (size.Width - os.Width) / 2;
            var oy = y + size.Height + 6;
            if (oy + os.Height > Height - 8)
                oy = y - os.Height - 6;
            if (ox < 8) ox = 8;
            if (ox + os.Width > Width - 8) ox = Width - os.Width - 8;
            Canvas.SetLeft(optionPanel, ox);
            Canvas.SetTop(optionPanel, oy);
        }
    }

    void Confirm()
    {
        var image = RenderResult();
        if (image == null) return;
        try
        {
            System.Windows.Clipboard.SetImage(image);
            captured = true;
        }
        catch (Exception ex)
        {
            MessageHost.Trace("snip confirm failed: " + ex.Message);
        }
        Close();
    }

    BitmapSource? RenderResult()
    {
        if (selectionDip.Width < 4 || selectionDip.Height < 4) return null;
        try
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
                Keyboard.ClearFocus();

            var px = Clamp((int)Math.Round(selectionDip.X * scaleX), 0, freezeBitmap.Width - 1);
            var py = Clamp((int)Math.Round(selectionDip.Y * scaleY), 0, freezeBitmap.Height - 1);
            var pw = Clamp((int)Math.Round(selectionDip.Width * scaleX), 1, freezeBitmap.Width - px);
            var ph = Clamp((int)Math.Round(selectionDip.Height * scaleY), 1, freezeBitmap.Height - py);

            using var crop = freezeBitmap.Clone(new Drawing.Rectangle(px, py, pw, ph), freezeBitmap.PixelFormat);
            var baseImage = ToBitmapSource(crop);
            baseImage.Freeze();

            if (annotate.Children.Count == 0)
                return baseImage;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(baseImage, new Rect(0, 0, pw, ph));
                var brush = new VisualBrush(annotate)
                {
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                dc.DrawRectangle(brush, null, new Rect(0, 0, pw, ph));
            }
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            MessageHost.Trace("render snip failed: " + ex.Message);
            return null;
        }
    }

    bool HitTestWindow(Point dip, out Rect winDip)
    {
        foreach (var r in windowDips)
        {
            if (r.Contains(dip))
            {
                winDip = r;
                return true;
            }
        }
        winDip = default;
        return false;
    }

    static bool AlmostEqual(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
        && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    static Rect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    /// <summary>
    /// Physical pixel rect → overlay DIPs, edges snapped to device pixels
    /// so the selection lines up with the frozen screenshot.
    /// </summary>
    static Rect PixelRectToDip(NativeRect rc, int screenX, int screenY, double scaleX, double scaleY, Rect view)
    {
        var left = (rc.Left - screenX) / scaleX;
        var top = (rc.Top - screenY) / scaleY;
        var right = (rc.Right - screenX) / scaleX;
        var bottom = (rc.Bottom - screenY) / scaleY;
        var dip = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        dip.Intersect(view);
        return dip;
    }

    static List<Rect> CollectWindowDips(int screenX, int screenY, double scaleX, double scaleY, double viewW, double viewH)
    {
        var list = new List<Rect>();
        var view = new Rect(0, 0, viewW, viewH);
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                return true;
            // DWM visible frame — GetWindowRect includes invisible Win10+ borders (looks loose).
            if (!NativeMethods.TryGetVisibleWindowRect(hwnd, out var rc))
                return true;

            var w = rc.Right - rc.Left;
            var h = rc.Bottom - rc.Top;
            if (w < 40 || h < 40) return true;

            var dip = PixelRectToDip(rc, screenX, screenY, scaleX, scaleY, view);
            if (dip.Width < 40 || dip.Height < 40) return true;
            if (dip.Width > viewW - 2 && dip.Height > viewH - 2) return true;

            list.Add(dip);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool Run()
    {
        var vs = Forms.SystemInformation.VirtualScreen;
        var viewW = SystemParameters.VirtualScreenWidth;
        var viewH = SystemParameters.VirtualScreenHeight;
        var scaleX = vs.Width / Math.Max(1.0, viewW);
        var scaleY = vs.Height / Math.Max(1.0, viewH);

        var windows = CollectWindowDips(vs.Left, vs.Top, scaleX, scaleY, viewW, viewH);

        Drawing.Bitmap freeze;
        try
        {
            freeze = new Drawing.Bitmap(vs.Width, vs.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Drawing.Graphics.FromImage(freeze);
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, freeze.Size, Drawing.CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex)
        {
            MessageHost.Trace("freeze screen failed: " + ex.Message);
            return false;
        }

        var overlay = new SnipOverlay(freeze, windows);
        overlay.ShowDialog();
        return overlay.captured;
    }

    sealed class ToggleButtonLite : Border
    {
        public Tool TagTool { get; }
        public event Action? Click;
        bool on;

        public bool IsOn
        {
            get => on;
            set
            {
                on = value;
                Background = on
                    ? new SolidColorBrush(MediaColor.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
                    : MediaBrushes.Transparent;
            }
        }

        public ToggleButtonLite(Tool tool, UIElement icon, string tip)
        {
            TagTool = tool;
            Width = 28;
            Height = 28;
            CornerRadius = new CornerRadius(6);
            Background = MediaBrushes.Transparent;
            Cursor = System.Windows.Input.Cursors.Hand;
            ToolTip = tip;
            Child = new Viewbox
            {
                Width = 15,
                Height = 15,
                Child = icon,
                IsHitTestVisible = false
            };
            MouseLeftButtonUp += (_, e) =>
            {
                Click?.Invoke();
                e.Handled = true;
            };
            MouseEnter += (_, _) =>
            {
                if (!on) Background = new SolidColorBrush(MediaColor.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            };
            MouseLeave += (_, _) =>
            {
                if (!on) Background = MediaBrushes.Transparent;
            };
        }
    }
}
