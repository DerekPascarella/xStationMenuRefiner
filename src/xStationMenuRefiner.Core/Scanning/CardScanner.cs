using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using xStationMenuRefiner.Core.Model;
using xStationMenuRefiner.Core.Naming;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Scanning;

public static class CardScanner
{
    public const string TempSuffix = ".__xsmc__";

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "00xstation",
        "System Volume Information",
        "$RECYCLE.BIN",
        "RECYCLER",
        "found.000",
    };

    // The xStation scanner can stop part way through a directory holding one of these,
    // which drops games from the menu.
    private static readonly HashSet<string> MacMetadataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".Trashes",
        ".Spotlight-V100",
        ".fseventsd",
        ".TemporaryItems",
        ".DocumentRevisions-V100",
    };

    private sealed class ParsedCue
    {
        public string Path { get; init; } = "";
        public CueDocument Document { get; init; } = null!;
    }

    // One CUE sheet and the images it owns, or a set of images no CUE claimed.
    private sealed class DiscSet
    {
        public ParsedCue? Cue { get; init; }

        // Set when a CUE sheet could not be read, so the row can still name it.
        public string? UnreadableCuePath { get; init; }

        public List<string> Images { get; init; } = new();

        // A second CUE sheet that named an image this set already owns.
        public ParsedCue? Contender { get; set; }
    }

    public static CardScanResult Scan(string rootPath)
    {
        var result = new CardScanResult
        {
            RootPath = rootPath,
        };

        result.Root.FullPath = rootPath;
        result.Root.Name = DisplayNameForRoot(rootPath);
        result.Root.IsRoot = true;

        ScanDirectory(result.Root, rootPath, result, isRoot: true);
        FlagDuplicateLabels(result);
        FlagDuplicateRows(result);
        SortChildren(result.Root);

        return result;
    }

    private static string DisplayNameForRoot(string rootPath)
    {
        string trimmed = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? rootPath : name;
    }

    // Mirrors the card's directories onto the parent's child list. Every folder is kept,
    // including one wrapping a single game and one holding nothing, because Folder
    // Browsing draws both. The flat menu reads CardScanResult.Games instead and never
    // looks at this tree.
    private static void ScanDirectory(MenuFolderNode parent, string directory, CardScanResult result, bool isRoot)
    {
        string[] subdirectories;
        string[] files;

        try
        {
            subdirectories = Directory.GetDirectories(directory);
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AddIssue(parent, result, IssueKind.InvalidName, IssueSeverity.Error,
                "Folder could not be read.", directory);
            return;
        }

        FlagStrayFiles(parent, files, result);

        var usableSubdirectories = new List<string>();

        foreach (string subdirectory in subdirectories)
        {
            string name = Path.GetFileName(subdirectory);

            if (MacMetadataFolders.Contains(name))
            {
                AddIssue(parent, result, IssueKind.MacMetadata, IssueSeverity.Error,
                    $"\"{name}\" was left behind by macOS and can stop the game list scan part way through.",
                    subdirectory);
                continue;
            }

            if (SkippedFolders.Contains(name) || name.StartsWith(".", StringComparison.Ordinal))
                continue;

            usableSubdirectories.Add(subdirectory);
        }

        var sets = PartitionDiscSets(files);
        bool holdsDiscContent = sets.Count > 0;

        if (isRoot)
        {
            if (holdsDiscContent)
                EmitGames(parent, directory, files, sets, result);

            foreach (string subdirectory in usableSubdirectories)
                ScanDirectory(parent, subdirectory, result, isRoot: false);

            return;
        }

        var folder = new MenuFolderNode
        {
            FullPath = directory,
            Name = Path.GetFileName(directory),
            Parent = parent,
        };

        if (holdsDiscContent)
            EmitGames(folder, directory, files, sets, result);

        foreach (string subdirectory in usableSubdirectories)
            ScanDirectory(folder, subdirectory, result, isRoot: false);

        CheckName(folder, result);

        parent.Children.Add(folder);
        result.Folders.Add(folder);
    }

    private static void FlagStrayFiles(MenuFolderNode parent, string[] files, CardScanResult result)
    {
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);

            if (name.Contains(TempSuffix, StringComparison.Ordinal))
            {
                AddIssue(parent, result, IssueKind.LeftoverTempName, IssueSeverity.Error,
                    "Temporary file left behind by an interrupted rename.", file);
                continue;
            }

            if (name.StartsWith("._", StringComparison.Ordinal) ||
                name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(parent, result, IssueKind.MacMetadata, IssueSeverity.Error,
                    $"\"{name}\" was left behind by macOS and can stop the game list scan part way through.",
                    file);
            }
        }
    }

    // The CUE sheets decide which images belong together. Images no CUE names stand on
    // their own: a family of tracks sharing a stem is one game, anything else groups by
    // the name it resolves to.
    private static List<DiscSet> PartitionDiscSets(string[] files)
    {
        var cuePaths = files.Where(f => HasExtension(f, ".cue"))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        var images = files.Where(f => LabelRules.IsImageExtension(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var sets = new List<DiscSet>();
        var owners = new Dictionary<string, DiscSet>(StringComparer.OrdinalIgnoreCase);

        foreach (string cuePath in cuePaths)
        {
            ParsedCue cue;

            try
            {
                cue = new ParsedCue { Path = cuePath, Document = CueDocument.Load(cuePath) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sets.Add(new DiscSet { UnreadableCuePath = cuePath });
                continue;
            }

            var wanted = new HashSet<string>(
                cue.Document.Files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

            var set = new DiscSet { Cue = cue };

            foreach (string image in images)
            {
                string name = Path.GetFileName(image);

                if (!wanted.Contains(name))
                    continue;

                // Matching without regard to case keeps a CUE that disagrees only in
                // spelling attached to its own game, where CaseOnlyCueMismatch reports it.
                if (owners.TryGetValue(name, out var owner))
                {
                    owner.Contender ??= cue;
                    continue;
                }

                owners[name] = set;
                set.Images.Add(image);
            }

            sets.Add(set);
        }

        var unclaimed = images.Where(i => !owners.ContainsKey(Path.GetFileName(i)));

        foreach (var group in unclaimed.GroupBy(
                     i => RepairedStem(Path.GetFileNameWithoutExtension(i)),
                     StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();

            if (IsTrackFamily(members))
            {
                sets.Add(new DiscSet { Images = members });
                continue;
            }

            foreach (var conforming in members.GroupBy(
                         i => LabelRules.LabelFromFileName(Path.GetFileName(i)),
                         StringComparer.OrdinalIgnoreCase))
            {
                sets.Add(new DiscSet { Images = conforming.ToList() });
            }
        }

        return sets;
    }

    private static void EmitGames(
        MenuFolderNode parent,
        string directory,
        string[] files,
        List<DiscSet> sets,
        CardScanResult result)
    {
        var ccdPaths = files.Where(f => HasExtension(f, ".ccd")).ToList();
        var subPaths = files.Where(f => HasExtension(f, ".sub")).ToList();

        foreach (var set in sets)
            BuildGameEntry(parent, directory, set, ccdPaths, subPaths, sets.Count, result);
    }

    // A CCD or SUB carries the same base name as its images. A folder holding one game
    // hands over everything, so a mismatched pair still reaches AttachIssues.
    private static List<string> SidecarsFor(string label, List<string> candidates, int setCount)
    {
        if (setCount == 1)
            return candidates;

        return candidates
            .Where(c => string.Equals(
                Path.GetFileNameWithoutExtension(c), label, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // One disc image set yields one row. When the image names disagree the card shows
    // several entries, and the count is recorded on the node.
    private static void BuildGameEntry(
        MenuFolderNode parent,
        string directory,
        DiscSet set,
        List<string> ccdPaths,
        List<string> subPaths,
        int setCount,
        CardScanResult result)
    {
        string folderName = Path.GetFileName(directory);
        var images = set.Images;
        var owningCue = set.Cue;

        var node = new GameNode
        {
            FullPath = directory,
            Name = folderName,
            FolderPath = directory,
            FolderName = folderName,
            // The card root is not a folder the user can rename, move or dissolve, so a
            // lone set sitting there is treated as loose files rather than an owner.
            OwnsFolder = setCount == 1 && !parent.IsRoot,
            Parent = parent,
        };

        if (images.Count == 0)
        {
            string cuePath = owningCue?.Path ?? set.UnreadableCuePath!;

            node.Label = Path.GetFileNameWithoutExtension(cuePath);
            node.Format = DiscFormat.Unsupported;
            node.CuePath = cuePath;
            node.CueFileName = Path.GetFileName(cuePath);
            node.Cue = owningCue?.Document;

            AddIssue(node, result, IssueKind.UnresolvedCueReference, IssueSeverity.Error,
                "Folder holds a CUE sheet and no disc images.", directory);

            parent.Children.Add(node);
            result.Games.Add(node);
            return;
        }

        var groups = images
            .GroupBy(f => LabelRules.LabelFromFileName(Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var firstDataFile = owningCue?.Document.FirstDataTrackFile();

        string label = groups[0].Key;

        if (firstDataFile != null)
        {
            string fromCue = LabelRules.LabelFromFileName(firstDataFile.Name);
            var match = groups.FirstOrDefault(g => string.Equals(g.Key, fromCue, StringComparison.OrdinalIgnoreCase));

            if (match != null)
                label = match.Key;
        }

        // When the image names disagree, the first entry's text carries a stray track
        // marker. The row shows the name the folder resolves to once repaired.
        if (groups.Count > 1)
        {
            var stems = images
                .Select(f => RepairedStem(Path.GetFileNameWithoutExtension(f)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (stems.Count == 1)
                label = stems[0];
        }

        var ownCcds = SidecarsFor(label, ccdPaths, setCount);
        var ownSubs = SidecarsFor(label, subPaths, setCount);

        node.Label = label;
        node.MenuEntryCount = groups.Count;
        node.Format = owningCue != null ? DiscFormat.CueBin : DetectFormat(images);
        node.CuePath = owningCue?.Path;
        node.CueFileName = owningCue == null ? null : Path.GetFileName(owningCue.Path);
        node.Cue = owningCue?.Document;

        if (owningCue != null)
            node.Sidecars.Add(owningCue.Path);

        node.Sidecars.AddRange(ownCcds);
        node.Sidecars.AddRange(ownSubs);

        foreach (string imagePath in images)
            node.Images.Add(BuildImage(imagePath, owningCue?.Document));

        AttachIssues(node, result, owningCue, set.Contender, ownCcds, images);

        parent.Children.Add(node);
        result.Games.Add(node);
    }

    private static string RepairedStem(string baseName) =>
        LabelRules.TrySplitLooseTrackSuffix(baseName, out string stem, out _)
            ? stem
            : LabelRules.StripTrackSuffix(baseName);

    // A family is two or more images reading as tracks of one game: at least one carries
    // a track marker and one is the first track, an unmarked name standing in for it.
    // Titles that merely end in "Track" and a number have no first track and stay apart.
    private static bool IsTrackFamily(List<string> images)
    {
        if (images.Count < 2)
            return false;

        bool marked = false;
        bool hasFirst = false;

        foreach (string image in images)
        {
            string baseName = Path.GetFileNameWithoutExtension(image);
            int? number = LabelRules.TrackNumberFromSuffix(baseName);

            if (number == null && LabelRules.TrySplitLooseTrackSuffix(baseName, out _, out int loose))
                number = loose;

            if (number == null || number == 1)
                hasFirst = true;

            if (number != null)
                marked = true;
        }

        return marked && hasFirst;
    }

    private static DiscFormat DetectFormat(List<string> images)
    {
        var extensions = images
            .Select(f => Path.GetExtension(f).ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (extensions.Contains(".bin"))
            return DiscFormat.CueBin;

        if (extensions.Contains(".img"))
            return DiscFormat.CcdImg;

        if (extensions.Contains(".iso"))
            return DiscFormat.Iso;

        return DiscFormat.Unsupported;
    }

    // xStation reads raw 2352-byte sectors only, so a cooked 2048-byte-sector image
    // lists in the menu and then fails to boot. A CUE can declare the layout outright;
    // otherwise the data tracks are sniffed. Returns the offending file's name, or null.
    private static string? CookedSectorFile(GameNode node, ParsedCue? owningCue)
    {
        var declared = owningCue?.Document.Tracks
            .FirstOrDefault(t => t.Mode.EndsWith("/2048", StringComparison.OrdinalIgnoreCase));

        if (declared != null && declared.FileIndex >= 0)
            return owningCue!.Document.Files[declared.FileIndex].Name;

        foreach (var image in node.Images)
        {
            if (image.CarriesData && HasCookedSignature(image.FullPath))
                return image.FileName;
        }

        return null;
    }

    private static readonly byte[] SectorSync =
        { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    // A raw file opens with the sync pattern whatever its extension. Absent that, an
    // ISO9660 descriptor at the 2048-sector position gives the layout away, and a file
    // matching neither signature is left alone.
    private static bool HasCookedSignature(string path)
    {
        const int descriptorOffset = 16 * 2048;
        Span<byte> buffer = stackalloc byte[12];

        try
        {
            using var stream = File.OpenRead(path);

            if (stream.Length < descriptorOffset + 6)
                return false;

            if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length &&
                buffer.SequenceEqual(SectorSync))
                return false;

            stream.Position = descriptorOffset;

            return stream.ReadAtLeast(buffer[..6], 6, throwOnEndOfStream: false) == 6 &&
                buffer[0] == 0x01 && buffer[1..6].SequenceEqual("CD001"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The firmware boots a CloneCD IMG as one bare data track, so a CCD sheet listing
    // a second track is describing audio the console will never play. Returns the
    // offending sheet's name, or null.
    private static string? MultiTrackCcdFile(List<string> ccdPaths)
    {
        foreach (string path in ccdPaths)
        {
            try
            {
                int tracks = File.ReadLines(path).Count(line =>
                    line.TrimStart().StartsWith("[TRACK", StringComparison.OrdinalIgnoreCase));

                if (tracks > 1)
                    return Path.GetFileName(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static TrackImage BuildImage(string imagePath, CueDocument? cue)
    {
        string fileName = Path.GetFileName(imagePath);
        string baseName = Path.GetFileNameWithoutExtension(fileName);

        var image = new TrackImage
        {
            FileName = fileName,
            FullPath = imagePath,
            TrackNumber = LabelRules.TrackNumberFromSuffix(baseName),
        };

        try
        {
            image.Size = new FileInfo(imagePath).Length;
        }
        catch (IOException)
        {
            image.Size = 0;
        }

        if (cue == null)
        {
            image.CarriesData = true;
            return image;
        }

        int fileIndex = cue.Files.FindIndex(f => string.Equals(f.Name, fileName, StringComparison.Ordinal));
        image.ReferencedByCue = fileIndex >= 0;

        if (fileIndex < 0)
            return image;

        foreach (var track in cue.TracksForFile(fileIndex))
        {
            image.CueTracks.Add(track.Number);

            if (!track.IsAudio)
                image.CarriesData = true;
        }

        return image;
    }

    private static void AttachIssues(
        GameNode node,
        CardScanResult result,
        ParsedCue? owningCue,
        ParsedCue? contender,
        List<string> ownCcds,
        List<string> images)
    {
        switch (node.Format)
        {
            // Hardware boots a lone data track whatever its extension. Only a family of
            // tracks that lost its CUE sheet is broken, because nothing binds the tracks.
            case DiscFormat.CueBin when owningCue == null:
            case DiscFormat.CcdImg when ownCcds.Count == 0:
                if (IsTrackFamily(images))
                {
                    AddIssue(node, result, IssueKind.MissingCue, IssueSeverity.Error,
                        "These images look like tracks of one game and need a CUE sheet to bind them.",
                        node.FolderPath);
                }
                break;

            case DiscFormat.Unsupported:
                AddIssue(node, result, IssueKind.MissingCue, IssueSeverity.Error,
                    $"Unrecognized disc image. xStation reads {LabelRules.SupportedFormatsDisplay}.",
                    node.FolderPath);
                break;
        }

        string? cooked = CookedSectorFile(node, owningCue);

        if (cooked != null)
        {
            AddIssue(node, result, IssueKind.UnsupportedSectorSize, IssueSeverity.Warning,
                $"\"{cooked}\" stores 2048 bytes per sector, which xStation cannot read. " +
                "Replace it with a CUE/BIN rip of this game.", node.FolderPath);
        }

        // A CUE sheet, when present, is what the firmware reads, so the CCD only
        // matters when the IMG stands alone.
        string? multiTrack = owningCue == null ? MultiTrackCcdFile(ownCcds) : null;

        if (multiTrack != null)
        {
            AddIssue(node, result, IssueKind.MultiTrackCcd, IssueSeverity.Warning,
                $"\"{multiTrack}\" lists several tracks, but xStation never reads CCD sheets, " +
                "so everything past the data track, usually CD audio, will not play. " +
                "Replace it with a CUE/BIN rip of this game.", node.FolderPath);
        }

        if (contender != null && node.CuePath != null)
        {
            string mine = Path.GetFileName(node.CuePath);
            string theirs = Path.GetFileName(contender.Path);
            bool mineFirst = string.CompareOrdinal(mine, theirs) <= 0;

            AddIssue(node, result, IssueKind.MultipleCues, IssueSeverity.Error,
                $"\"{(mineFirst ? mine : theirs)}\" and \"{(mineFirst ? theirs : mine)}\" " +
                "both name the same disc image.", node.FolderPath);
        }

        if (node.MenuEntryCount > 1)
        {
            AddIssue(node, result, IssueKind.SplitMenuEntries, IssueSeverity.Error,
                $"This folder will appear as {node.MenuEntryCount} separate entries because the image names do not match.",
                node.FolderPath, canAutoFix: true);
        }

        if (LabelRules.IsTrimmedByMenu(node.Label))
        {
            AddIssue(node, result, IssueKind.LabelTooLong, IssueSeverity.Warning,
                $"Label is {node.Label.Length} characters. The flat menu will show " +
                $"\"{LabelRules.MenuText(node.Label)}\".",
                node.FolderPath, appliesTo: MenuMode.Flat);
        }

        // Folder Browsing draws the file name, which carries the extension and any track
        // marker, so a label that fits the flat menu can still be cut here.
        string row = node.MenuFileName;
        string withoutMarker = node.Label + Path.GetExtension(row);

        if (LabelRules.IsTrimmedByMenu(row))
        {
            bool carriesMarker = !string.Equals(row, withoutMarker, StringComparison.Ordinal);

            AddIssue(node, result, IssueKind.LabelTooLong, IssueSeverity.Warning,
                $"This row is {row.Length} characters. Folder Browsing will show " +
                $"\"{LabelRules.MenuText(row)}\".",
                node.FolderPath,
                canAutoFix: carriesMarker && !LabelRules.IsTrimmedByMenu(withoutMarker),
                appliesTo: MenuMode.Browse);
        }

        var cueNames = new HashSet<string>(StringComparer.Ordinal);

        if (owningCue != null)
        {
            foreach (var reference in owningCue.Document.Files)
                cueNames.Add(reference.Name);
        }

        foreach (var image in node.Images)
        {
            string baseName = Path.GetFileNameWithoutExtension(image.FileName);

            if (!LabelRules.HasConformingTrackSuffix(baseName) &&
                LabelRules.TrySplitLooseTrackSuffix(baseName, out _, out _))
            {
                AddIssue(node, result, IssueKind.NonConformingTrackSuffix, IssueSeverity.Error,
                    $"\"{image.FileName}\" carries a track number xStation does not recognize.",
                    image.FullPath, canAutoFix: true);
            }

            int fromRoot = PathSafety.FromRootLength(image.FullPath, result.RootPath);

            if (fromRoot > PathSafety.MaxPathLength)
            {
                AddIssue(node, result, IssueKind.PathTooLong, IssueSeverity.Error,
                    $"Path is {fromRoot} characters from the card root. " +
                    $"The console cannot load past {PathSafety.MaxPathLength}.",
                    image.FullPath);
            }

            if (owningCue == null || image.ReferencedByCue)
                continue;

            string? caseMatch = cueNames.FirstOrDefault(
                n => string.Equals(n, image.FileName, StringComparison.OrdinalIgnoreCase));

            if (caseMatch != null)
            {
                AddIssue(node, result, IssueKind.CaseOnlyCueMismatch, IssueSeverity.Error,
                    $"CUE names \"{caseMatch}\" while the disc image is \"{image.FileName}\".",
                    image.FullPath, canAutoFix: true);
            }
            else
            {
                AddIssue(node, result, IssueKind.OrphanImage, IssueSeverity.Warning,
                    $"\"{image.FileName}\" is not referenced by the CUE sheet.", image.FullPath);
            }
        }

        if (owningCue != null)
        {
            var diskNames = new HashSet<string>(images.Select(Path.GetFileName)!, StringComparer.Ordinal);
            string folder = Path.GetDirectoryName(owningCue.Path) ?? node.FolderPath;

            foreach (var reference in owningCue.Document.Files)
            {
                if (diskNames.Contains(reference.Name))
                    continue;

                bool caseOnly = diskNames.Any(
                    n => string.Equals(n, reference.Name, StringComparison.OrdinalIgnoreCase));

                if (caseOnly)
                    continue;

                AddIssue(node, result, IssueKind.UnresolvedCueReference, IssueSeverity.Error,
                    $"CUE references \"{reference.Name}\", which is missing from the folder.",
                    Path.Combine(folder, reference.Name));
            }
        }

        // A game at the card root carries the root's name, which is empty on a real
        // drive. There is nothing there for the user to fix, so it is not checked.
        if (node.Parent is not { IsRoot: true })
            CheckName(node, result);
    }

    // The trim can flatten two different names onto one row, so the comparison is on what
    // the menu draws. The flat menu is one card-wide list, so a clash reaches across
    // folders.
    private static void FlagDuplicateLabels(CardScanResult result)
    {
        var duplicates = result.Games
            .Where(g => !string.IsNullOrEmpty(g.Label))
            .GroupBy(g => LabelRules.MenuText(g.Label), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicates)
        {
            foreach (var game in group)
            {
                AddIssue(game, result, IssueKind.DuplicateLabel, IssueSeverity.Warning,
                    $"{group.Count()} folders produce the flat menu entry \"{group.Key}\".",
                    game.FolderPath, appliesTo: MenuMode.Flat);
            }
        }
    }

    // Folder Browsing only ever draws siblings together. A directory cannot hold two
    // entries named the same, but it can hold two whose names differ only past the trim,
    // and those land on one row.
    private static void FlagDuplicateRows(CardScanResult result)
    {
        foreach (var folder in result.Folders.Append(result.Root))
        {
            var duplicates = folder.Children
                .Select(child => new
                {
                    Node = child,
                    Text = LabelRules.MenuText(child.MenuTextFor(MenuMode.Browse)),
                })
                .Where(row => !string.IsNullOrEmpty(row.Text))
                .GroupBy(row => row.Text, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            foreach (var group in duplicates)
            {
                foreach (var row in group)
                {
                    AddIssue(row.Node, result, IssueKind.DuplicateLabel, IssueSeverity.Warning,
                        $"{group.Count()} entries in this folder all appear as \"{group.Key}\".",
                        row.Node.FullPath, appliesTo: MenuMode.Browse);
                }
            }
        }
    }

    private static void CheckName(CardNode node, CardScanResult result)
    {
        string? problem = PathSafety.Validate(node.Name);

        if (problem != null)
            AddIssue(node, result, IssueKind.InvalidName, IssueSeverity.Warning, problem, node.FullPath);
    }

    private static void AddIssue(
        CardNode node,
        CardScanResult result,
        IssueKind kind,
        IssueSeverity severity,
        string message,
        string path,
        bool canAutoFix = false,
        MenuMode? appliesTo = null)
    {
        var issue = new EntryIssue
        {
            Kind = kind,
            Severity = severity,
            Message = message,
            Path = path,
            Node = node,
            CanAutoFix = canAutoFix,
            AppliesTo = appliesTo,
        };

        node.Issues.Add(issue);

        if (result.ReportOnce(issue))
            result.Issues.Add(issue);
    }

    private static bool HasExtension(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    // Folder Browsing draws every folder before any game, each group alphabetical.
    // Sorting on Name rather than menu text keeps one stable order for both modes.
    public static void SortChildren(MenuFolderNode folder)
    {
        folder.Children.Sort((a, b) =>
        {
            bool aFolder = a is MenuFolderNode;
            bool bFolder = b is MenuFolderNode;

            if (aFolder != bFolder)
                return aFolder ? -1 : 1;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var child in folder.Children)
        {
            if (child is MenuFolderNode subfolder)
                SortChildren(subfolder);
        }
    }
}
