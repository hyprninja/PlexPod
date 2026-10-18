using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlexPod.Plex.Http;

/// <summary>
/// Lenient JSON settings for Plex: numbers may arrive as strings and strings (e.g. keys) as numbers;
/// booleans may arrive as 0/1. Property names are matched case-sensitively.
/// </summary>
internal static class PlexJson
{
    // Case-sensitive on purpose: Plex sends both "guid" (string) and "Guid" (array of external IDs)
    // on the same object. Attributes are camelCase; child arrays (Media, Part, Genre…) are named explicitly.
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new LenientStringConverter(), new LenientBoolConverter() },
    };

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        var value = await response.Content.ReadFromJsonAsync<T>(Options, ct).ConfigureAwait(false);
        return value ?? throw new PlexProtocolException($"Plex returned an empty {typeof(T).Name}.");
    }

    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => SkipAndReturnNull(ref reader),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);

        private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
        {
            reader.Skip();
            return null;
        }
    }

    private sealed class LenientBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Number => reader.GetInt64() != 0,
                JsonTokenType.String => reader.GetString() is "1" or "true" or "True",
                _ => false,
            };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
            writer.WriteBooleanValue(value);
    }
}

/// <summary>Plex answered, but not with something PlexPod understands.</summary>
public sealed class PlexProtocolException(string message) : Exception(message);
