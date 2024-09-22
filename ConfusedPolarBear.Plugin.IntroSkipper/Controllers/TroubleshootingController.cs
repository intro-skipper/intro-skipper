using System;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Text;
using ConfusedPolarBear.Plugin.IntroSkipper.Helper;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Controllers;

/// <summary>
/// Troubleshooting controller.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("IntroSkipper")]
public class TroubleshootingController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<TroubleshootingController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TroubleshootingController"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="libraryManager">Library Manager.</param>
    /// <param name="logger">Logger.</param>
    public TroubleshootingController(
        IApplicationHost applicationHost,
        ILibraryManager libraryManager,
        ILogger<TroubleshootingController> logger)
    {
        _libraryManager = libraryManager;
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
        ArgumentNullException.ThrowIfNull(Plugin.Instance);

        var bundle = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"* Jellyfin version: {_applicationHost.ApplicationVersionString}")
            .AppendLine(CultureInfo.InvariantCulture, $"* Plugin version: {GetPluginVersion()}")
            .AppendLine(CultureInfo.InvariantCulture, $"* Queue contents: {Plugin.Instance.TotalQueued} episodes, {Plugin.Instance.TotalSeasons} seasons")
            .Append(FFmpegWrapper.GetChromaprintLogs());

        return bundle.ToString();
    }

    private string GetPluginVersion()
    {
        var version = Plugin.Instance?.Version?.ToString(3) ?? "Unknown";
        try
        {
            var commit = Commit.CommitHash;
            if (!string.IsNullOrWhiteSpace(commit))
            {
                version += $"+{commit[..12]}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Unable to append commit to version: {Exception}", ex);
        }

        return version;
    }

    /// <summary>
    /// Gets a Markdown formatted support bundle.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("Storage")]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetFreeSpace()
    {
        ArgumentNullException.ThrowIfNull(Plugin.Instance);
        var bundle = new StringBuilder();

        foreach (var library in _libraryManager.GetVirtualFolders())
        {
            try
            {
                var driveInfo = new DriveInfo(library.Locations[0]);
                var usedSpacePercentage = driveInfo.TotalSize > 0
                    ? (driveInfo.TotalSize - driveInfo.AvailableFreeSpace) / (double)driveInfo.TotalSize * 100
                    : 0;

                bundle.AppendFormat(
                    CultureInfo.CurrentCulture,
                    "Library: {0}\nDrive: {1}\nTotal Size: {2}\nAvailable Free Space: {3}\nTotal used in Percentage: {4:F2}%\n\n",
                    library.Name,
                    driveInfo.Name,
                    GetHumanReadableSize(driveInfo.TotalSize),
                    GetHumanReadableSize(driveInfo.AvailableFreeSpace),
                    usedSpacePercentage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Unable to get DriveInfo: {Exception}", ex);
            }
        }

        return bundle.ToString().TrimEnd('\n');
    }

    private static string GetHumanReadableSize(long bytes)
    {
        string[] sizes = ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
        int order = 0;
        double len = bytes;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
