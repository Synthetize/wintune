using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace WinTune.Core;

/// <summary>DNS of the active physical network adapters: read from the registry, set with PowerShell.</summary>
internal static class DnsService
{
    public const string Automatic = "auto";
    public const string Custom = "custom";

    private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";

    private static readonly (string Key, string Label, string[] Servers)[] Presets =
    {
        ("cloudflare", "Cloudflare (1.1.1.1)", new[] { "1.1.1.1", "1.0.0.1" }),
        ("google", "Google (8.8.8.8)", new[] { "8.8.8.8", "8.8.4.4" }),
        ("quad9", "Quad9 (9.9.9.9)", new[] { "9.9.9.9", "149.112.112.112" }),
    };

    private static readonly string[] VirtualWords =
        { "virtual", "hyper-v", "vmware", "vbox", "tap-", "tun", "vpn", "wireguard", "bluetooth", "loopback", "pseudo" };

    public static List<ChoiceOption> Options(string? current)
    {
        var options = new List<ChoiceOption> { new(Automatic, "Automatic (from the router)") };
        options.AddRange(Presets.Select(p => new ChoiceOption(p.Key, p.Label)));

        if (current == Custom)
        {
            options.Add(new ChoiceOption(Custom, "Custom (set outside WinTune)"));
        }

        return options;
    }

    /// <summary>Key of the choice in use on all active adapters; "custom" if they differ or are not recognized; null if it cannot be read.</summary>
    public static string? Read()
    {
        var keys = new HashSet<string>();

        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsRelevant(adapter))
                {
                    continue;
                }

                string? raw = RegistryUtil.GetString(RegistryHive.LocalMachine, InterfacesKey + adapter.Id, "NameServer");
                string[] servers = (raw ?? "")
                    .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                keys.Add(servers.Length == 0 ? Automatic : KeyFor(servers));
            }
        }
        catch
        {
            return null;
        }

        return keys.Count switch
        {
            0 => null,
            1 => keys.First(),
            _ => Custom,
        };
    }

    public static void Apply(string key)
    {
        if (key == Custom)
        {
            return;
        }

        string script;
        if (key == Automatic)
        {
            script = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { "
                + "Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses }; "
                + "Clear-DnsClientCache; Write-Output 'OK'";
        }
        else
        {
            var preset = Presets.FirstOrDefault(p => p.Key == key);
            if (preset.Servers is null)
            {
                throw new ArgumentException("Unknown DNS choice: " + key);
            }

            string addresses = string.Join(",", preset.Servers.Select(s => "'" + s + "'"));
            script = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { "
                + "Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses (" + addresses + ") }; "
                + "Clear-DnsClientCache; Write-Output 'OK'";
        }

        ProcessResult result = ProcessRunner.RunPowerShellAsync(script).GetAwaiter().GetResult();
        if (!result.Success || result.Output.TrimStart().StartsWith("ERROR:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Could not change the DNS servers. " + result.Output.Trim() + " " + result.Error.Trim());
        }
    }

    private static string KeyFor(string[] servers)
    {
        foreach ((string key, _, string[] preset) in Presets)
        {
            if (servers.Length == preset.Length && preset.All(p => servers.Contains(p)))
            {
                return key;
            }
        }

        return Custom;
    }

    private static bool IsRelevant(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up
            || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        string text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
        return !VirtualWords.Any(text.Contains);
    }
}
