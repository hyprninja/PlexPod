using Microsoft.Extensions.Logging.Abstractions;
using PlexPod.App.Services;
using PlexPod.App.ViewModels;
using PlexPod.Core.Media;
using PlexPod.Core.Models;
using PlexPod.Media;
using PlexPod.Media.Ffmpeg;
using PlexPod.Media.Pipeline;
using PlexPod.Media.Sync;
using PlexPod.Plex;
using PlexPod.Plex.Auth;
using PlexPod.Tests.Media;

namespace PlexPod.Tests.App;

/// <summary>The explicit state machines of docs/state-machines.md.</summary>
public sealed class StateMachineTests
{
    // ---- §1 Sync activity -------------------------------------------------------------------------

    /// <summary>Exactly the arrows drawn in docs/state-machines.md §1.</summary>
    private static readonly HashSet<(SyncActivity, SyncActivity)> Drawn =
    [
        (SyncActivity.Idle, SyncActivity.Planning),
        (SyncActivity.Idle, SyncActivity.SendingPlays),
        (SyncActivity.Planning, SyncActivity.Confirming),
        (SyncActivity.Planning, SyncActivity.Idle),
        (SyncActivity.Confirming, SyncActivity.Syncing),
        (SyncActivity.Confirming, SyncActivity.Idle),
        (SyncActivity.Syncing, SyncActivity.Stopping),
        (SyncActivity.Syncing, SyncActivity.Idle),
        (SyncActivity.Stopping, SyncActivity.Idle),
        (SyncActivity.SendingPlays, SyncActivity.Idle),
    ];

    [Fact]
    public void SyncActivity_AllowsExactlyTheDocumentedMoves()
    {
        foreach (var from in Enum.GetValues<SyncActivity>())
        {
            foreach (var to in Enum.GetValues<SyncActivity>())
            {
                Assert.True(SyncActivities.CanMove(from, to) == Drawn.Contains((from, to)), $"{from} → {to}");
            }
        }
    }

    [Fact]
    public void SyncActivity_EveryStateIsReachableAndLeadsBackToIdle()
    {
        foreach (var state in Enum.GetValues<SyncActivity>().Where(s => s != SyncActivity.Idle))
        {
            Assert.Contains(Drawn, edge => edge.Item2 == state);

            // Follow the table from this state; Idle must come into reach (nothing can get stuck).
            var seen = new HashSet<SyncActivity> { state };
            var frontier = new Queue<SyncActivity>([state]);
            while (frontier.TryDequeue(out var current))
            {
                foreach (var next in Enum.GetValues<SyncActivity>().Where(n => SyncActivities.CanMove(current, n) && seen.Add(n)))
                {
                    frontier.Enqueue(next);
                }
            }

            Assert.True(seen.Contains(SyncActivity.Idle), $"{state} can't get back to Idle");
        }

        Assert.False(SyncActivity.Idle.IsBusy());
        Assert.True(SyncActivity.Stopping.IsBusy());
    }

    // ---- §2 Sync outcome --------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, SyncOutcome.Complete)]
    [InlineData(false, true, SyncOutcome.Stopped)]
    [InlineData(false, false, SyncOutcome.FinishedWithProblems)]
    [InlineData(true, true, SyncOutcome.Complete)] // Stop arrived after the last step: the work is done
    public void SyncOutcome_ReadsCompletedBeforeStopped(bool completed, bool stopped, SyncOutcome expected)
    {
        var result = new SyncResult(completed, stopped, 0, 0, 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, [], [], string.Empty);
        Assert.Equal(expected, result.Outcome);
    }

    // ---- §3 Sync phase ----------------------------------------------------------------------------

    [Fact]
    public void SyncPhase_ShowsTheSameTextAsBefore()
    {
        var shown = new Dictionary<SyncPhase, string>
        {
            [SyncPhase.Preparing] = "Preparing",
            [SyncPhase.ReadingPlays] = "Reading plays on the iPod",
            [SyncPhase.SendingPlays] = "Sending plays to Plex",
            [SyncPhase.Moving] = "Moving",
            [SyncPhase.CheckingTags] = "Checking tags",
            [SyncPhase.Copying] = "Copying to iPod",
            [SyncPhase.WritingLibrary] = "Writing the iPod's library",
            [SyncPhase.WritingArt] = "Writing album art",
            [SyncPhase.WritingPlaylists] = "Writing playlists",
            [SyncPhase.CheckingLibrary] = "Checking the iPod's library",
            [SyncPhase.CheckingPlaylists] = "Checking playlists",
            [SyncPhase.Removing] = "Removing",
            [SyncPhase.Finishing] = "Finishing",
        };

        Assert.Equal(Enum.GetValues<SyncPhase>().Length, shown.Count);
        foreach (var (phase, text) in shown)
        {
            Assert.Equal(text, phase.Describe());
            Assert.Equal(text, new SyncProgress(phase, 0, 0, string.Empty, 0).PhaseText);
        }
    }

    // ---- §4 Sign-in -------------------------------------------------------------------------------

    private sealed class FakeAuth : IPlexAuthService
    {
        public Queue<Func<TokenValidation>> Answers { get; } = new();

        public Task<PlexPin> CreatePinAsync(CancellationToken ct) => Task.FromResult(new PlexPin(1, "CODE", DateTimeOffset.Now.AddMinutes(5)));

        public string BuildAuthUrl(PlexPin pin) => "https://app.plex.tv/auth";

        public Task<string> PollForTokenAsync(PlexPin pin, CancellationToken ct) => Task.FromResult("new-token");

        public Task<TokenValidation> ValidateTokenAsync(string token, CancellationToken ct) => Task.FromResult(Answers.Dequeue()());
    }

    private sealed class MemoryTokens(string? token) : ITokenStore
    {
        public string? Token { get; private set; } = token;

        public string? Load() => Token;

        public void Save(string token) => Token = token;

        public void Clear() => Token = null;
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public void Open(string url)
        {
        }
    }

    private static (SignInViewModel SignIn, FakeAuth Auth, MemoryTokens Tokens, InMemoryPlexTokenProvider Provider) SignIn(string? savedToken)
    {
        var auth = new FakeAuth();
        var tokens = new MemoryTokens(savedToken);
        var provider = new InMemoryPlexTokenProvider();
        return (new SignInViewModel(auth, tokens, provider, new NoBrowser(), NullLogger<SignInViewModel>.Instance), auth, tokens, provider);
    }

    [Fact]
    public void SignIn_StartsChecking_SoTheSignInButtonDoesntFlashAtStartUp()
    {
        var (signIn, _, _, _) = SignIn("saved");
        Assert.Equal(SignInState.Checking, signIn.State);
        Assert.False(signIn.IsSignedIn);
        Assert.False(signIn.IsSignedOut);
    }

    [Fact]
    public async Task SignIn_NoSavedToken_IsSignedOut()
    {
        var (signIn, _, _, _) = SignIn(null);
        Assert.False(await signIn.TryRestoreAsync(CancellationToken.None));
        Assert.Equal(SignInState.SignedOut, signIn.State);
        Assert.True(signIn.IsSignedOut);
    }

    [Fact]
    public async Task SignIn_Offline_KeepsTheToken_AndRefreshRecoversTheNameAndClearsTheMessage()
    {
        var (signIn, auth, _, provider) = SignIn("saved");
        auth.Answers.Enqueue(() => new TokenValidation(TokenStatus.Unreachable));
        Assert.True(await signIn.TryRestoreAsync(CancellationToken.None));
        Assert.Equal(SignInState.Offline, signIn.State);
        Assert.True(signIn.IsSignedIn);
        Assert.Equal("saved", provider.AccountToken);
        Assert.NotEmpty(signIn.Status);

        auth.Answers.Enqueue(() => new TokenValidation(TokenStatus.Valid, "simon"));
        Assert.True(await signIn.TryRestoreAsync(CancellationToken.None));
        Assert.Equal(SignInState.SignedIn, signIn.State);
        Assert.Equal("simon", signIn.Username);
        Assert.Empty(signIn.Status);
    }

    [Fact]
    public async Task SignIn_OfflineTokenFoundRevoked_SignsOut_AndTellsTheAppToClearTheLibrary()
    {
        var (signIn, auth, tokens, provider) = SignIn("saved");
        auth.Answers.Enqueue(() => new TokenValidation(TokenStatus.Unreachable));
        await signIn.TryRestoreAsync(CancellationToken.None);
        var signedOut = 0;
        signIn.SignedOut += (_, _) => signedOut++;

        auth.Answers.Enqueue(() => new TokenValidation(TokenStatus.Invalid));
        Assert.False(await signIn.TryRestoreAsync(CancellationToken.None));
        Assert.Equal(SignInState.SignedOut, signIn.State);
        Assert.Null(tokens.Token);
        Assert.Null(provider.AccountToken);
        Assert.Equal(1, signedOut);
    }

    [Fact]
    public async Task SignIn_CheckThatFails_NeverLeavesTheWindowWithoutASignInButton()
    {
        var (signIn, auth, _, _) = SignIn("saved");
        auth.Answers.Enqueue(() => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => signIn.TryRestoreAsync(CancellationToken.None));
        Assert.Equal(SignInState.SignedOut, signIn.State);
        Assert.True(signIn.IsSignedOut);
    }

    [Fact]
    public async Task SignIn_ThroughTheBrowser_EndsSignedIn_AndSavesTheToken()
    {
        var (signIn, auth, tokens, _) = SignIn(null);
        await signIn.TryRestoreAsync(CancellationToken.None);
        auth.Answers.Enqueue(() => new TokenValidation(TokenStatus.Valid, "simon"));
        await signIn.SignInCommand.ExecuteAsync(null);
        Assert.Equal(SignInState.SignedIn, signIn.State);
        Assert.Equal("new-token", tokens.Token);

        signIn.SignOutCommand.Execute(null);
        Assert.Equal(SignInState.SignedOut, signIn.State);
        Assert.Null(tokens.Token);
    }

    // ---- §6 ffmpeg --------------------------------------------------------------------------------

    [Theory]
    [InlineData("flac", 24, 96_000, true)]
    [InlineData("flac", 16, 48_000, true)]
    [InlineData("flac", 24, 44_100, false)] // bit depth only: dithered without soxr
    [InlineData("flac", 16, 44_100, false)]
    [InlineData("flac", null, null, false)] // not known yet
    [InlineData("mp3", 16, 48_000, false)]  // never re-encoded
    public void Resample_IsNeededOnlyToLowerAFlacSampleRate(string codec, int? bits, int? rate, bool expected) =>
        Assert.Equal(expected, FormatPolicy.NeedsResample(new AudioFormat(codec, codec, null, bits, rate)));

    [Fact]
    public async Task Ffmpeg_IsDetectedOnceAtATime_AndTheStateMatchesWhatWasFound()
    {
        using var dir = new TempDir();
        var settings = new SettingsViewModel(
            new SettingsStore(new AppPaths(dir.File("app")), NullLogger<SettingsStore>.Instance),
            new FfmpegLocator(TestFfmpeg.Runner, NullLogger<FfmpegLocator>.Instance),
            new ConversionCache(new MediaPaths(dir.File("cache"))));
        Assert.Equal(FfmpegState.Detecting, settings.FfmpegState);

        var first = settings.DetectAsync();
        var second = settings.DetectAsync();
        Assert.Same(first, second);
        await first;

        Assert.Equal(
            settings.Ffmpeg switch { null => FfmpegState.Missing, { HasSoxr: false } => FfmpegState.NoSoxr, _ => FfmpegState.Ready },
            settings.FfmpegState);
    }
}
