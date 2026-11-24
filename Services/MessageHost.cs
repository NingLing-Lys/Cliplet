using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace Cliplet;

public sealed class MessageHost : IDisposable
{
    const int PanelHotkeyId = 1;
    const int ScreenshotHotkeyId = 2;
    const int WsPopup = unchecked((int)0x80000000);
    readonly HwndSource source;
    readonly NativeMethods.LowLevelKeyboardProc keyboardProc;
    readonly IntPtr keyboardHook;
    bool listening;

    public event Action? ClipboardChanged;
    public event Action? HotkeyPressed;
    public event Action? ScreenshotHotkeyPressed;

    /// <summary>When true, global hotkeys are ignored (e.g. while capturing a new combo).</summary>
    public bool HotkeysSuspended { get; set; }

    public string HotkeyLabel { get; private set; } = "Ctrl+Alt+V";
    public uint HotkeyMods { get; private set; } = NativeMethods.ModControl | NativeMethods.ModAlt;
    public uint HotkeyKey { get; private set; } = NativeMethods.VkV;

    public string ScreenshotHotkeyLabel { get; private set; } = "Ctrl+Alt+A";
    public uint ScreenshotHotkeyMods { get; private set; } = NativeMethods.ModControl | NativeMethods.ModAlt;
    public uint ScreenshotHotkeyKey { get; private set; } = NativeMethods.VkA;

    public MessageHost(uint mods = 0, uint key = 0, uint screenshotMods = 0, uint screenshotKey = 0)
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

        keyboardProc = LowLevelKeyboard;
        keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            keyboardProc,
            NativeMethods.GetModuleHandle(null),
            0);
        if (keyboardHook == IntPtr.Zero)
            Trace("SetWindowsHookEx failed " + Marshal.GetLastWin32Error());

        if (key == 0)
        {
            mods = NativeMethods.ModControl | NativeMethods.ModAlt;
            key = NativeMethods.VkV;
        }

        if (!TryRegister(mods, key))
        {
            foreach (var (m, k) in DefaultFallbacks)
            {
                if (TryRegister(m, k)) break;
            }
        }

        if (screenshotKey == 0)
        {
            screenshotMods = NativeMethods.ModControl | NativeMethods.ModAlt;
            screenshotKey = NativeMethods.VkA;
        }

        if (!TryRegisterScreenshot(screenshotMods, screenshotKey))
        {
            foreach (var (m, k) in ScreenshotFallbacks)
            {
                if (m == HotkeyMods && k == HotkeyKey) continue;
                if (TryRegisterScreenshot(m, k)) break;
            }
        }

        if (!listening)
            Trace("AddClipboardFormatListener failed " + Marshal.GetLastWin32Error());
    }

    static readonly (uint Mods, uint Key)[] DefaultFallbacks =
    [
        (NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkV),
        (NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkSpace),
        (NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkV),
        (NativeMethods.ModAlt | NativeMethods.ModShift, NativeMethods.VkV),
        (NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkSpace),
    ];

    static readonly (uint Mods, uint Key)[] ScreenshotFallbacks =
    [
        (NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkA),
        (NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkA),
        (NativeMethods.ModAlt | NativeMethods.ModShift, NativeMethods.VkA),
        (NativeMethods.ModControl | NativeMethods.ModAlt, 0x58), // X
    ];

    public void Dispose()
    {
        if (keyboardHook != IntPtr.Zero)
            NativeMethods.UnhookWindowsHookEx(keyboardHook);
        if (listening)
            NativeMethods.RemoveClipboardFormatListener(source.Handle);
        NativeMethods.UnregisterHotKey(source.Handle, PanelHotkeyId);
        NativeMethods.UnregisterHotKey(source.Handle, ScreenshotHotkeyId);
        source.RemoveHook(Hook);
        source.Dispose();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmClipboardUpdate)
            ClipboardChanged?.Invoke();
        else if (msg == NativeMethods.WmHotkey)
        {
            // Backup path if LL hook is unavailable; LL path already swallows matching keys.
            var id = wParam.ToInt32();
            if (id == PanelHotkeyId)
                HotkeyPressed?.Invoke();
            else if (id == ScreenshotHotkeyId)
                ScreenshotHotkeyPressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    IntPtr LowLevelKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !HotkeysSuspended && keyboardHook != IntPtr.Zero)
        {
            var msg = wParam.ToInt32();
            if (msg is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                // Skip key-repeat (bit 30 of flags is transition state for extended; LLKHF_UP is 0x80).
                if ((info.flags & NativeMethods.LlkhfUp) == 0)
                {
                    var mods = ReadAsyncModifiers();
                    var vk = (uint)info.vkCode;
                    // Ignore pure modifier presses.
                    if (!IsModifierVk(vk))
                    {
                        if (vk == HotkeyKey && mods == HotkeyMods)
                        {
                            Dispatch(HotkeyPressed);
                            return (IntPtr)1;
                        }
                        if (vk == ScreenshotHotkeyKey && mods == ScreenshotHotkeyMods)
                        {
                            Dispatch(ScreenshotHotkeyPressed);
                            return (IntPtr)1;
                        }
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    static bool IsModifierVk(uint vk) =>
        vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C
            or 0x10 or 0x11 or 0x12; // Shift / Control / Menu

    static uint ReadAsyncModifiers()
    {
        uint mods = 0;
        if (IsDown(NativeMethods.VkLControl) || IsDown(NativeMethods.VkRControl) || IsDown(0x11))
            mods |= NativeMethods.ModControl;
        if (IsDown(NativeMethods.VkLMenu) || IsDown(NativeMethods.VkRMenu) || IsDown(0x12))
            mods |= NativeMethods.ModAlt;
        if (IsDown(NativeMethods.VkLShift) || IsDown(NativeMethods.VkRShift) || IsDown(0x10))
            mods |= NativeMethods.ModShift;
        if (IsDown(NativeMethods.VkLWin) || IsDown(NativeMethods.VkRWin))
            mods |= NativeMethods.ModWin;
        return mods;
    }

    static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    static void Dispatch(Action? action)
    {
        if (action == null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public bool TryRegister(uint mods, uint key)
    {
        mods &= NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModWin;
        if (key == 0) return false;
        if (mods == ScreenshotHotkeyMods && key == ScreenshotHotkeyKey) return false;

        NativeMethods.UnregisterHotKey(source.Handle, PanelHotkeyId);
        // LL hook is the high-priority path; RegisterHotKey only as fallback if hook failed.
        if (keyboardHook == IntPtr.Zero)
            NativeMethods.RegisterHotKey(source.Handle, PanelHotkeyId, mods | NativeMethods.ModNoRepeat, key);

        HotkeyMods = mods;
        HotkeyKey = key;
        HotkeyLabel = Format(mods, key);
        return true;
    }

    public bool TryRegisterScreenshot(uint mods, uint key)
    {
        mods &= NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModWin;
        if (key == 0) return false;
        if (mods == HotkeyMods && key == HotkeyKey) return false;

        NativeMethods.UnregisterHotKey(source.Handle, ScreenshotHotkeyId);
        if (keyboardHook == IntPtr.Zero)
            NativeMethods.RegisterHotKey(source.Handle, ScreenshotHotkeyId, mods | NativeMethods.ModNoRepeat, key);

        ScreenshotHotkeyMods = mods;
        ScreenshotHotkeyKey = key;
        ScreenshotHotkeyLabel = Format(mods, key);
        return true;
    }

    public static string Format(uint mods, uint key)
    {
        var sb = new StringBuilder();
        if ((mods & NativeMethods.ModControl) != 0) sb.Append("Ctrl+");
        if ((mods & NativeMethods.ModAlt) != 0) sb.Append("Alt+");
        if ((mods & NativeMethods.ModShift) != 0) sb.Append("Shift+");
        if ((mods & NativeMethods.ModWin) != 0) sb.Append("Win+");
        sb.Append(KeyName(key));
        return sb.ToString();
    }

    public static string KeyName(uint vk) => vk switch
    {
        NativeMethods.VkSpace => "Space",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => "0x" + vk.ToString("X")
    };

    public static IReadOnlyList<(string Label, uint Mods, uint Key)> Presets { get; } =
    [
        ("Ctrl+Alt+V", NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkV),
        ("Ctrl+Shift+V", NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkV),
        ("Ctrl+Shift+Space", NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkSpace),
        ("Alt+C", NativeMethods.ModAlt, 0x43),
        ("Ctrl+`", NativeMethods.ModControl, 0xC0),
        ("Alt+Shift+V", NativeMethods.ModAlt | NativeMethods.ModShift, NativeMethods.VkV),
        ("Ctrl+Alt+C", NativeMethods.ModControl | NativeMethods.ModAlt, 0x43),
        ("Win+Shift+V", NativeMethods.ModWin | NativeMethods.ModShift, NativeMethods.VkV),
    ];

    public static IReadOnlyList<(string Name, uint Vk)> KeyChoices { get; } =
        BuildKeyChoices();

    static List<(string Name, uint Vk)> BuildKeyChoices()
    {
        var list = new List<(string, uint)> { ("Space", NativeMethods.VkSpace) };
        for (uint c = (uint)'A'; c <= 'Z'; c++) list.Add((((char)c).ToString(), c));
        for (uint d = (uint)'0'; d <= '9'; d++) list.Add((((char)d).ToString(), d));
        for (uint f = 1; f <= 12; f++) list.Add(("F" + f, 0x6F + f));
        list.Add(("`", 0xC0));
        list.Add((";", 0xBA));
        list.Add(("=", 0xBB));
        list.Add((",", 0xBC));
        list.Add(("-", 0xBD));
        list.Add((".", 0xBE));
        list.Add(("/", 0xBF));
        list.Add(("[", 0xDB));
        list.Add(("]", 0xDD));
        list.Add(("\\", 0xDC));
        list.Add(("'", 0xDE));
        return list;
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

[StructLayout(LayoutKind.Sequential)]
struct KbdLlHookStruct
{
    public int vkCode;
    public int scanCode;
    public int flags;
    public int time;
    public IntPtr dwExtraInfo;
}
