using System.Collections.Concurrent;
using RockPlex.Core.Models;
using RockPlex.Plex.Library;

namespace RockPlex.App.Services;

/// <summary>
/// The loaded library shared by the view models: the snapshot, playlist contents as they load, and
/// enriched track formats as they arrive. The dictionaries are written from background tasks and
/// read by the resolver, which only does lookups.
/// </summary>
public sealed class LibraryState
{
    public LibrarySnapshot? Snapshot { get; private set; }

    public IPlexLibraryClient? Client { get; private set; }

    public ConcurrentDictionary<string, PlaylistContents> PlaylistContents { get; private set; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, AudioFormat> FormatOverrides { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Real on-device sizes (already-converted files in the cache), keyed by track rating key.</summary>
    public ConcurrentDictionary<string, long> KnownSizes { get; private set; } = new(StringComparer.Ordinal);

    public void Reset(IPlexLibraryClient? client, LibrarySnapshot? snapshot)
    {
        Client = client;
        Snapshot = snapshot;
        PlaylistContents = new(StringComparer.Ordinal);
        FormatOverrides = new(StringComparer.Ordinal);
        KnownSizes = new(StringComparer.Ordinal);
    }
}
