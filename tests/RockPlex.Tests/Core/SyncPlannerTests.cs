using RockPlex.Core.Models;
using RockPlex.Core.Sync;
using static RockPlex.Tests.Core.TestData;

namespace RockPlex.Tests.Core;

public sealed class SyncPlannerTests
{
    private const string Stamp = "100|200|1111";

    private static SyncTrackTarget Target(string key, string path, long bytes = 1_000, string stamp = Stamp, long partId = 0, string? partKey = null, long size = 1_000)
    {
        var track = Track(key, size: size);
        var part = track.Parts[0] with { PartId = partId == 0 ? track.Parts[0].PartId : partId, Key = partKey ?? track.Parts[0].Key };
        return new SyncTrackTarget(track with { Parts = [part] }, path, bytes, stamp);
    }

    private static ManifestTrack Owned(SyncTrackTarget t, string? devicePath = null, string? stamp = null, long size = 1_000) => new()
    {
        DevicePath = devicePath ?? t.DesiredPath,
        DesiredPath = t.DesiredPath,
        RatingKey = t.Track.RatingKey,
        PartId = t.Part.PartId,
        PartKey = t.Part.Key,
        SourceSizeBytes = t.Part.SizeBytes,
        SizeBytes = size,
        MetadataStamp = stamp ?? t.MetadataStamp,
    };

    private static DeviceManifest Manifest(params ManifestTrack[] tracks) => DeviceManifest.Empty("dev") with { Tracks = tracks };

    private static SyncPlan Plan(DeviceManifest manifest, SyncTrackTarget[] targets, SyncPlaylistTarget[]? playlists = null, string[]? userFiles = null) =>
        SyncPlanner.Plan(manifest, targets, playlists ?? [], userFiles ?? [], t => t.TrackArtist ?? "Artist");

    [Fact]
    public void EmptyDevice_AddsEverything()
    {
        var plan = Plan(Manifest(), [Target("t1", "/Music/A/2000 - B/01 One.flac"), Target("t2", "/Music/A/2000 - B/02 Two.flac")]);
        Assert.Equal(2, plan.Count(TrackOpKind.Add));
        Assert.Empty(plan.TrackRemovals);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void NothingChanged_IsEmpty()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var plan = Plan(Manifest(Owned(t)), [t]);
        Assert.True(plan.IsEmpty);
        Assert.Equal(TrackOpKind.Keep, Assert.Single(plan.TrackOps).Kind);
    }

    [Fact]
    public void StampChange_IsRetag_InPlace()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var op = Assert.Single(Plan(Manifest(Owned(t, stamp: "old")), [t]).TrackOps);
        Assert.Equal(TrackOpKind.Retag, op.Kind);
        Assert.Equal("/Music/A/01 One.flac", op.DevicePath);
    }

    [Fact]
    public void NewPartKey_IsReplace()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var newer = Target("t1", "/Music/A/01 One.flac", partKey: "/library/parts/1/999/file.flac");
        var op = Assert.Single(Plan(Manifest(Owned(t)), [newer]).TrackOps);
        Assert.Equal(TrackOpKind.Replace, op.Kind);
        Assert.Equal(t.DesiredPath, op.DevicePath);
    }

    [Fact]
    public void ReplaceAtNewPath_RemovesTheOldFileLast()
    {
        var mp3 = Target("t1", "/Music/A/01 One.mp3");
        var flac = Target("t1", "/Music/A/01 One.flac", partId: 77);
        var plan = Plan(Manifest(Owned(mp3)), [flac]);
        var op = Assert.Single(plan.TrackOps);
        Assert.Equal(TrackOpKind.Replace, op.Kind);
        Assert.Equal("/Music/A/01 One.flac", op.DevicePath);
        Assert.Equal("/Music/A/01 One.mp3", Assert.Single(plan.TrackRemovals).DevicePath);
    }

    [Fact]
    public void PathChange_IsMove_AndCarriesRetagWhenStampChanged()
    {
        var before = Target("t1", "/Music/Old Name/01 One.flac");
        var after = Target("t1", "/Music/New Name/01 One.flac", stamp: "new");
        var op = Assert.Single(Plan(Manifest(Owned(before)), [after]).TrackOps);
        Assert.Equal(TrackOpKind.Move, op.Kind);
        Assert.Equal("/Music/New Name/01 One.flac", op.DevicePath);
        Assert.True(op.AlsoRetag);
        Assert.True(op.NeedsPipeline);
    }

    [Fact]
    public void Deselected_IsRemoved()
    {
        var keep = Target("t1", "/Music/A/01 One.flac");
        var gone = Target("t2", "/Music/A/02 Two.flac");
        var plan = Plan(Manifest(Owned(keep), Owned(gone)), [keep]);
        Assert.Equal("t2", Assert.Single(plan.TrackRemovals).RatingKey);
    }

    [Fact]
    public void UserFile_IsNeverClaimed_NewTrackGetsSuffix_CaseInsensitive()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var op = Assert.Single(Plan(Manifest(), [t], userFiles: ["/MUSIC/a/01 one.FLAC"]).TrackOps);
        Assert.Equal("/Music/A/01 One (2).flac", op.DevicePath);
    }

    [Fact]
    public void TwoTracksWithTheSameDesiredPath_GetDistinctPaths()
    {
        var plan = Plan(Manifest(), [Target("t1", "/Music/A/01 Intro.flac"), Target("t2", "/Music/A/01 Intro.flac")]);
        Assert.Equal(["/Music/A/01 Intro (2).flac", "/Music/A/01 Intro.flac"], plan.TrackOps.Select(o => o.DevicePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ExistingSuffixedPath_IsStable()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var plan = Plan(Manifest(Owned(t, devicePath: "/Music/A/01 One (2).flac")), [t], userFiles: ["/Music/A/01 One.flac"]);
        Assert.Equal("/Music/A/01 One (2).flac", Assert.Single(plan.TrackOps).DevicePath);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void DuplicateManifestEntries_ExtraOnesAreRemoved()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var plan = Plan(Manifest(Owned(t), Owned(t, devicePath: "/Music/A/01 One (2).flac")), [t]);
        Assert.Equal("/Music/A/01 One (2).flac", Assert.Single(plan.TrackRemovals).DevicePath);
    }

    private static SyncPlaylistTarget Playlist(string key, string title, params PlaylistEntry[] entries) =>
        new(new PlaylistItem(key, "g", title, false, entries.Length, 0, $"/playlists/{key}/items"), new PlaylistContents(key, entries));

    [Fact]
    public void Playlist_KeepsOrderAndDuplicates_UsesExactManifestPaths()
    {
        var t1 = Target("t1", "/Music/A/01 One.flac");
        var t2 = Target("t2", "/Music/A/02 Two.flac");
        var existing = Owned(t2, devicePath: "/Music/a/02 two.flac");
        var pl = Playlist("p1", "Road/Trip", Entry(1, t2.Track), Entry(2, t1.Track), Entry(3, t2.Track), Entry(4, null, unavailable: true));

        var op = Assert.Single(Plan(Manifest(existing), [t1, t2], [pl]).PlaylistOps);

        Assert.Equal("/Playlists/Road_Trip.m3u8", op.DevicePath);
        Assert.Equal(["/Music/a/02 two.flac", "/Music/A/01 One.flac", "/Music/a/02 two.flac"], PlaylistWriter.TrackLines(op.Content).Select(l => l.Path));
        Assert.Single(op.SkippedEntries);
        Assert.StartsWith("#EXTM3U\n#ROCKPLEX ratingKey=p1\n#EXTINF:200,Artist - Track t2\n", op.Content, StringComparison.Ordinal);
        Assert.True(op.NeedsWrite);
    }

    [Fact]
    public void Playlist_Unchanged_IsNotRewritten_AndPlexRenameMovesIt()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var pl = Playlist("p1", "Chill", Entry(1, t.Track));
        var first = Assert.Single(Plan(Manifest(Owned(t)), [t], [pl]).PlaylistOps);
        var manifest = Manifest(Owned(t)) with { Playlists = [new ManifestPlaylist("p1", first.DevicePath, "Chill", first.ContentHash)] };

        Assert.False(Assert.Single(Plan(manifest, [t], [pl]).PlaylistOps).NeedsWrite);

        var renamed = pl with { Playlist = pl.Playlist with { Title = "Chill Out" } };
        var plan = Plan(manifest, [t], [renamed]);
        Assert.Equal("/Playlists/Chill Out.m3u8", Assert.Single(plan.PlaylistOps).DevicePath);
        Assert.Equal("/Playlists/Chill.m3u8", Assert.Single(plan.PlaylistRemovals).DevicePath);
    }

    [Fact]
    public void Playlist_NameTakenByUserPlaylist_GetsRockPlexSuffix_AndKeepsIt()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var pl = Playlist("p1", "Favourites", Entry(1, t.Track));
        var op = Assert.Single(Plan(Manifest(Owned(t)), [t], [pl], ["/Playlists/favourites.m3u8"]).PlaylistOps);
        Assert.Equal("/Playlists/Favourites (RockPlex).m3u8", op.DevicePath);

        var manifest = Manifest(Owned(t)) with { Playlists = [new ManifestPlaylist("p1", op.DevicePath, "Favourites", op.ContentHash)] };
        var again = Plan(manifest, [t], [pl], ["/Playlists/favourites.m3u8"]);
        Assert.False(Assert.Single(again.PlaylistOps).NeedsWrite);
        Assert.Empty(again.PlaylistRemovals);
    }

    [Fact]
    public void Playlist_Deselected_IsRemoved()
    {
        var t = Target("t1", "/Music/A/01 One.flac");
        var manifest = Manifest(Owned(t)) with { Playlists = [new ManifestPlaylist("p9", "/Playlists/Old.m3u8", "Old", "h")] };
        var plan = Plan(manifest, [t]);
        Assert.Equal("p9", Assert.Single(plan.PlaylistRemovals).RatingKey);
    }

    [Fact]
    public void PlaylistHeader_RoundTrips()
    {
        Assert.Equal("48211", PlaylistWriter.ReadRatingKey(["﻿#EXTM3U", "#ROCKPLEX ratingKey=48211 test-export=2026-10-18T11:38:00"]));
        Assert.Null(PlaylistWriter.ReadRatingKey(["#EXTM3U", "/Music/a.flac"]));
    }

    [Fact]
    public void Fit_Fits_WhenAddsFitInFreeSpace()
    {
        var plan = Plan(Manifest(), [Target("t1", "/Music/a.flac", bytes: 10_000_000)]);
        var fit = FitCheck.Evaluate(plan, 10L << 30);
        Assert.Equal(FitOutcome.Fits, fit.Outcome);
    }

    [Fact]
    public void Fit_NeedsRemovalsFirst_WhenSwappingNearCapacity()
    {
        var gone = Target("t2", "/Music/old.flac");
        var plan = Plan(Manifest(Owned(gone, size: 500L << 20)), [Target("t1", "/Music/new.flac", bytes: 400L << 20)]);
        var fit = FitCheck.Evaluate(plan, (100L << 20) + global::RockPlex.Core.Selection.CapacityCalculator.ReserveBytes);
        Assert.Equal(FitOutcome.FitsAfterRemovals, fit.Outcome);
    }

    [Fact]
    public void Fit_TooBig_ReportsOverage()
    {
        var plan = Plan(Manifest(), [Target("t1", "/Music/a.flac", bytes: 300L << 20)]);
        var fit = FitCheck.Evaluate(plan, (100L << 20) + global::RockPlex.Core.Selection.CapacityCalculator.ReserveBytes);
        Assert.Equal(FitOutcome.TooBig, fit.Outcome);
        Assert.True(fit.OverBy >= 200L << 20);
        Assert.Equal("t1", Assert.Single(fit.LargestWrites).Target.Track.RatingKey);
    }

    [Fact]
    public void Fit_InPlaceRetag_OnlyCountsGrowthPlusOneTempFile()
    {
        var t = Target("t1", "/Music/a.flac", bytes: 100L << 20);
        var plan = Plan(Manifest(Owned(t, stamp: "old", size: 100L << 20)), [t]);
        var fit = FitCheck.Evaluate(plan, 1L << 30);
        Assert.Equal(100L << 20, fit.BytesNeeded);
    }

    [Fact]
    public void Allocator_SuffixesBeforeExtension_AndHonoursOwnPath()
    {
        var allocator = new PathAllocator(["/Music/a.flac", "/Music/a (2).flac"]);
        Assert.Equal("/Music/a (3).flac", allocator.Allocate("/Music/a.flac"));
        Assert.Equal("/Music/a.flac", allocator.Allocate("/Music/A.flac", "/Music/a.flac"));
        Assert.Equal("/Playlists/x (RockPlex).m3u8", new PathAllocator(["/Playlists/X.m3u8"]).Allocate("/Playlists/x.m3u8", " (RockPlex)", null));
    }
}
