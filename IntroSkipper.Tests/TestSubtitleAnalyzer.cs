// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
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
    // The .ass sidecar is read first and holds the latest cue. The SAMI sidecar clears its cue
    // with a blank one, which ffmpeg logs as a decoding error without failing the run.
    [Fact]
    public async Task ExtractSubtitleCuesAsync_ReadsTextSidecarsInStartOrder()
    {
        using var directory = new TempDirectory();
        var mediaPath = directory.Join("episode.mp4");
        File.Copy(FfmpegTestHelpers.QueueFile("video/rainbow.mp4").Path, mediaPath);
        await File.WriteAllTextAsync(directory.Join("episode.ass"), AssSidecar);
        await File.WriteAllTextAsync(directory.Join("episode.en.srt"), "1\n00:00:05,000 --> 00:00:06,500\nPreviously on the show\n");
        await File.WriteAllTextAsync(directory.Join("episode.en.smi"), "<SAMI>\n<BODY>\n<SYNC Start=8000><P Class=ENCC>Last time</P>\n<SYNC Start=9500><P Class=ENCC>&nbsp;</P>\n</BODY>\n</SAMI>\n");

        var scan = await ReadEveryCueAsync(FfmpegTestHelpers.CreateFFmpegService(), new QueuedEpisode { Name = "episode", Path = mediaPath });

        SubtitleCue[] expected = [new(5, 6.5, "Previously on the show"), new(8, 9.5, "Last time"), new(10.5, 12, "<i>Here's</i> the preview")];
        Assert.Equal(expected, scan.Cues);
        Assert.True(scan.Complete);
    }

    // The embedded run reads only around the window: it stops after a window that ends early
    // and seeks before one that starts late. Inside the window it returns the cues a full read
    // does, at the same times, on a file whose clock starts at 7.5 s. The middle cue lies
    // outside both windows, so its absence shows the read was cut.
    [Theory]
    [InlineData(0, 60, new[] { "Previously on the show", "Across the end" })]
    [InlineData(180, 300, new[] { "Next time on the show" })]
    public async Task ExtractSubtitleCuesAsync_Window_ReadsWhatAFullReadFindsInsideIt(double start, double end, string[] inside)
    {
        using var directory = new TempDirectory();
        var episode = new QueuedEpisode { Name = "episode", Path = await MuxLongEpisodeAsync(directory), Duration = 300 };
        var ffmpeg = FfmpegTestHelpers.CreateFFmpegService();

        var full = await ReadEveryCueAsync(ffmpeg, episode);
        var windowed = await ffmpeg.ExtractSubtitleCuesAsync(episode, new TimeRange(start, end));

        SubtitleCue[] InWindow(SubtitleScan scan) => [.. scan.Cues.Where(cue => cue.Start >= start && cue.Start < end)];
        Assert.Equal(inside, InWindow(full).Select(cue => cue.Text));
        Assert.Equal(InWindow(full), InWindow(windowed));
        Assert.DoesNotContain(windowed.Cues, cue => cue.Text == "Middle");
        Assert.True(windowed.Complete);
    }

    // Each mode reads its own window of the file: the recap window at the start, and the last
    // MaximumPreviewDuration seconds for a preview. Each window holds its mode's cue, and a
    // chapter at 70 s ends the recap.
    [Theory]
    [InlineData(AnalysisMode.Recap, 20, 70)]
    [InlineData(AnalysisMode.Preview, 250, 300)]
    public async Task AnalyzeMediaFiles_FindsTheCueInsideTheWindowItReads(AnalysisMode mode, double expectedStart, double expectedEnd)
    {
        using var directory = new TempDirectory();
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration { EnableSubtitleRecapDetection = true, EnableSubtitlePreviewDetection = true };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config, [new ChapterInfo { StartPositionTicks = DatabaseTestHelpers.Ticks(70) }]);
        var episode = new QueuedEpisode
        {
            EpisodeId = episodeId,
            Name = "episode",
            Path = await MuxLongEpisodeAsync(directory),
            Duration = 300,
            IntroFingerprintEnd = 75,
            AnalysisConfigHash = "subtitle",
        };

        await new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, FfmpegTestHelpers.CreateFFmpegService(), database, config)
            .AnalyzeMediaFiles([episode], mode, CancellationToken.None);

        Assert.Equal([(expectedStart, expectedEnd, SegmentSource.Subtitle)], await StoredSegmentsAsync(database, episodeId, mode));
    }

    // ffmpeg drops every cue of a sidecar that is not UTF-8, and the run still succeeds. A
    // sidecar in a legacy charset is read through a UTF-8 copy decoded with its detected
    // charset, so every cue comes back with its text.
    [Theory]
    [InlineData("windows-1252", "Was bisher geschah – Größe…", "Wir sehen uns nächste Woche. Tschüß!", "Das Mädchen über der Brücke")]
    [InlineData("shift_jis", "前回までは", "東京の天気は晴れです。", "また来週お会いしましょう。")]
    [InlineData("gbk", "前情提要", "今天的天气很好。", "我们下周再见。")]
    public async Task ExtractSubtitleCuesAsync_LegacyCharsetSidecar_ReadsEveryCue(string charset, string first, string second, string third)
    {
        using var directory = new TempDirectory();
        var mediaPath = directory.Join("episode.mp4");
        File.Copy(FfmpegTestHelpers.QueueFile("video/rainbow.mp4").Path, mediaPath);
        string[] lines = [first, second, third];
        var srt = string.Concat(lines.Select((line, i) => $"{i + 1}\r\n00:00:0{(2 * i) + 1},000 --> 00:00:0{(2 * i) + 2},000\r\n{line}\r\n\r\n"));
        await File.WriteAllBytesAsync(directory.Join("episode.srt"), CodePagesEncodingProvider.Instance.GetEncoding(charset)!.GetBytes(srt));

        var scan = await ReadEveryCueAsync(FfmpegTestHelpers.CreateFFmpegService(), new QueuedEpisode { Name = "episode", Path = mediaPath });

        Assert.Equal(lines, scan.Cues.Select(cue => cue.Text));
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
        var mediaPath = await MuxEpisodeAsync(directory);

        var config = new PluginConfiguration();
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config);
        var tempDirectory = directory.Info.CreateSubdirectory("temp");
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "TempDirectory", tempDirectory.FullName);
        var ffmpeg = FfmpegTestHelpers.CreateFFmpegService();
        var episode = new QueuedEpisode { Name = "episode", Path = mediaPath };

        var everyLanguage = await ReadEveryCueAsync(ffmpeg, episode);
        config.SubtitleLanguages = "de";
        var germanOnly = await ReadEveryCueAsync(ffmpeg, episode);

        SubtitleCue germanCue = new(2, 3, "Zuvor bei der Serie");
        Assert.Equal([germanCue, new SubtitleCue(5, 6.5, "Previously on the show")], everyLanguage.Cues);
        Assert.True(everyLanguage.Complete);
        Assert.Equal([germanCue], germanOnly.Cues);
        Assert.True(germanOnly.Complete);
        Assert.Empty(tempDirectory.EnumerateFileSystemInfos());
    }

    // A sidecar that ffmpeg cannot open, or that cannot be read at all, fails only itself: the
    // embedded streams and the sidecar after it are still read, and the scan is incomplete, so
    // the episode is retried.
    [Theory]
    [InlineData("garbage")]
    [InlineData("empty")]
    [InlineData("locked")]
    public async Task ExtractSubtitleCuesAsync_BrokenSidecar_KeepsTheOtherCues(string broken)
    {
        using var directory = new TempDirectory();
        var mediaPath = await MuxEpisodeAsync(directory);
        await File.WriteAllTextAsync(directory.Join("episode.fr.srt"), "1\n00:00:08,000 --> 00:00:09,000\nPrecedemment\n");
        var garbage = new byte[4096];
        new Random(1).NextBytes(garbage);
        var brokenPath = directory.Join("episode.de.srt");
        await File.WriteAllBytesAsync(brokenPath, broken == "empty" ? [] : garbage);

        // An exclusive handle makes reading the sidecar throw, on Linux through .NET's advisory lock.
        using var lockHandle = broken == "locked" ? new FileStream(brokenPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var scan = await ReadEveryCueAsync(FfmpegTestHelpers.CreateFFmpegService(), new QueuedEpisode { Name = "episode", Path = mediaPath });

        Assert.Equal(["Zuvor bei der Serie", "Previously on the show", "Precedemment"], scan.Cues.Select(cue => cue.Text));
        Assert.False(scan.Complete);
    }

    // Sidecars whose names differ only in case are two files, and each is read. Only a
    // case-sensitive file system can hold both, so the case runs on Linux (as on CI).
    [Fact]
    public async Task ExtractSubtitleCuesAsync_ReadsSidecarsWhoseNamesDifferOnlyInCase()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var directory = new TempDirectory();
        var mediaPath = directory.Join("Episode.mp4");
        File.Copy(FfmpegTestHelpers.QueueFile("video/rainbow.mp4").Path, mediaPath);
        await File.WriteAllTextAsync(directory.Join("Episode.en.srt"), "1\n00:00:05,000 --> 00:00:06,000\nPreviously on the show\n");
        await File.WriteAllTextAsync(directory.Join("Episode.EN.srt"), "1\n00:00:08,000 --> 00:00:09,000\nLast time\n");

        var scan = await ReadEveryCueAsync(FfmpegTestHelpers.CreateFFmpegService(), new QueuedEpisode { Name = "Episode", Path = mediaPath });

        Assert.Equal([new SubtitleCue(5, 6, "Previously on the show"), new SubtitleCue(8, 9, "Last time")], scan.Cues);
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

        var scan = await ReadEveryCueAsync(FfmpegTestHelpers.CreateFFmpegService(), new QueuedEpisode { Name = "episode", Path = mediaPath });

        Assert.Empty(scan.Cues);
        Assert.False(scan.Complete);
    }

    // A sidecar's language comes from the name segments between the media name and the
    // extension, in any ISO 639 form, with or without a region, or as the language's English
    // or native name, and next to flags such as forced or hi. A sidecar without a language
    // segment is read whatever the selection.
    [Fact]
    public void SubtitleSidecarFiles_ReadOnlySidecarsInSelectedLanguages()
    {
        using var directory = new TempDirectory();
        string[] names = ["episode.de.srt", "episode.Deutsch.srt", "episode.en.hi.srt", "episode.en.srt", "episode.forced.srt", "episode.français.srt", "episode.French.srt", "episode.ger.forced.ass", "episode.German.sdh.srt", "episode.pt-BR.srt", "episode.srt"];
        foreach (var name in names.Append("episode.mkv"))
        {
            File.WriteAllText(directory.Join(name), string.Empty);
        }

        Assert.Equal(
            ["episode.de.srt", "episode.Deutsch.srt", "episode.forced.srt", "episode.ger.forced.ass", "episode.German.sdh.srt", "episode.pt-BR.srt", "episode.srt"],
            SubtitleSidecarFiles.FindTextSources(new QueuedEpisode { Path = directory.Join("episode.mkv") }, SubtitleLanguageSelection.Parse("deu, pt")).Select(path => Path.GetFileName(path)));
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
        var episode = new QueuedEpisode { Path = mediaPath, FileVersion = 2 };
        var textVersion = SubtitleSidecarFiles.FileVersion(episode, SubtitleLanguageSelection.Parse(null));

        File.WriteAllText(directory.Join("episode.ja.idx"), "VobSub index");
        File.WriteAllText(directory.Join("episode.ja.sub"), "image data");

        Assert.Equal([textPath], SubtitleSidecarFiles.FindTextSources(episode, SubtitleLanguageSelection.Parse(null)));
        Assert.Equal(textVersion, SubtitleSidecarFiles.FileVersion(episode, SubtitleLanguageSelection.Parse(null)));
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

        Assert.Equal([ownPath], SubtitleSidecarFiles.FindTextSources(new QueuedEpisode { Path = mediaPath }, SubtitleLanguageSelection.Parse(null)));
    }

    // A matching cue starts a recap that ends at the next chapter. The default pattern matches
    // the recap title cards of translation tracks once markup is removed. Apart from English,
    // Italian, Chinese and Korean, a wording must open the cue, since some of them also occur
    // in dialogue; the Korean one follows the title, so it must close the cue. "Previously
    // onThe Story..." comes from a track that lost the italics around "Previously on", which
    // glued it to the title.
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
    [InlineData("前回までは…", true)]
    [InlineData("【X】前情提要", true)]
    [InlineData("X 지난 이야기", true)]
    [InlineData("comme je l'ai dit précédemment", false)]
    [InlineData("Wie war's bisher bei euch?", false)]
    [InlineData("As I said previously, on Monday", false)]
    [InlineData("それは前回までは知らなかった", false)]
    [InlineData("지난 이야기는 그만하자", false)]
    public async Task DefaultRecapPattern_MatchesOnlyCuesThatIntroduceARecap(string cueText, bool matches)
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration
        {
            EnableSubtitleRecapDetection = true,
            MinimumRecapDuration = 5,
            MaximumRecapDuration = 120,
        };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config, [new ChapterInfo { StartPositionTicks = DatabaseTestHelpers.Ticks(60) }]);
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

    // A chapter start after the cue ends the recap whether the intro came first or the episode
    // has none. A cue that starts after the recap window, which ends at IntroFingerprintEnd,
    // counts as dialogue and yields no recap, even with that chapter.
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

    // A recap at a cue at 10 s ends between MinimumRecapDuration (15 s) and MaximumRecapDuration
    // after it, and before an intro that follows it: at the first chapter start in that range,
    // else at the first black run of at least 2 s that starts in it. Runs are a start frame and
    // a frame count at 23.976 fps, so 48 frames last 2.002 s and 47 frames 1.96 s. The black
    // scan covers the range Chromaprint and black-frame recaps scan, up to the intro start or
    // MaximumRecapDetectionDuration, so the detection cache serves both from one decode. With
    // no end the match is rejected: the mode settles and keeps its standing rows.
    [Theory]
    [InlineData(new[] { 20d, 45 }, new[] { 720, 72 }, 0, 120, 45, 0)] // a chapter before an earlier run; one before 25 s is too early
    [InlineData(new double[0], new[] { 480, 72, 720, 24, 1440, 72 }, 0, 120, 60.06, 120)] // a run before 25 s, a 1 s run, a 3 s run
    [InlineData(new double[0], new[] { 960, 48 }, 0, 120, 40.04, 120)] // 48 frames
    [InlineData(new double[0], new[] { 960, 47, 1680, 72 }, 0, 120, 70.07, 120)] // 47 frames are too short
    [InlineData(new[] { 55d }, new[] { 1440, 72 }, 50, 120, 0, 50)] // the intro at 50 s comes first, and is no end itself
    [InlineData(new[] { 55d }, new[] { 1440, 72 }, 0, 40, 0, 120)] // MaximumRecapDuration ends the range at 50 s
    public async Task RecapSubtitle_EndsAtTheFirstChapterElseTheFirstLongBlackRun(double[] chapters, int[] runs, double introStart, int maximumRecapDuration, double expectedEnd, double expectedScanEnd)
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        if (introStart > 0)
        {
            await database.SeedUserSegmentAsync(episodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(introStart), DatabaseTestHelpers.Ticks(introStart + 30));
        }

        var config = new PluginConfiguration { EnableSubtitleRecapDetection = true, MaximumRecapDuration = maximumRecapDuration };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config, [.. chapters.Select(start => new ChapterInfo { StartPositionTicks = DatabaseTestHelpers.Ticks(start) })]);
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(10, 13, "Previously on the story")], Complete: true),
            RangeBlackFrames = (_, range, _, _, _) => ScanFrames(range.End, runs),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 300, IntroFingerprintEnd = 120, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };

        await new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config)
            .AnalyzeMediaFiles([episode], AnalysisMode.Recap, CancellationToken.None);

        (double Start, double End, SegmentSource Source)[] expected = expectedEnd > 0 ? [(10, expectedEnd, SegmentSource.Subtitle)] : [];
        Assert.Equal(expected, await StoredSegmentsAsync(database, episodeId, AnalysisMode.Recap));
        Assert.Equal(expectedEnd > 0 ? SubtitleOutcome.None : SubtitleOutcome.Rejected, episode.GetSubtitleOutcome(AnalysisMode.Recap));
        StubFFmpegService.Call[] scans = expectedScanEnd > 0 ? [new StubFFmpegService.RangeScan(0, expectedScanEnd, 0, config.BlackFrameThreshold, AnalysisMode.Recap)] : [];
        Assert.Equal(scans, ffmpeg.Calls);
    }

    // The recap ends at the black run real ffmpeg reports: a generated episode with a recap cue
    // at 5 s, a 1 s black run at 30 s, which is too short, and a 3 s run at 40 s.
    [Fact]
    public async Task AnalyzeMediaFiles_EndsASubtitleRecapAtTheBlackRunFfmpegReports()
    {
        using var directory = new TempDirectory();
        var subtitles = directory.Join("english.srt");
        var mediaPath = directory.Join("black.mkv");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:05,000 --> 00:00:07,000\nPreviously on the show\n");
        static string Color(string color, int seconds, string label) => $"color=c={color}:s=64x36:r=24:d={seconds}[{label}];";
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", $"{Color("gray", 30, "a")}{Color("black", 1, "b")}{Color("gray", 9, "c")}{Color("black", 3, "d")}{Color("gray", 17, "e")}[a][b][c][d][e]concat=n=5",
                "-i", subtitles,
                "-map", "0:v", "-map", "1", "-c:v", "mpeg4", "-q:v", "2", "-c:s", "srt",
                mediaPath,
            ]);
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration { EnableSubtitleRecapDetection = true };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var episode = new QueuedEpisode { EpisodeId = episodeId, Name = "episode", Path = mediaPath, Duration = 60, IntroFingerprintEnd = 60, AnalysisConfigHash = "subtitle" };

        await new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, FfmpegTestHelpers.CreateFFmpegService(), database, config)
            .AnalyzeMediaFiles([episode], AnalysisMode.Recap, CancellationToken.None);

        Assert.Equal([(5d, 40d, SegmentSource.Subtitle)], await StoredSegmentsAsync(database, episodeId, AnalysisMode.Recap));
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

    // A season's subtitle preview override decides detection, whichever way the plugin-wide
    // setting points.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task PreviewSubtitle_SeasonOverrideControlsDetection(bool pluginWide, bool seasonOverride, bool expectedDetected)
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration { EnableSubtitlePreviewDetection = pluginWide, MinimumPreviewDuration = 5 };
        var extractionCalled = false;
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ =>
            {
                extractionCalled = true;
                return new([new SubtitleCue(100, 103, "Here's the preview")], Complete: true);
            },
        };
        var episode = new QueuedEpisode
        {
            EpisodeId = episodeId,
            Duration = 180,
            Path = "episode.mkv",
            AnalysisConfigHash = "subtitle",
            SubtitlePreviewDetectionOverride = seasonOverride,
        };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        Assert.Equal(expectedDetected, extractionCalled);
        Assert.Equal(expectedDetected, (await database.GetSegmentsAsync(episodeId)).Any(segment => segment.Type == AnalysisMode.Preview));
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

    // Muxes episode.mkv in the directory from a fixture video and two generated .srt files: an
    // English stream with a cue at 5 s and a German one with a cue at 2 s.
    private static async Task<string> MuxEpisodeAsync(TempDirectory directory)
    {
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
        return mediaPath;
    }

    // Muxes long.mkv in the directory: the fixture video looped to 300 s, with its clock
    // starting at 7.5 s, and an English subtitle stream holding a recap cue at 20 s, a cue from
    // 58 to 62 s, one at 150 s and a preview cue at 250 s.
    private static async Task<string> MuxLongEpisodeAsync(TempDirectory directory)
    {
        var subtitles = directory.Join("english.srt");
        var mediaPath = directory.Join("long.mkv");
        await File.WriteAllTextAsync(subtitles, """
            1
            00:00:20,000 --> 00:00:22,000
            Previously on the show

            2
            00:00:58,000 --> 00:01:02,000
            Across the end

            3
            00:02:30,000 --> 00:02:32,000
            Middle

            4
            00:04:10,000 --> 00:04:12,000
            Next time on the show

            """);
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-stream_loop", "29", "-i", FfmpegTestHelpers.QueueFile("video/rainbow.mp4").Path, "-i", subtitles,
                "-map", "0:v", "-map", "1", "-c:v", "copy", "-c:s", "srt", "-metadata:s:s:0", "language=eng",
                "-output_ts_offset", "7.5",
                mediaPath,
            ]);
        return mediaPath;
    }

    // Every frame of [0, end) at 23.976 fps, as the recap black-frame scan reports them: 100%
    // black inside a run and 0% elsewhere. Runs are pairs of a start frame and a frame count.
    private static BlackFrame[] ScanFrames(double end, int[] runs)
    {
        const double Interval = 1001d / 24000;
        bool Black(int frame) => runs.Chunk(2).Any(run => frame >= run[0] && frame < run[0] + run[1]);
        return [.. Enumerable.Range(0, (int)(end / Interval)).Select(frame => new BlackFrame(Black(frame) ? 100 : 0, frame * Interval, frame))];
    }

    // Reads every cue: the window covers the episode, so the run neither seeks nor stops
    // early, also when the duration is unknown (0).
    private static Task<SubtitleScan> ReadEveryCueAsync(FFmpegService ffmpeg, QueuedEpisode episode)
        => ffmpeg.ExtractSubtitleCuesAsync(episode, new TimeRange(0, episode.Duration));

    // The episode's stored segments of one mode, as start and end in seconds and source.
    private static async Task<(double Start, double End, SegmentSource Source)[]> StoredSegmentsAsync(IntroSkipperDatabase database, Guid episodeId, AnalysisMode mode)
        => [.. (await database.GetSegmentsAsync(episodeId))
            .Where(segment => segment.Type == mode)
            .Select(segment => (TickConversions.ToSeconds(segment.StartTicks), TickConversions.ToSeconds(segment.EndTicks), segment.Source))];
}
