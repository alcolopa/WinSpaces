using System.Net.Http.Headers;
using System.Text.Json;

namespace WindowsSpaces.Core;

/// <summary>A newer release found on the project's GitHub repo.</summary>
public sealed record UpdateInfo(string Version, string ReleaseUrl, string? InstallerUrl = null, long InstallerSize = 0, string? InstallerSha256 = null);

/// <summary>
/// Semantic-version parsing/ordering for release tags such as "v1.2.0" or
/// "0.0.1-beta" (prerelease sorts below the same release version).
/// </summary>
public readonly record struct SemVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        var prerelease = string.Empty;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0) return false;
        }

        version = new SemVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(SemVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // A release outranks any prerelease of the same number.
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;

        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = int.TryParse(a[i], out var an);
            var bNum = int.TryParse(b[i], out var bn);
            c = aNum && bNum ? an.CompareTo(bn)
                : aNum ? -1
                : bNum ? 1
                : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }

        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPrerelease ? $"-{Prerelease}" : string.Empty);
}

/// <summary>
/// Asks the GitHub Releases API whether a newer version than the running one
/// has been published. Never throws: any network/parse failure is "no update",
/// since an update check must never get in the way of the app itself.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly string _releasesUrl;

    public UpdateChecker(HttpClient http, string owner, string repo)
    {
        _http = http;
        _releasesUrl = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20";
    }

    /// <summary>
    /// Returns the newest release newer than <paramref name="currentVersion"/>,
    /// or null when up to date or the check failed. Prereleases are only
    /// offered to users already on a prerelease.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!SemVersion.TryParse(currentVersion, out var current)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsSpaces-UpdateCheck", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            UpdateInfo? best = null;
            SemVersion bestVersion = current;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (GetBool(release, "draft")) continue;
                if (!release.TryGetProperty("tag_name", out var tagElement)) continue;
                if (!SemVersion.TryParse(tagElement.GetString(), out var version)) continue;
                if (version.IsPrerelease && !current.IsPrerelease) continue;
                if (version.CompareTo(bestVersion) <= 0) continue;

                var url = release.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;

                bestVersion = version;
                best = FindInstaller(release) is { } asset
                    ? new UpdateInfo(version.ToString(), url, asset.Url, asset.Size, asset.Sha256)
                    : new UpdateInfo(version.ToString(), url);
            }

            return best;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (string Url, long Size, string? Sha256)? FindInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is null || url is null) continue;
            if (!name.StartsWith("WindowsSpacesSetup-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            // Only ever fetch an installer over HTTPS from GitHub itself.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))) continue;

            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha = digest["sha256:".Length..];
            }

            return (url, size, sha);
        }

        return null;
    }

    /// <summary>
    /// Downloads the release's installer into <paramref name="directory"/> and
    /// verifies size (and SHA-256 when GitHub published one). Returns the file
    /// path, or null on any failure; a partial or mismatched file is deleted.
    /// </summary>
    public async Task<string?> DownloadInstallerAsync(UpdateInfo update, string directory, CancellationToken cancellationToken = default)
    {
        if (update.InstallerUrl is null) return null;

        string? path = null;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, Path.GetFileName(new Uri(update.InstallerUrl).LocalPath));

            using var request = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsSpaces-UpdateCheck", "1.0"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using (var file = File.Create(path))
            {
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (update.InstallerSize > 0 && new FileInfo(path).Length != update.InstallerSize)
            {
                File.Delete(path);
                return null;
            }

            if (update.InstallerSha256 is not null)
            {
                await using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!hash.Equals(update.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    stream.Close();
                    File.Delete(path);
                    return null;
                }
            }

            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
            return null;
        }
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
