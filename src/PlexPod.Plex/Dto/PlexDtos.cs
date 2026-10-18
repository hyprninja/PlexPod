using System.Text.Json.Serialization;

namespace PlexPod.Plex.Dto;

// Shapes of the Plex JSON PlexPod reads. Every collection is optional: Plex omits empty arrays.

internal sealed class PinDto
{
    public long Id { get; set; }
    public string? Code { get; set; }
    public string? AuthToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? ExpiresIn { get; set; }
}

internal sealed class UserDto
{
    public string? Username { get; set; }
    public string? Title { get; set; }
    public string? Email { get; set; }
}

internal sealed class ResourceDto
{
    public string? Name { get; set; }
    public string? ClientIdentifier { get; set; }
    public string? Provides { get; set; }
    public bool Owned { get; set; }
    public string? AccessToken { get; set; }
    public string? ProductVersion { get; set; }
    public List<ConnectionDto>? Connections { get; set; }
}

internal sealed class ConnectionDto
{
    public string? Protocol { get; set; }
    public string? Address { get; set; }
    public int Port { get; set; }
    public string? Uri { get; set; }
    public bool Local { get; set; }
    public bool Relay { get; set; }

    [JsonPropertyName("IPv6")]
    public bool IPv6 { get; set; }
}

internal sealed class ContainerEnvelope
{
    [JsonPropertyName("MediaContainer")]
    public MediaContainerDto? MediaContainer { get; set; }
}

internal sealed class MediaContainerDto
{
    public int Size { get; set; }
    public int? TotalSize { get; set; }
    public int? Offset { get; set; }
    public string? MachineIdentifier { get; set; }
    [JsonPropertyName("Directory")]
    public List<DirectoryDto>? Directory { get; set; }
    [JsonPropertyName("Metadata")]
    public List<MetadataDto>? Metadata { get; set; }
}

internal sealed class DirectoryDto
{
    public string? Key { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public string? Uuid { get; set; }
}

internal sealed class MetadataDto
{
    public string? RatingKey { get; set; }
    public string? Key { get; set; }
    public string? Guid { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public string? ParentRatingKey { get; set; }
    public string? GrandparentRatingKey { get; set; }
    public string? ParentTitle { get; set; }
    public string? GrandparentTitle { get; set; }
    public string? OriginalTitle { get; set; }
    public int? Index { get; set; }
    public int? ParentIndex { get; set; }
    public long? Duration { get; set; }
    public long? UpdatedAt { get; set; }

    /// <summary>The signed-in user's rating, 0–10; absent when unrated.</summary>
    public double? UserRating { get; set; }
    public int? Year { get; set; }
    public string? OriginallyAvailableAt { get; set; }
    public string? Studio { get; set; }
    public int? LeafCount { get; set; }
    public string? Thumb { get; set; }
    public bool Smart { get; set; }
    public string? PlaylistType { get; set; }

    [JsonPropertyName("playlistItemID")]
    public long? PlaylistItemId { get; set; }

    [JsonPropertyName("Media")]
    public List<MediaDto>? Media { get; set; }

    [JsonPropertyName("Genre")]
    public List<TagDto>? Genres { get; set; }

    [JsonPropertyName("Style")]
    public List<TagDto>? Styles { get; set; }

    /// <summary>External IDs (<c>mbid://…</c>, <c>plex://…</c>); only sent with <c>includeGuids=1</c>.</summary>
    [JsonPropertyName("Guid")]
    public List<GuidDto>? Guids { get; set; }
}

internal sealed class TagDto
{
    public string? Tag { get; set; }
}

internal sealed class GuidDto
{
    public string? Id { get; set; }
}

internal sealed class MediaDto
{
    public int? Bitrate { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public string? Container { get; set; }
    [JsonPropertyName("Part")]
    public List<PartDto>? Part { get; set; }
}

internal sealed class PartDto
{
    public long Id { get; set; }
    public string? Key { get; set; }
    public string? File { get; set; }
    public long? Size { get; set; }
    public string? Container { get; set; }
    [JsonPropertyName("Stream")]
    public List<StreamDto>? Stream { get; set; }
}

internal sealed class StreamDto
{
    /// <summary>1 = video, 2 = audio, 3 = subtitle, 4 = lyrics.</summary>
    public int StreamType { get; set; }
    public string? Codec { get; set; }
    public int? BitDepth { get; set; }
    public int? SamplingRate { get; set; }
    public int? Channels { get; set; }
    public int? Bitrate { get; set; }
}
