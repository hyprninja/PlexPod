using RockPlex.Core.Models;
using RockPlex.Core.Selection;
using static RockPlex.Tests.Core.TestData;

namespace RockPlex.Tests.Core;

public sealed class SelectionResolverTests
{
    // ar1 → al1 (t1, t2), al2 (t3); ar2 → al3 (t4, t5 unavailable).
    private static readonly TrackItem T1 = Track("t1", "al1", "ar1", 100);
    private static readonly TrackItem T2 = Track("t2", "al1", "ar1", 200);
    private static readonly TrackItem T3 = Track("t3", "al2", "ar1", 300);
    private static readonly TrackItem T4 = Track("t4", "al3", "ar2", 400, Flac(24, 96_000));
    private static readonly TrackItem T5 = Track("t5", "al3", "ar2", 500, available: false);

    private static readonly LibrarySnapshot Snapshot = new(
        "3",
        [Artist("ar1"), Artist("ar2")],
        [Album("al1", "ar1"), Album("al2", "ar1"), Album("al3", "ar2")],
        [T1, T2, T3, T4, T5],
        [Playlist("p1"), Playlist("p2")]);

    private static readonly Dictionary<string, PlaylistContents> NoContents = [];

    private static SelectionProfile Profile(
        bool allSongs = false,
        ItemRef[]? playlists = null,
        ItemRef[]? artists = null,
        ItemRef[]? albums = null) =>
        new("3", allSongs, playlists ?? [], artists ?? [], albums ?? []);

    [Fact]
    public void EmptySelection_ResolvesToNothing()
    {
        var result = SelectionResolver.Resolve(Snapshot, Profile(), NoContents);
        Assert.Empty(result.TrackKeys);
        Assert.Equal(0, result.TotalEstimatedBytes);
        Assert.True(result.IsEstimateExact);
        Assert.Empty(result.MissingRefs);
    }

    [Fact]
    public void AlbumAndItsArtist_AreCountedOnce()
    {
        var result = SelectionResolver.Resolve(Snapshot, Profile(artists: [Ref("ar1")], albums: [Ref("al1")]), NoContents);
        Assert.Equal(["t1", "t2", "t3"], result.TrackKeys);
        Assert.Equal(600, result.TotalEstimatedBytes);
        Assert.Equal(new SourceCounts(0, 3, 0, 0), result.Counts);
    }

    [Fact]
    public void UnavailableLibraryTracks_AreExcludedAndCounted()
    {
        var result = SelectionResolver.Resolve(Snapshot, Profile(albums: [Ref("al3")]), NoContents);
        Assert.Equal(["t4"], result.TrackKeys);
        Assert.Equal(1, result.UnavailableTrackCount);
        Assert.Equal(1, result.ConversionCount);
        Assert.True(result.TotalEstimatedBytes < 400);
    }

    [Fact]
    public void AllSongs_IncludesEveryAvailableTrack_AndIgnoresArtistAndAlbumPicks()
    {
        var result = SelectionResolver.Resolve(Snapshot, Profile(allSongs: true, artists: [Ref("ar1")]), NoContents);
        Assert.Equal(["t1", "t2", "t3", "t4"], result.TrackKeys);
        Assert.Equal(new SourceCounts(0, 0, 0, 4), result.Counts);
    }

    [Fact]
    public void AllSongs_WithPlaylist_AttributesPlaylistTracksFirst()
    {
        var contents = new Dictionary<string, PlaylistContents>
        {
            ["p1"] = new("p1", [Entry(1, T3)]),
        };
        var result = SelectionResolver.Resolve(Snapshot, Profile(allSongs: true, playlists: [Ref("p1")]), contents);
        Assert.Equal(4, result.TrackCount);
        Assert.Equal(new SourceCounts(1, 0, 0, 3), result.Counts);
        Assert.Equal(["p1"], result.PlaylistKeys);
    }

    [Fact]
    public void PlaylistWithDuplicates_PullsEveryTrackOnce()
    {
        var contents = new Dictionary<string, PlaylistContents>
        {
            ["p1"] = new("p1", [Entry(10, T2), Entry(11, T1), Entry(12, T2)]),
        };
        var result = SelectionResolver.Resolve(Snapshot, Profile(playlists: [Ref("p1")]), contents);
        Assert.Equal(["t2", "t1"], result.TrackKeys);
        Assert.Equal(300, result.TotalEstimatedBytes);
        Assert.Equal(2, result.Counts.FromPlaylists);
    }

    [Fact]
    public void PlaylistTrackFromAnotherSection_IsIncludedFromEntryData()
    {
        var foreign = Track("t99", "alX", "arX", 999);
        var contents = new Dictionary<string, PlaylistContents> { ["p1"] = new("p1", [Entry(1, foreign)]) };
        var result = SelectionResolver.Resolve(Snapshot, Profile(playlists: [Ref("p1")]), contents);
        Assert.Equal(["t99"], result.TrackKeys);
        Assert.Equal(999, result.TotalEstimatedBytes);
    }

    [Fact]
    public void UnavailablePlaylistEntries_AreReported_NotSilentlyDropped()
    {
        var contents = new Dictionary<string, PlaylistContents>
        {
            ["p1"] = new("p1", [Entry(1, T1), Entry(2, null, unavailable: true), Entry(3, T5), Entry(4, T2, unavailable: true)]),
        };
        var result = SelectionResolver.Resolve(Snapshot, Profile(playlists: [Ref("p1")]), contents);
        Assert.Equal(["t1"], result.TrackKeys);
        Assert.Equal([2L, 3L, 4L], result.UnavailablePlaylistEntries.Select(e => e.PlaylistItemId));
        Assert.All(result.UnavailablePlaylistEntries, e => Assert.Equal("Playlist p1", e.PlaylistTitle));
    }

    [Fact]
    public void PlaylistNotLoadedYet_IsPending_AndEstimateInexact()
    {
        var result = SelectionResolver.Resolve(Snapshot, Profile(playlists: [Ref("p2")]), NoContents);
        Assert.Equal(["p2"], result.PendingPlaylistKeys);
        Assert.False(result.IsEstimateExact);
    }

    [Fact]
    public void StaleRef_IsMatchedByGuid()
    {
        var result = SelectionResolver.Resolve(Snapshot, Profile(albums: [Ref("old-key", "plex://album/al2")]), NoContents);
        Assert.Equal(["t3"], result.TrackKeys);
        Assert.Empty(result.MissingRefs);
    }

    [Fact]
    public void UnmatchedRef_IsReportedMissing()
    {
        var result = SelectionResolver.Resolve(Snapshot, Profile(artists: [Ref("gone", "plex://artist/gone")]), NoContents);
        Assert.Empty(result.TrackKeys);
        var missing = Assert.Single(result.MissingRefs);
        Assert.Equal(RefKind.Artist, missing.Kind);
        Assert.Equal("gone", missing.RatingKey);
    }

    [Fact]
    public void UnknownFlac_IsCountedAndMakesEstimateInexact()
    {
        var unknown = Track("t7", "al9", "ar9", 1_000, Flac(null, null));
        var snapshot = new LibrarySnapshot("3", [Artist("ar9")], [Album("al9", "ar9")], [unknown], []);
        var result = SelectionResolver.Resolve(snapshot, Profile(artists: [Ref("ar9")]), NoContents);
        Assert.Equal(1, result.UnknownFormatCount);
        Assert.Equal(["t7"], result.UnknownFormatTrackKeys);
        Assert.False(result.IsEstimateExact);
        Assert.Equal(1_000, result.TotalEstimatedBytes);

        var refined = SelectionResolver.Resolve(snapshot, Profile(artists: [Ref("ar9")]), NoContents,
            new Dictionary<string, AudioFormat> { ["t7"] = Flac(16, 44_100) });
        Assert.True(refined.IsEstimateExact);
        Assert.Equal(0, refined.UnknownFormatCount);
    }

    [Fact]
    public void KnownSizes_WinOverEstimates_AndCountAsExact()
    {
        var unknown = Track("t7", "al9", "ar9", 1_000, Flac(null, null));
        var snapshot = new LibrarySnapshot("3", [Artist("ar9")], [Album("al9", "ar9")], [unknown], []);
        var result = SelectionResolver.Resolve(snapshot, Profile(artists: [Ref("ar9")]), NoContents, null,
            new Dictionary<string, long> { ["t7"] = 400 });
        Assert.Equal(400, result.TotalEstimatedBytes);
        Assert.True(result.IsEstimateExact);
    }
}
