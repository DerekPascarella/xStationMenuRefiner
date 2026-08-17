using System;
using System.Collections.Generic;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Naming;

// Name rules for the card. The card is FAT32 or exFAT, so the Windows rules apply on
// every host.
public static class PathSafety
{
    public static readonly char[] ReservedCharacters = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxComponentLength = 255;

    // The longest path the console can load, measured from the card root.
    public const int MaxPathLength = 256;

    // The path from the card root, leading separator included.
    public static int FromRootLength(string fullPath, string rootPath) =>
        fullPath.Length - rootPath.TrimEnd('\\', '/').Length;

    // A forward slash in a name creates a nested folder on Windows without reporting an
    // error, so every name is checked before anything is written.
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name is empty.";

        foreach (char c in name)
        {
            if (Array.IndexOf(ReservedCharacters, c) >= 0)
                return $"Name contains the reserved character {c}";

            if (c < ' ')
                return "Name contains a control character.";
        }

        if (name.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
            return "Name ends with a space or a period.";

        if (name.StartsWith(" ", StringComparison.Ordinal))
            return "Name starts with a space.";

        string stem = name;
        int dot = stem.IndexOf('.');

        if (dot > 0)
            stem = stem.Substring(0, dot);

        if (ReservedDeviceNames.Contains(stem))
            return $"\"{stem}\" is a reserved device name on Windows.";

        if (name.Length > MaxComponentLength)
            return $"Name is longer than {MaxComponentLength} characters.";

        return null;
    }

    public static bool IsValid(string name) => Validate(name) == null;
}
