namespace PlexPod.Media.Sync;

/// <summary>
/// How a sync ended (docs/state-machines.md §2). Derived from <see cref="SyncResult.Completed"/> and
/// <see cref="SyncResult.Stopped"/> in the order the app has always read them: a sync that finished all
/// its work counts as complete even if Stop arrived after the last step.
/// </summary>
public enum SyncOutcome
{
    /// <summary>Everything planned was done, with no failures.</summary>
    Complete,

    /// <summary>The user pressed Stop. The iPod is consistent; the next sync finishes the job.</summary>
    Stopped,

    /// <summary>Ran to the end or was cut short (error, disconnect) with failures to report. The iPod is consistent.</summary>
    FinishedWithProblems,
}

/// <summary>
/// The steps of a sync, in the order they run (docs/state-machines.md §3). Which ones run depends on the
/// firmware and on whether space is tight (make-room mode runs <see cref="Removing"/> early).
/// </summary>
public enum SyncPhase
{
    Preparing,
    ReadingPlays,
    SendingPlays,
    Moving,
    CheckingTags,
    Copying,
    WritingLibrary,
    WritingArt,
    WritingPlaylists,
    CheckingLibrary,
    CheckingPlaylists,
    Removing,
    Finishing,
}

public static class SyncPhases
{
    /// <summary>The phase as the user sees it in the progress line.</summary>
    public static string Describe(this SyncPhase phase) => phase switch
    {
        SyncPhase.Preparing => "Preparing",
        SyncPhase.ReadingPlays => "Reading plays on the iPod",
        SyncPhase.SendingPlays => "Sending plays to Plex",
        SyncPhase.Moving => "Moving",
        SyncPhase.CheckingTags => "Checking tags",
        SyncPhase.Copying => "Copying to iPod",
        SyncPhase.WritingLibrary => "Writing the iPod's library",
        SyncPhase.WritingArt => "Writing album art",
        SyncPhase.WritingPlaylists => "Writing playlists",
        SyncPhase.CheckingLibrary => "Checking the iPod's library",
        SyncPhase.CheckingPlaylists => "Checking playlists",
        SyncPhase.Removing => "Removing",
        SyncPhase.Finishing => "Finishing",
        _ => phase.ToString(),
    };
}
