// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using IntroSkipper.Analyzers;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreditsRunner;

/// <summary>
/// Runs the keyframe credits path over corpus files outside Jellyfin, one file at a time, or the
/// intro chain over them, one season at a time.
/// </summary>
/// <remarks>
/// Every pass builds the production pieces afresh over an empty temp detection cache, so every
/// scan and probe decodes: <see cref="FFmpegService"/>, <see cref="DetectionCacheService"/> and
/// <see cref="KeyframeAnalyzer"/>, whose candidates go through
/// <see cref="CreditsCandidateCombiner.Combine"/> as the credits pass does. The credits window
/// follows <c>BaseItemAnalyzerTask.SetCreditsWindowsAsync</c> with ProbeAudioDuration off, its
/// default. ffmpeg runs from PATH at below-normal priority, as it does inside Jellyfin without a
/// configured path. With more than one repeat, an untimed pass first warms the page cache.
/// </remarks>
/// <param name="config">The configuration the analyzers run with. Intro mode sets it to read embedded chapters.</param>
/// <param name="repeats">Timed passes per file. Intro mode ignores it and makes one pass.</param>
/// <param name="progress">Where to write one line per file.</param>
/// <param name="mode">Credits, or Introduction for the intro chain.</param>
/// <param name="noChapters">Whether intro mode drops the chapters it reads.</param>
internal sealed class Runner(PluginConfiguration config, int repeats, TextWriter progress, AnalysisMode mode = AnalysisMode.Credits, bool noChapters = false)
{
    /// <summary>
    /// Runs every file, whose paths must exist.
    /// </summary>
    public async Task<RunResults> RunAsync(IReadOnlyList<CorpusFile> files, CancellationToken cancellationToken)
    {
        List<EpisodeResult> episodes = [];
        var ffmpeg = string.Empty;
        if (mode == AnalysisMode.Introduction)
        {
            config.UseEmbeddedChaptersForAnalysis = true;
            foreach (var season in files.GroupBy(file => file.Season ?? "\0" + file.Id))
            {
                IReadOnlyList<EpisodeResult> results;
                (ffmpeg, results) = await RunSeasonAsync([.. season], cancellationToken).ConfigureAwait(false);
                foreach (var episode in results)
                {
                    episodes.Add(episode);
                    await progress.WriteLineAsync($"{episode.Id}: {Describe(episode)}").ConfigureAwait(false);
                }
            }
        }
        else
        {
            foreach (var file in files)
            {
                if (repeats > 1)
                {
                    _ = await RunPassAsync(file, cancellationToken).ConfigureAwait(false);
                }

                var timed = new List<Pass>();
                for (var i = 0; i < repeats; i++)
                {
                    timed.Add(await RunPassAsync(file, cancellationToken).ConfigureAwait(false));
                }

                ffmpeg = timed[0].Ffmpeg;
                var episode = Summarize(file, timed);
                episodes.Add(episode);
                await progress.WriteLineAsync($"{file.Id}: {Describe(episode)}").ConfigureAwait(false);
            }
        }

        // The informational version ends in +commit when the build knows it.
        var version = typeof(KeyframeAnalyzer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return new RunResults(
            version[(version.IndexOf('+', StringComparison.Ordinal) + 1)..],
            ffmpeg,
            CpuModel(),
            RuntimeInformation.OSDescription,
            repeats,
            JsonSerializer.SerializeToElement(config, Json.Options),
            episodes,
            mode,
            noChapters);
    }

    /// <summary>
    /// Sets the credits window the way the analysis pass does.
    /// </summary>
    internal static QueuedEpisode Queue(CorpusFile file, PluginConfiguration config) => new()
    {
        EpisodeId = Guid.NewGuid(),
        Name = file.Id,
        Path = file.Path,
        Duration = file.Duration,
        CreditsFingerprintStart = Math.Max(0, file.Duration - (file.Movie ? config.MaximumMovieCreditsDuration : config.MaximumCreditsDuration)),
        CreditsFingerprintEnd = file.Duration,
    };

    // The first timed pass is the reference; the others vote on times and determinism. A timeout
    // can change the calls a pass makes, so when the passes disagree on them the file keeps the
    // calls of the pass with the most timeouts, and a timeout in any pass reaches score and diff.
    internal static EpisodeResult Summarize(CorpusFile file, IReadOnlyList<Pass> timed)
    {
        var first = timed[0];
        var sameCalls = timed.All(pass => pass.Calls.Select(call => call.Trigger).SequenceEqual(first.Calls.Select(call => call.Trigger)));
        var nondeterministic = !sameCalls || timed.Any(pass => pass.Failure != first.Failure || !pass.Combined.SequenceEqual(first.Combined));
        var calls = sameCalls
            ? [.. first.Calls.Select((call, i) => call with
            {
                WallSeconds = Median(timed.Select(pass => pass.Calls[i].WallSeconds)),
                CpuSeconds = MedianOrNull(timed.Select(pass => pass.Calls[i].CpuSeconds)),
                TimedOut = timed.Max(pass => pass.Calls[i].TimedOut),
            })]
            : timed.OrderByDescending(pass => pass.Calls.Sum(call => call.TimedOut)).First().Calls;

        return new EpisodeResult(
            file.Id,
            first.Episode.CreditsFingerprintStart,
            first.Episode.CreditsFingerprintEnd,
            first.Combined,
            first.Raw,
            first.Failure,
            nondeterministic,
            Median(timed.Select(pass => pass.Total.WallSeconds)),
            MedianOrNull(timed.Select(pass => pass.Total.CpuSeconds)),
            Median(timed.Select(pass => pass.PluginCpuSeconds)),
            calls);
    }

    private static string Describe(EpisodeResult episode)
        => (episode.Failure
            ?? (episode.Combined.Count == 0 ? "no credits" : string.Join(", ", episode.Combined)))
            + string.Create(CultureInfo.InvariantCulture, $" ({episode.WallSeconds:F1} s, {episode.Calls.Sum(call => call.Processes)} ffmpeg)");

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static double? MedianOrNull(IEnumerable<double?> values)
    {
        var known = values.ToArray();
        return known.All(value => value.HasValue) ? Median(known.Select(value => value!.Value)) : null;
    }

    private static string CpuModel()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal));
            if (line?.Split(':', 2) is [_, var model])
            {
                return model.Trim();
            }
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    private async Task<Pass> RunPassAsync(CorpusFile file, CancellationToken cancellationToken)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"credits-runner-{Guid.NewGuid():N}.db");
        try
        {
            var cache = new DetectionCacheService(NullLogger<DetectionCacheService>.Instance, new DetectionCacheDatabase(new CacheContextFactory(dbPath), NullLogger<DetectionCacheDatabase>.Instance));
            var recorder = new CallRecorder();
            var ffmpeg = new FFmpegService(recorder, cache);

            // Outside the timed span: the cache database's migration, and the ffmpeg check a pass
            // runs before analysis, which also tells the scan whether keyframe visuals are available.
            _ = cache.TryRead<double>(Guid.Empty, AnalysisMode.Credits, CacheEntryType.Keyframe, 0, 0, out _);
            await ffmpeg.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);
            var version = Version(ffmpeg);

            var analyzer = new KeyframeAnalyzer(NullLogger<KeyframeAnalyzer>.Instance, recorder.Wrap(ffmpeg), cache, config);
            var episode = Queue(file, config);
            using var self = Process.GetCurrentProcess();
            var pluginCpu = self.TotalProcessorTime;
            var mark = recorder.Begin();
            IReadOnlyList<AttributedSegment> raw = [];
            string? failure = null;
            try
            {
                raw = await analyzer.DetectCreditsAsync(episode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The analyzer throws when the scan or a boundary probe fails, which fails the
                // episode in the credits pass too. The run records it and goes on.
                failure = $"{e.GetType().Name}: {e.Message}";
            }

            var total = recorder.Measure("total", mark);
            self.Refresh();
            var combined = CreditsCandidateCombiner.Combine(raw, episode.CreditsFingerprintEnd, config.MinimumCreditsDuration);
            return new Pass(
                episode,
                version,
                [.. combined.Select(ToCandidate)],
                [.. raw.Select(ToCandidate)],
                failure,
                total,
                (self.TotalProcessorTime - pluginCpu).TotalSeconds,
                recorder.Calls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Runs one season through the intro chain as <c>BaseItemAnalyzerTask.RunAnalyzerChainAsync</c>
    /// runs it at default settings: the chapter analyzer, then Chromaprint when ffmpeg passed its
    /// check. Both write to a temp segment database, and each file's result is its Introduction
    /// rows. A file whose chapters ffprobe cannot read sits out, as it does in the season pass.
    /// </summary>
    private async Task<(string Ffmpeg, IReadOnlyList<EpisodeResult> Episodes)> RunSeasonAsync(IReadOnlyList<CorpusFile> season, CancellationToken cancellationToken)
    {
        // Its own folder, so the segment database finds no legacy database beside it to import.
        var directory = Directory.CreateTempSubdirectory("credits-runner-");
        try
        {
            var cache = new DetectionCacheService(
                NullLogger<DetectionCacheService>.Instance,
                new DetectionCacheDatabase(new CacheContextFactory(Path.Join(directory.FullName, "cache.db")), NullLogger<DetectionCacheDatabase>.Instance));
            var database = new IntroSkipperDatabase(new SegmentContextFactory(Path.Join(directory.FullName, "segments.db")), NullLogger<IntroSkipperDatabase>.Instance);
            var recorder = new CallRecorder();
            var service = new FFmpegService(recorder, cache);
            var ffmpegValid = await service.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);
            var ffmpeg = recorder.Wrap(service);
            var hash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid, chapterSource: ChapterSource.Embedded);

            // The window follows SeasonResolver. Chromaprint pads a lone open episode with the
            // episodes next to it by EpisodeNumber.
            var queued = season.Select((file, i) => new QueuedEpisode
            {
                EpisodeId = Guid.NewGuid(),
                Name = file.Id,
                Path = file.Path,
                Duration = file.Duration,
                EpisodeNumber = i + 1,
                Category = QueuedMediaCategory.Episode,
                IntroFingerprintEnd = Math.Min(file.Duration >= 5 * 60 ? file.Duration * config.AnalysisPercent / 100.0 : file.Duration, 60 * config.AnalysisLengthLimit),
                ChaptersSource = ChapterSource.Embedded,
                AnalysisConfigHash = hash,
            }).ToList();

            var failures = new Dictionary<Guid, string>();
            List<QueuedEpisode> readable = [];
            foreach (var episode in queued)
            {
                if (await ffmpeg.GetEmbeddedChaptersAsync(episode, cancellationToken).ConfigureAwait(false) is { } chapters)
                {
                    episode.Chapters = noChapters ? [] : chapters;
                    readable.Add(episode);
                }
                else
                {
                    failures[episode.EpisodeId] = "chapters unreadable";
                }
            }

            try
            {
                var items = await new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, ffmpeg, database, config, enableRecapBlackFrameFallback: false)
                    .AnalyzeMediaFiles(readable, AnalysisMode.Introduction, cancellationToken).ConfigureAwait(false);
                if (ffmpegValid)
                {
                    await new ChromaprintAnalyzer(NullLogger<ChromaprintAnalyzer>.Instance, ffmpeg, cache, database, config)
                        .AnalyzeMediaFiles(items, AnalysisMode.Introduction, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The analysis pass logs an exception out of the chain, keeps the rows each
                // analyzer call committed before it and retries the season on its next pass.
                // Here the run is the only pass, so the episodes still open fail with it.
                foreach (var episode in readable.Where(episode => episode.NeedsAnalysis(AnalysisMode.Introduction)))
                {
                    failures[episode.EpisodeId] = $"{e.GetType().Name}: {e.Message}";
                }
            }

            List<EpisodeResult> results = [];
            foreach (var episode in queued)
            {
                var failure = failures.GetValueOrDefault(episode.EpisodeId)
                    ?? (episode.GetAnalyzed(AnalysisMode.Introduction) == EpisodeState.AnalysisFailed ? "analysis failed" : null);
                Candidate[] rows = failure is not null
                    ? []
                    : [.. (await database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Where(row => row.Type == AnalysisMode.Introduction)
                        .Select(row => new Candidate(TickConversions.ToSeconds(row.StartTicks), TickConversions.ToSeconds(row.EndTicks), row.Source.ToString()))];
                var calls = recorder.CallsOf(episode.EpisodeId);
                results.Add(new EpisodeResult(
                    episode.Name,
                    0,
                    episode.IntroFingerprintEnd,
                    rows,
                    [],
                    failure,
                    Nondeterministic: false,
                    calls.Sum(call => call.WallSeconds),
                    calls.Any(call => call.CpuSeconds is null) ? null : calls.Sum(call => call.CpuSeconds!.Value),
                    PluginCpuSeconds: 0,
                    calls));
            }

            return (Version(service), results);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static string Version(FFmpegService ffmpeg)
        => ffmpeg.GetCheckResult().Outputs.FirstOrDefault(output => output.Name == "version")?.Output.Split('\n')[0].Trim() ?? "unknown";

    private static Candidate ToCandidate(AttributedSegment candidate)
        => new(candidate.Segment.Start, candidate.Segment.End, candidate.Source.ToString());

    internal sealed record Pass(
        QueuedEpisode Episode,
        string Ffmpeg,
        IReadOnlyList<Candidate> Combined,
        IReadOnlyList<Candidate> Raw,
        string? Failure,
        CallTiming Total,
        double PluginCpuSeconds,
        IReadOnlyList<CallTiming> Calls);

    private sealed class CacheContextFactory(string dbPath) : IDbContextFactory<DetectionCacheDbContext>
    {
        public DetectionCacheDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<DetectionCacheDbContext>();
            SqlitePragmas.Configure(builder, dbPath);
            return new DetectionCacheDbContext(builder.Options);
        }
    }

    private sealed class SegmentContextFactory(string dbPath) : IDbContextFactory<IntroSkipperDbContext>
    {
        public IntroSkipperDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<IntroSkipperDbContext>();
            SqlitePragmas.Configure(builder, dbPath);
            return new IntroSkipperDbContext(builder.Options);
        }
    }
}
