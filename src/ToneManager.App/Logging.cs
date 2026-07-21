using System;
using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace ToneManager.App;

/// <summary>
/// Programmatic NLog setup. Writes a single rolling-per-run file under <c>logs/</c> next to the
/// app binary so device/performance timings (see <c>SonuClient</c> and <c>ReorderService</c>) can
/// be inspected after a session. Configured in code to avoid shipping/copying an NLog.config.
/// </summary>
public static class Logging
{
    /// <summary>Configures NLog and returns the absolute path of the log file.</summary>
    public static string Configure()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sonulab.log");

        var config = new LoggingConfiguration();
        var target = new FileTarget("file")
        {
            FileName = file,
            // time | LEVEL | ShortLoggerName | message
            Layout = "${time}|${level:uppercase=true:padding=-5}|${logger:shortName=true}|${message}",
            KeepFileOpen = true,
            AutoFlush = true,   // flush each write so timings show up immediately
        };
        // Debug captures per-operation reorder phase timing + summaries (low volume). Lower this to
        // LogLevel.Trace to also capture per-command device timings (~130 lines per reorder).
        config.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
        LogManager.Configuration = config;
        return file;
    }
}
