using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SlideViewer.Views;

namespace SlideViewer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();

            // Headless verification mode: --render <deck.pptx> <index> <out.png>
            var renderAt = Array.IndexOf(args, "--render");
            if (renderAt >= 0 && args.Length >= renderAt + 4)
            {
                var code = Views.RenderTool.Run(args[renderAt + 1],
                    int.TryParse(args[renderAt + 2], out var i) ? i : 0, args[renderAt + 3]);
                Environment.Exit(code);
                return;
            }

            // Self-test: exercises window construction, opening a deck, thumbnail
            // building, and entering/leaving presentation mode, then exits.
            var selfTestAt = Array.IndexOf(args, "--selftest");
            if (selfTestAt >= 0)
            {
                var deck = args.Length > selfTestAt + 1 ? args[selfTestAt + 1] : null;
                var exit = 0;
                try
                {
                    var w = new MainWindow();
                    w.Show();
                    if (deck != null && File.Exists(deck)) w.OpenFile(deck);
                    w.RunSelfTest();
                    Console.WriteLine("SELFTEST OK");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("SELFTEST FAILED: " + ex);
                    exit = 1;
                }
                Environment.Exit(exit);
                return;
            }

            var window = new MainWindow();
            desktop.MainWindow = window;

            // A file passed on the command line (Open With / installer file
            // association) opens immediately.
            if (args.Length > 0 && File.Exists(args[0]))
                window.OpenFile(args[0]);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
