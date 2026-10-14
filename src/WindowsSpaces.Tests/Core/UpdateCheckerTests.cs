using System.Net;
using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class UpdateCheckerTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private static UpdateChecker Checker(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new StubHandler(status, json)), "o", "r");

    private static string Release(string tag, bool draft = false) =>
        $"{{\"tag_name\":\"{tag}\",\"draft\":{(draft ? "true" : "false")},\"html_url\":\"https://example/{tag}\"}}";

    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.0.2", "0.0.1")]
    [InlineData("1.0.0", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("v1.2.0", "1.1.9")]
    public void SemVersion_OrdersCorrectly(string higher, string lower)
    {
        Assert.True(SemVersion.TryParse(higher, out var h));
        Assert.True(SemVersion.TryParse(lower, out var l));
        Assert.True(h.CompareTo(l) > 0);
    }

    [Fact]
    public void SemVersion_IgnoresBuildMetadata_AndRejectsGarbage()
    {
        Assert.True(SemVersion.TryParse("1.2.3+abc", out var v));
        Assert.Equal("1.2.3", v.ToString());
        Assert.False(SemVersion.TryParse("not-a-version", out _));
        Assert.False(SemVersion.TryParse(null, out _));
    }

    [Fact]
    public async Task NewerRelease_IsReported()
    {
        var update = await Checker($"[{Release("v0.0.3-beta")},{Release("v0.0.2-beta")}]").CheckAsync("0.0.1-beta");
        Assert.NotNull(update);
        Assert.Equal("0.0.3-beta", update!.Version);
        Assert.Equal("https://example/v0.0.3-beta", update.ReleaseUrl);
    }

    [Fact]
    public async Task InstallerAsset_IsPicked_OnlyFromGitHubHttps()
    {
        const string good = "https://github.com/o/r/releases/download/v1.1.0/WindowsSpacesSetup-1.1.0.exe";
        var json = "[{\"tag_name\":\"v1.1.0\",\"html_url\":\"https://example/r\",\"assets\":[" +
                   "{\"name\":\"WindowsSpacesSetup-1.1.0.exe\",\"browser_download_url\":\"https://evil.example/x.exe\",\"size\":1}," +
                   $"{{\"name\":\"WindowsSpacesSetup-1.1.0.exe\",\"browser_download_url\":\"{good}\",\"size\":42,\"digest\":\"sha256:ABCD\"}}]}}]";

        var update = await Checker(json).CheckAsync("1.0.0");

        Assert.NotNull(update);
        Assert.Equal(good, update!.InstallerUrl);
        Assert.Equal(42, update.InstallerSize);
        Assert.Equal("ABCD", update.InstallerSha256);
    }

    [Fact]
    public async Task Download_WithWrongSize_IsRejected()
    {
        var update = new UpdateInfo("1.1.0", "https://example/r",
            "https://github.com/o/r/releases/download/v1.1.0/WindowsSpacesSetup-1.1.0.exe", 999);
        var dir = Path.Combine(Path.GetTempPath(), "ws-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(await Checker("binary").DownloadInstallerAsync(update, dir));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SameOrOlderRelease_ReturnsNull()
    {
        Assert.Null(await Checker($"[{Release("v0.0.1-beta")}]").CheckAsync("0.0.1-beta"));
        Assert.Null(await Checker($"[{Release("v0.0.1")}]").CheckAsync("0.0.2"));
    }

    [Fact]
    public async Task Prerelease_NotOfferedToStableUsers()
    {
        Assert.Null(await Checker($"[{Release("v2.0.0-beta")}]").CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task Drafts_AreIgnored()
    {
        Assert.Null(await Checker($"[{Release("v9.0.0", draft: true)}]").CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task ServerError_OrBadJson_ReturnsNull()
    {
        Assert.Null(await Checker("[]", HttpStatusCode.Forbidden).CheckAsync("1.0.0"));
        Assert.Null(await Checker("<html>", HttpStatusCode.OK).CheckAsync("1.0.0"));
    }
}
