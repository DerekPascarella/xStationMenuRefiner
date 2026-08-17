using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using xStationMenuRefiner.Core.Model;
using xStationMenuRefiner.Core.Naming;
using xStationMenuRefiner.Core.Scanning;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Changes;

// Turns pending edits into an explicit list of filesystem operations. Nothing here
// writes to disk, though it does read the folder to check for collisions.
public static class ChangePlanner
{
    public static ChangePlan Build(IEnumerable<PendingEdit> edits, PlanOptions options)
    {
        var plan = new ChangePlan();

        foreach (var edit in edits)
        {
            var change = edit.Kind switch
            {
                PendingEditKind.GameLabel => PlanGameLabel((GameNode)edit.Node!, edit.NewValue, options),
                PendingEditKind.TrackSuffixFix => PlanGameLabel((GameNode)edit.Node!, ((GameNode)edit.Node!).Label, options),
                PendingEditKind.CueReferenceFix => PlanCueReferenceFix((GameNode)edit.Node!),
                PendingEditKind.ShortenMenuEntry => PlanShortenEntry((GameNode)edit.Node!),
                PendingEditKind.CreateFolder => PlanCreateFolder((MenuFolderNode)edit.Node!, edit.NewValue),
                PendingEditKind.DeleteFolder => PlanDeleteFolder((MenuFolderNode)edit.Node!),
                PendingEditKind.MoveEntry => PlanMove(edit.Node!, edit.Destination!),
                PendingEditKind.WrapInFolder => PlanWrap((GameNode)edit.Node!),
                PendingEditKind.UnwrapFolder => PlanUnwrap((GameNode)edit.Node!),
                PendingEditKind.FolderName => PlanFolderRename((MenuFolderNode)edit.Node!, edit.NewValue),
                PendingEditKind.SetFolderBrowsing =>
                    PlanConfigMode((MenuFolderNode)edit.Node!, edit.NewValue == "on"),
                _ => null,
            };

            if (change != null)
                plan.Changes.Add(change);
        }

        // Deeper paths first, so renaming a parent folder never invalidates work queued
        // for something inside it.
        plan.Changes.Sort((a, b) => Depth(b.FolderPath).CompareTo(Depth(a.FolderPath)));

        return plan;
    }

    private static int Depth(string path) =>
        path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

    public static FolderChange PlanGameLabel(GameNode node, string newLabel, PlanOptions options)
    {
        var change = new FolderChange
        {
            FolderPath = node.FolderPath,
            OldLabel = node.Label,
            NewLabel = newLabel,
            Kind = PendingEditKind.GameLabel,
        };

        newLabel = newLabel.Trim();

        string? nameProblem = PathSafety.Validate(newLabel);

        if (nameProblem != null)
        {
            change.Problems.Add(nameProblem);
            return change;
        }

        var renames = BuildImageRenames(node, newLabel, options, change);

        if (!change.IsValid)
            return change;

        AppendFileOperations(node, newLabel, renames, change, options);
        return change;
    }

    // Repoints CUE FILE entries at the spelling the files actually carry, for the case
    // where a rename landed on disk and the CUE kept the old letter case.
    public static FolderChange PlanCueReferenceFix(GameNode node)
    {
        var change = new FolderChange
        {
            FolderPath = node.FolderPath,
            OldLabel = node.Label,
            NewLabel = node.Label,
            Kind = PendingEditKind.CueReferenceFix,
        };

        if (node.Cue == null || node.CuePath == null)
        {
            change.Problems.Add("This entry has no CUE sheet.");
            return change;
        }

        var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var image in node.Images)
            onDisk[image.FileName] = image.FileName;

        var patch = new PlannedOperation
        {
            Kind = OperationKind.PatchCue,
            SourcePath = node.CuePath,
            TargetPath = node.CuePath,
        };

        foreach (var reference in node.Cue.Files)
        {
            if (!onDisk.TryGetValue(reference.Name, out string? actual))
                continue;

            if (string.Equals(reference.Name, actual, StringComparison.Ordinal))
                continue;

            patch.CueReplacements[reference.Name] = actual;
        }

        if (patch.CueReplacements.Count == 0)
        {
            change.Problems.Add("Every CUE reference already matches the files on disk.");
            return change;
        }

        patch.Summary = $"Repoint {PlatformUtil.Counted(patch.CueReplacements.Count, "FILE reference")} in {node.CueFileName}";
        change.Operations.Add(patch);

        return change;
    }

    // Drops the track marker from the data track so Folder Browsing draws a shorter row.
    // The other tracks keep their markers, which the firmware still groups correctly, and
    // CD audio still plays. Both were confirmed on hardware.
    public static FolderChange PlanShortenEntry(GameNode node)
    {
        var change = new FolderChange
        {
            FolderPath = node.FolderPath,
            OldLabel = node.MenuFileName,
            NewLabel = node.Label,
            Kind = PendingEditKind.ShortenMenuEntry,
        };

        var data = node.Images.FirstOrDefault(i => i.CarriesData);

        if (data == null)
        {
            change.Problems.Add("This entry has no data track.");
            return change;
        }

        string shortened = node.Label + Path.GetExtension(data.FileName);

        if (string.Equals(shortened, data.FileName, StringComparison.Ordinal))
        {
            change.Problems.Add("This entry already has no track marker.");
            return change;
        }

        if (SafeFileNames(node.FolderPath).Any(
                n => string.Equals(n, shortened, StringComparison.OrdinalIgnoreCase)))
        {
            change.Problems.Add($"\"{shortened}\" already exists in the folder.");
            return change;
        }

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.RenameFile,
            SourcePath = data.FullPath,
            TargetPath = Path.Combine(node.FolderPath, shortened),
            Summary = $"{data.FileName}  ->  {shortened}",
        });

        if (node.CuePath != null)
        {
            var patch = new PlannedOperation
            {
                Kind = OperationKind.PatchCue,
                SourcePath = node.CuePath,
                TargetPath = node.CuePath,
                Summary = $"Repoint {Path.GetFileName(node.CuePath)} at {shortened}",
            };

            patch.CueReplacements[data.FileName] = shortened;
            change.Operations.Add(patch);
        }

        return change;
    }

    public static FolderChange PlanCreateFolder(MenuFolderNode parent, string name)
    {
        name = name.Trim();

        var change = new FolderChange
        {
            FolderPath = parent.FullPath,
            OldLabel = parent.Name,
            NewLabel = name,
            Kind = PendingEditKind.CreateFolder,
        };

        string? problem = PathSafety.Validate(name);

        if (problem != null)
        {
            change.Problems.Add(problem);
            return change;
        }

        string target = Path.Combine(parent.FullPath, name);
        int fromRoot = PathSafety.FromRootLength(target, RootOf(parent));

        if (TakenAt(parent.FullPath, name))
        {
            change.Problems.Add($"\"{name}\" already exists here.");
            return change;
        }

        if (fromRoot > PathSafety.MaxPathLength)
        {
            change.Problems.Add(
                $"That folder would sit {fromRoot} characters from the card root. " +
                $"The console cannot load past {PathSafety.MaxPathLength}.");
            return change;
        }

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.CreateDirectory,
            TargetPath = target,
            Summary = $"Create {name}\\",
        });

        return change;
    }

    public static FolderChange PlanDeleteFolder(MenuFolderNode node)
    {
        var change = new FolderChange
        {
            FolderPath = node.FullPath,
            OldLabel = node.Name,
            NewLabel = node.Name,
            Kind = PendingEditKind.DeleteFolder,
        };

        if (node.IsRoot)
        {
            change.Problems.Add("The card's root cannot be deleted.");
            return change;
        }

        // XMC never removes anything it could not put back.
        if (!IsEmptyOnDisk(node.FullPath))
        {
            change.Problems.Add(
                $"\"{node.Name}\" still holds something. Empty it in your file browser first.");
            return change;
        }

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.DeleteDirectory,
            SourcePath = node.FullPath,
            Summary = $"Delete {node.Name}\\",
        });

        return change;
    }

    public static FolderChange PlanMove(CardNode node, MenuFolderNode destination)
    {
        var change = new FolderChange
        {
            FolderPath = SourcePathOf(node),
            OldLabel = node.Name,
            NewLabel = destination.Name,
            Kind = PendingEditKind.MoveEntry,
        };

        if (node is MenuFolderNode folder)
        {
            if (folder.IsRoot)
            {
                change.Problems.Add("The card's root cannot be moved.");
                return change;
            }

            if (SamePath(folder.FullPath, destination.FullPath) ||
                IsInside(destination.FullPath, folder.FullPath))
            {
                change.Problems.Add($"\"{folder.Name}\" cannot be moved inside itself.");
                return change;
            }

            if (SamePath(Path.GetDirectoryName(folder.FullPath) ?? "", destination.FullPath))
            {
                change.Problems.Add($"\"{folder.Name}\" is already here.");
                return change;
            }

            MoveDirectoryInto(folder.FullPath, folder.Name, destination.FullPath, change, RootOf(destination));
            return change;
        }

        if (node is not GameNode game)
        {
            change.Problems.Add("This entry cannot be moved.");
            return change;
        }

        if (game.OwnsFolder)
        {
            string source = game.FolderPath;
            string name = Path.GetFileName(source);

            if (SamePath(source, destination.FullPath) || IsInside(destination.FullPath, source))
            {
                change.Problems.Add($"\"{name}\" cannot be moved inside itself.");
                return change;
            }

            if (SamePath(Path.GetDirectoryName(source) ?? "", destination.FullPath))
            {
                change.Problems.Add($"\"{name}\" is already here.");
                return change;
            }

            MoveDirectoryInto(source, name, destination.FullPath, change, RootOf(destination));
            return change;
        }

        // A game sharing its folder moves as its own files, since the folder belongs to
        // its neighbors too.
        if (SamePath(game.FolderPath, destination.FullPath))
        {
            change.Problems.Add($"\"{game.Label}\" is already here.");
            return change;
        }

        MoveLooseGameInto(game, destination.FullPath, change, RootOf(destination));
        return change;
    }

    // Turns a loose game into one the console draws as a folder row.
    public static FolderChange PlanWrap(GameNode node)
    {
        var change = new FolderChange
        {
            FolderPath = node.FolderPath,
            OldLabel = node.MenuFileName,
            NewLabel = node.Label,
            Kind = PendingEditKind.WrapInFolder,
        };

        if (node.OwnsFolder)
        {
            change.Problems.Add($"\"{node.Label}\" already has a folder of its own.");
            return change;
        }

        string? problem = PathSafety.Validate(node.Label);

        if (problem != null)
        {
            change.Problems.Add(problem);
            return change;
        }

        if (TakenAt(node.FolderPath, node.Label))
        {
            change.Problems.Add($"\"{node.Label}\" already exists here.");
            return change;
        }

        string target = Path.Combine(node.FolderPath, node.Label);

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.CreateDirectory,
            TargetPath = target,
            Summary = $"Create {node.Label}\\",
        });

        MoveFilesInto(OwnFiles(node), target, change, RootOf(node));
        return change;
    }

    // Dissolves a game's folder so the console draws the image file itself.
    public static FolderChange PlanUnwrap(GameNode node)
    {
        var change = new FolderChange
        {
            FolderPath = node.FolderPath,
            OldLabel = node.Label,
            NewLabel = node.MenuFileName,
            Kind = PendingEditKind.UnwrapFolder,
        };

        if (!node.OwnsFolder)
        {
            change.Problems.Add($"\"{node.Label}\" is not in a folder of its own.");
            return change;
        }

        string? parent = Path.GetDirectoryName(node.FolderPath);

        if (string.IsNullOrEmpty(parent))
        {
            change.Problems.Add("This entry is already at the card's root.");
            return change;
        }

        var files = OwnFiles(node);

        foreach (string file in files)
        {
            if (TakenAt(parent, Path.GetFileName(file)))
            {
                change.Problems.Add($"\"{Path.GetFileName(file)}\" already exists in the folder above.");
                return change;
            }
        }

        // Anything the scan did not attribute to this game would be stranded.
        if (SafeFileNames(node.FolderPath).Count() != files.Count ||
            Directory.GetDirectories(node.FolderPath).Length > 0)
        {
            change.Problems.Add($"\"{node.Name}\" holds more than this game.");
            return change;
        }

        MoveFilesInto(files, parent, change, RootOf(node));

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.DeleteDirectory,
            SourcePath = node.FolderPath,
            Summary = $"Delete {node.Name}\\",
        });

        return change;
    }

    private static List<string> OwnFiles(GameNode node)
    {
        var files = new List<string>(node.Sidecars);

        foreach (var image in node.Images)
            files.Add(image.FullPath);

        return files;
    }

    private static void MoveFilesInto(
        List<string> files, string destination, FolderChange change, string rootPath)
    {
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            string target = Path.Combine(destination, name);
            int fromRoot = PathSafety.FromRootLength(target, rootPath);

            if (TakenAt(destination, name))
            {
                change.Problems.Add($"\"{name}\" already exists there.");
                return;
            }

            if (fromRoot > PathSafety.MaxPathLength)
            {
                change.Problems.Add(
                    $"\"{name}\" would sit {fromRoot} characters from the card root. " +
                    $"The console cannot load past {PathSafety.MaxPathLength}.");
                return;
            }

            change.Operations.Add(new PlannedOperation
            {
                Kind = OperationKind.RenameFile,
                SourcePath = file,
                TargetPath = target,
                Summary = $"{name}  ->  {destination}\\{name}",
            });
        }
    }

    private static void MoveLooseGameInto(
        GameNode game, string destination, FolderChange change, string rootPath)
    {
        MoveFilesInto(OwnFiles(game), destination, change, rootPath);
    }

    private static void MoveDirectoryInto(
        string source, string name, string destination, FolderChange change, string rootPath)
    {
        if (TakenAt(destination, name))
        {
            change.Problems.Add($"\"{name}\" already exists there.");
            return;
        }

        string target = Path.Combine(destination, name);

        foreach (string file in SafeFilePaths(source))
        {
            int length = PathSafety.FromRootLength(target, rootPath) + file.Length - source.Length;

            if (length > PathSafety.MaxPathLength)
            {
                change.Problems.Add(
                    $"\"{Path.GetFileName(file)}\" would sit {length} characters from the card root. " +
                    $"The console cannot load past {PathSafety.MaxPathLength}.");
                return;
            }
        }

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.RenameDirectory,
            SourcePath = source,
            TargetPath = target,
            Summary = $"{name}\\  ->  {destination}\\{name}\\",
        });
    }

    private static string RootOf(CardNode node)
    {
        var folder = node as MenuFolderNode ?? node.Parent!;

        while (folder.Parent != null)
            folder = folder.Parent;

        return folder.FullPath;
    }

    private static string SourcePathOf(CardNode node) =>
        node is GameNode game ? game.FolderPath : node.FullPath;

    private static bool SamePath(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string candidate, string ancestor)
    {
        string prefix = ancestor.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TakenAt(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        return Directory.Exists(path) || File.Exists(path);
    }

    private static bool IsEmptyOnDisk(string folder)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeFilePaths(string folder)
    {
        try
        {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    private static Dictionary<string, string> BuildImageRenames(
        GameNode node, string newLabel, PlanOptions options, FolderChange change)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = OrderImagesByCue(node);

        int assigned = 0;

        foreach (var image in images)
        {
            string extension = Path.GetExtension(image.FileName);
            string baseName = Path.GetFileNameWithoutExtension(image.FileName);
            assigned++;

            int? trackNumber = image.TrackNumber;

            // A track marker in an unrecognized shape still carries the number, so a
            // rename doubles as a repair.
            if (trackNumber == null && LabelRules.TrySplitLooseTrackSuffix(baseName, out _, out int loose))
                trackNumber = loose;

            if (trackNumber == null && images.Count > 1)
                trackNumber = assigned;

            string newName = LabelRules.ImageFileName(newLabel, trackNumber, extension, options.PadTrackNumbers);

            if (!string.Equals(newName, image.FileName, StringComparison.Ordinal))
                renames[image.FileName] = newName;
        }

        return renames;
    }

    private static void AppendFileOperations(
        GameNode node,
        string newLabel,
        Dictionary<string, string> imageRenames,
        FolderChange change,
        PlanOptions options)
    {
        string folder = node.FolderPath;

        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var untouched = new HashSet<string>(SafeFileNames(folder), StringComparer.OrdinalIgnoreCase);

        foreach (var pair in imageRenames)
            untouched.Remove(pair.Key);

        foreach (var pair in imageRenames)
        {
            if (!targetNames.Add(pair.Value))
            {
                change.Problems.Add($"Two disc images would both be named \"{pair.Value}\".");
                continue;
            }

            if (untouched.Contains(pair.Value))
            {
                change.Problems.Add($"\"{pair.Value}\" already exists in the folder.");
                continue;
            }

            change.Operations.Add(new PlannedOperation
            {
                Kind = OperationKind.RenameFile,
                SourcePath = Path.Combine(folder, pair.Key),
                TargetPath = Path.Combine(folder, pair.Value),
                Summary = $"{pair.Key}  ->  {pair.Value}",
            });
        }

        // CUE, CCD and SUB files all carry the label as their base name.
        string? newCueName = null;

        foreach (string sidecar in node.Sidecars)
        {
            string oldName = Path.GetFileName(sidecar);
            string extension = Path.GetExtension(sidecar);
            string newName = newLabel + extension;

            if (string.Equals(extension, ".cue", StringComparison.OrdinalIgnoreCase))
                newCueName = newName;

            if (string.Equals(newName, oldName, StringComparison.Ordinal))
                continue;

            untouched.Remove(oldName);

            if (untouched.Contains(newName))
            {
                change.Problems.Add($"\"{newName}\" already exists in the folder.");
                continue;
            }

            change.Operations.Add(new PlannedOperation
            {
                Kind = OperationKind.RenameFile,
                SourcePath = sidecar,
                TargetPath = Path.Combine(folder, newName),
                Summary = $"{oldName}  ->  {newName}",
            });
        }

        if (node.CuePath != null && imageRenames.Count > 0)
        {
            string cueAfterRename = Path.Combine(folder, newCueName ?? node.CueFileName!);

            var patch = new PlannedOperation
            {
                Kind = OperationKind.PatchCue,
                SourcePath = cueAfterRename,
                TargetPath = cueAfterRename,
                Summary = $"Update {PlatformUtil.Counted(imageRenames.Count, "FILE reference")} in {Path.GetFileName(cueAfterRename)}",
            };

            foreach (var pair in imageRenames)
                patch.CueReplacements[pair.Key] = pair.Value;

            change.Operations.Add(patch);
        }

        string targetFolder = folder;

        if (options.RenameFolder && !string.Equals(node.FolderName, newLabel, StringComparison.Ordinal))
        {
            if (!node.OwnsFolder)
            {
                change.Warnings.Add("The folder keeps its name because other games live in it.");
            }
            else
            {
                string parent = Path.GetDirectoryName(folder) ?? "";
                targetFolder = Path.Combine(parent, newLabel);

                if (ConflictsWithSibling(parent, folder, newLabel))
                {
                    change.Problems.Add($"A folder named \"{newLabel}\" already exists here.");
                    targetFolder = folder;
                }
                else
                {
                    change.Operations.Add(new PlannedOperation
                    {
                        Kind = OperationKind.RenameDirectory,
                        SourcePath = folder,
                        TargetPath = targetFolder,
                        Summary = $"{node.FolderName}\\  ->  {newLabel}\\",
                    });
                }
            }
        }

        WarnOnLongPaths(change, targetFolder, imageRenames.Values, RootOf(node));

        if (LabelRules.IsTrimmedByMenu(newLabel))
        {
            change.Warnings.Add(
                $"\"{newLabel}\" is {newLabel.Length} characters. The menu will show " +
                $"\"{LabelRules.MenuText(newLabel)}\".");
        }

        if (change.Operations.Count == 0 && change.IsValid)
            change.Problems.Add("Nothing would change.");
    }

    private static void WarnOnLongPaths(
        FolderChange change, string folder, IEnumerable<string> names, string rootPath)
    {
        foreach (string name in names)
        {
            int length = PathSafety.FromRootLength(folder, rootPath) + 1 + name.Length;

            if (length > PathSafety.MaxPathLength)
            {
                change.Problems.Add(
                    $"\"{name}\" would sit {length} characters from the card root. " +
                    $"The console cannot load past {PathSafety.MaxPathLength}.");
                return;
            }
        }
    }

    public static FolderChange PlanFolderRename(MenuFolderNode node, string newName)
    {
        var change = new FolderChange
        {
            FolderPath = node.FullPath,
            OldLabel = node.Name,
            NewLabel = newName,
            Kind = PendingEditKind.FolderName,
        };

        newName = newName.Trim();

        string? problem = PathSafety.Validate(newName);

        if (problem != null)
        {
            change.Problems.Add(problem);
            return change;
        }

        if (string.Equals(newName, node.Name, StringComparison.Ordinal))
        {
            change.Problems.Add("Nothing would change.");
            return change;
        }

        string parent = Path.GetDirectoryName(node.FullPath) ?? "";

        if (ConflictsWithSibling(parent, node.FullPath, newName))
        {
            change.Problems.Add($"A folder named \"{newName}\" already exists here.");
            return change;
        }

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.RenameDirectory,
            SourcePath = node.FullPath,
            TargetPath = Path.Combine(parent, newName),
            Summary = $"{node.Name}\\  ->  {newName}\\",
        });

        return change;
    }

    // The single byte the console reads at boot. Only the Folder Browsing bit moves.
    // Every other bit, and anything after byte 0, is preserved.
    public static FolderChange PlanConfigMode(MenuFolderNode root, bool browse)
    {
        string folder = Path.Combine(root.FullPath, RemovableDrives.SystemFolderName);
        string path = Path.Combine(folder, "config.txt");

        var change = new FolderChange
        {
            FolderPath = folder,
            OldLabel = $"Folder Browsing {(browse ? "off" : "on")}",
            NewLabel = $"Folder Browsing {(browse ? "on" : "off")}",
            Kind = PendingEditKind.SetFolderBrowsing,
        };

        if (!Directory.Exists(folder))
        {
            change.Problems.Add($"The card has no \"{RemovableDrives.SystemFolderName}\" folder.");
            return change;
        }

        byte[] bytes;

        try
        {
            bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            change.Problems.Add($"\"config.txt\" could not be read: {ex.Message}");
            return change;
        }

        bool existed = bytes.Length > 0;
        byte before = existed ? bytes[0] : (byte)0;

        if (!existed)
            bytes = new[] { browse ? (byte)(0x03 | CardConfig.FolderBrowsingFlag) : (byte)0x03 };
        else if (browse)
            bytes[0] |= CardConfig.FolderBrowsingFlag;
        else
            bytes[0] &= unchecked((byte)~CardConfig.FolderBrowsingFlag);

        if (existed && bytes[0] == before)
            return change;

        change.Operations.Add(new PlannedOperation
        {
            Kind = OperationKind.WriteConfig,
            TargetPath = path,
            FileBytes = bytes,
            Summary = $"config.txt: turn Folder Browsing {(browse ? "on" : "off")}",
        });

        return change;
    }

    private static List<TrackImage> OrderImagesByCue(GameNode node)
    {
        if (node.Cue == null)
            return node.Images.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase).ToList();

        var order = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < node.Cue.Files.Count; i++)
            order[node.Cue.Files[i].Name] = i;

        return node.Images
            .OrderBy(i => order.TryGetValue(i.FileName, out int index) ? index : int.MaxValue)
            .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeFileNames(string folder)
    {
        try
        {
            return Directory.GetFiles(folder).Select(Path.GetFileName)!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // A case-only rename lands on an existing path, so the same directory has to be
    // allowed through.
    private static bool ConflictsWithSibling(string parent, string currentPath, string newName)
    {
        string target = Path.Combine(parent, newName);

        if (string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase))
            return false;

        return Directory.Exists(target) || File.Exists(target);
    }
}
