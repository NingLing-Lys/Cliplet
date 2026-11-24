using System.Windows;

namespace Cliplet;

static class ScreenCapture
{
    /// <summary>
    /// Built-in region snip (no ms-screenclip / Snipping Tool). Result goes to the clipboard.
    /// </summary>
    public static void StartRegionSnip()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null)
        {
            SnipOverlay.Run();
            return;
        }

        if (app.Dispatcher.CheckAccess())
            SnipOverlay.Run();
        else
            app.Dispatcher.Invoke(SnipOverlay.Run);
    }
}
