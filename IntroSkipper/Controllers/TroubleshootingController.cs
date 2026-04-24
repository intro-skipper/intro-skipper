// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Net.Mime;
using System.Text;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="TroubleshootingController"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="logger">Logger.</param>
    public TroubleshootingController(
        IApplicationHost applicationHost,
        ILogger<TroubleshootingController> logger)
    {
        _applicationHost = applicationHost;
        _logger = logger;
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
    /// Gets a Markdown formatted support bundle.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("SupportBundle")]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetSupportBundle()
    {
        ArgumentNullException.ThrowIfNull(Plugin.Instance);

        var bundle = new StringBuilder();

        bundle.Append("* Jellyfin version: ");
        bundle.Append(_applicationHost.ApplicationVersionString);
        bundle.Append('\n');

        var version = Plugin.Instance.Version.ToString(4);

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

        bundle.Append("* Plugin version: ");
        bundle.Append(version);
        bundle.Append('\n');

        bundle.Append("* Runs on: ");
        bundle.Append(Helper.OperatingSystem.DetermineOperatingSystem());
        bundle.Append('\n');

        bundle.Append("* Queue contents: ");
        bundle.Append(Plugin.Instance.TotalQueued);
        bundle.Append(" episodes, ");
        bundle.Append(Plugin.Instance.TotalSeasons);
        bundle.Append(" seasons\n");

        bundle.Append("* Warnings: `");
        bundle.Append(WarningManager.GetWarnings());
        bundle.Append("`\n");

        bundle.Append(FFmpegWrapper.GetChromaprintLogs());

        return bundle.ToString();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unable to append commit to version: {Exception}")]
    private static partial void LogUnableToAppendCommit(ILogger logger, object exception);
}
