using System.Text;
using RockPlex.Core.Stats;

namespace RockPlex.Tests.Core;

public sealed class PlaybackLogParserTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Parse_ReadsLines_SkipsHeadersAndJunk()
    {
        var log = Bytes("# Started Ver. 4.0 1700000000\n1700000100:200000:210000:/Music/A/2000 - B/01 One.flac\nnot a line\n12:x:3:/Music/x\n1700000400:5000:300000:/Music/A/2000 - B/02 Two.flac\n");
        var parsed = PlaybackLogParser.Parse(log);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal(1700000100, parsed.Entries[0].Timestamp);
        Assert.Equal(200000, parsed.Entries[0].ElapsedMs);
        Assert.Equal(210000, parsed.Entries[0].LengthMs);
        Assert.Equal("/Music/A/2000 - B/01 One.flac", parsed.Entries[0].DevicePath);
        Assert.Equal(log.Length, parsed.ConsumedBytes);
        Assert.Equal(log.Length, parsed.Entries[1].EndOffset);
    }

    [Fact]
    public void Parse_PathWithColons_KeepsWholePath()
    {
        var parsed = PlaybackLogParser.Parse(Bytes("1:2:3:/Music/A/2000 - Live: Part 1/01 Intro: Go.mp3\n"));
        Assert.Equal("/Music/A/2000 - Live: Part 1/01 Intro: Go.mp3", Assert.Single(parsed.Entries).DevicePath);
    }

    [Fact]
    public void Parse_Crlf_AndUtf8()
    {
        var parsed = PlaybackLogParser.Parse(Bytes("1:2:3:/Music/Björk/2001 - Vespertine/01 Hidden Place.flac\r\n"));
        Assert.Equal("/Music/Björk/2001 - Vespertine/01 Hidden Place.flac", Assert.Single(parsed.Entries).DevicePath);
    }

    [Fact]
    public void Parse_TruncatedLastLine_IsLeftForLater()
    {
        var complete = "1:2:3:/Music/a.flac\n";
        var parsed = PlaybackLogParser.Parse(Bytes(complete + "4:5:6:/Music/b.fl"));
        Assert.Single(parsed.Entries);
        Assert.Equal(Encoding.UTF8.GetByteCount(complete), parsed.ConsumedBytes);
    }

    [Fact]
    public void Parse_FromOffset_ResumesMidFile()
    {
        var first = "# header\n1:2:3:/Music/a.flac\n";
        var log = Bytes(first + "4:5:6:/Music/b.flac\n");
        var parsed = PlaybackLogParser.Parse(log, Encoding.UTF8.GetByteCount(first));
        Assert.Equal("/Music/b.flac", Assert.Single(parsed.Entries).DevicePath);
    }

    [Fact]
    public void Fingerprint_DependsOnFirstLineOnly()
    {
        Assert.Equal(PlaybackLogParser.Fingerprint(Bytes("# A\n1:2:3:/x\n")), PlaybackLogParser.Fingerprint(Bytes("# A\n9:9:9:/y\n")));
        Assert.NotEqual(PlaybackLogParser.Fingerprint(Bytes("# A\n")), PlaybackLogParser.Fingerprint(Bytes("# B\n")));
    }

    [Theory]
    [InlineData(189_999, 211_111, false)]
    [InlineData(190_000, 211_111, true)]
    [InlineData(9_000, 10_000, true)]
    [InlineData(8_999, 10_000, false)]
    [InlineData(5_000, 0, false)]
    public void PlayRule_NinetyPercent(long elapsed, long length, bool counts) =>
        Assert.Equal(counts, PlayRule.Counts(new PlaybackLogEntry(0, elapsed, length, "/Music/a", 0)));

    [Fact]
    public void PlayedAt_IsLocalWallClock()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus10", TimeSpan.FromHours(10), "Plus10", "Plus10");
        var entry = new PlaybackLogEntry(new DateTimeOffset(2026, 10, 1, 20, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(), 1, 1, "/Music/a", 0);
        var at = entry.PlayedAt(zone);
        Assert.Equal(new DateTime(2026, 10, 1, 20, 30, 0), at.DateTime);
        Assert.Equal(TimeSpan.FromHours(10), at.Offset);
    }
}

public sealed class ChangelogParserTests
{
    [Fact]
    public void Parse_ReadsTagsAndEscapes()
    {
        const string text = "## Changelog version 1\n"
            + "artist=\"Say \\\"Hi\\\"\" title=\"Back\\\\slash\" filename=\"/Music/A/2000 - B/01 One.flac\" rating=\"8\" playcount=\"3\" lastplayed=\"42\"\n"
            + "\n"
            + "comment=\"two\\nlines\" filename=\"/Music/x.mp3\" rating=\"0\"\r\n";
        var entries = ChangelogParser.Parse(text);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Say \"Hi\"", entries[0].Tags["artist"]);
        Assert.Equal("Back\\slash", entries[0].Tags["title"]);
        Assert.Equal("/Music/A/2000 - B/01 One.flac", entries[0].Filename);
        Assert.Equal(8, entries[0].Rating);
        Assert.Equal(3, entries[0].PlayCount);
        Assert.Equal("two\nlines", entries[1].Tags["comment"]);
        Assert.Equal(0, entries[1].Rating);
    }

    [Fact]
    public void Parse_TruncatedLine_KeepsCompletePairs()
    {
        var entry = Assert.Single(ChangelogParser.Parse("filename=\"/Music/a.flac\" rating=\"7\" title=\"unterminat"));
        Assert.Equal("/Music/a.flac", entry.Filename);
        Assert.Equal(7, entry.Rating);
        Assert.False(entry.Tags.ContainsKey("title"));
    }

    [Fact]
    public void Parse_Junk_IsIgnored()
    {
        Assert.Empty(ChangelogParser.Parse("garbage\nno pairs here\n"));
        Assert.Null(Assert.Single(ChangelogParser.Parse("filename=\"/a\" rating=\"x\"")).Rating);
    }
}

public sealed class StatsPlannerTests
{
    private const string PathA = "/Music/A/2000 - B/01 One.flac";
    private const string PathB = "/Music/A/2000 - B/02 Two.flac";

    private static readonly Dictionary<string, string> Owned = new() { [PathA] = "101", [PathB] = "102" };

    private static LogFile Log(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private static StatsPlan Plan(
        IReadOnlyList<LogFile> logs,
        StatsState? state = null,
        string? changelog = null,
        RatingMode mode = RatingMode.IpodWins,
        Func<string, bool>? plexHasRating = null) =>
        StatsPlanner.Plan(logs, ChangelogParser.Parse(changelog ?? ""), state ?? StatsState.Empty, Owned, mode, plexHasRating ?? (_ => false));

    private static StatsState After(IReadOnlyList<LogFile> logs, StatsPlan plan) =>
        StatsState.Empty with { Bookmarks = StatsPlanner.Bookmarks(logs, plan.EndOffsets) };

    [Fact]
    public void Plays_FilterByRuleAndOwnership()
    {
        var plan = Plan([Log("playback.log", $"# s\n1:200:200:{PathA}\n2:10:200:{PathB}\n3:200:200:/Music/Mine/x.mp3\n")]);
        Assert.Equal([StepKind.Send, StepKind.TooShort, StepKind.NotOurs], plan.Steps.Select(s => s.Kind));
        Assert.Equal(1, plan.PlaysToSend);
        Assert.Equal("101", plan.Steps[0].RatingKey);
    }

    [Fact]
    public void Plays_MatchCaseInsensitively()
    {
        var plan = Plan([Log("playback.log", $"1:200:200:{PathA.ToUpperInvariant()}\n")]);
        Assert.Equal("101", Assert.Single(plan.Steps).RatingKey);
    }

    [Fact]
    public void SameInputTwice_SendsNothingSecondTime()
    {
        var logs = new[] { Log("playback.log", $"# s\n1:200:200:{PathA}\n") };
        var first = Plan(logs);
        Assert.Equal(1, first.PlaysToSend);
        var second = Plan(logs, After(logs, first));
        Assert.Empty(second.Steps);
        Assert.True(second.IsEmpty);
    }

    [Fact]
    public void Bookmark_ResumesWhenLogGrows()
    {
        LogFile[] before = [Log("playback.log", $"# s\n1:200:200:{PathA}\n")];
        var first = Plan(before);
        var second = Plan([Log("playback.log", $"# s\n1:200:200:{PathA}\n2:200:200:{PathB}\n")], After(before, first));
        Assert.Equal("102", Assert.Single(second.Steps).RatingKey);
        Assert.Equal(first.EndOffsets[0], second.StartOffsets[0]);
    }

    [Fact]
    public void RotatedLog_IsRecognisedByItsContent()
    {
        var start = $"# s\n1:200:200:{PathA}\n";
        LogFile[] before = [Log("playback.log", start)];
        var first = Plan(before);

        // Rockbox renamed it (with one more play added first) and started a new log with the same header.
        LogFile[] after = [Log("playback_0001.log", start + $"2:200:200:{PathB}\n"), Log("playback.log", $"# s\n3:200:200:{PathB}\n")];
        var second = Plan(after, After(before, first));
        Assert.Equal([(0, "102"), (1, "102")], second.Steps.Select(s => (s.LogIndex, s.RatingKey!)));
        Assert.Equal(2, StatsPlanner.Bookmarks(after, second.EndOffsets).Count);
    }

    [Theory]
    [InlineData("# s\n3:200:200:/Music/A/2000 - B/02 Two.flac\n")] // shorter than the bookmark
    [InlineData("# s\n3:200:200:/Music/A/2000 - B/02 Two.flac\n4:200:200:/Music/A/2000 - B/02 Two.flac\n")] // longer
    public void ReplacedLogWithSameHeader_IsReadFromStart(string replacement)
    {
        LogFile[] before = [Log("playback.log", $"# s\n1:200:200:{PathA}\n")];
        var second = Plan([Log("playback.log", replacement)], After(before, Plan(before)));
        Assert.All(second.Steps, s => Assert.Equal("102", s.RatingKey));
        Assert.Equal(0, second.StartOffsets[0]);
    }

    [Fact]
    public void Bookmark_ForAHeaderOnlyLog_IsNotSaved()
    {
        LogFile[] logs = [Log("playback.log", "")];
        Assert.Empty(StatsPlanner.Bookmarks(logs, Plan(logs).EndOffsets));
    }

    [Fact]
    public void Ratings_SentOnlyWhenChanged()
    {
        var log = $"filename=\"{PathA}\" rating=\"8\"\nfilename=\"{PathB}\" rating=\"6\"\n";
        var state = StatsState.Empty with { RatingsSent = new Dictionary<string, int> { ["101"] = 8 } };
        var plan = Plan([], state, log);
        var r = Assert.Single(plan.Ratings);
        Assert.Equal(("102", 6), (r.RatingKey, r.Rating));
    }

    [Fact]
    public void Ratings_UnratedZero_NeverSent_ButClearingIs()
    {
        var log = $"filename=\"{PathA}\" rating=\"0\"\nfilename=\"{PathB}\" rating=\"0\"\n";
        var state = StatsState.Empty with { RatingsSent = new Dictionary<string, int> { ["102"] = 4 } };
        var r = Assert.Single(Plan([], state, log).Ratings);
        Assert.Equal("102", r.RatingKey);
        Assert.Equal(-1, r.PlexValue);
    }

    [Fact]
    public void Ratings_FillGapsOnly_SkipsTracksRatedInPlex()
    {
        var log = $"filename=\"{PathA}\" rating=\"8\"\nfilename=\"{PathB}\" rating=\"6\"\n";
        var plan = Plan([], changelog: log, mode: RatingMode.FillGapsOnly, plexHasRating: k => k == "101");
        Assert.Equal("102", Assert.Single(plan.Ratings).RatingKey);
    }

    [Fact]
    public void Ratings_IgnoreUnownedOutOfRangeAndDuplicates()
    {
        var log = $"filename=\"/Music/Mine/x.mp3\" rating=\"8\"\nfilename=\"{PathA}\" rating=\"11\"\nfilename=\"{PathB}\" rating=\"5\"\nfilename=\"{PathB}\" rating=\"9\"\n";
        var r = Assert.Single(Plan([], changelog: log).Ratings);
        Assert.Equal(("102", 5), (r.RatingKey, r.Rating));
    }
}
