using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RockPlex.Plex;
using RockPlex.Plex.Auth;
using static RockPlex.Tests.Plex.FakeHttpHandler;

namespace RockPlex.Tests.Plex;

public sealed class PlexAuthServiceTests
{
    private static PlexAuthService Create(FakeHttpHandler fake, TimeProvider? time = null, PlexClientOptions? options = null) =>
        new(
            new HttpClient(PlexTestSetup.Pipeline(fake, options: options)) { BaseAddress = PlexAuthService.BaseAddress },
            options ?? PlexTestSetup.Options,
            time ?? TimeProvider.System,
            NullLogger<PlexAuthService>.Instance);

    /// <summary>A clock frozen just after the fixture PINs were created.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset FixtureCreated = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatePin_PostsStrongPin_WithPlexHeaders()
    {
        var fake = new FakeHttpHandler(_ => Fixture("pin-created.json"));
        var pin = await Create(fake).CreatePinAsync(CancellationToken.None);

        Assert.Equal(1234567, pin.Id);
        Assert.Equal("abcd1234efgh5678ijkl", pin.Code);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero), pin.ExpiresAt);

        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://plex.tv/api/v2/pins?strong=true", request.Uri.ToString());
        Assert.Equal("RockPlex", request.Headers["X-Plex-Product"]);
        Assert.Equal("test-client-id", request.Headers["X-Plex-Client-Identifier"]);
        Assert.Equal("9.9.9", request.Headers["X-Plex-Version"]);
        Assert.Equal("Windows", request.Headers["X-Plex-Platform"]);
        Assert.Equal("TestPC", request.Headers["X-Plex-Device-Name"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.False(request.Headers.ContainsKey("X-Plex-Token"));
    }

    [Fact]
    public void AuthUrl_HasClientIdCodeAndProduct()
    {
        var url = Create(new FakeHttpHandler(_ => Status(HttpStatusCode.OK)))
            .BuildAuthUrl(new PlexPin(1, "CODE1", DateTimeOffset.MaxValue));
        Assert.Equal("https://app.plex.tv/auth#?clientID=test-client-id&code=CODE1&context%5Bdevice%5D%5Bproduct%5D=RockPlex", url);
    }

    [Fact]
    public async Task Poll_ReturnsToken_AfterTwoPendingResponses()
    {
        var responses = new Queue<string>(["pin-pending.json", "pin-pending.json", "pin-approved.json"]);
        var fake = new FakeHttpHandler(_ => Fixture(responses.Dequeue()));
        var pin = new PlexPin(1234567, "abcd1234efgh5678ijkl", FixtureCreated.AddMinutes(30));

        var token = await Create(fake, new FixedTime(FixtureCreated)).PollForTokenAsync(pin, CancellationToken.None);

        Assert.Equal("SECRET-ACCOUNT-TOKEN", token);
        Assert.Equal(3, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Equal("https://plex.tv/api/v2/pins/1234567?code=abcd1234efgh5678ijkl", r.Uri.ToString()));
    }

    [Fact]
    public async Task Poll_ThrowsWhenPinExpires()
    {
        var fake = new FakeHttpHandler(_ => Fixture("pin-pending.json"));
        var pin = new PlexPin(1234567, "abcd1234efgh5678ijkl", FixtureCreated.AddMinutes(30));
        var time = new FixedTime(FixtureCreated.AddMinutes(31));

        await Assert.ThrowsAsync<PinExpiredException>(() => Create(fake, time).PollForTokenAsync(pin, CancellationToken.None));
    }

    [Fact]
    public async Task Poll_ThrowsWhenPlexForgetsThePin()
    {
        var fake = new FakeHttpHandler(_ => Status(HttpStatusCode.NotFound));
        var pin = new PlexPin(1, "c", DateTimeOffset.MaxValue);
        await Assert.ThrowsAsync<PinExpiredException>(() => Create(fake).PollForTokenAsync(pin, CancellationToken.None));
    }

    [Fact]
    public async Task Poll_StopsOnCancel()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeHttpHandler(_ =>
        {
            cts.Cancel();
            return Fixture("pin-pending.json");
        });
        var pin = new PlexPin(1, "c", DateTimeOffset.MaxValue);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(fake).PollForTokenAsync(pin, cts.Token));
    }

    [Fact]
    public async Task Validate_200_IsValid_WithUsername()
    {
        var fake = new FakeHttpHandler(_ => Fixture("user.json"));
        var result = await Create(fake).ValidateTokenAsync("tok", CancellationToken.None);
        Assert.Equal(new TokenValidation(TokenStatus.Valid, "rockfan"), result);
        Assert.Equal("tok", Assert.Single(fake.Requests).Headers["X-Plex-Token"]);
    }

    [Fact]
    public async Task Validate_401_IsInvalid()
    {
        var result = await Create(new FakeHttpHandler(_ => Status(HttpStatusCode.Unauthorized)))
            .ValidateTokenAsync("tok", CancellationToken.None);
        Assert.Equal(TokenStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Validate_500_IsUnreachable_NotInvalid()
    {
        var result = await Create(new FakeHttpHandler(_ => Status(HttpStatusCode.InternalServerError)))
            .ValidateTokenAsync("tok", CancellationToken.None);
        Assert.Equal(TokenStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task Validate_NetworkError_IsUnreachable()
    {
        var result = await Create(new FakeHttpHandler(_ => throw new HttpRequestException("no route")))
            .ValidateTokenAsync("tok", CancellationToken.None);
        Assert.Equal(TokenStatus.Unreachable, result.Status);
    }
}
