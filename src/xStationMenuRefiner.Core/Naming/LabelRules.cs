using System;
using System.Globalization;
using System.Text.RegularExpressions;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Naming;

// How xStation turns a file name into the text shown in its menu.
//
//     label = basename of the first data-track image
//             minus the extension
//             minus a trailing parenthesized track marker
//
// The folder name and the CUE file name have no effect on the menu.
public static class LabelRules
{
    // The only form xStation recognizes. A capital T, real parentheses, one space.
    private static readonly Regex ConformingTrackSuffix =
        new(@"\s*\(Track (\d{1,3})\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Everything a person might have meant as a track marker. This drives repairs.
    private static readonly Regex LooseTrackSuffix =
        new(@"[\s._-]*[\(\[]?\s*track\s*[\s._#-]*(\d{1,3})\s*[\)\]]?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // xStation reads CUE/BIN with one or many bins, standalone ISO, and CloneCD's
    // CCD/IMG pairing.
    public static readonly string[] ImageExtensions = { ".bin", ".img", ".iso" };

    // Files that describe an image and have to carry the same base name as it.
    public static readonly string[] SidecarExtensions = { ".cue", ".ccd", ".sub" };

    public const string SupportedFormatsDisplay = "CUE/BIN, ISO, CCD/IMG";

    // The xStation menu trims a label at this many characters.
    public const int MaxLabelLength = 47;

    // Past this length the menu keeps the ending instead of dropping it.
    public const int ElisionThreshold = 56;

    // How much of the ending survives, after the two dots that stand in for the middle.
    public const int ElidedTailLength = 8;

    // What the menu puts on screen for a name. Anything longer than MaxLabelLength loses
    // its middle, and only past ElisionThreshold is the ending kept.
    //
    //     47 or fewer  ->  shown whole
    //     48 to 56     ->  first 47 characters
    //     57 or more   ->  first 47, "..", last 8
    public static string MenuText(string name)
    {
        if (name.Length <= MaxLabelLength)
            return name;

        if (name.Length <= ElisionThreshold)
            return name.Substring(0, MaxLabelLength);

        return string.Concat(
            name.AsSpan(0, MaxLabelLength),
            "..",
            name.AsSpan(name.Length - ElidedTailLength));
    }

    public static bool IsTrimmedByMenu(string name) => name.Length > MaxLabelLength;

    public static bool IsImageExtension(string extension) =>
        Array.Exists(ImageExtensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));

    public static bool IsSidecarExtension(string extension) =>
        Array.Exists(SidecarExtensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));

    public static string LabelFromFileName(string fileName)
    {
        string baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return StripTrackSuffix(baseName);
    }

    public static string StripTrackSuffix(string baseName)
    {
        var match = ConformingTrackSuffix.Match(baseName);
        return match.Success ? baseName.Substring(0, match.Index) : baseName;
    }

    public static bool HasConformingTrackSuffix(string baseName) =>
        ConformingTrackSuffix.IsMatch(baseName);

    public static int? TrackNumberFromSuffix(string baseName)
    {
        var match = ConformingTrackSuffix.Match(baseName);

        if (!match.Success)
            return null;

        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // Splits a base name carrying a track marker in some unrecognized shape, such as
    // "Resident Evil 2 Track 1" or "Game_track02". Returns false when there is no marker.
    public static bool TrySplitLooseTrackSuffix(string baseName, out string stem, out int trackNumber)
    {
        stem = baseName;
        trackNumber = 0;

        if (ConformingTrackSuffix.IsMatch(baseName))
            return false;

        var match = LooseTrackSuffix.Match(baseName);

        if (!match.Success || match.Index == 0)
            return false;

        stem = baseName.Substring(0, match.Index).TrimEnd(' ', '.', '_', '-');

        if (stem.Length == 0)
            return false;

        trackNumber = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return true;
    }

    // Builds the image file name for a label, keeping the track marker when there is one.
    public static string ImageFileName(string label, int? trackNumber, string extension, bool padTrackNumbers = true)
    {
        if (trackNumber == null)
            return label + extension;

        string number = padTrackNumbers
            ? trackNumber.Value.ToString("00", CultureInfo.InvariantCulture)
            : trackNumber.Value.ToString(CultureInfo.InvariantCulture);

        return $"{label} (Track {number}){extension}";
    }
}
