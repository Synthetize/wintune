using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>A component detected on the PC, with the default link to its drivers page.</summary>
public sealed record HardwareComponent(string Type, string Model, string Detail, string DefaultLink)
{
    /// <summary>Key used in the profile to remember the link chosen by the user.</summary>
    public string Key => Type + "|" + Model;
}

/// <summary>Detects the hardware from the Windows registry, without drivers or external tools.</summary>
public static class HardwareService
{
    public const string AmdDrivers = "https://www.amd.com/en/support/download.html";
    public const string NvidiaDrivers = "https://www.nvidia.com/Download/index.aspx";
    public const string IntelDrivers = "https://www.intel.com/content/www/us/en/download-center/home.html";

    private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\";
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string NetworkClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string AudioClass = "{4d36e96c-e325-11ce-bfc1-08002be10318}";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    private static readonly string[] VirtualWords =
    {
        "Virtual", "Miniport", "Loopback", "TAP", "VPN", "Hyper-V", "VMware", "VirtualBox",
        "Bluetooth", "Kernel Debug", "Wi-Fi Direct", "Streaming", "Remote", "Basic",
    };

    public static List<HardwareComponent> Detect()
    {
        var components = new List<HardwareComponent>();

        // CPU and chipset: the manufacturer page covers both.
        string cpu = SystemInfo.CpuName;
        components.Add(new HardwareComponent("CPU & chipset", cpu, "", VendorLink(cpu) ?? SearchLink(cpu + " chipset driver")));

        // Graphics cards.
        foreach ((string name, string detail, string _) in ReadClass(DisplayClass))
        {
            components.Add(new HardwareComponent("Graphics card", name, detail, VendorLink(name) ?? SearchLink(name + " driver")));
        }

        // Motherboard: the support page of the model has chipset, network, audio and BIOS.
        string manufacturer = RegistryUtil.GetString(RegistryHive.LocalMachine, BiosKey, "BaseBoardManufacturer")?.Trim() ?? "";
        string product = RegistryUtil.GetString(RegistryHive.LocalMachine, BiosKey, "BaseBoardProduct")?.Trim() ?? "";
        string board = (manufacturer + " " + product).Trim();
        if (board.Length > 0)
        {
            string? biosVersion = RegistryUtil.GetString(RegistryHive.LocalMachine, BiosKey, "BIOSVersion");
            string? biosDate = RegistryUtil.GetString(RegistryHive.LocalMachine, BiosKey, "BIOSReleaseDate");
            string detail = string.IsNullOrWhiteSpace(biosVersion)
                ? ""
                : "BIOS " + biosVersion.Trim() + (string.IsNullOrWhiteSpace(biosDate) ? "" : " dated " + biosDate.Trim());

            components.Add(new HardwareComponent("Motherboard", board, detail, SearchLink(board + " support driver download")));
        }

        // Physical network adapters.
        foreach ((string name, string detail, string _) in ReadClass(NetworkClass, physicalNetworkOnly: true))
        {
            components.Add(new HardwareComponent("Network", name, detail, SearchLink(name + " driver")));
        }

        // Audio devices with manufacturer drivers.
        foreach ((string name, string detail, string provider) in ReadClass(AudioClass))
        {
            if (string.Equals(provider, "Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            components.Add(new HardwareComponent("Audio", name, detail, VendorLink(name) ?? SearchLink(name + " driver")));
        }

        return components;
    }

    /// <summary>Manufacturer driver page, if the name makes it recognizable.</summary>
    private static string? VendorLink(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return NvidiaDrivers;
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            return AmdDrivers;
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return IntelDrivers;
        }

        return null;
    }

    /// <summary>When there is no reliable page, the link is a web search with the component name.</summary>
    private static string SearchLink(string text) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString(text);

    /// <summary>Devices of a hardware class: name, driver version, driver manufacturer.</summary>
    private static List<(string Name, string Detail, string Provider)> ReadClass(string classGuid, bool physicalNetworkOnly = false)
    {
        var devices = new List<(string Name, string Detail, string Provider)>();
        string classPath = ClassRoot + classGuid;

        List<string> subKeys;
        try
        {
            subKeys = RegistryUtil.GetSubKeyNames(RegistryHive.LocalMachine, classPath);
        }
        catch
        {
            return devices;
        }

        foreach (string subKey in subKeys)
        {
            // Device entries have four-digit names (0000, 0001...).
            if (subKey.Length != 4 || !subKey.All(char.IsDigit))
            {
                continue;
            }

            try
            {
                string path = classPath + @"\" + subKey;
                string? name = RegistryUtil.GetString(RegistryHive.LocalMachine, path, "DriverDesc")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (VirtualWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (physicalNetworkOnly)
                {
                    // Bit 0x4 = physical adapter.
                    int characteristics = RegistryUtil.GetDword(RegistryHive.LocalMachine, path, "Characteristics") ?? 0;
                    if ((characteristics & 0x4) == 0)
                    {
                        continue;
                    }
                }

                if (devices.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string? version = RegistryUtil.GetString(RegistryHive.LocalMachine, path, "DriverVersion");
                string? date = RegistryUtil.GetString(RegistryHive.LocalMachine, path, "DriverDate");
                string provider = RegistryUtil.GetString(RegistryHive.LocalMachine, path, "ProviderName") ?? "";

                string detail = string.IsNullOrWhiteSpace(version)
                    ? ""
                    : "Driver " + version.Trim() + (string.IsNullOrWhiteSpace(date) ? "" : " dated " + date.Trim());

                devices.Add((name, detail, provider));
            }
            catch
            {
                // An unreadable entry is skipped.
            }
        }

        return devices;
    }
}
