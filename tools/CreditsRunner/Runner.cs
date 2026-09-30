// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreditsRunner;

/// <summary>
/// Runs the keyframe credits path over corpus files outside Jellyfin, one file at a time.
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
/// <param name="config">The configuration the analyzer runs with.</param>
/// <param name="repeats">Timed passes per file.</param>
/// <param name="progress">Where to write one line per file.</param>
internal sealed class Runner(PluginConfiguration config, int repeats, TextWriter progress)
{
    /// <summary>
    /// Runs every file.
    /// </summary>
    /// <param name="files">The files, with absolute paths.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>The results, with one entry per file.</returns>
    public async Task<RunResults> RunAsync(IReadOnlyList<CorpusFile> files, CancellationToken cancellationToken)
    {
        List<EpisodeResult> episodes = [];
        var ffmpeg = string.Empty;
        foreach (var file in files)
        {
            var passes = new List<Pass>();
            if (repeats > 1)
            {
                passes.Add(await RunPassAsync(file, cancellationToken).ConfigureAwait(false));
            }

            for (var i = 0; i < repeats; i++)
            {
                passes.Add(await RunPassAsync(file, cancellationToken).ConfigureAwait(false));
            }

            ffmpeg = passes[0].Ffmpeg;
            var episode = Summarize(file, passes[0], passes.Count > repeats ? passes[1..] : passes);
            episodes.Add(episode);
            await progress.WriteLineAsync($"{file.Id}: {Describe(episode)}").ConfigureAwait(false);
        }

        return new RunResults(
            typeof(KeyframeAnalyzer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            ffmpeg,
            CpuModel(),
            RuntimeInformation.OSDescription,
            repeats,
            JsonSerializer.SerializeToElement(config, Json.Options),
            episodes);
    }

    /// <summary>
    /// Sets the credits window the way the analysis pass does.
    /// </summary>
    /// <param name="file">The corpus file.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The episode to analyze.</returns>
    internal static QueuedEpisode Queue(CorpusFile file, PluginConfiguration config) => new()
    {
        EpisodeId = Guid.NewGuid(),
        Name = file.Id,
        Path = file.Path,
        Duration = file.Duration,
        CreditsFingerprintStart = Math.Max(0, file.Duration - (file.Movie ? config.MaximumMovieCreditsDuration : config.MaximumCreditsDuration)),
        CreditsFingerprintEnd = file.Duration,
    };

    private static EpisodeResult Summarize(CorpusFile file, Pass first, IReadOnlyList<Pass> timed)
    {
        var sameCalls = timed.All(pass => pass.Calls.Select(call => call.Trigger).SequenceEqual(first.Calls.Select(call => call.Trigger)));
        var nondeterministic = !sameCalls || timed.Any(pass => pass.Failure != first.Failure || !pass.Combined.SequenceEqual(first.Combined));
        var calls = sameCalls
            ? [.. first.Calls.Select((call, i) => call with
            {
                WallSeconds = Median(timed.Select(pass => pass.Calls[i].WallSeconds)),
                CpuSeconds = MedianOrNull(timed.Select(pass => pass.Calls[i].CpuSeconds)),
            })]
            : first.Calls;

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
            ?? (episode.Combined.Count == 0 ? "no credits" : string.Join(", ", episode.Combined.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Start:F2}-{c.End:F2} {c.Source}")))))
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
            var version = ffmpeg.GetCheckResult().Outputs.FirstOrDefault(output => output.Name == "version")?.Output.Split('\n')[0].Trim() ?? "unknown";

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

    private static Candidate ToCandidate(AttributedSegment candidate)
        => new(candidate.Segment.Start, candidate.Segment.End, candidate.Source.ToString());

    private sealed record Pass(
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
}
