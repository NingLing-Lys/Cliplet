using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cliplet;

sealed class MessageHost : IDisposable
{
    const int HotkeyId = 1;
    const int WsPopup = unchecked((int)0x80000000);
    readonly HwndSource source;
    bool listening;

    public event Action? ClipboardChanged;
    public event Action? HotkeyPressed;
    public string HotkeyLabel { get; private set; } = "Ctrl+Alt+V";

    public MessageHost()
    {
        var parameters = new HwndSourceParameters("Cliplet.Message")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = WsPopup,
            ExtendedWindowStyle = NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate
        };
        source = new HwndSource(parameters);
        source.AddHook(Hook);
        listening = NativeMethods.AddClipboardFormatListener(source.Handle);
        RegisterHotkeys(source.Handle);
        if (!listening)
            Trace("AddClipboardFormatListener failed " + Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (listening)
            NativeMethods.RemoveClipboardFormatListener(source.Handle);
        NativeMethods.UnregisterHotKey(source.Handle, HotkeyId);
        source.RemoveHook(Hook);
        source.Dispose();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmClipboardUpdate)
            ClipboardChanged?.Invoke();
        else if (msg == NativeMethods.WmHotkey)
            HotkeyPressed?.Invoke();
        return IntPtr.Zero;
    }

    void RegisterHotkeys(IntPtr hwnd)
    {
        if (NativeMethods.RegisterHotKey(hwnd, HotkeyId, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, NativeMethods.VkV))
        {
            HotkeyLabel = "Ctrl+Alt+V";
            return;
        }
        if (NativeMethods.RegisterHotKey(hwnd, HotkeyId, NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat, NativeMethods.VkSpace))
        {
            HotkeyLabel = "Ctrl+Shift+Space";
            return;
        }
        HotkeyLabel = "热键被占用";
    }

    public static void Trace(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cliplet");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
        }
        catch
        {
            // 诊断日志失败不影响剪贴板。
        }
    }
}
