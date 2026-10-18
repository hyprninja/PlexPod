using System.Text;
using RockPlex.Core.Tagging;

namespace RockPlex.Tests.Core;

public sealed class DevicePathBuilderTests
{
    [Fact]
    public void BuildsTheStandardLayout()
    {
        var path = DevicePathBuilder.Build(new PathFields("Sigur Rós", 2005, "Takk...", 1, 2, "Hoppípolla"), "FLAC", isMultiDisc: false);
        Assert.Equal("/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac", path);
    }

    [Fact]
    public void MultiDisc_GetsDiscPrefix_NoSubfolder()
    {
        var path = DevicePathBuilder.Build(new PathFields("A", 1999, "B", 2, 3, "C"), "mp3", isMultiDisc: true);
        Assert.Equal("/Music/A/1999 - B/2-03 C.mp3", path);
    }

    [Fact]
    public void MissingValues_FallBack()
    {
        var path = DevicePathBuilder.Build(new PathFields(null, null, " ", null, null, null), ".m4a", isMultiDisc: false);
        Assert.Equal("/Music/Unknown Artist/Unknown Album/Untitled.m4a", path);
    }

    [Theory]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("Vol. 1: Intro", "Vol. 1- Intro")]
    [InlineData("What? *Now* <here> | \"there\"", "What_ _Now_ _here_ _ _there_")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("tab\there", "tab here")]
    [InlineData("CON", "CON_")]
    [InlineData("nul.txt", "nul.txt_")]
    [InlineData("...", "fallback")]
    public void Sanitize_MakesFat32SafeNames(string input, string expected) =>
        Assert.Equal(expected, DevicePathBuilder.Sanitize(input, "fallback"));

    [Fact]
    public void Sanitize_NormalisesToNfc()
    {
        var decomposed = "Sigur Rós";
        Assert.Equal("Sigur Rós", DevicePathBuilder.Sanitize(decomposed, "x"));
        Assert.True(DevicePathBuilder.Sanitize(decomposed, "x").IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void LongNames_AreCapped_WithoutSplittingSurrogates()
    {
        var emoji = string.Concat(Enumerable.Repeat("🎸", 100));
        var segment = DevicePathBuilder.Sanitize(emoji, "x");
        Assert.True(segment.Length <= DevicePathBuilder.MaxSegmentLength);
        Assert.False(char.IsHighSurrogate(segment[^1]));
    }

    [Fact]
    public void WholePath_StaysUnderLimit_AndKeepsExtension()
    {
        var huge = new string('a', 300);
        var path = DevicePathBuilder.Build(new PathFields(huge, 2000, huge, 1, 1, huge), "flac", isMultiDisc: false);
        Assert.True(path.Length <= DevicePathBuilder.MaxPathLength, $"{path.Length} chars");
        Assert.EndsWith(".flac", path, StringComparison.Ordinal);
        Assert.StartsWith("/Music/aaaa", path, StringComparison.Ordinal);
        Assert.Contains("/01 a", path, StringComparison.Ordinal);
    }
}
