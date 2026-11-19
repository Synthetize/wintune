using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinTune.Core;

/// <summary>The latest release published on GitHub.</summary>
public sealed record ReleaseInfo(string Tag, Version Version, string Notes, string PageUrl, string ExeUrl, string? Sha256);

/// <summary>
/// Updates WinTune from the latest GitHub release: downloads the WinTune.exe attached to it, checks its SHA-256,
/// swaps it with the running file (a running exe can be renamed, not deleted) and restarts.
/// </summary>
public static class SelfUpdateService
{
    public const string Repository = "Synthetize/wintune";
    private const string ExeAsset = "WinTune.exe";
    private const string HashAsset = "WinTune.exe.sha256";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinTune-updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Version of the running program (from the project file).</summary>
    public static Version CurrentVersion =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>Removes the old exe left by the previous update. Called at startup.</summary>
    public static void CleanupOldFile()
    {
        try
        {
            string old = ExePath + ".old";
            if (ExePath.Length > 0 && File.Exists(old))
            {
                File.Delete(old);
            }
        }
        catch
        {
            // The file may still be in use for a moment: it is removed at the next start.
        }
    }

    /// <summary>Asks GitHub for the latest release. Null if there is none; throws if GitHub cannot be reached.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync()
    {
        using HttpResponseMessage response = await Http.GetAsync("https://api.github.com/repos/" + Repository + "/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version))
        {
            return null;
        }

        string? exeUrl = null;
        string? hashUrl = null;
        string? digest = null;

        if (root.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? "" : "";

                if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = url;
                    if (asset.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String)
                    {
                        string text = d.GetString() ?? "";
                        digest = text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? text["sha256:".Length..] : null;
                    }
                }
                else if (string.Equals(name, HashAsset, StringComparison.OrdinalIgnoreCase))
                {
                    hashUrl = url;
                }
            }
        }

        if (exeUrl is null || !IsGitHubUrl(exeUrl))
        {
            return null;
        }

        string? sha = digest;
        if (sha is null && hashUrl is not null && IsGitHubUrl(hashUrl))
        {
            string text = await Http.GetStringAsync(hashUrl);
            sha = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        string notes = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : "";

        return new ReleaseInfo(tag, Normalize(version), notes, page, exeUrl, sha);
    }

    public static bool IsNewer(ReleaseInfo release) => release.Version > CurrentVersion;

    private static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads, checks and installs the release, then restarts WinTune. Returns an error message,
    /// or null if everything went well (the program is about to restart).
    /// </summary>
    public static async Task<string?> InstallAsync(ReleaseInfo release)
    {
        if (ExePath.Length == 0 || !File.Exists(ExePath))
        {
            return "The path of the running program is not known.";
        }

        if (string.IsNullOrWhiteSpace(release.Sha256))
        {
            return "The release has no SHA-256 hash, so the file cannot be verified. Nothing was installed.";
        }

        string newPath = ExePath + ".new";
        string oldPath = ExePath + ".old";

        try
        {
            using (HttpResponseMessage response = await Http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync();
                await using FileStream target = File.Create(newPath);
                await source.CopyToAsync(target);
            }

            string actual;
            await using (FileStream check = File.OpenRead(newPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check));
            }

            if (!string.Equals(actual, release.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(newPath);
                return "The downloaded file does not match the published hash. Nothing was installed.";
            }

            if (new FileInfo(newPath).Length < 1024 * 1024)
            {
                File.Delete(newPath);
                return "The downloaded file is too small to be WinTune. Nothing was installed.";
            }

            // The running exe is renamed (allowed), the new one takes its place; if that fails, the old one comes back.
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            File.Move(ExePath, oldPath);
            try
            {
                File.Move(newPath, ExePath);
            }
            catch
            {
                File.Move(oldPath, ExePath);
                throw;
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(newPath))
                {
                    File.Delete(newPath);
                }
            }
            catch
            {
                // Leftover download: removed at the next try.
            }

            return "The update failed: " + ex.Message;
        }

        Restart();
        return null;
    }

    /// <summary>Starts the new exe a moment after this one has exited (only one copy can run at a time).</summary>
    private static void Restart()
    {
        var info = new ProcessStartInfo(
            Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\cmd.exe",
            "/c ping -n 4 127.0.0.1 >nul & start \"\" \"" + ExePath + "\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process.Start(info)?.Dispose();

        App.IsExiting = true;
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }
}
