using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core;

public enum ManualUpdateReason
{
    None,
    UnsupportedPlatform,
    KillSwitch,
}

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }
    public bool ManualUpdateRequired { get; set; }
    public ManualUpdateReason ManualReason { get; set; }
    public string LatestTag { get; set; } = "";
    public string LatestVersion { get; set; } = "";

    // Separates "the check could not run" from "nothing newer exists", so an
    // explicit check does not report being up to date after a network failure.
    public bool CheckFailed { get; set; }
}

public sealed class DownloadProgress
{
    public long BytesRead { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
}

// Message is already user-facing text. The wizard displays it as-is.
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
}

public static class UpdateManager
{
    private static readonly HttpClient Client;

    private const string StagingDirName = "xStationMenuRefiner_update";
    private const string LockFileName = "xStationMenuRefiner_update.lock";
    private const string AutoUpdateKillSwitch = "This release cannot be auto-updated.";
    private const string InstallMarker = "INSTALLING";
    private const string WindowsScriptName = "_xstation_updater.bat";
    private const string UnixScriptName = "_xstation_updater.sh";

    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromMinutes(5);

    // A normal install finishes in seconds. An install marker older than this
    // belongs to an updater script that died partway through.
    private static readonly TimeSpan InstallMarkerStaleAfter = TimeSpan.FromMinutes(10);

    static UpdateManager()
    {
        Client = new HttpClient();
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.AppExecutableBase + "-UpdateCheck/1.0");
    }

    private static Version ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new Version(0, 0);

        string cleaned = text.TrimStart('v', 'V');
        int hyphen = cleaned.IndexOf('-');

        if (hyphen > 0)
            cleaned = cleaned.Substring(0, hyphen);

        return Version.TryParse(cleaned, out var parsed) ? parsed : new Version(0, 0);
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var result = new UpdateCheckResult();

        try
        {
            string url = $"https://api.github.com/repos/{Constants.Repo}/releases/latest";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await Client.GetAsync(url, timeout.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);

            string tag = document.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? ""
                : "";

            string body = document.RootElement.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? ""
                : "";

            result.LatestTag = tag;
            result.LatestVersion = "v" + tag.TrimStart('v', 'V');

            bool isNewer = ParseVersion(tag) > ParseVersion(Constants.Version);
            bool killSwitchActive = IsKillSwitchArmed(body);
            bool isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

            result.UpdateAvailable = isNewer && !killSwitchActive && !isMacOS;
            result.ManualUpdateRequired = isNewer && (killSwitchActive || isMacOS);

            if (result.ManualUpdateRequired)
            {
                result.ManualReason = killSwitchActive
                    ? ManualUpdateReason.KillSwitch
                    : ManualUpdateReason.UnsupportedPlatform;
            }
        }
        catch
        {
            result.UpdateAvailable = false;
            result.ManualUpdateRequired = false;
            result.CheckFailed = true;
        }

        return result;
    }

    // The sentinel counts only when it stands alone on a line, so release notes
    // can quote it without arming it.
    private static bool IsKillSwitchArmed(string body) =>
        body.Split('\n').Any(line => line.Trim().Equals(AutoUpdateKillSwitch, StringComparison.OrdinalIgnoreCase));

    private static string GetAssetSuffix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.X86
                ? "win-x86.zip"
                : "win-x64.zip";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64-AppBundle.tar.gz"
                : "osx-x64-AppBundle.tar.gz";
        }

        return "linux-x64.tar.gz";
    }

    private static string GetAssetFileName(string tag)
    {
        string cleanTag = tag.TrimStart('v', 'V');
        return $"{Constants.AppExecutableBase}.v{cleanTag}-{GetAssetSuffix()}";
    }

    private static string GetAssetUrl(string tag) =>
        $"https://github.com/{Constants.Repo}/releases/download/{tag}/{GetAssetFileName(tag)}";

    private static string GetStagingDir() => Path.Combine(Path.GetTempPath(), StagingDirName);

    private static string GetLockFilePath() => Path.Combine(Path.GetTempPath(), LockFileName);

    // Cross-instance lock, so two copies of the application (or one copy plus a
    // running updater script) cannot share the staging directory. The file holds
    // the owning process ID, or the install marker while the script is copying.
    public static bool TryBeginUpdate()
    {
        string path = GetLockFilePath();

        if (File.Exists(path))
        {
            string content;

            try
            {
                content = File.ReadAllText(path).Trim();
            }
            catch
            {
                content = "";
            }

            if (content.Equals(InstallMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsMarkerStale(path))
                    return false;
            }
            else if (int.TryParse(content, out int pid) && pid != Environment.ProcessId)
            {
                try
                {
                    using var owner = Process.GetProcessById(pid);

                    if (owner != null && !owner.HasExited)
                        return false;
                }
                catch (ArgumentException)
                {
                    // No process carries that ID, so the lock is abandoned.
                }
                catch
                {
                    // The process state could not be read. Treat the lock as abandoned.
                }
            }
        }

        try
        {
            File.WriteAllText(path, Environment.ProcessId.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMarkerStale(string path)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > InstallMarkerStaleAfter;
        }
        catch
        {
            return true;
        }
    }

    public static void EndUpdate()
    {
        try
        {
            string path = GetLockFilePath();

            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static bool IsAnotherInstanceUpdating()
    {
        string path = GetLockFilePath();

        if (!File.Exists(path))
            return false;

        string content;

        try
        {
            content = File.ReadAllText(path).Trim();
        }
        catch
        {
            return false;
        }

        if (content.Equals(InstallMarker, StringComparison.OrdinalIgnoreCase))
            return !IsMarkerStale(path);

        if (int.TryParse(content, out int pid) && pid != Environment.ProcessId)
        {
            try
            {
                using var owner = Process.GetProcessById(pid);
                return owner != null && !owner.HasExited;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public static async Task DownloadUpdateAsync(string tag, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        string stagingDir = GetStagingDir();
        string downloadDir = Path.Combine(stagingDir, "download");

        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, true);

        Directory.CreateDirectory(downloadDir);

        string url = GetAssetUrl(tag);
        string downloadPath = Path.Combine(downloadDir, GetAssetFileName(tag));

        // Cancels the download once no bytes have arrived for the idle timeout.
        // Every chunk restarts the timer.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleTimer = new Timer(_ =>
        {
            try
            {
                idleCts.Cancel();
            }
            catch
            {
            }
        }, null, DownloadIdleTimeout, Timeout.InfiniteTimeSpan);

        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idleCts.Token);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;

            using var contentStream = await response.Content.ReadAsStreamAsync(idleCts.Token);
            using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

            byte[] buffer = new byte[65536];
            long bytesRead = 0;
            int read;
            var stopwatch = Stopwatch.StartNew();
            long lastReportBytes = 0;
            double lastReportTime = 0;

            while ((read = await contentStream.ReadAsync(buffer, idleCts.Token)) > 0)
            {
                idleTimer.Change(DownloadIdleTimeout, Timeout.InfiniteTimeSpan);

                await fileStream.WriteAsync(buffer.AsMemory(0, read), idleCts.Token);
                bytesRead += read;

                double elapsed = stopwatch.Elapsed.TotalSeconds;

                if (elapsed - lastReportTime >= 0.25)
                {
                    double speed = elapsed - lastReportTime > 0
                        ? (bytesRead - lastReportBytes) / (elapsed - lastReportTime)
                        : 0;

                    lastReportBytes = bytesRead;
                    lastReportTime = elapsed;

                    progress?.Report(new DownloadProgress
                    {
                        BytesRead = bytesRead,
                        TotalBytes = totalBytes,
                        SpeedBytesPerSecond = speed,
                    });
                }
            }

            // Rate limiting above can swallow the last chunk's report, which would
            // leave the bar short of full.
            progress?.Report(new DownloadProgress
            {
                BytesRead = bytesRead,
                TotalBytes = totalBytes,
                SpeedBytesPerSecond = 0,
            });
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            CleanupStagingDirectory();

            throw new UpdateException(
                "The download stalled.\n\n" +
                "No data arrived for several minutes. Check your internet connection and try again.");
        }
        catch
        {
            CleanupStagingDirectory();
            throw;
        }
    }

    public static async Task ExtractUpdateAsync(string tag, CancellationToken cancellationToken)
    {
        string stagingDir = GetStagingDir();
        string archivePath = Path.Combine(stagingDir, "download", GetAssetFileName(tag));
        string extractedDir = Path.Combine(stagingDir, "extracted");

        Directory.CreateDirectory(extractedDir);

        try
        {
            if (GetAssetSuffix().EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, extractedDir), cancellationToken);
            else
                await ExtractTarGzAsync(archivePath, extractedDir, cancellationToken);
        }
        catch
        {
            CleanupStagingDirectory();
            throw;
        }
    }

    private static async Task ExtractTarGzAsync(string archivePath, string extractedDir, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{archivePath}\" -C \"{extractedDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);

            if (process != null)
            {
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode == 0)
                    return;
            }
        }
        catch
        {
            // No tar on the path.
        }

        throw new UpdateException(
            "The downloaded update could not be extracted.\n\n" +
            "Extracting this update needs the 'tar' command-line tool. Install it through your " +
            "distribution's package manager and try again.");
    }

    // Flattens the extracted tree down to the application files and folds the
    // current settings into the copy that is about to be installed.
    public static async Task PrepareUpdateAsync()
    {
        string stagingDir = GetStagingDir();
        string extractedDir = Path.Combine(stagingDir, "extracted");

        string? contentRoot = FindContentRoot(extractedDir);

        if (contentRoot == null)
        {
            throw new UpdateException(
                "The downloaded file does not look like an xStation Menu Refiner release.\n\n" +
                "It downloaded successfully but did not contain the expected files. Please try again later.");
        }

        if (contentRoot != extractedDir)
        {
            string tempMove = Path.Combine(stagingDir, "content_temp");
            Directory.Move(contentRoot, tempMove);

            if (Directory.Exists(extractedDir))
                Directory.Delete(extractedDir, true);

            Directory.Move(tempMove, extractedDir);
        }

        await Task.Run(() => MergeSettings(extractedDir));
    }

    // The Windows archive holds its files at the root, while the Linux archive
    // wraps them in a versioned folder.
    private static string? FindContentRoot(string extractedDir)
    {
        if (HasAppFiles(extractedDir))
            return extractedDir;

        foreach (string subDir in Directory.GetDirectories(extractedDir))
        {
            if (HasAppFiles(subDir))
                return subDir;

            string macosDir = Path.Combine(subDir, "Contents", "MacOS");

            if (Directory.Exists(macosDir) && HasAppFiles(macosDir))
                return macosDir;
        }

        foreach (string subDir in Directory.GetDirectories(extractedDir))
        {
            foreach (string nested in Directory.GetDirectories(subDir))
            {
                if (HasAppFiles(nested))
                    return nested;
            }
        }

        return null;
    }

    private static bool HasAppFiles(string directory)
    {
        return File.Exists(Path.Combine(directory, $"{Constants.AppExecutableBase}.exe")) ||
               File.Exists(Path.Combine(directory, Constants.AppExecutableBase)) ||
               File.Exists(Path.Combine(directory, $"{Constants.AppExecutableBase}.dll"));
    }

    // Keys the release introduces supply defaults. Values the user already has win.
    private static void MergeSettings(string extractedDir)
    {
        string currentSettingsPath = Path.Combine(AppSettings.GetAppDirectory(), "settings.json");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            currentSettingsPath = Path.Combine(home, "Library", "Application Support", Constants.AppExecutableBase, "settings.json");
        }

        string newSettingsPath = Path.Combine(extractedDir, "settings.json");

        if (!File.Exists(currentSettingsPath))
        {
            // Nothing to carry over. The application writes its own defaults on
            // first close.
            DeleteIfExists(newSettingsPath);
            return;
        }

        try
        {
            using var currentDocument = JsonDocument.Parse(File.ReadAllText(currentSettingsPath));

            if (!File.Exists(newSettingsPath))
            {
                File.Copy(currentSettingsPath, newSettingsPath);
                return;
            }

            using var newDocument = JsonDocument.Parse(File.ReadAllText(newSettingsPath));

            var merged = new Dictionary<string, JsonElement>();

            foreach (var property in newDocument.RootElement.EnumerateObject())
                merged[property.Name] = property.Value;

            foreach (var property in currentDocument.RootElement.EnumerateObject())
                merged[property.Name] = property.Value;

            File.WriteAllText(newSettingsPath, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A damaged settings file on either side leaves the current one in
            // place untouched.
            DeleteIfExists(newSettingsPath);
        }
    }

    public static void LaunchUpdaterAndExit()
    {
        string extractedDir = Path.Combine(GetStagingDir(), "extracted");
        string appDir = AppSettings.GetAppDirectory();
        int pid = Environment.ProcessId;

        // Written before this process exits, so a copy launched during the gap
        // between the exit and the script's own first write still sees the
        // install in progress.
        try
        {
            File.WriteAllText(GetLockFilePath(), InstallMarker);
        }
        catch
        {
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), WindowsScriptName);
            File.WriteAllText(scriptPath, GenerateWindowsScript(pid, extractedDir, appDir));

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        else
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), UnixScriptName);
            File.WriteAllText(scriptPath, GenerateUnixScript(pid, extractedDir, appDir));

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(2000);
            }
            catch
            {
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"\"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }

        Environment.Exit(0);
    }

    // The script templates are verbatim string literals, so their line endings
    // follow however this source file is stored. Pinning them keeps cmd.exe and
    // bash working either way.
    private static string WithLineEndings(string text, string lineEnding) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", lineEnding);

    private static string GenerateWindowsScript(int pid, string extractedDir, string appDir)
    {
        string escapedExtracted = extractedDir.Replace("/", "\\");
        string escapedApp = appDir.TrimEnd('\\').Replace("/", "\\");
        string stagingDir = GetStagingDir().Replace("/", "\\");
        string lockPath = GetLockFilePath().Replace("/", "\\");
        string exePath = Path.Combine(escapedApp, $"{Constants.AppExecutableBase}.exe");

        // Wait-Process returns at once when the process ID is already gone.
        // Xcopy overwrites matching files and leaves everything else in the
        // install folder alone, so someone running the executable straight out
        // of Downloads keeps the rest of that folder.
        return WithLineEndings($@"@echo off
powershell -NoProfile -Command ""Wait-Process -Id {pid} -ErrorAction SilentlyContinue""

echo {InstallMarker}> ""{lockPath}""

xcopy /E /Y ""{escapedExtracted}\*"" ""{escapedApp}\""

rmdir /S /Q ""{escapedExtracted}"" 2>NUL
rmdir /S /Q ""{stagingDir}"" 2>NUL

del /F /Q ""{lockPath}"" 2>NUL

start """" ""{exePath}""

del ""%~f0""
", "\r\n");
    }

    private static string GenerateUnixScript(int pid, string extractedDir, string appDir)
    {
        string escapedApp = appDir.TrimEnd('/');
        string stagingDir = GetStagingDir();
        string lockPath = GetLockFilePath();
        string exePath = $"{escapedApp}/{Constants.AppExecutableBase}";

        // Cp overwrites matching files and leaves everything else in the install
        // folder alone. The "/." suffix carries dotfiles across as well.
        return WithLineEndings($@"#!/bin/bash

# Wait for the previous process to exit.
while kill -0 {pid} 2>/dev/null; do
    sleep 1
done

# Keep a concurrent launch out while the copy runs.
echo {InstallMarker} > ""{lockPath}""

cp -rf ""{extractedDir}/."" ""{escapedApp}/""

rm -rf ""{stagingDir}""
rm -f ""{lockPath}""

chmod +x ""{exePath}""
""{exePath}"" &

rm ""$0""
", "\n");
    }

    public static void CleanupStaleStagingData()
    {
        // Clearing any of this mid-install would corrupt the copy in progress.
        if (IsAnotherInstanceUpdating())
            return;

        CleanupStagingDirectory();

        foreach (string script in new[] { WindowsScriptName, UnixScriptName })
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), script);

                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }

    public static void CleanupStagingDirectory()
    {
        try
        {
            string stagingDir = GetStagingDir();

            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
        }
        catch
        {
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
