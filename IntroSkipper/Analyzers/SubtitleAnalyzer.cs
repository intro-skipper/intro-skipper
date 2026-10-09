// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Detects recaps and previews from matching text subtitle cues.
/// </summary>
/// <remarks>
/// Subtitle detection is deliberately independent from credits detection. A recap ends at the
/// beginning of the stored intro, while a preview runs from its matching cue through the episode
/// duration. When no subtitle cue matches, the normal analyzer chain remains eligible.
/// </remarks>
internal sealed partial class SubtitleAnalyzer(
    ILogger<SubtitleAnalyzer> logger,
    IFFmpegService ffmpegService,
    IntroSkipperDatabase database,
    PluginConfiguration configuration) : IMediaFileAnalyzer
{
    private readonly ILogger<SubtitleAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IntroSkipperDatabase _database = database;
    private readonly PluginConfiguration _config = configuration;

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (mode is not (AnalysisMode.Recap or AnalysisMode.Preview) || !IsEnabled(mode))
        {
            return analysisQueue;
        }

        var expression = GetPattern(mode);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return analysisQueue;
        }

        Regex regex;
        try
        {
            regex = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            LogInvalidPattern(_logger, mode, ex.Message);
            return analysisQueue;
        }

        foreach (var episode in analysisQueue.Where(item => item.NeedsAnalysis(mode)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cues = await _ffmpegService.ExtractSubtitleCuesAsync(episode, cancellationToken).ConfigureAwait(false);
            var segment = mode == AnalysisMode.Recap
                ? await FindRecapAsync(episode, cues, regex, cancellationToken).ConfigureAwait(false)
                : FindPreview(episode, cues, regex);

            if (mode == AnalysisMode.Preview && segment is not null)
            {
                // Preview writes normally preserve the credits-derived pass's rows so the two
                // producers can coexist. A subtitle match is the explicit, more authoritative
                // preview strategy, so remove the competing credits-derived row first.
                await _database.ClearCreditsDerivedPreviewsAsync([episode.EpisodeId], cancellationToken).ConfigureAwait(false);
            }

            await _database.ReplaceAutoSegmentsAsync(
                episode.EpisodeId,
                mode,
                segment is null ? [] : [segment],
                SegmentSource.Subtitle,
                episode.AnalysisConfigHash,
                cancellationToken).ConfigureAwait(false);

            if (segment is not null)
            {
                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                LogFoundSubtitleSegment(_logger, episode.Name, mode, segment.Start, segment.End);
            }
        }

        return analysisQueue;
    }

    private bool IsEnabled(AnalysisMode mode)
        => mode == AnalysisMode.Recap
            ? _config.EnableSubtitleRecapDetection
            : _config.EnableSubtitlePreviewDetection;

    private string GetPattern(AnalysisMode mode)
        => mode == AnalysisMode.Recap ? _config.SubtitleRecapPattern : _config.SubtitlePreviewPattern;

    private async Task<Segment?> FindRecapAsync(
        QueuedEpisode episode,
        IReadOnlyList<SubtitleCue> cues,
        Regex regex,
        CancellationToken cancellationToken)
    {
        var introStart = (await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(segment => segment.Type == AnalysisMode.Introduction && segment.State == SegmentState.Active)
            .Select(segment => TickConversions.ToSeconds(segment.StartTicks))
            .Where(start => start > 0 && start < episode.Duration)
            .OrderBy(start => start)
            .FirstOrDefault();

        if (introStart <= 0)
        {
            return null;
        }

        foreach (var cue in cues.Where(cue => cue.Start < introStart).OrderBy(cue => cue.Start))
        {
            if (!Matches(regex, cue.Text))
            {
                continue;
            }

            var segment = new Segment(episode.EpisodeId, new TimeRange(cue.Start, introStart));
            if (segment.Valid
                && segment.Duration >= _config.MinimumRecapDuration
                && segment.Duration <= _config.MaximumRecapDuration)
            {
                return segment;
            }
        }

        return null;
    }

    private Segment? FindPreview(QueuedEpisode episode, IReadOnlyList<SubtitleCue> cues, Regex regex)
    {
        foreach (var cue in cues.OrderByDescending(cue => cue.Start))
        {
            if (!Matches(regex, cue.Text))
            {
                continue;
            }

            var segment = new Segment(episode.EpisodeId, new TimeRange(cue.Start, episode.Duration));
            if (segment.Valid && segment.Duration >= _config.MinimumPreviewDuration)
            {
                return segment;
            }
        }

        return null;
    }

    private static bool Matches(Regex regex, string text)
    {
        var plainText = SubtitleText(text);
        try
        {
            return regex.IsMatch(plainText);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string SubtitleText(string text)
    {
        var withoutMarkup = MarkupRegex().Replace(text, " ");
        return string.Join(' ', withoutMarkup.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex("<[^>]*>|\\{[^}]*\\}", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupRegex();

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Episode}: subtitle {Mode} candidate [{Start:F2}, {End:F2}]")]
    private static partial void LogFoundSubtitleSegment(ILogger logger, string episode, AnalysisMode mode, double start, double end);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid subtitle {Mode} regular expression: {Message}")]
    private static partial void LogInvalidPattern(ILogger logger, AnalysisMode mode, string message);
}
