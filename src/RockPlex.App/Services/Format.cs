using System.Globalization;

namespace RockPlex.App.Services;

/// <summary>Display formatting. Sizes use 1024-based units labelled GB, as Windows Explorer does.</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {units[unit]}");
    }

    public static string Count(int value, string singular, string? plural = null) =>
        string.Create(CultureInfo.CurrentCulture, $"{value:N0} {(value == 1 ? singular : plural ?? singular + "s")}");
}
