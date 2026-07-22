using System.Runtime.InteropServices;

namespace SlideViewer;

/// <summary>
/// Makes startup failures visible.
///
/// The app is built as a Windows GUI executable (WinExe), so it has no console:
/// an unhandled exception would otherwise kill it with no message at all — the
/// window simply never appears. Every failure is written to a log file and, on
/// Windows, shown in a message box so the problem can be reported.
/// </summary>
internal static class CrashReporter
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_ICONERROR = 0x10;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SlideViewer", "crash.log");

    /// <summary>Routes unhandled exceptions from every thread to the reporter.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception ?? new Exception("Unknown error"), "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, "Background task failure");
            e.SetObserved();
        };
    }

    /// <summary>Records a non-fatal problem to the log without interrupting the
    /// user — used for things the UI already reports in place, like a camera
    /// that won't open.</summary>
    public static void Log(Exception exception, string context) =>
        Write(exception, context);

    public static void Report(Exception exception, string context)
    {
        Write(exception, context);

        var message =
            $"SlideViewer could not start.\n\n{exception.GetType().Name}: {exception.Message}\n\n" +
            $"A full report was saved to:\n{LogPath}";

        if (OperatingSystem.IsWindows())
        {
            try { MessageBoxW(IntPtr.Zero, message, "SlideViewer", MB_ICONERROR); } catch { }
        }
        else
        {
            Console.Error.WriteLine($"{context}: {exception}");
        }
    }

    private static void Write(Exception exception, string context)
    {
        var details = $"""
            SlideViewer report
            Time    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            Context : {context}
            OS      : {Environment.OSVersion} ({RuntimeInformation.OSArchitecture})
            Runtime : {RuntimeInformation.FrameworkDescription}

            {exception}
            """;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                details + Environment.NewLine + new string('-', 70) + Environment.NewLine);
        }
        catch { /* logging must never mask the original failure */ }
    }
}
