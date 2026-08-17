using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MsBox.Avalonia.Enums;
using xStationMenuRefiner.App.Views;
using xStationMenuRefiner.App.Views.Shared;
using xStationMenuRefiner.Core;
using xStationMenuRefiner.Core.Changes;
using xStationMenuRefiner.Core.Model;
using xStationMenuRefiner.Core.Naming;
using xStationMenuRefiner.Core.Scanning;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App;

public sealed class TrackRow
{
    public string Number { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Size { get; init; } = "";
}

public sealed class IssueRow
{
    public string Message { get; init; } = "";
    public bool CanAutoFix { get; init; }
    public EntryIssue Issue { get; init; } = null!;
}

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<TreeNodeVm> _roots = new();
    private readonly List<PendingEdit> _structuralEdits = new();
    private readonly Dictionary<string, string> _volumePaths = new(StringComparer.Ordinal);

    private CardScanResult? _scan;

    private MenuMode _mode = MenuMode.Flat;
    private CardConfig _detected = new();
    private bool _suppressModeEvent;
    private string _cardPath = "";
    private TreeNodeVm? _selected;
    private bool _suppressCardChange;

    // A folder reached through Browse or given on the command line. Nothing detects
    // it as a volume, so the dropdown needs it spelled out. Lasts for this run only.
    private string _browsedPath = "";

    public MainWindow()
    {
        InitializeComponent();

        UpdateManager.CleanupStaleStagingData();

        OptionRenameFolder.IsChecked = _settings.RenameFolderWithLabel;
        OptionPad.IsChecked = _settings.PadTrackNumbers;
        OptionCheckUpdates.IsChecked = _settings.CheckForUpdatesOnStart;

        Tree.ItemsSource = _roots;

        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
    }

    private PlanOptions Options => new()
    {
        RenameFolder = OptionRenameFolder.IsChecked,
        PadTrackNumbers = OptionPad.IsChecked,
    };

    // Restores the size and position from the last run, provided the saved spot still
    // lands on a monitor that is currently attached.
    private void RestorePlacement()
    {
        if (_settings.WindowWidth >= MinWidth)
            Width = _settings.WindowWidth;

        if (_settings.WindowHeight >= MinHeight)
            Height = _settings.WindowHeight;

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            var saved = new PixelPoint((int)_settings.WindowLeft, (int)_settings.WindowTop);

            if (IsOnAttachedScreen(saved))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = saved;
            }
        }

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private bool IsOnAttachedScreen(PixelPoint point)
    {
        foreach (var screen in Screens.All)
        {
            // Leaves room for the title bar so the window stays draggable.
            var usable = screen.WorkingArea;

            if (point.X >= usable.X - 32 && point.X <= usable.Right - 96 &&
                point.Y >= usable.Y - 8 && point.Y <= usable.Bottom - 48)
            {
                return true;
            }
        }

        return false;
    }

    private void SavePlacement()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
            _settings.WindowLeft = Position.X;
            _settings.WindowTop = Position.Y;
        }
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        RestorePlacement();

        if (!string.IsNullOrEmpty(Program.StartupPath))
            _browsedPath = Program.StartupPath;

        RefreshCardList();
        UpdateStatus();

        if (_settings.CheckForUpdatesOnStart)
            await CheckForUpdatesAsync(silent: true);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        SaveSettings();
    }

    // The update dialogs write SkippedUpdateVersion straight to disk, so that one
    // field is re-read before this instance overwrites the file.
    private void SaveSettings()
    {
        _settings.SkippedUpdateVersion = AppSettings.Load().SkippedUpdateVersion;
        _settings.Save();
    }

    // Called from the update wizard, which hands off to an updater that exits the
    // process without Closing ever running.
    public void PersistPlacement()
    {
        SavePlacement();
        SaveSettings();
    }

    // ---------------------------------------------------------------- card selection

    // "F:/" and "F:\" name the same volume, so comparisons run on a canonical form.
    private static string CanonicalPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(CanonicalPath(a), CanonicalPath(b), StringComparison.OrdinalIgnoreCase);

    // Pass the path of a card that should stay selected if it is still connected.
    private void RefreshCardList(string? keep = null)
    {
        _suppressCardChange = true;

        _volumePaths.Clear();
        var entries = new List<string>();

        foreach (var volume in RemovableDrives.Enumerate())
        {
            if (_volumePaths.ContainsKey(volume.Display))
                continue;

            entries.Add(volume.Display);
            _volumePaths[volume.Display] = volume.Path;
        }

        // The browsed folder only earns its own row when no drive already covers it,
        // so a mounted card keeps its volume label rather than showing as a bare path.
        if (!string.IsNullOrEmpty(_browsedPath) &&
            Directory.Exists(_browsedPath) &&
            !_volumePaths.Values.Any(p => SamePath(p, _browsedPath)))
        {
            entries.Add(_browsedPath);
            _volumePaths[_browsedPath] = _browsedPath;
        }

        // This also runs while the dropdown is open, where reassigning the source would
        // close it.
        if (CardCombo.ItemsSource is not IEnumerable<string> shown ||
            !shown.SequenceEqual(entries, StringComparer.Ordinal))
        {
            CardCombo.ItemsSource = entries;
        }

        // Enumerate already sorts cards ahead of everything else, so the first entry
        // carrying the system folder is the best candidate.
        string? preferred =
            entries.FirstOrDefault(e => SamePath(_volumePaths[e], keep ?? ""))
            ?? entries.FirstOrDefault(e => SamePath(_volumePaths[e], _browsedPath))
            ?? entries.FirstOrDefault(e => RemovableDrives.HasSystemFolder(_volumePaths[e]))
            ?? entries.FirstOrDefault();

        _suppressCardChange = false;

        if (preferred != null)
            CardCombo.SelectedItem = preferred;
    }

    private async void CardCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressCardChange || CardCombo.SelectedItem is not string key)
            return;

        if (!_volumePaths.TryGetValue(key, out string? path))
            return;

        if (string.Equals(path, _cardPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (!await ConfirmDiscardAsync())
        {
            _suppressCardChange = true;
            CardCombo.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            _suppressCardChange = false;
            return;
        }

        LoadCard(path);
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync())
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a folder as an xStation card",
            AllowMultiple = false,
        });

        if (folders.Count == 0)
            return;

        string? path = folders[0].TryGetLocalPath();

        if (string.IsNullOrEmpty(path))
            return;

        _browsedPath = path;

        // Refreshing selects the new row, and that selection is what loads the card.
        RefreshCardList();
    }

    private async void RescanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_cardPath) && !await ConfirmDiscardAsync())
            return;

        string target = _cardPath;

        RefreshCardList(target);

        // Only re-read when the refresh left the same card selected. Landing on a
        // different one has already loaded it.
        if (!string.IsNullOrEmpty(target) && SamePath(target, _cardPath) && Directory.Exists(target))
            LoadCard(target);
    }

    private void CardCombo_DropDownOpened(object? sender, EventArgs e) => RefreshCardList(_cardPath);

    private void LoadCard(string path)
    {
        if (!Directory.Exists(path))
        {
            _ = DialogBox.ShowAsync(this, "Information", $"\"{path}\" is not available.");
            return;
        }

        // Store the canonical form so a path typed as "F:/" matches the drive's "F:\".
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        _cardPath = path;
        _structuralEdits.Clear();

        _scan = CardScanner.Scan(path);

        _detected = CardConfig.Read(path);
        _mode = _detected.Mode;

        _suppressModeEvent = true;
        MenuModeBox.SelectedIndex = _mode == MenuMode.Browse ? 1 : 0;
        _suppressModeEvent = false;

        RebuildTree();
        SelectNode(null);
        UpdateStatus();

        Title = $"xStation Menu Refiner  ·  {path}";

        if (!RemovableDrives.HasSystemFolder(path))
        {
            _ = DialogBox.ShowAsync(this, "Warning",
                $"\"{path}\" has no \"{RemovableDrives.SystemFolderName}\" folder at its root, so it may not be an xStation card.\n\n" +
                "Check that this is the right drive before applying any changes.");
        }
    }

    // Switching only changes how the same scan is drawn, so there is nothing to reread.
    private async void MenuModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressModeEvent || _scan == null)
            return;

        var chosen = MenuModeBox.SelectedIndex == 1 ? MenuMode.Browse : MenuMode.Flat;

        if (chosen == _mode)
            return;

        _mode = chosen;
        RebuildTree();
        SelectNode(null);
        UpdateStatus();

        _structuralEdits.RemoveAll(edit => edit.Kind == PendingEditKind.SetFolderBrowsing);

        if (chosen != _detected.Mode && _scan != null)
        {
            bool browse = chosen == MenuMode.Browse;

            var answer = await DialogBox.ShowAsync(this, "Card Configuration",
                $"This card has Folder Browsing turned {(browse ? "off" : "on")}, so this view is " +
                "only a preview until the setting changes.\n\n" +
                $"Stage a change to the card's configuration file turning Folder Browsing {(browse ? "on" : "off")}? " +
                "It will be written when you apply changes.", ButtonEnum.YesNo);

            if (answer == ButtonResult.Yes)
            {
                _structuralEdits.Add(new PendingEdit
                {
                    Kind = PendingEditKind.SetFolderBrowsing,
                    Node = _scan.Root,
                    NewValue = browse ? "on" : "off",
                    OriginalValue = browse ? "off" : "on",
                });
            }
        }

        UpdateStatus();
    }

    private void RebuildTree()
    {
        // Every row is replaced here, so labels typed but not yet applied are carried
        // across. Rows built for the other view are left behind, because switching the
        // view draws different text entirely and has always started over.
        var edited = new Dictionary<CardNode, string>();

        foreach (var node in AllNodes())
        {
            if (node.IsModified && !node.IsSplitRow && node.Mode == _mode)
                edited[node.Model] = node.Label;
        }

        _roots.Clear();

        if (_scan == null)
            return;

        Tree.Classes.Set("flat", _mode == MenuMode.Flat);

        var layout = StagedLayout.Build(_scan.Root, _structuralEdits);

        // The flat menu is one card-wide list with no folders in it. Browse mode walks
        // the directories in their staged shape.
        var built = new List<TreeNodeVm>();

        if (_mode == MenuMode.Flat)
        {
            foreach (var game in _scan.Games)
                AddVms(game, null, built, layout, edited);
        }
        else
        {
            foreach (var child in layout.ChildrenOf(_scan.Root))
                AddVms(child, null, built, layout, edited);
        }

        foreach (var vm in SortRows(built))
            _roots.Add(vm);

        ApplyFilter();
    }

    // A folder whose image names disagree draws one row per name, so the tree does too.
    private void AddVms(CardNode model, TreeNodeVm? parent, IList<TreeNodeVm> into, StagedLayout layout,
        IReadOnlyDictionary<CardNode, string> edited)
    {
        if (model is GameNode split && split.MenuEntryCount > 1)
        {
            foreach (string text in SplitRowTexts(split))
                into.Add(new TreeNodeVm(split, _mode, text) { ParentVm = parent });

            return;
        }

        var vm = new TreeNodeVm(model, _mode)
        {
            ParentVm = parent,
            IsPreviewRow = model is MenuFolderNode mf && layout.IsSynthetic(mf),
        };

        if (edited.TryGetValue(model, out string? label))
            vm.Label = label;

        vm.IsStructurallyStaged = layout.IsStaged(model);

        if (model is MenuFolderNode folder)
        {
            var children = new List<TreeNodeVm>();

            foreach (var child in layout.ChildrenOf(folder))
                AddVms(child, vm, children, layout, edited);

            foreach (var child in SortRows(children))
                vm.Children.Add(child);

            vm.IsExpanded = vm.Children.Count <= 40;
        }

        into.Add(vm);
    }

    private IEnumerable<string> SplitRowTexts(GameNode game) =>
        game.Images
            .GroupBy(i => LabelRules.LabelFromFileName(i.FileName), StringComparer.OrdinalIgnoreCase)
            .Select(g => _mode == MenuMode.Browse ? g.First().FileName : g.Key);

    // Folders before games, each group alphabetical, which is the order the console draws.
    private static IEnumerable<TreeNodeVm> SortRows(IEnumerable<TreeNodeVm> rows) =>
        rows.OrderBy(r => r.IsFolder ? 0 : 1)
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<TreeNodeVm> AllNodes()
    {
        var stack = new Stack<TreeNodeVm>(_roots);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            foreach (var child in node.Children)
                stack.Push(child);
        }
    }

    // ---------------------------------------------------------------- selection and details

    private void Tree_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SelectNode(Tree.SelectedItem as TreeNodeVm);

    private void SelectNode(TreeNodeVm? node)
    {
        _selected = node;

        EmptyDetail.IsVisible = node == null;
        DetailPanel.IsVisible = node != null;

        if (node == null)
        {
            UpdateEditButtons();
            return;
        }

        LabelEditor.Text = node.Label;
        UpdateEditButtons();

        if (node.Game is { } game)
        {
            LabelHint.Text = game.MenuEntryCount > 1
                ? $"This folder shows as {game.MenuEntryCount} entries. Renaming it puts every image under one name and fixes that."
                : "Taken from the first data track's file name.";

            FolderNameText.Text = game.FolderName.Length > 0 ? game.FolderName : "(card root)";
            SheetNameText.Text = game.CueFileName
                                 ?? (game.Sidecars.Count > 0 ? Path.GetFileName(game.Sidecars[0]) : "none");
            FormatText.Text = game.FormatDisplay;

            TracksHeader.IsVisible = true;
            TrackList.ItemsSource = game.Images
                .Select(i => new TrackRow
                {
                    Number = i.TrackNumber?.ToString("00")
                             ?? (i.CueTracks.Count > 0 ? i.CueTracks[0].ToString("00") : "--"),
                    FileName = i.FileName,
                    Size = PlatformUtil.FormatSize(i.Size),
                })
                .ToList();
        }
        else
        {
            LabelHint.Text = "Folder name, shown by xStation when browsing.";
            FolderNameText.Text = node.Model.Name;
            SheetNameText.Text = "--";
            FormatText.Text = "Menu folder";

            TracksHeader.IsVisible = false;
            TrackList.ItemsSource = null;
        }

        var issues = node.Model.Issues
            .Where(i => i.AppliesIn(_mode))
            .GroupBy(i => i.Message, StringComparer.Ordinal)
            .Select(g => new IssueRow
            {
                Message = g.Key,
                CanAutoFix = g.Any(i => i.CanAutoFix),
                Issue = g.First(),
            })
            .ToList();

        IssuesHeader.IsVisible = issues.Count > 0;
        IssueList.ItemsSource = issues;
    }

    // Expands the node's ancestors so a collapsed branch does not hide it, then
    // highlights it in the tree and shows its details. Passing null just clears
    // the detail panel, matching SelectNode(null).
    private void SelectAndReveal(TreeNodeVm? target)
    {
        if (target != null)
        {
            for (var parent = target.ParentVm; parent != null; parent = parent.ParentVm)
                parent.IsExpanded = true;

            Tree.SelectedItem = target;
        }

        SelectNode(target);
    }

    // ---------------------------------------------------------------- editing

    private void Row_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: TreeNodeVm node })
            return;

        node.IsEditing = true;
        e.Handled = true;
    }

    private void EditBox_Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void EditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TreeNodeVm node })
            return;

        if (e.Key == Key.Enter)
        {
            node.IsEditing = false;
            CommitEdit(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.ResetLabel();
            node.IsEditing = false;
            CommitEdit(node);
            e.Handled = true;
        }
    }

    private void EditBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: TreeNodeVm node })
        {
            node.IsEditing = false;
            CommitEdit(node);
        }
    }

    private void CommitEdit(TreeNodeVm node)
    {
        string trimmed = node.Label.Trim();

        if (!string.Equals(trimmed, node.Label, StringComparison.Ordinal))
            node.Label = trimmed;

        if (trimmed.Length == 0)
            node.ResetLabel();

        if (ReferenceEquals(node, _selected))
            LabelEditor.Text = node.Label;

        UpdateStatus();
    }

    private void LabelEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        StageButton_Click(sender, e);
        e.Handled = true;
    }

    private void LabelEditor_TextChanged(object? sender, TextChangedEventArgs e) => UpdateEditButtons();

    // Both buttons stay disabled while clicking them would change nothing.
    private void UpdateEditButtons()
    {
        if (_selected == null)
        {
            StageButton.IsEnabled = false;
            RevertButton.IsEnabled = false;
            return;
        }

        string value = (LabelEditor.Text ?? "").Trim();
        bool differs = !string.Equals(value, _selected.Label, StringComparison.Ordinal);

        StageButton.IsEnabled = value.Length > 0 && differs;
        RevertButton.IsEnabled = differs || _selected.IsModified ||
            _structuralEdits.Any(edit => ReferenceEquals(edit.Node, _selected.Model));
    }

    private void StageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        string value = (LabelEditor.Text ?? "").Trim();

        if (value.Length == 0)
        {
            _ = DialogBox.ShowAsync(this, "Information", "A menu label cannot be empty.");
            return;
        }

        string? problem = PathSafety.Validate(value);

        if (problem != null)
        {
            _ = DialogBox.ShowAsync(this, "Information", problem);
            return;
        }

        _selected.Label = value;
        UpdateStatus();
    }

    private void RevertButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        var model = _selected.Model;

        _selected.ResetLabel();
        _structuralEdits.RemoveAll(edit => ReferenceEquals(edit.Node, model));
        RebuildTree();

        // The rebuild threw away the row that was selected, so the same node is picked
        // up from the rows that replaced it.
        SelectAndReveal(AllNodes().FirstOrDefault(vm => ReferenceEquals(vm.Model, model)));
        UpdateStatus();
    }

    // ---------------------------------------------------------------- filtering

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string filter = (SearchBox.Text ?? "").Trim();

        foreach (var root in _roots)
            root.ApplyFilter(filter);
    }

    private void ExpandAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var root in _roots)
            root.SetExpandedDeep(true);
    }

    private void CollapseAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var root in _roots)
            root.SetExpandedDeep(false);
    }

    // ---------------------------------------------------------------- options

    private void OptionRenameFolder_Click(object? sender, RoutedEventArgs e)
    {
        _settings.RenameFolderWithLabel = OptionRenameFolder.IsChecked;
        _settings.Save();
    }

    private void OptionPad_Click(object? sender, RoutedEventArgs e)
    {
        _settings.PadTrackNumbers = OptionPad.IsChecked;
        _settings.Save();
    }

    private void OptionCheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdatesOnStart = OptionCheckUpdates.IsChecked;
        _settings.Save();
    }

    // ---------------------------------------------------------------- repairs

    private async void RepairAllIssues_Click(object? sender, RoutedEventArgs e)
    {
        int queued = QueueRepairs(AllNodes());

        UpdateStatus();

        await DialogBox.ShowAsync(this, "Information", queued == 0
            ? "Nothing on this card needs repair."
            : $"{PlatformUtil.Counted(queued, "folder")} queued for repair. Use Apply Changes to review them.");
    }

    private int QueueRepairs(IEnumerable<TreeNodeVm> nodes)
    {
        int queued = 0;

        foreach (var node in nodes)
        {
            if (node.Game is not { } game)
                continue;

            // A repair for a problem the current menu does not have would change nothing
            // the user can see.
            foreach (var issue in game.Issues.Where(i => i.CanAutoFix && i.AppliesIn(_mode)))
            {
                var kind = issue.Kind switch
                {
                    IssueKind.CaseOnlyCueMismatch => PendingEditKind.CueReferenceFix,
                    IssueKind.LabelTooLong => PendingEditKind.ShortenMenuEntry,
                    _ => PendingEditKind.TrackSuffixFix,
                };

                if (AlreadyQueued(game, kind))
                    continue;

                _structuralEdits.Add(new PendingEdit
                {
                    Kind = kind,
                    Node = game,
                    OriginalValue = game.Label,
                    NewValue = game.Label,
                });

                queued++;
            }
        }

        return queued;
    }

    // ---------------------------------------------------------------- organizing

    private static TreeNodeVm? RowOf(object? sender) =>
        (sender as Control)?.DataContext as TreeNodeVm;

    private async void NewFolderAtRoot_Click(object? sender, RoutedEventArgs e)
    {
        if (_scan != null)
            await CreateFolderInAsync(_scan.Root);
    }

    private async void NewFolderInside_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Folder is { } folder)
            await CreateFolderInAsync(folder);
    }

    // Making and removing an empty folder writes nothing a later step depends on, so both
    // happen at once and the tree can be used as a destination straight away.
    private async Task CreateFolderInAsync(MenuFolderNode parent)
    {
        string where = parent.IsRoot
            ? "Name for the new folder at the card root."
            : $"Name for the new folder inside \"{parent.Name}\".";

        string? name = await new TextPromptWindow("New Folder", where, "New Folder")
            .ShowDialog<string?>(this);

        if (string.IsNullOrEmpty(name))
            return;

        var change = ChangePlanner.PlanCreateFolder(parent, name);

        if (!change.IsValid)
        {
            await DialogBox.ShowAsync(this, "Cannot create that folder", string.Join(" ", change.Problems));
            return;
        }

        var plan = new ChangePlan();
        plan.Changes.Add(change);

        var result = ChangeExecutor.Execute(plan);

        if (result.Failed > 0)
        {
            await DialogBox.ShowAsync(this, "Error", "The folder could not be created.");
            return;
        }

        LoadCard(_cardPath);
    }

    private async void DeleteFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Folder is not { } folder)
            return;

        var change = ChangePlanner.PlanDeleteFolder(folder);

        if (!change.IsValid)
        {
            await DialogBox.ShowAsync(this, "Cannot delete that folder", string.Join(" ", change.Problems));
            return;
        }

        var answer = await DialogBox.ShowAsync(this, "Delete folder",
            $"Delete the empty folder \"{folder.Name}\"?\n\n" +
            "This one happens straight away rather than waiting for Apply Changes, because it " +
            "only removes an empty folder. Making a folder of the same name puts it back.",
            ButtonEnum.YesNo);

        if (answer != ButtonResult.Yes)
            return;

        var plan = new ChangePlan();
        plan.Changes.Add(change);

        var result = ChangeExecutor.Execute(plan);

        if (result.Failed > 0)
        {
            await DialogBox.ShowAsync(this, "Error",
                "The folder could not be deleted. Something may have been added to it.");
        }

        LoadCard(_cardPath);
    }

    private async void MoveEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || _scan == null)
            return;

        var destination = await new FolderPickerWindow(_scan, row.Model, row.Label)
            .ShowDialog<MenuFolderNode?>(this);

        if (destination != null)
            StageStructural(PendingEditKind.MoveEntry, row, destination);
    }

    private void WrapEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && row.CanWrap)
            StageStructural(PendingEditKind.WrapInFolder, row);
    }

    private void UnwrapEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && row.CanUnwrap)
            StageStructural(PendingEditKind.UnwrapFolder, row);
    }

    // Moving anything carries game files with it, so it waits for review like a rename.
    private void StageStructural(
        PendingEditKind kind, TreeNodeVm row, MenuFolderNode? destination = null)
    {
        static bool Structural(PendingEditKind k) =>
            k is PendingEditKind.MoveEntry
              or PendingEditKind.WrapInFolder
              or PendingEditKind.UnwrapFolder;

        _structuralEdits.RemoveAll(edit => ReferenceEquals(edit.Node, row.Model) &&
            (edit.Kind == kind || (Structural(edit.Kind) && Structural(kind))));

        // Unwrapping takes the game's folder away with it, so anything staged to move
        // into that folder would have nowhere left to sit.
        if (kind == PendingEditKind.UnwrapFolder && row.Model.Parent is { } removed)
        {
            _structuralEdits.RemoveAll(edit => edit.Kind == PendingEditKind.MoveEntry &&
                ReferenceEquals(edit.Destination, removed));
        }

        _structuralEdits.Add(new PendingEdit
        {
            Kind = kind,
            Node = row.Model,
            Destination = destination,
            OriginalValue = row.Label,
            NewValue = destination?.Name ?? row.Label,
        });

        RebuildTree();
        SelectAndReveal(AllNodes().FirstOrDefault(vm => ReferenceEquals(vm.Model, row.Model)));

        UpdateStatus();
    }

    // ---------------------------------------------------------------- dragging

    private TreeNodeVm? _dragCandidate;
    private Point _dragOrigin;

    private void Row_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var row = RowOf(sender);

        _dragCandidate = row is { CanMove: true } ? row : null;
        _dragOrigin = e.GetPosition(this);
    }

    private async void Row_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is not { } row || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }

        var moved = e.GetPosition(this) - _dragOrigin;

        // A few pixels of travel separates a drag from a click that wandered.
        if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6)
            return;

        _dragCandidate = null;

        var data = new DataObject();
        data.Set(DragRowFormat, row);

        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private const string DragRowFormat = "xsmc-row";

    private void Row_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanDropOn(sender, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Row_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!CanDropOn(sender, e))
            return;

        if (e.Data.Get(DragRowFormat) is TreeNodeVm row && RowOf(sender)?.Folder is { } folder)
            StageStructural(PendingEditKind.MoveEntry, row, folder);
    }

    // Dropping is offered only where the planner would accept the move, so the tree never
    // invites something it would then refuse.
    private bool CanDropOn(object? sender, DragEventArgs e)
    {
        if (e.Data.Get(DragRowFormat) is not TreeNodeVm row)
            return false;

        // A wrap preview has no directory behind it, so the planner would look at an
        // empty path and accept a drop into a folder that does not exist yet.
        if (RowOf(sender) is not { IsPreviewRow: false, Folder: { } folder })
            return false;

        return ChangePlanner.PlanMove(row.Model, folder).IsValid;
    }

    private bool AlreadyQueued(CardNode node, PendingEditKind kind) =>
        _structuralEdits.Any(edit => edit.Kind == kind && ReferenceEquals(edit.Node, node));

    private async void RepairIssue_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;

        int queued = QueueRepairs(new[] { _selected });
        UpdateStatus();

        await DialogBox.ShowAsync(this, "Information", queued == 0
            ? "That repair is already queued."
            : "Repair queued. Use Apply Changes to review it.");
    }

    // ---------------------------------------------------------------- apply

    private List<PendingEdit> CollectEdits()
    {
        var edits = new List<PendingEdit>();

        foreach (var node in AllNodes())
        {
            if (!node.IsModified)
                continue;

            edits.Add(new PendingEdit
            {
                Kind = node.Game != null ? PendingEditKind.GameLabel : PendingEditKind.FolderName,
                Node = node.Model,
                NewValue = node.Label,
                OriginalValue = node.OriginalLabel,
            });
        }

        edits.AddRange(_structuralEdits);
        return edits;
    }

    private int PendingCount() => AllNodes().Count(n => n.IsModified) + _structuralEdits.Count;

    // Read by the update wizard, which discards these if it installs.
    public int PendingChangeCount => PendingCount();

    private async void ApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        var edits = CollectEdits();

        if (edits.Count == 0)
            return;

        var plan = ChangePlanner.Build(edits, Options);

        if (plan.OperationCount == 0 && !plan.HasProblems)
        {
            await DialogBox.ShowAsync(this, "Information", "Nothing would change on the card.");
            return;
        }

        var result = await new ApplyReviewWindow(plan).ShowDialog<ExecutionResult?>(this);

        if (result == null)
            return;

        _structuralEdits.Clear();
        LoadCard(_cardPath);

        if (plan.Changes.Any(c => c.Kind != PendingEditKind.SetFolderBrowsing && c.IsValid))
            await RemindToRefreshAsync(result);
    }

    // The card keeps its own game list and only rebuilds it on demand, so without this
    // the menu still shows the old names and the card looks untouched.
    private async Task RemindToRefreshAsync(ExecutionResult? result)
    {
        if (result is not { Failed: 0, Succeeded: > 0 })
            return;

        await DialogBox.ShowAsync(this, "One more step",
            "The changes are on the card.\n\n" +
            "On the console, open the xStation options menu and choose \"Refresh Game List\". " +
            "The menu keeps showing the old names until you do.");
    }

    private void ShortenEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { CanShorten: true } row)
            StageStructural(PendingEditKind.ShortenMenuEntry, row);
    }

    private async void DiscardButton_Click(object? sender, RoutedEventArgs e)
    {
        int count = PendingCount();

        if (count == 0)
            return;

        var answer = await DialogBox.ShowAsync(this, "Discard Changes",
            $"Throw away {PlatformUtil.Counted(count, "pending change")}?", ButtonEnum.YesNo);

        if (answer != ButtonResult.Yes)
            return;

        var model = _selected?.Model;

        foreach (var node in AllNodes())
            node.ResetLabel();

        _structuralEdits.Clear();
        RebuildTree();

        if (model != null)
            SelectAndReveal(AllNodes().FirstOrDefault(vm => ReferenceEquals(vm.Model, model)));

        UpdateStatus();
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        int count = PendingCount();

        if (count == 0)
            return true;

        var answer = await DialogBox.ShowAsync(this, "Discard Changes",
            $"{PlatformUtil.Counted(count, "pending change")} not yet applied. Discard them?", ButtonEnum.YesNo);

        return answer == ButtonResult.Yes;
    }

    private void UpdateStatus()
    {
        UpdateEditButtons();

        if (_scan == null)
        {
            SummaryText.Text = "No card loaded.";
            PendingText.Text = "";
            IssuesButton.Content = "Issues";
            ApplyButton.IsEnabled = false;
            DiscardButton.IsEnabled = false;
            return;
        }

        // A clash between two folders only matters in the flat menu, so the counts follow
        // whichever menu is being previewed.
        var visible = _scan.IssuesFor(_mode).ToList();
        int errors = visible.Count(i => i.Severity == IssueSeverity.Error);
        int warnings = visible.Count - errors;

        SummaryText.Text = _mode == MenuMode.Browse
            ? $"{PlatformUtil.Counted(_scan.Games.Count, "game")} in {PlatformUtil.Counted(_scan.Folders.Count, "folder")}"
            : PlatformUtil.Counted(_scan.Games.Count, "game");

        IssuesButton.Content = visible.Count == 0
            ? "No Issues"
            : $"Issues: {PlatformUtil.Counted(errors, "error")}, {PlatformUtil.Counted(warnings, "warning")}";

        int pending = PendingCount();
        PendingText.Text = pending == 0 ? "" : $"{PlatformUtil.Counted(pending, "pending change")}";

        ApplyButton.IsEnabled = pending > 0;
        DiscardButton.IsEnabled = pending > 0;
    }

    // ---------------------------------------------------------------- windows

    private async void IssuesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_scan == null || !_scan.IssuesFor(_mode).Any())
        {
            await DialogBox.ShowAsync(this, "Information", "Nothing on this card needs attention.");
            return;
        }

        var chosen = await new IssuesWindow(_scan, _mode).ShowDialog<EntryIssue?>(this);

        if (chosen?.Node == null)
            return;

        var target = AllNodes().FirstOrDefault(n => ReferenceEquals(n.Model, chosen.Node));

        if (target == null)
            return;

        SelectAndReveal(target);
    }

    private void OpenCardFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_cardPath))
            PlatformUtil.OpenFolder(_cardPath);
    }

    private void About_Click(object? sender, RoutedEventArgs e) => _ = new AboutWindow().ShowDialog(this);

    private async Task CheckForUpdatesAsync(bool silent)
    {
        var result = await UpdateManager.CheckForUpdateAsync();
        await ShowUpdateResultAsync(result, silent);
    }

    // The About window runs its own check so its button can show progress, then
    // closes and hands the result back here, where the dialogs can parent to the
    // main window.
    public async Task ShowUpdateResultAsync(UpdateCheckResult result, bool silent)
    {
        if (result.CheckFailed)
        {
            if (!silent)
            {
                await DialogBox.ShowAsync(this, "Error",
                    "Could not check for updates.\n\nCheck your internet connection and try again.");
            }

            return;
        }

        if (result.ManualUpdateRequired)
        {
            if (silent && UpdateAvailableDialog.ShouldSkipVersion(result.LatestTag))
                return;

            await new ManualUpdateDialog(result.LatestTag, result.LatestVersion, result.ManualReason).ShowDialog(this);
            return;
        }

        if (!result.UpdateAvailable)
        {
            if (!silent)
                await DialogBox.ShowAsync(this, "Information", "This is the latest release.");

            return;
        }

        // An explicit check always reports, so the skip list applies only to the
        // check that runs on its own at startup.
        if (silent && UpdateAvailableDialog.ShouldSkipVersion(result.LatestTag))
            return;

        var dialog = new UpdateAvailableDialog(result.LatestTag, result.LatestVersion);
        await dialog.ShowDialog(this);

        if (dialog.UserWantsUpdate)
            await new UpdateWizardWindow(result.LatestTag, result.LatestVersion).ShowDialog(this);
    }
}
