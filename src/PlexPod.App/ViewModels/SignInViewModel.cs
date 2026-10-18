using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlexPod.App.Services;
using PlexPod.Plex;
using PlexPod.Plex.Auth;

namespace PlexPod.App.ViewModels;

/// <summary>Plex PIN sign-in, token persistence (DPAPI) and sign-out.</summary>
public sealed partial class SignInViewModel(
    IPlexAuthService auth,
    TokenStore tokens,
    InMemoryPlexTokenProvider tokenProvider,
    IBrowserLauncher browser,
    ILogger<SignInViewModel> logger) : ObservableObject
{
    public event EventHandler? SignedIn;

    public event EventHandler? SignedOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; private set; }

    public bool IsSignedOut => !IsSignedIn;

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
            return false;
        }

        var result = await auth.ValidateTokenAsync(token, ct);
        switch (result.Status)
        {
            case TokenStatus.Valid:
                Accept(token, result.Username);
                return true;
            case TokenStatus.Invalid:
                logger.LogInformation("Saved Plex token was revoked; signing out");
                tokens.Clear();
                Status = "Your Plex sign-in expired. Please sign in again.";
                return false;
            default:
                Accept(token, null);
                Status = "Can't reach plex.tv right now. Press Refresh to retry.";
                return true;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SignInAsync(CancellationToken ct)
    {
        try
        {
            Status = "Opening Plex in your browser…";
            var pin = await auth.CreatePinAsync(ct);
            browser.Open(auth.BuildAuthUrl(pin));
            Status = "Waiting for Plex… approve PlexPod in the browser window.";
            var token = await auth.PollForTokenAsync(pin, ct);
            var validation = await auth.ValidateTokenAsync(token, ct);
            tokens.Save(token);
            Accept(token, validation.Username);
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
    }

    [RelayCommand]
    private void SignOut()
    {
        tokens.Clear();
        tokenProvider.Set(null);
        IsSignedIn = false;
        Username = null;
        Status = string.Empty;
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    private void Accept(string token, string? username)
    {
        tokenProvider.Set(token);
        Username = username;
        IsSignedIn = true;
    }
}
