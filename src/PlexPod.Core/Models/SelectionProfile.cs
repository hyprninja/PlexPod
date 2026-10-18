namespace PlexPod.Core.Models;

/// <summary>
/// A saved pick. <see cref="Guid"/> lets the pick survive Plex reassigning rating keys;
/// <see cref="Title"/> is for display only.
/// </summary>
public sealed record ItemRef(string RatingKey, string Guid, string Title);

/// <summary>The user's selection, stored as live rules (IDs), resolved again against every snapshot.</summary>
public sealed record SelectionProfile(
    string SectionKey,
    bool AllSongs,
    IReadOnlyList<ItemRef> Playlists,
    IReadOnlyList<ItemRef> Artists,
    IReadOnlyList<ItemRef> Albums)
{
    public static SelectionProfile Empty(string sectionKey) => new(sectionKey, false, [], [], []);

    public bool IsEmpty => !AllSongs && Playlists.Count == 0 && Artists.Count == 0 && Albums.Count == 0;
}
