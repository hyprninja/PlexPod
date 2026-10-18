using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using PlexPod.Plex.Http;
using static PlexPod.Tests.Plex.FakeHttpHandler;

namespace PlexPod.Tests.Plex;

public sealed class TransientRetryHandlerTests
{
    private static HttpClient Client(FakeHttpHandler fake) =>
        new(new TransientRetryHandler(NullLogger<TransientRetryHandler>.Instance) { BaseDelay = TimeSpan.Zero, InnerHandler = fake });

    private static HttpRequestException Reset() =>
        new("An error occurred while sending the request.",
            new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset)));

    [Fact]
    public async Task Get_IsRetried_AfterAConnectionReset()
    {
        var calls = 0;
        var fake = new FakeHttpHandler(_ => ++calls == 1 ? throw Reset() : Fixture("user.json"));
        using var response = await Client(fake).GetAsync(new Uri("https://plex.tv/api/v2/resources"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Get_GivesUp_AfterMaxRetries()
    {
        var fake = new FakeHttpHandler(_ => throw Reset());
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(fake).GetAsync(new Uri("https://plex.tv/x")));
        Assert.Equal(1 + TransientRetryHandler.MaxRetries, fake.Requests.Count);
    }

    [Fact]
    public async Task Get_MarkedNoRetry_IsSentOnce()
    {
        var fake = new FakeHttpHandler(_ => throw Reset());
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://server:32400/:/scrobble?key=1"));
        request.Options.Set(TransientRetryHandler.NoRetry, true);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(fake).SendAsync(request));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Post_IsNeverRetried()
    {
        var fake = new FakeHttpHandler(_ => throw Reset());
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(fake).PostAsync(new Uri("https://plex.tv/api/v2/pins"), null));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task CertificateAndRefusedErrors_FailFast()
    {
        var tls = new FakeHttpHandler(_ => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "ssl",
            new System.Security.Authentication.AuthenticationException("RemoteCertificateNameMismatch")));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(tls).GetAsync(new Uri("https://192-168-1-20.x.plex.direct:32400/identity")));
        Assert.Single(tls.Requests);

        var refused = new FakeHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused",
            new SocketException((int)SocketError.ConnectionRefused)));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(refused).GetAsync(new Uri("http://192.168.1.20:32400/identity")));
        Assert.Single(refused.Requests);
    }

    [Fact]
    public async Task HttpErrorStatus_IsNotRetried()
    {
        var fake = new FakeHttpHandler(_ => Status(HttpStatusCode.InternalServerError));
        using var response = await Client(fake).GetAsync(new Uri("https://plex.tv/x"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(fake.Requests);
    }
}
