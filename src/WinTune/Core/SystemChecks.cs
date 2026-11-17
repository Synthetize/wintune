using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinTune.Core;

public enum CheckLevel
{
    Ok,
    Warning,
    Info,
}

/// <summary>Result of an Overview check.</summary>
public sealed record CheckResult(string Title, CheckLevel Level, string Value, string Detail);

/// <summary>
/// System checks of the Overview. The information Windows only exposes through WMI
/// (RAM modules, license, Secure Boot, TPM, disks) is read with a single PowerShell script.
/// </summary>
public static class SystemChecks
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    private const string Script =
        "$r = [ordered]@{}; " +
        "try { $r.ram = @(Get-CimInstance Win32_PhysicalMemory | ForEach-Object { [ordered]@{ " +
        "type = [int]$_.SMBIOSMemoryType; speed = [int]$_.Speed; configured = [int]$_.ConfiguredClockSpeed; " +
        "voltage = [int]$_.ConfiguredVoltage; code = ([string]$_.PartNumber).Trim(); capacity = [long]$_.Capacity } }) } catch { $r.ram = @() }; " +
        "try { $r.secureBoot = [bool](Confirm-SecureBootUEFI) } catch { $r.secureBoot = $null }; " +
        "try { $t = Get-Tpm; $r.tpmPresent = [bool]$t.TpmPresent; $r.tpmReady = [bool]$t.TpmReady } catch { $r.tpmPresent = $null; $r.tpmReady = $null }; " +
        "try { $l = Get-CimInstance SoftwareLicensingProduct -Filter \"ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL\" | Select-Object -First 1; " +
        "$r.license = if ($l) { [int]$l.LicenseStatus } else { -1 } } catch { $r.license = $null }; " +
        "try { $r.trim = (& fsutil.exe behavior query DisableDeleteNotify | Out-String) } catch { $r.trim = $null }; " +
        "try { $r.disks = @(Get-PhysicalDisk | ForEach-Object { [ordered]@{ name = [string]$_.FriendlyName; type = [string]$_.MediaType; " +
        "health = [string]$_.HealthStatus; size = [long]$_.Size } }) } catch { $r.disks = @() }; " +
        "$r | ConvertTo-Json -Depth 4 -Compress";

    /// <summary>Runs all the checks. The ones that fail appear as "not determinable".</summary>
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results = new List<CheckResult>();
        JsonObject? data = null;

        try
        {
            ProcessResult output = await ProcessRunner.RunPowerShellAsync(Script, 90000);
            string text = output.Output.Trim();
            int start = text.IndexOf('{');
            if (start >= 0)
            {
                data = JsonNode.Parse(text.Substring(start)) as JsonObject;
            }
        }
        catch
        {
            // Without data the checks are "not determinable".
        }

        results.Add(CheckRam(data));
        results.Add(CheckActivation(data));
        results.Add(CheckSecureBootAndTpm(data));
        results.Add(CheckTrim(data));
        results.Add(CheckDisks(data));
        results.Add(CheckBios());

        return results;
    }

    // ---------- RAM ----------

    private static CheckResult CheckRam(JsonObject? data)
    {
        const string title = "RAM profile (XMP/EXPO)";

        if (data?["ram"] is not JsonArray modules || modules.Count == 0)
        {
            return Unknown(title);
        }

        var parsed = modules.OfType<JsonObject>().ToList();
        if (parsed.Count == 0)
        {
            return Unknown(title);
        }

        int type = parsed.Max(m => ReadInt(m["type"]));
        int configured = parsed.Max(m => ReadInt(m["configured"]));
        int rated = parsed.Max(m => ReadInt(m["speed"]));
        int voltage = parsed.Max(m => ReadInt(m["voltage"]));
        long totalBytes = parsed.Sum(m => ReadLong(m["capacity"]));
        string partNumber = parsed.Select(m => ReadString(m["code"])).FirstOrDefault(c => c.Length > 0) ?? "";

        // 26 = DDR4, 34 = DDR5. Standard (JEDEC) speed and voltage, the highest common for each type.
        string typeName;
        int standardSpeed;
        int standardVoltage;
        switch (type)
        {
            case 34:
                typeName = "DDR5";
                standardSpeed = 5600;
                standardVoltage = 1100;
                break;
            case 26:
                typeName = "DDR4";
                standardSpeed = 3200;
                standardVoltage = 1200;
                break;
            default:
                typeName = "";
                standardSpeed = 0;
                standardVoltage = 0;
                break;
        }

        int speed = configured > 0 ? configured : rated;

        var parts = new List<string>();
        if (speed > 0)
        {
            parts.Add(speed + " MHz");
        }

        if (voltage > 0)
        {
            parts.Add((voltage / 1000.0).ToString("0.00") + " V");
        }

        if (typeName.Length > 0)
        {
            parts.Add(typeName);
        }

        if (totalBytes > 0)
        {
            parts.Add(parsed.Count + " x " + Math.Round(totalBytes / (double)parsed.Count / 1024 / 1024 / 1024) + " GB");
        }

        if (partNumber.Length > 0)
        {
            parts.Add(partNumber);
        }

        string detail = string.Join("  ·  ", parts);

        if (standardSpeed == 0 || speed == 0)
        {
            return new CheckResult(title, CheckLevel.Info, "Cannot be determined", detail);
        }

        bool fasterThanStandard = speed > standardSpeed;
        bool higherVoltage = voltage >= standardVoltage + 50;

        if (fasterThanStandard || higherVoltage)
        {
            return new CheckResult(title, CheckLevel.Ok, "Enabled", detail);
        }

        // The modules declare a higher speed than the one they run at: a clear sign the profile is off.
        string hint = rated > speed + 100
            ? "The modules advertise " + rated + " MHz but run at " + speed + ". Enable XMP/EXPO in the BIOS. "
            : "Speed and voltage are the standard ones: if the kit is faster, enable XMP/EXPO in the BIOS. ";

        return new CheckResult(title, CheckLevel.Warning, "Probably not enabled", hint + detail);
    }

    // ---------- Activation ----------

    private static CheckResult CheckActivation(JsonObject? data)
    {
        const string title = "Windows activation";

        if (data is null || data["license"] is not JsonNode node)
        {
            return Unknown(title);
        }

        int status = ReadInt(node);
        return status == 1
            ? new CheckResult(title, CheckLevel.Ok, "Activated", SystemInfo.WindowsName)
            : new CheckResult(title, CheckLevel.Warning, "Not activated", "Check in Settings, System, Activation.");
    }

    // ---------- Secure Boot and TPM ----------

    private static CheckResult CheckSecureBootAndTpm(JsonObject? data)
    {
        const string title = "Secure Boot and TPM";

        if (data is null)
        {
            return Unknown(title);
        }

        bool? secureBoot = ReadBool(data["secureBoot"]);
        bool? tpmPresent = ReadBool(data["tpmPresent"]);
        bool? tpmReady = ReadBool(data["tpmReady"]);

        string secureText = secureBoot switch
        {
            true => "Secure Boot enabled",
            false => "Secure Boot disabled",
            _ => "Secure Boot cannot be detected",
        };

        string tpmText = tpmPresent switch
        {
            true => tpmReady == true ? "TPM enabled" : "TPM present but not ready",
            false => "TPM missing or disabled",
            _ => "TPM cannot be detected",
        };

        bool allGood = secureBoot == true && tpmPresent == true && tpmReady == true;
        bool unknown = secureBoot is null && tpmPresent is null;

        if (unknown)
        {
            return Unknown(title);
        }

        return new CheckResult(
            title,
            allGood ? CheckLevel.Ok : CheckLevel.Warning,
            allGood ? "Enabled" : "To check",
            secureText + "  ·  " + tpmText + (allGood ? "" : ". They are enabled in the BIOS; some games and Windows 11 need them."));
    }

    // ---------- TRIM ----------

    private static CheckResult CheckTrim(JsonObject? data)
    {
        const string title = "SSD TRIM";

        string text = data is null ? "" : ReadString(data["trim"]);
        Match match = Regex.Match(text, @"DisableDeleteNotify\s*=\s*(\d)");
        if (!match.Success)
        {
            return Unknown(title);
        }

        // 0 = TRIM on (the "notification disable" is off).
        return match.Groups[1].Value == "0"
            ? new CheckResult(title, CheckLevel.Ok, "Enabled", "Windows tells SSDs which blocks are free.")
            : new CheckResult(title, CheckLevel.Warning, "Disabled", "Re-enable it with: fsutil behavior set DisableDeleteNotify 0");
    }

    // ---------- Disks ----------

    private static CheckResult CheckDisks(JsonObject? data)
    {
        const string title = "Disk health";

        if (data?["disks"] is not JsonArray disks || disks.Count == 0)
        {
            return Unknown(title);
        }

        var lines = new List<string>();
        bool allHealthy = true;

        foreach (JsonObject disk in disks.OfType<JsonObject>())
        {
            string name = ReadString(disk["name"]);
            string health = ReadString(disk["health"]);
            long size = ReadLong(disk["size"]);

            bool healthy = string.Equals(health, "Healthy", StringComparison.OrdinalIgnoreCase);
            allHealthy &= healthy;

            string sizeText = size > 0 ? " (" + Math.Round(size / 1000.0 / 1000 / 1000) + " GB)" : "";
            lines.Add(name + sizeText + (healthy ? "" : ": " + health));
        }

        return new CheckResult(
            title,
            allHealthy ? CheckLevel.Ok : CheckLevel.Warning,
            allHealthy ? "Healthy" : "To check",
            string.Join("  ·  ", lines));
    }

    // ---------- BIOS ----------

    private static CheckResult CheckBios()
    {
        const string title = "BIOS";

        string? version = RegistryUtil.GetString(RegistryHive.LocalMachine, BiosKey, "BIOSVersion")?.Trim();
        string? date = RegistryUtil.GetString(RegistryHive.LocalMachine, BiosKey, "BIOSReleaseDate")?.Trim();
        string? board = RegistryUtil.GetString(RegistryHive.LocalMachine, BiosKey, "BaseBoardProduct")?.Trim();

        if (string.IsNullOrWhiteSpace(version))
        {
            return Unknown(title);
        }

        return new CheckResult(
            title,
            CheckLevel.Info,
            "Version " + version,
            string.Join("  ·  ", new[] { string.IsNullOrWhiteSpace(date) ? "" : "dated " + date, board ?? "" }.Where(p => p.Length > 0)));
    }

    // ---------- Helpers ----------

    private static CheckResult Unknown(string title) =>
        new(title, CheckLevel.Info, "Cannot be determined", "Windows did not provide this information.");

    private static int ReadInt(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int number))
            {
                return number;
            }

            if (value.TryGetValue(out long big))
            {
                return (int)Math.Min(big, int.MaxValue);
            }
        }

        return 0;
    }

    private static long ReadLong(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out long number))
            {
                return number;
            }

            if (value.TryGetValue(out double floating))
            {
                return (long)floating;
            }
        }

        return 0;
    }

    private static string ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && text != null ? text.Trim() : "";

    private static bool? ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
}
