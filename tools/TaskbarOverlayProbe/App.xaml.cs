using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace TaskbarOverlayProbe;

public partial class App : Application
{
    private ProbeLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var keepOpen = e.Args.Any(a => string.Equals(a, "--keep", StringComparison.OrdinalIgnoreCase));
        var seconds = ParseSeconds(e.Args);
        var outputDirectory = ParseOutputDirectory(e.Args);

        _log = new ProbeLog(outputDirectory);
        _log.Write("=== TaskbarOverlayProbe start ===");
        _log.Write($"args={string.Join(' ', e.Args)} keep={keepOpen} seconds={seconds} out={outputDirectory}");
        _log.Write($"os={Environment.OSVersion} version={Environment.Version}");

        DispatcherUnhandledException += (_, args) =>
        {
            _log.Write($"unhandled: {args.Exception}");
            _log.Flush();
        };

        var window = new ProbeWindow(_log, outputDirectory, seconds, keepOpen);
        MainWindow = window;
        window.Show();
    }

    private static int ParseSeconds(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--seconds", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var value))
            {
                return Math.Clamp(value, 4, 300);
            }
        }

        return 24;
    }

    private static string ParseOutputDirectory(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return Path.Combine(
            Path.GetTempPath(),
            "hotspotmeter-taskbar-probe",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Write("=== TaskbarOverlayProbe exit ===");
        _log?.Flush();
        _log?.Dispose();
        base.OnExit(e);
    }
}