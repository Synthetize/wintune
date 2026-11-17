using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinTune.Core;

/// <summary>A connected display, with the current refresh rate and the maximum at the resolution in use.</summary>
public sealed record DisplayInfo(string DeviceName, int Number, int Width, int Height, int CurrentHz, int MaxHz)
{
    public bool IsAtMax => CurrentHz >= MaxHz;
}

/// <summary>Reads and sets the refresh rate of the displays.</summary>
public static class DisplayService
{
    public static List<DisplayInfo> List()
    {
        var displays = new List<DisplayInfo>();
        int number = 0;

        for (uint index = 0; index < 16; index++)
        {
            var device = default(NativeMethods.DISPLAY_DEVICE);
            device.cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();

            if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
            {
                continue;
            }

            NativeMethods.DEVMODE current = NewDevMode();
            if (!NativeMethods.EnumDisplaySettings(device.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref current))
            {
                continue;
            }

            // Highest rate among the modes with the same resolution and color depth.
            int max = current.dmDisplayFrequency;
            for (int mode = 0; mode < 2000; mode++)
            {
                NativeMethods.DEVMODE candidate = NewDevMode();
                if (!NativeMethods.EnumDisplaySettings(device.DeviceName, mode, ref candidate))
                {
                    break;
                }

                bool sameMode = candidate.dmPelsWidth == current.dmPelsWidth
                    && candidate.dmPelsHeight == current.dmPelsHeight
                    && candidate.dmBitsPerPel == current.dmBitsPerPel
                    && (candidate.dmDisplayFlags & NativeMethods.DM_INTERLACED) == 0;

                if (sameMode && candidate.dmDisplayFrequency > max)
                {
                    max = candidate.dmDisplayFrequency;
                }
            }

            number++;
            displays.Add(new DisplayInfo(
                device.DeviceName,
                number,
                current.dmPelsWidth,
                current.dmPelsHeight,
                current.dmDisplayFrequency,
                max));
        }

        return displays;
    }

    /// <summary>Sets the refresh rate of a display leaving everything else unchanged.</summary>
    public static void SetFrequency(string deviceName, int hertz)
    {
        NativeMethods.DEVMODE mode = NewDevMode();
        if (!NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode))
        {
            throw new InvalidOperationException("Could not read the display settings.");
        }

        int before = mode.dmDisplayFrequency;
        mode.dmDisplayFrequency = hertz;
        mode.dmFields = NativeMethods.DM_DISPLAYFREQUENCY;

        int result = NativeMethods.ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, NativeMethods.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result != NativeMethods.DISP_CHANGE_SUCCESSFUL)
        {
            throw new InvalidOperationException("Windows rejected the requested refresh rate (code " + result + ").");
        }

        TweakEngine.LogChange("schermo.frequenza", before + " Hz", hertz + " Hz");
    }

    private static NativeMethods.DEVMODE NewDevMode()
    {
        var mode = default(NativeMethods.DEVMODE);
        mode.dmSize = (short)Marshal.SizeOf<NativeMethods.DEVMODE>();
        return mode;
    }
}
