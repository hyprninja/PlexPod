using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using PlexPod.Plex;
using PlexPod.Plex.Http;

namespace PlexPod.Tests.Plex;

/// <summary>A request as the fake server saw it (headers flattened, so it outlives the message).</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

/// <summary>Fake <see cref="HttpMessageHandler"/>: routes every request to a responder and records it.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers));
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Fixture(string name) => Json(ReadFixture(name));

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler, Uri? baseAddress = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { BaseAddress = baseAddress };
}

/// <summary>Captures every formatted log line, to prove tokens never reach the log.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue(formatter(state, exception));
}

internal static class PlexTestSetup
{
    public static readonly PlexClientOptions Options = new()
    {
        ClientIdentifier = "test-client-id",
        Version = "9.9.9",
        DeviceName = "TestPC",
        PinPollInterval = TimeSpan.Zero,
        PlayStateCheckDelay = TimeSpan.Zero,
        ProbeTimeout = TimeSpan.FromSeconds(1),
    };

    /// <summary>The real headers handler in front of the fake server, as in production.</summary>
    public static HttpMessageHandler Pipeline(FakeHttpHandler fake, string? accountToken = null, PlexClientOptions? options = null)
    {
        var tokens = new InMemoryPlexTokenProvider();
        tokens.Set(accountToken);
        return new PlexHeadersHandler(options ?? Options, tokens) { InnerHandler = fake };
    }
}
