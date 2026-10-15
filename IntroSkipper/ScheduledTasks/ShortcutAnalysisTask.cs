// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Analyzes a small, serialized batch of shortcut videos. Shortcut targets are often remote
/// resources, so keeping this work in its own recurring task prevents the normal library pass
/// from issuing a large parallel burst of FFmpeg requests.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ShortcutAnalysisTask"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="queue">Analysis queue that runs the shortcut batch.</param>
public partial class ShortcutAnalysisTask(
    ILogger<ShortcutAnalysisTask> logger,
    AnalysisScheduler queue) : IScheduledTask
{
    private const int DefaultIntervalHours = 1;

    private readonly ILogger<ShortcutAnalysisTask> _logger = logger;
    private readonly AnalysisScheduler _queue = queue;

    /// <inheritdoc />
    public string Name => "Analyze Shortcut Videos";

    /// <inheritdoc />
    public string Category => "Intro Skipper";

    /// <inheritdoc />
    public string Description => "Slowly analyzes shortcut videos such as .strm files in small batches.";

    /// <inheritdoc />
    public string Key => "IntroSkipperShortcutAnalysisTask";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Configuration.ProcessShortcutVideos != true)
        {
            progress.Report(100);
            return;
        }

        var batchSize = Math.Clamp(
            plugin.Configuration.ShortcutAnalysisBatchSize,
            1,
            PluginConfiguration.MaximumShortcutAnalysisBatchSize);
        LogScheduledTaskStarting(_logger, batchSize);
        try
        {
            await _queue.RunLibraryAsync(
                progress,
                cancellationToken,
                shortcutsOnly: true,
                shortcutBatchSize: batchSize).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            // The regular library task may already be waiting in the shared queue. The next
            // interval will retry this small batch after that pass has completed.
            LogScheduledTaskAlreadyQueued(_logger);
            progress.Report(100);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(DefaultIntervalHours).Ticks,
            },
        ];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting shortcut analysis task with a batch size of {BatchSize}")]
    private static partial void LogScheduledTaskStarting(ILogger logger, int batchSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping shortcut analysis task because another library pass is already queued")]
    private static partial void LogScheduledTaskAlreadyQueued(ILogger logger);
}
