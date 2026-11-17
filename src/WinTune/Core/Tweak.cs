using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTune.Core;

public sealed record ChoiceOption(string Key, string Label);

/// <summary>
/// A single optimization. The value is always text: "true"/"false" for switches,
/// the option key for choices. So the profile and the change log treat everything the same way.
/// </summary>
public abstract class Tweak
{
    public required string Id { get; init; }

    public required string Category { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = "";

    /// <summary>The PC must be restarted for the change to take effect.</summary>
    public bool NeedsRestart { get; init; }

    /// <summary>File Explorer must be restarted for the change to take effect.</summary>
    public bool NeedsExplorerRestart { get; init; }

    public bool Windows11Only { get; init; }

    /// <summary>
    /// Entry managed by a dedicated page: it does not appear on the Optimizations page,
    /// but it is part of the profile and of the comparison on the Overview.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>Recommended value, used by "Apply recommended". Null if there is no recommendation.</summary>
    public string? Recommended { get; init; }

    /// <summary>
    /// Confirmation question to show before applying a value from the switch; null if not needed.
    /// For the few entries that start something long or delicate.
    /// </summary>
    public Func<string, string?>? Confirm { get; init; }

    /// <summary>Optional check: does the entry exist on this PC (e.g. is a service present)?</summary>
    public Func<bool>? IsPresent { get; init; }

    public bool IsAvailable
    {
        get
        {
            if (Windows11Only && !SystemInfo.IsWindows11)
            {
                return false;
            }

            try
            {
                return IsPresent?.Invoke() ?? true;
            }
            catch
            {
                return false;
            }
        }
    }

    public abstract string? ReadValue();

    public abstract void WriteValue(string value);

    /// <summary>Text to show for a value (e.g. "Disabled", "15 minutes").</summary>
    public abstract string DisplayValue(string? value);
}

/// <summary>On/off optimization.</summary>
public sealed class ToggleTweak : Tweak
{
    public required Func<bool> Read { get; init; }

    public required Action<bool> Write { get; init; }

    public string OnText { get; init; } = "Enabled";

    public string OffText { get; init; } = "Disabled";

    public static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public override string? ReadValue() => Read() ? "true" : "false";

    public override void WriteValue(string value) => Write(IsTrue(value));

    public override string DisplayValue(string? value) =>
        value is null ? "unknown" : IsTrue(value) ? OnText : OffText;
}

/// <summary>Optimization with a list of possible values.</summary>
public sealed class ChoiceTweak : Tweak
{
    public required Func<IReadOnlyList<ChoiceOption>> GetOptions { get; init; }

    public required Func<string?> Read { get; init; }

    public required Action<string> Write { get; init; }

    public override string? ReadValue() => Read();

    public override void WriteValue(string value) => Write(value);

    public override string DisplayValue(string? value)
    {
        if (value is null)
        {
            return "unknown";
        }

        try
        {
            ChoiceOption? option = GetOptions()
                .FirstOrDefault(o => string.Equals(o.Key, value, StringComparison.OrdinalIgnoreCase));
            return option?.Label ?? value;
        }
        catch
        {
            return value;
        }
    }
}
