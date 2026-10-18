namespace RockPlex.Core.Tagging;

/// <summary>Where a field's value comes from.</summary>
public enum TagFieldSource
{
    Plex,

    /// <summary>Carried over from the original file's tags (Plex doesn't hold it).</summary>
    OriginalFile,
}

/// <summary>
/// Which tag fields are written to the iPod copies (owner decision D4). A switched-off field is
/// absent from the file, never written empty. RockPlex's own ID tags are not covered: they are
/// always written, because the manifest (M3) and stats return (M4) depend on them.
/// </summary>
public sealed record TagPolicy
{
    public static readonly TagPolicy Default = new();

    public bool Title { get; init; } = true;

    public bool Artist { get; init; } = true;

    public bool AlbumArtist { get; init; } = true;

    public bool Album { get; init; } = true;

    public bool TrackNumber { get; init; } = true;

    public bool TrackTotal { get; init; } = true;

    public bool DiscNumber { get; init; } = true;

    public bool DiscTotal { get; init; } = true;

    public bool Date { get; init; } = true;

    /// <summary>The first Plex genre only, never joined (D1).</summary>
    public bool Genre { get; init; } = true;

    /// <summary>The first Plex style.</summary>
    public bool Grouping { get; init; }

    public bool Compilation { get; init; } = true;

    /// <summary>Plex <c>studio</c>. Rockbox ignores it.</summary>
    public bool Label { get; init; }

    public bool MusicBrainzTrackId { get; init; } = true;

    /// <summary>Kept from the original file (D3).</summary>
    public bool Composer { get; init; } = true;

    /// <summary>Kept from the original file; often junk, so off by default.</summary>
    public bool Comment { get; init; }

    /// <summary>Every switch, in display order, for the settings UI and tests.</summary>
    public static IReadOnlyList<TagField> Fields { get; } =
    [
        new("Title", "Title", TagFieldSource.Plex, IsCore: true, p => p.Title, (p, v) => p with { Title = v }),
        new("Artist", "Artist (track artist)", TagFieldSource.Plex, IsCore: true, p => p.Artist, (p, v) => p with { Artist = v }),
        new("AlbumArtist", "Album artist", TagFieldSource.Plex, IsCore: true, p => p.AlbumArtist, (p, v) => p with { AlbumArtist = v }),
        new("Album", "Album", TagFieldSource.Plex, IsCore: true, p => p.Album, (p, v) => p with { Album = v }),
        new("TrackNumber", "Track number", TagFieldSource.Plex, IsCore: true, p => p.TrackNumber, (p, v) => p with { TrackNumber = v }),
        new("TrackTotal", "Track total", TagFieldSource.Plex, IsCore: false, p => p.TrackTotal, (p, v) => p with { TrackTotal = v }),
        new("DiscNumber", "Disc number", TagFieldSource.Plex, IsCore: false, p => p.DiscNumber, (p, v) => p with { DiscNumber = v }),
        new("DiscTotal", "Disc total", TagFieldSource.Plex, IsCore: false, p => p.DiscTotal, (p, v) => p with { DiscTotal = v }),
        new("Date", "Date / year", TagFieldSource.Plex, IsCore: false, p => p.Date, (p, v) => p with { Date = v }),
        new("Genre", "Genre (first Plex genre only)", TagFieldSource.Plex, IsCore: false, p => p.Genre, (p, v) => p with { Genre = v }),
        new("Grouping", "Grouping (first Plex style)", TagFieldSource.Plex, IsCore: false, p => p.Grouping, (p, v) => p with { Grouping = v }),
        new("Compilation", "Compilation flag", TagFieldSource.Plex, IsCore: false, p => p.Compilation, (p, v) => p with { Compilation = v }),
        new("Label", "Label (studio)", TagFieldSource.Plex, IsCore: false, p => p.Label, (p, v) => p with { Label = v }),
        new("MusicBrainzTrackId", "MusicBrainz track ID", TagFieldSource.Plex, IsCore: false, p => p.MusicBrainzTrackId, (p, v) => p with { MusicBrainzTrackId = v }),
        new("Composer", "Composer", TagFieldSource.OriginalFile, IsCore: false, p => p.Composer, (p, v) => p with { Composer = v }),
        new("Comment", "Comment", TagFieldSource.OriginalFile, IsCore: false, p => p.Comment, (p, v) => p with { Comment = v }),
    ];

    public static TagPolicy None { get; } = Fields.Aggregate(new TagPolicy(), (p, f) => f.Set(p, false));

    /// <summary>Compact fingerprint of the switches, e.g. "1111111110110110", stored in the manifest's metadata stamp.</summary>
    public string Fingerprint() => string.Concat(Fields.Select(f => f.Get(this) ? '1' : '0'));
}

/// <summary>
/// One switch in <see cref="TagPolicy"/>. <see cref="IsCore"/> fields drive Rockbox's database
/// browsing; switching one off files those tracks under &lt;Untagged&gt;.
/// </summary>
public sealed record TagField(
    string Key,
    string DisplayName,
    TagFieldSource Source,
    bool IsCore,
    Func<TagPolicy, bool> Get,
    Func<TagPolicy, bool, TagPolicy> Set);
