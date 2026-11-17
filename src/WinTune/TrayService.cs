using System;
using System.Windows;
using System.Windows.Threading;
using WinTune.Core;

namespace WinTune;

/// <summary>WinTune icon in the notification area, with the menu "Open WinTune" and "Exit".</summary>
internal static class TrayService
{
    private static System.Windows.Forms.NotifyIcon? _icon;
    private static bool _hintShown;

    /// <summary>Shows the icon (creating it if it does not exist).</summary>
    public static void Enable()
    {
        if (_icon is not null)
        {
            _icon.Visible = true;
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open WinTune", (System.Drawing.Image?)null, (_, _) => ShowWindow());
        menu.Items.Add("Exit", (System.Drawing.Image?)null, (_, _) => ExitApp());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WinTune",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                ShowWindow();
            }
        };
    }

    /// <summary>Removes the icon.</summary>
    public static void Disable()
    {
        if (_icon is null)
        {
            return;
        }

        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }

    /// <summary>Brings the window to the front (recreating it if it was closed); "openApps" opens the Apps page.</summary>
    public static void ShowWindow(bool openApps = false) => App.ShowMainWindow(openApps);

    /// <summary>Says once per session that the program is still running.</summary>
    public static void ShowHideHint()
    {
        if (_icon is null || _hintShown)
        {
            return;
        }

        _hintShown = true;
        _icon.ShowBalloonTip(
            3000,
            "WinTune",
            "WinTune is still running in the notification area. Right-click the icon to exit.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>Really closes the program.</summary>
    public static void ExitApp()
    {
        App.IsExiting = true;
        Disable();
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is string path
                && System.Drawing.Icon.ExtractAssociatedIcon(path) is System.Drawing.Icon icon)
            {
                return icon;
            }
        }
        catch (Exception)
        {
            // Without the file icon the generic one is used.
        }

        return System.Drawing.SystemIcons.Application;
    }
}
