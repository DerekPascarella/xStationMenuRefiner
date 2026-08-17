using System.Diagnostics;
using System.Runtime.InteropServices;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core;

public static class PlatformUtil
{
    // "1 game", "3 games". Pass plural when it is not the singular plus an s.
    public static string Counted(int value, string singular, string? plural = null) =>
        $"{value} {(value == 1 ? singular : plural ?? singular + "s")}";

    public static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
        }
        catch
        {
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", path);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", path);
        }
        catch
        {
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }
}
