using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using xStationMenuRefiner.Core;
using xStationMenuRefiner.Core.Model;
using xStationMenuRefiner.Core.Naming;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App;

// One row in the tree. Holds the edited label until Apply, so typing never touches disk.
public sealed class TreeNodeVm : INotifyPropertyChanged
{
    public TreeNodeVm(CardNode model, MenuMode mode)
    {
        Model = model;
        Mode = mode;
        OriginalLabel = model.MenuTextFor(mode);
        _label = OriginalLabel;
    }

    // One of the several rows a folder draws when its image names disagree. The row is
    // real on the console but has no name of its own to edit, so it is read only and the
    // repair on the underlying entry is what fixes it.
    public TreeNodeVm(GameNode model, MenuMode mode, string splitText)
    {
        Model = model;
        Mode = mode;
        IsSplitRow = true;
        OriginalLabel = splitText;
        _label = splitText;
    }

    public CardNode Model { get; }
    public MenuMode Mode { get; }
    public bool IsSplitRow { get; }
    public TreeNodeVm? ParentVm { get; set; }
    public ObservableCollection<TreeNodeVm> Children { get; } = new();

    public string OriginalLabel { get; }

    public bool IsFolder => Model is MenuFolderNode;
    public bool IsGame => Model is GameNode;

    // A folder with a directory behind it, so it can be given a new folder or deleted.
    // A wrap preview has neither until the wrap is applied.
    public bool IsRealFolder => IsFolder && !IsPreviewRow;

    // A split row stands for part of a folder the console draws wrong, so it has no
    // structure of its own to reorganize.
    public bool CanMove => !IsSplitRow && !IsPreviewRow;
    public bool CanWrap => !IsSplitRow && !IsPreviewRow && Model is GameNode { OwnsFolder: false };
    public bool CanUnwrap => !IsSplitRow && !IsPreviewRow && Model is GameNode { OwnsFolder: true };

    // The flat menu drops the track marker on its own, so there is nothing to shorten
    // unless Folder Browsing is drawing the file name.
    public bool CanShorten =>
        !IsSplitRow &&
        !IsPreviewRow &&
        Mode == MenuMode.Browse &&
        Model is GameNode game &&
        game.Images.Count > 1 &&
        !string.Equals(
            game.MenuFileName,
            game.Label + System.IO.Path.GetExtension(game.MenuFileName),
            StringComparison.Ordinal);
    public GameNode? Game => Model as GameNode;
    public MenuFolderNode? Folder => Model as MenuFolderNode;

    private string _label;
    public string Label
    {
        get => _label;
        set
        {
            string trimmed = value ?? "";

            if (string.Equals(_label, trimmed, StringComparison.Ordinal))
                return;

            _label = trimmed;
            Raise();
            Raise(nameof(IsModified));
            Raise(nameof(ShowsPending));
            Raise(nameof(MenuPreview));
            Raise(nameof(IsTrimmed));
            Raise(nameof(TrimNote));
            Raise(nameof(RowTip));
            Raise(nameof(Detail));
        }
    }

    public bool IsModified =>
        !IsSplitRow && !string.Equals(_label, OriginalLabel, StringComparison.Ordinal);

    // True for a synthetic wrap folder: a row that exists only in the staged preview,
    // so it cannot be renamed, moved, or reorganized until it is real.
    public bool IsPreviewRow { get; init; }

    private bool _isStructurallyStaged;
    public bool IsStructurallyStaged
    {
        get => _isStructurallyStaged;
        set
        {
            if (_isStructurallyStaged == value)
                return;

            _isStructurallyStaged = value;
            Raise();
            Raise(nameof(ShowsPending));
        }
    }

    public bool ShowsPending => IsModified || IsStructurallyStaged;

    // What the firmware will draw for the text currently in the box.
    public string MenuPreview => LabelRules.MenuText(_label);

    public bool IsTrimmed => LabelRules.IsTrimmedByMenu(_label);

    public string TrimNote => IsTrimmed ? $"trimmed from {_label.Length}" : "";

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(); } }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { if (_isVisible != value) { _isVisible = value; Raise(); } }
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value || (value && (IsSplitRow || IsPreviewRow)))
                return;

            _isEditing = value;
            Raise();
            Raise(nameof(IsNotEditing));
        }
    }

    public bool IsNotEditing => !_isEditing;

    private IEnumerable<EntryIssue> ScopedIssues => Model.Issues.Where(i => i.AppliesIn(Mode));

    public bool HasError => ScopedIssues.Any(i => i.Severity == IssueSeverity.Error);

    public bool HasWarning => !HasError && ScopedIssues.Any();

    public string StatusTip => string.Join("\n", ScopedIssues.Select(i => i.Message).Distinct());

    public string Detail
    {
        get
        {
            if (Folder is { } folder)
            {
                int count = folder.GameCount;
                string games = count == 1 ? "1 game" : $"{count} games";
                return IsTrimmed ? $"{TrimNote}  ·  {games}" : games;
            }

            if (Game is not { } game)
                return "";

            if (IsSplitRow)
                return $"1 of {game.MenuEntryCount} rows from one folder";

            if (game.MenuEntryCount > 1)
                return $"shows as {game.MenuEntryCount} entries";

            string tracks = game.Images.Count == 1 ? "1 track" : $"{game.Images.Count} tracks";
            string body = $"{game.FormatDisplay}  ·  {tracks}  ·  {PlatformUtil.FormatSize(game.TotalSize)}";

            return IsTrimmed ? $"{TrimNote}  ·  {body}" : body;
        }
    }

    public string RowTip
    {
        get
        {
            // The row shows the trimmed text, so the tip carries the name in full.
            string full = IsTrimmed
                ? $"{_label}\n{_label.Length} characters, trimmed to {MenuPreview.Length}\n\n"
                : "";

            if (Folder != null)
                return full + Model.FullPath;

            if (Game is not { } game)
                return "";

            string source = game.MenuFileName;

            if (string.IsNullOrEmpty(source))
                return full + Model.FullPath;

            // With Folder Browsing on the row is that file name, so saying where it comes
            // from adds nothing.
            return Mode == MenuMode.Browse
                ? full + Model.FullPath
                : full + $"Menu label comes from {source}";
        }
    }

    public void ResetLabel() => Label = OriginalLabel;

    // Shows a row when it matches, and keeps a folder visible while anything under it
    // still matches.
    public bool ApplyFilter(string filter)
    {
        bool selfMatches = string.IsNullOrEmpty(filter) ||
                           Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           OriginalLabel.Contains(filter, StringComparison.OrdinalIgnoreCase);

        bool childMatches = false;

        foreach (var child in Children)
        {
            if (child.ApplyFilter(filter))
                childMatches = true;
        }

        IsVisible = selfMatches || childMatches;

        if (childMatches && !string.IsNullOrEmpty(filter))
            IsExpanded = true;

        return IsVisible;
    }

    public void SetExpandedDeep(bool expanded)
    {
        if (Children.Count > 0)
            IsExpanded = expanded;

        foreach (var child in Children)
            child.SetExpandedDeep(expanded);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
