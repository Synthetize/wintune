using System;
using System.Windows.Controls;

namespace WinTune.Views;

/// <summary>
/// Reads the duration chosen in the Drivers and Updates pages: a drop-down list
/// (the items have Tag "none", "7d", "14d", "1", "2", "3" months, "date") plus a date picker.
/// </summary>
public static class DurationHelper
{
    /// <summary>True if the chosen item is "Until a date".</summary>
    public static bool IsCustomDate(ComboBox box) => Tag(box) == "date";

    /// <summary>True if the chosen item is "No expiry".</summary>
    public static bool IsUnlimited(ComboBox box) => Tag(box) == "none";

    /// <summary>
    /// Chosen expiry date, or null for "No expiry".
    /// If the choice is not valid it returns false and explains why in "error".
    /// </summary>
    public static bool TryResolve(ComboBox box, DatePicker picker, out DateTime? until, out string error)
    {
        until = null;
        error = "";

        switch (Tag(box))
        {
            case "none":
                return true;

            case "7d":
                until = DateTime.Today.AddDays(7);
                return true;

            case "14d":
                until = DateTime.Today.AddDays(14);
                return true;

            case "1":
            case "2":
            case "3":
                until = DateTime.Today.AddMonths(int.Parse(Tag(box)));
                return true;

            case "date":
                if (!picker.SelectedDate.HasValue)
                {
                    error = "Pick a date.";
                    return false;
                }

                if (picker.SelectedDate.Value.Date <= DateTime.Today)
                {
                    error = "The date must be after today.";
                    return false;
                }

                until = picker.SelectedDate.Value.Date;
                return true;

            default:
                error = "Pick a duration.";
                return false;
        }
    }

    private static string Tag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
}
