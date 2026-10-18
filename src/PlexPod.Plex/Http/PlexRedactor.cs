using System.Text.RegularExpressions;

namespace PlexPod.Plex.Http;

/// <summary>Removes Plex tokens from text before it is logged.</summary>
public static partial class PlexRedactor
{
    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : TokenPattern().Replace(text, "$1=REDACTED");

    public static string Redact(Uri? uri) => Redact(uri?.ToString());

    [GeneratedRegex(@"(X-Plex-Token|authToken|accessToken)=[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
