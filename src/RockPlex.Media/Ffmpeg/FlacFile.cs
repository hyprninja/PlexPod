using RockPlex.Core.Media;

namespace RockPlex.Media.Ffmpeg;

/// <summary>Reads STREAMINFO straight from a file, skipping any ID3v2 glued onto the front.</summary>
public static class FlacFile
{
    public static FlacStreamInfo ReadStreamInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> first = stackalloc byte[10];
        stream.ReadExactly(first);
        var skip = FlacStreamInfo.LeadingId3Length(first);

        stream.Position = skip;
        var header = new byte[4 + FlacStreamInfo.StreamInfoLength];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return FlacStreamInfo.Parse(header.AsSpan(0, read));
    }

    public static FlacStreamInfo? TryReadStreamInfo(string path)
    {
        try
        {
            return ReadStreamInfo(path);
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or IOException)
        {
            return null;
        }
    }
}
