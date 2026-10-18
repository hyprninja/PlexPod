using System.Net;
using RockPlex.Plex;
using RockPlex.Plex.Http;
using RockPlex.Plex.Library;
using RockPlex.Plex.Servers;
using Microsoft.Extensions.Logging.Abstractions;
using static RockPlex.Tests.Plex.FakeHttpHandler;

namespace RockPlex.Tests.Plex;

/// <summary>The account token only ever goes to plex.tv over HTTPS; servers only see their own token.</summary>
public sealed class TokenScopeTests
{
    private static async Task<RecordedRequest> SendAsync(string url, string? explicitToken = null)
    {
        var fake = new FakeHttpHandler(_ => Status(HttpStatusCode.OK));
        using var client = new HttpClient(PlexTestSetup.Pipeline(fake, "SECRET-ACCOUNT-TOKEN"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (explicitToken is not null)
        {
            request.Headers.TryAddWithoutValidation(PlexHeadersHandler.TokenHeader, explicitToken);
        }

        using var response = await client.SendAsync(request);
        return Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData("https://plex.tv/api/v2/resources")]
    [InlineData("https://clients.plex.tv/api/v2/auth/token")]
    public async Task PlexTv_GetsTheAccountToken(string url) =>
        Assert.Equal("SECRET-ACCOUNT-TOKEN", (await SendAsync(url)).Headers["X-Plex-Token"]);

    [Theory]
    [InlineData("http://192.168.1.20:32400/identity")]
    [InlineData("https://192-168-1-20.abc.plex.direct:32400/identity")]
    [InlineData("http://plex.tv/api/v2/user")]
    [InlineData("https://plex.tv.evil.example/api")]
    [InlineData("https://evilplex.tv/api")]
    public async Task OtherHosts_NeverGetTheAccountToken(string url) =>
        Assert.False((await SendAsync(url)).Headers.ContainsKey("X-Plex-Token"));

    [Fact]
    public async Task AnExplicitServerToken_IsLeftAlone() =>
        Assert.Equal("server-token", (await SendAsync("http://192.168.1.20:32400/library/sections", "server-token")).Headers["X-Plex-Token"]);

    [Fact]
    public async Task ManualServerUrl_ProbeCarriesNoToken()
    {
        var fake = new FakeHttpHandler(_ => Status(HttpStatusCode.OK));
        var discovery = new PlexServerDiscovery(new FakeHttpClientFactory(PlexTestSetup.Pipeline(fake, "SECRET-ACCOUNT-TOKEN")), PlexTestSetup.Options, NullLogger<PlexServerDiscovery>.Instance);
        var server = new PlexServer("S", "m", "SECRET-SERVER-TOKEN", true, []);

        var connection = await discovery.SelectConnectionAsync(server, new Uri("http://typo-or-stranger.example:32400/"), CancellationToken.None);

        Assert.NotNull(connection);
        Assert.False(Assert.Single(fake.Requests).Headers.ContainsKey("X-Plex-Token"));
    }

    private static PlexLibraryClient Library(FakeHttpHandler fake, PlexClientOptions options) =>
        new(new HttpClient(PlexTestSetup.Pipeline(fake)), new PlexServerConnection(new Uri("http://192.168.1.20:32400/"), "t", "m", "S"), options, NullLogger<PlexLibraryClient>.Instance);

    [Fact]
    public async Task DownloadWithUnknownSize_IsCapped()
    {
        var fake = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[500]) });
        var options = new PlexClientOptions { ClientIdentifier = "c", MaxUnknownPartBytes = 100 };
        var part = new global::RockPlex.Core.Models.MediaPartRef(1, "/library/parts/1/1/file.flac", 0, "flac");

        await Assert.ThrowsAsync<PlexProtocolException>(() => Library(fake, options).DownloadPartAsync(part, new MemoryStream(), null, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedImage_IsRejected()
    {
        var fake = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[500]) });
        var options = new PlexClientOptions { ClientIdentifier = "c", MaxImageBytes = 100 };

        await Assert.ThrowsAsync<PlexProtocolException>(() => Library(fake, options).DownloadImageAsync("/thumb/1", CancellationToken.None));
    }
}
