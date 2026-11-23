// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The chapter source: ffprobe's chapter listing, and the season pass that reads each
/// episode's chapters once, from Jellyfin's chapter table or, with
/// <see cref="PluginConfiguration.UseEmbeddedChaptersForAnalysis"/>, from the media file.
/// </summary>
public class TestEmbeddedChapters
{
    private const string ChapterNameFormat = "Chapter {0}";
    private static readonly long Runtime = DatabaseTestHelpers.Ticks(120);

    /// <summary>
    /// The read yields what Jellyfin's probe stores in its chapter table: starts rounded to the
    /// millisecond, an untitled chapter named by its number in ffprobe's order, sorted by start.
    /// </summary>
    [Fact]
    public void ParseChapters_ShapesChaptersAsJellyfinStoresThem()
    {
        var chapters = FFmpegOutputParser.ParseChapters(
            """
            {
              "chapters": [
                { "start_time": "98.250000", "tags": { "title": "Main" } },
                { "start_time": "90.048625", "tags": { "title": "Opening" } },
                { "start_time": "0.000000" }
              ]
            }
            """,
            Runtime,
            ChapterNameFormat);

        Assert.Equal(
            [(0L, "Chapter 3"), (900_490_000L, "Opening"), (982_500_000L, "Main")],
            chapters.Select(chapter => (chapter.StartPositionTicks, chapter.Name)));
    }

    /// <summary>
    /// A half millisecond rounds to even as Jellyfin rounds it, and a title that is a timestamp,
    /// a bare number or blank is replaced like a missing one.
    /// </summary>
    [Theory]
    [InlineData("1.000500", "Opening", 10_000_000L, "Opening")]
    [InlineData("1.001500", "Opening", 10_020_000L, "Opening")]
    [InlineData("5.000000", "1:23", 50_000_000L, "Chapter 1")]
    [InlineData("5.000000", "12", 50_000_000L, "Chapter 1")]
    [InlineData("5.000000", "  ", 50_000_000L, "Chapter 1")]
    public void ParseChapters_RoundsAndNamesAsJellyfinDoes(string start, string title, long ticks, string name)
    {
        var chapter = Assert.Single(FFmpegOutputParser.ParseChapters(
            $$"""{ "chapters": [ { "start_time": "{{start}}", "tags": { "title": "{{title}}" } } ] }""",
            Runtime,
            ChapterNameFormat));

        Assert.Equal((ticks, name), (chapter.StartPositionTicks, chapter.Name));
    }

    /// <summary>
    /// Jellyfin saves no chapter at or past the item's runtime, an end marker included, and the
    /// rounded start is what it compares.
    /// </summary>
    [Fact]
    public void ParseChapters_DropsChaptersAtOrPastTheRuntime()
    {
        var chapters = FFmpegOutputParser.ParseChapters(
            """
            {
              "chapters": [
                { "start_time": "0.000000", "tags": { "title": "Episode" } },
                { "start_time": "119.999700", "tags": { "title": "End" } },
                { "start_time": "120.000000", "tags": { "title": "Past" } }
              ]
            }
            """,
            Runtime,
            ChapterNameFormat);

        Assert.Equal(["Episode"], chapters.Select(chapter => chapter.Name));
    }

    [Fact]
    public void ParseChapters_DropsStartsThatAreNotTickCounts()
    {
        var chapters = FFmpegOutputParser.ParseChapters(
            """
            {
              "chapters": [
                { "start_time": "NaN", "tags": { "title": "NaN" } },
                { "start_time": "1e100", "tags": { "title": "Too late" } },
                { "start_time": "-1.000000", "tags": { "title": "Before start" } },
                { "start_time": "15.000000", "tags": { "title": "Opening" } }
              ]
            }
            """,
            Runtime,
            ChapterNameFormat);

        var chapter = Assert.Single(chapters);
        Assert.Equal(150_000_000, chapter.StartPositionTicks);
        Assert.Equal("Opening", chapter.Name);
    }

    /// <summary>
    /// Output without a chapters array is a failed read, which the probe retries, not a file
    /// without chapters, which would settle the episode.
    /// </summary>
    /// <param name="raw">ffprobe stdout.</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{ "chapters": {} }""")]
    public void ParseChapters_RejectsOutputWithoutAChaptersArray(string raw)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => FFmpegOutputParser.ParseChapters(raw, Runtime, ChapterNameFormat));
    }

    [Fact]
    public void ParseChapters_ReadsAnEmptyChaptersArrayAsNoChapters()
    {
        Assert.Empty(FFmpegOutputParser.ParseChapters("""{ "chapters": [] }""", Runtime, ChapterNameFormat));
    }

    /// <summary>
    /// Pins the JSON shape the installed ffprobe prints for a Matroska file's chapters.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task GetEmbeddedChaptersAsync_ReadsTheContainerChapters()
    {
        using var directory = new TempDirectory();
        var metadata = directory.Join("chapters.txt");
        var path = directory.Join("chapters.mkv");
        await File.WriteAllTextAsync(
            metadata,
            """
            ;FFMETADATA1
            [CHAPTER]
            TIMEBASE=1/1000000
            START=0
            END=1048625
            title=Opening
            [CHAPTER]
            TIMEBASE=1/1000000
            START=1048625
            END=1500000
            title=Main
            [CHAPTER]
            TIMEBASE=1/1000000
            START=1500000
            END=2000000
            """.ReplaceLineEndings("\n"));
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=32x32:r=25:d=2", "-f", "ffmetadata", "-i", metadata, "-map", "0", "-map_chapters", "1", "-c:v", "ffv1", path]);

        var chapters = await FfmpegTestHelpers.CreateFFmpegService().GetEmbeddedChaptersAsync(
            new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "chapters", Path = path, Duration = 2 });

        Assert.NotNull(chapters);
        Assert.Equal(
            [(0L, "Opening"), (10_490_000L, "Main"), (15_000_000L, "Chapter 3")],
            chapters.Select(chapter => (chapter.StartPositionTicks, chapter.Name)));
    }

    /// <summary>
    /// A file without chapters reads as an empty list, and one ffprobe cannot open reads as
    /// null, so the pass never takes an unreadable file for one without chapters.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task GetEmbeddedChaptersAsync_TellsAFileWithoutChaptersFromAnUnreadableOne()
    {
        using var directory = new TempDirectory();
        var ffmpeg = FfmpegTestHelpers.CreateFFmpegService();

        var none = await ffmpeg.GetEmbeddedChaptersAsync(FfmpegTestHelpers.QueueFile("video/rainbow.mp4"));
        var unreadable = await ffmpeg.GetEmbeddedChaptersAsync(
            new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "missing", Path = directory.Join("missing.mkv") });

        Assert.NotNull(none);
        Assert.Empty(none);
        Assert.Null(unreadable);
    }

    /// <summary>
    /// Jellyfin's chapter table holds an intro chapter another plugin added, and the file
    /// holds its own. The pass analyzes the chapters of the configured source only, and with
    /// the setting off it never runs ffprobe for them.
    /// </summary>
    [Theory]
    [InlineData(false, 20, 60)]
    [InlineData(true, 100, 140)]
    public async Task SeasonPass_ReadsChaptersFromTheConfiguredSource(bool embedded, double start, double end)
    {
        var config = IntroductionOnly();
        config.UseEmbeddedChaptersForAnalysis = embedded;
        using var season = new SeasonFixture(config, IntroChapters(20, 60), episodeCount: 1);
        var embeddedReads = 0;
        var ffmpeg = Ffmpeg(chapters: _ =>
        {
            Interlocked.Increment(ref embeddedReads);
            return IntroChapters(100, 140);
        });

        await season.AnalyzeAsync(ffmpeg);

        var intro = Assert.Single(await season.Database.GetSegmentsAsync(season.EpisodeIds[0]));
        Assert.Equal(SegmentSource.Chapter, intro.Source);
        Assert.Equal((start, end), (TickConversions.ToSeconds(intro.StartTicks), TickConversions.ToSeconds(intro.EndTicks)));
        Assert.Equal(embedded ? 1 : 0, embeddedReads);
    }

    /// <summary>
    /// One episode's embedded chapters cannot be read. Its siblings are analyzed and
    /// recorded in every mode, and it is no Chromaprint reference for them. It gets no
    /// record, so the next pass retries it, and it keeps the intro it had.
    /// </summary>
    [Fact]
    public async Task SeasonPass_EpisodeWithUnreadableChapters_SitsOutAndKeepsItsRows()
    {
        var config = IntroductionOnly();
        config.ScanCredits = true;
        config.UseEmbeddedChaptersForAnalysis = true;
        using var season = new SeasonFixture(config, [], episodeCount: 3);
        var unreadable = season.EpisodeIds[1];
        Guid[] readable = [season.EpisodeIds[0], season.EpisodeIds[2]];
        await season.Database.ReplaceAutoSegmentsAsync(
            unreadable,
            AnalysisMode.Introduction,
            [new Segment(unreadable, new TimeRange(10, 40))],
            SegmentSource.Chromaprint,
            "old-hash");
        var standing = Assert.Single(await season.Database.GetSegmentsAsync(unreadable));
        var fingerprinted = new ConcurrentBag<Guid>();
        var ffmpeg = Ffmpeg(
            chapters: episode => episode.EpisodeId == unreadable ? null : [Chapter("Episode", 0), Chapter("Ending", 180)],
            valid: true,
            fingerprints: (episode, _) =>
            {
                fingerprinted.Add(episode.EpisodeId);
                return Enumerable.Repeat(0x11111111u * (uint)episode.EpisodeNumber, 300).ToArray();
            });

        await season.AnalyzeAsync(ffmpeg);

        var records = (await season.Database.GetSeasonQueueSnapshotAsync(season.SeasonId, season.EpisodeIds)).AnalysisRecords.Keys;
        Assert.Equal(
            readable.SelectMany(id => new[] { (id, AnalysisMode.Introduction), (id, AnalysisMode.Credits) }).Order(),
            records.Order());
        Assert.Equal(readable.Order(), fingerprinted.Order());
        foreach (var id in readable)
        {
            var credits = Assert.Single(await season.Database.GetSegmentsAsync(id));
            Assert.Equal((AnalysisMode.Credits, SegmentSource.Chapter), (credits.Type, credits.Source));
        }

        var kept = Assert.Single(await season.Database.GetSegmentsAsync(unreadable));
        Assert.Equal((standing.Id, standing.StartTicks, standing.EndTicks), (kept.Id, kept.StartTicks, kept.EndTicks));
    }

    /// <summary>
    /// A season with nothing to analyze reads no chapters, so a scheduled pass over a
    /// settled season or an opted-out Season 0 runs no ffprobe for them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeasonPass_WithNothingToAnalyze_ReadsNoChapters(bool seasonZero)
    {
        var config = IntroductionOnly();
        config.UseEmbeddedChaptersForAnalysis = true;
        using var season = new SeasonFixture(config, [], episodeCount: 2, seasonNumber: seasonZero ? 0 : 1);
        var embeddedReads = 0;
        var ffmpeg = Ffmpeg(chapters: _ =>
        {
            Interlocked.Increment(ref embeddedReads);
            return IntroChapters(100, 140);
        });

        // The first pass analyzes and records the season, reading each episode once.
        if (!seasonZero)
        {
            await season.AnalyzeAsync(ffmpeg);
            Assert.Equal(2, embeddedReads);
            embeddedReads = 0;
        }

        await season.AnalyzeAsync(ffmpeg);

        Assert.Equal(0, embeddedReads);
    }

    private static PluginConfiguration IntroductionOnly() => new()
    {
        ScanIntroduction = true,
        ScanCredits = false,
        ScanRecap = false,
        ScanPreview = false,
        ScanCommercial = false,
        EnableSponsorBlockChapterDetection = false,
    };

    private static ChapterInfo Chapter(string name, double start)
        => new() { Name = name, StartPositionTicks = DatabaseTestHelpers.Ticks(start) };

    private static ChapterInfo[] IntroChapters(double start, double end)
        => [Chapter("Episode", 0), Chapter("Opening", start), Chapter("Story", end)];

    // The pass's ffmpeg: Chromaprint as given, the embedded chapter read through the hook, and
    // time adjustment probes that find nothing, so a chapter range is stored as authored.
    private static StubFFmpegService Ffmpeg(
        Func<QueuedEpisode, ChapterInfo[]?> chapters,
        bool valid = false,
        Func<QueuedEpisode, AnalysisMode, uint[]>? fingerprints = null) => new()
    {
        VersionCheck = () => valid,
        EmbeddedChapters = chapters,
        Fingerprints = fingerprints,
        Silence = (_, _, _) => [],
        KeyFrames = (_, _, _) => [],
    };

    /// <summary>
    /// One season of four-minute episodes whose media files exist on disk, the only items of
    /// one library, over a fresh segment database. Jellyfin's chapter table answers every
    /// episode with the given chapters.
    /// </summary>
    private sealed class SeasonFixture : IDisposable
    {
        private readonly TempSegmentDb _db = new();
        private readonly TempDirectory _media = new();
        private readonly EntrypointTestHelpers.PluginInstanceScope _scope;
        private readonly ILibraryManager _libraryManager;

        public SeasonFixture(PluginConfiguration config, ChapterInfo[] jellyfinChapters, int episodeCount, int seasonNumber = 1)
        {
            _scope = EntrypointTestHelpers.CreatePluginScope(config, jellyfinChapters);
            var seriesId = Guid.NewGuid();
            var episodes = Enumerable.Range(1, episodeCount).Select(number =>
            {
                var path = _media.Join($"episode-{number}.mkv");
                File.WriteAllText(path, string.Empty);
                return JellyfinItems.Episode(Guid.NewGuid(), seriesId, SeasonId, name: $"Episode {number}", path: path, seasonNumber: seasonNumber, episodeNumber: number);
            }).ToArray();
            EpisodeIds = [.. episodes.Select(episode => episode.Id)];
            _libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], JellyfinItems.WithParents(episodes));
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", _libraryManager);
        }

        public Guid SeasonId { get; } = Guid.NewGuid();

        public Guid[] EpisodeIds { get; }

        public IntroSkipperDatabase Database => _db.Database;

        // Runs a full pass over the season, as the scheduler does.
        public Task AnalyzeAsync(IFFmpegService ffmpeg) => new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateSeasonResolver(_libraryManager),
            ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(),
            cacheDatabase: null!,
            _db.Database).AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None, [SeasonId]);

        public void Dispose()
        {
            _scope.Dispose();
            _media.Dispose();
            _db.Dispose();
        }
    }
}
