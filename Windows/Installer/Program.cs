using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;

namespace SlideViewerSetup;

/// <summary>
/// Self-contained installer for SlideViewer.
///
/// The whole application — including the .NET runtime it needs — is embedded in
/// this executable, so installing requires no downloads and no prerequisites.
/// The same executable is copied into the install folder as the uninstaller.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string AppName = "SlideViewer";
    private const string AppExe = "SlideViewer.exe";
    private const string Version = "1.0.3";
    private const string Publisher = "SlideViewer";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SlideViewer";
    private const string UninstallerName = "Uninstall.exe";

    private static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    private static int Main(string[] args)
    {
        try { Console.Title = $"{AppName} Setup"; } catch { }
        try
        {
            if (args.Contains("--uninstall-cleanup")) return UninstallCleanup(args);
            if (args.Contains("--uninstall")) return Uninstall();
            return Install(silent: args.Contains("--silent"));
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine($"  Setup failed: {ex.Message}");
            Console.ResetColor();
            Pause();
            return 1;
        }
    }

    // ── Install ─────────────────────────────────────────────────────────────

    private static int Install(bool silent)
    {
        Banner();
        Console.WriteLine($"  Installing to: {InstallDir}");
        Console.WriteLine();

        if (IsRunning())
        {
            Console.WriteLine($"  {AppName} is currently running. Please close it, then run setup again.");
            Pause();
            return 1;
        }

        Step("Preparing folder");
        if (Directory.Exists(InstallDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(InstallDir))
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                catch { /* overwritten below where possible */ }
            }
        }
        Directory.CreateDirectory(InstallDir);

        Step("Extracting application files");
        using (var payload = typeof(Program).Assembly.GetManifestResourceStream("payload.zip"))
        {
            if (payload == null)
                throw new InvalidOperationException("This setup file is incomplete (no payload).");
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
            var total = archive.Entries.Count;
            var done = 0;
            var rootFull = Path.GetFullPath(InstallDir);
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
                // Guard against path traversal in a malformed archive.
                if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                if (++done % 25 == 0 || done == total) Progress(done, total);
            }
            Console.WriteLine();
        }

        var exePath = Path.Combine(InstallDir, AppExe);
        if (!File.Exists(exePath))
            throw new InvalidOperationException("The application executable is missing after extraction.");

        Step("Creating shortcuts");
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk"), exePath);
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"), exePath);

        Step("Registering with Windows");
        var uninstaller = Path.Combine(InstallDir, UninstallerName);
        try { File.Copy(Environment.ProcessPath!, uninstaller, overwrite: true); } catch { }
        RegisterUninstall(uninstaller, exePath);
        RegisterFileAssociation(exePath);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  {AppName} {Version} installed successfully.");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  A shortcut is on your Desktop and in the Start Menu.");
        Console.WriteLine("  Right-click any .pptx file and choose \"Open with\" to view it.");
        Console.WriteLine();

        if (!silent)
        {
            Console.Write("  Launch SlideViewer now? [Y/n] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is null or "" or "y" or "yes")
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        return 0;
    }

    // ── Uninstall ───────────────────────────────────────────────────────────

    private static int Uninstall()
    {
        Banner();
        Console.WriteLine($"  This will remove {AppName} from your computer.");
        Console.Write("  Continue? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is not ("y" or "yes")) { Console.WriteLine("  Cancelled."); return 0; }

        if (IsRunning())
        {
            Console.WriteLine($"  {AppName} is running. Please close it and try again.");
            Pause();
            return 1;
        }

        // The uninstaller lives inside the folder it must delete, so it finishes
        // the job from a temp copy.
        var temp = Path.Combine(Path.GetTempPath(), $"{AppName}-uninstall.exe");
        File.Copy(Environment.ProcessPath!, temp, overwrite: true);
        Process.Start(new ProcessStartInfo(temp)
        {
            Arguments = $"--uninstall-cleanup \"{InstallDir}\"",
            UseShellExecute = true
        });
        return 0;
    }

    private static int UninstallCleanup(string[] args)
    {
        var target = args.SkipWhile(a => a != "--uninstall-cleanup").Skip(1).FirstOrDefault() ?? InstallDir;
        Banner();

        Step("Removing files");
        for (int attempt = 0; attempt < 20 && Directory.Exists(target); attempt++)
        {
            try { Directory.Delete(target, true); }
            catch { Thread.Sleep(250); }
        }

        Step("Removing shortcuts");
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
        {
            var link = Path.Combine(Environment.GetFolderPath(folder), $"{AppName}.lnk");
            try { if (File.Exists(link)) File.Delete(link); } catch { }
        }

        Step("Cleaning registry");
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{AppName}.pptx", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{AppExe}", false);
            using var progids = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\.pptx\OpenWithProgids", writable: true);
            progids?.DeleteValue($"{AppName}.pptx", throwOnMissingValue: false);
        }
        catch { }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  {AppName} has been removed.");
        Console.ResetColor();
        Pause();
        return 0;
    }

    // ── Windows integration ─────────────────────────────────────────────────

    /// <summary>Creates a .lnk via the WScript.Shell COM object, present on
    /// every Windows install.</summary>
    private static void CreateShortcut(string linkPath, string targetPath)
    {
        try { if (File.Exists(linkPath)) File.Delete(linkPath); } catch { }
        var script =
            $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{linkPath}');" +
            $"$s.TargetPath='{targetPath}';" +
            $"$s.WorkingDirectory='{Path.GetDirectoryName(targetPath)}';" +
            $"$s.IconLocation='{targetPath},0';" +
            "$s.Description='SlideViewer - Viewer for PowerPoint and PPTX';" +
            "$s.Save()";
        RunPowerShell(script);
    }

    private static void RunPowerShell(string script)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(15000);
        }
        catch { /* shortcuts are best-effort */ }
    }

    private static void RegisterUninstall(string uninstaller, string exePath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
        if (key == null) return;
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        try
        {
            var size = new DirectoryInfo(InstallDir)
                .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
            key.SetValue("EstimatedSize", (int)size, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }

    /// <summary>Adds SlideViewer to the "Open with" list for .pptx without
    /// hijacking the user's default association.</summary>
    private static void RegisterFileAssociation(string exePath)
    {
        try
        {
            var progId = $"{AppName}.pptx";
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
                key?.SetValue("", "PowerPoint Presentation");
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\DefaultIcon"))
                key?.SetValue("", $"{exePath},0");
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command"))
                key?.SetValue("", $"\"{exePath}\" \"%1\"");
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pptx\OpenWithProgids"))
                key?.SetValue(progId, Array.Empty<byte>(), Microsoft.Win32.RegistryValueKind.None);
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\Applications\{AppExe}\shell\open\command"))
                key?.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch { }
    }

    private static bool IsRunning()
    {
        try { return Process.GetProcessesByName(AppName).Length > 0; }
        catch { return false; }
    }

    // ── Console chrome ──────────────────────────────────────────────────────

    private static void Banner()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  {AppName} {Version}");
        Console.ResetColor();
        Console.WriteLine("  Viewer for PowerPoint & PPTX");
        Console.WriteLine("  " + new string('-', 44));
        Console.WriteLine();
    }

    private static void Step(string message) => Console.WriteLine($"  * {message}...");

    private static void Progress(int done, int total)
    {
        var percent = total == 0 ? 100 : done * 100 / total;
        Console.Write($"\r    {percent,3}%  ({done}/{total} files)");
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.Write("  Press Enter to close...");
        Console.ReadLine();
    }
}
