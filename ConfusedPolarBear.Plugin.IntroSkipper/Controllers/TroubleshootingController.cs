using System;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Text;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
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
    /// <param name="libraryManager">k host.</param>
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

        var libraries = _libraryManager.GetVirtualFolders();
        foreach (var library in libraries)
        {
            try
            {
                DriveInfo driveInfo = new DriveInfo(library.Locations[0]);
                // Get available free space in bytes
                long availableFreeSpace = driveInfo.AvailableFreeSpace;

                // Get total size of the drive in bytes
                long totalSize = driveInfo.TotalSize;

                // Get total used space in Percentage
                double usedSpacePercentage = totalSize > 0 ? (totalSize - availableFreeSpace) / (double)totalSize * 100 : 0;

                bundle.Append(CultureInfo.CurrentCulture, $"Library: {library.Name}\n");
                bundle.Append(CultureInfo.CurrentCulture, $"Drive: {driveInfo.Name}\n");
                bundle.Append(CultureInfo.CurrentCulture, $"Total Size: {GetHumanReadableSize(totalSize)}\n");
                bundle.Append(CultureInfo.CurrentCulture, $"Available Free Space: {GetHumanReadableSize(availableFreeSpace)}\n");
                bundle.Append(CultureInfo.CurrentCulture, $"Total used in Percentage: {Math.Round(usedSpacePercentage, 2)}%\n\n");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Unable to get DriveInfo: {Exception}", ex);
            }
        }

        return bundle.ToString().TrimEnd('\n');
    }

    private string GetHumanReadableSize(long bytes)
    {
        string[] sizes = ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
