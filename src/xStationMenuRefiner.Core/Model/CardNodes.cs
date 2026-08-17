using System.Collections.Generic;
using System.Linq;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Model;

// The shapes xStation accepts.
public enum DiscFormat
{
    CueBin,
    CcdImg,
    Iso,
    Unsupported,
}

public enum IssueSeverity
{
    Warning,
    Error,
}

public enum IssueKind
{
    MissingCue,
    MultipleCues,
    UnresolvedCueReference,
    CaseOnlyCueMismatch,
    OrphanImage,
    NonConformingTrackSuffix,
    SplitMenuEntries,
    DuplicateLabel,
    LabelTooLong,
    PathTooLong,
    MacMetadata,
    LeftoverTempName,
    InvalidName,
    UnsupportedSectorSize,
    MultiTrackCcd,
}

public sealed class EntryIssue
{
    public IssueKind Kind { get; set; }
    public IssueSeverity Severity { get; set; } = IssueSeverity.Warning;
    public string Message { get; set; } = "";
    public string Path { get; set; } = "";
    public bool CanAutoFix { get; set; }
    public CardNode? Node { get; set; }

    // Null when the problem exists whichever menu the card draws. Two games sharing a
    // name only collide in the flat list, so that one is scoped to a single mode.
    public MenuMode? AppliesTo { get; set; }

    public bool AppliesIn(MenuMode mode) => AppliesTo == null || AppliesTo == mode;
}

// Which menu the firmware draws, set by Folder Browsing in the xStation options.
public enum MenuMode
{
    Flat,
    Browse,
}

public abstract class CardNode
{
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public MenuFolderNode? Parent { get; set; }
    public List<EntryIssue> Issues { get; } = new();

    // What xStation puts on screen for this entry, before the menu trims it.
    public abstract string MenuTextFor(MenuMode mode);
}

// A directory xStation shows as a folder to walk into.
public sealed class MenuFolderNode : CardNode
{
    public List<CardNode> Children { get; } = new();
    public bool IsRoot { get; set; }

    // The flat menu has no folders in it at all.
    public override string MenuTextFor(MenuMode mode) => mode == MenuMode.Browse ? Name : "";

    public int GameCount
    {
        get
        {
            int total = 0;

            foreach (var child in Children)
            {
                if (child is GameNode)
                    total++;
                else if (child is MenuFolderNode folder)
                    total += folder.GameCount;
            }

            return total;
        }
    }
}

// One image file belonging to a game.
public sealed class TrackImage
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public int? TrackNumber { get; set; }
    public bool ReferencedByCue { get; set; }
    public bool CarriesData { get; set; }
    public List<int> CueTracks { get; } = new();
}

// One disc image set, and one entry in the xStation menu.
public sealed class GameNode : CardNode
{
    // The shared base name of the set's images, with any track marker removed.
    public string Label { get; set; } = "";

    public string FolderPath { get; set; } = "";
    public string FolderName { get; set; } = "";

    // False when the folder holds more than one game, which makes the folder name a
    // shared property and not this game's to rename.
    public bool OwnsFolder { get; set; } = true;
    public string? CuePath { get; set; }
    public string? CueFileName { get; set; }
    public CueDocument? Cue { get; set; }

    public DiscFormat Format { get; set; } = DiscFormat.CueBin;

    // CUE, CCD and SUB files that have to keep the same base name as the images.
    public List<string> Sidecars { get; } = new();

    public List<TrackImage> Images { get; } = new();

    // How many entries this folder will produce on the card. Anything above one means
    // the image names do not agree with each other.
    public int MenuEntryCount { get; set; } = 1;

    // Browse mode launches the data track by name, so that file name is the row. Images
    // sort by file name, so the data track is not always the one that sorts first.
    public string MenuFileName
    {
        get
        {
            foreach (var image in Images)
            {
                if (image.CarriesData)
                    return image.FileName;
            }

            return Images.Count > 0 ? Images[0].FileName : Label;
        }
    }

    public override string MenuTextFor(MenuMode mode) =>
        mode == MenuMode.Browse ? MenuFileName : Label;

    public long TotalSize
    {
        get
        {
            long total = 0;

            foreach (var image in Images)
                total += image.Size;

            return total;
        }
    }

    public string FormatDisplay => Format switch
    {
        DiscFormat.CueBin => "CUE/BIN",
        DiscFormat.CcdImg => "CCD/IMG",
        DiscFormat.Iso => "ISO",
        _ => "Unsupported",
    };
}

public sealed class CardScanResult
{
    private readonly HashSet<(IssueKind, string, string)> _reported = new();

    public string RootPath { get; set; } = "";
    public MenuFolderNode Root { get; set; } = new() { IsRoot = true };
    public List<EntryIssue> Issues { get; } = new();
    public List<GameNode> Games { get; } = new();
    public List<MenuFolderNode> Folders { get; } = new();

    public IEnumerable<EntryIssue> IssuesFor(MenuMode mode) => Issues.Where(i => i.AppliesIn(mode));

    // Keeps the card-wide list free of the same problem reported by several nodes.
    public bool ReportOnce(EntryIssue issue) => _reported.Add((issue.Kind, issue.Message, issue.Path));
}
