using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace WinTune.Core;

/// <summary>A Windows event linked to crashes, shutdowns or instability.</summary>
public sealed record SystemEvent(
    DateTime Time,
    string Category,
    string Source,
    string Explanation,
    string Message,
    int Count);

/// <summary>
/// Reads from the Windows event log only the events useful to understand crashes, shutdowns and instability.
/// Uses wevtutil with XML output, which does not depend on the Windows language.
/// </summary>
public static class EventLogService
{
    public const string Shutdowns = "Shutdowns";
    public const string BlueScreens = "Blue screens";
    public const string Hardware = "Hardware errors";
    public const string Temperature = "Temperature";
    public const string GraphicsCard = "Graphics card";
    public const string Disks = "Disks";
    public const string Memory = "Memory";
    public const string DriverServices = "Drivers & services";
    public const string Applications = "Applications";
    public const string Changes = "What changed";

    /// <summary>Categories in the order they are shown.</summary>
    public static IReadOnlyList<string> Categories { get; } = new[]
    {
        Shutdowns, BlueScreens, Hardware, Temperature, GraphicsCard, Disks, Memory, DriverServices, Applications, Changes,
    };

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>
    /// Rule: log, source, ID (empty = all), category, explanation.
    /// With "OnlyProblems" only warnings and errors are taken, for sources whose IDs are all read.
    /// </summary>
    private sealed record Rule(string Log, string Provider, int[] Ids, string Category, string Explanation, bool OnlyProblems = false);

    private static readonly Rule[] Rules =
    {
        new("System", "Microsoft-Windows-Kernel-Power", new[] { 41 }, Shutdowns, "Restart without a proper shutdown: freeze, reset or power loss"),
        new("System", "EventLog", new[] { 6008 }, Shutdowns, "Unexpected system shutdown"),
        new("System", "User32", new[] { 1074, 1076 }, Shutdowns, "Shutdown or restart requested by a program or by the user"),

        new("System", "Microsoft-Windows-WER-SystemErrorReporting", new[] { 1001 }, BlueScreens, "Blue screen: the code is in the message"),
        new("System", "volmgr", new[] { 161 }, BlueScreens, "Crash dump file not created"),

        new("System", "Microsoft-Windows-WHEA-Logger", Array.Empty<int>(), Hardware, "Hardware error reported by the processor, memory or motherboard: typical of an unstable overclock or undervolt"),

        new("System", "Microsoft-Windows-Kernel-Processor-Power", new[] { 37 }, Temperature, "Processor speed limited by the firmware"),

        new("System", "Display", new[] { 4101 }, GraphicsCard, "The graphics driver stopped responding and was recovered"),
        new("System", "amdwddmg", Array.Empty<int>(), GraphicsCard, "AMD graphics driver error", true),
        new("System", "amdkmdag", Array.Empty<int>(), GraphicsCard, "AMD graphics driver error", true),
        new("System", "nvlddmkm", Array.Empty<int>(), GraphicsCard, "NVIDIA graphics driver error", true),

        new("System", "disk", new[] { 7, 11, 51, 153 }, Disks, "Read or write error on a disk"),
        new("System", "stornvme", new[] { 11, 129 }, Disks, "The NVMe drive did not respond and was reset"),
        new("System", "storahci", new[] { 129 }, Disks, "The SATA drive did not respond and was reset"),
        new("System", "Ntfs", new[] { 55 }, Disks, "Damaged file system: a disk check is needed"),

        new("System", "Microsoft-Windows-MemoryDiagnostics-Results", new[] { 1101, 1102, 1201, 1202 }, Memory, "Result of the Windows Memory Diagnostic"),
        new("System", "Microsoft-Windows-Resource-Exhaustion-Detector", new[] { 2004 }, Memory, "Virtual memory almost exhausted"),

        new("System", "Service Control Manager", new[] { 7000, 7001, 7031, 7034 }, DriverServices, "Service failed to start or stopped unexpectedly"),
        new("System", "Microsoft-Windows-Kernel-PnP", new[] { 219 }, DriverServices, "Driver not loaded for a device"),

        new("System", "Microsoft-Windows-WindowsUpdateClient", new[] { 19 }, Changes, "Update installed"),
        new("System", "Microsoft-Windows-WindowsUpdateClient", new[] { 20 }, Changes, "An update failed to install"),

        new("Application", "Application Error", new[] { 1000 }, Applications, "A program closed because of an error"),
        new("Application", "Application Hang", new[] { 1002 }, Applications, "A program stopped responding"),
    };

    /// <summary>Events of the last given days, most recent first.</summary>
    public static async Task<List<SystemEvent>> ReadAsync(int days)
    {
        var events = new List<SystemEvent>();

        foreach (IGrouping<string, Rule> log in Rules.GroupBy(r => r.Log))
        {
            events.AddRange(await ReadLogAsync(log.Key, log.ToList(), days));
        }

        return Collapse(events.OrderByDescending(e => e.Time).ToList());
    }

    /// <summary>Blue-screen dump files in C:\Windows\Minidump.</summary>
    public static (int Count, DateTime? Latest, string Folder) GetMinidumps()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

        try
        {
            if (!Directory.Exists(folder))
            {
                return (0, null, folder);
            }

            List<FileInfo> files = new DirectoryInfo(folder).GetFiles("*.dmp").ToList();
            DateTime? latest = null;
            if (files.Count > 0)
            {
                latest = files.Max(f => f.LastWriteTime);
            }

            return (files.Count, latest, folder);
        }
        catch
        {
            return (0, null, folder);
        }
    }

    private static async Task<List<SystemEvent>> ReadLogAsync(string log, List<Rule> rules, int days)
    {
        var events = new List<SystemEvent>();
        long milliseconds = days * 24L * 60 * 60 * 1000;

        var clauses = new List<string>();
        foreach (Rule rule in rules)
        {
            string clause = "Provider[@Name='" + rule.Provider + "']";

            if (rule.Ids.Length > 0)
            {
                clause += " and (" + string.Join(" or ", rule.Ids.Select(id => "EventID=" + id)) + ")";
            }
            else if (rule.OnlyProblems)
            {
                clause += " and (Level=1 or Level=2 or Level=3)";
            }

            clauses.Add("(" + clause + ")");
        }

        string query =
            "*[System[TimeCreated[timediff(@SystemTime) <= " + milliseconds + "] and (" +
            string.Join(" or ", clauses) + ")]]";

        string arguments = "qe " + log + " /q:\"" + query + "\" /f:RenderedXml /e:Events /c:600 /rd:true";
        string wevtutil = ProcessRunner.SystemTool("wevtutil.exe");

        // First as Unicode, to get messages with the right accents; if the output is not readable, try again without.
        XDocument? document = null;
        try
        {
            ProcessResult unicode = await ProcessRunner.RunAsync(wevtutil, arguments + " /uni:true", 120000, Encoding.Unicode);
            document = ParseEvents(unicode.Output);

            if (document is null)
            {
                ProcessResult plain = await ProcessRunner.RunAsync(wevtutil, arguments, 120000, Encoding.UTF8);
                document = ParseEvents(plain.Output);
            }
        }
        catch
        {
            return events;
        }

        if (document?.Root is null)
        {
            return events;
        }

        foreach (XElement element in document.Root.Elements(Ns + "Event"))
        {
            try
            {
                XElement? system = element.Element(Ns + "System");
                if (system is null)
                {
                    continue;
                }

                string provider = system.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "";
                if (!int.TryParse(system.Element(Ns + "EventID")?.Value, out int id))
                {
                    continue;
                }

                string? timeText = system.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
                if (!DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime time))
                {
                    continue;
                }

                Rule? rule = rules.FirstOrDefault(r =>
                    string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase)
                    && (r.Ids.Length == 0 || r.Ids.Contains(id)));

                if (rule is null)
                {
                    continue;
                }

                string message = element.Element(Ns + "RenderingInfo")?.Element(Ns + "Message")?.Value?.Trim() ?? "";
                if (message.Length == 0)
                {
                    // Without ready-made text the raw event data is shown.
                    message = string.Join("  ·  ", element.Descendants(Ns + "Data")
                        .Select(d => d.Value.Trim())
                        .Where(v => v.Length > 0)
                        .Take(8));
                }

                events.Add(new SystemEvent(
                    time.ToLocalTime(),
                    rule.Category,
                    ShortName(provider) + " " + id,
                    rule.Explanation,
                    message,
                    1));
            }
            catch
            {
                // An unreadable event is skipped.
            }
        }

        return events;
    }

    /// <summary>Turns the wevtutil output into an XML document. Null if it contains no readable events.</summary>
    private static XDocument? ParseEvents(string output)
    {
        int start = output.IndexOf("<Event", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        string xml = output.Substring(start);

        // If wevtutil did not add the root element, it is added here.
        if (!xml.StartsWith("<Events", StringComparison.Ordinal))
        {
            xml = "<Events>" + xml + "</Events>";
        }

        try
        {
            return XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Merges identical events close together (same source, within two minutes) into a single row with the count:
    /// some events appear once for every processor core.
    /// </summary>
    private static List<SystemEvent> Collapse(List<SystemEvent> events)
    {
        var collapsed = new List<SystemEvent>();

        foreach (SystemEvent current in events)
        {
            SystemEvent? last = collapsed.Count > 0 ? collapsed[collapsed.Count - 1] : null;

            if (last != null
                && last.Source == current.Source
                && last.Category == current.Category
                && (last.Time - current.Time).Duration() <= TimeSpan.FromMinutes(2))
            {
                collapsed[collapsed.Count - 1] = last with { Count = last.Count + 1 };
            }
            else
            {
                collapsed.Add(current);
            }
        }

        return collapsed;
    }

    private static string ShortName(string provider) =>
        provider.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase)
            ? provider.Substring("Microsoft-Windows-".Length)
            : provider;
}
