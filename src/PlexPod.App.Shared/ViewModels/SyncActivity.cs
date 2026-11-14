namespace PlexPod.App.ViewModels;

/// <summary>What the sync area is doing (docs/state-machines.md §1). One at a time: the operation gate enforces it.</summary>
public enum SyncActivity
{
    Idle,

    /// <summary>Reading the iPod and comparing with Plex. Nothing is written; Stop simply abandons it.</summary>
    Planning,

    /// <summary>The plan is on screen, waiting for Start sync or Cancel.</summary>
    Confirming,

    Syncing,

    /// <summary>Stop was pressed; the engine finishes the file it is writing, then saves its records.</summary>
    Stopping,

    SendingPlays,
}

/// <summary>The allowed moves between <see cref="SyncActivity"/> states, as drawn in docs/state-machines.md §1.</summary>
public static class SyncActivities
{
    public static bool CanMove(SyncActivity from, SyncActivity to) => (from, to) switch
    {
        (SyncActivity.Idle, SyncActivity.Planning or SyncActivity.SendingPlays) => true,
        (SyncActivity.Planning, SyncActivity.Confirming or SyncActivity.Idle) => true,
        (SyncActivity.Confirming, SyncActivity.Syncing or SyncActivity.Idle) => true,
        (SyncActivity.Syncing, SyncActivity.Stopping or SyncActivity.Idle) => true,
        (SyncActivity.Stopping, SyncActivity.Idle) => true,
        (SyncActivity.SendingPlays, SyncActivity.Idle) => true,
        _ => false,
    };

    /// <summary>True while an operation holds the sync area (anything but <see cref="SyncActivity.Idle"/>).</summary>
    public static bool IsBusy(this SyncActivity activity) => activity != SyncActivity.Idle;
}
