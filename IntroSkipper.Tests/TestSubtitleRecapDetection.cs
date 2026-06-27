// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Subtitles;
using Xunit;

/// <summary>
/// Unit tests for the subtitle-driven recap detection prototype. These exercise the full pure
/// pipeline (parse -> match -> build) with no media files, plus the codec classifier and the
/// ffprobe JSON parser used to scope which streams are eligible.
/// </summary>
public class TestSubtitleRecapDetection
{
    // ───────────────────────── Parser: SubRip ─────────────────────────

    [Fact]
    public void ParseSubRip_ParsesCuesWithCommaMilliseconds()
    {
        const string srt = """
            1
            00:00:02,000 --> 00:00:05,000
            Previously on Test Show...

            2
            00:00:05,500 --> 00:00:09,000
            ...the hero lost everything.
            """;

        var cues = SubtitleParser.ParseSubRip(srt);

        Assert.Equal(2, cues.Count);
        Assert.Equal(2.0, cues[0].Start);
        Assert.Equal(5.0, cues[0].End);
        Assert.Equal("Previously on Test Show...", cues[0].Text);
        Assert.Equal(5.5, cues[1].Start);
    }

    [Fact]
    public void ParseSubRip_JoinsMultiLineCuesAndStripsMarkup_ViaMatcher()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:04,000
            <i>Previously</i>
            on the show

            """;

        var cues = SubtitleParser.ParseSubRip(srt);

        Assert.Single(cues);
        Assert.Equal("<i>Previously</i> on the show", cues[0].Text);
        // Markup is stripped during normalization, not parsing.
        Assert.Equal("previously on the show", RecapPhraseMatcher.Normalize(cues[0].Text));
    }

    [Fact]
    public void ParseSubRip_ToleratesBomCrlfAndMissingIndex()
    {
        const string srt = "\uFEFF00:00:02,000 --> 00:00:05,000\r\nPreviously on\r\n\r\n";

        var cues = SubtitleParser.ParseSubRip(srt);

        Assert.Single(cues);
        Assert.Equal(2.0, cues[0].Start);
        Assert.Equal("Previously on", cues[0].Text);
    }

    // ───────────────────────── Parser: WebVTT ─────────────────────────

    [Fact]
    public void ParseWebVtt_HandlesDotMillisecondsCueSettingsAndShortTimestamps()
    {
        const string vtt = """
            WEBVTT

            NOTE this is a comment

            intro-1
            00:02.000 --> 00:05.000 align:start position:50%
            Previously on Test Show

            00:00:30.000 --> 00:00:33.000
            Welcome back.
            """;

        var cues = SubtitleParser.Parse(vtt);

        Assert.Equal(SubtitleFormat.WebVtt, SubtitleParser.DetectFormat(vtt));
        Assert.Equal(2, cues.Count);
        Assert.Equal(2.0, cues[0].Start);
        Assert.Equal(5.0, cues[0].End);
        Assert.Equal("Previously on Test Show", cues[0].Text);
        Assert.Equal(30.0, cues[1].Start);
    }

    // ───────────────────────── Parser: ASS ─────────────────────────

    [Fact]
    public void ParseAss_ParsesDialogueLinesAndTextCommas()
    {
        const string ass = """
            [Script Info]
            ScriptType: v4.00+

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:02.00,0:00:05.00,Default,,0,0,0,,{\an8}Previously on, the show
            Dialogue: 0,0:00:06.00,0:00:09.00,Default,,0,0,0,,Second line\Nwith a break
            """;

        var cues = SubtitleParser.Parse(ass);

        Assert.Equal(SubtitleFormat.Ass, SubtitleParser.DetectFormat(ass));
        Assert.Equal(2, cues.Count);
        Assert.Equal(2.0, cues[0].Start);
        Assert.Equal(5.0, cues[0].End);
        // Text field keeps its internal comma; the ASS override block survives until normalization.
        Assert.Equal("{\\an8}Previously on, the show", cues[0].Text);
        Assert.Equal("Second line with a break", cues[1].Text);
    }

    [Theory]
    [InlineData("00:00:02,000", 2.0)]
    [InlineData("00:00:02.500", 2.5)]
    [InlineData("01:02:03,250", 3723.25)]
    [InlineData("00:05.250", 5.25)]
    [InlineData("00:00:00,5", 0.5)]
    public void TryParseTimestamp_ParsesSupportedForms(string token, double expected)
    {
        Assert.True(SubtitleParser.TryParseTimestamp(token, out var seconds));
        Assert.Equal(expected, seconds, precision: 3);
    }

    [Theory]
    [InlineData("not a time")]
    [InlineData("12345")]
    public void TryParseTimestamp_RejectsGarbage(string token)
    {
        Assert.False(SubtitleParser.TryParseTimestamp(token, out _));
    }

    // ───────────────────────── Phrase matcher ─────────────────────────

    [Theory]
    [InlineData("Previously on Breaking Bad")]
    [InlineData("PREVIOUSLY ON THE WIRE")]
    [InlineData("Previously, on Lost")]
    [InlineData("Last time on The Expanse")]
    [InlineData("Last week on...")]
    [InlineData("[NARRATOR] Previously on the show")] // short leading speaker label tolerated
    [InlineData("- Previously on the show")]
    public void Matcher_MatchesEnglishOpenings(string text)
    {
        Assert.True(RecapPhraseMatcher.Default.IsRecapOpening(text));
    }

    [Theory]
    [InlineData("Précédemment dans...")]      // French, with diacritics
    [InlineData("Anteriormente en...")]       // Spanish
    [InlineData("Was bisher geschah")]        // German
    [InlineData("Negli episodi precedenti")]  // Italian
    [InlineData("前回のあらすじ")]              // Japanese (前回 …)
    public void Matcher_MatchesMultilingualOpenings(string text)
    {
        Assert.True(RecapPhraseMatcher.Default.IsRecapOpening(text));
    }

    [Theory]
    [InlineData("I told you previously on Tuesday that we should leave")] // mid-line, beyond anchor
    [InlineData("Welcome back. Let's begin.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Matcher_RejectsNonOpenings(string text)
    {
        Assert.False(RecapPhraseMatcher.Default.IsRecapOpening(text));
    }

    [Fact]
    public void Matcher_IsConfigurable()
    {
        var matcher = new RecapPhraseMatcher(["catch up on the story"]);

        Assert.True(matcher.IsRecapOpening("Catch up on the story so far"));
        Assert.False(matcher.IsRecapOpening("Previously on the show"));
        Assert.Equal(1, matcher.PhraseCount);
    }

    [Fact]
    public void Normalize_StripsDiacriticsMarkupAndPunctuation()
    {
        Assert.Equal("previously on the show", RecapPhraseMatcher.Normalize("<i>Previously</i>, on the show!"));
        Assert.Equal("precedemment dans", RecapPhraseMatcher.Normalize("Précédemment, dans"));
    }

    // ───────────────────────── Segment builder ─────────────────────────

    private static readonly SubtitleRecapOptions DefaultOptions = new();

    [Fact]
    public void Build_AnchorsAtPhraseStart_NotForcedToZero()
    {
        var cues = new List<SubtitleCue>
        {
            new(2.0, 5.0, "Previously on Test Show"),
            new(5.5, 9.0, "...the hero lost everything."),
            new(30.0, 33.0, "Welcome back."),
        };

        var result = SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, DefaultOptions);

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Start); // start is the matched cue, NOT 0
        Assert.Equal(9.0, result.End);    // cluster ends before the 30s gap
        Assert.Equal(2, result.CueCount);
        Assert.False(result.SnappedToBlackFrame);
    }

    [Fact]
    public void Build_SnapsEndToNearbyBlackFrame()
    {
        var cues = new List<SubtitleCue>
        {
            new(2.0, 5.0, "Previously on Test Show"),
            new(5.5, 9.0, "...the hero lost everything."),
            new(30.0, 33.0, "Welcome back."),
        };

        // Fade-to-black occurs at 10.0s, 1.0s after the last recap cue.
        var blackFrames = new List<double> { 10.0, 10.2, 10.4 };

        var result = SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, DefaultOptions, blackFrames);

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Start);
        Assert.Equal(10.0, result.End);
        Assert.True(result.SnappedToBlackFrame);
    }

    [Fact]
    public void Build_IgnoresDistantBlackFrames()
    {
        var cues = new List<SubtitleCue>
        {
            new(2.0, 5.0, "Previously on Test Show"),
            new(5.5, 9.0, "...the hero lost everything."),
        };

        // Black frame is 20s away — well beyond BlackFrameSnapSeconds (6s default).
        var result = SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, DefaultOptions, [29.0]);

        Assert.NotNull(result);
        Assert.Equal(9.0, result!.End);
        Assert.False(result.SnappedToBlackFrame);
    }

    [Fact]
    public void Build_StopsClusterAtLargeGap()
    {
        var cues = new List<SubtitleCue>
        {
            new(2.0, 5.0, "Previously on Test Show"),
            new(6.0, 9.0, "Recap line two"),
            new(10.0, 13.0, "Recap line three"),
            // 20s gap -> cold open begins; must not be absorbed.
            new(33.0, 36.0, "Cold open dialogue"),
            new(37.0, 40.0, "More dialogue"),
        };

        var result = SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, DefaultOptions);

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Start);
        Assert.Equal(13.0, result.End);
        Assert.Equal(3, result.CueCount);
    }

    [Fact]
    public void Build_ReturnsNull_WhenNoRecapPhrase()
    {
        var cues = new List<SubtitleCue>
        {
            new(2.0, 5.0, "Hello there."),
            new(6.0, 9.0, "General Kenobi."),
        };

        Assert.Null(SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, DefaultOptions));
    }

    [Fact]
    public void Build_ReturnsNull_WhenPhraseIsBeyondMaxWindow()
    {
        var cues = new List<SubtitleCue>
        {
            new(900.0, 903.0, "Previously on Test Show"), // 15 min in -> not a recap
        };

        var result = SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, DefaultOptions);

        Assert.Null(result);
    }

    [Fact]
    public void Build_ReturnsNull_WhenTooShort()
    {
        var cues = new List<SubtitleCue>
        {
            new(2.0, 4.0, "Previously on Test Show"), // 2s < MinDurationSeconds (5s)
            new(30.0, 33.0, "Welcome back."),
        };

        Assert.Null(SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, DefaultOptions));
    }

    [Fact]
    public void Build_ClampsToMaxDuration()
    {
        var cues = new List<SubtitleCue>();
        // A continuous wall of cues 5s apart for 5 minutes, all anchored by the opener.
        cues.Add(new SubtitleCue(2.0, 5.0, "Previously on Test Show"));
        for (var t = 6.0; t < 320.0; t += 5.0)
        {
            cues.Add(new SubtitleCue(t, t + 3.0, "recap continues"));
        }

        var options = new SubtitleRecapOptions { MaxWindowSeconds = 600, MaxDurationSeconds = 90 };
        var result = SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, options);

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Start);
        Assert.Equal(92.0, result.End); // start + MaxDurationSeconds
    }

    [Fact]
    public void Build_SnapStartToZero_IsOptInAndBounded()
    {
        var cues = new List<SubtitleCue>
        {
            new(1.5, 5.0, "Previously on Test Show"),
            new(5.5, 9.0, "...the hero lost everything."),
        };

        var off = SubtitleRecapSegmentBuilder.Build(cues, RecapPhraseMatcher.Default, new SubtitleRecapOptions());
        Assert.Equal(1.5, off!.Start);

        var on = SubtitleRecapSegmentBuilder.Build(
            cues,
            RecapPhraseMatcher.Default,
            new SubtitleRecapOptions { SnapStartToZero = true, StartSnapSeconds = 2.0 });
        Assert.Equal(0.0, on!.Start);
    }

    [Fact]
    public void Build_ReturnsNull_ForEmptyInput()
    {
        Assert.Null(SubtitleRecapSegmentBuilder.Build([], RecapPhraseMatcher.Default, DefaultOptions));
    }

    // ───────────────────────── Codec classification ─────────────────────────

    [Theory]
    [InlineData("subrip")]
    [InlineData("srt")]
    [InlineData("mov_text")]
    [InlineData("webvtt")]
    [InlineData("ass")]
    [InlineData("ssa")]
    [InlineData("SUBRIP")] // case-insensitive
    public void Codec_RecognizesTextCodecs(string codec)
    {
        Assert.True(SubtitleCodec.IsTextBased(codec));
        Assert.False(SubtitleCodec.IsImageBased(codec));
    }

    [Theory]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("dvd_subtitle")]
    [InlineData("dvb_subtitle")]
    [InlineData("xsub")]
    public void Codec_RecognizesImageCodecs(string codec)
    {
        Assert.False(SubtitleCodec.IsTextBased(codec));
        Assert.True(SubtitleCodec.IsImageBased(codec));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("some_future_codec")]
    public void Codec_TreatsUnknownAsNonText(string? codec)
    {
        Assert.False(SubtitleCodec.IsTextBased(codec));
    }

    // ───────────────────────── ffprobe JSON parsing ─────────────────────────

    [Fact]
    public void Probe_ParsesMixedTextAndImageStreams()
    {
        // Shape mirrors: ffprobe -select_streams s -show_entries
        //   stream=index,codec_name,codec_type,disposition:stream_tags=language -of json
        const string json = """
            {
              "streams": [
                {
                  "index": 2,
                  "codec_name": "subrip",
                  "codec_type": "subtitle",
                  "disposition": { "forced": 0 },
                  "tags": { "language": "eng" }
                },
                {
                  "index": 3,
                  "codec_name": "hdmv_pgs_subtitle",
                  "codec_type": "subtitle",
                  "disposition": { "forced": 1 },
                  "tags": { "language": "jpn" }
                }
              ]
            }
            """;

        var streams = SubtitleProbe.Parse(json);

        Assert.Equal(2, streams.Count);

        Assert.Equal(2, streams[0].Index);
        Assert.Equal("subrip", streams[0].Codec);
        Assert.Equal("eng", streams[0].Language);
        Assert.True(streams[0].IsTextBased);
        Assert.False(streams[0].IsForced);

        Assert.Equal("hdmv_pgs_subtitle", streams[1].Codec);
        Assert.False(streams[1].IsTextBased);
        Assert.True(streams[1].IsForced);

        // Only the text stream is eligible for phrase detection.
        Assert.Single(streams, s => s.IsTextBased);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"streams\": []}")]
    public void Probe_ReturnsEmptyForMissingOrInvalid(string json)
    {
        Assert.Empty(SubtitleProbe.Parse(json));
    }
}
