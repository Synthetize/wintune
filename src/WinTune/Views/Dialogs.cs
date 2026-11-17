using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WinTune.Views;

/// <summary>Confirmation and warning windows in the style of the application.</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
        };

        SetOwner(box);

        Wpf.Ui.Controls.MessageBoxResult result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public static async Task InfoAsync(string title, string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };

        SetOwner(box);

        await box.ShowDialogAsync();
    }

    /// <summary>Asks for a text. Returns null if the user cancels.</summary>
    public static async Task<string?> PromptAsync(string title, string message, string initialText, string confirmText = "Save")
    {
        var input = new TextBox
        {
            Text = initialText,
            MinWidth = 380,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
        };

        SetOwner(box);

        Wpf.Ui.Controls.MessageBoxResult result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary ? input.Text.Trim() : null;
    }

    private static void SetOwner(Window box)
    {
        Window? main = Application.Current?.MainWindow;
        if (main != null && main.IsLoaded && !ReferenceEquals(main, box))
        {
            box.Owner = main;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }
}
