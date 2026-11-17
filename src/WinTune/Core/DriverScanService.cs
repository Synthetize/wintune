using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinTune.Core;

/// <summary>A driver that Windows Update offers and that is not installed yet.</summary>
public sealed record DriverOffer(
    string Id,
    string Title,
    string Manufacturer,
    string Model,
    string DriverClass,
    string Date,
    long Size,
    bool RebootRequired);

/// <summary>Outcome of installing one driver.</summary>
public sealed record DriverInstallResult(string Id, bool Ok, bool RebootRequired, string Message);

/// <summary>
/// Looks for newer drivers in Windows Update (the same catalog Windows uses, with the matching done by Windows
/// from the hardware IDs) and installs the ones the user picks. Works for every manufacturer.
/// </summary>
public static class DriverScanService
{
    public static async Task<(List<DriverOffer> Offers, string? Error)> ScanAsync()
    {
        const string script = """
            $s = New-Object -ComObject Microsoft.Update.Session
            $searcher = $s.CreateUpdateSearcher()
            $searcher.ServerSelection = 2
            $r = $searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
            $list = @()
            foreach ($u in $r.Updates) {
                $date = ''
                try { $date = ([datetime]$u.DriverVerDate).ToString('yyyy-MM-dd') } catch { }
                $list += [pscustomobject]@{
                    Id = [string]$u.Identity.UpdateID
                    Title = [string]$u.Title
                    Manufacturer = [string]$u.DriverManufacturer
                    Model = [string]$u.DriverModel
                    Class = [string]$u.DriverClass
                    Date = $date
                    Size = [int64]$u.MaxDownloadSize
                    Reboot = [bool]$u.RebootRequired
                }
            }
            Write-Output ('JSON:' + (ConvertTo-Json -InputObject @($list) -Compress))
            """;

        try
        {
            ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 5 * 60 * 1000);
            JsonElement? json = ExtractJson(result.Output, out string? error);
            if (json is null)
            {
                return (new List<DriverOffer>(), error ?? "Windows Update did not answer.");
            }

            var offers = new List<DriverOffer>();
            foreach (JsonElement item in json.Value.EnumerateArray())
            {
                offers.Add(new DriverOffer(
                    Text(item, "Id"),
                    Text(item, "Title"),
                    Text(item, "Manufacturer"),
                    Text(item, "Model"),
                    Text(item, "Class"),
                    Text(item, "Date"),
                    item.TryGetProperty("Size", out JsonElement size) && size.TryGetInt64(out long bytes) ? bytes : 0,
                    item.TryGetProperty("Reboot", out JsonElement reboot) && reboot.ValueKind == JsonValueKind.True));
            }

            return (offers.OrderBy(o => o.DriverClass, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(o => o.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<DriverOffer>(), ex.Message);
        }
    }

    /// <summary>Downloads and installs the given drivers (by update ID), all in one operation.</summary>
    public static async Task<(List<DriverInstallResult> Results, string? Error)> InstallAsync(IEnumerable<string> ids)
    {
        // The IDs end up inside a script: only real GUIDs are accepted.
        List<string> safe = ids.Where(i => Guid.TryParse(i, out _)).Select(i => Guid.Parse(i).ToString()).ToList();
        if (safe.Count == 0)
        {
            return (new List<DriverInstallResult>(), "No valid driver selected.");
        }

        string idList = string.Join(",", safe.Select(i => "'" + i + "'"));
        string script = "$ids = @(" + idList + ")\n" + """
            $s = New-Object -ComObject Microsoft.Update.Session
            $searcher = $s.CreateUpdateSearcher()
            $searcher.ServerSelection = 2
            $r = $searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
            $coll = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($u in $r.Updates) {
                if ($ids -contains [string]$u.Identity.UpdateID) {
                    if (-not $u.EulaAccepted) { $u.AcceptEula() }
                    [void]$coll.Add($u)
                }
            }
            if ($coll.Count -eq 0) { Write-Output 'JSON:[]'; exit 0 }
            $d = $s.CreateUpdateDownloader()
            $d.Updates = $coll
            [void]$d.Download()
            $i = $s.CreateUpdateInstaller()
            $i.Updates = $coll
            $res = $i.Install()
            $out = @()
            for ($k = 0; $k -lt $coll.Count; $k++) {
                $ur = $res.GetUpdateResult($k)
                $out += [pscustomobject]@{
                    Id = [string]$coll.Item($k).Identity.UpdateID
                    Code = [int]$ur.ResultCode
                    Reboot = [bool]$ur.RebootRequired
                }
            }
            Write-Output ('JSON:' + (ConvertTo-Json -InputObject @($out) -Compress))
            """;

        try
        {
            ProcessResult result = await ProcessRunner.RunPowerShellAsync(script, 60 * 60 * 1000);
            JsonElement? json = ExtractJson(result.Output, out string? error);
            if (json is null)
            {
                return (new List<DriverInstallResult>(), error ?? "Windows Update did not answer.");
            }

            var results = new List<DriverInstallResult>();
            foreach (JsonElement item in json.Value.EnumerateArray())
            {
                int code = item.TryGetProperty("Code", out JsonElement c) && c.TryGetInt32(out int v) ? v : 0;
                bool reboot = item.TryGetProperty("Reboot", out JsonElement r) && r.ValueKind == JsonValueKind.True;
                bool ok = code is 2 or 3;
                results.Add(new DriverInstallResult(Text(item, "Id"), ok, reboot, ok ? "Installed" : DescribeCode(code)));
            }

            return (results, null);
        }
        catch (Exception ex)
        {
            return (new List<DriverInstallResult>(), ex.Message);
        }
    }

    private static string DescribeCode(int code) => code switch
    {
        4 => "Installation failed",
        5 => "Installation aborted",
        1 => "Installation did not finish",
        _ => "Windows Update result " + code,
    };

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>Finds the "JSON:" line of the script output; otherwise returns the error text.</summary>
    private static JsonElement? ExtractJson(string output, out string? error)
    {
        error = null;
        string[] lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        string? jsonLine = lines.LastOrDefault(l => l.StartsWith("JSON:", StringComparison.Ordinal));
        if (jsonLine is null)
        {
            string? errorLine = lines.FirstOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
            error = errorLine is null ? null : errorLine["ERROR:".Length..].Trim();
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonLine["JSON:".Length..]);
            JsonElement root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Array ? root : null;
        }
        catch (JsonException)
        {
            error = "The answer of Windows Update could not be read.";
            return null;
        }
    }
}
