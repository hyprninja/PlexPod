using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RockPlex.Core.Models;
using RockPlex.Plex;
using RockPlex.Plex.Http;
using RockPlex.Plex.Library;
using RockPlex.Plex.Servers;
using static RockPlex.Tests.Plex.FakeHttpHandler;

namespace RockPlex.Tests.Plex;

public sealed class PlexLibraryClientTests
{
    private const string ServerToken = "SECRET-SERVER-TOKEN";

    private static readonly PlexServerConnection Connection =
        new(new Uri("http://192.168.1.20:32400/"), ServerToken, "server-machine-1", "Living Room Mac");

    private static PlexLibraryClient Create(FakeHttpHandler fake, PlexClientOptions? options = null, CapturingLogger<PlexLibraryClient>? logger = null) =>
        new(
            new HttpClient(PlexTestSetup.Pipeline(fake, "SECRET-ACCOUNT-TOKEN", options)),
            Connection,
            options ?? PlexTestSetup.Options,
            (Microsoft.Extensions.Logging.ILogger<PlexLibraryClient>?)logger ?? NullLogger<PlexLibraryClient>.Instance);

    [Fact]
    public async Task Sections_KeepsOnlyMusic()
    {
        var fake = new FakeHttpHandler(_ => Fixture("sections.json"));
        var sections = await Create(fake).GetMusicSectionsAsync(CancellationToken.None);

        Assert.Equal([new MusicSection("3", "Music", "music-uuid"), new MusicSection("7", "Hi-Res", "hires-uuid")], sections);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("http://192.168.1.20:32400/library/sections", request.Uri.ToString());
        Assert.Equal(ServerToken, request.Headers["X-Plex-Token"]);
    }

    [Fact]
    public async Task Tracks_MapStringAndNumericKeys_AndMissingMediaIsUnavailable()
    {
        var fake = new FakeHttpHandler(_ => Fixture("tracks.json"));
        var tracks = await Create(fake).GetTracksAsync("3", null, CancellationToken.None);

        Assert.Equal(3, tracks.Count);
        var flac = tracks[0];
        Assert.Equal("48211", flac.RatingKey);
        Assert.Equal("plex://track/5d07cdd5", flac.Guid);
        Assert.Equal("Hoppípolla", flac.Title);
        Assert.Equal("5530", flac.AlbumKey);
        Assert.Equal("1021", flac.ArtistKey);
        Assert.Equal("Sigur Rós feat. Amiina", flac.TrackArtist);
        Assert.Equal(2, flac.Index);
        Assert.Equal(1, flac.DiscIndex);
        Assert.Equal(268_000, flac.DurationMs);
        Assert.Equal(1_700_000_000, flac.UpdatedAt);
        Assert.Equal(new AudioFormat("flac", "flac", 1950, null, null, 2), flac.Format);
        var part = Assert.Single(flac.Parts);
        Assert.Equal(new MediaPartRef(9001, "/library/parts/9001/1700000000/file.flac", 65_400_000, "flac"), part);

        var mp3 = tracks[1];
        Assert.Equal("48212", mp3.RatingKey);
        Assert.Equal("5530", mp3.AlbumKey);
        Assert.Equal(3, mp3.Index);
        Assert.Equal(375_000, mp3.DurationMs);
        Assert.Equal(15_000_000, mp3.SourceSizeBytes);
        Assert.Null(mp3.TrackArtist);

        Assert.False(tracks[2].IsAvailable);
        Assert.Equal("http://192.168.1.20:32400/library/sections/3/all?type=10", Assert.Single(fake.Requests).Uri.ToString());
    }

    [Fact]
    public async Task Paging_OverThreePages_JoinsInOrder()
    {
        const int total = 7;
        var fake = new FakeHttpHandler(request =>
        {
            var start = int.Parse(request.Headers.GetValues("X-Plex-Container-Start").Single(), CultureInfo.InvariantCulture);
            var size = int.Parse(request.Headers.GetValues("X-Plex-Container-Size").Single(), CultureInfo.InvariantCulture);
            var items = Enumerable.Range(start, Math.Max(0, Math.Min(size, total - start)))
                .Select(i => $$"""{ "ratingKey": "{{i}}", "guid": "plex://artist/{{i}}", "title": "Artist {{i}}" }""");
            return Json($$"""{ "MediaContainer": { "size": {{size}}, "totalSize": {{total}}, "offset": {{start}}, "Metadata": [{{string.Join(",", items)}}] } }""");
        });
        var options = new PlexClientOptions { ClientIdentifier = "c", PageSize = 3 };
        var progress = new List<int>();

        var artists = await Create(fake, options).GetArtistsAsync("3", new SyncProgress(progress), CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, total).Select(i => i.ToString(CultureInfo.InvariantCulture)), artists.Select(a => a.RatingKey));
        Assert.Equal(["0", "3", "6"], fake.Requests.Select(r => r.Headers["X-Plex-Container-Start"]));
        Assert.All(fake.Requests, r => Assert.Equal("3", r.Headers["X-Plex-Container-Size"]));
        Assert.All(fake.Requests, r => Assert.EndsWith("/library/sections/3/all?type=8", r.Uri.ToString(), StringComparison.Ordinal));
        Assert.Equal([3, 6, 7], progress);
    }

    [Fact]
    public async Task Paging_WithoutTotalSize_StopsOnShortPage()
    {
        var calls = 0;
        var fake = new FakeHttpHandler(_ => ++calls == 1
            ? Json("""{ "MediaContainer": { "Metadata": [ { "ratingKey": "1" }, { "ratingKey": "2" } ] } }""")
            : Json("""{ "MediaContainer": { "Metadata": [ { "ratingKey": "3" } ] } }"""));
        var albums = await Create(fake, new PlexClientOptions { ClientIdentifier = "c", PageSize = 2 })
            .GetAlbumsAsync("3", null, CancellationToken.None);
        Assert.Equal(["1", "2", "3"], albums.Select(a => a.RatingKey));
    }

    [Fact]
    public async Task Playlists_KeepOnlyAudio()
    {
        var fake = new FakeHttpHandler(_ => Fixture("playlists.json"));
        var playlists = await Create(fake).GetPlaylistsAsync(CancellationToken.None);

        Assert.Equal(["Road Trip", "Chill"], playlists.Select(p => p.Title));
        Assert.Equal(new PlaylistItem("70001", "com.plexapp.agents.none://a1", "Road Trip", true, 142, 31_000_000, "/playlists/70001/items"), playlists[0]);
        Assert.False(playlists[1].IsSmart);
        Assert.Equal("http://192.168.1.20:32400/playlists?playlistType=audio", Assert.Single(fake.Requests).Uri.ToString());
    }

    [Fact]
    public async Task PlaylistItems_KeepOrderAndDuplicates_SkipNonTracks_FlagMissingMedia()
    {
        var fake = new FakeHttpHandler(_ => Fixture("playlist-items.json"));
        var playlist = new PlaylistItem("70001", "g", "Road Trip", true, 5, 0, "/playlists/70001/items");
        var contents = await Create(fake).GetPlaylistContentsAsync(playlist, CancellationToken.None);

        Assert.Equal("70001", contents.PlaylistRatingKey);
        Assert.Equal([501L, 502L, 503L, 505L], contents.Entries.Select(e => e.PlaylistItemId));
        Assert.Equal(["48212", "48211", "48212", "48213"], contents.Entries.Select(e => e.Track!.RatingKey));
        Assert.Equal([false, false, false, true], contents.Entries.Select(e => e.Unavailable));
        Assert.Equal("http://192.168.1.20:32400/playlists/70001/items", Assert.Single(fake.Requests).Uri.ToString());
    }

    [Fact]
    public async Task TrackDetail_ReadsBitDepthAndRateFromAudioStream()
    {
        var fake = new FakeHttpHandler(_ => Fixture("track-detail.json"));
        var track = await Create(fake).GetTrackDetailAsync("48211", CancellationToken.None);

        Assert.Equal(24, track!.Format.BitDepth);
        Assert.Equal(96_000, track.Format.SampleRateHz);
        Assert.Equal("http://192.168.1.20:32400/library/metadata/48211", Assert.Single(fake.Requests).Uri.ToString());
    }

    [Fact]
    public async Task TokenNeverAppearsInUrlsOrLogs()
    {
        var fake = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/library/sections" => Fixture("sections.json"),
            "/playlists" => Fixture("playlists.json"),
            _ => Fixture("tracks.json"),
        });
        var logger = new CapturingLogger<PlexLibraryClient>();
        var client = Create(fake, logger: logger);

        await client.GetMusicSectionsAsync(CancellationToken.None);
        await client.GetTracksAsync("3", null, CancellationToken.None);
        await client.GetPlaylistsAsync(CancellationToken.None);

        Assert.NotEmpty(logger.Lines);
        Assert.All(fake.Requests, r => Assert.DoesNotContain("SECRET", r.Uri.ToString(), StringComparison.Ordinal));
        Assert.All(logger.Lines, l => Assert.DoesNotContain("SECRET", l, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://h:32400/library/parts/1/file.flac?X-Plex-Token=abc123&download=1", "http://h:32400/library/parts/1/file.flac?X-Plex-Token=REDACTED&download=1")]
    [InlineData("https://h/photo?url=x&x-plex-token=abc", "https://h/photo?url=x&x-plex-token=REDACTED")]
    [InlineData("no token here", "no token here")]
    public void Redactor_StripsTokens(string input, string expected) => Assert.Equal(expected, PlexRedactor.Redact(input));

    [Fact]
    public void Combine_KeepsReverseProxyPrefix()
    {
        Assert.Equal("https://example.com/plex/library/sections", PlexLibraryClient.Combine(new Uri("https://example.com/plex"), "/library/sections").ToString());
        Assert.Equal("http://h:32400/identity", PlexLibraryClient.Combine(new Uri("http://h:32400/"), "identity").ToString());
    }

    [Fact]
    public async Task Enricher_FetchesHighBitrateFlac_AssumesCdForLowBitrate_SkipsOthers()
    {
        static TrackItem T(string key, AudioFormat format) =>
            new(key, "", key, "a", "b", null, null, null, 0, format, [new MediaPartRef(long.Parse(key, CultureInfo.InvariantCulture), "/p", 1000, "flac")], 1);

        var fake = new FakeHttpHandler(_ => Fixture("track-detail.json"));
        var enricher = new TrackDetailEnricher(NullLogger<TrackDetailEnricher>.Instance);
        var results = new Dictionary<string, AudioFormat>();
        TrackItem[] tracks =
        [
            T("1", new AudioFormat("flac", "flac", 1950)),          // fetched
            T("2", new AudioFormat("flac", "flac", 900)),           // assumed 16/44.1
            T("3", new AudioFormat("mp3", "mp3", 320)),             // not FLAC
            T("4", new AudioFormat("flac", "flac", 900, 16, 44_100)), // already known
        ];

        await enricher.EnrichAsync(Create(fake), tracks, (k, f) => results[k] = f, CancellationToken.None);
        Assert.Equal(["1", "2"], results.Keys.Order());
        Assert.Equal((24, 96_000), (results["1"].BitDepth, results["1"].SampleRateHz));
        Assert.Equal((16, 44_100), (results["2"].BitDepth, results["2"].SampleRateHz));
        Assert.Single(fake.Requests);

        // Second pass hits the cache.
        await enricher.EnrichAsync(Create(fake), tracks, (k, f) => results[k] = f, CancellationToken.None);
        Assert.Single(fake.Requests);
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; posts to the thread pool, which races assertions).</summary>
    private sealed class SyncProgress(List<int> sink) : IProgress<int>
    {
        public void Report(int value) => sink.Add(value);
    }
}
