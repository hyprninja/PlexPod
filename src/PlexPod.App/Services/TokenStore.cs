using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PlexPod.App.Services;

/// <summary>
/// The Plex account token, encrypted with DPAPI for the current Windows user in <c>token.bin</c>.
/// Never written in plaintext and never logged.
/// </summary>
public sealed class TokenStore(AppPaths paths, ILogger<TokenStore> logger)
{
    private static readonly byte[] Entropy = "PlexPod.PlexToken.v1"u8.ToArray();

    /// <summary>Tokens saved before the rename; re-saved under <see cref="Entropy"/> once read.</summary>
    private static readonly byte[] LegacyEntropy = "RockPlex.PlexToken.v1"u8.ToArray();

    public string? Load()
    {
        try
        {
            if (!File.Exists(paths.Token))
            {
                return null;
            }

            var cipher = File.ReadAllBytes(paths.Token);
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                var token = Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, LegacyEntropy, DataProtectionScope.CurrentUser));
                Save(token);
                return token;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Another user's or another machine's file, or corrupt: treat as signed out.
            logger.LogWarning("Stored Plex token could not be decrypted ({Error}); signing out", ex.GetType().Name);
            Clear();
            return null;
        }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        var temp = paths.Token + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, paths.Token, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(paths.Token);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Could not delete token.bin: {Message}", ex.Message);
        }
    }
}
