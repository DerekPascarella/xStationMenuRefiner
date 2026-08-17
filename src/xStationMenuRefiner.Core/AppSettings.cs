using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core;

// Settings file. Lives next to the executable on Windows and Linux, and in
// ~/Library/Application Support/xStationMenuRefiner/ on macOS, where .app bundles
// are read-only.
public class AppSettings
{
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 700;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    public bool RenameFolderWithLabel { get; set; } = true;
    public bool PadTrackNumbers { get; set; } = true;

    public string SkippedUpdateVersion { get; set; } = "";
    public bool CheckForUpdatesOnStart { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Returns the directory the executable was launched from. AppContext.BaseDirectory
    // points at the extraction folder under single-file publishing.
    public static string GetAppDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(directory))
                return directory;
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    private static string GetSettingsDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", Constants.AppExecutableBase);
        }

        return GetAppDirectory();
    }

    private static string GetSettingsPath() => Path.Combine(GetSettingsDirectory(), "settings.json");

    public static AppSettings Load()
    {
        string path = GetSettingsPath();

        if (!File.Exists(path))
            return new AppSettings();

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string directory = GetSettingsDirectory();
            Directory.CreateDirectory(directory);
            File.WriteAllText(GetSettingsPath(), JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // The settings file might be read-only or on a volume that has gone away.
        }
    }
}
