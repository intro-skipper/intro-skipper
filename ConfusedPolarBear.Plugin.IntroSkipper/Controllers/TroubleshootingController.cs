using System;
using System.Net.Mime;
using System.Text;
using MediaBrowser.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Controllers;

/// <summary>
/// Troubleshooting controller.
/// </summary>
[Authorize(Policy = "RequiresElevation")]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("IntroSkipper")]
public class TroubleshootingController : ControllerBase
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
    /// Gets a Markdown formatted support bundle.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("SupportBundle")]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetSupportBundle()
    {
        var config = Plugin.Instance!.Configuration;
        var bundle = new StringBuilder();

        bundle.Append("* Jellyfin version: ");
        bundle.Append(_applicationHost.ApplicationVersionString);
        bundle.Append('\n');

        var version = Plugin.Instance.Version.ToString(3);

        try
        {
            var commit = Plugin.Instance.GetCommit();
            if (!string.IsNullOrWhiteSpace(commit))
            {
                version += string.Concat("+", commit.AsSpan(0, 12));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Unable to append commit to version: {Exception}", ex);
        }

        bundle.Append("* Plugin version: ");
        bundle.Append(version);
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
}
