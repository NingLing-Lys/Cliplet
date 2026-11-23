using System.IO;
using System.Windows;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;
using Registry = Microsoft.Win32.Registry;

namespace Cliplet;

sealed class AppHost : IDisposable
{
    readonly Mutex mutex;
    readonly bool ownsMutex;
    readonly ClipStore store;
    readonly MessageHost messages;
    readonly MainWindow window;
    readonly Forms.NotifyIcon tray;
    readonly TrayAnimation animation;
    bool disposed;

    public AppHost()
    {
        mutex = new Mutex(true, @"Local\Cliplet.Windows.SingleInstance", out ownsMutex);
        if (!ownsMutex)
            throw new InvalidOperationException("Cliplet 已在运行，看一下任务栏右下角。");

        Theme.Apply();
        store = new ClipStore();
        messages = new MessageHost();
        window = new MainWindow(store);
        tray = new Forms.NotifyIcon
        {
            Text = "Cliplet",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        animation = new TrayAnimation(tray);
        tray.MouseDown += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                window.PrepareTrayToggle();
        };
        tray.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                window.ToggleFromTray();
        };
    }

    public void Start()
    {
        messages.ClipboardChanged += () => store.OnClipboardChanged();
        messages.HotkeyPressed += () => window.ToggleFromHotkey();
        store.HotkeyLabel = messages.HotkeyLabel;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.AllowClose();
        messages.Dispose();
        animation.Dispose();
        tray.Visible = false;
        tray.Dispose();
        store.Dispose();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        var show = new Forms.ToolStripMenuItem("显示剪贴板");
        var pause = new Forms.ToolStripMenuItem("暂停记录");
        var clear = new Forms.ToolStripMenuItem("清空未置顶");
        var startup = new Forms.ToolStripMenuItem("开机启动");
        var exit = new Forms.ToolStripMenuItem("退出 Cliplet");
        show.Click += (_, _) => window.ShowPanel();
        pause.Click += (_, _) => store.TogglePaused();
        clear.Click += (_, _) => store.ClearUnpinned();
        startup.Click += (_, _) => LoginStartup.Set(!LoginStartup.IsEnabled);
        exit.Click += (_, _) =>
        {
            window.AllowClose();
            System.Windows.Application.Current.Shutdown();
        };
        menu.Opening += (_, _) =>
        {
            pause.Checked = store.Paused;
            pause.Text = store.Paused ? "继续记录" : "暂停记录";
            startup.Checked = LoginStartup.IsEnabled;
        };
        menu.Items.Add(show);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(pause);
        menu.Items.Add(clear);
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

}

sealed class TrayAnimation : IDisposable
{
    readonly Forms.NotifyIcon tray;
    readonly Drawing.Icon[] frames;
    readonly Forms.Timer timer;
    int index;

    public TrayAnimation(Forms.NotifyIcon tray)
    {
        this.tray = tray;
        frames = LoadFrames();
        tray.Icon = frames[0];
        timer = new Forms.Timer { Interval = 40 };
        timer.Tick += (_, _) =>
        {
            index = (index + 1) % frames.Length;
            tray.Icon = frames[index];
        };
        if (frames.Length > 1)
            timer.Start();
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Dispose();
        tray.Icon = null;
        foreach (var frame in frames)
            NativeMethods.DestroyIcon(frame.Handle);
    }

    static Drawing.Icon[] LoadFrames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.gif");
        using var image = Drawing.Image.FromFile(path);
        var dimension = new Drawing.Imaging.FrameDimension(image.FrameDimensionsList[0]);
        var count = image.GetFrameCount(dimension);
        const int size = 48;
        var icons = new Drawing.Icon[count];
        for (var i = 0; i < count; i++)
        {
            image.SelectActiveFrame(dimension, i);
            using var source = new Drawing.Bitmap(image);
            Premultiply(source);
            using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(Drawing.Color.Transparent);
                graphics.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
                graphics.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Drawing.Rectangle(0, 0, size, size));
            }
            Unpremultiply(bitmap);
            icons[i] = Drawing.Icon.FromHandle(bitmap.GetHicon());
        }
        return icons;
    }

    static void Premultiply(Drawing.Bitmap bitmap) => AdjustAlpha(bitmap, premultiply: true);

    static void Unpremultiply(Drawing.Bitmap bitmap) => AdjustAlpha(bitmap, premultiply: false);

    static void AdjustAlpha(Drawing.Bitmap bitmap, bool premultiply)
    {
        var data = bitmap.LockBits(
            new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Drawing.Imaging.ImageLockMode.ReadWrite,
            Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = Math.Abs(data.Stride) * bitmap.Height;
            var pixels = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, bytes);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha == 0)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    continue;
                }
                if (alpha == 255)
                    continue;
                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[i + channel] = premultiply
                        ? (byte)(pixels[i + channel] * alpha / 255)
                        : (byte)Math.Min(255, pixels[i + channel] * 255 / alpha);
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, bytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

static class LoginStartup
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Cliplet";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key == null) return;
        if (enabled)
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
                key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

static class Theme
{
    public static void Apply()
    {
        var light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                light = value != 0;
        }
        catch
        {
            light = true;
        }

        var resources = System.Windows.Application.Current.Resources;
        if (light)
        {
            Put(resources, "WindowBg", "#F6F6F7");
            Put(resources, "CardBg", "#FFFFFF");
            Put(resources, "TextBrush", "#1C1C1E");
            Put(resources, "SecondaryBrush", "#6E6E73");
            Put(resources, "AccentBrush", "#2563EB");
            Put(resources, "LineBrush", "#E4E4E8");
            Put(resources, "ItemHover", "#14000000");
            Put(resources, "ItemSelected", "#332563EB");
        }
        else
        {
            Put(resources, "WindowBg", "#17171A");
            Put(resources, "CardBg", "#232328");
            Put(resources, "TextBrush", "#F3F3F5");
            Put(resources, "SecondaryBrush", "#A0A0A8");
            Put(resources, "AccentBrush", "#7AA2FF");
            Put(resources, "LineBrush", "#34343A");
            Put(resources, "ItemHover", "#14FFFFFF");
            Put(resources, "ItemSelected", "#337AA2FF");
        }
    }

    static void Put(ResourceDictionary resources, string key, string hex) =>
        resources[key] = new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(hex)!);
}
