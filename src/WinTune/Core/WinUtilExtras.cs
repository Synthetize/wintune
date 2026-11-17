// Entries taken from "Chris Titus Tech's Windows Utility" (https://github.com/ChrisTitusTech/winutil)
// that are not made only of registry values. MIT license, Copyright (c) 2022 CT Tech Group LLC.
// Written by hand: the actions are those of the original scripts, ported to C# where possible.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace WinTune.Core;

internal static class WinUtilExtras
{
    private const string DwmKey = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string ShellKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell";
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private const string FolderViewsQuestion =
        "The view settings of all folders (sorting, columns, icon size) go back to their defaults.\n\nContinue?";

    private static string RazerFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer", "Razer");

    private static string LogitechFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LogiDownloadAssistant");

    private static string StoreFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        "Microsoft.WindowsStore_8wekyb3d8bbwe",
        "LocalState");

    private static string StoreDatabase => Path.Combine(StoreFolder, "store.db");

    public static List<Tweak> Build() => new()
    {
        // ---------- Gaming and performance ----------
        new ChoiceTweak
        {
            Id = "gaming.multiplaneOverlay",
            Category = TweakCatalog.Gaming,
            Title = "Multiplane Overlay (MPO)",
            Description = "Composes the image on multiple layers. With some graphics cards it causes flickering or a brief black screen: in that case try turning it off.",
            NeedsRestart = true,
            GetOptions = () => new List<ChoiceOption>
            {
                new("attivo", "Enabled (default)"),
                new("compatibilita", "Disabled (compatibility)"),
                new("disattivato", "Fully disabled"),
            },
            Read = ReadMultiplaneOverlay,
            Write = WriteMultiplaneOverlay,
        },

        // ---------- Privacy and telemetry ----------
        new ToggleTweak
        {
            Id = "privacy.ricercaStore",
            Category = TweakCatalog.Privacy,
            Title = "Recommended results in Microsoft Store search",
            Description = "Suggested apps and content that appear as you type in the Microsoft Store search.",
            OnText = "Enabled",
            OffText = "Disabled",
            IsPresent = () => File.Exists(StoreDatabase) || AclUtil.HasDeny(StoreDatabase),
            Read = () => !AclUtil.HasDeny(StoreDatabase),
            Write = on => AclUtil.SetFileDeny(StoreDatabase, FileSystemRights.FullControl, !on),
        },

        // ---------- File Explorer ----------
        new ToggleTweak
        {
            Id = "esplora.tipoCartellaAutomatico",
            Category = TweakCatalog.EsploraFile,
            Title = "Automatic folder type discovery",
            Description = "File Explorer picks the view (pictures, music, documents) by looking at the content: in large folders this slows down opening. When disabled, all folders use the generic view and grouping is not available.",
            OnText = "Enabled",
            OffText = "Disabled",
            NeedsRestart = true,
            Confirm = _ => FolderViewsQuestion,
            Read = () => !string.Equals(
                RegistryUtil.GetString(RegistryHive.CurrentUser, ShellKey + @"\Bags\AllFolders\Shell", "FolderType"),
                "NotSpecified",
                StringComparison.OrdinalIgnoreCase),
            Write = on =>
            {
                // In both directions the view store is reset, as the original script does.
                RegistryUtil.DeleteKeyTree(RegistryHive.CurrentUser, ShellKey + @"\Bags");
                RegistryUtil.DeleteKeyTree(RegistryHive.CurrentUser, ShellKey + @"\BagMRU");

                if (!on)
                {
                    RegistryUtil.SetString(RegistryHive.CurrentUser, ShellKey + @"\Bags\AllFolders\Shell", "FolderType", "NotSpecified");
                }
            },
        },

        // ---------- System ----------
        new ToggleTweak
        {
            Id = "sistema.bitlocker",
            Category = TweakCatalog.Sistema,
            Title = "System drive encryption (BitLocker)",
            Description = "BitLocker, or \"Device encryption\" on Windows Home. When you turn it off, Windows decrypts the drive in the background: it can take from a few minutes to a few hours and the PC stays usable. Without encryption, anyone who gets hold of the drive can read its data.",
            IsPresent = () => BitLockerService.IsSupported,
            Confirm = value => value == "true"
                ? "Encryption is turned back on from the Windows settings, which also have you save the recovery key.\n\nOpen them now?"
                : "Windows will decrypt the system drive in the background: it can take from a few minutes to a few hours, and the PC stays usable.\n\nWithout encryption, anyone who gets hold of the drive can read its data. Continue?",
            Read = () => BitLockerService.IsEncrypted,
            Write = on =>
            {
                if (on)
                {
                    BitLockerService.OpenSettings();
                }
                else
                {
                    BitLockerService.Disable();
                }
            },
        },
        new ToggleTweak
        {
            Id = "sistema.softwareLogitech",
            Category = TweakCatalog.Sistema,
            Title = "Logitech Download Assistant",
            Description = "The program that Windows Update reinstalls along with Logitech drivers and that offers to download the manufacturer software. The devices work without it too.",
            OnText = "Allowed",
            OffText = "Blocked",
            Read = () => !AclUtil.IsFolderBlocked(LogitechFolder),
            Write = on =>
            {
                if (on)
                {
                    AclUtil.UnblockFolder(LogitechFolder);
                    return;
                }

                foreach (Process process in Process.GetProcessesByName("logi_download_assistant"))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch
                    {
                        // If it does not close, its files stay but the folder is locked anyway.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                AclUtil.BlockFolder(LogitechFolder);
            },
        },
    };

    // ---------- Multiplane Overlay ----------

    private static string ReadMultiplaneOverlay()
    {
        bool testMode = RegistryUtil.GetDword(RegistryHive.LocalMachine, DwmKey, "OverlayTestMode") == 5;
        bool disabled = RegistryUtil.GetDword(RegistryHive.LocalMachine, GraphicsDriversKey, "DisableOverlays") == 1;
        return testMode && disabled ? "disattivato" : testMode ? "compatibilita" : "attivo";
    }

    private static void WriteMultiplaneOverlay(string value)
    {
        if (value == "attivo")
        {
            RegistryUtil.DeleteValue(RegistryHive.LocalMachine, DwmKey, "OverlayTestMode");
        }
        else
        {
            RegistryUtil.SetDword(RegistryHive.LocalMachine, DwmKey, "OverlayTestMode", 5);
        }

        if (value == "disattivato")
        {
            RegistryUtil.SetDword(RegistryHive.LocalMachine, GraphicsDriversKey, "DisableOverlays", 1);
        }
        else
        {
            RegistryUtil.DeleteValue(RegistryHive.LocalMachine, GraphicsDriversKey, "DisableOverlays");
        }
    }

    // ---------- Parts of the telemetry package that are not registry values ----------

    public static void ApplyTelemetryExtras() => SetTelemetryExtras(false);

    public static void UndoTelemetryExtras() => SetTelemetryExtras(true);

    private static void SetTelemetryExtras(bool enabled)
    {
        SetService("DiagTrack", enabled, "auto");
        SetService("WerSvc", enabled, "demand");

        // PowerShell 7 telemetry: system environment variable.
        try
        {
            if (enabled)
            {
                RegistryUtil.DeleteValue(RegistryHive.LocalMachine, MachineEnvironmentKey, "POWERSHELL_TELEMETRY_OPTOUT");
            }
            else
            {
                RegistryUtil.SetString(RegistryHive.LocalMachine, MachineEnvironmentKey, "POWERSHELL_TELEMETRY_OPTOUT", "1");
            }
        }
        catch
        {
            // Secondary entry: if it is not written, the rest of the package still applies.
        }

        // Automatic sample submission to Defender: 2 = never, 1 = safe samples. Starts in the background;
        // if Defender is not active the command fails without consequences.
        _ = ProcessRunner.RunPowerShellAsync("Set-MpPreference -SubmitSamplesConsent " + (enabled ? "1" : "2"), 60000);
    }

    private static void SetService(string name, bool enabled, string defaultStart)
    {
        try
        {
            if (ServiceUtil.Exists(name))
            {
                ServiceUtil.SetEnabled(name, enabled, defaultStart);
            }
        }
        catch
        {
            // Some services are protected: the rest of the package still applies.
        }
    }

    // ---------- Windows AI features ----------

    /// <summary>Removes Copilot and the AI components and turns off Recall, in a window that shows the progress.</summary>
    public static void RemoveWindowsAi() => ProcessRunner.StartVisiblePowerShell(
        "$ErrorActionPreference = 'Continue'\n" +
        "Write-Host 'Removing the Windows AI features...'\n" +
        "$Appx = (Get-AppxPackage MicrosoftWindows.Client.CoreAI).PackageFullName\n" +
        "$Sid = (Get-LocalUser $Env:UserName).Sid.Value\n" +
        "if ($Appx) { New-Item \"HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\EndOfLife\\$Sid\\$Appx\" -Force | Out-Null }\n" +
        "Get-AppxPackage -AllUsers '*Copilot*' | Remove-AppxPackage -AllUsers\n" +
        "winget uninstall -e --name 'Copilot' --silent --force --accept-source-agreements 2>$null\n" +
        "Get-AppxPackage -AllUsers Microsoft.MicrosoftOfficeHub | Remove-AppxPackage -AllUsers\n" +
        "if ($Appx) { Remove-AppxPackage $Appx }\n" +
        "if (Get-Service -Name WSAIFabricSvc -ErrorAction SilentlyContinue) { Set-Service -Name WSAIFabricSvc -StartupType Disabled }\n" +
        "Write-Host 'Disabling Recall (if present)...'\n" +
        "Disable-WindowsOptionalFeature -FeatureName Recall -Online -NoRestart -ErrorAction SilentlyContinue | Out-Null\n" +
        "Write-Host 'Done.' -ForegroundColor Green\n" +
        "Read-Host 'Press Enter to close'");

    // ---------- Razer software ----------

    public static void BlockRazerFolder() => AclUtil.BlockFolder(RazerFolder);

    public static void UnblockRazerFolder() => AclUtil.UnblockFolder(RazerFolder);
}
