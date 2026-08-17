using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Scanning;

public sealed class RemovableVolume
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }
    public bool LooksLikeXStationCard { get; set; }

    public string Display =>
        string.IsNullOrWhiteSpace(Label) ? Path : $"{Path} ({Label})";
}

public static class RemovableDrives
{
    // The system folder every xStation card carries. Its presence is the strongest hint
    // that a volume is a card, and CardConfig reads the flags it holds.
    public const string SystemFolderName = "00xstation";

    public static List<RemovableVolume> Enumerate()
    {
        var volumes = new List<RemovableVolume>();

        foreach (var drive in SafeGetDrives())
        {
            if (!IsCandidate(drive))
                continue;

            string root = drive.RootDirectory.FullName;

            var volume = new RemovableVolume
            {
                Path = root,
                Label = SafeLabel(drive),
                LooksLikeXStationCard = HasSystemFolder(root),
            };

            try
            {
                volume.TotalSize = drive.TotalSize;
                volume.FreeSpace = drive.AvailableFreeSpace;
            }
            catch (IOException)
            {
            }

            volumes.Add(volume);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            AddUnixMountPoints(volumes);

        return volumes
            .GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(v => v.LooksLikeXStationCard)
            .ThenBy(v => v.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasSystemFolder(string root)
    {
        try
        {
            return Directory.Exists(Path.Combine(root, SystemFolderName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<DriveInfo> SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return Array.Empty<DriveInfo>();
        }
    }

    // Removable volumes are always offered. A fixed volume only shows up when it carries
    // the xStation system folder, which covers cards Windows reports as fixed.
    private static bool IsCandidate(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady)
                return false;

            if (drive.DriveType == DriveType.Removable)
                return true;

            return drive.DriveType == DriveType.Fixed && HasSystemFolder(drive.RootDirectory.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // Linux and macOS mount removable media under well-known parents, and DriveInfo
    // reports many of those as fixed.
    private static void AddUnixMountPoints(List<RemovableVolume> volumes)
    {
        var parents = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            parents.Add("/Volumes");
        }
        else
        {
            parents.Add("/media");
            parents.Add("/run/media");
            parents.Add("/mnt");

            string user = Environment.UserName;
            parents.Add($"/media/{user}");
            parents.Add($"/run/media/{user}");
        }

        foreach (string parent in parents)
        {
            if (!Directory.Exists(parent))
                continue;

            string[] entries;
            try
            {
                entries = Directory.GetDirectories(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                if (volumes.Any(v => string.Equals(v.Path, entry, StringComparison.Ordinal)))
                    continue;

                if (!HasSystemFolder(entry))
                    continue;

                volumes.Add(new RemovableVolume
                {
                    Path = entry,
                    Label = Path.GetFileName(entry),
                    LooksLikeXStationCard = true,
                });
            }
        }
    }
}
