using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using PlexPod.Core.Models;
using PlexPod.Core.Tagging;
using PlexPod.Media.Ffmpeg;
using PlexPod.Plex.Library;
using PlexPod.Plex.Servers;

namespace PlexPod.Tests.Media;

/// <summary>A fact that needs ffmpeg; skipped (with the reason shown) when none is installed.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (TestFfmpeg.Path is null)
        {
            Skip = "ffmpeg not found (PATH or winget); install it to run the conversion tests.";
        }
    }
}

internal static class TestFfmpeg
{
    public static readonly string? Path = FfmpegLocator.Candidates(null).FirstOrDefault(File.Exists);

    public static readonly FfmpegRunner Runner = new(NullLogger<FfmpegRunner>.Instance);

    private static readonly Lazy<Task<FfmpegInstall?>> LazyInstall = new(() =>
        new FfmpegLocator(Runner, NullLogger<FfmpegLocator>.Instance).LocateAsync(null, CancellationToken.None));

    public static async Task<FfmpegInstall> InstallAsync() =>
        await LazyInstall.Value ?? throw new InvalidOperationException("ffmpeg vanished");

    /// <summary>Generates a sine-tone FLAC with the given format.</summary>
    public static async Task<string> MakeFlacAsync(string path, int rate, int bits, double seconds = 1.0, int? blockSize = null)
    {
        var install = await InstallAsync();
        List<string> args =
        [
            "-hide_banner", "-v", "error", "-y", "-f", "lavfi",
            "-i", string.Create(CultureInfo.InvariantCulture, $"sine=frequency=1000:duration={seconds}:sample_rate={rate}"),
            "-ac", "2", "-sample_fmt", bits > 16 ? "s32" : "s16",
        ];
        if (bits is not (16 or 32))
        {
            args.AddRange(["-bits_per_raw_sample", bits.ToString(CultureInfo.InvariantCulture)]);
        }

        if (blockSize is int b)
        {
            args.AddRange(["-frame_size", b.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-c:a", "flac", path]);
        await Runner.RunAsync(install.Path, args, CancellationToken.None);
        return path;
    }
}

/// <summary>A temp folder deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string CopyFixture(string name)
    {
        var target = File(name);
        System.IO.File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "audio", name), target);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>An in-memory Plex library: parts are served from local files.</summary>
internal sealed class FakeLibraryClient : IPlexLibraryClient
{
    public Dictionary<long, string> PartFiles { get; } = [];

    public Dictionary<string, AlbumDetail> Albums { get; } = [];

    public byte[]? Image { get; set; }

    public int Downloads { get; private set; }

    public PlexServerConnection Connection { get; } = new(new Uri("http://fake:32400/"), "t", "m", "Fake");

    /// <summary>Parts whose download throws an unexpected (non-HTTP) exception.</summary>
    public HashSet<long> ExplodeOnDownload { get; } = [];

    /// <summary>Runs before each album-detail lookup (e.g. to fail it, or to press Stop).</summary>
    public Func<string, CancellationToken, Task>? BeforeAlbumDetail { get; set; }

    public async Task<long> DownloadPartAsync(MediaPartRef part, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        Downloads++;
        if (ExplodeOnDownload.Contains(part.PartId))
        {
            throw new InvalidOperationException("simulated unexpected failure");
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(PartFiles[part.PartId], ct);
        await destination.WriteAsync(bytes, ct);
        return bytes.Length;
    }

    public Task<byte[]> DownloadImageAsync(string imagePath, CancellationToken ct) =>
        Image is null ? throw new HttpRequestException("no image") : Task.FromResult(Image);

    public async Task<AlbumDetail?> GetAlbumDetailAsync(string albumKey, CancellationToken ct)
    {
        if (BeforeAlbumDetail is { } hook)
        {
            await hook(albumKey, ct);
        }

        return Albums.GetValueOrDefault(albumKey);
    }

    public Task<ArtistDetail?> GetArtistDetailAsync(string artistKey, CancellationToken ct) => Task.FromResult<ArtistDetail?>(new ArtistDetail(artistKey, "A", ["Rock"]));

    public Task<PlexTrackDetail?> GetTrackDetailAsync(string ratingKey, CancellationToken ct) => Task.FromResult<PlexTrackDetail?>(null);

    /// <summary>Every play and rating sent, in order ("play:101", "rate:101=8").</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Fails a write before it reaches the fake server (e.g. Plex down after n plays).</summary>
    public Func<string, bool>? FailWrite { get; set; }

    public Task ScrobbleAsync(string ratingKey, CancellationToken ct) => Write($"play:{ratingKey}");

    public Task RateAsync(string ratingKey, double rating, CancellationToken ct) =>
        Write(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"rate:{ratingKey}={rating}"));

    private Task Write(string what)
    {
        if (FailWrite?.Invoke(what) == true)
        {
            throw new HttpRequestException("simulated Plex outage");
        }

        Writes.Add(what);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MusicSection>> GetMusicSectionsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<ArtistItem>> GetArtistsAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<AlbumItem>> GetAlbumsAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<TrackItem>> GetTracksAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<PlaylistItem>> GetPlaylistsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<PlaylistContents> GetPlaylistContentsAsync(PlaylistItem playlist, CancellationToken ct) => throw new NotSupportedException();
}
