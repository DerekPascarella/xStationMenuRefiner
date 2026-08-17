using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using xStationMenuRefiner.Core.Changes;
using xStationMenuRefiner.Core.Model;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views;

public sealed class FolderChoice
{
    public string Display { get; init; } = "";
    public MenuFolderNode Node { get; init; } = null!;
}

public partial class FolderPickerWindow : Window
{
    public FolderPickerWindow()
    {
        InitializeComponent();
    }

    public FolderPickerWindow(CardScanResult scan, CardNode moving, string movingLabel)
    {
        InitializeComponent();

        PromptText.Text = $"Where should \"{movingLabel}\" go?";

        var choices = new List<FolderChoice>();

        // Asking the planner which destinations it would accept keeps this list and the
        // rules that govern the move from drifting apart.
        foreach (var folder in Candidates(scan))
        {
            if (!ChangePlanner.PlanMove(moving, folder).IsValid)
                continue;

            choices.Add(new FolderChoice { Display = Describe(folder, scan.RootPath), Node = folder });
        }

        FolderList.ItemsSource = choices;

        if (choices.Count == 0)
        {
            EmptyText.IsVisible = true;
            FolderList.IsVisible = false;
        }
    }

    private static IEnumerable<MenuFolderNode> Candidates(CardScanResult scan) =>
        new[] { scan.Root }
            .Concat(scan.Folders.OrderBy(f => f.FullPath, System.StringComparer.OrdinalIgnoreCase));

    private static string Describe(MenuFolderNode folder, string rootPath)
    {
        if (folder.IsRoot)
            return "(card root)";

        string relative = folder.FullPath.Length > rootPath.Length
            ? folder.FullPath.Substring(rootPath.Length).Trim(Path.DirectorySeparatorChar)
            : folder.Name;

        int depth = relative.Count(c => c == Path.DirectorySeparatorChar);

        return new string(' ', depth * 4) + folder.Name;
    }

    private void FolderList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        MoveButton.IsEnabled = FolderList.SelectedItem is FolderChoice;

    private void FolderList_DoubleTapped(object? sender, TappedEventArgs e) => Accept();

    private void MoveButton_Click(object? sender, RoutedEventArgs e) => Accept();

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        if (FolderList.SelectedItem is FolderChoice choice)
            Close(choice.Node);
    }
}
