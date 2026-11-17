using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinTune.Core;

public sealed record PowerPlan(string Guid, string Name);

/// <summary>Power plans and sleep timings, read from the APIs and set with powercfg.</summary>
internal static class PowerUtil
{
    public static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid VideoIdleTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    public static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid StandbyIdleTimeout = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    private static readonly Regex PlanLine = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.*)\)",
        RegexOptions.Compiled);

    private static string PowerCfg => ProcessRunner.SystemTool("powercfg.exe");

    public static Guid? GetActiveScheme()
    {
        if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            NativeMethods.LocalFree(pointer);
        }
    }

    /// <summary>Value on mains power, in seconds. Null if not readable.</summary>
    public static uint? ReadAcSeconds(Guid subgroup, Guid setting)
    {
        Guid? scheme = GetActiveScheme();
        if (scheme is null)
        {
            return null;
        }

        Guid schemeGuid = scheme.Value;
        uint result = NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroup, ref setting, out uint value);
        return result == 0 ? value : null;
    }

    /// <summary>Minutes as text ("15"), "0" for never. Null if not readable.</summary>
    public static string? ReadAcMinutes(Guid subgroup, Guid setting)
    {
        uint? seconds = ReadAcSeconds(subgroup, setting);
        return seconds is null ? null : (seconds.Value / 60).ToString();
    }

    public static void SetMonitorTimeout(string minutes) =>
        ProcessRunner.RunOrThrow(PowerCfg, "/change monitor-timeout-ac " + ParseMinutes(minutes));

    public static void SetStandbyTimeout(string minutes) =>
        ProcessRunner.RunOrThrow(PowerCfg, "/change standby-timeout-ac " + ParseMinutes(minutes));

    public static void SetHibernate(bool enabled) =>
        ProcessRunner.RunOrThrow(PowerCfg, "/hibernate " + (enabled ? "on" : "off"));

    public static List<PowerPlan> ListPlans()
    {
        var plans = new List<PowerPlan>();
        ProcessResult result = ProcessRunner.Run(PowerCfg, "/list");

        foreach (string line in result.Output.Split('\n'))
        {
            Match match = PlanLine.Match(line);
            if (match.Success)
            {
                plans.Add(new PowerPlan(match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value.Trim()));
            }
        }

        return plans;
    }

    public static void SetActivePlan(string guid)
    {
        if (!Guid.TryParse(guid, out Guid parsed))
        {
            throw new ArgumentException("Invalid power plan: " + guid);
        }

        ProcessRunner.RunOrThrow(PowerCfg, "/setactive " + parsed.ToString("D"));
    }

    /// <summary>Name WinTune gives to the "Ultimate Performance" plan it creates, the same in all Windows languages.</summary>
    public const string UltimatePlanName = "Ultimate Performance";

    private const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private static readonly Regex AnyGuid = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    public static PowerPlan? FindUltimatePlan() =>
        ListPlans().FirstOrDefault(p => string.Equals(p.Name, UltimatePlanName, StringComparison.OrdinalIgnoreCase));

    public static bool IsUltimateActive()
    {
        PowerPlan? plan = FindUltimatePlan();
        Guid? active = GetActiveScheme();
        return plan is not null && active is not null && Guid.TryParse(plan.Guid, out Guid id) && id == active.Value;
    }

    /// <summary>Creates the plan (if it does not exist) and activates it.</summary>
    public static void EnableUltimate()
    {
        PowerPlan? plan = FindUltimatePlan();
        string guid;

        if (plan is null)
        {
            ProcessResult result = ProcessRunner.Run(PowerCfg, "/duplicatescheme " + UltimateTemplate);
            Match match = AnyGuid.Match(result.Output);
            if (!result.Success || !match.Success)
            {
                throw new InvalidOperationException("This PC does not offer the Ultimate Performance plan. " + result.Summary);
            }

            guid = match.Value.ToLowerInvariant();
            ProcessRunner.RunOrThrow(PowerCfg, "/changename " + guid + " \"" + UltimatePlanName + "\"");
        }
        else
        {
            guid = plan.Guid;
        }

        SetActivePlan(guid);
    }

    /// <summary>Goes back to the Balanced plan and deletes the plan created by WinTune.</summary>
    public static void DisableUltimate()
    {
        PowerPlan? plan = FindUltimatePlan();
        if (plan is null)
        {
            return;
        }

        if (IsUltimateActive())
        {
            ProcessRunner.RunOrThrow(PowerCfg, "/setactive " + BalancedPlan);
        }

        ProcessRunner.RunOrThrow(PowerCfg, "/delete " + plan.Guid);
    }

    private static int ParseMinutes(string minutes)
    {
        if (!int.TryParse(minutes, out int value) || value < 0 || value > 6000)
        {
            throw new ArgumentException("Invalid number of minutes: " + minutes);
        }

        return value;
    }
}
