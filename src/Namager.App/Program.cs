using Avalonia;
using System;

namespace Namager.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var logPath = Logging.Configure();
        var log = NLog.LogManager.GetCurrentClassLogger();
        log.Info("===== NAMager for Sonulab started; logging to {0} =====", logPath);
        // Last-resort diagnostics: crashes previously ONLY appeared in the Windows event log
        // (nothing in tonemanager.log), which slowed the field-crash investigations. The real
        // protection is the per-command guards in the ViewModels; these just guarantee a record.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error(e.Exception, "unobserved task exception");
            e.SetObserved();
        };
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // The UI loop has already unwound — the app is going down. Log it where the user's
            // logs live, then rethrow so the OS crash reporting (event log/WER) still fires.
            log.Fatal(ex, "unhandled exception — application terminating");
            throw;
        }
        finally
        {
            NLog.LogManager.Shutdown();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
