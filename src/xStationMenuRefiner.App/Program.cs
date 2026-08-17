using System;
using Avalonia;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App;

internal static class Program
{
    // A folder passed on the command line opens as a card at startup.
    public static string? StartupPath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && System.IO.Directory.Exists(args[0]))
            StartupPath = args[0];

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
