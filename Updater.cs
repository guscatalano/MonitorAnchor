using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MonitorAnchor;

/// <summary>Checks GitHub releases for a newer build, downloads it, and swaps the running exe in place.</summary>
public static class Updater
{
    public const string RepoUrl = "https://github.com/guscatalano/MonitorAnchor";
    private const string LatestApi = "https://api.github.com/repos/guscatalano/MonitorAnchor/releases/latest";

    /// <summary>Release asset that matches how this build was published: MSIX when packaged, else the exe flavour set by the csproj.</summary>
    public static string AssetName => Packaged.IsPackaged ? "MonitorAnchor.msix" : ExeAssetName;

#if SELF_CONTAINED
    private const string ExeAssetName = "MonitorAnchor-selfcontained.exe";
#else
    private const string ExeAssetName = "MonitorAnchor.exe";
#endif

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public sealed record Release(Version Version, string Tag, string PageUrl, string AssetUrl, long Size, string? Sha256);

    /// <summary>Returns the latest release, or null if it could not be read or has no matching asset.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient();
        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi, ct));
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? RepoUrl : RepoUrl;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
            {
                string digest = d.GetString() ?? "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..];
            }
            return new Release(version, tag, page, url, size, sha);
        }
        return null;
    }

    /// <summary>Downloads the release asset next to the data folder and verifies it. Returns the file path.</summary>
    public static async Task<string> DownloadAsync(Release release, CancellationToken ct = default)
    {
        string dir = Path.Combine(DisplayProfile.ConfigDir, "update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"MonitorAnchor-{release.Version}{Path.GetExtension(AssetName)}");

        using (var http = NewClient())
        {
            var bytes = await http.GetByteArrayAsync(release.AssetUrl, ct);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }

        var info = new FileInfo(path);
        if (release.Size > 0 && info.Length != release.Size)
            throw new InvalidOperationException($"download size {info.Length} does not match release asset size {release.Size}");
        if (release.Sha256 != null)
        {
            await using var fs = File.OpenRead(path);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("download checksum does not match the release");
        }
        return path;
    }

    /// <summary>
    /// Replaces the running exe with <paramref name="newExe"/> and starts it. A running exe cannot be
    /// overwritten but it can be renamed, so the old file is parked as *.old and cleaned up on next start.
    /// Returns true when the new process was started; the caller should then exit.
    /// </summary>
    public static bool SwapAndRestart(string newExe)
    {
        if (Packaged.IsPackaged)
        {
            // The package folder is read-only; hand the new MSIX to App Installer, which upgrades in place.
            Process.Start(new ProcessStartInfo(newExe) { UseShellExecute = true });
            Log.Write($"Update downloaded to {newExe}; App Installer opened to apply it");
            return false;
        }
        string current = Environment.ProcessPath ?? throw new InvalidOperationException("cannot determine own path");
        string parked = current + ".old";

        if (File.Exists(parked)) File.Delete(parked);
        File.Move(current, parked);
        try
        {
            File.Move(newExe, current);
        }
        catch
        {
            File.Move(parked, current); // put the old one back
            throw;
        }

        Process.Start(new ProcessStartInfo(current, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false });
        Log.Write($"Update installed: {current} replaced, new instance starting");
        return true;
    }

    /// <summary>Deletes the parked previous version, if any. Called at startup.</summary>
    public static void CleanupOld()
    {
        try
        {
            string? current = Environment.ProcessPath;
            if (current != null && File.Exists(current + ".old")) File.Delete(current + ".old");
            string dir = Path.Combine(DisplayProfile.ConfigDir, "update");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // still in use by the exiting instance; next start will get it
        }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MonitorAnchor", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
