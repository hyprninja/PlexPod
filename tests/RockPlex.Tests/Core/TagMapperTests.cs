using RockPlex.Core.Models;
using RockPlex.Core.Tagging;
using static RockPlex.Tests.Core.TestData;

namespace RockPlex.Tests.Core;

public sealed class TagMapperTests
{
    private static readonly AlbumDetail AlbumDetail = new(
        "al1", " Takk... ", "ar1", "Sigur Rós", 2005, "2005-09-12T00:00:00Z",
        ["Post-Rock", "Ambient"], ["Dream Pop"], "EMI", 11, "/library/metadata/al1/thumb/1");

    private static TrackItem T(string key, int? index, int? disc, string? trackArtist = null) =>
        Track(key, "al1", "ar1") with { Index = index, DiscIndex = disc, TrackArtist = trackArtist, Title = $" Song {key} " };

    private static TagSource Source(
        TrackItem track,
        IReadOnlyList<TrackItem>? albumTracks = null,
        AlbumDetail? album = null,
        ArtistDetail? artist = null,
        SourceTags? original = null,
        string? mbid = "mb-123") =>
        new(track, album ?? AlbumDetail, artist, albumTracks ?? [track], mbid, original ?? new SourceTags(" Jónsi ", "Ripped by XYZ"));

    [Fact]
    public void Defaults_WriteThePlexFields_AndComposerButNotComment()
    {
        var t2 = T("t2", 2, 1);
        var tags = TagMapper.Map(Source(t2, [T("t1", 1, 1), t2, T("t3", 3, 1)]), TagPolicy.Default);

        Assert.Equal("Song t2", tags.Title);
        Assert.Equal("Sigur Rós", tags.Artist);
        Assert.Equal("Sigur Rós", tags.AlbumArtist);
        Assert.Equal("Takk...", tags.Album);
        Assert.Equal(2, tags.TrackNumber);
        Assert.Equal(3, tags.TrackTotal);
        Assert.Equal(1, tags.DiscNumber);
        Assert.Equal(1, tags.DiscTotal);
        Assert.Equal("2005-09-12", tags.Date);
        Assert.Equal("Post-Rock", tags.Genre);
        Assert.Null(tags.Grouping);
        Assert.False(tags.Compilation);
        Assert.Null(tags.Label);
        Assert.Equal("mb-123", tags.MusicBrainzTrackId);
        Assert.Equal("Jónsi", tags.Composer);
        Assert.Null(tags.Comment);
        Assert.Equal("t2", tags.RockPlexRatingKey);
        Assert.Equal(2, tags.RockPlexPartId);
    }

    [Fact]
    public void TrackArtist_WinsOverAlbumArtist()
    {
        var tags = TagMapper.Map(Source(T("t1", 1, 1, "Sigur Rós feat. Amiina")), TagPolicy.Default);
        Assert.Equal("Sigur Rós feat. Amiina", tags.Artist);
        Assert.Equal("Sigur Rós", tags.AlbumArtist);
    }

    [Fact]
    public void TotalsArePerDisc()
    {
        var tracks = new[] { T("t1", 1, 1), T("t2", 2, 1), T("t3", 1, 2), T("t4", 2, 2), T("t5", 3, 2) };
        var tags = TagMapper.Map(Source(tracks[3], tracks), TagPolicy.Default);
        Assert.Equal(3, tags.TrackTotal);
        Assert.Equal(2, tags.DiscNumber);
        Assert.Equal(2, tags.DiscTotal);
    }

    [Fact]
    public void DateFallsBackToYear_ThenNothing()
    {
        Assert.Equal("2005", TagMapper.Date(AlbumDetail with { OriginallyAvailableAt = null }));
        Assert.Equal("2005", TagMapper.Date(AlbumDetail with { OriginallyAvailableAt = "garbage" }));
        Assert.Null(TagMapper.Date(AlbumDetail with { OriginallyAvailableAt = null, Year = null }));
    }

    [Fact]
    public void Genre_IsFirstOnly_FallingBackToArtist_NeverJoined()
    {
        var noAlbumGenre = AlbumDetail with { Genres = ["  "] };
        var artist = new ArtistDetail("ar1", "Sigur Rós", ["Rock", "Experimental"]);
        Assert.Equal("Rock", TagMapper.Map(Source(T("t1", 1, 1), album: noAlbumGenre, artist: artist), TagPolicy.Default).Genre);
        Assert.Null(TagMapper.Map(Source(T("t1", 1, 1), album: noAlbumGenre), TagPolicy.Default).Genre);
    }

    [Fact]
    public void Compilation_ForVariousArtists()
    {
        var va = AlbumDetail with { ArtistTitle = "various artists" };
        Assert.True(TagMapper.Map(Source(T("t1", 1, 1), album: va), TagPolicy.Default).Compilation);
        Assert.False(TagMapper.Map(Source(T("t1", 1, 1), album: va), TagPolicy.Default with { Compilation = false }).Compilation);
    }

    [Fact]
    public void EmptyValues_AreDropped_NotWrittenEmpty()
    {
        var bare = AlbumDetail with { Title = " ", Studio = "", Genres = [], Styles = [] };
        var tags = TagMapper.Map(Source(T("t1", null, null) with { Title = "  " }, album: bare, mbid: " ", original: SourceTags.None),
            TagPolicy.Default with { Label = true, Grouping = true });
        var keys = tags.ToFields().Select(f => f.Key).ToList();
        Assert.Equal([TagSet.ArtistKey, TagSet.AlbumArtistKey, TagSet.TrackTotalKey, TagSet.DateKey, TagSet.RatingKeyKey, TagSet.GuidKey, TagSet.PartIdKey], keys);
        Assert.All(tags.ToFields(), f => Assert.False(string.IsNullOrWhiteSpace(f.Value)));
    }

    public static TheoryData<string> FieldKeys() => [.. TagPolicy.Fields.Select(f => f.Key)];

    [Theory]
    [MemberData(nameof(FieldKeys))]
    public void EachSwitch_StripsItsField(string key)
    {
        var field = TagPolicy.Fields.Single(f => f.Key == key);
        var everything = TagPolicy.Fields.Aggregate(TagPolicy.Default, (p, f) => f.Set(p, true));
        var va = AlbumDetail with { ArtistTitle = "Various Artists" };
        var source = Source(T("t1", 1, 1, "Guest"), album: va);

        var on = TagMapper.Map(source, everything).ToFields().Select(f => f.Key).ToHashSet();
        var off = TagMapper.Map(source, field.Set(everything, false)).ToFields().Select(f => f.Key).ToHashSet();

        var removed = on.Except(off).ToList();
        Assert.Single(removed);
        Assert.Empty(off.Except(on));
    }

    [Fact]
    public void EverythingOff_LeavesOnlyIdentityTags()
    {
        var tags = TagMapper.Map(Source(T("t1", 1, 1)), TagPolicy.None);
        Assert.Equal([TagSet.RatingKeyKey, TagSet.GuidKey, TagSet.PartIdKey], tags.ToFields().Select(f => f.Key));
    }

    [Fact]
    public void NoReplayGainKeys_EverWritten()
    {
        var everything = TagPolicy.Fields.Aggregate(TagPolicy.Default, (p, f) => f.Set(p, true));
        var tags = TagMapper.Map(Source(T("t1", 1, 1)), everything);
        Assert.DoesNotContain(tags.ToFields(), f => f.Key.StartsWith("REPLAYGAIN", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentHash_IsStable_AndChangesWithPolicyOrData()
    {
        var source = Source(T("t1", 1, 1));
        var a = TagMapper.Map(source, TagPolicy.Default).ContentHash();
        Assert.Equal(a, TagMapper.Map(source, TagPolicy.Default).ContentHash());
        Assert.NotEqual(a, TagMapper.Map(source, TagPolicy.Default with { Genre = false }).ContentHash());
        Assert.NotEqual(a, TagMapper.Map(source with { Track = source.Track with { Title = "Other" } }, TagPolicy.Default).ContentHash());

        // Stripping a field that has no value anyway changes nothing on disk, so the hash stays put.
        Assert.Equal(a, TagMapper.Map(source, TagPolicy.Default with { Comment = false }).ContentHash());
    }

    [Fact]
    public void Policy_Fields_CoverEveryBoolProperty()
    {
        var props = typeof(TagPolicy).GetProperties().Where(p => p.PropertyType == typeof(bool)).Select(p => p.Name).Order();
        Assert.Equal(props, TagPolicy.Fields.Select(f => f.Key).Order());
        Assert.All(TagPolicy.Fields, f => Assert.Equal(typeof(TagPolicy).GetProperty(f.Key)!.GetValue(TagPolicy.Default), f.Get(TagPolicy.Default)));
    }
}
