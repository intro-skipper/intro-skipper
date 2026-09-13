// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using System.Runtime.InteropServices;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.ScheduledTasks;
using IntroSkipper.Services;
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
    private readonly AnalysisScheduler _queue;

    /// <summary>
    /// Initializes a new instance of the <see cref="TroubleshootingController"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ffmpegService">FFmpeg service.</param>
    /// <param name="taskManager">Scheduled task manager, used to report the detection task's last run and progress.</param>
    /// <param name="queue">Analysis queue, used to report what is running and pending.</param>
    public TroubleshootingController(
        IApplicationHost applicationHost,
        ILogger<TroubleshootingController> logger,
        IFFmpegService ffmpegService,
        ITaskManager taskManager,
        AnalysisScheduler queue)
    {
        _applicationHost = applicationHost;
        _logger = logger;
        _ffmpegService = ffmpegService;
        _taskManager = taskManager;
        _queue = queue;
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
        var plugin = Plugin.Instance!;
        var ffmpeg = _ffmpegService.GetCheckResult();
        var settings = ConfigurationReport.Enumerate(plugin.Configuration);
        var detectTask = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is DetectSegmentsTask);
        var queue = _queue.Status;

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
                    new("Scan running", DescribeScanState(queue, detectTask)),
                    new("Queue", DescribeQueue(queue)),
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

    // The queue owns the running definition (shared with the dashboard's ScanStatus
    // endpoint); the scheduled task worker only contributes its progress while a library
    // pass runs.
    private static string DescribeScanState(AnalysisSchedulerStatus queue, IScheduledTaskWorker? task)
    {
        if (!queue.PassRunning)
        {
            return queue.IsRunning ? "pending" : "no";
        }

        return task is { State: TaskState.Running, CurrentProgress: { } progress }
            ? FormattableString.Invariant($"yes ({progress:0}%)")
            : "yes";
    }

    // "2 changed items, 1 manual scan, library pass pending", or "empty".
    private static string DescribeQueue(AnalysisSchedulerStatus queue)
    {
        List<string> parts = [];
        if (queue.ChangedItems > 0)
        {
            parts.Add(FormattableString.Invariant($"{queue.ChangedItems} changed item{(queue.ChangedItems == 1 ? string.Empty : "s")}"));
        }

        if (queue.ManualScans > 0)
        {
            parts.Add(FormattableString.Invariant($"{queue.ManualScans} manual scan{(queue.ManualScans == 1 ? string.Empty : "s")}"));
        }

        if (queue.LibraryPending)
        {
            parts.Add("library pass pending");
        }

        return parts.Count == 0 ? "empty" : string.Join(", ", parts);
    }

    private static string FormatDuration(TimeSpan duration) => duration switch
    {
        { TotalHours: >= 1 } => FormattableString.Invariant($"{(int)duration.TotalHours} h {duration.Minutes} min"),
        { TotalMinutes: >= 1 } => FormattableString.Invariant($"{(int)duration.TotalMinutes} min"),
        _ => FormattableString.Invariant($"{(int)duration.TotalSeconds} s"),
    };

    // CI rewrites Commit.CommitHash to the full 40-character hash; a local build leaves it empty.
    private static string GetPluginVersion()
    {
        var version = Plugin.Instance!.Version.ToString(4);
        var commit = Commit.CommitHash;
        return commit.Length >= 12 ? string.Concat(version, "+", commit.AsSpan(0, 12)) : version;
    }
}
