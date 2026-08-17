using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using xStationMenuRefiner.Core.Model;
using xStationMenuRefiner.Core;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views;

public sealed class IssueEntryRow
{
    public string Message { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsError { get; init; }
    public bool IsWarning => !IsError;
    public EntryIssue Issue { get; init; } = null!;
}

public sealed class IssueGroupRow
{
    public string Title { get; init; } = "";
    public List<IssueEntryRow> Items { get; init; } = new();
}

public partial class IssuesWindow : Window
{
    private readonly CardScanResult? _scan;
    private readonly MenuMode _mode;

    public IssuesWindow()
    {
        InitializeComponent();
    }

    public IssuesWindow(CardScanResult scan, MenuMode mode)
    {
        _scan = scan;
        _mode = mode;
        InitializeComponent();
        Rebuild();
    }

    private void Rebuild()
    {
        if (_scan == null)
            return;

        bool errorsOnly = ErrorsOnly.IsChecked == true;

        // Some problems only exist in one of the two menus, so the list follows whichever
        // one is being previewed.
        var scoped = _scan.IssuesFor(_mode).ToList();

        var groups = scoped
            .Where(i => !errorsOnly || i.Severity == IssueSeverity.Error)
            .GroupBy(i => i.Kind)
            .OrderByDescending(g => g.Any(i => i.Severity == IssueSeverity.Error))
            .ThenBy(g => Describe(g.Key))
            .Select(g => new IssueGroupRow
            {
                Title = $"{Describe(g.Key)}  ({g.Count()})",
                Items = g.Select(i => new IssueEntryRow
                {
                    Message = i.Message,
                    Path = i.Path,
                    IsError = i.Severity == IssueSeverity.Error,
                    Issue = i,
                }).ToList(),
            })
            .ToList();

        GroupList.ItemsSource = groups;

        int errors = scoped.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = scoped.Count - errors;

        HeaderText.Text = $"{PlatformUtil.Counted(errors, "error")} and {PlatformUtil.Counted(warnings, "warning")}";
        SubHeaderText.Text = "Errors change what the menu shows or stop a game loading. Warnings are worth a look.";
    }

    private static string Describe(IssueKind kind) => kind switch
    {
        IssueKind.MissingCue => "Missing control file",
        IssueKind.MultipleCues => "More than one CUE sheet",
        IssueKind.UnresolvedCueReference => "CUE points at a missing file",
        IssueKind.CaseOnlyCueMismatch => "CUE and file disagree on letter case",
        IssueKind.OrphanImage => "Disc image no CUE references",
        IssueKind.NonConformingTrackSuffix => "Track number xStation will not recognize",
        IssueKind.SplitMenuEntries => "Folder shows up as several menu entries",
        IssueKind.DuplicateLabel => "Two folders produce the same menu entry",
        IssueKind.LabelTooLong => "Label longer than the menu can show",
        IssueKind.PathTooLong => "Path longer than the card supports",
        IssueKind.MacMetadata => "macOS metadata that breaks the game list scan",
        IssueKind.LeftoverTempName => "Leftover temporary file",
        IssueKind.InvalidName => "Name the filesystem cannot hold",
        IssueKind.UnsupportedSectorSize => "2048-byte-sector image xStation cannot read",
        IssueKind.MultiTrackCcd => "CloneCD tracks xStation will not play",
        _ => "Other",
    };

    private void ErrorsOnly_Changed(object? sender, RoutedEventArgs e) => Rebuild();

    private void Show_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IssueEntryRow row })
            Close(row.Issue);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
