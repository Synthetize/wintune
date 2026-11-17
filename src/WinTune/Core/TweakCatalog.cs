using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>
/// List of all the optimizations. To add one, a new entry in Build() is enough:
/// the interface, the profile and "Apply recommended" pick it up by themselves.
/// </summary>
public static class TweakCatalog
{
    public const string Gaming = "Gaming & performance";
    public const string Privacy = "Privacy & telemetry";
    public const string FileExplorer = "File Explorer";
    public const string Taskbar = "Taskbar & Start";
    public const string Appearance = "Appearance & accessibility";
    public const string Power = "Power";
    public const string SystemCategory = "System";
    public const string Network = "Network";
    public const string Browser = "Browser";
    public const string ServicesSafe = "Services: safe list";
    public const string ServicesAggressive = "Services: aggressive list";
    public const string Driver = "Drivers";
    public const string Updates = "Updates";

    /// <summary>Order in which the categories appear on the Optimizations page.</summary>
    public static IReadOnlyList<string> CategoryOrder { get; } = new[]
    {
        Gaming,
        Privacy,
        FileExplorer,
        Taskbar,
        Appearance,
        Power,
        SystemCategory,
        Network,
        Browser,
        ServicesSafe,
        ServicesAggressive,
    };

    private const string MouseKey = @"Control Panel\Mouse";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameConfigStoreKey = @"System\GameConfigStore";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string DataCollectionPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string AdvertisingKey = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
    private const string ContentDeliveryKey = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ExplorerPolicyKey = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string WidgetPolicyKey = @"SOFTWARE\Policies\Microsoft\Dsh";
    private const string SystemPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string PrivacyKey = @"Software\Microsoft\Windows\CurrentVersion\Privacy";
    private const string FeedbackKey = @"Software\Microsoft\Siuf\Rules";

    // Values that control suggestions, tips and apps proposed by Windows.
    private static readonly string[] SuggestionValues =
    {
        "SubscribedContent-338388Enabled",
        "SubscribedContent-338389Enabled",
        "SubscribedContent-338393Enabled",
        "SubscribedContent-353694Enabled",
        "SubscribedContent-353696Enabled",
        "SystemPaneSuggestionsEnabled",
        "SoftLandingEnabled",
        "SilentInstalledAppsEnabled",
    };

    private static readonly string[] ActivityPolicyValues =
    {
        "EnableActivityFeed",
        "PublishUserActivities",
        "UploadUserActivities",
    };

    private static readonly int[] MonitorMinutes = { 1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 120, 0 };
    private static readonly int[] StandbyMinutes = { 15, 30, 45, 60, 120, 180, 0 };

    private static readonly Lazy<IReadOnlyList<Tweak>> Catalog = new(BuildAll);

    public static IReadOnlyList<Tweak> All => Catalog.Value;

    public static Tweak? Find(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Full catalog: the entries written here plus the ones taken from the Chris Titus Tech Windows Utility,
    /// inserted before the services so their categories appear before the two service lists.
    /// </summary>
    private static IReadOnlyList<Tweak> BuildAll()
    {
        var all = new List<Tweak>(Build());
        int services = all.FindIndex(t => t.Category == ServicesSafe);
        int index = services < 0 ? all.Count : services;

        List<Tweak> imported = WinUtilTweaks.Build();
        imported.AddRange(WinUtilExtras.Build());
        all.InsertRange(index, imported);
        return all;
    }

    private static IReadOnlyList<Tweak> Build() => new List<Tweak>
    {
        // ---------- Mouse (shown among the gaming entries) ----------
        new ToggleTweak
        {
            Id = "mouse.acceleration",
            Category = Gaming,
            Title = "Mouse acceleration",
            Description = "This is the Windows \"Enhance pointer precision\" setting. When disabled, the pointer movement is always proportional.",
            Recommended = "false",
            Read = ReadMouseAcceleration,
            Write = WriteMouseAcceleration,
        },

        // ---------- Power ----------
        new ToggleTweak
        {
            Id = "power.hibernation",
            Category = Power,
            Title = "Hibernation",
            Description = "Turning it off also disables fast startup and frees the space used by the hiberfil.sys file.",
            Recommended = "false",
            Read = ReadHibernate,
            Write = PowerUtil.SetHibernate,
        },
        new ChoiceTweak
        {
            Id = "power.display",
            Category = Power,
            Title = "Turn off the screen",
            Description = "Idle time before the screen turns off (when plugged in).",
            GetOptions = () => MinuteOptions(MonitorMinutes, PowerUtil.ReadAcMinutes(PowerUtil.VideoSubgroup, PowerUtil.VideoIdleTimeout)),
            Read = () => PowerUtil.ReadAcMinutes(PowerUtil.VideoSubgroup, PowerUtil.VideoIdleTimeout),
            Write = PowerUtil.SetMonitorTimeout,
        },
        new ChoiceTweak
        {
            Id = "power.sleep",
            Category = Power,
            Title = "Sleep",
            Description = "Idle time before the PC goes to sleep (when plugged in).",
            GetOptions = () => MinuteOptions(StandbyMinutes, PowerUtil.ReadAcMinutes(PowerUtil.SleepSubgroup, PowerUtil.StandbyIdleTimeout)),
            Read = () => PowerUtil.ReadAcMinutes(PowerUtil.SleepSubgroup, PowerUtil.StandbyIdleTimeout),
            Write = PowerUtil.SetStandbyTimeout,
        },
        new ChoiceTweak
        {
            Id = "power.plan",
            Category = Power,
            Title = "Power plan",
            Description = "Choose among the plans available on this PC.",
            GetOptions = () => PowerUtil.ListPlans().Select(p => new ChoiceOption(p.Guid, p.Name)).ToList(),
            Read = () => PowerUtil.GetActiveScheme()?.ToString("D").ToLowerInvariant(),
            Write = PowerUtil.SetActivePlan,
        },
        new ToggleTweak
        {
            Id = "power.ultimatePerformance",
            Category = Power,
            Title = "Ultimate Performance plan",
            Description = "Creates and activates the hidden \"Ultimate Performance\" power plan: no CPU power saving, so more heat and consumption. Turning it off goes back to Balanced and deletes the plan. Not meant for laptops on battery.",
            OnText = "Active",
            OffText = "Not active",
            Read = PowerUtil.IsUltimateActive,
            Write = on =>
            {
                if (on)
                {
                    PowerUtil.EnableUltimate();
                }
                else
                {
                    PowerUtil.DisableUltimate();
                }
            },
        },
        new ChoiceTweak
        {
            Id = "network.dns",
            Category = Network,
            Title = "DNS servers",
            Description = "Name servers used by your active network adapters (IPv4). \"Automatic\" uses the ones from your router or provider. Changing it can speed up or slow down name lookups; it does not change your Internet speed.",
            GetOptions = () => DnsService.Options(DnsService.Read()),
            Read = DnsService.Read,
            Write = DnsService.Apply,
        },

        // ---------- File Explorer ----------
        new ToggleTweak
        {
            Id = "explorer.extensions",
            Category = FileExplorer,
            Title = "File extensions",
            Description = "Shows the extension (.txt, .exe...) next to the file name.",
            OnText = "Shown",
            OffText = "Hidden",
            Recommended = "true",
            Read = () => (RegistryUtil.GetDword(RegistryHive.CurrentUser, ExplorerAdvancedKey, "HideFileExt") ?? 1) == 0,
            Write = on => SetExplorerValue("HideFileExt", on ? 0 : 1),
        },
        new ToggleTweak
        {
            Id = "explorer.hidden",
            Category = FileExplorer,
            Title = "Hidden files and folders",
            Description = "Also shows files and folders marked as hidden.",
            OnText = "Shown",
            OffText = "Hidden",
            Read = () => (RegistryUtil.GetDword(RegistryHive.CurrentUser, ExplorerAdvancedKey, "Hidden") ?? 2) == 1,
            Write = on => SetExplorerValue("Hidden", on ? 1 : 2),
        },
        new ToggleTweak
        {
            Id = "explorer.thisPc",
            Category = FileExplorer,
            Title = "Open File Explorer on \"This PC\"",
            Description = "Instead of the home page with recent files.",
            OnText = "Yes",
            OffText = "No",
            Read = () => (RegistryUtil.GetDword(RegistryHive.CurrentUser, ExplorerAdvancedKey, "LaunchTo") ?? 2) == 1,
            Write = on => SetExplorerValue("LaunchTo", on ? 1 : 2),
        },
        new ToggleTweak
        {
            Id = "explorer.classicMenu",
            Category = FileExplorer,
            Title = "Classic context menu",
            Description = "Right-click opens the full menu straight away, without \"Show more options\".",
            OnText = "Enabled",
            OffText = "Disabled",
            Windows11Only = true,
            NeedsExplorerRestart = true,
            Read = () => RegistryUtil.KeyExists(RegistryHive.CurrentUser, ClassicMenuKey + @"\InprocServer32"),
            Write = WriteClassicMenu,
        },

        // ---------- Gaming ----------
        new ToggleTweak
        {
            Id = "gaming.gameMode",
            Category = Gaming,
            Title = "Game Mode",
            Description = "Gives priority to the game in the foreground.",
            Recommended = "true",
            Read = () => (RegistryUtil.GetDword(RegistryHive.CurrentUser, GameBarKey, "AutoGameModeEnabled") ?? 1) != 0,
            Write = on =>
            {
                RegistryUtil.SetDword(RegistryHive.CurrentUser, GameBarKey, "AutoGameModeEnabled", on ? 1 : 0);
                RegistryUtil.SetDword(RegistryHive.CurrentUser, GameBarKey, "AllowAutoGameMode", on ? 1 : 0);
            },
        },
        new ToggleTweak
        {
            Id = "gaming.gameBar",
            Category = Gaming,
            Title = "Xbox Game Bar",
            Description = "Overlay and background recording. It is turned off, not uninstalled.",
            Recommended = "false",
            Read = ReadGameBar,
            Write = WriteGameBar,
        },
        new ToggleTweak
        {
            Id = "gaming.gpuScheduling",
            Category = Gaming,
            Title = "Hardware-accelerated GPU scheduling",
            Description = "Windows graphics setting. Takes effect after the next restart.",
            NeedsRestart = true,
            Read = () => RegistryUtil.GetDword(RegistryHive.LocalMachine, GraphicsDriversKey, "HwSchMode") == 2,
            Write = on => RegistryUtil.SetDword(RegistryHive.LocalMachine, GraphicsDriversKey, "HwSchMode", on ? 2 : 1),
        },

        // ---------- Privacy and telemetry ----------
        new ToggleTweak
        {
            Id = "privacy.minimalTelemetry",
            Category = Privacy,
            Title = "Minimum telemetry",
            Description = "Limits the diagnostic data sent to Microsoft to the minimum allowed by your Windows edition.",
            Recommended = "true",
            Read = () => RegistryUtil.GetDword(RegistryHive.LocalMachine, DataCollectionPolicyKey, "AllowTelemetry") == 0,
            Write = on =>
            {
                if (on)
                {
                    RegistryUtil.SetDword(RegistryHive.LocalMachine, DataCollectionPolicyKey, "AllowTelemetry", 0);
                }
                else
                {
                    RegistryUtil.DeleteValue(RegistryHive.LocalMachine, DataCollectionPolicyKey, "AllowTelemetry");
                }
            },
        },
        new ToggleTweak
        {
            Id = "privacy.advertisingId",
            Category = Privacy,
            Title = "Advertising ID",
            Description = "Identifier that apps use to show you personalized ads.",
            OnText = "Enabled",
            OffText = "Disabled",
            Recommended = "false",
            Read = () => ReadFlag(RegistryHive.CurrentUser, AdvertisingKey, "Enabled", 1),
            Write = on => RegistryUtil.SetDword(RegistryHive.CurrentUser, AdvertisingKey, "Enabled", on ? 1 : 0),
        },
        new ToggleTweak
        {
            Id = "privacy.suggestions",
            Category = Privacy,
            Title = "Suggestions and recommended apps",
            Description = "Tips in the Start menu and in Settings, and apps suggested or installed by Windows.",
            OnText = "Enabled",
            OffText = "Disabled",
            Recommended = "false",
            Read = () => SuggestionValues.Any(name => ReadFlag(RegistryHive.CurrentUser, ContentDeliveryKey, name, 1)),
            Write = on =>
            {
                foreach (string name in SuggestionValues)
                {
                    RegistryUtil.SetDword(RegistryHive.CurrentUser, ContentDeliveryKey, name, on ? 1 : 0);
                }
            },
        },
        new ToggleTweak
        {
            Id = "privacy.webSearch",
            Category = Privacy,
            Title = "Web search in the Start menu",
            Description = "Bing results mixed with files and programs when you search from the Start menu.",
            Recommended = "false",
            NeedsExplorerRestart = true,
            Read = () => RegistryUtil.GetDword(RegistryHive.CurrentUser, ExplorerPolicyKey, "DisableSearchBoxSuggestions") != 1,
            Write = on =>
            {
                if (on)
                {
                    RegistryUtil.DeleteValue(RegistryHive.CurrentUser, ExplorerPolicyKey, "DisableSearchBoxSuggestions");
                }
                else
                {
                    RegistryUtil.SetDword(RegistryHive.CurrentUser, ExplorerPolicyKey, "DisableSearchBoxSuggestions", 1);
                }

                RegistryUtil.SetDword(RegistryHive.CurrentUser, SearchKey, "BingSearchEnabled", on ? 1 : 0);
            },
        },
        new ToggleTweak
        {
            Id = "privacy.widget",
            Category = Taskbar,
            Title = "Widget",
            Description = "News and weather panel on the taskbar, with its background processes.",
            OnText = "Enabled",
            OffText = "Disabled",
            Windows11Only = true,
            NeedsRestart = true,
            Read = () => RegistryUtil.GetDword(RegistryHive.LocalMachine, WidgetPolicyKey, "AllowNewsAndInterests") != 0,
            Write = on =>
            {
                if (on)
                {
                    RegistryUtil.DeleteValue(RegistryHive.LocalMachine, WidgetPolicyKey, "AllowNewsAndInterests");
                }
                else
                {
                    RegistryUtil.SetDword(RegistryHive.LocalMachine, WidgetPolicyKey, "AllowNewsAndInterests", 0);
                }
            },
        },
        new ToggleTweak
        {
            Id = "privacy.activityHistory",
            Category = Privacy,
            Title = "Activity history",
            Description = "Collects the activities done on the PC and sends them to Microsoft.",
            Recommended = "false",
            Read = () => !ActivityPolicyValues.All(name => RegistryUtil.GetDword(RegistryHive.LocalMachine, SystemPolicyKey, name) == 0),
            Write = on =>
            {
                foreach (string name in ActivityPolicyValues)
                {
                    if (on)
                    {
                        RegistryUtil.DeleteValue(RegistryHive.LocalMachine, SystemPolicyKey, name);
                    }
                    else
                    {
                        RegistryUtil.SetDword(RegistryHive.LocalMachine, SystemPolicyKey, name, 0);
                    }
                }
            },
        },
        new ToggleTweak
        {
            Id = "privacy.personalizedExperiences",
            Category = Privacy,
            Title = "Tailored experiences",
            Description = "Microsoft uses diagnostic data for tailored tips, suggestions and ads.",
            OnText = "Enabled",
            OffText = "Disabled",
            Recommended = "false",
            Read = () => ReadFlag(RegistryHive.CurrentUser, PrivacyKey, "TailoredExperiencesWithDiagnosticDataEnabled", 1),
            Write = on => RegistryUtil.SetDword(RegistryHive.CurrentUser, PrivacyKey, "TailoredExperiencesWithDiagnosticDataEnabled", on ? 1 : 0),
        },
        new ToggleTweak
        {
            Id = "privacy.feedbackPrompts",
            Category = Privacy,
            Title = "Feedback requests",
            Description = "Windows occasionally asks for your opinion on the system.",
            OnText = "Enabled",
            OffText = "Disabled",
            Recommended = "false",
            Read = () => RegistryUtil.GetDword(RegistryHive.CurrentUser, FeedbackKey, "NumberOfSIUFInPeriod") != 0,
            Write = on =>
            {
                if (on)
                {
                    RegistryUtil.DeleteValue(RegistryHive.CurrentUser, FeedbackKey, "NumberOfSIUFInPeriod");
                }
                else
                {
                    RegistryUtil.SetDword(RegistryHive.CurrentUser, FeedbackKey, "NumberOfSIUFInPeriod", 0);
                }
            },
        },
        new ToggleTweak
        {
            Id = "privacy.explorerAds",
            Category = Privacy,
            Title = "Ads in File Explorer",
            Description = "Promotional notices (for example from OneDrive) inside File Explorer.",
            Recommended = "false",
            Read = () => ReadFlag(RegistryHive.CurrentUser, ExplorerAdvancedKey, "ShowSyncProviderNotifications", 1),
            Write = on => SetExplorerValue("ShowSyncProviderNotifications", on ? 1 : 0),
        },

        // ---------- Drivers and updates: managed by their pages, here only for the profile ----------
        new ToggleTweak
        {
            Id = "driver.automatic",
            Category = Driver,
            Title = "Automatic drivers from Windows Update",
            OnText = "Enabled",
            OffText = "Disabled",
            Hidden = true,
            Read = () => !DriverUpdateService.IsBlocked,
            Write = on =>
            {
                if (on)
                {
                    DriverUpdateService.Unblock();
                }
                else
                {
                    DriverUpdateService.Block(null);
                }
            },
        },
        new ChoiceTweak
        {
            Id = "updates.mode",
            Category = Updates,
            Title = "Windows Update mode",
            Hidden = true,
            GetOptions = UpdateModeOptions,
            Read = ReadUpdateMode,
            Write = WriteUpdateMode,
        },

        // ---------- Services: safe list ----------
        Service(ServicesSafe, "DiagTrack", "Telemetry (DiagTrack)", "Connected User Experiences and Telemetry: collects and sends diagnostic data.", "auto", true),
        Service(ServicesSafe, "dmwappushservice", "WAP Push message routing", "Used almost only for telemetry.", "demand", true),
        Service(ServicesSafe, "MapsBroker", "Downloaded maps manager", "Only needed by the Maps app with offline maps.", "delayed-auto", true),
        Service(ServicesSafe, "Fax", "Fax", "Sending and receiving faxes.", "demand", true),
        Service(ServicesSafe, "RetailDemo", "Retail demo", "Demo mode for display PCs in shops.", "demand", true),
        Service(ServicesSafe, "RemoteRegistry", "Remote Registry", "Allows the registry to be modified from another computer on the network.", "disabled", true),
        Service(ServicesSafe, "AJRouter", "AllJoyn Router", "Communication with AllJoyn IoT devices, now rarely used.", "demand", true),
        Service(ServicesSafe, "WMPNetworkSvc", "Windows Media Player network sharing", "Shares media libraries with other devices.", "demand", true),
        Service(ServicesSafe, "WpcMonSvc", "Parental controls", "Only needed if you use Microsoft Family parental controls.", "demand", true),
        Service(ServicesSafe, "wisvc", "Windows Insider Program", "Only needed if you receive Windows preview builds.", "demand", true),
        Service(ServicesSafe, "CscService", "Offline Files", "Local copies of network files, used almost only on company networks.", "demand", true),
        new ToggleTweak
        {
            // The only service that is not turned off but set to "manual", as the Chris Titus Tech Windows Utility does.
            Id = "services.StorSvc.autoStart",
            Category = ServicesSafe,
            Title = "Storage service: automatic start",
            Description = "Manages storage settings and installations from the Microsoft Store. When disabled, the service does not start by itself at boot but only when needed: it is not turned off.",
            OnText = "Enabled",
            OffText = "Disabled",
            IsPresent = () => ServiceUtil.Exists("StorSvc"),
            Read = () => ServiceUtil.CurrentStartType("StorSvc") is "auto" or "delayed-auto",
            Write = on => ProcessRunner.RunOrThrow(
                ProcessRunner.SystemTool("sc.exe"),
                "config \"StorSvc\" start= " + (on ? "auto" : "demand")),
        },

        // ---------- Services: aggressive list ----------
        Service(ServicesAggressive, "SysMain", "SysMain (Superfetch)", "Preloads frequently used programs into memory. On an SSD the benefit is minimal.", "auto", false),
        Service(ServicesAggressive, "WSearch", "Windows Search indexing", "Without it, file search is much slower.", "delayed-auto", false),
        Service(ServicesAggressive, "WerSvc", "Windows Error Reporting", "Sends crash reports to Microsoft. Without it, some crash details are not recorded.", "demand", false),
        Service(ServicesAggressive, "Spooler", "Print spooler", "Turn off only if you use no printers, not even print to PDF.", "auto", false),
        Service(ServicesAggressive, "bthserv", "Bluetooth support", "Turn off only if you use no Bluetooth devices.", "demand", false),
        Service(ServicesAggressive, "lfsvc", "Geolocation", "PC location for apps and weather.", "demand", false),
        Service(ServicesAggressive, "PcaSvc", "Program Compatibility Assistant", "Reports compatibility problems with old programs.", "auto", false),
        Service(ServicesAggressive, "TrkWks", "Distributed Link Tracking", "Keeps shortcuts valid for files moved between NTFS drives.", "auto", false),
        Service(ServicesAggressive, "WbioSrvc", "Biometric service", "Turn off only if you do not use fingerprint or face recognition (Windows Hello).", "demand", false),
        Service(ServicesAggressive, "SCardSvr", "Smart card", "Turn off only if you use no smart card readers (ID cards, digital signature).", "demand", false),
        Service(ServicesAggressive, "PhoneSvc", "Phone service", "Used by Phone Link.", "demand", false),
        Service(ServicesAggressive, "XblAuthManager", "Xbox Live: authentication", "Needed by the Xbox app, Game Pass and Microsoft Store games.", "demand", false),
        Service(ServicesAggressive, "XblGameSave", "Xbox Live: game saves", "Syncs saves of Xbox and Game Pass games.", "demand", false),
        Service(ServicesAggressive, "XboxNetApiSvc", "Xbox Live: networking", "Multiplayer for Xbox and Game Pass games.", "demand", false),
        Service(ServicesAggressive, "XboxGipSvc", "Xbox: accessories", "Management of Xbox controllers and accessories.", "demand", false),
        Service(ServicesAggressive, "SharedAccess", "Internet Connection Sharing", "Needed by the Windows mobile hotspot and the network of some virtual machines.", "demand", false),
    };

    // ---------- Drivers and updates (dedicated pages) ----------

    private static IReadOnlyList<ChoiceOption> UpdateModeOptions() => new List<ChoiceOption>
    {
        new("enabled", "Enabled"),
        new("notifyOnly", "Notify only"),
        new("paused", "Paused"),
        new("disabled", "Disabled"),
    };

    private static string ReadUpdateMode() => WindowsUpdateService.CurrentMode switch
    {
        UpdateMode.NotifyOnly => "notifyOnly",
        UpdateMode.Paused => "paused",
        UpdateMode.Disabled => "disabled",
        _ => "enabled",
    };

    private static void WriteUpdateMode(string value)
    {
        switch (value)
        {
            case "notifyOnly":
                WindowsUpdateService.Apply(UpdateMode.NotifyOnly, null);
                break;
            case "paused":
                // The profile does not carry a date: the standard Windows pause is used, 35 days.
                WindowsUpdateService.Apply(UpdateMode.Paused, DateTime.Now.AddDays(35));
                break;
            case "disabled":
                WindowsUpdateService.Apply(UpdateMode.Disabled, null);
                break;
            default:
                WindowsUpdateService.Apply(UpdateMode.Enabled, null);
                break;
        }
    }

    // ---------- Common helpers ----------

    private static bool ReadFlag(RegistryHive hive, string key, string name, int defaultValue) =>
        (RegistryUtil.GetDword(hive, key, name) ?? defaultValue) != 0;

    private static ToggleTweak Service(string category, string serviceName, string title, string description, string defaultStart, bool recommendOff) =>
        new()
        {
            Id = "services." + serviceName,
            Category = category,
            Title = title,
            Description = description,
            OnText = "Enabled",
            OffText = "Disabled",
            Recommended = recommendOff ? "false" : null,
            IsPresent = () => ServiceUtil.Exists(serviceName),
            Read = () => ServiceUtil.IsEnabled(serviceName),
            Write = on => ServiceUtil.SetEnabled(serviceName, on, defaultStart),
        };

    // ---------- Mouse ----------

    private static bool ReadMouseAcceleration()
    {
        var parameters = new int[3];
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSE, 0, parameters, 0))
        {
            return parameters[2] != 0;
        }

        return RegistryUtil.GetString(RegistryHive.CurrentUser, MouseKey, "MouseSpeed") != "0";
    }

    private static void WriteMouseAcceleration(bool enabled)
    {
        // Windows defaults with acceleration on: thresholds 6 and 10, speed 1.
        int threshold1 = enabled ? 6 : 0;
        int threshold2 = enabled ? 10 : 0;
        int speed = enabled ? 1 : 0;

        RegistryUtil.SetString(RegistryHive.CurrentUser, MouseKey, "MouseThreshold1", threshold1.ToString());
        RegistryUtil.SetString(RegistryHive.CurrentUser, MouseKey, "MouseThreshold2", threshold2.ToString());
        RegistryUtil.SetString(RegistryHive.CurrentUser, MouseKey, "MouseSpeed", speed.ToString());

        var parameters = new[] { threshold1, threshold2, speed };
        bool applied = NativeMethods.SystemParametersInfo(
            NativeMethods.SPI_SETMOUSE,
            0,
            parameters,
            NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);

        if (!applied)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    // ---------- Power ----------

    private static bool ReadHibernate()
    {
        int? enabled = RegistryUtil.GetDword(RegistryHive.LocalMachine, PowerKey, "HibernateEnabled");
        if (enabled.HasValue)
        {
            return enabled.Value != 0;
        }

        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return File.Exists(Path.Combine(systemDrive, "hiberfil.sys"));
    }

    private static IReadOnlyList<ChoiceOption> MinuteOptions(int[] standard, string? current)
    {
        var minutes = new List<int>(standard);

        // If the current value is not among the expected ones it is added, so it stays visible.
        if (int.TryParse(current, out int currentMinutes) && !minutes.Contains(currentMinutes))
        {
            minutes.Add(currentMinutes);
        }

        return minutes
            .OrderBy(m => m == 0 ? int.MaxValue : m)
            .Select(m => new ChoiceOption(m.ToString(), FormatMinutes(m)))
            .ToList();
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes == 0)
        {
            return "Never";
        }

        if (minutes % 60 == 0)
        {
            int hours = minutes / 60;
            return hours == 1 ? "1 hour" : hours + " hours";
        }

        return minutes == 1 ? "1 minute" : minutes + " minutes";
    }

    // ---------- File Explorer ----------

    private static void SetExplorerValue(string name, int value)
    {
        RegistryUtil.SetDword(RegistryHive.CurrentUser, ExplorerAdvancedKey, name, value);
        ExplorerUtil.NotifySettingsChanged();
    }

    private static void WriteClassicMenu(bool enabled)
    {
        if (enabled)
        {
            // The key with an empty default value brings back the old menu.
            RegistryUtil.SetString(RegistryHive.CurrentUser, ClassicMenuKey + @"\InprocServer32", "", "");
        }
        else
        {
            RegistryUtil.DeleteKeyTree(RegistryHive.CurrentUser, ClassicMenuKey);
        }
    }

    // ---------- Gaming ----------

    private static bool ReadGameBar()
    {
        int capture = RegistryUtil.GetDword(RegistryHive.CurrentUser, GameDvrKey, "AppCaptureEnabled") ?? 1;
        int dvr = RegistryUtil.GetDword(RegistryHive.CurrentUser, GameConfigStoreKey, "GameDVR_Enabled") ?? 1;
        int overlay = RegistryUtil.GetDword(RegistryHive.CurrentUser, GameBarKey, "UseNexusForGameBarEnabled") ?? 1;
        return capture != 0 || dvr != 0 || overlay != 0;
    }

    private static void WriteGameBar(bool enabled)
    {
        int value = enabled ? 1 : 0;
        RegistryUtil.SetDword(RegistryHive.CurrentUser, GameDvrKey, "AppCaptureEnabled", value);
        RegistryUtil.SetDword(RegistryHive.CurrentUser, GameConfigStoreKey, "GameDVR_Enabled", value);
        RegistryUtil.SetDword(RegistryHive.CurrentUser, GameBarKey, "UseNexusForGameBarEnabled", value);
    }
}
