using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PlexPod.Core.ItunesDb;
using PlexPod.Core.Sync;
using PlexPod.Media.Pipeline;
using PlexPod.Media.Tags;

namespace PlexPod.Media.Sync;

/// <summary>
/// The stock firmware's database as a view of the manifest (M6 plan §4.2): built in full from the
/// tracks PlexPod owns and the selected playlists, never edited in place.
/// </summary>
internal static class StockLibrary
{
    /// <summary>Room for the database and its temp copy (written beside the old one, then renamed).</summary>
    public static long DatabaseReserve(int tracks) => 2 * ((4L << 20) + (tracks * 4096L));

    public static ulong TrackDbid(string ratingKey) => ItunesDbWriter.StableId("track|" + ratingKey);

    public static ulong PlaylistId(string ratingKey) => ItunesDbWriter.StableId("playlist|" + ratingKey);

    /// <summary>
    /// What the database is built from: tracks (with the stamp that changes when their metadata does)
    /// and playlists with their entries. A different signature means the database needs rebuilding.
    /// </summary>
    public static string Signature(IEnumerable<SyncTrackTarget> targets, IEnumerable<SyncPlaylistTarget> playlists, string masterName)
    {
        var b = new StringBuilder();
        b.Append("v1|").Append(masterName).Append('\n');
        foreach (var t in targets.OrderBy(t => t.Track.RatingKey, StringComparer.Ordinal))
        {
            b.Append(CultureInfo.InvariantCulture, $"T|{t.Track.RatingKey}|{t.MetadataStamp}|{t.Part.PartId}|{t.Part.Key}|{t.DesiredPath}\n");
        }

        foreach (var p in playlists)
        {
            b.Append(CultureInfo.InvariantCulture, $"P|{p.Playlist.RatingKey}|{p.Playlist.Title}|");
            b.AppendJoin(',', p.Contents.Entries.Select(e => e.Unavailable ? "-" : e.Track?.RatingKey ?? "-"));
            b.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));
    }

    /// <summary>The database record for a file just written, keeping its id, added date, plays and rating.</summary>
    public static ItdbTrack Record(ProcessedTrack processed, string devicePath, string ratingKey, ItdbTrack? previous, byte plexRating)
    {
        var tags = processed.Tags;
        var audio = processed.Audio;
        return new ItdbTrack
        {
            Dbid = previous?.Dbid ?? TrackDbid(ratingKey),
            IpodPath = IpodPath.ToColon(devicePath),
            Title = tags?.Title ?? Path.GetFileNameWithoutExtension(devicePath),
            Artist = tags?.Artist,
            Album = tags?.Album,
            AlbumArtist = tags?.AlbumArtist,
            Genre = tags?.Genre,
            Composer = tags?.Composer,
            Comment = tags?.Comment,
            Grouping = tags?.Grouping,
            Compilation = tags?.Compilation ?? false,
            Kind = audio.Kind ?? ItdbFileKinds.FromExtension(Path.GetExtension(devicePath)) ?? ItdbFileKind.Aac,
            TrackNumber = (uint)Math.Max(0, tags?.TrackNumber ?? 0),
            TrackCount = (uint)Math.Max(0, tags?.TrackTotal ?? 0),
            DiscNumber = (uint)Math.Max(0, tags?.DiscNumber ?? 0),
            DiscCount = (uint)Math.Max(0, tags?.DiscTotal ?? 0),
            Year = YearOf(tags?.Date),
            LengthMs = audio.LengthMs,
            Bitrate = audio.Bitrate,
            SampleRate = audio.SampleRate,
            SizeBytes = (uint)Math.Min(uint.MaxValue, processed.SizeBytes),
            Pregap = audio.Gapless?.Pregap ?? 0,
            Postgap = audio.Gapless?.Postgap ?? 0,
            SampleCount = audio.Gapless?.SampleCount ?? 0,
            GaplessTrack = audio.Gapless is not null,
            Added = previous?.Added ?? DateTime.Now,
            Modified = DateTime.Now,
            PlayCount = previous?.PlayCount ?? 0,
            LastPlayed = previous?.LastPlayed,
            SkipCount = previous?.SkipCount ?? 0,
            LastSkipped = previous?.LastSkipped,
            Rating = previous?.Rating ?? plexRating,
        };
    }

    /// <summary>For a file PlexPod adopted (no record in the manifest): read what the database needs from the file.</summary>
    public static ItdbTrack? FromFile(string local, ManifestTrack owned)
    {
        TagReadBack back;
        try
        {
            back = TagWriter.ReadBack(local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            return null;
        }

        string? F(string key) => back.Fields.FirstOrDefault(f => f.Key == key).Value;
        uint N(string key) => uint.TryParse(F(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var (length, bitrate, rate) = TagWriter.ReadAudio(local);
        var ext = Path.GetExtension(local);
        return new ItdbTrack
        {
            Dbid = TrackDbid(owned.RatingKey),
            IpodPath = IpodPath.ToColon(owned.DevicePath),
            Title = F(Core.Tagging.TagSet.TitleKey) ?? Path.GetFileNameWithoutExtension(local),
            Artist = F(Core.Tagging.TagSet.ArtistKey),
            Album = F(Core.Tagging.TagSet.AlbumKey),
            AlbumArtist = F(Core.Tagging.TagSet.AlbumArtistKey),
            Genre = F(Core.Tagging.TagSet.GenreKey),
            Composer = F(Core.Tagging.TagSet.ComposerKey),
            Comment = F(Core.Tagging.TagSet.CommentKey),
            Grouping = F(Core.Tagging.TagSet.GroupingKey),
            Kind = ItdbFileKinds.FromExtension(ext, TagWriter.IsAppleLossless(local) ? "alac" : null) ?? ItdbFileKind.Aac,
            TrackNumber = N(Core.Tagging.TagSet.TrackNumberKey),
            TrackCount = N(Core.Tagging.TagSet.TrackTotalKey),
            DiscNumber = N(Core.Tagging.TagSet.DiscNumberKey),
            DiscCount = N(Core.Tagging.TagSet.DiscTotalKey),
            Year = YearOf(F(Core.Tagging.TagSet.DateKey)),
            LengthMs = length,
            Bitrate = bitrate,
            SampleRate = rate,
            SizeBytes = (uint)Math.Min(uint.MaxValue, owned.SizeBytes),
            Added = DateTime.Now,
        };
    }

    /// <summary>
    /// Builds the database: every owned track (sorted by album artist, album, disc, track) and each
    /// selected playlist. P1 holds here too: a playlist with a track that didn't make it onto the iPod
    /// keeps its previous version from the database being replaced, or is left out with a failure.
    /// </summary>
    public static ItdbLibrary Build(
        IEnumerable<ManifestTrack> owned,
        IReadOnlyList<SyncPlaylistTarget> playlists,
        string deviceId,
        string masterName,
        Func<ManifestTrack, ItdbTrack?> fallback,
        ItdbReadResult? previous,
        List<string> failures,
        List<string> warnings)
    {
        var records = new List<(ManifestTrack Owned, ItdbTrack Record)>();
        var seen = new HashSet<ulong>();
        foreach (var t in owned.Where(t => IpodPath.IsMusicFile(t.DevicePath)))
        {
            var record = t.Stock ?? fallback(t);
            if (record is null)
            {
                warnings.Add($"Left {t.DevicePath} out of the iPod's library: its tags couldn't be read.");
                continue;
            }

            record = record with { IpodPath = IpodPath.ToColon(t.DevicePath), SizeBytes = (uint)Math.Min(uint.MaxValue, t.SizeBytes) };
            if (seen.Add(record.Dbid))
            {
                records.Add((t, record));
            }
        }

        records.Sort((a, b) => CompareForLibrary(a.Record, b.Record));
        var dbidByKey = records.GroupBy(r => r.Owned.RatingKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Record.Dbid, StringComparer.Ordinal);
        var lists = new List<ItdbPlaylist>();
        foreach (var target in playlists)
        {
            var id = PlaylistId(target.Playlist.RatingKey);
            var entries = new List<ulong>();
            var missing = new List<string>();
            foreach (var entry in target.Contents.Entries.Where(e => !e.Unavailable && e.Track is { IsAvailable: true }))
            {
                if (dbidByKey.TryGetValue(entry.Track!.RatingKey, out var dbid))
                {
                    entries.Add(dbid);
                }
                else
                {
                    missing.Add(entry.Track.Title);
                }
            }

            if (missing.Count == 0)
            {
                lists.Add(new ItdbPlaylist(id, target.Playlist.Title, entries));
                continue;
            }

            var kept = PreviousVersion(previous, id, seen);
            if (kept is not null)
            {
                lists.Add(kept with { Name = target.Playlist.Title });
                failures.Add($"Playlist \"{target.Playlist.Title}\" not updated: {missing.Count} of its tracks didn't copy ({string.Join(", ", missing.Distinct().Take(3))}). The previous version stays.");
            }
            else
            {
                failures.Add($"Playlist \"{target.Playlist.Title}\" left off the iPod for now: {missing.Count} of its tracks didn't copy ({string.Join(", ", missing.Distinct().Take(3))}).");
            }
        }

        return new ItdbLibrary(
            ItunesDbWriter.StableId("db|" + deviceId),
            ItunesDbWriter.StableId("library|" + deviceId),
            masterName,
            ItunesDbWriter.StableId("master|" + deviceId),
            [.. records.Select(r => r.Record)],
            lists);
    }

    /// <summary>The playlist as it is in the database on the iPod, if every one of its tracks is still there.</summary>
    private static ItdbPlaylist? PreviousVersion(ItdbReadResult? previous, ulong id, HashSet<ulong> present)
    {
        var old = previous?.Playlists.FirstOrDefault(p => !p.IsMaster && p.Id == id);
        if (old is null)
        {
            return null;
        }

        var dbidById = previous!.Tracks.ToDictionary(t => t.Id, t => t.Track.Dbid);
        var entries = old.TrackIds.Select(i => dbidById.GetValueOrDefault(i)).ToList();
        return entries.All(present.Contains) ? new ItdbPlaylist(id, old.Name, entries) : null;
    }

    private static int CompareForLibrary(ItdbTrack a, ItdbTrack b)
    {
        var c = string.Compare(a.AlbumArtist ?? a.Artist, b.AlbumArtist ?? b.Artist, StringComparison.OrdinalIgnoreCase);
        if (c == 0)
        {
            c = string.Compare(a.Album, b.Album, StringComparison.OrdinalIgnoreCase);
        }

        if (c == 0)
        {
            c = a.DiscNumber.CompareTo(b.DiscNumber);
        }

        if (c == 0)
        {
            c = a.TrackNumber.CompareTo(b.TrackNumber);
        }

        if (c == 0)
        {
            c = string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        }

        return c != 0 ? c : a.Dbid.CompareTo(b.Dbid);
    }

    private static uint YearOf(string? date) =>
        date is { Length: >= 4 } && uint.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : 0;
}
