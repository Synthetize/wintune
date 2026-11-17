using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LHM = LibreHardwareMonitor.Hardware;

namespace WinTune.Core;

/// <summary>Kind of measurement shown on the Sensors page.</summary>
public enum SensorKind
{
    Temperature,
    Load,
    Clock,
    Power,
    Fan,
    Voltage,
    Data,
    SmallData,
}

/// <summary>A measurement read from a sensor.</summary>
public sealed record SensorReading(string Key, string Hardware, SensorKind Kind, string Name, float Value, float? Min, float? Max)
{
    public string Unit => UnitOf(Kind);

    public static string UnitOf(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Power => "W",
        SensorKind.Fan => "RPM",
        SensorKind.Voltage => "V",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        _ => "",
    };

    public static string KindLabel(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Temperature",
        SensorKind.Load => "Load",
        SensorKind.Clock => "Clock",
        SensorKind.Power => "Power",
        SensorKind.Fan => "Fan",
        SensorKind.Voltage => "Voltage",
        SensorKind.Data => "Memory",
        SensorKind.SmallData => "Memory",
        _ => "",
    };

    /// <summary>Value with the unit, for example "45 °C" or "1.25 V".</summary>
    public static string Format(SensorKind kind, float value)
    {
        string number = kind switch
        {
            SensorKind.Voltage => value.ToString("0.000"),
            SensorKind.Power => value.ToString("0.0"),
            SensorKind.Data => value.ToString("0.0"),
            SensorKind.Load => value.ToString("0.0"),
            _ => Math.Round(value).ToString("0"),
        };

        return number + " " + UnitOf(kind);
    }
}

/// <summary>A component (CPU, graphics card, disk...) with its measurements.</summary>
public sealed record HardwareReading(string Name, string TypeLabel, List<SensorReading> Sensors);

/// <summary>
/// Sensor reading with LibreHardwareMonitorLib. The program already runs as administrator,
/// so nothing else needs to be started. Some CPU, RAM and motherboard sensors need the
/// PawnIO driver: without it those sensors are missing but the others work.
/// </summary>
public static class SensorService
{
    private static readonly object Gate = new();
    private static LHM.Computer? _computer;

    static SensorService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Reset();
    }

    /// <summary>Message of the last error when opening the sensors, if there was one.</summary>
    public static string? OpenError { get; private set; }

    /// <summary>Closes and reopens the sensors at the next reading (for example after installing the driver).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                _computer?.Close();
            }
            catch (Exception)
            {
                // Closing failed: start again from zero anyway.
            }

            _computer = null;
            OpenError = null;
        }
    }

    /// <summary>Reads all the sensors. To be called off the interface thread: the first reading takes a few seconds.</summary>
    public static List<HardwareReading> Read()
    {
        lock (Gate)
        {
            if (_computer is null)
            {
                try
                {
                    var computer = new LHM.Computer
                    {
                        IsCpuEnabled = true,
                        IsGpuEnabled = true,
                        IsMemoryEnabled = true,
                        IsMotherboardEnabled = true,
                        IsStorageEnabled = true,
                    };

                    computer.Open();
                    _computer = computer;
                    OpenError = null;
                }
                catch (Exception ex)
                {
                    OpenError = ex.Message;
                    return new List<HardwareReading>();
                }
            }

            var result = new List<HardwareReading>();
            foreach (LHM.IHardware hardware in _computer.Hardware)
            {
                Collect(hardware, result);
            }

            return result
                .OrderBy(h => Rank(h.TypeLabel))
                .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static void Collect(LHM.IHardware hardware, List<HardwareReading> result)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception)
        {
            // A component that does not respond must not stop the reading of the others.
        }

        var sensors = new List<SensorReading>();
        foreach (LHM.ISensor sensor in hardware.Sensors)
        {
            SensorKind? kind = MapKind(sensor.SensorType);
            if (kind is null || sensor.Value is not float value || float.IsNaN(value))
            {
                continue;
            }

            sensors.Add(new SensorReading(
                sensor.Identifier.ToString(),
                hardware.Name,
                kind.Value,
                sensor.Name,
                value,
                sensor.Min,
                sensor.Max));
        }

        if (sensors.Count > 0)
        {
            sensors = sensors
                .OrderBy(s => (int)s.Kind)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Add(new HardwareReading(hardware.Name, TypeLabel(hardware.HardwareType.ToString()), sensors));
        }

        foreach (LHM.IHardware sub in hardware.SubHardware)
        {
            Collect(sub, result);
        }
    }

    private static SensorKind? MapKind(LHM.SensorType type) => type switch
    {
        LHM.SensorType.Temperature => SensorKind.Temperature,
        LHM.SensorType.Load => SensorKind.Load,
        LHM.SensorType.Clock => SensorKind.Clock,
        LHM.SensorType.Power => SensorKind.Power,
        LHM.SensorType.Fan => SensorKind.Fan,
        LHM.SensorType.Voltage => SensorKind.Voltage,
        LHM.SensorType.Data => SensorKind.Data,
        LHM.SensorType.SmallData => SensorKind.SmallData,
        _ => null,
    };

    private static string TypeLabel(string hardwareType) => hardwareType switch
    {
        "Cpu" => "CPU",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU",
        "Memory" => "Memory",
        "Motherboard" or "SuperIO" or "EmbeddedController" => "Motherboard",
        "Storage" => "Storage",
        _ => hardwareType,
    };

    private static int Rank(string typeLabel) => typeLabel switch
    {
        "CPU" => 0,
        "GPU" => 1,
        "Memory" => 2,
        "Motherboard" => 3,
        "Storage" => 4,
        _ => 5,
    };

    // ---------- Summary values ----------

    /// <summary>CPU temperature (the package value, if there is one); null if it cannot be read.</summary>
    public static float? FindCpuTemperature(List<HardwareReading> data)
    {
        List<SensorReading> temps = data
            .Where(h => h.TypeLabel == "CPU")
            .SelectMany(h => h.Sensors)
            .Where(s => s.Kind == SensorKind.Temperature)
            .ToList();

        foreach (string hint in new[] { "Package", "Tctl", "Tdie", "Core Max", "Average" })
        {
            SensorReading? match = temps.FirstOrDefault(s => s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Value;
            }
        }

        return temps.Count > 0 ? temps[0].Value : null;
    }

    /// <summary>Graphics card temperature (the core, if there is one); null if it cannot be read.</summary>
    public static float? FindGpuTemperature(List<HardwareReading> data)
    {
        List<SensorReading> temps = data
            .Where(h => h.TypeLabel == "GPU")
            .SelectMany(h => h.Sensors)
            .Where(s => s.Kind == SensorKind.Temperature)
            .ToList();

        SensorReading? core = temps.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
        if (core is not null)
        {
            return core.Value;
        }

        return temps.Count > 0 ? temps[0].Value : null;
    }
}

// ---------- Before/after snapshots ----------

public sealed class SnapshotEntry
{
    public string Key { get; set; } = "";

    public string Hardware { get; set; } = "";

    public string Name { get; set; } = "";

    public SensorKind Kind { get; set; }

    public float Value { get; set; }
}

public sealed class SensorSnapshot
{
    public string Name { get; set; } = "";

    public DateTime Taken { get; set; }

    public List<SnapshotEntry> Entries { get; set; } = new();

    /// <summary>File path; not part of the saved content.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Name + "  ·  " + Taken.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>Row of the comparison between a snapshot and the current values.</summary>
public sealed record SnapshotComparison(string Hardware, string Name, SensorKind Kind, float Then, float Now)
{
    public float Delta => Now - Then;
}

public static class SnapshotService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string Folder => Path.Combine(ProfileService.DataFolder, "snapshots");

    public static SensorSnapshot Save(string name, List<HardwareReading> data)
    {
        Directory.CreateDirectory(Folder);

        var snapshot = new SensorSnapshot
        {
            Name = name,
            Taken = DateTime.Now,
            Entries = data
                .SelectMany(h => h.Sensors)
                .Where(s => s.Kind != SensorKind.Voltage)
                .Select(s => new SnapshotEntry { Key = s.Key, Hardware = s.Hardware, Name = s.Name, Kind = s.Kind, Value = s.Value })
                .ToList(),
        };

        string path = Path.Combine(Folder, snapshot.Taken.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, Options));
        snapshot.FilePath = path;
        return snapshot;
    }

    public static List<SensorSnapshot> List()
    {
        var result = new List<SensorSnapshot>();
        if (!Directory.Exists(Folder))
        {
            return result;
        }

        foreach (string file in Directory.GetFiles(Folder, "*.json"))
        {
            try
            {
                SensorSnapshot? snapshot = JsonSerializer.Deserialize<SensorSnapshot>(File.ReadAllText(file));
                if (snapshot is not null)
                {
                    snapshot.FilePath = file;
                    result.Add(snapshot);
                }
            }
            catch (Exception)
            {
                // Damaged file: skip it.
            }
        }

        return result.OrderByDescending(s => s.Taken).ToList();
    }

    public static void Delete(SensorSnapshot snapshot)
    {
        if (File.Exists(snapshot.FilePath))
        {
            File.Delete(snapshot.FilePath);
        }
    }

    /// <summary>Compares the snapshot with the current values; the measurements present in both are kept.</summary>
    public static List<SnapshotComparison> Compare(SensorSnapshot snapshot, List<HardwareReading> current)
    {
        Dictionary<string, SensorReading> now = current
            .SelectMany(h => h.Sensors)
            .GroupBy(s => s.Key)
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<SnapshotComparison>();
        foreach (SnapshotEntry entry in snapshot.Entries)
        {
            if (now.TryGetValue(entry.Key, out SensorReading? reading))
            {
                rows.Add(new SnapshotComparison(entry.Hardware, entry.Name, entry.Kind, entry.Value, reading.Value));
            }
        }

        return rows
            .OrderBy(r => r.Hardware, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => (int)r.Kind)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
