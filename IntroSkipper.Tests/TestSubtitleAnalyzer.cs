// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestSubtitleAnalyzer
{
    private const string AssSidecar = """
        [Script Info]
        ScriptType: v4.00+

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,16,&Hffffff,&Hffffff,&H0,&H0,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,0

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:10.50,0:00:12.00,Default,,0,0,0,,{\i1}Here's{\i0} the preview
        """;

    // jellyfin-ffmpeg 7.1.3 and 8.1.2 both write this for an .srt sidecar: MM:SS.mmm below an
    // hour, HH:MM:SS.mmm from an hour on.
    [Fact]
    public void ParseWebVtt_ReadsMinuteAndHourCueTimesAndCombinesLines()
    {
        var cues = FFmpegOutputParser.ParseWebVtt("""
            WEBVTT

            00:10.500 --> 00:12.000
            Previously on <i>the show</i>

            01:00:01.000 --> 01:00:02.250
            Here's the preview
            second line
            """);

        Assert.Equal(
            [
                new SubtitleCue(10.5, 12, "Previously on <i>the show</i>"),
                new SubtitleCue(3601, 3602.25, "Here's the preview second line"),
            ],
            cues);
    }

    // Bitmap codecs would need OCR, and neither ffmpeg build can decode arib_caption, so
    // extracting either would only fail. Both builds decode the older srt and ssa ids.
    [Fact]
    public void ParseTextSubtitleStreams_KeepsTextCodecsFfmpegCanDecode()
    {
        Assert.Equal([2, 5, 6, 7], FFmpegOutputParser.ParseTextSubtitleStreams("2,subrip\r\n3,hdmv_pgs_subtitle\n4,arib_caption\n5,ass\n6,srt\n7,ssa\n", SubtitleLanguageSelection.Parse(null)));
    }

    // The ISO 639-1, 639-2/B and 639-2/T codes of a language match each other, in the setting
    // and in a stream's tag. Untagged and und streams are always read.
    [Theory]
    [InlineData("de")]
    [InlineData("ger")]
    [InlineData("deu")]
    public void ParseTextSubtitleStreams_KeepsStreamsInTheSelectedLanguage(string setting)
    {
        const string Listing = "0,subrip\n1,subrip,ger\n2,subrip,deu\n3,subrip,de\n4,subrip,eng\n5,subrip,und\n";

        Assert.Equal([0, 1, 2, 3, 5], FFmpegOutputParser.ParseTextSubtitleStreams(Listing, SubtitleLanguageSelection.Parse(setting)));
    }

    // .NET gives Bokmål and Nynorsk codes of their own, but files usually tag Norwegian "nor".
    [Theory]
    [InlineData("nb")]
    [InlineData("nob")]
    [InlineData("no")]
    public void ParseTextSubtitleStreams_ReadsBokmalAndNynorskAsNorwegian(string setting)
    {
        const string Listing = "0,subrip,nor\n1,subrip,nob\n2,subrip,nn\n3,subrip,swe\n";

        Assert.Equal([0, 1, 2], FFmpegOutputParser.ParseTextSubtitleStreams(Listing, SubtitleLanguageSelection.Parse(setting)));
    }

    // Cues from every source come back in start order, which the recap search relies on.
    // The .ass sidecar is read first and holds the later cue.
    [Fact]
    public async Task ExtractSubtitleCuesAsync_ReadsTextSidecarsInStartOrder()
    {
        using var directory = new TempDirectory();
        var mediaPath = directory.Join("episode.mp4");
        File.Copy(FfmpegTestHelpers.QueueFile("video/rainbow.mp4").Path, mediaPath);
        await File.WriteAllTextAsync(directory.Join("episode.ass"), AssSidecar);
        await File.WriteAllTextAsync(directory.Join("episode.en.srt"), "1\n00:00:05,000 --> 00:00:06,500\nPreviously on the show\n");

        var scan = await FfmpegTestHelpers.CreateFFmpegService().ExtractSubtitleCuesAsync(new QueuedEpisode { Name = "episode", Path = mediaPath });

        SubtitleCue[] expected = [new(5, 6.5, "Previously on the show"), new(10.5, 12, "<i>Here's</i> the preview")];
        Assert.Equal(expected, scan.Cues);
        Assert.True(scan.Complete);
    }

    // Reads both embedded text streams of an episode muxed here from a fixture video and two
    // generated .srt files, through the ffmpeg run that writes one capped WebVTT file per
    // stream. The run leaves nothing in the plugin's temp directory, and the language setting
    // filters on the tags ffprobe lists. The second stream holds the earlier cue.
    [Fact]
    public async Task ExtractSubtitleCuesAsync_ReadsEmbeddedStreamsInSelectedLanguages()
    {
        using var directory = new TempDirectory();
        var english = directory.Join("english.srt");
        var german = directory.Join("german.srt");
        var mediaPath = directory.Join("episode.mkv");
        await File.WriteAllTextAsync(english, "1\n00:00:05,000 --> 00:00:06,500\nPreviously on the show\n");
        await File.WriteAllTextAsync(german, "1\n00:00:02,000 --> 00:00:03,000\nZuvor bei der Serie\n");
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-i", FfmpegTestHelpers.QueueFile("video/rainbow.mp4").Path, "-i", english, "-i", german,
                "-map", "0:v", "-map", "1", "-map", "2", "-c:v", "copy", "-c:s", "srt",
                "-metadata:s:s:0", "language=eng", "-metadata:s:s:1", "language=ger",
                mediaPath,
            ]);

        var config = new PluginConfiguration();
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config);
        var tempDirectory = directory.Info.CreateSubdirectory("temp");
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "TempDirectory", tempDirectory.FullName);
        var ffmpeg = FfmpegTestHelpers.CreateFFmpegService();
        var episode = new QueuedEpisode { Name = "episode", Path = mediaPath };

        var everyLanguage = await ffmpeg.ExtractSubtitleCuesAsync(episode);
        config.SubtitleLanguages = "de";
        var germanOnly = await ffmpeg.ExtractSubtitleCuesAsync(episode);

        SubtitleCue germanCue = new(2, 3, "Zuvor bei der Serie");
        Assert.Equal([germanCue, new SubtitleCue(5, 6.5, "Previously on the show")], everyLanguage.Cues);
        Assert.True(everyLanguage.Complete);
        Assert.Equal([germanCue], germanOnly.Cues);
        Assert.True(germanOnly.Complete);
        Assert.Empty(tempDirectory.EnumerateFileSystemInfos());
    }

    // ffprobe cannot list the streams of a file that is not media. The scan comes back
    // incomplete instead of throwing, so the subtitle analyzer keeps the standing rows and
    // retries the episode rather than settling it as having no match.
    [Fact]
    public async Task ExtractSubtitleCuesAsync_UnreadableMediaFile_ReturnsAnIncompleteScan()
    {
        using var directory = new TempDirectory();
        var mediaPath = directory.Join("episode.mkv");
        await File.WriteAllTextAsync(mediaPath, "not a media file");

        var scan = await FfmpegTestHelpers.CreateFFmpegService().ExtractSubtitleCuesAsync(new QueuedEpisode { Name = "episode", Path = mediaPath });

        Assert.Empty(scan.Cues);
        Assert.False(scan.Complete);
    }

    // A sidecar's language comes from the name segments between the media name and the
    // extension, in any ISO 639 form, with or without a region, and next to flags such as
    // forced or hi. A sidecar without a language segment is read whatever the selection.
    [Fact]
    public void SubtitleSidecarFiles_ReadOnlySidecarsInSelectedLanguages()
    {
        using var directory = new TempDirectory();
        string[] names = ["episode.de.srt", "episode.en.hi.srt", "episode.en.srt", "episode.forced.srt", "episode.ger.forced.ass", "episode.pt-BR.srt", "episode.srt"];
        foreach (var name in names.Append("episode.mkv"))
        {
            File.WriteAllText(directory.Join(name), string.Empty);
        }

        Assert.Equal(
            ["episode.de.srt", "episode.forced.srt", "episode.ger.forced.ass", "episode.pt-BR.srt", "episode.srt"],
            SubtitleSidecarFiles.FindTextSources(directory.Join("episode.mkv"), SubtitleLanguageSelection.Parse("deu, pt")).Select(path => Path.GetFileName(path)));
    }

    // A .sub next to a .idx is a VobSub image stream, which subtitle detection cannot read,
    // so adding or rewriting the pair never reopens it.
    [Fact]
    public void SubtitleSidecarFiles_IgnoreVobSubPairs()
    {
        using var directory = new TempDirectory();
        var mediaPath = directory.Join("episode.mkv");
        var textPath = directory.Join("episode.en.srt");
        File.WriteAllText(mediaPath, string.Empty);
        File.WriteAllText(textPath, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var textVersion = SubtitleSidecarFiles.FileVersion(mediaPath, 2, SubtitleLanguageSelection.Parse(null));

        File.WriteAllText(directory.Join("episode.ja.idx"), "VobSub index");
        File.WriteAllText(directory.Join("episode.ja.sub"), "image data");

        Assert.Equal([textPath], SubtitleSidecarFiles.FindTextSources(mediaPath, SubtitleLanguageSelection.Parse(null)));
        Assert.Equal(textVersion, SubtitleSidecarFiles.FileVersion(mediaPath, 2, SubtitleLanguageSelection.Parse(null)));
    }

    // The lookup matches the media file's name literally, so a name holding a wildcard
    // character neither picks up a sibling's sidecar nor fails on a sibling shorter than
    // the name. Only Linux allows * in a file name, so the case runs there (as on CI).
    [Fact]
    public void SubtitleSidecarFiles_MatchTheMediaNameLiterally()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TempDirectory();
        var mediaPath = directory.Join("episode*.mkv");
        var ownPath = directory.Join("episode*.en.srt");
        File.WriteAllText(mediaPath, string.Empty);
        File.WriteAllText(ownPath, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        File.WriteAllText(directory.Join("episode.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        File.WriteAllText(directory.Join("episode2.en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

        Assert.Equal([ownPath], SubtitleSidecarFiles.FindTextSources(mediaPath, SubtitleLanguageSelection.Parse(null)));
    }

    // A matching cue starts a recap that ends at the detected intro's start. The default
    // pattern matches the recap title cards of translation tracks once markup is removed.
    // Apart from English and Italian, a wording must open the cue, since some of them also
    // occur in dialogue. "Previously onThe Story..." comes from a track that lost the italics
    // around "Previously on", which glued it to the title.
    [Theory]
    [InlineData("[Narrator] Previously on the story", true)]
    [InlineData("Previously onThe Story...", true)]
    [InlineData("<i>ZUVOR BEI X</i>", true)]
    [InlineData("Zuletzt bei X:", true)]
    [InlineData("WAS BISHER GESCHAH", true)]
    [InlineData("BISHER BEI X", true)]
    [InlineData("ANTERIORMENTE, EN X...", true)]
    [InlineData("ANTERIORMENTE EN", true)]
    [InlineData("ANTERIORMENTE…", true)]
    [InlineData("PREVIAMENTE", true)]
    [InlineData(@"{\an8}PRÉCÉDEMMENT", true)]
    [InlineData("PRÉCÉDEMMENT DANS X", true)]
    [InlineData("ANTERIORMENTE EM X", true)]
    [InlineData("NELL'EPISODIO PRECEDENTE", true)]
    [InlineData("NEGLI EPISODI PRECEDENTI", true)]
    [InlineData("WAT VOORAFGING", true)]
    [InlineData("comme je l'ai dit précédemment", false)]
    [InlineData("Wie war's bisher bei euch?", false)]
    [InlineData("As I said previously, on Monday", false)]
    public async Task DefaultRecapPattern_MatchesOnlyCuesThatIntroduceARecap(string cueText, bool matches)
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.SeedUserSegmentAsync(episodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(60), DatabaseTestHelpers.Ticks(90));

        var config = new PluginConfiguration
        {
            EnableSubtitleRecapDetection = true,
            MinimumRecapDuration = 5,
            MaximumRecapDuration = 120,
        };
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(10, 13, cueText)], Complete: true),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 120, IntroFingerprintEnd = 120, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Recap, CancellationToken.None);

        (double Start, double End, SegmentSource Source)[] expected = matches ? [(10, 60, SegmentSource.Subtitle)] : [];
        Assert.Equal(expected, await StoredSegmentsAsync(database, episodeId, AnalysisMode.Recap));
    }

    // With no intro after the cue, whether the intro came first or the episode has none,
    // only the next chapter start is a reliable recap end. A cue that starts after the recap
    // window, which ends at IntroFingerprintEnd, counts as dialogue and yields no recap, even
    // with that chapter.
    [Theory]
    [InlineData(true, 120, true)]
    [InlineData(false, 120, true)]
    [InlineData(true, 25, false)]
    public async Task RecapSubtitle_WithoutAnIntroAfterTheCue_EndsAtTheNextChapterInsideTheRecapWindow(bool introFirst, double introFingerprintEnd, bool detected)
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        if (introFirst)
        {
            await database.SeedUserSegmentAsync(episodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(0), DatabaseTestHelpers.Ticks(20));
        }

        var config = new PluginConfiguration
        {
            EnableSubtitleRecapDetection = true,
            MinimumRecapDuration = 5,
            MaximumRecapDuration = 120,
        };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(
            config,
            [new ChapterInfo { StartPositionTicks = DatabaseTestHelpers.Ticks(40) }]);
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(30, 33, "Previously on the story")], Complete: true),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 120, IntroFingerprintEnd = introFingerprintEnd, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Recap, CancellationToken.None);

        (double Start, double End, SegmentSource Source)[] expected = detected ? [(30, 40, SegmentSource.Subtitle)] : [];
        Assert.Equal(expected, await StoredSegmentsAsync(database, episodeId, AnalysisMode.Recap));
    }

    // "Next time" introduces a preview only at the start of a cue once markup is removed, or
    // after the speaker label of an SDH track; inside dialogue it does not.
    [Theory]
    [InlineData("Now the preview", true)]
    [InlineData("<i>Next time:</i> The Long Road", true)]
    [InlineData("NARRATOR: Next time on the story", true)]
    [InlineData("[Narrator] Next time on the story", true)]
    [InlineData("I'll bring the map next time.", false)]
    public async Task DefaultPreviewPattern_MatchesOnlyCuesThatIntroduceAPreview(string cueText, bool matches)
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration { EnableSubtitlePreviewDetection = true, MinimumPreviewDuration = 5 };
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(100, 103, cueText)], Complete: true),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        (double Start, double End, SegmentSource Source)[] expected = matches ? [(100, 180, SegmentSource.Subtitle)] : [];
        Assert.Equal(expected, await StoredSegmentsAsync(database, episodeId, AnalysisMode.Preview));
    }

    [Fact]
    public async Task IncompleteSubtitleScanWithoutMatch_PreservesSubtitleRowAndRemainsRetryable()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Preview,
            [new Segment(episodeId, new TimeRange(100, 180))],
            SegmentSource.Subtitle,
            configHash: "previous-config");
        var config = new PluginConfiguration { EnableSubtitlePreviewDetection = true };
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(100, 103, "Ordinary dialogue")], Complete: false),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "new-config" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episodeId));
        Assert.Equal("previous-config", preview.ConfigHash);
        Assert.Equal(SubtitleOutcome.Unresolved, episode.GetSubtitleOutcome(AnalysisMode.Preview));
        Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task PreviewSubtitle_RejectsCandidateLongerThanMaximum()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration
        {
            EnableSubtitlePreviewDetection = true,
            MinimumPreviewDuration = 5,
            MaximumPreviewDuration = 60,
        };
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(100, 103, "Here's the preview")], Complete: true),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(episodeId));
        Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task DisabledSubtitleModeDoesNotReadSubtitles()
    {
        var config = new PluginConfiguration();
        var extractionCalled = false;
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ =>
            {
                extractionCalled = true;
                return new([], Complete: true);
            },
        };
        var analyzer = new SubtitleAnalyzer(
            NullLogger<SubtitleAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);

        await analyzer.AnalyzeMediaFiles(
            [new QueuedEpisode { EpisodeId = Guid.NewGuid() }],
            AnalysisMode.Preview,
            CancellationToken.None);

        Assert.False(extractionCalled);
    }

    // The episode's stored segments of one mode, as start and end in seconds and source.
    private static async Task<(double Start, double End, SegmentSource Source)[]> StoredSegmentsAsync(IntroSkipperDatabase database, Guid episodeId, AnalysisMode mode)
        => [.. (await database.GetSegmentsAsync(episodeId))
            .Where(segment => segment.Type == mode)
            .Select(segment => (TickConversions.ToSeconds(segment.StartTicks), TickConversions.ToSeconds(segment.EndTicks), segment.Source))];
}
