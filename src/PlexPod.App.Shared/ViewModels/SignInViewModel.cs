using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlexPod.App.Services;
using PlexPod.Plex;
using PlexPod.Plex.Auth;

namespace PlexPod.App.ViewModels;

/// <summary>Where sign-in stands (docs/state-machines.md §4).</summary>
public enum SignInState
{
    /// <summary>Checking the saved token with plex.tv (at start-up).</summary>
    Checking,

    SignedOut,

    /// <summary>Waiting for the user to approve PlexPod in the browser.</summary>
    SigningIn,

    SignedIn,

    /// <summary>A saved token, but plex.tv couldn't be reached to check it. Kept; Refresh checks again.</summary>
    Offline,
}

/// <summary>
/// Plex PIN sign-in, token persistence (DPAPI on Windows, the Keychain on macOS) and sign-out.
/// <see cref="State"/> is the state machine; <see cref="IsSignedIn"/> and <see cref="IsSignedOut"/> derive from it.
/// </summary>
public sealed partial class SignInViewModel(
    IPlexAuthService auth,
    ITokenStore tokens,
    InMemoryPlexTokenProvider tokenProvider,
    IBrowserLauncher browser,
    ILogger<SignInViewModel> logger) : ObservableObject
{
    public event EventHandler? SignedIn;

    public event EventHandler? SignedOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsSignedOut))]
    public partial SignInState State { get; private set; } = SignInState.Checking;

    /// <summary>A token is in use: checked (<see cref="SignInState.SignedIn"/>) or kept while plex.tv is unreachable.</summary>
    public bool IsSignedIn => State is SignInState.SignedIn or SignInState.Offline;

    /// <summary>The "Sign in with Plex" button shows: no token, or one being obtained.</summary>
    public bool IsSignedOut => State is SignInState.SignedOut or SignInState.SigningIn;

    [ObservableProperty]
    public partial string? Username { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>
    /// Restores the saved token. Invalid (401) → discarded and signed out. Unreachable → kept, and
    /// reported as signed in so the user can retry with Refresh once the network is back.
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken ct)
    {
        var token = tokens.Load();
        if (token is null)
        {
            State = SignInState.SignedOut;
            return false;
        }

        // Re-checking an offline sign-in keeps it in use meanwhile, so the window doesn't flicker.
        if (State != SignInState.Offline)
        {
            State = SignInState.Checking;
        }

        try
        {
            var result = await auth.ValidateTokenAsync(token, ct);
            switch (result.Status)
            {
                case TokenStatus.Valid:
                    Accept(token, result.Username, SignInState.SignedIn);
                    Status = string.Empty;
                    return true;
                case TokenStatus.Invalid:
                    logger.LogInformation("Saved Plex token was revoked; signing out");
                    var wasInUse = IsSignedIn;
                    tokens.Clear();
                    tokenProvider.Set(null);
                    Username = null;
                    State = SignInState.SignedOut;
                    Status = "Your Plex sign-in expired. Please sign in again.";
                    if (wasInUse)
                    {
                        // It was in use offline: clear the library as Sign out does.
                        SignedOut?.Invoke(this, EventArgs.Empty);
                    }

                    return false;
                default:
                    Accept(token, Username, SignInState.Offline);
                    Status = "Can't reach plex.tv right now. Press Refresh to retry.";
                    return true;
            }
        }
        finally
        {
            // Cancelled or failed mid-check: never leave the window without a way to sign in.
            if (State == SignInState.Checking)
            {
                State = SignInState.SignedOut;
            }
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SignInAsync(CancellationToken ct)
    {
        State = SignInState.SigningIn;
        try
        {
            Status = "Opening Plex in your browser…";
            var pin = await auth.CreatePinAsync(ct);
            browser.Open(auth.BuildAuthUrl(pin));
            Status = "Waiting for Plex… approve PlexPod in the browser window.";
            var token = await auth.PollForTokenAsync(pin, ct);
            var validation = await auth.ValidateTokenAsync(token, ct);
            tokens.Save(token);
            Accept(token, validation.Username, SignInState.SignedIn);
            Status = string.Empty;
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = "Sign-in cancelled.";
        }
        catch (PinExpiredException)
        {
            Status = "The sign-in code expired. Try again.";
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Sign-in failed");
            Status = "Couldn't reach plex.tv. Check your connection and try again.";
        }
        finally
        {
            if (State == SignInState.SigningIn)
            {
                State = SignInState.SignedOut;
            }
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        tokens.Clear();
        tokenProvider.Set(null);
        State = SignInState.SignedOut;
        Username = null;
        Status = string.Empty;
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    private void Accept(string token, string? username, SignInState state)
    {
        tokenProvider.Set(token);
        Username = username;
        State = state;
    }
}
