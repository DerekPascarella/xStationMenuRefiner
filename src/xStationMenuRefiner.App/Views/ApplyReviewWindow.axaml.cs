using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using xStationMenuRefiner.Core.Changes;
using xStationMenuRefiner.Core;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views;

public sealed class ReviewRow : INotifyPropertyChanged
{
    public FolderChange Change { get; init; } = null!;
    public string Header { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public List<string> Operations { get; init; } = new();
    public bool HasProblem { get; init; }
    public string Problems { get; init; } = "";
    public bool HasWarning { get; init; }
    public string Warnings { get; init; } = "";

    public bool CanSelect => !HasProblem;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ResultRow
{
    public string Header { get; init; } = "";
    public string Detail { get; init; } = "";
}

public partial class ApplyReviewWindow : Window
{
    private readonly ChangePlan _plan;
    private List<ReviewRow> _rows = new();
    private ExecutionResult? _result;

    public ApplyReviewWindow()
    {
        _plan = new ChangePlan();
        InitializeComponent();
    }

    public ApplyReviewWindow(ChangePlan plan)
    {
        _plan = plan;
        InitializeComponent();
        BuildReview();
    }

    private void BuildReview()
    {
        _rows = _plan.Changes.Select(change => new ReviewRow
        {
            Change = change,
            Header = DescribeChange(change),
            FolderPath = change.FolderPath,
            Operations = change.Operations.Select(o => "    " + o.Summary).ToList(),
            HasProblem = !change.IsValid,
            Problems = string.Join("\n", change.Problems),
            HasWarning = change.Warnings.Count > 0,
            Warnings = string.Join("\n", change.Warnings),
            IsSelected = change.IsValid,
        }).ToList();

        foreach (var row in _rows)
            row.PropertyChanged += Row_PropertyChanged;

        ChangeList.ItemsSource = _rows;

        int blocked = _rows.Count(r => !r.CanSelect);

        SubHeaderText.Text = blocked == 0
            ? "Nothing is written to the card until you press Apply."
            : $"{PlatformUtil.Counted(blocked, "folder")} cannot be processed and will be skipped.";

        UpdateSelection();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        int folders = 0;
        int operations = 0;

        foreach (var row in _rows)
        {
            if (!row.IsSelected)
                continue;

            folders++;
            operations += row.Change.Operations.Count;
        }

        HeaderText.Text = $"{PlatformUtil.Counted(operations, "operation")} across {PlatformUtil.Counted(folders, "folder")}";

        ApplyButton.Content = $"Apply {PlatformUtil.Counted(operations, "Operation")}";
        ApplyButton.IsEnabled = folders > 0;
    }

    private void SelectAllButton_Click(object? sender, RoutedEventArgs e) => SetAllSelected(true);

    private void ClearAllButton_Click(object? sender, RoutedEventArgs e) => SetAllSelected(false);

    private void SetAllSelected(bool selected)
    {
        foreach (var row in _rows)
            row.IsSelected = selected && row.CanSelect;
    }

    private static string DescribeChange(FolderChange change) => change.Kind switch
    {
        PendingEditKind.GameLabel when string.Equals(change.OldLabel, change.NewLabel, StringComparison.Ordinal)
            => $"Conform to label: {change.NewLabel}",
        PendingEditKind.GameLabel => $"{change.OldLabel}   ->   {change.NewLabel}",
        PendingEditKind.FolderName => $"Folder: {change.OldLabel}   ->   {change.NewLabel}",
        PendingEditKind.TrackSuffixFix => $"Repair track names: {change.NewLabel}",
        PendingEditKind.CueReferenceFix => $"Repoint CUE references: {change.OldLabel}",
        _ => change.NewLabel,
    };

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(_result);

    private async void ApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_result != null)
        {
            Close(_result);
            return;
        }

        var selected = new ChangePlan();

        foreach (var row in _rows)
        {
            if (row.IsSelected)
                selected.Changes.Add(row.Change);
        }

        ReviewPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        SelectionButtons.IsVisible = false;
        ApplyButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        HeaderText.Text = "Writing changes";
        SubHeaderText.Text = "Leave the card connected until this finishes.";

        var result = await Task.Run(() => ChangeExecutor.Execute(selected, ReportProgress));

        _result = result;
        ShowResult(result);
    }

    private void ReportProgress(string label, int index, int total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressLabel.Text = label;
            ProgressBarControl.Value = total == 0 ? 0 : index * 100.0 / total;
            ProgressCount.Text = $"{index} of {total}";
        });
    }

    private void ShowResult(ExecutionResult result)
    {
        ProgressPanel.IsVisible = false;
        ResultPanel.IsVisible = true;

        HeaderText.Text = result.Failed == 0 && result.Skipped == 0
            ? "All changes applied"
            : "Finished with problems";

        SubHeaderText.Text =
            $"{PlatformUtil.Counted(result.Succeeded, "folder")} processed, {result.Failed} failed, {result.Skipped} skipped, " +
            $"{PlatformUtil.Counted(result.OperationsCompleted, "operation")} written.";

        var failures = result.Folders
            .Where(f => !f.Success)
            .Select(f => new ResultRow
            {
                Header = f.Label,
                Detail = BuildFailureDetail(f),
            })
            .ToList();

        ResultHeader.Text = failures.Count == 0
            ? "Every folder was processed."
            : $"{PlatformUtil.Counted(failures.Count, "folder")} need attention.";

        ResultList.ItemsSource = failures;

        CancelButton.IsEnabled = true;
        CancelButton.Content = "Close";
        ApplyButton.IsVisible = false;
    }

    private static string BuildFailureDetail(FolderExecutionResult folder)
    {
        string reason = folder.Error ?? "Unknown failure.";

        if (folder.Skipped)
            return "Skipped. " + reason;

        return folder.RolledBack
            ? reason + " The folder was put back the way it was."
            : reason + " Some operations could not be undone, so check this folder by hand.";
    }
}
