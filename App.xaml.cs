using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Cliplet;

public partial class App : System.Windows.Application
{
    AppHost? host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            host = new AppHost();
            host.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cliplet");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        host?.Dispose();
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cliplet");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), e.Exception + Environment.NewLine);
        }
        catch
        {
            // 日志写失败时仍然把异常交给系统。
        }
    }
}
