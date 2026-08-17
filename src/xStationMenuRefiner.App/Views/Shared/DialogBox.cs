using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

// Written by Derek Pascarella (ateam)

namespace xStationMenuRefiner.App.Views.Shared;

// Standard message boxes share one width and center over the owning window.
public static class DialogBox
{
    private const double MaxDialogWidth = 460;

    public static async Task<ButtonResult> ShowAsync(
        Control? host,
        string title,
        string message,
        ButtonEnum buttons = ButtonEnum.Ok,
        Icon icon = Icon.None)
    {
        var owner = TopLevel.GetTopLevel(host) as Window;

        var box = MessageBoxManager.GetMessageBoxStandard(new MessageBoxStandardParams
        {
            ContentTitle = title,
            ContentMessage = message,
            ButtonDefinitions = buttons,
            Icon = icon,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            MaxWidth = MaxDialogWidth,
        });

        return owner != null
            ? await box.ShowWindowDialogAsync(owner)
            : await box.ShowAsync();
    }
}
