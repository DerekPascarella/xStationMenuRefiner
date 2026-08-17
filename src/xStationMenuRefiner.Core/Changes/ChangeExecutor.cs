using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using xStationMenuRefiner.Core.Model;
using xStationMenuRefiner.Core.Scanning;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.Core.Changes;

public sealed class FolderExecutionResult
{
    public string FolderPath { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public bool RolledBack { get; set; }
    public string? Error { get; set; }
    public int OperationsCompleted { get; set; }
}

public sealed class ExecutionResult
{
    public List<FolderExecutionResult> Folders { get; } = new();

    public int Succeeded => Folders.Count(f => f.Success);
    public int Failed => Folders.Count(f => !f.Success && !f.Skipped);
    public int Skipped => Folders.Count(f => f.Skipped);

    public int OperationsCompleted
    {
        get
        {
            int total = 0;

            foreach (var folder in Folders)
                total += folder.OperationsCompleted;

            return total;
        }
    }
}

public static class ChangeExecutor
{
    private enum UndoKind
    {
        MoveFile,
        MoveDirectory,
        RestoreBytes,
        CreateDirectory,
        DeleteDirectory,
        DeleteFile,
    }

    private sealed class UndoStep
    {
        public UndoKind Kind { get; init; }
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public byte[]? Bytes { get; init; }
    }

    public static ExecutionResult Execute(
        ChangePlan plan,
        Action<string, int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ExecutionResult();
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int index = 0;
        int total = plan.Changes.Count;

        foreach (var change in plan.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            string label = string.IsNullOrEmpty(change.NewLabel) ? change.OldLabel : change.NewLabel;
            progress?.Invoke(label, index, total);

            var folderResult = new FolderExecutionResult
            {
                FolderPath = change.FolderPath,
                Label = label,
            };

            if (!change.IsValid)
            {
                folderResult.Skipped = true;
                folderResult.Error = string.Join(" ", change.Problems);
                result.Folders.Add(folderResult);
                continue;
            }

            var journal = new List<UndoStep>();

            try
            {
                RunOperations(change, redirects, journal, folderResult);
                folderResult.Success = true;
            }
            catch (Exception ex)
            {
                folderResult.Success = false;
                folderResult.Error = ex.Message;
                folderResult.RolledBack = TryRollback(journal);
            }

            result.Folders.Add(folderResult);
        }

        return result;
    }

    private static void RunOperations(
        FolderChange change,
        Dictionary<string, string> redirects,
        List<UndoStep> journal,
        FolderExecutionResult folderResult)
    {
        // A folder has to exist before anything moves into it.
        foreach (var operation in change.Operations)
        {
            if (operation.Kind != OperationKind.CreateDirectory)
                continue;

            string created = Resolve(operation.TargetPath, redirects);

            Directory.CreateDirectory(created);
            journal.Add(new UndoStep { Kind = UndoKind.DeleteDirectory, From = created });

            VerifyDirectoryName(created);
            folderResult.OperationsCompleted++;
        }

        var fileRenames = change.Operations.Where(o => o.Kind == OperationKind.RenameFile).ToList();
        var staged = new List<(string Temp, string Target)>();

        // Every file moves to a temporary name before it takes its final one. A rename
        // that changes only letter case does nothing in a single hop, and two files can
        // swap names inside one folder.
        int counter = 0;

        foreach (var operation in fileRenames)
        {
            string source = Resolve(operation.SourcePath, redirects);
            string temp = UniqueTempPath(source, ref counter);

            File.Move(source, temp);
            journal.Add(new UndoStep { Kind = UndoKind.MoveFile, From = temp, To = source });

            staged.Add((temp, Resolve(operation.TargetPath, redirects)));
        }

        foreach (var (temp, target) in staged)
        {
            File.Move(temp, target);
            journal.RemoveAll(s => s.Kind == UndoKind.MoveFile && s.From == temp);
            journal.Add(new UndoStep { Kind = UndoKind.MoveFile, From = target, To = OriginalOf(temp) });

            VerifyFileName(target);
            folderResult.OperationsCompleted++;
        }

        foreach (var operation in change.Operations)
        {
            switch (operation.Kind)
            {
                case OperationKind.RenameFile:
                case OperationKind.CreateDirectory:
                    break;

                case OperationKind.DeleteDirectory:
                    {
                        string source = Resolve(operation.SourcePath, redirects);

                        // The folder can fill up between planning and applying, and losing
                        // what someone put there is not recoverable.
                        if (Directory.EnumerateFileSystemEntries(source).Any())
                            throw new IOException($"\"{Path.GetFileName(source)}\" is no longer empty.");

                        Directory.Delete(source);
                        journal.Add(new UndoStep { Kind = UndoKind.CreateDirectory, From = source });

                        folderResult.OperationsCompleted++;
                        break;
                    }

                case OperationKind.PatchCue:
                    PatchCue(operation, redirects, journal);
                    folderResult.OperationsCompleted++;
                    break;

                case OperationKind.RenameDirectory:
                    {
                        string source = Resolve(operation.SourcePath, redirects);
                        string target = Resolve(operation.TargetPath, redirects);

                        MoveDirectoryThroughTemp(source, target, journal);
                        VerifyDirectoryName(target);

                        redirects[operation.SourcePath] = target;
                        folderResult.OperationsCompleted++;
                        break;
                    }

                case OperationKind.WriteConfig:
                    {
                        string target = operation.TargetPath;
                        byte[]? original = File.Exists(target) ? File.ReadAllBytes(target) : null;

                        File.WriteAllBytes(target, operation.FileBytes!);

                        journal.Add(original == null
                            ? new UndoStep { Kind = UndoKind.DeleteFile, From = target }
                            : new UndoStep { Kind = UndoKind.RestoreBytes, From = target, Bytes = original });

                        if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(operation.FileBytes))
                            throw new IOException("\"config.txt\" did not read back as written.");

                        folderResult.OperationsCompleted++;
                        break;
                    }
            }
        }
    }

    private static void PatchCue(
        PlannedOperation operation,
        Dictionary<string, string> redirects,
        List<UndoStep> journal)
    {
        string path = Resolve(operation.SourcePath, redirects);

        byte[] original = File.ReadAllBytes(path);
        var document = CueDocument.Parse(path, original);
        byte[] updated = document.Rewrite(operation.CueReplacements);

        if (updated.AsSpan().SequenceEqual(original))
            return;

        File.WriteAllBytes(path, updated);
        journal.Add(new UndoStep { Kind = UndoKind.RestoreBytes, From = path, Bytes = original });
    }

    private static void MoveDirectoryThroughTemp(string source, string target, List<UndoStep> journal)
    {
        if (string.Equals(source, target, StringComparison.Ordinal))
            return;

        int counter = 0;
        string temp = UniqueTempPath(source, ref counter);

        Directory.Move(source, temp);
        journal.Add(new UndoStep { Kind = UndoKind.MoveDirectory, From = temp, To = source });

        Directory.Move(temp, target);
        journal.RemoveAll(s => s.Kind == UndoKind.MoveDirectory && s.From == temp);
        journal.Add(new UndoStep { Kind = UndoKind.MoveDirectory, From = target, To = source });
    }

    private static string UniqueTempPath(string path, ref int counter)
    {
        string candidate;

        do
        {
            candidate = path + CardScanner.TempSuffix + counter;
            counter++;
        }
        while (File.Exists(candidate) || Directory.Exists(candidate));

        return candidate;
    }

    private static string OriginalOf(string tempPath)
    {
        int marker = tempPath.LastIndexOf(CardScanner.TempSuffix, StringComparison.Ordinal);
        return marker < 0 ? tempPath : tempPath.Substring(0, marker);
    }

    // Confirms the name landed on disk exactly as asked, letter case included.
    private static void VerifyFileName(string path)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        string expected = Path.GetFileName(path);

        foreach (string candidate in Directory.GetFiles(folder))
        {
            if (string.Equals(Path.GetFileName(candidate), expected, StringComparison.Ordinal))
                return;
        }

        throw new IOException($"\"{expected}\" did not land on disk with the requested spelling.");
    }

    private static void VerifyDirectoryName(string path)
    {
        string parent = Path.GetDirectoryName(path) ?? "";
        string expected = Path.GetFileName(path);

        foreach (string candidate in Directory.GetDirectories(parent))
        {
            if (string.Equals(Path.GetFileName(candidate), expected, StringComparison.Ordinal))
                return;
        }

        throw new IOException($"Folder \"{expected}\" did not land on disk with the requested spelling.");
    }

    private static bool TryRollback(List<UndoStep> journal)
    {
        bool complete = true;

        for (int i = journal.Count - 1; i >= 0; i--)
        {
            var step = journal[i];

            try
            {
                switch (step.Kind)
                {
                    case UndoKind.MoveFile:
                        if (File.Exists(step.From))
                            File.Move(step.From, step.To);
                        break;

                    case UndoKind.MoveDirectory:
                        if (Directory.Exists(step.From))
                            Directory.Move(step.From, step.To);
                        break;

                    case UndoKind.RestoreBytes:
                        File.WriteAllBytes(step.From, step.Bytes!);
                        break;

                    case UndoKind.DeleteDirectory:
                        if (Directory.Exists(step.From) &&
                            !Directory.EnumerateFileSystemEntries(step.From).Any())
                        {
                            Directory.Delete(step.From);
                        }

                        break;

                    case UndoKind.CreateDirectory:
                        Directory.CreateDirectory(step.From);
                        break;

                    case UndoKind.DeleteFile:
                        File.Delete(step.From);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }

        return complete;
    }

    // Rewrites a path whose ancestor has already been renamed in this run.
    private static string Resolve(string path, Dictionary<string, string> redirects)
    {
        if (redirects.Count == 0)
            return path;

        foreach (var pair in redirects)
        {
            if (string.Equals(path, pair.Key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;

            string prefix = pair.Key.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(pair.Value, path.Substring(prefix.Length));
        }

        return path;
    }
}
