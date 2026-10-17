using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RockPlex.Plex.Auth;
using RockPlex.Plex.Servers;
using static RockPlex.Tests.Plex.FakeHttpHandler;

namespace RockPlex.Tests.Plex;

public sealed class PlexServerDiscoveryTests
{
    private static PlexServerDiscovery Create(FakeHttpHandler fake, string? accountToken = "SECRET-ACCOUNT-TOKEN") =>
        new(
            new FakeHttpClientFactory(PlexTestSetup.Pipeline(fake, accountToken), PlexAuthService.BaseAddress),
            PlexTestSetup.Options,
            NullLogger<PlexServerDiscovery>.Instance);

    private static async Task<PlexServer> LoadServerAsync()
    {
        var servers = await Create(new FakeHttpHandler(_ => Fixture("resources.json"))).GetServersAsync(CancellationToken.None);
        return servers[0];
    }

    [Fact]
    public async Task Resources_KeepsOnlyServers()
    {
        var fake = new FakeHttpHandler(_ => Fixture("resources.json"));
        var servers = await Create(fake).GetServersAsync(CancellationToken.None);

        Assert.Equal(["server-machine-1", "server-machine-2"], servers.Select(s => s.MachineIdentifier));
        var home = servers[0];
        Assert.Equal("Living Room Mac", home.Name);
        Assert.True(home.Owned);
        Assert.Equal("SECRET-SERVER-TOKEN", home.AccessToken);
        Assert.Equal(3, home.Connections.Count);
        Assert.False(servers[1].Owned);
        Assert.Empty(servers[1].Connections);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("https://plex.tv/api/v2/resources?includeHttps=1", request.Uri.ToString());
        Assert.Equal("SECRET-ACCOUNT-TOKEN", request.Headers["X-Plex-Token"]);
    }

    [Fact]
    public async Task Candidates_LocalFirst_ThenPlainHttp_ThenRemote_NeverRelay()
    {
        var order = PlexServerDiscovery.OrderCandidates(await LoadServerAsync(), null).Select(u => u.ToString());
        Assert.Equal(
        [
            "https://192-168-1-20.abc.plex.direct:32400/",
            "http://192.168.1.20:32400/",
            "https://203-0-113-5.abc.plex.direct:32400/",
        ], order);
    }

    [Fact]
    public async Task Candidates_OverrideComesFirst()
    {
        var order = PlexServerDiscovery.OrderCandidates(await LoadServerAsync(), new Uri("http://nas.lan:32400/"));
        Assert.Equal("http://nas.lan:32400/", order[0].ToString());
    }

    [Fact]
    public async Task Select_PicksLocalHttps_WhenReachable()
    {
        var fake = new FakeHttpHandler(_ => Status(HttpStatusCode.OK));
        var connection = await Create(fake).SelectConnectionAsync(await LoadServerAsync(), null, CancellationToken.None);

        Assert.NotNull(connection);
        Assert.Equal("https://192-168-1-20.abc.plex.direct:32400/", connection.BaseUri.ToString());
        Assert.Equal("SECRET-SERVER-TOKEN", connection.AccessToken);
        Assert.DoesNotContain(fake.Requests, r => r.Uri.Host.StartsWith("10-0-0-1", StringComparison.Ordinal));
        Assert.All(fake.Requests, r => Assert.Equal("SECRET-SERVER-TOKEN", r.Headers["X-Plex-Token"]));
    }

    [Fact]
    public async Task Select_FallsBackToPlainHttp_WhenPlexDirectIsBlocked()
    {
        var fake = new FakeHttpHandler(r => r.RequestUri!.Host.EndsWith("plex.direct", StringComparison.Ordinal)
            ? throw new HttpRequestException("DNS rebind protection")
            : Status(HttpStatusCode.OK));
        var connection = await Create(fake).SelectConnectionAsync(await LoadServerAsync(), null, CancellationToken.None);
        Assert.Equal("http://192.168.1.20:32400/", connection!.BaseUri.ToString());
    }

    [Fact]
    public async Task Select_OverrideWins_WhenReachable()
    {
        var fake = new FakeHttpHandler(_ => Status(HttpStatusCode.OK));
        var connection = await Create(fake).SelectConnectionAsync(await LoadServerAsync(), new Uri("http://nas.lan:32400/"), CancellationToken.None);
        Assert.Equal("http://nas.lan:32400/", connection!.BaseUri.ToString());
        Assert.Contains(fake.Requests, r => r.Uri.ToString() == "http://nas.lan:32400/identity");
    }

    [Fact]
    public async Task Select_UnreachableOverride_FallsBackToDiscovered()
    {
        var fake = new FakeHttpHandler(r => r.RequestUri!.Host == "nas.lan" ? Status(HttpStatusCode.BadGateway) : Status(HttpStatusCode.OK));
        var connection = await Create(fake).SelectConnectionAsync(await LoadServerAsync(), new Uri("http://nas.lan:32400/"), CancellationToken.None);
        Assert.Equal("https://192-168-1-20.abc.plex.direct:32400/", connection!.BaseUri.ToString());
    }

    [Fact]
    public async Task Select_OnlyRelay_ReturnsNull()
    {
        var server = new PlexServer("S", "m", "t", true,
            [new PlexConnection(new Uri("https://relay.plex.direct:8443"), "10.0.0.1", 8443, "https", false, true)]);
        var fake = new FakeHttpHandler(_ => Status(HttpStatusCode.OK));
        Assert.Null(await Create(fake).SelectConnectionAsync(server, null, CancellationToken.None));
        Assert.Empty(fake.Requests);
    }
}
