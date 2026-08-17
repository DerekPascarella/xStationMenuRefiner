using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using xStationMenuRefiner.Core.Naming;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow()
    {
        InitializeComponent();
    }

    public TextPromptWindow(string title, string prompt, string initial = "")
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        Entry.Text = initial;

        Opened += (_, _) =>
        {
            Entry.Focus();
            Entry.SelectAll();
        };
    }

    private void Entry_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Accept();

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        string value = (Entry.Text ?? "").Trim();

        if (value.Length == 0)
        {
            Show("Enter a name.");
            return;
        }

        string? problem = PathSafety.Validate(value);

        if (problem != null)
        {
            Show(problem);
            return;
        }

        Close(value);
    }

    private void Show(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
