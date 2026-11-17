using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinTune.Core;

/// <summary>Gets the icon of a program, as File Explorer shows it.</summary>
public static class IconUtil
{
    /// <summary>Icon for a command line (e.g. "C:\App\app.exe" --silent). Null if the file is not found.</summary>
    public static ImageSource? ForCommand(string command)
    {
        string? path = ExtractPath(command);
        return path is null ? null : ForFile(path);
    }

    public static ImageSource? ForFile(string path)
    {
        try
        {
            var info = default(NativeMethods.SHFILEINFO);
            IntPtr result = NativeMethods.SHGetFileInfo(
                path,
                0,
                ref info,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Path of the executable file inside a command line, if it exists.</summary>
    public static string? ExtractPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate = text;

        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end > 1)
            {
                candidate = text.Substring(1, end - 1);
            }
        }
        else
        {
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0)
            {
                candidate = text.Substring(0, exe + 4);
            }
        }

        try
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }

            return File.Exists(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }
}
