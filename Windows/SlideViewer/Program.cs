using Avalonia;

namespace SlideViewer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CrashReporter.Install();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            // Without this the window just never appears and the user sees nothing.
            CrashReporter.Report(ex, "Startup");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
