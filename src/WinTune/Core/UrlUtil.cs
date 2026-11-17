using System;
using System.Diagnostics;
using System.IO;

namespace WinTune.Core;

/// <summary>Opening links in the browser.</summary>
public static class UrlUtil
{
    /// <summary>True if the text is an http or https web address.</summary>
    public static bool IsWebLink(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Opens the link in the default browser. It goes through File Explorer, so the browser starts
    /// as a normal user and not with the WinTune administrator privileges.
    /// </summary>
    public static void Open(string url)
    {
        if (!IsWebLink(url))
        {
            throw new ArgumentException("Invalid address: " + url);
        }

        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, "\"" + url + "\"") { UseShellExecute = false })?.Dispose();
    }
}
