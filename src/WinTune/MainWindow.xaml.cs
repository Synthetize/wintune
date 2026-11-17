using WinTune.Core;
using WinTune.Views.Pages;

namespace WinTune;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <param name="openApps">Opens the Apps page instead of the Overview (click on the updates notification).</param>
    /// <param name="startMinimized">Starts minimized in the taskbar (start with Windows without the notification-area icon).</param>
    public MainWindow(bool openApps = false, bool startMinimized = false)
    {
        InitializeComponent();

        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);

        if (startMinimized)
        {
            WindowState = System.Windows.WindowState.Minimized;
        }

        // A minimized window is not needed: the unused memory is given back to Windows.
        StateChanged += (_, _) =>
        {
            if (WindowState == System.Windows.WindowState.Minimized)
            {
                MemoryTrimmer.TrimSoon();
            }
            else
            {
                MemoryTrimmer.Cancel();
            }
        };

        Loaded += (_, _) => RootNavigation.Navigate(openApps ? typeof(AppPage) : typeof(OverviewPage));
    }

    /// <summary>Switches to another section, as if the user picked it from the side menu.</summary>
    public void NavigateTo(System.Type pageType) => RootNavigation.Navigate(pageType);
}
