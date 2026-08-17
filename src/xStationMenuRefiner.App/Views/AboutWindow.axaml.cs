using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using xStationMenuRefiner.Core;
using xStationMenuRefiner.Core.Naming;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionRun.Text = "v" + Constants.Version;
        FormatsText.Text = $"Disc image formats: {LabelRules.SupportedFormatsDisplay}";
    }

    private void LinkText_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        PlatformUtil.OpenUrl(Constants.AppUrl);

    private async void CheckForUpdatesButton_Click(object? sender, RoutedEventArgs e)
    {
        var button = (Button)sender!;
        button.IsEnabled = false;
        button.Content = "Checking...";

        var result = await UpdateManager.CheckForUpdateAsync();

        // Whatever comes next parents to the main window, so About steps out of
        // the way first.
        var main = Owner as MainWindow;
        Close();

        if (main != null)
            await main.ShowUpdateResultAsync(result, silent: false);
    }
}
