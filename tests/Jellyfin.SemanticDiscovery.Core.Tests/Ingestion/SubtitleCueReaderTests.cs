using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Ingestion;

public sealed class SubtitleCueReaderTests
{
    private static async Task<IReadOnlyList<SubtitleCue>> ReadAsync(string content)
    {
        using StringReader reader = new(content);
        return await SubtitleCueReader.ReadAsync(reader);
    }

    [Fact]
    public async Task ReadAsyncStripsLeadingByteOrderMarkAndDetectsWebVttHeader()
    {
        string content =
            "\uFEFFWEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello world\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        SubtitleCue cue = Assert.Single(cues);
        Assert.Equal("Hello world", cue.Text);
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, cue.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks, cue.EndTicks);
    }

    [Fact]
    public async Task ReadAsyncSkipsSrtSequenceNumberLines()
    {
        string content =
            "1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello", cues[0].Text);
        Assert.Equal("World", cues[1].Text);
    }

    [Fact]
    public async Task ReadAsyncStripsHtmlAndAssInlineTags()
    {
        string content =
            "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n<b>Hello</b> {\\i1}World{\\i0}\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        Assert.Equal("Hello World", Assert.Single(cues).Text);
    }

    [Fact]
    public async Task ReadAsyncSkipsWebVttNoteStyleAndRegionBlocks()
    {
        string content =
            "WEBVTT\n\n" +
            "NOTE This is a comment\nspanning two lines\n\n" +
            "STYLE\n::cue { color: red; }\n\n" +
            "REGION\nid:bottom\n\n" +
            "00:00:01.000 --> 00:00:02.000\nHello\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        Assert.Equal("Hello", Assert.Single(cues).Text);
    }

    [Fact]
    public async Task ReadAsyncParsesCommaDecimalTimingForSrt()
    {
        string content = "1\n00:00:01,500 --> 00:00:02,750\nHello\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        SubtitleCue cue = Assert.Single(cues);
        Assert.Equal((long)(1.5m * TimeSpan.TicksPerSecond), cue.StartTicks);
        Assert.Equal((long)(2.75m * TimeSpan.TicksPerSecond), cue.EndTicks);
    }

    [Fact]
    public async Task ReadAsyncParsesDotDecimalTimingWithHoursForWebVtt()
    {
        string content = "WEBVTT\n\n01:02:03.456 --> 01:02:04.789\nHello\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        SubtitleCue cue = Assert.Single(cues);
        long expectedStart = (long)(3723.456m * TimeSpan.TicksPerSecond);
        long expectedEnd = (long)(3724.789m * TimeSpan.TicksPerSecond);
        Assert.Equal(expectedStart, cue.StartTicks);
        Assert.Equal(expectedEnd, cue.EndTicks);
    }

    [Fact]
    public async Task ReadAsyncDropsCueWhenEndPrecedesStartButKeepsParsingSubsequentCues()
    {
        string content =
            "WEBVTT\n\n" +
            "00:00:05.000 --> 00:00:04.000\nShould be dropped\n\n" +
            "00:00:01.000 --> 00:00:02.000\nGood cue\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        Assert.Equal("Good cue", Assert.Single(cues).Text);
    }

    [Fact]
    public async Task ReadAsyncSkipsBlockWithUnparsableTimingAndKeepsParsingSubsequentCues()
    {
        string content =
            "WEBVTT\n\n" +
            "bad --> timing\nShould not appear\n\n" +
            "00:00:01.000 --> 00:00:02.000\nGood cue\n";

        IReadOnlyList<SubtitleCue> cues = await ReadAsync(content);

        Assert.Equal("Good cue", Assert.Single(cues).Text);
    }

    [Fact]
    public async Task ReadAsyncReturnsEmptyForEmptyInput()
    {
        IReadOnlyList<SubtitleCue> cues = await ReadAsync(string.Empty);

        Assert.Empty(cues);
    }
}
