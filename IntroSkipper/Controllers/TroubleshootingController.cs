// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using System.Runtime.InteropServices;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Troubleshooting controller.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("IntroSkipper")]
public partial class TroubleshootingController : ControllerBase
{
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<TroubleshootingController> _logger;
    private readonly IFFmpegService _ffmpegService;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TroubleshootingController"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ffmpegService">FFmpeg service.</param>
    /// <param name="taskManager">Scheduled task manager, used to report the detection task's last run.</param>
    public TroubleshootingController(
        IApplicationHost applicationHost,
        ILogger<TroubleshootingController> logger,
        IFFmpegService ffmpegService,
        ITaskManager taskManager)
    {
        _applicationHost = applicationHost;
        _logger = logger;
        _ffmpegService = ffmpegService;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Plugin meta endpoint.
    /// </summary>
    /// <returns>The version info.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public JsonResult GetPluginMetadata()
    {
        var json = new
        {
            version = Plugin.Instance!.Version.ToString(3),
        };

        return new JsonResult(json);
    }

    /// <summary>
    /// Gets the support bundle as Markdown, ready to paste into a bug report.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("SupportBundle")]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetSupportBundle() => BuildSupportBundle().Markdown;

    /// <summary>
    /// Gets the support bundle as sections for the dashboard, together with its Markdown rendering.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("SupportBundle/Json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SupportBundle> GetSupportBundleJson() => BuildSupportBundle();

    private SupportBundle BuildSupportBundle()
    {
        ArgumentNullException.ThrowIfNull(Plugin.Instance);

        var plugin = Plugin.Instance;
        var ffmpeg = _ffmpegService.GetCheckResult();
        var settings = ConfigurationReport.Enumerate(plugin.Configuration);
        var detectTask = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is DetectSegmentsTask);

        List<SupportBundleSection> sections =
        [
            new("Overview")
            {
                Entries =
                [
                    new("Jellyfin version", _applicationHost.ApplicationVersionString),
                    new("Plugin version", GetPluginVersion()),
                    new("Runs on", Helper.OperatingSystem.DetermineOperatingSystem()),
                    new("Runtime", FormattableString.Invariant($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}, {Environment.ProcessorCount} CPUs")),
                    new("FFmpeg", ffmpeg.Status),
                    new("FFmpeg path", string.IsNullOrEmpty(plugin.FFmpegPath) ? "unknown" : plugin.FFmpegPath),
                    new("Debug logging", _logger.IsEnabled(LogLevel.Debug) ? "on" : "off"),
                    new("Last scan", detectTask is null ? "unknown" : DescribeLastRun(detectTask)),
                    new("Scan running", DescribeScanState(detectTask)),
                    new("Queue contents", FormattableString.Invariant($"{plugin.TotalQueued} episodes, {plugin.TotalSeasons} seasons")),
                    new("Warnings", WarningManager.GetWarnings()),
                    new(
                        "File Transformation plugin",
                        (plugin.Configuration.FileTransformationPluginEnabled ? "installed" : "not installed")
                        + (plugin.Configuration.UseFileTransformationPlugin ? ", enabled in settings" : ", disabled in settings")),
                ],
            },
            new("Changed settings")
            {
                Entries = [.. settings.Where(s => !s.IsDefault).Select(s => new SupportBundleEntry(s.Name, $"{s.Value} (default {s.Default})"))],
            },
            new("All settings", Collapsed: true)
            {
                Text = string.Join('\n', settings.Select(s => $"{s.Name}: {s.Value}")),
            },
            .. ffmpeg.Outputs.Select(o => new SupportBundleSection($"FFmpeg {o.Name}", Collapsed: true) { Text = o.Output }),
        ];

        return new SupportBundle(sections);
    }

    // "2026-08-22 03:00 UTC, Completed in 14 min", with the error message appended for failed runs.
    private static string DescribeLastRun(IScheduledTaskWorker task)
    {
        if (task.LastExecutionResult is not { } result)
        {
            return "never";
        }

        var summary = FormattableString.Invariant($"{result.StartTimeUtc:yyyy-MM-dd HH:mm} UTC, {result.Status}");
        if (result.EndTimeUtc >= result.StartTimeUtc)
        {
            summary += " in " + FormatDuration(result.EndTimeUtc - result.StartTimeUtc);
        }

        // Entries are single-line; exception messages occasionally span several.
        return string.IsNullOrWhiteSpace(result.ErrorMessage) ? summary : summary + ": " + result.ErrorMessage.ReplaceLineEndings(" ").Trim();
    }

    // Manual season scans hold ScheduledTaskSemaphore without going through the task worker, so the
    // semaphore decides whether a scan is running; the worker only contributes its progress.
    private static string DescribeScanState(IScheduledTaskWorker? task)
    {
        if (task?.State == TaskState.Cancelling)
        {
            return "cancelling";
        }

        if (!ScheduledTaskSemaphore.IsBusy && task?.State != TaskState.Running)
        {
            return "no";
        }

        return task is { State: TaskState.Running, CurrentProgress: { } progress }
            ? FormattableString.Invariant($"yes ({progress:0}%)")
            : "yes";
    }

    private static string FormatDuration(TimeSpan duration) => duration switch
    {
        { TotalHours: >= 1 } => FormattableString.Invariant($"{(int)duration.TotalHours} h {duration.Minutes} min"),
        { TotalMinutes: >= 1 } => FormattableString.Invariant($"{(int)duration.TotalMinutes} min"),
        _ => FormattableString.Invariant($"{(int)duration.TotalSeconds} s"),
    };

    private string GetPluginVersion()
    {
        var version = Plugin.Instance!.Version.ToString(4);

        try
        {
            var commit = Commit.CommitHash;
            if (!string.IsNullOrWhiteSpace(commit))
            {
                version += string.Concat("+", commit.AsSpan(0, 12));
            }
        }
        catch (Exception ex)
        {
            LogUnableToAppendCommit(_logger, ex);
        }

        return version;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to append commit to version: {Exception}")]
    private static partial void LogUnableToAppendCommit(ILogger logger, object exception);
}
