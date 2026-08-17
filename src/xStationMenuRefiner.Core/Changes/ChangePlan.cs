using System.Collections.Generic;
using xStationMenuRefiner.Core.Model;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Changes;

public enum OperationKind
{
    RenameFile,
    RenameDirectory,
    PatchCue,
    CreateDirectory,
    DeleteDirectory,
    WriteConfig,
}

public sealed class PlannedOperation
{
    public OperationKind Kind { get; set; }
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";

    // Old name to new name for every FILE line the CUE has to carry.
    public Dictionary<string, string> CueReplacements { get; } = new();

    public string Summary { get; set; } = "";

    // The whole file a WriteConfig lays down, existing bytes with only bit 5 changed.
    public byte[]? FileBytes { get; set; }
}

public enum PendingEditKind
{
    GameLabel,
    FolderName,
    TrackSuffixFix,
    CueReferenceFix,
    ShortenMenuEntry,
    CreateFolder,
    DeleteFolder,
    MoveEntry,
    WrapInFolder,
    UnwrapFolder,
    SetFolderBrowsing,
}

public sealed class PendingEdit
{
    public PendingEditKind Kind { get; set; }
    public CardNode? Node { get; set; }
    public string NewValue { get; set; } = "";

    // What the tree showed before the edit, used for the review list.
    public string OriginalValue { get; set; } = "";

    // Where a MoveEntry lands.
    public MenuFolderNode? Destination { get; set; }
}

// Everything one folder needs, kept together so a failure can be contained and rolled
// back without touching any other folder.
public sealed class FolderChange
{
    public string FolderPath { get; set; } = "";
    public string OldLabel { get; set; } = "";
    public string NewLabel { get; set; } = "";
    public PendingEditKind Kind { get; set; }
    public List<PlannedOperation> Operations { get; } = new();
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();

    public bool IsValid => Problems.Count == 0;
}

public sealed class ChangePlan
{
    public List<FolderChange> Changes { get; } = new();

    public int OperationCount
    {
        get
        {
            int total = 0;

            foreach (var change in Changes)
                total += change.Operations.Count;

            return total;
        }
    }

    public bool HasProblems
    {
        get
        {
            foreach (var change in Changes)
            {
                if (!change.IsValid)
                    return true;
            }

            return false;
        }
    }
}

public sealed class PlanOptions
{
    // Only affects how the card reads on a PC while Folder Browsing is off. With it on,
    // the folder name is what the menu shows.
    public bool RenameFolder { get; set; } = true;

    public bool PadTrackNumbers { get; set; } = true;
}
