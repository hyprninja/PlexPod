using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RockPlex.App.Services;

/// <summary>
/// The Plex account token, encrypted with DPAPI for the current Windows user in <c>token.bin</c>.
/// Never written in plaintext and never logged.
/// </summary>
public sealed class TokenStore(AppPaths paths, ILogger<TokenStore> logger)
{
    private static readonly byte[] Entropy = "RockPlex.PlexToken.v1"u8.ToArray();

    public string? Load()
    {
        try
        {
            if (!File.Exists(paths.Token))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(paths.Token), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
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
